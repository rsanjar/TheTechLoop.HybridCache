using System.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Configuration;
using TheTechLoop.HybridCache.Metrics;
using TheTechLoop.HybridCache.Tagging;

namespace TheTechLoop.HybridCache.Services;

/// <summary>
/// Redis-based distributed cache with stampede protection, circuit breaker,
/// and OpenTelemetry metrics. Designed for CQRS read-path optimization.
/// </summary>
public class RedisCacheService : ICacheService
{
    private readonly IDistributedCache _cache;
    private readonly IDistributedLock _lock;
    private readonly ILogger<RedisCacheService> _logger;
    private readonly CacheConfig _config;
    private readonly CacheMetrics _metrics;
    private readonly CircuitBreakerState _circuitBreaker;
    private readonly ICacheTagService? _tagService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisCacheService"/> class.
    /// </summary>
    /// <param name="cache"></param>
    /// <param name="distributedLock"></param>
    /// <param name="logger"></param>
    /// <param name="config"></param>
    /// <param name="metrics"></param>
    /// <param name="tagService">Optional tag service for group invalidation</param>
    public RedisCacheService(
        IDistributedCache cache,
        IDistributedLock distributedLock,
        ILogger<RedisCacheService> logger,
        IOptions<CacheConfig> config,
        CacheMetrics metrics,
        ICacheTagService? tagService = null)
    {
        _cache = cache;
        _lock = distributedLock;
        _logger = logger;
        _config = config.Value;
        _metrics = metrics;
        _tagService = tagService;
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

        if (_config.CircuitBreaker.Enabled && _circuitBreaker.IsOpen)
        {
            _metrics.RecordCircuitBreakerBypass();
            LogDebug("Circuit breaker open, bypassing cache for key: {Key}", key);
            return await factory();
        }

        var sw = Stopwatch.StartNew();

        try
        {
            // Try reading from cache
            var cachedData = await _cache.GetStringAsync(key, cancellationToken);

            if (!string.IsNullOrEmpty(cachedData))
            {
                sw.Stop();
                _metrics.RecordHit(key, sw.Elapsed.TotalMilliseconds);
                LogDebug("Cache hit for key: {Key}", key);

                _circuitBreaker.RecordSuccess();
                return JsonSerializer.Deserialize<T>(cachedData, JsonOptions)!;
            }

            sw.Stop();
            _metrics.RecordMiss(key, sw.Elapsed.TotalMilliseconds);
            LogDebug("Cache miss for key: {Key}", key);

            // Stampede protection: acquire lock before populating
            await using var lockHandle = await _lock.TryAcquireAsync(
                $"lock:{key}", TimeSpan.FromSeconds(10), cancellationToken);

            if (lockHandle is null)
            {
                // Another instance is populating; wait briefly and retry from cache
                try
                {
                    await Task.Delay(150, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // If cancelled during wait, fall back to factory immediately
                    return await factory();
                }

                cachedData = await _cache.GetStringAsync(key, cancellationToken);

                if (!string.IsNullOrEmpty(cachedData))
                    return JsonSerializer.Deserialize<T>(cachedData, JsonOptions)!;
            }

            // Populate from source
            var value = await factory();
            await SetCacheSafeAsync(key, value, expiration, cancellationToken);

            _circuitBreaker.RecordSuccess();
            return value;
        }
        catch (Exception ex)
        {
            _metrics.RecordError(key);
            _circuitBreaker.RecordFailure();
            _logger.LogError(ex, "Cache error for key: {Key}, falling back to source", key);
            return await factory();
        }
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || IsCircuitOpen())
            return default;

        try
        {
            var cachedData = await _cache.GetStringAsync(key, cancellationToken);

            if (string.IsNullOrEmpty(cachedData))
            {
                _metrics.RecordMiss(key, 0);
                LogDebug("Cache miss for key: {Key}", key);
                return default;
            }

            _metrics.RecordHit(key, 0);
            LogDebug("Cache hit for key: {Key}", key);
            _circuitBreaker.RecordSuccess();

            return JsonSerializer.Deserialize<T>(cachedData, JsonOptions);
        }
        catch (Exception ex)
        {
            _metrics.RecordError(key);
            _circuitBreaker.RecordFailure();
            _logger.LogError(ex, "Error retrieving from cache for key: {Key}", key);
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
        if (!_config.Enabled || IsCircuitOpen() || value is null)
            return;

        await SetCacheSafeAsync(key, value, expiration, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(
        string key,
        T value,
        CacheEntryOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || IsCircuitOpen() || value is null)
            return;

        await SetCacheSafeAsync(key, value, options.Expiration, cancellationToken);

        // Handle tags for group invalidation
        if (options.Tags.Any())
        {
            await AddTagsAsync(key, options.Tags, cancellationToken);
        }

        // Note: IDistributedCache doesn't support sliding expiration natively.
        // Sliding expiration requires calling RefreshAsync on each access.
        // For true sliding expiration, use MultiLevelCacheService with L1 cache.
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || IsCircuitOpen())
            return;

        try
        {
            await _cache.RemoveAsync(key, cancellationToken);
            _metrics.RecordEviction(key);
            LogDebug("Cache removed for key: {Key}", key);
        }
        catch (Exception ex)
        {
            _metrics.RecordError(key);
            _circuitBreaker.RecordFailure();
            _logger.LogError(ex, "Error removing cache for key: {Key}", key);
        }
    }

    /// <inheritdoc />
    public async Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled)
            return;

        _logger.LogWarning(
            "RemoveByPrefixAsync with prefix: {Prefix}. " +
            "Pattern-based deletion requires IConnectionMultiplexer. " +
            "Use CacheInvalidationPublisher.PublishPrefixAsync for cross-service invalidation.",
            prefix);

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RefreshAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || IsCircuitOpen())
            return;

        try
        {
            await _cache.RefreshAsync(key, cancellationToken);
            LogDebug("Cache refreshed for key: {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing cache for key: {Key}", key);
        }
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, T?>();

        if (!_config.Enabled || IsCircuitOpen())
            return result;

        var keyList = keys.ToList();
        if (!keyList.Any())
            return result;

        try
        {
            // Pipeline multiple GET operations
            var tasks = keyList.Select(key => _cache.GetStringAsync(key, cancellationToken)).ToArray();
            var values = await Task.WhenAll(tasks);

            for (int i = 0; i < keyList.Count; i++)
            {
                var key = keyList[i];
                var data = values[i];

                if (!string.IsNullOrEmpty(data))
                {
                    _metrics.RecordHit(key, 0);
                    result[key] = JsonSerializer.Deserialize<T>(data, JsonOptions);
                }
                else
                {
                    _metrics.RecordMiss(key, 0);
                    result[key] = default;
                }
            }

            _circuitBreaker.RecordSuccess();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving multiple keys from cache");
            _circuitBreaker.RecordFailure();
        }

        return result;
    }

    /// <inheritdoc />
    public async Task SetManyAsync<T>(Dictionary<string, T> items, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || IsCircuitOpen() || !items.Any())
            return;

        try
        {
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration ?? TimeSpan.FromMinutes(_config.DefaultExpirationMinutes)
            };

            // Pipeline multiple SET operations
            var tasks = items
                .Where(kvp => kvp.Value is not null && !EqualityComparer<T>.Default.Equals(kvp.Value, default))
                .Select(kvp =>
                {
                    var serialized = JsonSerializer.Serialize(kvp.Value, JsonOptions);
                    return _cache.SetStringAsync(kvp.Key, serialized, options, cancellationToken);
                })
                .ToArray();

            await Task.WhenAll(tasks);
            _circuitBreaker.RecordSuccess();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting multiple keys in cache");
            _circuitBreaker.RecordFailure();
        }
    }

    private async Task SetCacheSafeAsync<T>(
        string key,
        T value,
        TimeSpan? expiration,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (value is null || EqualityComparer<T>.Default.Equals(value, default))
                return;

            var serializedData = JsonSerializer.Serialize(value, JsonOptions);
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration ?? TimeSpan.FromMinutes(_config.DefaultExpirationMinutes)
            };

            await _cache.SetStringAsync(key, serializedData, options, cancellationToken);
            LogDebug("Cache set for key: {Key}, Expiration: {Expiration}", key, options.AbsoluteExpirationRelativeToNow);
        }
        catch (Exception ex)
        {
            _metrics.RecordError(key);
            _circuitBreaker.RecordFailure();
            _logger.LogWarning(ex, "Failed to write to cache for key: {Key}", key);
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
        if (!_config.CircuitBreaker.Enabled)
            return false;

        if (!_circuitBreaker.IsOpen)
            return false;

        _metrics.RecordCircuitBreakerBypass();
        LogDebug("Circuit breaker open, bypassing cache");
        return true;
    }

    private void LogDebug(string message, params object[] args)
    {
        if (_config.EnableLogging)
            _logger.LogDebug(message, args);
    }
}
