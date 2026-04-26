using FluentAssertions;
using Moq;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Compression;

namespace TheTechLoop.HybridCache.Tests.Compression;

public class CompressedCacheServiceTests
{
    private readonly Mock<ICacheService> _innerMock;
    private readonly Mock<ICacheServiceWithEntryOptions> _entryOptionsInnerMock;

    public CompressedCacheServiceTests()
    {
        _innerMock = new Mock<ICacheService>();
        _entryOptionsInnerMock = new Mock<ICacheServiceWithEntryOptions>();
    }

    #region SetAsync + GetAsync round-trip

    [Fact]
    public async Task SetAsync_SmallValue_StoresWithRawHeader()
    {
        byte[]? capturedValue = null;

        _innerMock
            .Setup(s => s.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], TimeSpan?, CancellationToken>((_, v, _, _) => capturedValue = v)
            .Returns(Task.CompletedTask);

        var sut = new CompressedCacheService(_innerMock.Object, compressionThresholdBytes: 1024);

        await sut.SetAsync("key", "small", TimeSpan.FromMinutes(5));

        capturedValue.Should().NotBeNull();
        capturedValue![0].Should().Be(0x00, "small values should have raw header (0x00)");
    }

    [Fact]
    public async Task SetAsync_LargeValue_StoresWithGzipHeader()
    {
        byte[]? capturedValue = null;

        _innerMock
            .Setup(s => s.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], TimeSpan?, CancellationToken>((_, v, _, _) => capturedValue = v)
            .Returns(Task.CompletedTask);

        var sut = new CompressedCacheService(_innerMock.Object, compressionThresholdBytes: 10);

        await sut.SetAsync("key", new string('A', 200), TimeSpan.FromMinutes(5));

        capturedValue.Should().NotBeNull();
        capturedValue![0].Should().Be(0x01, "large values should have gzip header (0x01)");
    }

    [Fact]
    public async Task SetAndGet_SmallValue_RoundTrips()
    {
        byte[]? stored = null;

        _innerMock
            .Setup(s => s.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], TimeSpan?, CancellationToken>((_, v, _, _) => stored = v)
            .Returns(Task.CompletedTask);

        _innerMock
            .Setup(s => s.GetAsync<byte[]>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => stored);

        var sut = new CompressedCacheService(_innerMock.Object, compressionThresholdBytes: 1024);

        await sut.SetAsync("key", "hello world", TimeSpan.FromMinutes(5));
        var result = await sut.GetAsync<string>("key");

        result.Should().Be("hello world");
    }

    [Fact]
    public async Task SetAndGet_LargeValue_CompressesAndDecompresses()
    {
        byte[]? stored = null;
        var original = new string('Z', 5000);

        _innerMock
            .Setup(s => s.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], TimeSpan?, CancellationToken>((_, v, _, _) => stored = v)
            .Returns(Task.CompletedTask);

        _innerMock
            .Setup(s => s.GetAsync<byte[]>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => stored);

        var sut = new CompressedCacheService(_innerMock.Object, compressionThresholdBytes: 100);

        await sut.SetAsync("key", original, TimeSpan.FromMinutes(5));

        stored.Should().NotBeNull();
        stored![0].Should().Be(0x01, "large values should be gzip-compressed");
        stored.Length.Should().BeLessThan(original.Length, "compressed data should be smaller than original");

        var result = await sut.GetAsync<string>("key");
        result.Should().Be(original);
    }

    #endregion

    #region SetAsync with CacheEntryOptions

    [Fact]
    public async Task SetAsync_WithOptions_NullValue_DoesNothing()
    {
        var sut = new CompressedCacheService(_innerMock.Object);

        await sut.SetAsync<string?>("key", null, CacheEntryOptions.Absolute(TimeSpan.FromMinutes(5)));

        _innerMock.Verify(s => s.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
            It.IsAny<CacheEntryOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetAsync_WithOptions_DelegatesCompressedToInner()
    {
        var options = CacheEntryOptions.Absolute(TimeSpan.FromMinutes(10), "tag1");

        _innerMock
            .Setup(s => s.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<CacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new CompressedCacheService(_innerMock.Object);

        await sut.SetAsync("key", "value", options);

        _innerMock.Verify(s => s.SetAsync("key", It.IsAny<byte[]>(), options,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetAsync

    [Fact]
    public async Task GetAsync_NullFromInner_ReturnsDefault()
    {
        _innerMock
            .Setup(s => s.GetAsync<byte[]>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        var sut = new CompressedCacheService(_innerMock.Object);

        var result = await sut.GetAsync<string>("key");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_EmptyFromInner_ReturnsDefault()
    {
        _innerMock
            .Setup(s => s.GetAsync<byte[]>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<byte>());

        var sut = new CompressedCacheService(_innerMock.Object);

        var result = await sut.GetAsync<string>("key");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_SingleByteFromInner_ReturnsDefault()
    {
        _innerMock
            .Setup(s => s.GetAsync<byte[]>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 0x00 });

        var sut = new CompressedCacheService(_innerMock.Object);

        var result = await sut.GetAsync<string>("key");

        result.Should().BeNull();
    }

    #endregion

    #region GetOrCreateAsync

    [Fact]
    public async Task GetOrCreateAsync_RoundTrips()
    {
        byte[]? stored = null;

        _innerMock
            .Setup(s => s.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Func<Task<byte[]>>>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, Func<Task<byte[]>> factory, TimeSpan _, CancellationToken _) =>
            {
                stored ??= await factory();
                return stored;
            });

        var sut = new CompressedCacheService(_innerMock.Object, compressionThresholdBytes: 10);

        var result = await sut.GetOrCreateAsync(
            "key",
            async () => new string('X', 200),
            TimeSpan.FromMinutes(5));

        result.Should().Be(new string('X', 200));
    }

    [Fact]
    public async Task GetOrCreateAsync_WithEntryOptions_DelegatesCompressedFactoryToInner()
    {
        byte[]? stored = null;
        var options = CacheEntryOptions.Absolute(TimeSpan.FromMinutes(5), "tag-a");

        _entryOptionsInnerMock
            .Setup(s => s.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Func<Task<byte[]>>>(),
                It.IsAny<CacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, Func<Task<byte[]>> factory, CacheEntryOptions _, CancellationToken _) =>
            {
                stored ??= await factory();
                return stored;
            });

        var sut = new CompressedCacheService(_entryOptionsInnerMock.Object, compressionThresholdBytes: 10);

        var result = await sut.GetOrCreateAsync(
            "key",
            async () => new string('X', 200),
            options);

        result.Should().Be(new string('X', 200));
        _entryOptionsInnerMock.Verify(s => s.GetOrCreateAsync(
            "key",
            It.IsAny<Func<Task<byte[]>>>(),
            options,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetManyAsync / SetManyAsync

    [Fact]
    public async Task SetManyAndGetMany_RoundTrips()
    {
        var store = new Dictionary<string, byte[]?>();

        _innerMock
            .Setup(s => s.SetManyAsync(It.IsAny<Dictionary<string, byte[]>>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<Dictionary<string, byte[]>, TimeSpan?, CancellationToken>((items, _, _) =>
            {
                foreach (var kvp in items) store[kvp.Key] = kvp.Value;
            })
            .Returns(Task.CompletedTask);

        _innerMock
            .Setup(s => s.GetManyAsync<byte[]>(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> keys, CancellationToken _) =>
                keys.ToDictionary(k => k, k => store.GetValueOrDefault(k)));

        var sut = new CompressedCacheService(_innerMock.Object, compressionThresholdBytes: 10);

        var items = new Dictionary<string, string>
        {
            ["small"] = "hi",
            ["large"] = new string('B', 500)
        };

        await sut.SetManyAsync(items, TimeSpan.FromMinutes(5));
        var results = await sut.GetManyAsync<string>(["small", "large"]);

        results["small"].Should().Be("hi");
        results["large"].Should().Be(new string('B', 500));
    }

    [Fact]
    public async Task GetManyAsync_WithNullValues_ReturnsDefaults()
    {
        _innerMock
            .Setup(s => s.GetManyAsync<byte[]>(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, byte[]?> { ["k1"] = null, ["k2"] = Array.Empty<byte>() });

        var sut = new CompressedCacheService(_innerMock.Object);

        var results = await sut.GetManyAsync<string>(["k1", "k2"]);

        results["k1"].Should().BeNull();
        results["k2"].Should().BeNull();
    }

    #endregion

    #region Delegation methods

    [Fact]
    public async Task RemoveAsync_DelegatesToInner()
    {
        var sut = new CompressedCacheService(_innerMock.Object);

        await sut.RemoveAsync("key");

        _innerMock.Verify(s => s.RemoveAsync("key", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveByPrefixAsync_DelegatesToInner()
    {
        var sut = new CompressedCacheService(_innerMock.Object);

        await sut.RemoveByPrefixAsync("prefix:");

        _innerMock.Verify(s => s.RemoveByPrefixAsync("prefix:", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshAsync_DelegatesToInner()
    {
        var sut = new CompressedCacheService(_innerMock.Object);

        await sut.RefreshAsync("key");

        _innerMock.Verify(s => s.RefreshAsync("key", It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion
}

