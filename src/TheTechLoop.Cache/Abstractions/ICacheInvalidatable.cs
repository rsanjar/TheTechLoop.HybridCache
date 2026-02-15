namespace TheTechLoop.Cache.Abstractions;

/// <summary>
/// Marker interface for MediatR commands that should automatically invalidate
/// cache entries after successful execution.
/// <para>
/// Applied by <see cref="Behaviors.CacheInvalidationBehavior{TRequest,TResponse}"/>
/// which runs AFTER the handler completes, removing the specified keys and
/// publishing cross-service invalidation events via Pub/Sub.
/// </para>
/// </summary>
/// <example>
/// <code>
/// public record UpdateDealershipCommand(int Id, string Name) : IRequest&lt;bool&gt;, ICacheInvalidatable
/// {
///     public IReadOnlyList&lt;string&gt; CacheKeysToInvalidate =&gt;
///         [$"Dealership:{Id}"];
///
///     public IReadOnlyList&lt;string&gt; CachePrefixesToInvalidate =&gt;
///         ["Dealership:Search"];
/// }
/// </code>
/// </example>
public interface ICacheInvalidatable
{
    /// <summary>
    /// Exact cache keys to remove after the command succeeds.
    /// Will be automatically prefixed by <see cref="Keys.CacheKeyBuilder"/>.
    /// <para>Example: <c>["Dealership:42"]</c></para>
    /// </summary>
    IReadOnlyList<string> CacheKeysToInvalidate { get; }

    /// <summary>
    /// Cache key prefixes for pattern-based invalidation.
    /// All keys starting with this prefix will be removed.
    /// Will be automatically prefixed by <see cref="Keys.CacheKeyBuilder"/>.
    /// <para>Example: <c>["Dealership:Search", "Dealership:List"]</c></para>
    /// </summary>
    IReadOnlyList<string> CachePrefixesToInvalidate { get; }
}
