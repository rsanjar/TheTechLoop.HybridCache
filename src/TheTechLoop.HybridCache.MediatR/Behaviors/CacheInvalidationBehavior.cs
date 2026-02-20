using MediatR;
using Microsoft.Extensions.Logging;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Keys;
using TheTechLoop.HybridCache.MediatR.Abstractions;

namespace TheTechLoop.HybridCache.MediatR.Behaviors;

/// <summary>
/// MediatR pipeline behavior that automatically invalidates cache entries
/// after a command implementing <see cref="ICacheInvalidatable"/> succeeds.
/// <para>
/// This behavior runs AFTER the handler completes successfully.
/// It removes the specified exact keys and prefix patterns from the cache,
/// then publishes cross-service invalidation events via Pub/Sub
/// (if <see cref="ICacheInvalidationPublisher"/> is registered).
/// </para>
/// <para>
/// Cache invalidation is best-effort: a 5-second timeout is applied so that
/// a Redis failure or reconnect delay never blocks the HTTP response.
/// </para>
/// <para>
/// Register in DI via <c>services.AddTheTechLoopCacheBehaviors()</c>
/// or manually via <c>cfg.AddBehavior(typeof(IPipelineBehavior&lt;,&gt;), typeof(CacheInvalidationBehavior&lt;,&gt;))</c>.
/// </para>
/// </summary>
/// <typeparam name="TRequest">The MediatR request type</typeparam>
/// <typeparam name="TResponse">The response type</typeparam>
public sealed class CacheInvalidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private static readonly TimeSpan InvalidationTimeout = TimeSpan.FromSeconds(5);

    private readonly ICacheService _cache;
    private readonly ICacheInvalidationPublisher? _publisher;
    private readonly CacheKeyBuilder _keyBuilder;
    private readonly ILogger<CacheInvalidationBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CacheInvalidationBehavior{TRequest,TResponse}"/>.
    /// </summary>
    /// <param name="cache">Cache service for local invalidation</param>
    /// <param name="keyBuilder">Key builder for service-scoped prefixing</param>
    /// <param name="logger">Logger</param>
    /// <param name="publisher">
    /// Optional Pub/Sub publisher for cross-service invalidation.
    /// Null when <c>AddTheTechLoopCacheInvalidation()</c> is not registered.
    /// </param>
    public CacheInvalidationBehavior(
        ICacheService cache,
        CacheKeyBuilder keyBuilder,
        ILogger<CacheInvalidationBehavior<TRequest, TResponse>> logger,
        ICacheInvalidationPublisher? publisher = null)
    {
        _cache = cache;
        _keyBuilder = keyBuilder;
        _logger = logger;
        _publisher = publisher;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Execute the handler first
        var response = await next(cancellationToken);

        // Only invalidate after successful execution
        if (request is not ICacheInvalidatable invalidatable)
            return response;

        _logger.LogDebug(
            "CacheInvalidationBehavior processing {RequestType}: " +
            "{KeyCount} keys, {PrefixCount} prefixes",
            typeof(TRequest).Name,
            invalidatable.CacheKeysToInvalidate.Count,
            invalidatable.CachePrefixesToInvalidate.Count);

        // Use a short timeout so a Redis reconnect delay never blocks the HTTP response.
        // Invalidation is best-effort: a stale cache entry will expire on its own.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(InvalidationTimeout);
        var ct = timeoutCts.Token;

        // Invalidate exact keys
        foreach (var key in invalidatable.CacheKeysToInvalidate)
        {
            var scopedKey = _keyBuilder.Key(key);

            await _cache.RemoveAsync(scopedKey, ct);

            if (_publisher is not null)
                await _publisher.PublishAsync(scopedKey, ct);
        }

        // Invalidate prefix patterns
        foreach (var prefix in invalidatable.CachePrefixesToInvalidate)
        {
            var scopedPrefix = _keyBuilder.Key(prefix);

            await _cache.RemoveByPrefixAsync(scopedPrefix, ct);

            if (_publisher is not null)
                await _publisher.PublishPrefixAsync(scopedPrefix, ct);
        }

        return response;
    }
}
