namespace TheTechLoop.HybridCache.Abstractions;

/// <summary>
/// Estimates the relative memory size of a cache entry for L1 (in-memory) cache
/// eviction decisions. Implementations return a unit-less weight: higher values
/// mean the entry occupies more of the configured <c>SizeLimit</c> budget.
/// <para>
/// Register a custom implementation to improve memory fairness for your
/// specific payload families (e.g., large DTOs, binary blobs, collections
/// of complex objects).
/// </para>
/// </summary>
public interface ICacheSizeEstimator
{
    /// <summary>
    /// Returns the estimated relative size of <paramref name="value"/>.
    /// Must return at least 1.
    /// </summary>
    long EstimateSize<T>(T value);
}

/// <summary>
/// Default size estimator that accounts for common .NET payload shapes.
/// <list type="bullet">
///   <item><c>string</c> — ~2 bytes per char, 1 unit per 1 KB</item>
///   <item><c>byte[]</c> — 1 unit per 1 KB</item>
///   <item><c>ICollection</c> — count × per-element base weight</item>
///   <item><c>IDictionary</c> — count × per-entry base weight</item>
///   <item>everything else — 1 unit</item>
/// </list>
/// </summary>
public sealed class DefaultCacheSizeEstimator : ICacheSizeEstimator
{
    private const int BytesPerUnit = 1024;

    /// <inheritdoc />
    public long EstimateSize<T>(T value)
    {
        if (value is null)
            return 1;

        return value switch
        {
            string s => Math.Max(1, s.Length * 2 / BytesPerUnit),
            byte[] b => Math.Max(1, b.Length / BytesPerUnit),
            System.Collections.IDictionary d => Math.Max(1, d.Count * 2),
            System.Collections.ICollection c => Math.Max(1, c.Count),
            _ => 1
        };
    }
}
