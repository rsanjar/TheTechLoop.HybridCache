using MediatR;
using Microsoft.Extensions.Logging;
using TheTechLoop.Cache.Abstractions;
using TheTechLoop.Cache.Keys;
using TheTechLoop.Cache.MediatR.Abstractions;

namespace TheTechLoop.Cache.MediatR.Behaviors;

/// <summary>
/// MediatR pipeline behavior that automatically caches responses for any
/// request implementing <see cref="ICacheable"/>.
/// <para>
/// This behavior intercepts the request BEFORE the handler runs.
/// If a cached value exists, it is returned immediately without executing the handler.
/// On a cache miss, the handler runs, and the result is stored in cache.
/// </para>
/// <para>
/// Register in DI via <c>services.AddTheTechLoopCacheBehaviors()</c>
/// or manually via <c>cfg.AddBehavior(typeof(IPipelineBehavior&lt;,&gt;), typeof(CachingBehavior&lt;,&gt;))</c>.
/// </para>
/// </summary>
/// <typeparam name="TRequest">The MediatR request type</typeparam>
/// <typeparam name="TResponse">The response type</typeparam>
public sealed class CachingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;
    private readonly ILogger<CachingBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CachingBehavior{TRequest,TResponse}"/>.
    /// </summary>
    public CachingBehavior(
        ICacheService cache,
        CacheKeyBuilder keyBuilder,
        ILogger<CachingBehavior<TRequest, TResponse>> logger)
    {
        _cache = cache;
        _keyBuilder = keyBuilder;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Only intercept requests that implement ICacheable
        if (request is not ICacheable cacheable)
            return await next(cancellationToken);

        // Build the service-scoped key: "company-svc:v1:{CacheKey}"
        var scopedKey = _keyBuilder.Key(cacheable.CacheKey);

        _logger.LogDebug(
            "CachingBehavior intercepting {RequestType} with key: {CacheKey}",
            typeof(TRequest).Name, scopedKey);

        return await _cache.GetOrCreateAsync(
            scopedKey,
            async () => await next(cancellationToken),
            cacheable.CacheDuration,
            cancellationToken);
    }
}
