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
    private readonly ICacheTagInvalidationService? _tagInvalidationService;
    private readonly ICacheTagInvalidationPublisher? _tagPublisher;
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
    /// <param name="tagInvalidationService">Optional tag invalidation service for local tag invalidation.</param>
    /// <param name="tagPublisher">Optional Pub/Sub publisher for cross-service tag invalidation.</param>
    public CacheInvalidationBehavior(
        ICacheService cache,
        CacheKeyBuilder keyBuilder,
        ILogger<CacheInvalidationBehavior<TRequest, TResponse>> logger,
        ICacheInvalidationPublisher? publisher = null,
        ICacheTagInvalidationService? tagInvalidationService = null,
        ICacheTagInvalidationPublisher? tagPublisher = null)
    {
        _cache = cache;
        _keyBuilder = keyBuilder;
        _logger = logger;
        _publisher = publisher;
        _tagInvalidationService = tagInvalidationService;
        _tagPublisher = tagPublisher;
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
        var invalidatable = request as ICacheInvalidatable;
        var tagInvalidatable = request as ICacheTagInvalidatable;

        if (invalidatable is null && tagInvalidatable is null)
            return response;

        _logger.LogDebug(
            "CacheInvalidationBehavior processing {RequestType}: " +
            "{KeyCount} keys, {PrefixCount} prefixes, {TagCount} tags",
            typeof(TRequest).Name,
            invalidatable?.CacheKeysToInvalidate.Count ?? 0,
            invalidatable?.CachePrefixesToInvalidate.Count ?? 0,
            tagInvalidatable?.CacheTagsToInvalidate.Count ?? 0);

        // Use a short timeout so a Redis reconnect delay never blocks the HTTP response.
        // Invalidation is best-effort: a stale cache entry will expire on its own.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(InvalidationTimeout);
        var ct = timeoutCts.Token;

        if (invalidatable is not null)
        {
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
        }

        if (tagInvalidatable is not null)
        {
            foreach (var tag in tagInvalidatable.CacheTagsToInvalidate)
            {
                var scopedTag = _keyBuilder.Key(tag);

                if (_tagInvalidationService is not null)
                {
                    await _tagInvalidationService.RemoveByTagAsync(scopedTag, ct);
                }
                else
                {
                    _logger.LogWarning(
                        "Tag invalidation service is not registered. Skipping local invalidation for tag: {Tag}",
                        scopedTag);
                }

                if (_tagPublisher is not null)
                {
                    await _tagPublisher.PublishTagAsync(scopedTag, ct);
                }
                else
                {
                    _logger.LogDebug(
                        "Tag invalidation publisher is not registered. Skipping cross-service invalidation for tag: {Tag}",
                        scopedTag);
                }
            }
        }

        return response;
    }
}
