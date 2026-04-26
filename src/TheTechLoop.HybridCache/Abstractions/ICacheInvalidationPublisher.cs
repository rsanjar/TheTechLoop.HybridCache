namespace TheTechLoop.HybridCache.Abstractions;

/// <summary>
/// Publishes cache invalidation events across microservices via Redis Pub/Sub.
/// Used on the CQRS write-path to notify all service instances of stale data.
/// </summary>
public interface ICacheInvalidationPublisher
{
    /// <summary>
    /// Publishes a cache invalidation event for a specific key.
    /// All subscribed microservice instances will remove this key from their local/distributed caches.
    /// </summary>
    Task PublishAsync(string cacheKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a cache invalidation event for all keys matching a prefix pattern.
    /// </summary>
    Task PublishPrefixAsync(string prefix, CancellationToken cancellationToken = default);
}

/// <summary>
/// Publishes cache invalidation events for cache tags across microservices.
/// </summary>
public interface ICacheTagInvalidationPublisher
{
    /// <summary>
    /// Publishes a cache invalidation event for all keys associated with a tag.
    /// </summary>
    Task PublishTagAsync(string tag, CancellationToken cancellationToken = default);
}

/// <summary>
/// Removes local and distributed cache entries associated with cache tags.
/// </summary>
public interface ICacheTagInvalidationService
{
    /// <summary>
    /// Removes all cache entries associated with the given tag.
    /// </summary>
    Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default);
}
