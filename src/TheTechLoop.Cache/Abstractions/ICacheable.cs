namespace TheTechLoop.Cache.Abstractions;

/// <summary>
/// Marker interface for MediatR queries that should be automatically cached
/// by <see cref="Behaviors.CachingBehavior{TRequest,TResponse}"/>.
/// <para>
/// Apply to any <c>IRequest&lt;T&gt;</c> to enable convention-based caching
/// without writing cache logic in the handler.
/// </para>
/// </summary>
/// <example>
/// <code>
/// public record GetDealershipByIdQuery(int Id) : IRequest&lt;Dealership?&gt;, ICacheable
/// {
///     public string CacheKey =&gt; $"Dealership:{Id}";
///     public TimeSpan CacheDuration =&gt; TimeSpan.FromMinutes(30);
/// }
/// </code>
/// </example>
public interface ICacheable
{
    /// <summary>
    /// The cache key for this request. Will be automatically prefixed
    /// with the service name and version by <see cref="Keys.CacheKeyBuilder"/>.
    /// <para>Example: <c>"Dealership:42"</c> → becomes <c>"company-svc:v1:Dealership:42"</c></para>
    /// </summary>
    string CacheKey { get; }

    /// <summary>
    /// How long the cached response should live.
    /// </summary>
    TimeSpan CacheDuration { get; }
}
