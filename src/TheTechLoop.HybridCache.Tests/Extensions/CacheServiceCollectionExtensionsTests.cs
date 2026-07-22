using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Configuration;
using TheTechLoop.HybridCache.Extensions;
using TheTechLoop.HybridCache.Metrics;
using TheTechLoop.HybridCache.Services;
using TheTechLoop.HybridCache.Streams;

namespace TheTechLoop.HybridCache.Tests.Extensions;

public class CacheServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddTheTechLoopCache_HealthCheck_ReusesRegisteredConnectionMultiplexer()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TheTechLoopCache:Configuration"] =
                    "redis:6379,user=default,password=secret,defaultDatabase=15,ssl=false,abortConnect=false",
                ["TheTechLoopCache:InstanceName"] = "test:",
                ["TheTechLoopCache:ServiceName"] = "test-svc",
                ["TheTechLoopCache:Enabled"] = "true"
            })
            .Build();

        var connectionMultiplexer = new Mock<IConnectionMultiplexer>();

        services.AddLogging();
        services.AddTheTechLoopCache(configuration);
        services.RemoveAll<IConnectionMultiplexer>();
        services.AddSingleton(connectionMultiplexer.Object);

        using var provider = services.BuildServiceProvider();
        var healthCheckService = provider.GetRequiredService<HealthCheckService>();

        var report = await healthCheckService.CheckHealthAsync(
            registration => registration.Name == "redis-cache",
            TestContext.Current.CancellationToken);

        report.Status.Should().Be(HealthStatus.Healthy);
        connectionMultiplexer.Verify(
            instance => instance.GetEndPoints(It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public void AddTheTechLoopCacheInvalidation_WithStreams_RegistersMediatRPublisherInterfaces()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TheTechLoopCache:UseStreamsForInvalidation"] = "true"
            })
            .Build();

        services.AddTheTechLoopCacheInvalidation(configuration);

        services.Should().Contain(d => d.ServiceType == typeof(RedisCacheInvalidationStreamPublisher));
        services.Should().Contain(d => d.ServiceType == typeof(ICacheInvalidationStreamPublisher));
        services.Should().Contain(d => d.ServiceType == typeof(ICacheInvalidationPublisher));
        services.Should().Contain(d => d.ServiceType == typeof(ICacheTagInvalidationPublisher));
    }

    [Fact]
    public void AddTheTechLoopCacheInvalidation_WithPubSub_ResolvesSubscriberWithoutMemoryCache()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(useStreams: false);
        AddSubscriberDependencies(services, configuration);

        services.AddTheTechLoopCacheInvalidation(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        provider.GetService<IMemoryCache>().Should().BeNull();
        provider.GetServices<IHostedService>()
            .Should()
            .ContainSingle(service => service is CacheInvalidationSubscriber);
    }

    [Fact]
    public void AddTheTechLoopCacheInvalidation_WithStreams_ResolvesConsumerWithoutMemoryCache()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(useStreams: true);
        AddSubscriberDependencies(services, configuration);

        services.AddTheTechLoopCacheInvalidation(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        provider.GetService<IMemoryCache>().Should().BeNull();
        provider.GetServices<IHostedService>()
            .Should()
            .ContainSingle(service => service is CacheInvalidationStreamConsumer);
    }

    private static IConfiguration CreateConfiguration(bool useStreams)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TheTechLoopCache:UseStreamsForInvalidation"] = useStreams.ToString(),
                ["TheTechLoopCache:InvalidationChannel"] = "cache:invalidation",
                ["TheTechLoopCache:ServiceName"] = "test-svc",
                ["TheTechLoopCache:InstanceName"] = "test:"
            })
            .Build();
    }

    private static void AddSubscriberDependencies(IServiceCollection services, IConfiguration configuration)
    {
        var config = configuration.GetSection("TheTechLoopCache").Get<CacheConfig>() ?? new CacheConfig();

        services.AddLogging();
        services.AddSingleton(Mock.Of<IConnectionMultiplexer>());
        services.AddSingleton(Mock.Of<IDistributedCache>());
        services.AddSingleton<IOptions<CacheConfig>>(Options.Create(config));
        services.AddSingleton(CreateMetrics());
    }

    private static CacheMetrics CreateMetrics()
    {
        var meterFactoryMock = new Mock<IMeterFactory>();
        meterFactoryMock
            .Setup(f => f.Create(It.IsAny<MeterOptions>()))
            .Returns((MeterOptions options) => new Meter(options));
        return new CacheMetrics(meterFactoryMock.Object);
    }
}
