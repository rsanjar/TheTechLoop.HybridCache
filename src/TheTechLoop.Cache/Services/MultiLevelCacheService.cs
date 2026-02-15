using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheTechLoop.Cache.Abstractions;
using TheTechLoop.Cache.Configuration;
using TheTechLoop.Cache.Metrics;

namespace TheTechLoop.Cache.Services;

/// <summary>
/// Multi-level cache: L1 in-memory (fast, per-instance) + L2 Redis (shared, durable).
/// Optimal for CQRS read-heavy workloads where the same data is queried frequently
/// by the same instance. L1 dramatically reduces Redis round-trips.
/// </summary>
public class MultiLevelCacheService : ICacheService
{
    private readonly IMemoryCache _l1;
    private readonly IDistributedCache _l2;
    private readonly IDistributedLock _lock;
    private readonly ILogger<MultiLevelCacheService> _logger;
    private readonly CacheConfig _config;
    private readonly CacheMetrics _metrics;
    private readonly CircuitBreakerState _circuitBreaker;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiLevelCacheService"/> class.
    /// </summary>
    /// <param name="l1Cache"></param>
    /// <param name="l2Cache"></param>
    /// <param name="distributedLock"></param>
    /// <param name="logger"></param>
    /// <param name="config"></param>
    /// <param name="metrics"></param>
    public MultiLevelCacheService(
        IMemoryCache l1Cache,
        IDistributedCache l2Cache,
        IDistributedLock distributedLock,
        ILogger<MultiLevelCacheService> logger,
        IOptions<CacheConfig> config,
        CacheMetrics metrics)
    {
        _l1 = l1Cache;
        _l2 = l2Cache;
        _lock = distributedLock;
        _logger = logger;
        _config = config.Value;
        _metrics = metrics;
        _circuitBreaker = new CircuitBreakerState(
            _config.CircuitBreaker.BreakDurationSeconds,
            _config.CircuitBreaker.FailureThreshold);
    }

    /// <inheritdoc />
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
            return await factory();

        var sw = Stopwatch.StartNew();

        // L1: Check in-memory cache first
        if (_l1.TryGetValue(key, out T? l1Value) && l1Value is not null)
        {
            sw.Stop();
            _metrics.RecordHit(key, sw.Elapsed.TotalMilliseconds, "L1");
            LogDebug("L1 cache hit for key: {Key}", key);
            return l1Value;
        }

        // L2: Check Redis (if circuit is closed)
        if (!IsCircuitOpen())
        {
            try
            {
                var l2Data = await _l2.GetStringAsync(key, cancellationToken);

                if (!string.IsNullOrEmpty(l2Data))
                {
                    sw.Stop();
                    _metrics.RecordHit(key, sw.Elapsed.TotalMilliseconds, "L2");
                    LogDebug("L2 cache hit for key: {Key}", key);

                    var l2Value = JsonSerializer.Deserialize<T>(l2Data, JsonOptions)!;

                    // Promote to L1
                    SetL1(key, l2Value);

                    _circuitBreaker.RecordSuccess();
                    return l2Value;
                }
            }
            catch (Exception ex)
            {
                _circuitBreaker.RecordFailure();
                _logger.LogWarning(ex, "L2 read failed for key: {Key}, proceeding to factory", key);
            }
        }

        sw.Stop();
        _metrics.RecordMiss(key, sw.Elapsed.TotalMilliseconds);
        LogDebug("Cache miss (L1+L2) for key: {Key}", key);

        // Stampede protection
        await using var lockHandle = await _lock.TryAcquireAsync(
            $"lock:{key}", TimeSpan.FromSeconds(10), cancellationToken);

        if (lockHandle is null)
        {
            await Task.Delay(150, cancellationToken);

            // Retry L1 then L2
            if (_l1.TryGetValue(key, out T? retryValue) && retryValue is not null)
                return retryValue;

            try
            {
                var retryData = await _l2.GetStringAsync(key, cancellationToken);
                if (!string.IsNullOrEmpty(retryData))
                    return JsonSerializer.Deserialize<T>(retryData, JsonOptions)!;
            }
            catch
            {
                // Fall through to factory
            }
        }

        // Populate from source
        var value = await factory();

        // Write to both levels
        SetL1(key, value);
        await SetL2SafeAsync(key, value, expiration, cancellationToken);

        return value;
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
            return default;

        // L1
        if (_l1.TryGetValue(key, out T? l1Value))
            return l1Value;

        // L2
        if (IsCircuitOpen())
            return default;

        try
        {
            var l2Data = await _l2.GetStringAsync(key, cancellationToken);

            if (string.IsNullOrEmpty(l2Data))
                return default;

            var value = JsonSerializer.Deserialize<T>(l2Data, JsonOptions);

            if (value is not null)
                SetL1(key, value);

            _circuitBreaker.RecordSuccess();
            return value;
        }
        catch (Exception ex)
        {
            _circuitBreaker.RecordFailure();
            _logger.LogError(ex, "Error retrieving from L2 cache for key: {Key}", key);
            return default;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || value is null)
            return;

        SetL1(key, value);
        await SetL2SafeAsync(key, value, expiration, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(
        string key,
        T value,
        CacheEntryOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || value is null)
            return;

        // L1 supports sliding expiration natively
        if (options.ExpirationType == CacheExpirationType.Sliding)
        {
            var l1Options = new MemoryCacheEntryOptions
            {
                SlidingExpiration = options.Expiration,
                Size = EstimateSize(value),
                Priority = CacheItemPriority.Normal
            };
            _l1.Set(key, value, l1Options);
        }
        else
        {
            SetL1(key, value);
        }

        await SetL2SafeAsync(key, value, options.Expiration, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
            return;

        // Remove from both levels
        _l1.Remove(key);

        try
        {
            await _l2.RemoveAsync(key, cancellationToken);
            _metrics.RecordEviction(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing from L2 cache for key: {Key}", key);
        }
    }

    /// <inheritdoc />
    public async Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        // L1: IMemoryCache doesn't support prefix-based removal natively.
        // L1 entries will naturally expire via their short TTL.
        // For immediate L1 invalidation, use CacheInvalidationSubscriber.

        _logger.LogWarning(
            "RemoveByPrefixAsync: L1 entries will expire naturally. " +
            "Use CacheInvalidationPublisher for cross-instance L1 invalidation. Prefix: {Prefix}", prefix);

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RefreshAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || IsCircuitOpen())
            return;

        try
        {
            await _l2.RefreshAsync(key, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing L2 cache for key: {Key}", key);
        }
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, T?>();

        if (!_config.Enabled)
            return result;

        var keyList = keys.ToList();
        var missingKeys = new List<string>();

        // Check L1 first
        foreach (var key in keyList)
        {
            if (_l1.TryGetValue(key, out T? value) && value is not null)
            {
                result[key] = value;
            }
            else
            {
                missingKeys.Add(key);
                result[key] = default;
            }
        }

        // Check L2 for missing keys
        if (missingKeys.Any() && !IsCircuitOpen())
        {
            try
            {
                var tasks = missingKeys.Select(k => _l2.GetStringAsync(k, cancellationToken)).ToArray();
                var values = await Task.WhenAll(tasks);

                for (int i = 0; i < missingKeys.Count; i++)
                {
                    var key = missingKeys[i];
                    var data = values[i];

                    if (!string.IsNullOrEmpty(data))
                    {
                        var value = JsonSerializer.Deserialize<T>(data, JsonOptions);
                        result[key] = value;
                        // Promote to L1
                        if (value is not null)
                            SetL1(key, value);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving multiple keys from L2 cache");
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task SetManyAsync<T>(Dictionary<string, T> items, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || !items.Any())
            return;

        // Set in L1
        foreach (var kvp in items)
        {
            if (kvp.Value is not null)
                SetL1(kvp.Key, kvp.Value);
        }

        // Set in L2
        if (!IsCircuitOpen())
        {
            try
            {
                var options = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = expiration ?? TimeSpan.FromMinutes(_config.DefaultExpirationMinutes)
                };

                var tasks = items
                    .Where(kvp => kvp.Value is not null && !EqualityComparer<T>.Default.Equals(kvp.Value, default))
                    .Select(kvp =>
                    {
                        var serialized = JsonSerializer.Serialize(kvp.Value, JsonOptions);
                        return _l2.SetStringAsync(kvp.Key, serialized, options, cancellationToken);
                    })
                    .ToArray();

                await Task.WhenAll(tasks);
                _circuitBreaker.RecordSuccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting multiple keys in L2 cache");
                _circuitBreaker.RecordFailure();
            }
        }
    }

    private void SetL1<T>(string key, T value)
    {
        if (!_config.MemoryCache.Enabled || value is null)
            return;

        var l1Options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_config.MemoryCache.DefaultExpirationSeconds),
            Size = EstimateSize(value),
            Priority = CacheItemPriority.Normal
        };

        _l1.Set(key, value, l1Options);
    }

    private static long EstimateSize<T>(T value)
    {
        return value switch
        {
            string s => Math.Max(1, s.Length / 1000),  // 1 unit per KB
            System.Collections.ICollection c => Math.Max(1, c.Count / 100),
            _ => 1
        };
    }

    private async Task SetL2SafeAsync<T>(
        string key,
        T value,
        TimeSpan? expiration,
        CancellationToken cancellationToken)
    {
        if (IsCircuitOpen() || value is null || EqualityComparer<T>.Default.Equals(value, default))
            return;

        try
        {
            var serialized = JsonSerializer.Serialize(value, JsonOptions);
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration ?? TimeSpan.FromMinutes(_config.DefaultExpirationMinutes)
            };

            await _l2.SetStringAsync(key, serialized, options, cancellationToken);
            _circuitBreaker.RecordSuccess();
        }
        catch (Exception ex)
        {
            _circuitBreaker.RecordFailure();
            _logger.LogWarning(ex, "Failed to write to L2 cache for key: {Key}", key);
        }
    }

    private bool IsCircuitOpen()
    {
        if (!_config.CircuitBreaker.Enabled || !_circuitBreaker.IsOpen)
            return false;

        _metrics.RecordCircuitBreakerBypass();
        return true;
    }

    private void LogDebug(string message, params object[] args)
    {
        if (_config.EnableLogging)
            _logger.LogDebug(message, args);
    }
}
