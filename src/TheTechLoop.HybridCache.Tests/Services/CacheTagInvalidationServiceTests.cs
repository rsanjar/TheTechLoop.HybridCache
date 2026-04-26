using System.Diagnostics.Metrics;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Configuration;
using TheTechLoop.HybridCache.Metrics;
using TheTechLoop.HybridCache.Services;
using TheTechLoop.HybridCache.Streams;
using TheTechLoop.HybridCache.Tagging;

namespace TheTechLoop.HybridCache.Tests.Services;

public class CacheTagInvalidationServiceTests
{
    [Fact]
    public async Task RemoveByTagAsync_RemovesLogicalKeysFromL1AndLetsTagServiceCleanL2AndMetadata()
    {
        var tagServiceMock = new Mock<ICacheTagService>();
        var memoryCacheMock = new Mock<IMemoryCache>();

        tagServiceMock
            .Setup(s => s.GetKeysByTagAsync("test-svc:v1:Entity", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["test-svc:v1:Entity:1", "test-svc:v1:Entity:2"]);

        var sut = new CacheTagInvalidationService(
            tagServiceMock.Object,
            NullLogger<CacheTagInvalidationService>.Instance,
            memoryCacheMock.Object);

        await sut.RemoveByTagAsync("test-svc:v1:Entity");

        memoryCacheMock.Verify(m => m.Remove("test-svc:v1:Entity:1"), Times.Once);
        memoryCacheMock.Verify(m => m.Remove("test-svc:v1:Entity:2"), Times.Once);

        tagServiceMock.Verify(s => s.RemoveByTagAsync("test-svc:v1:Entity", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CacheInvalidationSubscriber_TagMessage_DelegatesToTagInvalidationService()
    {
        var redisMock = new Mock<IConnectionMultiplexer>();
        var distributedCacheMock = new Mock<IDistributedCache>();
        var tagInvalidationMock = new Mock<ICacheTagInvalidationService>();

        var sut = new CacheInvalidationSubscriber(
            redisMock.Object,
            distributedCacheMock.Object,
            NullLogger<CacheInvalidationSubscriber>.Instance,
            Options.Create(new CacheConfig { InvalidationChannel = "cache:invalidation" }),
            CreateMetrics(),
            memoryCache: null,
            tagInvalidationService: tagInvalidationMock.Object);

        var method = typeof(CacheInvalidationSubscriber).GetMethod(
            "HandleInvalidationAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var task = (Task)method!.Invoke(sut, ["tag:test-svc:v1:Entity", CancellationToken.None])!;
        await task;

        tagInvalidationMock.Verify(s => s.RemoveByTagAsync(
            "test-svc:v1:Entity",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CacheInvalidationSubscriber_KeyMessage_CleansTagIndexes()
    {
        var redisMock = new Mock<IConnectionMultiplexer>();
        var distributedCacheMock = new Mock<IDistributedCache>();
        var tagServiceMock = new Mock<ICacheTagService>();

        var sut = new CacheInvalidationSubscriber(
            redisMock.Object,
            distributedCacheMock.Object,
            NullLogger<CacheInvalidationSubscriber>.Instance,
            Options.Create(new CacheConfig { InvalidationChannel = "cache:invalidation" }),
            CreateMetrics(),
            memoryCache: null,
            tagInvalidationService: null,
            tagService: tagServiceMock.Object);

        var method = typeof(CacheInvalidationSubscriber).GetMethod(
            "HandleInvalidationAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var task = (Task)method!.Invoke(sut, ["key:test-svc:v1:Entity:42", CancellationToken.None])!;
        await task;

        tagServiceMock.Verify(s => s.RemoveAsync(
            "test-svc:v1:Entity:42",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CacheInvalidationStreamConsumer_TagMessage_DelegatesToTagInvalidationService()
    {
        var redisMock = new Mock<IConnectionMultiplexer>();
        var distributedCacheMock = new Mock<IDistributedCache>();
        var tagInvalidationMock = new Mock<ICacheTagInvalidationService>();

        var sut = new CacheInvalidationStreamConsumer(
            redisMock.Object,
            distributedCacheMock.Object,
            NullLogger<CacheInvalidationStreamConsumer>.Instance,
            Options.Create(new CacheConfig { ServiceName = "test-svc" }),
            CreateMetrics(),
            memoryCache: null,
            tagInvalidationService: tagInvalidationMock.Object);

        var method = typeof(CacheInvalidationStreamConsumer).GetMethod(
            "ProcessInvalidationMessageAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var entry = new StreamEntry(
            "1-0",
            [
                new NameValueEntry("type", "tag"),
                new NameValueEntry("tag", "test-svc:v1:Entity")
            ]);

        var task = (Task)method!.Invoke(sut, [entry, CancellationToken.None])!;
        await task;

        tagInvalidationMock.Verify(s => s.RemoveByTagAsync(
            "test-svc:v1:Entity",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CacheInvalidationStreamConsumer_KeyMessage_CleansTagIndexes()
    {
        var redisMock = new Mock<IConnectionMultiplexer>();
        var distributedCacheMock = new Mock<IDistributedCache>();
        var tagServiceMock = new Mock<ICacheTagService>();

        var sut = new CacheInvalidationStreamConsumer(
            redisMock.Object,
            distributedCacheMock.Object,
            NullLogger<CacheInvalidationStreamConsumer>.Instance,
            Options.Create(new CacheConfig { ServiceName = "test-svc" }),
            CreateMetrics(),
            memoryCache: null,
            tagInvalidationService: null,
            tagService: tagServiceMock.Object);

        var method = typeof(CacheInvalidationStreamConsumer).GetMethod(
            "ProcessInvalidationMessageAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var entry = new StreamEntry(
            "1-0",
            [
                new NameValueEntry("type", "key"),
                new NameValueEntry("key", "test-svc:v1:Entity:42")
            ]);

        var task = (Task)method!.Invoke(sut, [entry, CancellationToken.None])!;
        await task;

        tagServiceMock.Verify(s => s.RemoveAsync(
            "test-svc:v1:Entity:42",
            It.IsAny<CancellationToken>()), Times.Once);
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
