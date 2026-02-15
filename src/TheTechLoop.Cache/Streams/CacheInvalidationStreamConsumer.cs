using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TheTechLoop.Cache.Configuration;

namespace TheTechLoop.Cache.Streams;

/// <summary>
/// Background service that processes cache invalidation messages from Redis Streams.
/// Unlike Pub/Sub, Redis Streams guarantee message delivery even if a consumer is temporarily offline.
/// <para>
/// Messages are persisted in the stream and each consumer group tracks its position.
/// Ideal for critical invalidation scenarios where message loss is unacceptable.
/// </para>
/// </summary>
public class CacheInvalidationStreamConsumer : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDistributedCache _distributedCache;
    private readonly IMemoryCache? _memoryCache;
    private readonly ILogger<CacheInvalidationStreamConsumer> _logger;
    private readonly string _streamName;
    private readonly string _consumerGroup;
    private readonly string _consumerName;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheInvalidationStreamConsumer"/> class.
    /// </summary>
    /// <param name="redis"></param>
    /// <param name="distributedCache"></param>
    /// <param name="logger"></param>
    /// <param name="config"></param>
    /// <param name="memoryCache"></param>
    public CacheInvalidationStreamConsumer(
        IConnectionMultiplexer redis,
        IDistributedCache distributedCache,
        ILogger<CacheInvalidationStreamConsumer> logger,
        IOptions<CacheConfig> config,
        IMemoryCache? memoryCache = null)
    {
        _redis = redis;
        _distributedCache = distributedCache;
        _memoryCache = memoryCache;
        _logger = logger;

        var serviceName = config.Value.ServiceName ?? "default";
        _streamName = $"cache:invalidation:stream";
        _consumerGroup = $"cache-consumers";
        _consumerName = $"{serviceName}:{Environment.MachineName}:{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Executes the background service.
    /// </summary>
    /// <param name="stoppingToken"></param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var db = _redis.GetDatabase();

            // Create consumer group if it doesn't exist
            try
            {
                await db.StreamCreateConsumerGroupAsync(_streamName, _consumerGroup, StreamPosition.NewMessages);
                _logger.LogInformation("Created consumer group {Group} for stream {Stream}", _consumerGroup, _streamName);
            }
            catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
            {
                // Group already exists, continue
                _logger.LogDebug("Consumer group {Group} already exists", _consumerGroup);
            }

            _logger.LogInformation(
                "Cache invalidation stream consumer started: {Consumer} in group {Group}",
                _consumerName, _consumerGroup);

            // Process messages in a loop
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Read new messages from the stream
                    var entries = await db.StreamReadGroupAsync(
                        _streamName,
                        _consumerGroup,
                        _consumerName,
                        StreamPosition.NewMessages,
                        count: 10);

                    if (entries.Length == 0)
                    {
                        await Task.Delay(1000, stoppingToken);
                        continue;
                    }

                    foreach (var entry in entries)
                    {
                        try
                        {
                            await ProcessInvalidationMessageAsync(entry, stoppingToken);

                            // Acknowledge the message
                            await db.StreamAcknowledgeAsync(_streamName, _consumerGroup, entry.Id);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to process stream message {MessageId}", entry.Id);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reading from invalidation stream");
                    await Task.Delay(5000, stoppingToken);
                }
            }

            _logger.LogInformation("Cache invalidation stream consumer stopping");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache invalidation stream consumer failed");
        }
    }

    private async Task ProcessInvalidationMessageAsync(StreamEntry entry, CancellationToken ct)
    {
        var values = entry.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());

        if (!values.TryGetValue("type", out var type))
            return;

        switch (type)
        {
            case "key":
                if (values.TryGetValue("key", out var key))
                {
                    _memoryCache?.Remove(key);
                    await _distributedCache.RemoveAsync(key, ct);
                    _logger.LogInformation("Invalidated cache key: {Key}", key);
                }
                break;

            case "prefix":
                if (values.TryGetValue("prefix", out var prefix))
                {
                    _logger.LogInformation("Received prefix invalidation: {Prefix}", prefix);
                    await RemoveByPrefixViaScanAsync(prefix, ct);
                }
                break;

            case "tag":
                if (values.TryGetValue("tag", out var tag))
                {
                    _logger.LogInformation("Received tag invalidation: {Tag}", tag);
                    // Tag invalidation requires ICacheTagService
                    // Implementation deferred to consumer
                }
                break;
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

            var pattern = $"{prefix}*";
            var keys = new List<RedisKey>();

            await foreach (var key in server.KeysAsync(pattern: pattern).WithCancellation(ct))
            {
                keys.Add(key);

                if (keys.Count >= 100)
                {
                    await db.KeyDeleteAsync(keys.ToArray());
                    keys.Clear();
                }
            }

            if (keys.Any())
            {
                await db.KeyDeleteAsync(keys.ToArray());
            }

            _logger.LogInformation("Deleted keys matching prefix: {Prefix}", prefix);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove keys by prefix: {Prefix}", prefix);
        }
    }
}

/// <summary>
/// Publisher for Redis Streams-based cache invalidation.
/// </summary>
public interface ICacheInvalidationStreamPublisher
{
    /// <summary>
    /// Publishes a cache invalidation message for a specific cache key.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task PublishAsync(string key, CancellationToken cancellationToken = default);


    /// <summary>
    /// Publishes a cache invalidation message for a specific cache key prefix.
    /// </summary>
    /// <param name="prefix"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task PublishPrefixAsync(string prefix, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Publishes a cache invalidation message for a specific cache key tag.
    /// </summary>
    /// <param name="tag"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task PublishTagAsync(string tag, CancellationToken cancellationToken = default);
}

/// <summary>
/// Redis Streams implementation of cache invalidation publisher.
/// </summary>
public class RedisCacheInvalidationStreamPublisher : ICacheInvalidationStreamPublisher
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisCacheInvalidationStreamPublisher> _logger;
    private readonly string _streamName;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisCacheInvalidationStreamPublisher"/> class.
    /// </summary>
    /// <param name="redis"></param>
    /// <param name="logger"></param>
    public RedisCacheInvalidationStreamPublisher(
        IConnectionMultiplexer redis,
        ILogger<RedisCacheInvalidationStreamPublisher> logger)
    {
        _redis = redis;
        _logger = logger;
        _streamName = "cache:invalidation:stream";
    }

    /// <summary>
    /// Publishes a cache invalidation message for a specific cache key.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="cancellationToken"></param>
    public async Task PublishAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = _redis.GetDatabase();

            await db.StreamAddAsync(_streamName, new[]
            {
                new NameValueEntry("type", "key"),
                new NameValueEntry("key", key)
            });

            _logger.LogDebug("Published key invalidation to stream: {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish key invalidation to stream: {Key}", key);
        }
    }

    /// <summary>
    /// Publishes a cache invalidation message for a specific cache key prefix.
    /// </summary>
    /// <param name="prefix"></param>
    /// <param name="cancellationToken"></param>
    public async Task PublishPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = _redis.GetDatabase();

            await db.StreamAddAsync(_streamName, new[]
            {
                new NameValueEntry("type", "prefix"),
                new NameValueEntry("prefix", prefix)
            });

            _logger.LogDebug("Published prefix invalidation to stream: {Prefix}", prefix);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish prefix invalidation to stream: {Prefix}", prefix);
        }
    }

    /// <summary>
    /// Publishes a cache invalidation message for a specific cache key tag.
    /// </summary>
    /// <param name="tag"></param>
    /// <param name="cancellationToken"></param>
    public async Task PublishTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = _redis.GetDatabase();

            await db.StreamAddAsync(_streamName, new[]
            {
                new NameValueEntry("type", "tag"),
                new NameValueEntry("tag", tag)
            });

            _logger.LogDebug("Published tag invalidation to stream: {Tag}", tag);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish tag invalidation to stream: {Tag}", tag);
        }
    }
}
