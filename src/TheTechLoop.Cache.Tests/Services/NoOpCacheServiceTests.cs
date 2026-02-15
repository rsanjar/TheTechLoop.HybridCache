using FluentAssertions;
using TheTechLoop.Cache.Services;

namespace TheTechLoop.Cache.Tests.Services;

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
    public async Task GetAsync_AlwaysReturnsDefault()
    {
        var result = await _sut.GetAsync<string>("any-key");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_CompletesWithoutError()
    {
        var act = () => _sut.SetAsync("key", "value", TimeSpan.FromMinutes(5));

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
}
