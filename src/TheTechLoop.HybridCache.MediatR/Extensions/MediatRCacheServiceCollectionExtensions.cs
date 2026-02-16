using MediatR;
using Microsoft.Extensions.DependencyInjection;
using TheTechLoop.HybridCache.MediatR.Behaviors;

namespace TheTechLoop.HybridCache.MediatR.Extensions;

/// <summary>
/// DI registration extension methods for TheTechLoop.Cache.MediatR pipeline behaviors.
/// </summary>
public static class MediatRCacheServiceCollectionExtensions
{
    /// <summary>
    /// Registers MediatR pipeline behaviors for automatic caching and cache invalidation.
    /// <list type="bullet">
    ///   <item><see cref="CachingBehavior{TRequest,TResponse}"/> — auto-caches queries implementing <see cref="Abstractions.ICacheable"/></item>
    ///   <item><see cref="CacheInvalidationBehavior{TRequest,TResponse}"/> — auto-invalidates after commands implementing <see cref="Abstractions.ICacheInvalidatable"/></item>
    /// </list>
    /// <para>
    /// Call this AFTER <c>AddMediatR()</c> and <c>AddTheTechLoopCache()</c>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddTheTechLoopCacheBehaviors(this IServiceCollection services)
    {
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(CacheInvalidationBehavior<,>));
        return services;
    }
}
