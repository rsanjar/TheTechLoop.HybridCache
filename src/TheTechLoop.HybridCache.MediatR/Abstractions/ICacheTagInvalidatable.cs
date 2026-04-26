namespace TheTechLoop.HybridCache.MediatR.Abstractions;

/// <summary>
/// Opt-in extension for MediatR requests that invalidate cache entries by tag
/// after the handler completes successfully.
/// </summary>
public interface ICacheTagInvalidatable
{
    /// <summary>
    /// Logical cache tags to invalidate. Tags are automatically scoped by the
    /// configured cache service name and version.
    /// </summary>
    IReadOnlyList<string> CacheTagsToInvalidate { get; }
}
