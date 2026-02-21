using TheTechLoop.HybridCache.Abstractions;

namespace TheTechLoop.HybridCache.Services;

/// <summary>
/// No-op cache service used when caching is disabled.
/// All read operations return default; all write operations are no-ops.
/// </summary>
public class NoOpCacheService : ICacheService
{
    /// <summary>
    /// Gets or creates a cache entry.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="factory"></param>
    /// <param name="expiration"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiration, CancellationToken cancellationToken = default)
        => await factory();

    /// <summary>
    /// Gets a cache entry.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        => Task.FromResult<T?>(default);

    /// <summary>
    /// Sets a cache entry.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <param name="expiration"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Sets a cache entry.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <param name="options"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public Task SetAsync<T>(string key, T value, CacheEntryOptions options, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Removes a cache entry.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Removes cache entries by prefix.
    /// </summary>
    /// <param name="prefix"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Refreshes a cache entry.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task RefreshAsync(string key, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Gets multiple cache entries.
    /// </summary>
    /// <param name="keys"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public Task<Dictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys, CancellationToken cancellationToken = default)
        => Task.FromResult(keys.ToDictionary(k => k, k => default(T)));

    /// <summary>
    /// Sets multiple cache entries.
    /// </summary>
    /// <param name="items"></param>
    /// <param name="expiration"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public Task SetManyAsync<T>(Dictionary<string, T> items, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
