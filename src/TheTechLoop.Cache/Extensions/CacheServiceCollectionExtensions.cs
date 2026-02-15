using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TheTechLoop.Cache.Abstractions;
using TheTechLoop.Cache.Behaviors;
using TheTechLoop.Cache.Compression;
using TheTechLoop.Cache.Configuration;
using TheTechLoop.Cache.Keys;
using TheTechLoop.Cache.Metrics;
using TheTechLoop.Cache.Services;
using TheTechLoop.Cache.Streams;
using TheTechLoop.Cache.Tagging;
using TheTechLoop.Cache.Warming;

namespace TheTechLoop.Cache.Extensions;

/// <summary>
/// DI registration extension methods for TheTechLoop.Cache.
/// </summary>
public static class CacheServiceCollectionExtensions
{
    private const string ConfigSection = "TheTechLoopCache";

    /// <summary>
    /// Registers the core distributed cache services:
    /// Redis IDistributedCache, ICacheService, IDistributedLock, CacheMetrics,
    /// CacheKeyBuilder, and health checks.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">Application configuration</param>
    /// <param name="configSectionName">Configuration section name (default: "TheTechLoopCache")</param>
    public static IServiceCollection AddTheTechLoopCache(
        this IServiceCollection services,
        IConfiguration configuration,
        string configSectionName = ConfigSection)
    {
        // Bind and validate configuration
        services.AddOptions<CacheConfig>()
            .Bind(configuration.GetSection(configSectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var config = configuration.GetSection(configSectionName).Get<CacheConfig>() ?? new CacheConfig();

        // Register CacheMetrics (requires IMeterFactory which is registered by default in .NET 8+)
        services.AddSingleton<CacheMetrics>();

        // Register effectiveness metrics if enabled
        if (config.EnableEffectivenessMetrics)
        {
            services.AddSingleton<CacheEffectivenessMetrics>();
        }

        // Register CacheKeyBuilder scoped to this service
        services.AddSingleton(new CacheKeyBuilder(config.ServiceName, config.CacheVersion));

        if (!config.Enabled)
        {
            // Register no-op implementations when cache is disabled
            services.AddSingleton<ICacheService, NoOpCacheService>();
            services.AddSingleton<IDistributedLock, NoOpDistributedLock>();
            return services;
        }

        // Register Redis distributed cache
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = config.Configuration;
            options.InstanceName = config.InstanceName;
        });

        // Register IConnectionMultiplexer as singleton for Pub/Sub, locks, and SCAN
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var redisOptions = ConfigurationOptions.Parse(config.Configuration);
            redisOptions.AbortOnConnectFail = false;
            redisOptions.ConnectRetry = 3;
            redisOptions.ConnectTimeout = 5000;
            redisOptions.SyncTimeout = 5000;
            redisOptions.KeepAlive = 60;  // ✅ Keep connection alive
            redisOptions.ReconnectRetryPolicy = new ExponentialRetry(5000, 60000);  // ✅ Exponential backoff

            var multiplexer = ConnectionMultiplexer.Connect(redisOptions);

            // Log connection events
            multiplexer.ConnectionFailed += (sender, args) =>
            {
                var logger = sp.GetRequiredService<ILogger<IConnectionMultiplexer>>();
                logger.LogError("Redis connection failed: {EndPoint} - {FailureType}", args.EndPoint, args.FailureType);
            };

            multiplexer.ConnectionRestored += (sender, args) =>
            {
                var logger = sp.GetRequiredService<ILogger<IConnectionMultiplexer>>();
                logger.LogInformation("Redis connection restored: {EndPoint}", args.EndPoint);
            };

            return multiplexer;
        });

        // Register distributed lock
        services.AddSingleton<IDistributedLock, RedisDistributedLock>();

        // Register cache tagging if enabled
        if (config.EnableTagging)
        {
            services.AddSingleton<ICacheTagService, RedisCacheTagService>();
        }

        // Register cache service (single-level Redis)
        services.AddSingleton<ICacheService, RedisCacheService>();

        // Wrap with compression if enabled
        if (config.EnableCompression)
        {
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ICacheService));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
                services.AddSingleton<ICacheService>(sp =>
                {
                    var inner = ActivatorUtilities.CreateInstance<RedisCacheService>(sp);
                    return new CompressedCacheService(inner, config.CompressionThresholdBytes);
                });
            }
        }

        // Health checks
        if (!string.IsNullOrEmpty(config.Configuration))
        {
            services.AddHealthChecks()
                .AddRedis(
                    config.Configuration.Split(',')[0],
                    name: "redis-cache",
                    tags: ["cache", "redis", "ready"]);
        }

        return services;
    }

    /// <summary>
    /// Upgrades the cache to multi-level: L1 in-memory + L2 Redis.
    /// Call AFTER AddTheTechLoopCache. Replaces the ICacheService registration.
    /// </summary>
    public static IServiceCollection AddTheTechLoopMultiLevelCache(
        this IServiceCollection services,
        IConfiguration configuration,
        string configSectionName = ConfigSection)
    {
        var config = configuration.GetSection(configSectionName).Get<CacheConfig>() ?? new CacheConfig();

        if (!config.Enabled)
            return services;

        // Add memory cache for L1
        services.AddMemoryCache(options =>
        {
            options.SizeLimit = config.MemoryCache.SizeLimit;
        });

        // Replace ICacheService with multi-level implementation
        // Remove existing ICacheService registration
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(ICacheService));
        if (existing is not null)
            services.Remove(existing);

        services.AddSingleton<ICacheService, MultiLevelCacheService>();

        return services;
    }

    /// <summary>
    /// Enables cross-service cache invalidation via Redis Pub/Sub or Streams.
    /// Registers ICacheInvalidationPublisher and starts the background subscriber.
    /// </summary>
    public static IServiceCollection AddTheTechLoopCacheInvalidation(
        this IServiceCollection services,
        IConfiguration configuration,
        string configSectionName = ConfigSection)
    {
        var config = configuration.GetSection(configSectionName).Get<CacheConfig>() ?? new CacheConfig();

        if (config.UseStreamsForInvalidation)
        {
            // Use Redis Streams for guaranteed delivery
            services.AddSingleton<ICacheInvalidationStreamPublisher, RedisCacheInvalidationStreamPublisher>();
            services.AddHostedService<CacheInvalidationStreamConsumer>();
        }
        else
        {
            // Use Pub/Sub (default)
            services.AddSingleton<ICacheInvalidationPublisher, RedisCacheInvalidationPublisher>();
            services.AddHostedService<CacheInvalidationSubscriber>();
        }

        return services;
    }

    /// <summary>
    /// Enables cache warming on application startup.
    /// Register your ICacheWarmupStrategy implementations to define what data to pre-load.
    /// </summary>
    public static IServiceCollection AddTheTechLoopCacheWarmup(this IServiceCollection services)
    {
        services.AddHostedService<CacheWarmupService>();
        return services;
    }

    /// <summary>
    /// Registers MediatR pipeline behaviors for automatic caching and cache invalidation.
    /// <list type="bullet">
    ///   <item><see cref="CachingBehavior{TRequest,TResponse}"/> — auto-caches queries implementing <see cref="ICacheable"/></item>
    ///   <item><see cref="CacheInvalidationBehavior{TRequest,TResponse}"/> — auto-invalidates after commands implementing <see cref="ICacheInvalidatable"/></item>
    /// </list>
    /// <para>
    /// Call this AFTER <c>AddMediatR()</c> and <c>AddTheTechLoopCache()</c>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddTheTechLoopCacheBehaviors(this IServiceCollection services)
    {
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(CacheInvalidationBehavior<,>));
        return services;
    }
}
