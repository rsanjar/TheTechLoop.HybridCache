using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TheTechLoop.Cache.Abstractions;

namespace TheTechLoop.Cache.Warming;

/// <summary>
/// Strategy for determining which data to pre-load into cache on startup.
/// </summary>
public interface ICacheWarmupStrategy
{
    /// <summary>
    /// Executes the warmup logic, populating cache with frequently accessed data.
    /// </summary>
    Task WarmupAsync(ICacheService cache, CancellationToken cancellationToken);
}

/// <summary>
/// Hosted service that pre-loads frequently accessed data into cache on application startup.
/// <para>
/// Prevents cold-start cache misses by warming the cache before accepting requests.
/// Ideal for static reference data (countries, states, categories).
/// </para>
/// </summary>
/// <example>
/// <code>
/// public class ReferenceDataWarmupStrategy : ICacheWarmupStrategy
/// {
///     public async Task WarmupAsync(ICacheService cache, CancellationToken ct)
///     {
///         var countries = await _countryRepo.GetAllAsync();
///         await cache.SetAsync("Countries", countries, TimeSpan.FromHours(24), ct);
///     }
/// }
/// </code>
/// </example>
public class CacheWarmupService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CacheWarmupService> _logger;

    public CacheWarmupService(
        IServiceProvider services,
        ILogger<CacheWarmupService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Cache warmup service starting");

            using var scope = _services.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<ICacheService>();
            var strategies = scope.ServiceProvider.GetServices<ICacheWarmupStrategy>();

            foreach (var strategy in strategies)
            {
                try
                {
                    var strategyName = strategy.GetType().Name;
                    _logger.LogInformation("Executing warmup strategy: {Strategy}", strategyName);

                    await strategy.WarmupAsync(cache, stoppingToken);

                    _logger.LogInformation("Warmup strategy {Strategy} completed", strategyName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Warmup strategy {Strategy} failed", strategy.GetType().Name);
                    // Continue with other strategies
                }
            }

            _logger.LogInformation("Cache warmup service completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache warmup service failed");
        }

        // Complete immediately - this is a one-time startup task
    }
}

/// <summary>
/// Example warmup strategy for static reference data.
/// </summary>
public class ReferenceDataWarmupStrategy : ICacheWarmupStrategy
{
    /// <summary>
    /// Warms up the cache with reference data.
    /// </summary>
    /// <param name="cache"></param>
    /// <param name="cancellationToken"></param>
    public async Task WarmupAsync(ICacheService cache, CancellationToken cancellationToken)
    {
        // Example: Pre-load countries, states, and other reference data
        // Implementation provided by consuming application

        await Task.CompletedTask;

        // Real implementation:
        // var countries = await _countryRepository.GetAllAsync(cancellationToken);
        // await cache.SetAsync("reference:countries", countries, TimeSpan.FromHours(24), cancellationToken);
        //
        // var states = await _stateRepository.GetAllAsync(cancellationToken);
        // await cache.SetAsync("reference:states", states, TimeSpan.FromHours(24), cancellationToken);
    }
}
