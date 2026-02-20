using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TheTechLoop.HybridCache.Configuration;

namespace TheTechLoop.HybridCache.Services;

/// <summary>
/// Background service that subscribes to Redis Pub/Sub cache invalidation events.
/// Automatically removes invalidated keys from both L1 (memory) and L2 (Redis) caches.
/// Each microservice instance runs its own subscriber to stay in sync.
/// </summary>
public class CacheInvalidationSubscriber : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDistributedCache _distributedCache;
    private readonly IMemoryCache? _memoryCache;
    private readonly ILogger<CacheInvalidationSubscriber> _logger;
    private readonly string _channel;
    private readonly string _instanceName;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheInvalidationSubscriber"/> class.
    /// </summary>
    /// <param name="redis"></param>
    /// <param name="distributedCache"></param>
    /// <param name="logger"></param>
    /// <param name="config"></param>
    /// <param name="memoryCache"></param>
    public CacheInvalidationSubscriber(
        IConnectionMultiplexer redis,
        IDistributedCache distributedCache,
        ILogger<CacheInvalidationSubscriber> logger,
        IOptions<CacheConfig> config,
        IMemoryCache? memoryCache = null)
    {
        _redis = redis;
        _distributedCache = distributedCache;
        _logger = logger;
        _memoryCache = memoryCache;
        _channel = config.Value.InvalidationChannel;
        _instanceName = config.Value.InstanceName ?? string.Empty;
    }

    /// <summary>
    /// Executes the background service.
    /// </summary>
    /// <param name="stoppingToken"></param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var subscriber = _redis.GetSubscriber();

            await subscriber.SubscribeAsync(
                RedisChannel.Literal(_channel),
                (channel, message) =>
                {
                    // Fire-and-forget with proper error handling
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var payload = message.ToString();
                            await HandleInvalidationAsync(payload, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing cache invalidation message: {Message}", message);
                        }
                    }, stoppingToken);
                });

            _logger.LogInformation(
                "Cache invalidation subscriber started on channel: {Channel}", _channel);

            // Keep alive until cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Cache invalidation subscriber stopping");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache invalidation subscriber failed");
        }
    }

    private async Task HandleInvalidationAsync(string payload, CancellationToken ct)
    {
        if (payload.StartsWith("key:"))
        {
            var key = payload[4..];
            _memoryCache?.Remove(key);
            await _distributedCache.RemoveAsync(key, ct);
            _logger.LogInformation("Invalidated cache key: {Key}", key);
        }
        else if (payload.StartsWith("prefix:"))
        {
            var prefix = payload[7..];
            _logger.LogInformation(
                "Received prefix invalidation: {Prefix}. " +
                "L1 cache cleared for matching entries if tracked. " +
                "L2 prefix deletion requires SCAN via IConnectionMultiplexer.",
                prefix);

            // Prefix-based deletion via Redis SCAN
            await RemoveByPrefixViaScanAsync(prefix, ct);
        }
    }

    private async Task RemoveByPrefixViaScanAsync(string prefix, CancellationToken ct)
    {
        try
        {
            var db = _redis.GetDatabase();
            var server = _redis.GetServers().FirstOrDefault();

            if (server is null)
                return;

            // IDistributedCache automatically prepends InstanceName when writing keys,
            // so raw IConnectionMultiplexer SCAN must include it to match the full Redis key.
            var pattern = $"{_instanceName}{prefix}*";
            var keys = new List<RedisKey>();

            await foreach (var key in server.KeysAsync(pattern: pattern).WithCancellation(ct))
            {
                keys.Add(key);

                // Batch delete in chunks
                if (keys.Count >= 100)
                {
                    await db.KeyDeleteAsync([.. keys]);
                    keys.Clear();
                }
            }

            if (keys.Count > 0)
                await db.KeyDeleteAsync([.. keys]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during prefix SCAN deletion for prefix: {Prefix}", prefix);
        }
    }
}
