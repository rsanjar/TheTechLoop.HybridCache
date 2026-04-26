namespace TheTechLoop.HybridCache.MediatR.Abstractions;

/// <summary>
/// Opt-in extension for cacheable MediatR requests that should be associated
/// with one or more cache tags for group invalidation.
/// </summary>
public interface ITaggedCacheable : ICacheable
{
    /// <summary>
    /// Logical cache tags for this request. Tags are automatically scoped by
    /// the configured cache service name and version.
    /// </summary>
    IReadOnlyList<string> CacheTags { get; }
}
