using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Configuration;
using TheTechLoop.HybridCache.Metrics;
using TheTechLoop.HybridCache.Services;

namespace TheTechLoop.HybridCache.Tests.Services;

public class MultiLevelCacheServiceTests
{
    private readonly Mock<IMemoryCache> _l1Mock;
    private readonly Mock<IDistributedCache> _l2Mock;
    private readonly Mock<IDistributedLock> _lockMock;
    private readonly CacheConfig _config;
    private readonly CacheMetrics _metrics;
    private readonly MultiLevelCacheService _sut;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MultiLevelCacheServiceTests()
    {
        _l1Mock = new Mock<IMemoryCache>();
        _l2Mock = new Mock<IDistributedCache>();
        _lockMock = new Mock<IDistributedLock>();
        _config = new CacheConfig
        {
            Enabled = true,
            EnableLogging = false,
            DefaultExpirationMinutes = 60,
            MaxBatchConcurrency = 50,
            StampedeRetryBaseDelayMs = 10,
            StampedeRetryMaxAttempts = 2,
            CircuitBreaker = new CircuitBreakerConfig
            {
                Enabled = true,
                BreakDurationSeconds = 60,
                FailureThreshold = 5
            },
            MemoryCache = new MemoryCacheConfig
            {
                Enabled = true,
                DefaultExpirationSeconds = 30,
                SizeLimit = 1024
            }
        };

        _metrics = CreateMetrics();

        _sut = new MultiLevelCacheService(
            _l1Mock.Object,
            _l2Mock.Object,
            _lockMock.Object,
            NullLogger<MultiLevelCacheService>.Instance,
            Options.Create(_config),
            _metrics);
    }

    #region GetOrCreateAsync

    [Fact]
    public async Task GetOrCreateAsync_WhenDisabled_CallsFactory()
    {
        var config = CreateDisabledConfig();
        var sut = CreateService(config);
        var factoryCalled = false;

        var result = await sut.GetOrCreateAsync(
            "key",
            async () => { factoryCalled = true; return "value"; },
            TimeSpan.FromMinutes(5));

        result.Should().Be("value");
        factoryCalled.Should().BeTrue();
    }

    [Fact]
    public async Task GetOrCreateAsync_L1Hit_ReturnsWithoutL2()
    {
        SetupL1Hit("l1-key", "l1-value");

        var factoryCalled = false;
        var result = await _sut.GetOrCreateAsync(
            "l1-key",
            async () => { factoryCalled = true; return "factory"; },
            TimeSpan.FromMinutes(5));

        result.Should().Be("l1-value");
        factoryCalled.Should().BeFalse();
        _l2Mock.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetOrCreateAsync_L2Hit_PromotesToL1()
    {
        SetupL1Miss<string>("l2-key");
        SetupL2Get("l2-key", "l2-value");
        object? capturedValue = null;

        _l1Mock
            .Setup(m => m.CreateEntry(It.IsAny<object>()))
            .Returns(() =>
            {
                var entry = new Mock<ICacheEntry>();
                entry.SetupAllProperties();
                entry.Setup(e => e.Dispose()).Callback(() => capturedValue = entry.Object.Value);
                return entry.Object;
            });

        var result = await _sut.GetOrCreateAsync(
            "l2-key",
            async () => "factory",
            TimeSpan.FromMinutes(5));

        result.Should().Be("l2-value");
        _l1Mock.Verify(m => m.CreateEntry(It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateAsync_BothMiss_CallsFactory()
    {
        SetupL1Miss<string>("miss-key");
        SetupL2GetNull("miss-key");
        SetupLockAcquire();
        SetupL1CreateEntry();

        var result = await _sut.GetOrCreateAsync(
            "miss-key",
            async () => "factory-value",
            TimeSpan.FromMinutes(5));

        result.Should().Be("factory-value");
    }

    [Fact]
    public async Task GetOrCreateAsync_L2Failure_FallsToFactory()
    {
        SetupL1Miss<string>("fail-key");
        SetupLockAcquire();
        SetupL1CreateEntry();

        _l2Mock
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Redis down"));

        var result = await _sut.GetOrCreateAsync(
            "fail-key",
            async () => "fallback",
            TimeSpan.FromMinutes(5));

        result.Should().Be("fallback");
    }

    [Fact]
    public async Task GetOrCreateAsync_LockNotAcquired_RetriesAndFallsToFactory()
    {
        SetupL1Miss<string>("retry-key");
        SetupL2GetNull("retry-key");
        SetupL1CreateEntry();

        _lockMock
            .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);

        var result = await _sut.GetOrCreateAsync(
            "retry-key",
            async () => "after-retry",
            TimeSpan.FromMinutes(5));

        result.Should().Be("after-retry");
    }

    #endregion

    #region GetAsync

    [Fact]
    public async Task GetAsync_WhenDisabled_ReturnsDefault()
    {
        var sut = CreateService(CreateDisabledConfig());

        var result = await sut.GetAsync<string>("key");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_L1Hit_ReturnsValue()
    {
        SetupL1Hit("l1-hit", "cached");

        var result = await _sut.GetAsync<string>("l1-hit");

        result.Should().Be("cached");
        _l2Mock.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAsync_L2Hit_PromotesAndReturns()
    {
        SetupL1Miss<string>("l2-hit");
        SetupL2Get("l2-hit", "from-redis");
        SetupL1CreateEntry();

        var result = await _sut.GetAsync<string>("l2-hit");

        result.Should().Be("from-redis");
        _l1Mock.Verify(m => m.CreateEntry(It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task GetAsync_L2Empty_ReturnsDefault()
    {
        SetupL1Miss<string>("empty-key");
        SetupL2GetNull("empty-key");

        var result = await _sut.GetAsync<string>("empty-key");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_L2Error_ReturnsDefault()
    {
        SetupL1Miss<string>("error-key");

        _l2Mock
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Redis error"));

        var result = await _sut.GetAsync<string>("error-key");

        result.Should().BeNull();
    }

    #endregion

    #region SetAsync

    [Fact]
    public async Task SetAsync_WhenDisabled_DoesNothing()
    {
        var sut = CreateService(CreateDisabledConfig());

        await sut.SetAsync("key", "value", TimeSpan.FromMinutes(5));

        _l1Mock.Verify(m => m.CreateEntry(It.IsAny<object>()), Times.Never);
        _l2Mock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetAsync_WithNullValue_DoesNothing()
    {
        await _sut.SetAsync<string?>("key", null);

        _l1Mock.Verify(m => m.CreateEntry(It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task SetAsync_WithValue_WritesToBothLevels()
    {
        SetupL1CreateEntry();

        await _sut.SetAsync("key", "value", TimeSpan.FromMinutes(10));

        _l1Mock.Verify(m => m.CreateEntry(It.IsAny<object>()), Times.Once);
        _l2Mock.Verify(c => c.SetAsync("key", It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetAsync_WithSlidingOptions_UsesSlidingExpirationOnL1()
    {
        var options = CacheEntryOptions.Sliding(TimeSpan.FromMinutes(5));
        var entryMock = new Mock<ICacheEntry>();
        entryMock.SetupAllProperties();
        entryMock.Setup(e => e.Dispose());

        _l1Mock.Setup(m => m.CreateEntry("slide-key")).Returns(entryMock.Object);

        await _sut.SetAsync("slide-key", "value", options);

        entryMock.VerifySet(e => e.SlidingExpiration = TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task SetAsync_WithAbsoluteOptions_UsesAbsoluteExpirationOnL1()
    {
        var options = CacheEntryOptions.Absolute(TimeSpan.FromMinutes(10));
        SetupL1CreateEntry();

        await _sut.SetAsync("abs-key", "value", options);

        // Absolute uses SetL1 which uses AbsoluteExpirationRelativeToNow
        _l1Mock.Verify(m => m.CreateEntry(It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task SetAsync_L2WriteFailure_DoesNotThrow()
    {
        SetupL1CreateEntry();

        _l2Mock
            .Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Redis write failed"));

        var act = () => _sut.SetAsync("key", "value", TimeSpan.FromMinutes(5));

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region RemoveAsync

    [Fact]
    public async Task RemoveAsync_WhenDisabled_DoesNothing()
    {
        var sut = CreateService(CreateDisabledConfig());

        await sut.RemoveAsync("key");

        _l1Mock.Verify(m => m.Remove(It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task RemoveAsync_RemovesFromBothLevels()
    {
        await _sut.RemoveAsync("remove-key");

        _l1Mock.Verify(m => m.Remove("remove-key"), Times.Once);
        _l2Mock.Verify(c => c.RemoveAsync("remove-key", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAsync_L2Error_DoesNotThrow()
    {
        _l2Mock
            .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Redis error"));

        var act = () => _sut.RemoveAsync("key");

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region RemoveByPrefixAsync

    [Fact]
    public async Task RemoveByPrefixAsync_CompletesWithWarning()
    {
        var act = () => _sut.RemoveByPrefixAsync("prefix:");

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region RefreshAsync

    [Fact]
    public async Task RefreshAsync_WhenDisabled_DoesNothing()
    {
        var sut = CreateService(CreateDisabledConfig());

        await sut.RefreshAsync("key");

        _l2Mock.Verify(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshAsync_RefreshesL2()
    {
        await _sut.RefreshAsync("refresh-key");

        _l2Mock.Verify(c => c.RefreshAsync("refresh-key", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshAsync_L2Error_DoesNotThrow()
    {
        _l2Mock
            .Setup(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Redis error"));

        var act = () => _sut.RefreshAsync("key");

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region GetManyAsync

    [Fact]
    public async Task GetManyAsync_WhenDisabled_ReturnsEmpty()
    {
        var sut = CreateService(CreateDisabledConfig());

        var result = await sut.GetManyAsync<string>(["k1", "k2"]);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetManyAsync_AllL1Hits_NoL2Call()
    {
        SetupL1Hit("k1", "v1");
        SetupL1Hit("k2", "v2");

        var result = await _sut.GetManyAsync<string>(["k1", "k2"]);

        result.Should().HaveCount(2);
        result["k1"].Should().Be("v1");
        result["k2"].Should().Be("v2");
        _l2Mock.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetManyAsync_MixedL1L2_FillsFromL2()
    {
        SetupL1Hit("k1", "from-l1");
        SetupL1Miss<string>("k2");
        SetupL2Get("k2", "from-l2");
        SetupL1CreateEntry();

        var result = await _sut.GetManyAsync<string>(["k1", "k2"]);

        result["k1"].Should().Be("from-l1");
        result["k2"].Should().Be("from-l2");
    }

    [Fact]
    public async Task GetManyAsync_AllMisses_ReturnsDefaults()
    {
        SetupL1Miss<string>("k1");
        SetupL1Miss<string>("k2");
        SetupL2GetNull("k1");
        SetupL2GetNull("k2");

        var result = await _sut.GetManyAsync<string>(["k1", "k2"]);

        result.Should().HaveCount(2);
        result["k1"].Should().BeNull();
        result["k2"].Should().BeNull();
    }

    [Fact]
    public async Task GetManyAsync_L2Error_ReturnsPartialResults()
    {
        SetupL1Hit("k1", "from-l1");
        SetupL1Miss<string>("k2");

        _l2Mock
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Redis error"));

        var result = await _sut.GetManyAsync<string>(["k1", "k2"]);

        result["k1"].Should().Be("from-l1");
        result["k2"].Should().BeNull();
    }

    #endregion

    #region SetManyAsync

    [Fact]
    public async Task SetManyAsync_WhenDisabled_DoesNothing()
    {
        var sut = CreateService(CreateDisabledConfig());
        var items = new Dictionary<string, string> { ["k1"] = "v1" };

        await sut.SetManyAsync(items);

        _l1Mock.Verify(m => m.CreateEntry(It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task SetManyAsync_EmptyDictionary_DoesNothing()
    {
        await _sut.SetManyAsync(new Dictionary<string, string>());

        _l2Mock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetManyAsync_WritesToBothLevels()
    {
        SetupL1CreateEntry();

        var items = new Dictionary<string, string>
        {
            ["k1"] = "v1",
            ["k2"] = "v2"
        };

        await _sut.SetManyAsync(items, TimeSpan.FromMinutes(10));

        _l1Mock.Verify(m => m.CreateEntry(It.IsAny<object>()), Times.Exactly(2));
        _l2Mock.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SetManyAsync_L2Error_DoesNotThrow()
    {
        SetupL1CreateEntry();

        _l2Mock
            .Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Redis write failed"));

        var items = new Dictionary<string, string> { ["k1"] = "v1" };

        var act = () => _sut.SetManyAsync(items);

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region Helpers

    private MultiLevelCacheService CreateService(CacheConfig config)
    {
        return new MultiLevelCacheService(
            _l1Mock.Object,
            _l2Mock.Object,
            _lockMock.Object,
            NullLogger<MultiLevelCacheService>.Instance,
            Options.Create(config),
            CreateMetrics());
    }

    private static CacheConfig CreateDisabledConfig() => new()
    {
        Enabled = false,
        CircuitBreaker = new CircuitBreakerConfig(),
        MemoryCache = new MemoryCacheConfig()
    };

    private static CacheMetrics CreateMetrics()
    {
        var meterFactoryMock = new Mock<IMeterFactory>();
        meterFactoryMock
            .Setup(f => f.Create(It.IsAny<MeterOptions>()))
            .Returns((MeterOptions options) => new Meter(options));
        return new CacheMetrics(meterFactoryMock.Object);
    }

    private void SetupL1Hit<T>(string key, T value)
    {
        object outVal = value!;
        _l1Mock
            .Setup(m => m.TryGetValue(key, out outVal))
            .Returns(true);
    }

    private void SetupL1Miss<T>(string key)
    {
        object? outVal = null;
        _l1Mock
            .Setup(m => m.TryGetValue(key, out outVal))
            .Returns(false);
    }

    private void SetupL2Get<T>(string key, T? value) where T : class
    {
        if (value is null)
        {
            _l2Mock
                .Setup(c => c.GetAsync(key, It.IsAny<CancellationToken>()))
                .ReturnsAsync((byte[]?)null);
        }
        else
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            _l2Mock
                .Setup(c => c.GetAsync(key, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Encoding.UTF8.GetBytes(json));
        }
    }

    private void SetupL2GetNull(string key)
    {
        _l2Mock
            .Setup(c => c.GetAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);
    }

    private void SetupLockAcquire()
    {
        var disposableMock = new Mock<IAsyncDisposable>();
        disposableMock.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _lockMock
            .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(disposableMock.Object);
    }

    private void SetupL1CreateEntry()
    {
        _l1Mock
            .Setup(m => m.CreateEntry(It.IsAny<object>()))
            .Returns(() =>
            {
                var entry = new Mock<ICacheEntry>();
                entry.SetupAllProperties();
                entry.Setup(e => e.Dispose());
                return entry.Object;
            });
    }

    #endregion
}
