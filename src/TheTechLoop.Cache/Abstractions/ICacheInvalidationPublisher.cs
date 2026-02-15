namespace TheTechLoop.Cache.Abstractions;

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
