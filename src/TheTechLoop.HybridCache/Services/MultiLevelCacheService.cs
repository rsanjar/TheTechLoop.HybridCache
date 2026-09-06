using System.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Configuration;
using TheTechLoop.HybridCache.Metrics;
using TheTechLoop.HybridCache.Serialization;
using TheTechLoop.HybridCache.Tagging;

namespace TheTechLoop.HybridCache.Services;

/// <summary>
/// Multi-level cache: L1 in-memory (fast, per-instance) + L2 Redis (shared, durable).
/// Optimal for CQRS read-heavy workloads where the same data is queried frequently
/// by the same instance. L1 dramatically reduces Redis round-trips.
/// </summary>
public class MultiLevelCacheService : ICacheServiceWithEntryOptions
{
    private readonly IMemoryCache _l1;
    private readonly IDistributedCache _l2;
    private readonly IDistributedLock _lock;
    private readonly ILogger<MultiLevelCacheService> _logger;
    private readonly CacheConfig _config;
    private readonly CacheMetrics _metrics;
    private readonly CircuitBreakerState _circuitBreaker;
    private readonly ICacheSizeEstimator _sizeEstimator;
    private readonly ICacheTagService? _tagService;
    private readonly RequestCoalescer _coalescer = new();
    private readonly ICacheSerializer _serializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiLevelCacheService"/> class.
    /// </summary>
    /// <param name="l1Cache"></param>
    /// <param name="l2Cache"></param>
    /// <param name="distributedLock"></param>
    /// <param name="logger"></param>
    /// <param name="config"></param>
    /// <param name="metrics"></param>
    /// <param name="sizeEstimator">Optional pluggable size estimator for L1 eviction</param>
    /// <param name="tagService">Optional tag service for group invalidation</param>
    public MultiLevelCacheService(
        IMemoryCache l1Cache,
        IDistributedCache l2Cache,
        IDistributedLock distributedLock,
        ILogger<MultiLevelCacheService> logger,
        IOptions<CacheConfig> config,
        CacheMetrics metrics,
        ICacheSizeEstimator? sizeEstimator = null,
        ICacheTagService? tagService = null)
        : this(l1Cache, l2Cache, distributedLock, logger, config, metrics, sizeEstimator, tagService,
            new SystemTextJsonCacheSerializer()) { }

    /// <summary>Creates a multi-level cache with an explicitly supplied, thread-safe serializer.</summary>
    public MultiLevelCacheService(IMemoryCache l1Cache, IDistributedCache l2Cache, IDistributedLock distributedLock,
        ILogger<MultiLevelCacheService> logger, IOptions<CacheConfig> config, CacheMetrics metrics,
        ICacheSizeEstimator? sizeEstimator, ICacheTagService? tagService, ICacheSerializer serializer)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _l1 = l1Cache;
        _l2 = l2Cache;
        _lock = distributedLock;
        _logger = logger;
        _config = config.Value;
        _metrics = metrics;
        _sizeEstimator = sizeEstimator ?? new DefaultCacheSizeEstimator();
        _tagService = tagService;
        _circuitBreaker = new CircuitBreakerState(
            _config.CircuitBreaker.BreakDurationSeconds,
            _config.CircuitBreaker.FailureThreshold,
            _config.CircuitBreaker.HalfOpenSuccessThreshold,
            state =>
            {
                _metrics.RecordCircuitBreakerTransition(state);
                _logger.LogWarning("Circuit breaker transitioned to {State}", state);
            });
    }

    /// <inheritdoc />
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
        => await GetOrCreateAsync(
            key,
            factory,
            CacheEntryOptions.Absolute(expiration),
            cancellationToken);

    /// <inheritdoc />
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<Task<T>> factory,
        CacheEntryOptions options,
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
                var l2Bytes = await _l2.GetAsync(key, cancellationToken);

                if (l2Bytes is { Length: > 0 })
                {
                    sw.Stop();
                    _metrics.RecordHit(key, sw.Elapsed.TotalMilliseconds, "L2");
                    LogDebug("L2 cache hit for key: {Key}", key);

                    var l2Value = _serializer.Deserialize<T>(l2Bytes)!;

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

        // Coalesce in-process callers: only one thread per key enters
        // the lock + factory path; the rest await the same Task.
        return await _coalescer.CoalesceAsync(key, () =>
            PopulateMultiLevelAsync(key, factory, options, cancellationToken));
    }

    /// <summary>
    /// Acquires the distributed lock, retries on failure, and populates
    /// both cache levels.  Called by the coalescer so that at most one
    /// in-process caller executes this for a given key at a time.
    /// </summary>
    private async Task<T> PopulateMultiLevelAsync<T>(
        string key,
        Func<Task<T>> factory,
        CacheEntryOptions options,
        CancellationToken cancellationToken)
    {
        // Re-check L1 (may have been populated by another coalesced caller)
        if (_l1.TryGetValue(key, out T? l1Value) && l1Value is not null)
            return l1Value;

        // Re-check L2 (another process may have populated while we waited)
        if (!IsCircuitOpen())
        {
            try
            {
                var l2Bytes = await _l2.GetAsync(key, cancellationToken);
                if (l2Bytes is { Length: > 0 })
                {
                    var l2Value = _serializer.Deserialize<T>(l2Bytes)!;
                    SetL1(key, l2Value);
                    _circuitBreaker.RecordSuccess();
                    return l2Value;
                }
            }
            catch (Exception ex)
            {
                _circuitBreaker.RecordFailure();
                _logger.LogWarning(ex, "L2 re-check failed for key: {Key}", key);
            }
        }

        // Stampede protection: acquire lock or poll until populated
        var (lockHandle, found, polledValue) = await StampedeProtection.AcquireOrPollAsync<T>(
            key, _lock, _config, _metrics,
            isCircuitOpen: () => IsCircuitOpen(),
            pollCacheAsync: async () =>
            {
                // Retry L1 then L2
                if (_l1.TryGetValue(key, out T? retryValue) && retryValue is not null)
                    return (true, retryValue);

                try
                {
                    var retryBytes = await _l2.GetAsync(key, cancellationToken);
                    if (retryBytes is { Length: > 0 })
                    {
                        var value = _serializer.Deserialize<T>(retryBytes)!;
                        SetL1(key, value);
                        return (true, value);
                    }
                }
                catch
                {
                    // Fall through to next attempt or factory
                }

                return (false, default);
            },
            cancellationToken);

        if (lockHandle is not null)
            await lockHandle.DisposeAsync();

        if (found)
            return polledValue!;

        // Populate from source
        var result = await factory();

        // Write to both levels
        SetL1(key, result, options);
        if (await SetL2SafeAsync(key, result, options.Expiration, cancellationToken))
            await AddTagsAsync(key, options.Tags, cancellationToken);

        return result;
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
            var l2Bytes = await _l2.GetAsync(key, cancellationToken);

            if (l2Bytes is not { Length: > 0 })
                return default;

            var value = _serializer.Deserialize<T>(l2Bytes);

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

        SetL1(key, value, options);
        if (await SetL2SafeAsync(key, value, options.Expiration, cancellationToken))
            await AddTagsAsync(key, options.Tags, cancellationToken);
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
            if (_tagService is not null)
                await _tagService.RemoveAsync(key, cancellationToken);

            _metrics.RecordEviction(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing from L2 cache for key: {Key}", key);
        }
    }

    /// <summary>
    /// Prefix removal is handled by invalidation subscribers using Redis SCAN.
    /// Direct calls do not enumerate local L1 entries or distributed L2 keys.
    /// </summary>
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

        _metrics.RecordBatchSize(keyList.Count, "get");

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
            }
        }

        // Check L2 for missing keys
        if (missingKeys.Count > 0 && !IsCircuitOpen())
        {
            try
            {
                foreach (var chunk in missingKeys.Chunk(_config.MaxBatchConcurrency))
                {
                    var tasks = chunk.Select(k => _l2.GetAsync(k, cancellationToken)).ToArray();
                    var values = await Task.WhenAll(tasks);

                    for (int i = 0; i < chunk.Length; i++)
                    {
                        var key = chunk[i];
                        var data = values[i];

                        if (data is { Length: > 0 })
                        {
                            var value = _serializer.Deserialize<T>(data);
                            result[key] = value;
                            // Promote to L1
                            if (value is not null)
                                SetL1(key, value);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving multiple keys from L2 cache");
            }
        }

        // Populate defaults for keys not found in either level
        foreach (var key in missingKeys)
        {
            result.TryAdd(key, default);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task SetManyAsync<T>(Dictionary<string, T> items, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || !items.Any())
            return;

        _metrics.RecordBatchSize(items.Count, "set");

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

                var validItems = items
                    .Where(kvp => kvp.Value is not null && !EqualityComparer<T>.Default.Equals(kvp.Value, default))
                    .ToArray();

                foreach (var chunk in validItems.Chunk(_config.MaxBatchConcurrency))
                {
                    var tasks = chunk.Select(kvp =>
                    {
                        var bytes = _serializer.Serialize(kvp.Value);
                        return _l2.SetAsync(kvp.Key, bytes, options, cancellationToken);
                    }).ToArray();

                    await Task.WhenAll(tasks);
                }

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
            Size = _sizeEstimator.EstimateSize(value),
            Priority = CacheItemPriority.Normal
        };

        _l1.Set(key, value, l1Options);
    }

    private void SetL1<T>(string key, T value, CacheEntryOptions options)
    {
        if (!_config.MemoryCache.Enabled || value is null)
            return;

        if (options.ExpirationType == CacheExpirationType.Sliding)
        {
            var l1Options = new MemoryCacheEntryOptions
            {
                SlidingExpiration = options.Expiration,
                Size = _sizeEstimator.EstimateSize(value),
                Priority = CacheItemPriority.Normal
            };
            _l1.Set(key, value, l1Options);
            return;
        }

        var absoluteOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = options.Expiration,
            Size = _sizeEstimator.EstimateSize(value),
            Priority = CacheItemPriority.Normal
        };

        _l1.Set(key, value, absoluteOptions);
    }

    private async Task<bool> SetL2SafeAsync<T>(
        string key,
        T value,
        TimeSpan? expiration,
        CancellationToken cancellationToken)
    {
        if (IsCircuitOpen() || value is null || EqualityComparer<T>.Default.Equals(value, default))
            return false;

        try
        {
            var bytes = _serializer.Serialize(value);
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration ?? TimeSpan.FromMinutes(_config.DefaultExpirationMinutes)
            };

            await _l2.SetAsync(key, bytes, options, cancellationToken);
            _circuitBreaker.RecordSuccess();
            return true;
        }
        catch (Exception ex)
        {
            _circuitBreaker.RecordFailure();
            _logger.LogWarning(ex, "Failed to write to L2 cache for key: {Key}", key);
            return false;
        }
    }

    private async Task AddTagsAsync(string key, IReadOnlyList<string> tags, CancellationToken cancellationToken)
    {
        if (_tagService is null || !tags.Any())
            return;

        await _tagService.AddTagsAsync(key, tags, cancellationToken);
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
