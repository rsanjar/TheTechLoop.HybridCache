using System.ComponentModel.DataAnnotations;

namespace TheTechLoop.HybridCache.Configuration;

/// <summary>
/// Configuration for the TheTechLoop distributed cache.
/// Bind to "TheTechLoopCache" section in appsettings.json.
/// </summary>
public sealed class CacheConfig
{
    /// <summary>
    /// Redis connection string (host:port,password=xxx,defaultDatabase=0,...)
    /// </summary>
    [Required(ErrorMessage = "Redis connection string is required")]
    public string Configuration { get; set; } = string.Empty;

    /// <summary>
    /// Instance name prefix for all cache keys (e.g., "TheTechLoop:Company:")
    /// </summary>
    [Required(ErrorMessage = "Instance name is required")]
    public string InstanceName { get; set; } = string.Empty;

    /// <summary>
    /// Logical service name used for key scoping across microservices (e.g., "company-svc")
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// Cache version prefix. Bump this when breaking DTO changes are deployed
    /// to automatically ignore stale cache entries.
    /// </summary>
    public string CacheVersion { get; set; } = "v1";

    /// <summary>
    /// Default cache expiration time in minutes
    /// </summary>
    [Range(1, 1440, ErrorMessage = "Default expiration must be between 1 and 1440 minutes")]
    public int DefaultExpirationMinutes { get; set; } = 60;

    /// <summary>
    /// Enable detailed debug logging for cache operations
    /// </summary>
    public bool EnableLogging { get; set; }

    /// <summary>
    /// Master switch to enable or disable caching entirely
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Circuit breaker configuration for graceful Redis degradation
    /// </summary>
    public CircuitBreakerConfig CircuitBreaker { get; set; } = new();

    /// <summary>
    /// L1 (in-memory) cache settings for multi-level caching
    /// </summary>
    public MemoryCacheConfig MemoryCache { get; set; } = new();

    /// <summary>
    /// Redis Pub/Sub invalidation channel name
    /// </summary>
    public string InvalidationChannel { get; set; } = "cache:invalidation";

    /// <summary>
    /// Use Redis Streams instead of Pub/Sub for guaranteed message delivery.
    /// Recommended for production environments.
    /// </summary>
    public bool UseStreamsForInvalidation { get; set; }

    /// <summary>
    /// Enable automatic cache compression for large values (> 1KB).
    /// </summary>
    public bool EnableCompression { get; set; }

    /// <summary>
    /// Compression threshold in bytes (default: 1024 = 1KB).
    /// Values larger than this are automatically compressed.
    /// </summary>
    public int CompressionThresholdBytes { get; set; } = 1024;

    /// <summary>
    /// Enable cache tagging for group invalidation.
    /// </summary>
    public bool EnableTagging { get; set; }

    /// <summary>
    /// Enable cache warming on startup.
    /// </summary>
    public bool EnableWarmup { get; set; }

    /// <summary>
    /// Enable entity-level cache effectiveness metrics.
    /// </summary>
    public bool EnableEffectivenessMetrics { get; set; } = true;
}

/// <summary>
/// Circuit breaker configuration for handling Redis outages gracefully
/// </summary>
public sealed class CircuitBreakerConfig
{
    /// <summary>
    /// Enable the circuit breaker pattern
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Duration in seconds to keep the circuit open after failure threshold
    /// </summary>
    [Range(5, 600)]
    public int BreakDurationSeconds { get; set; } = 60;

    /// <summary>
    /// Number of consecutive failures before opening the circuit
    /// </summary>
    [Range(1, 50)]
    public int FailureThreshold { get; set; } = 5;
}

/// <summary>
/// Configuration for the in-memory (L1) cache layer
/// </summary>
public sealed class MemoryCacheConfig
{
    /// <summary>
    /// Enable L1 in-memory cache in front of Redis
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Default L1 cache expiration in seconds. Should be much shorter than Redis TTL.
    /// </summary>
    [Range(1, 300)]
    public int DefaultExpirationSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of items to hold in L1 cache
    /// </summary>
    [Range(10, 100000)]
    public int SizeLimit { get; set; } = 1024;
}
