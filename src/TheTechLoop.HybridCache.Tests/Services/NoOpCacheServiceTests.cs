using FluentAssertions;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Services;

namespace TheTechLoop.HybridCache.Tests.Services;

public class NoOpCacheServiceTests
{
    private readonly NoOpCacheService _sut = new();

    [Fact]
    public async Task GetOrCreateAsync_AlwaysCallsFactory()
    {
        var factoryCalled = false;

        var result = await _sut.GetOrCreateAsync(
            "any-key",
            async () => { factoryCalled = true; return "value"; },
            TimeSpan.FromMinutes(5));

        result.Should().Be("value");
        factoryCalled.Should().BeTrue();
    }

    [Fact]
    public async Task GetOrCreateAsync_WithEntryOptions_AlwaysCallsFactory()
    {
        var factoryCalled = false;

        var result = await _sut.GetOrCreateAsync(
            "any-key",
            async () => { factoryCalled = true; return "value"; },
            CacheEntryOptions.Absolute(TimeSpan.FromMinutes(5), "tag-a"));

        result.Should().Be("value");
        factoryCalled.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_AlwaysReturnsDefault()
    {
        var result = await _sut.GetAsync<string>("any-key");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ValueType_ReturnsDefault()
    {
        var result = await _sut.GetAsync<int>("any-key");

        result.Should().Be(0);
    }

    [Fact]
    public async Task SetAsync_CompletesWithoutError()
    {
        var act = () => _sut.SetAsync("key", "value", TimeSpan.FromMinutes(5));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SetAsync_WithCacheEntryOptions_CompletesWithoutError()
    {
        var options = CacheEntryOptions.Absolute(TimeSpan.FromMinutes(5), "tag1");

        var act = () => _sut.SetAsync("key", "value", options);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RemoveAsync_CompletesWithoutError()
    {
        var act = () => _sut.RemoveAsync("key");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RemoveByPrefixAsync_CompletesWithoutError()
    {
        var act = () => _sut.RemoveByPrefixAsync("prefix");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RefreshAsync_CompletesWithoutError()
    {
        var act = () => _sut.RefreshAsync("key");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetManyAsync_ReturnsDefaultForAllKeys()
    {
        var result = await _sut.GetManyAsync<string>(["k1", "k2", "k3"]);

        result.Should().HaveCount(3);
        result["k1"].Should().BeNull();
        result["k2"].Should().BeNull();
        result["k3"].Should().BeNull();
    }

    [Fact]
    public async Task SetManyAsync_CompletesWithoutError()
    {
        var items = new Dictionary<string, string>
        {
            ["k1"] = "v1",
            ["k2"] = "v2"
        };

        var act = () => _sut.SetManyAsync(items, TimeSpan.FromMinutes(5));

        await act.Should().NotThrowAsync();
    }
}
