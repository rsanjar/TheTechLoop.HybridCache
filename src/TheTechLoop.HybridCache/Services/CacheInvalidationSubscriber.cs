using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Diagnostics;
using System.Threading.Channels;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Configuration;
using TheTechLoop.HybridCache.Metrics;
using TheTechLoop.HybridCache.Tagging;

namespace TheTechLoop.HybridCache.Services;

/// <summary>
/// Background service that subscribes to Redis Pub/Sub cache invalidation events.
/// Automatically removes invalidated keys from both L1 (memory) and L2 (Redis) caches.
/// Each microservice instance runs its own subscriber to stay in sync.
/// <para>
/// Messages are dispatched through a bounded channel with a single consumer,
/// providing backpressure under bursts without unbounded task creation.
/// </para>
/// </summary>
public class CacheInvalidationSubscriber : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDistributedCache _distributedCache;
    private readonly IMemoryCache? _memoryCache;
    private readonly ILogger<CacheInvalidationSubscriber> _logger;
    private readonly CacheMetrics _metrics;
    private readonly ICacheTagInvalidationService? _tagInvalidationService;
    private readonly ICacheTagService? _tagService;
    private readonly string _channel;
    private readonly string _instanceName;

    private readonly Channel<string> _messageChannel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheInvalidationSubscriber"/> class.
    /// </summary>
    /// <param name="redis"></param>
    /// <param name="distributedCache"></param>
    /// <param name="logger"></param>
    /// <param name="config"></param>
    /// <param name="metrics"></param>
    /// <param name="memoryCache"></param>
    /// <param name="tagInvalidationService"></param>
    /// <param name="tagService"></param>
    public CacheInvalidationSubscriber(
        IConnectionMultiplexer redis,
        IDistributedCache distributedCache,
        ILogger<CacheInvalidationSubscriber> logger,
        IOptions<CacheConfig> config,
        CacheMetrics metrics,
        IMemoryCache? memoryCache = null,
        ICacheTagInvalidationService? tagInvalidationService = null,
        ICacheTagService? tagService = null)
    {
        _redis = redis;
        _distributedCache = distributedCache;
        _logger = logger;
        _metrics = metrics;
        _memoryCache = memoryCache;
        _tagInvalidationService = tagInvalidationService;
        _tagService = tagService;
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
                (_, message) =>
                {
                    if (!_messageChannel.Writer.TryWrite(message.ToString()))
                    {
                        _logger.LogWarning("Invalidation channel full, dropped oldest message");
                    }
                });

            _logger.LogInformation(
                "Cache invalidation subscriber started on channel: {Channel}", _channel);

            // Single consumer loop — processes messages sequentially
            await foreach (var payload in _messageChannel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await HandleInvalidationAsync(payload, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing cache invalidation message: {Message}", payload);
                }
            }
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
            if (_tagService is not null)
                await _tagService.RemoveAsync(key, ct);

            _logger.LogDebug("Invalidated cache key: {Key}", key);
        }
        else if (payload.StartsWith("prefix:"))
        {
            var prefix = payload[7..];
            _logger.LogDebug(
                "Received prefix invalidation: {Prefix}. " +
                "L1 cache cleared for matching entries if tracked. " +
                "L2 prefix deletion requires SCAN via IConnectionMultiplexer.",
                prefix);

            // Prefix-based deletion via Redis SCAN
            await RemoveByPrefixViaScanAsync(prefix, ct);
        }
        else if (payload.StartsWith("tag:"))
        {
            var tag = payload[4..];
            if (_tagInvalidationService is not null)
            {
                await _tagInvalidationService.RemoveByTagAsync(tag, ct);
                _logger.LogDebug("Invalidated cache tag: {Tag}", tag);
            }
            else
            {
                _logger.LogWarning(
                    "Received tag invalidation for {Tag}, but no tag invalidation service is registered",
                    tag);
            }
        }
    }

    private async Task RemoveByPrefixViaScanAsync(string prefix, CancellationToken ct)
    {
        try
        {
            var db = _redis.GetDatabase();
            var scanSw = Stopwatch.StartNew();

            // IDistributedCache automatically prepends InstanceName when writing keys,
            // so raw IConnectionMultiplexer SCAN must include it to match the full Redis key.
            var pattern = $"{_instanceName}{prefix}*";
            var deletedCount = 0L;

            // Iterate all connected primary/master endpoints for cluster-awareness.
            // In standalone setups this yields a single server; in clusters it covers all shards.
            foreach (var server in _redis.GetServers().Where(s => s.IsConnected && !s.IsReplica))
            {
                var keys = new List<RedisKey>();

                await foreach (var key in server.KeysAsync(pattern: pattern).WithCancellation(ct))
                {
                    keys.Add(key);

                    // Batch delete in chunks
                    if (keys.Count >= 100)
                    {
                        await db.KeyDeleteAsync([.. keys]);
                        deletedCount += keys.Count;
                        keys.Clear();
                    }
                }

                if (keys.Count > 0)
                {
                    await db.KeyDeleteAsync([.. keys]);
                    deletedCount += keys.Count;
                }
            }

            scanSw.Stop();
            _metrics.RecordScanDeletion(scanSw.Elapsed.TotalMilliseconds, deletedCount);
            _logger.LogDebug("Prefix SCAN deletion complete: {Prefix}, deleted {Count} keys", prefix, deletedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during prefix SCAN deletion for prefix: {Prefix}", prefix);
        }
    }
}
