using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Configuration;
using TheTechLoop.HybridCache.Metrics;

namespace TheTechLoop.HybridCache.Services;

/// <summary>
/// In-process only cache service backed solely by <see cref="IMemoryCache"/>.
/// Activated when <see cref="CacheConfig.UseMemoryOnly"/> is <c>true</c>.
/// <para>
/// No Redis, no distributed locking, no pub/sub — purely local.
/// Suitable for single-instance deployments, local development, and tests.
/// </para>
/// </summary>
public sealed class MemoryOnlyCacheService : ICacheService
{
    private readonly IMemoryCache _cache;
    private readonly CacheConfig _config;
    private readonly CacheMetrics _metrics;
    private readonly ILogger<MemoryOnlyCacheService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryOnlyCacheService"/> class.
    /// </summary>
    public MemoryOnlyCacheService(
        IMemoryCache cache,
        IOptions<CacheConfig> config,
        CacheMetrics metrics,
        ILogger<MemoryOnlyCacheService> logger)
    {
        _cache = cache;
        _config = config.Value;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(key, out T? cached) && cached is not null)
        {
            _metrics.RecordHit(key, 0, "L1");
            return cached;
        }

        _metrics.RecordMiss(key, 0, "L1");

        var value = await factory();

        Set(key, value, expiration);
        return value;
    }

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(key, out T? value))
        {
            _metrics.RecordHit(key, 0, "L1");
            return Task.FromResult(value);
        }

        _metrics.RecordMiss(key, 0, "L1");
        return Task.FromResult<T?>(default);
    }

    /// <inheritdoc />
    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        if (value is not null)
            Set(key, value, expiration ?? TimeSpan.FromMinutes(_config.DefaultExpirationMinutes));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetAsync<T>(
        string key,
        T value,
        CacheEntryOptions options,
        CancellationToken cancellationToken = default)
    {
        if (value is null)
            return Task.CompletedTask;

        var entryOptions = options.ExpirationType == CacheExpirationType.Sliding
            ? new MemoryCacheEntryOptions { SlidingExpiration = options.Expiration, Size = 1 }
            : new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.Expiration, Size = 1 };

        _cache.Set(key, value, entryOptions);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _cache.Remove(key);
        _metrics.RecordEviction(key, "L1");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        // IMemoryCache does not support prefix-based removal.
        _logger.LogWarning(
            "RemoveByPrefixAsync is not supported in memory-only mode. Prefix: {Prefix}", prefix);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RefreshAsync(string key, CancellationToken cancellationToken = default)
        => Task.CompletedTask; // TTL reset is automatic for sliding-expiration entries on access

    /// <inheritdoc />
    public Task<Dictionary<string, T?>> GetManyAsync<T>(
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default)
    {
        var keyList = keys.ToList();
        _metrics.RecordBatchSize(keyList.Count, "get");

        var result = new Dictionary<string, T?>(keyList.Count);

        foreach (var key in keyList)
        {
            result[key] = _cache.TryGetValue(key, out T? value) ? value : default;
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task SetManyAsync<T>(
        Dictionary<string, T> items,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        if (!items.Any())
            return Task.CompletedTask;

        _metrics.RecordBatchSize(items.Count, "set");

        var ttl = expiration ?? TimeSpan.FromMinutes(_config.DefaultExpirationMinutes);

        foreach (var (key, value) in items)
        {
            if (value is not null)
                Set(key, value, ttl);
        }

        return Task.CompletedTask;
    }

    private void Set<T>(string key, T value, TimeSpan expiration)
    {
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration,
            Size = 1
        };

        _cache.Set(key, value, options);
    }
}
