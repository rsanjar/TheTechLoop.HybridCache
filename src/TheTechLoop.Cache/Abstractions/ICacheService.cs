namespace TheTechLoop.Cache.Abstractions;

/// <summary>
/// Core cache service contract for distributed caching.
/// Designed for CQRS read-path optimization with graceful fallback.
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// Gets a value from cache or creates it using the factory function (read-through).
    /// This is the primary read-path method for CQRS query handlers.
    /// </summary>
    Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a value from cache. Returns default if not found.
    /// </summary>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a value in cache with optional expiration.
    /// Typically used on the CQRS write-path after a successful command.
    /// </summary>
    Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a value in cache with advanced expiration options (sliding, tagging).
    /// </summary>
    Task SetAsync<T>(string key, T value, CacheEntryOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a specific key from cache.
    /// Primary invalidation method for CQRS command handlers.
    /// </summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all keys matching a prefix pattern using Redis SCAN.
    /// Useful for invalidating entity groups (e.g., all dealership keys).
    /// </summary>
    Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the sliding expiration of a cache entry.
    /// </summary>
    Task RefreshAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets multiple keys in a single batch operation (pipeline).
    /// More efficient than calling GetAsync multiple times.
    /// </summary>
    Task<Dictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets multiple key-value pairs in a single batch operation (pipeline).
    /// More efficient than calling SetAsync multiple times.
    /// </summary>
    Task SetManyAsync<T>(Dictionary<string, T> items, TimeSpan? expiration = null, CancellationToken cancellationToken = default);
}
