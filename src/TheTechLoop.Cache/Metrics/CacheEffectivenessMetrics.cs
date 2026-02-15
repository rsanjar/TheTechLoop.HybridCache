using System.Diagnostics.Metrics;

namespace TheTechLoop.Cache.Metrics;

/// <summary>
/// Enhanced cache metrics with entity-level tracking for cache effectiveness analysis.
/// Tracks hit rate, latency, and size by entity type (e.g., User, Dealership, Country).
/// </summary>
public sealed class CacheEffectivenessMetrics
{
    private readonly Counter<long> _entityHits;
    private readonly Counter<long> _entityMisses;
    private readonly Histogram<double> _entityLatency;
    private readonly Histogram<long> _entitySize;
    private readonly ObservableGauge<double> _hitRateByEntity;

    private readonly Dictionary<string, EntityStats> _entityStats = new();
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheEffectivenessMetrics"/> class.
    /// </summary>
    /// <param name="meterFactory"></param>
    public CacheEffectivenessMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("TheTechLoop.Cache.Effectiveness");

        _entityHits = meter.CreateCounter<long>(
            "cache.entity.hits",
            description: "Cache hits per entity type");

        _entityMisses = meter.CreateCounter<long>(
            "cache.entity.misses",
            description: "Cache misses per entity type");

        _entityLatency = meter.CreateHistogram<double>(
            "cache.entity.latency",
            unit: "ms",
            description: "Cache operation latency per entity type");

        _entitySize = meter.CreateHistogram<long>(
            "cache.entity.size",
            unit: "bytes",
            description: "Cached entity size in bytes");

        _hitRateByEntity = meter.CreateObservableGauge(
            "cache.entity.hit_rate",
            observeValues: () =>
            {
                lock (_lock)
                {
                    return _entityStats
                        .Where(kvp => kvp.Value.TotalRequests > 0)
                        .Select(kvp =>
                        {
                            var hitRate = (double)kvp.Value.Hits / kvp.Value.TotalRequests;
                            return new Measurement<double>(
                                hitRate,
                                new KeyValuePair<string, object?>("entity", kvp.Key));
                        });
                }
            },
            unit: "ratio",
            description: "Cache hit rate by entity type (0.0 - 1.0)");
    }

    /// <summary>
    /// Records a cache hit for an entity.
    /// </summary>
    /// <param name="entityType">Entity type (e.g., "User", "Dealership")</param>
    /// <param name="latencyMs">Operation latency in milliseconds</param>
    /// <param name="sizeBytes">Size of cached value in bytes</param>
    public void RecordEntityHit(string entityType, double latencyMs, long sizeBytes = 0)
    {
        _entityHits.Add(1, new KeyValuePair<string, object?>("entity", entityType));
        _entityLatency.Record(latencyMs, new KeyValuePair<string, object?>("entity", entityType));

        if (sizeBytes > 0)
            _entitySize.Record(sizeBytes, new KeyValuePair<string, object?>("entity", entityType));

        UpdateStats(entityType, isHit: true);
    }

    /// <summary>
    /// Records a cache miss for an entity.
    /// </summary>
    public void RecordEntityMiss(string entityType, double latencyMs)
    {
        _entityMisses.Add(1, new KeyValuePair<string, object?>("entity", entityType));
        _entityLatency.Record(latencyMs, new KeyValuePair<string, object?>("entity", entityType));

        UpdateStats(entityType, isHit: false);
    }

    /// <summary>
    /// Gets cache statistics for an entity type.
    /// </summary>
    public EntityCacheStats GetEntityStats(string entityType)
    {
        lock (_lock)
        {
            if (!_entityStats.TryGetValue(entityType, out var stats))
                return new EntityCacheStats(entityType, 0, 0, 0.0);

            var hitRate = stats.TotalRequests > 0
                ? (double)stats.Hits / stats.TotalRequests
                : 0.0;

            return new EntityCacheStats(entityType, stats.Hits, stats.Misses, hitRate);
        }
    }

    /// <summary>
    /// Gets cache statistics for all entity types.
    /// </summary>
    public IReadOnlyList<EntityCacheStats> GetAllEntityStats()
    {
        lock (_lock)
        {
            return _entityStats.Select(kvp =>
            {
                var hitRate = kvp.Value.TotalRequests > 0
                    ? (double)kvp.Value.Hits / kvp.Value.TotalRequests
                    : 0.0;

                return new EntityCacheStats(kvp.Key, kvp.Value.Hits, kvp.Value.Misses, hitRate);
            }).ToList();
        }
    }

    private void UpdateStats(string entityType, bool isHit)
    {
        lock (_lock)
        {
            if (!_entityStats.TryGetValue(entityType, out var stats))
            {
                stats = new EntityStats();
                _entityStats[entityType] = stats;
            }

            if (isHit)
                stats.Hits++;
            else
                stats.Misses++;
        }
    }

    private class EntityStats
    {
        public long Hits { get; set; }
        public long Misses { get; set; }
        public long TotalRequests => Hits + Misses;
    }
}

/// <summary>
/// Cache statistics for a specific entity type.
/// </summary>
public sealed record EntityCacheStats(
    string EntityType,
    long Hits,
    long Misses,
    double HitRate
)
{
    /// <summary>
    /// Gets the total number of requests (hits + misses).
    /// </summary>
    public long TotalRequests => Hits + Misses;

    /// <summary>
    /// Gets a string representation of the cache stats.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
        => $"{EntityType}: {Hits}/{TotalRequests} hits ({HitRate:P1} hit rate)";
}

/// <summary>
/// Helper extension methods for extracting entity type from cache keys.
/// </summary>
public static class CacheKeyExtensions
{
    /// <summary>
    /// Extracts entity type from a service-scoped cache key.
    /// Example: "company-svc:v1:Dealership:42" → "Dealership"
    /// </summary>
    public static string ExtractEntityType(this string key)
    {
        var parts = key.Split(':');

        // Assume pattern: {service}:{version}:{entity}:{id}
        if (parts.Length >= 3)
            return parts[2];

        // Fallback: return first segment
        return parts.Length > 0 ? parts[0] : "Unknown";
    }
}
