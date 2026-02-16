using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Configuration;

namespace TheTechLoop.HybridCache.Services;

/// <summary>
/// Publishes cache invalidation events to all subscribed microservice instances
/// via Redis Pub/Sub. Used on the CQRS write-path after successful commands.
/// </summary>
public class RedisCacheInvalidationPublisher : ICacheInvalidationPublisher
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisCacheInvalidationPublisher> _logger;
    private readonly string _channel;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisCacheInvalidationPublisher"/> class.
    /// </summary>
    /// <param name="redis"></param>
    /// <param name="logger"></param>
    /// <param name="config"></param>
    public RedisCacheInvalidationPublisher(
        IConnectionMultiplexer redis,
        ILogger<RedisCacheInvalidationPublisher> logger,
        IOptions<CacheConfig> config)
    {
        _redis = redis;
        _logger = logger;
        _channel = config.Value.InvalidationChannel;
    }

    /// <inheritdoc />
    public async Task PublishAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var subscriber = _redis.GetSubscriber();
            var message = $"key:{cacheKey}";
            await subscriber.PublishAsync(RedisChannel.Literal(_channel), message);

            _logger.LogInformation("Published cache invalidation for key: {Key}", cacheKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish cache invalidation for key: {Key}", cacheKey);
        }
    }

    /// <inheritdoc />
    public async Task PublishPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        try
        {
            var subscriber = _redis.GetSubscriber();
            var message = $"prefix:{prefix}";
            await subscriber.PublishAsync(RedisChannel.Literal(_channel), message);

            _logger.LogInformation("Published cache invalidation for prefix: {Prefix}", prefix);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish cache invalidation for prefix: {Prefix}", prefix);
        }
    }
}
