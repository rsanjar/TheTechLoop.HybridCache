using System.Diagnostics.Metrics;

namespace TheTechLoop.HybridCache.Metrics;

/// <summary>
/// OpenTelemetry-compatible cache metrics using System.Diagnostics.Metrics.
/// Tracks hits, misses, errors, evictions, and circuit breaker bypasses.
/// Metrics are exported via any configured OTel exporter (Prometheus, OTLP, etc).
/// </summary>
public sealed class CacheMetrics
{
    /// <summary>
    /// The name of the meter used for cache metrics.
    /// </summary>
    public const string MeterName = "TheTechLoop.Cache";

    private readonly Counter<long> _hits;
    private readonly Counter<long> _misses;
    private readonly Counter<long> _errors;
    private readonly Counter<long> _evictions;
    private readonly Counter<long> _circuitBreakerBypasses;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _lockWaitDuration;
    private readonly Histogram<long> _batchSize;
    private readonly Histogram<double> _scanDuration;
    private readonly Counter<long> _scanDeletedKeys;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheMetrics"/> class.
    /// </summary>
    /// <param name="meterFactory"></param>
    public CacheMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _hits = meter.CreateCounter<long>(
            "cache.hits",
            description: "Number of cache hits");

        _misses = meter.CreateCounter<long>(
            "cache.misses",
            description: "Number of cache misses");

        _errors = meter.CreateCounter<long>(
            "cache.errors",
            description: "Number of cache operation errors");

        _evictions = meter.CreateCounter<long>(
            "cache.evictions",
            description: "Number of cache evictions (explicit removals)");

        _circuitBreakerBypasses = meter.CreateCounter<long>(
            "cache.circuit_breaker.bypasses",
            description: "Number of requests that bypassed cache due to open circuit breaker");

        _duration = meter.CreateHistogram<double>(
            "cache.duration",
            unit: "ms",
            description: "Cache operation duration in milliseconds");

        _lockWaitDuration = meter.CreateHistogram<double>(
            "cache.lock.wait_duration",
            unit: "ms",
            description: "Time spent waiting for stampede-protection lock acquisition");

        _batchSize = meter.CreateHistogram<long>(
            "cache.batch.size",
            unit: "{keys}",
            description: "Number of keys in bulk GetMany/SetMany operations");

        _scanDuration = meter.CreateHistogram<double>(
            "cache.scan.duration",
            unit: "ms",
            description: "Duration of prefix SCAN-based key deletion operations");

        _scanDeletedKeys = meter.CreateCounter<long>(
            "cache.scan.deleted_keys",
            description: "Total number of keys deleted via prefix SCAN operations");
    }

    /// <summary>
    /// Records a cache hit.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="durationMs"></param>
    /// <param name="level"></param>
    public void RecordHit(string key, double durationMs, string level = "L2")
    {
        _hits.Add(1,
            new KeyValuePair<string, object?>("cache.key_prefix", ExtractPrefix(key)),
            new KeyValuePair<string, object?>("cache.level", level));

        _duration.Record(durationMs,
            new KeyValuePair<string, object?>("cache.operation", "hit"),
            new KeyValuePair<string, object?>("cache.level", level));
    }

    /// <summary>
    /// Records a cache miss.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="durationMs"></param>
    /// <param name="level"></param>
    public void RecordMiss(string key, double durationMs, string level = "L2")
    {
        _misses.Add(1,
            new KeyValuePair<string, object?>("cache.key_prefix", ExtractPrefix(key)));

        _duration.Record(durationMs,
            new KeyValuePair<string, object?>("cache.operation", "miss"));
    }

    /// <summary>
    /// Records a cache operation error.
    /// </summary>
    /// <param name="key"></param>
    public void RecordError(string key)
    {
        _errors.Add(1,
            new KeyValuePair<string, object?>("cache.key_prefix", ExtractPrefix(key)));
    }

    /// <summary>
    /// Records a cache eviction.
    /// </summary>
    /// <param name="key"></param>
    public void RecordEviction(string key)
    {
        _evictions.Add(1,
            new KeyValuePair<string, object?>("cache.key_prefix", ExtractPrefix(key)));
    }

    /// <summary>
    /// Records a cache circuit breaker bypass.
    /// </summary>
    public void RecordCircuitBreakerBypass()
    {
        _circuitBreakerBypasses.Add(1);
    }

    /// <summary>
    /// Records the time spent waiting for a stampede-protection lock.
    /// </summary>
    /// <param name="durationMs">Wait duration in milliseconds</param>
    /// <param name="acquired">Whether the lock was successfully acquired</param>
    public void RecordLockWait(double durationMs, bool acquired)
    {
        _lockWaitDuration.Record(durationMs,
            new KeyValuePair<string, object?>("cache.lock.acquired", acquired));
    }

    /// <summary>
    /// Records the batch size for a bulk GetMany/SetMany operation.
    /// </summary>
    /// <param name="count">Number of keys in the batch</param>
    /// <param name="operation">"get" or "set"</param>
    public void RecordBatchSize(long count, string operation)
    {
        _batchSize.Record(count,
            new KeyValuePair<string, object?>("cache.operation", operation));
    }

    /// <summary>
    /// Records prefix SCAN deletion metrics.
    /// </summary>
    /// <param name="durationMs">Scan duration in milliseconds</param>
    /// <param name="deletedCount">Number of keys deleted</param>
    public void RecordScanDeletion(double durationMs, long deletedCount)
    {
        _scanDuration.Record(durationMs);
        _scanDeletedKeys.Add(deletedCount);
    }

    /// <summary>
    /// Extracts the first segment of a cache key to use as a low-cardinality metric tag.
    /// Example: "company-svc:v1:User:42" → "company-svc"
    /// </summary>
    private static string ExtractPrefix(string key)
    {
        var idx = key.IndexOf(':');
        return idx > 0 ? key[..idx] : key;
    }
}
