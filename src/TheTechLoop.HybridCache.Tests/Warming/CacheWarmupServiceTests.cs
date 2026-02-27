using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Warming;

namespace TheTechLoop.HybridCache.Tests.Warming;

public class CacheWarmupServiceTests
{
    [Fact]
    public async Task ExecuteAsync_RunsAllStrategies()
    {
        var strategy1 = new Mock<ICacheWarmupStrategy>();
        var strategy2 = new Mock<ICacheWarmupStrategy>();

        strategy1.Setup(s => s.WarmupAsync(It.IsAny<ICacheService>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        strategy2.Setup(s => s.WarmupAsync(It.IsAny<ICacheService>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var services = BuildServiceProvider(
            [strategy1.Object, strategy2.Object]);

        var sut = new CacheWarmupService(services, NullLogger<CacheWarmupService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        // BackgroundService completes quickly since this is a one-time task
        await Task.Delay(100);

        strategy1.Verify(s => s.WarmupAsync(It.IsAny<ICacheService>(), It.IsAny<CancellationToken>()), Times.Once);
        strategy2.Verify(s => s.WarmupAsync(It.IsAny<ICacheService>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_FailingStrategy_ContinuesWithOthers()
    {
        var failingStrategy = new Mock<ICacheWarmupStrategy>();
        var successStrategy = new Mock<ICacheWarmupStrategy>();

        failingStrategy.Setup(s => s.WarmupAsync(It.IsAny<ICacheService>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Strategy failed"));
        successStrategy.Setup(s => s.WarmupAsync(It.IsAny<ICacheService>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var services = BuildServiceProvider(
            [failingStrategy.Object, successStrategy.Object]);

        var sut = new CacheWarmupService(services, NullLogger<CacheWarmupService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        // Second strategy should still run despite first failing
        successStrategy.Verify(s => s.WarmupAsync(It.IsAny<ICacheService>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NoStrategies_CompletesSuccessfully()
    {
        var services = BuildServiceProvider([]);

        var sut = new CacheWarmupService(services, NullLogger<CacheWarmupService>.Instance);

        var act = async () =>
        {
            await sut.StartAsync(CancellationToken.None);
            await Task.Delay(100);
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExecuteAsync_StrategiesReceiveCacheService()
    {
        ICacheService? capturedCache = null;
        var strategy = new Mock<ICacheWarmupStrategy>();
        strategy.Setup(s => s.WarmupAsync(It.IsAny<ICacheService>(), It.IsAny<CancellationToken>()))
            .Callback<ICacheService, CancellationToken>((cache, _) => capturedCache = cache)
            .Returns(Task.CompletedTask);

        var services = BuildServiceProvider([strategy.Object]);

        var sut = new CacheWarmupService(services, NullLogger<CacheWarmupService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        capturedCache.Should().NotBeNull();
    }

    [Fact]
    public async Task ReferenceDataWarmupStrategy_CompletesWithoutError()
    {
        var strategy = new ReferenceDataWarmupStrategy();
        var cacheMock = new Mock<ICacheService>();

        var act = () => strategy.WarmupAsync(cacheMock.Object, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private static IServiceProvider BuildServiceProvider(
        IReadOnlyList<ICacheWarmupStrategy> strategies)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<ICacheService>());

        foreach (var strategy in strategies)
            services.AddSingleton(strategy);

        return services.BuildServiceProvider();
    }
}
