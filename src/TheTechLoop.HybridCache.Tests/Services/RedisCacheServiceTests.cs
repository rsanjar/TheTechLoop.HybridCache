using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Configuration;
using TheTechLoop.HybridCache.Metrics;
using TheTechLoop.HybridCache.Services;

namespace TheTechLoop.HybridCache.Tests.Services;

public class RedisCacheServiceTests
{
    private readonly Mock<IDistributedCache> _cacheMock;
    private readonly Mock<IDistributedLock> _lockMock;
    private readonly CacheConfig _config;
    private readonly CacheMetrics _metrics;
    private readonly RedisCacheService _sut;

    public RedisCacheServiceTests()
    {
        _cacheMock = new Mock<IDistributedCache>();
        _lockMock = new Mock<IDistributedLock>();
        _config = new CacheConfig
        {
            Enabled = true,
            EnableLogging = false,
            DefaultExpirationMinutes = 60,
            CircuitBreaker = new CircuitBreakerConfig
            {
                Enabled = true,
                BreakDurationSeconds = 60,
                FailureThreshold = 5
            }
        };

        _metrics = CreateMetrics();

        _sut = new RedisCacheService(
            _cacheMock.Object,
            _lockMock.Object,
            NullLogger<RedisCacheService>.Instance,
            Options.Create(_config),
            _metrics);
    }

    #region GetOrCreateAsync

    [Fact]
    public async Task GetOrCreateAsync_WhenCacheDisabled_CallsFactory()
    {
        var config = new CacheConfig { Enabled = false, CircuitBreaker = new CircuitBreakerConfig() };
        var sut = CreateService(config);
        var factoryCalled = false;

        var result = await sut.GetOrCreateAsync(
            "key",
            async () => { factoryCalled = true; return "value"; },
            TimeSpan.FromMinutes(5));

        result.Should().Be("value");
        factoryCalled.Should().BeTrue();
        _cacheMock.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetOrCreateAsync_OnCacheHit_ReturnsCachedValue()
    {
        var expected = new TestDto { Id = 1, Name = "Cached" };
        var json = JsonSerializer.Serialize(expected, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        SetupCacheGet("test-key", json);

        var factoryCalled = false;
        var result = await _sut.GetOrCreateAsync(
            "test-key",
            async () => { factoryCalled = true; return new TestDto { Id = 2, Name = "Fresh" }; },
            TimeSpan.FromMinutes(5));

        result.Should().NotBeNull();
        result.Id.Should().Be(1);
        result.Name.Should().Be("Cached");
        factoryCalled.Should().BeFalse();
    }

    [Fact]
    public async Task GetOrCreateAsync_OnCacheMiss_CallsFactoryAndCachesResult()
    {
        SetupCacheGet("miss-key", null);
        SetupLockAcquire();

        var result = await _sut.GetOrCreateAsync(
            "miss-key",
            async () => new TestDto { Id = 42, Name = "FromDB" },
            TimeSpan.FromMinutes(10));

        result.Should().NotBeNull();
        result.Id.Should().Be(42);

        _cacheMock.Verify(c => c.SetAsync(
            "miss-key",
            It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateAsync_OnCacheException_FallsBackToFactory()
    {
        _cacheMock
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Redis down"));

        var result = await _sut.GetOrCreateAsync(
            "error-key",
            async () => new TestDto { Id = 99, Name = "Fallback" },
            TimeSpan.FromMinutes(5));

        result.Should().NotBeNull();
        result.Id.Should().Be(99);
        result.Name.Should().Be("Fallback");
    }

    [Fact]
    public async Task GetOrCreateAsync_DoesNotCacheDefaultValues()
    {
        SetupCacheGet("null-key", null);
        SetupLockAcquire();

        var result = await _sut.GetOrCreateAsync<TestDto?>(
            "null-key",
            async () => null,
            TimeSpan.FromMinutes(5));

        result.Should().BeNull();

        _cacheMock.Verify(c => c.SetAsync(
            It.IsAny<string>(),
            It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region GetAsync

    [Fact]
    public async Task GetAsync_WhenCacheDisabled_ReturnsDefault()
    {
        var config = new CacheConfig { Enabled = false, CircuitBreaker = new CircuitBreakerConfig() };
        var sut = CreateService(config);

        var result = await sut.GetAsync<TestDto>("key");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_OnHit_ReturnsDeserializedValue()
    {
        var expected = new TestDto { Id = 1, Name = "Cached" };
        var json = JsonSerializer.Serialize(expected, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        SetupCacheGet("hit-key", json);

        var result = await _sut.GetAsync<TestDto>("hit-key");

        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_OnMiss_ReturnsDefault()
    {
        SetupCacheGet("miss-key", null);

        var result = await _sut.GetAsync<TestDto>("miss-key");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_OnException_ReturnsDefault()
    {
        _cacheMock
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Redis error"));

        var result = await _sut.GetAsync<TestDto>("error-key");

        result.Should().BeNull();
    }

    #endregion

    #region SetAsync

    [Fact]
    public async Task SetAsync_WhenDisabled_DoesNothing()
    {
        var config = new CacheConfig { Enabled = false, CircuitBreaker = new CircuitBreakerConfig() };
        var sut = CreateService(config);

        await sut.SetAsync("key", new TestDto { Id = 1 });

        _cacheMock.Verify(c => c.SetAsync(
            It.IsAny<string>(), It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetAsync_WithNullValue_DoesNothing()
    {
        await _sut.SetAsync<TestDto?>("key", null);

        _cacheMock.Verify(c => c.SetAsync(
            It.IsAny<string>(), It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetAsync_WithValue_StoresInCache()
    {
        await _sut.SetAsync("key", new TestDto { Id = 1, Name = "Test" }, TimeSpan.FromMinutes(10));

        _cacheMock.Verify(c => c.SetAsync(
            "key",
            It.IsAny<byte[]>(),
            It.Is<DistributedCacheEntryOptions>(o => o.AbsoluteExpirationRelativeToNow == TimeSpan.FromMinutes(10)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetAsync_WithoutExpiration_UsesDefaultFromConfig()
    {
        await _sut.SetAsync("key", new TestDto { Id = 1, Name = "Test" });

        _cacheMock.Verify(c => c.SetAsync(
            "key",
            It.IsAny<byte[]>(),
            It.Is<DistributedCacheEntryOptions>(o => o.AbsoluteExpirationRelativeToNow == TimeSpan.FromMinutes(60)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region RemoveAsync

    [Fact]
    public async Task RemoveAsync_WhenDisabled_DoesNothing()
    {
        var config = new CacheConfig { Enabled = false, CircuitBreaker = new CircuitBreakerConfig() };
        var sut = CreateService(config);

        await sut.RemoveAsync("key");

        _cacheMock.Verify(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveAsync_RemovesKeyFromCache()
    {
        await _sut.RemoveAsync("remove-key");

        _cacheMock.Verify(c => c.RemoveAsync("remove-key", It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region RefreshAsync

    [Fact]
    public async Task RefreshAsync_WhenDisabled_DoesNothing()
    {
        var config = new CacheConfig { Enabled = false, CircuitBreaker = new CircuitBreakerConfig() };
        var sut = CreateService(config);

        await sut.RefreshAsync("key");

        _cacheMock.Verify(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshAsync_RefreshesKey()
    {
        await _sut.RefreshAsync("refresh-key");

        _cacheMock.Verify(c => c.RefreshAsync("refresh-key", It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Helpers

    private RedisCacheService CreateService(CacheConfig config)
    {
        return new RedisCacheService(
            _cacheMock.Object,
            _lockMock.Object,
            NullLogger<RedisCacheService>.Instance,
            Options.Create(config),
            CreateMetrics());
    }

    private static CacheMetrics CreateMetrics()
    {
        var meterFactoryMock = new Mock<IMeterFactory>();
        meterFactoryMock
            .Setup(f => f.Create(It.IsAny<MeterOptions>()))
            .Returns((MeterOptions options) => new Meter(options));
        return new CacheMetrics(meterFactoryMock.Object);
    }

    private void SetupCacheGet(string key, string? value)
    {
        _cacheMock
            .Setup(c => c.GetAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(value is null ? null : Encoding.UTF8.GetBytes(value));
    }

    private void SetupLockAcquire()
    {
        var disposableMock = new Mock<IAsyncDisposable>();
        disposableMock.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _lockMock
            .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(disposableMock.Object);
    }

    #endregion
}

public class TestDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
