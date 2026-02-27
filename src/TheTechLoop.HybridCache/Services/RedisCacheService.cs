using System.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Configuration;
using TheTechLoop.HybridCache.Metrics;
using TheTechLoop.HybridCache.Serialization;
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
    private readonly RequestCoalescer _coalescer = new();

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
            var cachedBytes = await _cache.GetAsync(key, cancellationToken);

            if (cachedBytes is { Length: > 0 })
            {
                sw.Stop();
                _metrics.RecordHit(key, sw.Elapsed.TotalMilliseconds);
                LogDebug("Cache hit for key: {Key}", key);

                _circuitBreaker.RecordSuccess();
                return CacheSerializer.Deserialize<T>(cachedBytes)!;
            }

            sw.Stop();
            _metrics.RecordMiss(key, sw.Elapsed.TotalMilliseconds);
            LogDebug("Cache miss for key: {Key}", key);

            // Coalesce in-process callers: only one thread per key enters
            // the lock + factory path; the rest await the same Task.
            return await _coalescer.CoalesceAsync(key, () =>
                PopulateAsync(key, factory, expiration, cancellationToken));
        }
        catch (Exception ex)
        {
            _metrics.RecordError(key);
            _circuitBreaker.RecordFailure();
            _logger.LogError(ex, "Cache error for key: {Key}, falling back to source", key);
            return await factory();
        }
    }

    /// <summary>
    /// Acquires the distributed lock, retries on failure, and populates the cache.
    /// Called by the coalescer so that at most one in-process caller executes this
    /// for a given key at a time.
    /// </summary>
    private async Task<T> PopulateAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan expiration,
        CancellationToken cancellationToken)
    {
        // Re-check cache (may have been populated by another process while
        // this caller was waiting for the coalescing slot)
        var cachedBytes = await _cache.GetAsync(key, cancellationToken);
        if (cachedBytes is { Length: > 0 })
        {
            _circuitBreaker.RecordSuccess();
            return CacheSerializer.Deserialize<T>(cachedBytes)!;
        }

        // Stampede protection: acquire lock or poll until populated
        var (lockHandle, found, polledValue) = await StampedeProtection.AcquireOrPollAsync<T>(
            key, _lock, _config, _metrics,
            isCircuitOpen: () => IsCircuitOpen(),
            pollCacheAsync: async () =>
            {
                var bytes = await _cache.GetAsync(key, cancellationToken);
                if (bytes is { Length: > 0 })
                    return (true, CacheSerializer.Deserialize<T>(bytes)!);
                return (false, default);
            },
            cancellationToken);

        if (lockHandle is not null)
            await lockHandle.DisposeAsync();

        if (found)
            return polledValue!;

        // Populate from source
        var value = await factory();
        await SetCacheSafeAsync(key, value, expiration, cancellationToken);

        _circuitBreaker.RecordSuccess();
        return value;
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || IsCircuitOpen())
            return default;

        try
        {
            var cachedBytes = await _cache.GetAsync(key, cancellationToken);

            if (cachedBytes is not { Length: > 0 })
            {
                _metrics.RecordMiss(key, 0);
                LogDebug("Cache miss for key: {Key}", key);
                return default;
            }

            _metrics.RecordHit(key, 0);
            LogDebug("Cache hit for key: {Key}", key);
            _circuitBreaker.RecordSuccess();

            return CacheSerializer.Deserialize<T>(cachedBytes);
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

        _metrics.RecordBatchSize(keyList.Count, "get");

        try
        {
            // Pipeline GET operations in bounded chunks to avoid Redis saturation
            foreach (var chunk in keyList.Chunk(_config.MaxBatchConcurrency))
            {
                var tasks = chunk.Select(key => _cache.GetAsync(key, cancellationToken)).ToArray();
                var values = await Task.WhenAll(tasks);

                for (int i = 0; i < chunk.Length; i++)
                {
                    var key = chunk[i];
                    var data = values[i];

                    if (data is { Length: > 0 })
                    {
                        _metrics.RecordHit(key, 0);
                        result[key] = CacheSerializer.Deserialize<T>(data);
                    }
                    else
                    {
                        _metrics.RecordMiss(key, 0);
                        result[key] = default;
                    }
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

        _metrics.RecordBatchSize(items.Count, "set");

        try
        {
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration ?? TimeSpan.FromMinutes(_config.DefaultExpirationMinutes)
            };

            // Pipeline SET operations in bounded chunks to avoid Redis saturation
            var validItems = items
                .Where(kvp => kvp.Value is not null && !EqualityComparer<T>.Default.Equals(kvp.Value, default))
                .ToArray();

            foreach (var chunk in validItems.Chunk(_config.MaxBatchConcurrency))
            {
                var tasks = chunk.Select(kvp =>
                {
                    var bytes = CacheSerializer.Serialize(kvp.Value);
                    return _cache.SetAsync(kvp.Key, bytes, options, cancellationToken);
                }).ToArray();

                await Task.WhenAll(tasks);
            }

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

            var bytes = CacheSerializer.Serialize(value);
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration ?? TimeSpan.FromMinutes(_config.DefaultExpirationMinutes)
            };

            await _cache.SetAsync(key, bytes, options, cancellationToken);
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
