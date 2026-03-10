using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Configuration;
using TheTechLoop.HybridCache.Metrics;
using TheTechLoop.HybridCache.Services;

namespace TheTechLoop.HybridCache.Tests.Services;

public class MemoryOnlyCacheServiceTests
{
    private readonly IMemoryCache _cache;
    private readonly CacheConfig _config;
    private readonly CacheMetrics _metrics;
    private readonly MemoryOnlyCacheService _sut;

    public MemoryOnlyCacheServiceTests()
    {
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 });

        _config = new CacheConfig
        {
            UseMemoryOnly = true,
            Enabled = true,
            DefaultExpirationMinutes = 60,
            MemoryCache = new MemoryCacheConfig
            {
                Enabled = true,
                DefaultExpirationSeconds = 30,
                SizeLimit = 1024
            }
        };

        var meterFactoryMock = new Mock<IMeterFactory>();
        meterFactoryMock
            .Setup(f => f.Create(It.IsAny<MeterOptions>()))
            .Returns((MeterOptions o) => new Meter(o));

        _metrics = new CacheMetrics(meterFactoryMock.Object);

        _sut = new MemoryOnlyCacheService(
            _cache,
            Options.Create(_config),
            _metrics,
            NullLogger<MemoryOnlyCacheService>.Instance);
    }

    #region GetOrCreateAsync

    [Fact]
    public async Task GetOrCreateAsync_CacheHit_ReturnsCachedValue()
    {
        _cache.Set("key1", "cached-value", new MemoryCacheEntryOptions { Size = 1 });

        var result = await _sut.GetOrCreateAsync(
            "key1", () => Task.FromResult("factory-value"), TimeSpan.FromMinutes(5));

        result.Should().Be("cached-value");
    }

    [Fact]
    public async Task GetOrCreateAsync_CacheMiss_CallsFactoryAndCaches()
    {
        var factoryCalls = 0;

        var result = await _sut.GetOrCreateAsync(
            "key-miss",
            () => { factoryCalls++; return Task.FromResult("factory-result"); },
            TimeSpan.FromMinutes(5));

        result.Should().Be("factory-result");
        factoryCalls.Should().Be(1);

        // Second call should hit cache
        var result2 = await _sut.GetOrCreateAsync(
            "key-miss",
            () => { factoryCalls++; return Task.FromResult("should-not-call"); },
            TimeSpan.FromMinutes(5));

        result2.Should().Be("factory-result");
        factoryCalls.Should().Be(1);
    }

    #endregion

    #region GetAsync / SetAsync

    [Fact]
    public async Task GetAsync_KeyNotFound_ReturnsDefault()
    {
        var result = await _sut.GetAsync<string>("nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_ReturnsValue()
    {
        await _sut.SetAsync("key-set", "hello", TimeSpan.FromMinutes(1));

        var result = await _sut.GetAsync<string>("key-set");
        result.Should().Be("hello");
    }

    [Fact]
    public async Task SetAsync_WithOptions_AbsoluteExpiration_Stores()
    {
        var options = CacheEntryOptions.Absolute(TimeSpan.FromMinutes(10));

        await _sut.SetAsync("key-abs", 42, options);

        var result = await _sut.GetAsync<int>("key-abs");
        result.Should().Be(42);
    }

    [Fact]
    public async Task SetAsync_WithOptions_SlidingExpiration_Stores()
    {
        var options = CacheEntryOptions.Sliding(TimeSpan.FromMinutes(5));

        await _sut.SetAsync("key-slide", 99, options);

        var result = await _sut.GetAsync<int>("key-slide");
        result.Should().Be(99);
    }

    [Fact]
    public async Task SetAsync_NullValue_DoesNotStore()
    {
        await _sut.SetAsync<string?>("key-null", null);

        var result = await _sut.GetAsync<string>("key-null");
        result.Should().BeNull();
    }

    #endregion

    #region RemoveAsync

    [Fact]
    public async Task RemoveAsync_RemovesValue()
    {
        await _sut.SetAsync("key-remove", "value", TimeSpan.FromMinutes(1));
        await _sut.RemoveAsync("key-remove");

        var result = await _sut.GetAsync<string>("key-remove");
        result.Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_NonExistentKey_DoesNotThrow()
    {
        var act = () => _sut.RemoveAsync("no-such-key");

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region Batch operations

    [Fact]
    public async Task GetManyAsync_ReturnsAllFoundValues()
    {
        await _sut.SetAsync("batch:1", "a", TimeSpan.FromMinutes(5));
        await _sut.SetAsync("batch:2", "b", TimeSpan.FromMinutes(5));

        var result = await _sut.GetManyAsync<string>(["batch:1", "batch:2", "batch:3"]);

        result["batch:1"].Should().Be("a");
        result["batch:2"].Should().Be("b");
        result["batch:3"].Should().BeNull();
    }

    [Fact]
    public async Task SetManyAsync_ThenGetManyAsync_ReturnsAll()
    {
        var items = new Dictionary<string, int>
        {
            ["multi:1"] = 10,
            ["multi:2"] = 20,
            ["multi:3"] = 30
        };

        await _sut.SetManyAsync(items, TimeSpan.FromMinutes(5));

        var result = await _sut.GetManyAsync<int>(["multi:1", "multi:2", "multi:3"]);

        result["multi:1"].Should().Be(10);
        result["multi:2"].Should().Be(20);
        result["multi:3"].Should().Be(30);
    }

    [Fact]
    public async Task SetManyAsync_EmptyDictionary_DoesNotThrow()
    {
        var act = () => _sut.SetManyAsync(new Dictionary<string, string>());

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region Unsupported operations

    [Fact]
    public async Task RemoveByPrefixAsync_DoesNotThrow()
    {
        var act = () => _sut.RemoveByPrefixAsync("prefix:");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RefreshAsync_DoesNotThrow()
    {
        var act = () => _sut.RefreshAsync("any-key");

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region CacheConfig.UseMemoryOnly validation

    [Fact]
    public void CacheConfig_UseMemoryOnly_True_NoRedisRequired()
    {
        var config = new CacheConfig
        {
            UseMemoryOnly = true,
            Enabled = true,
            Configuration = "",    // empty — would normally fail Required
            InstanceName = ""      // empty — would normally fail Required
        };

        var context = new System.ComponentModel.DataAnnotations.ValidationContext(config);
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        var isValid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            config, context, results, validateAllProperties: true);

        isValid.Should().BeTrue("UseMemoryOnly=true should bypass Redis field requirements");
        results.Should().BeEmpty();
    }

    [Fact]
    public void CacheConfig_UseMemoryOnly_False_RequiresConfiguration()
    {
        var config = new CacheConfig
        {
            UseMemoryOnly = false,
            Enabled = true,
            Configuration = "",
            InstanceName = ""
        };

        var results = config.Validate(
            new System.ComponentModel.DataAnnotations.ValidationContext(config)).ToList();

        results.Should().HaveCount(2);
        results.Should().Contain(r => r.MemberNames.Contains("Configuration"));
        results.Should().Contain(r => r.MemberNames.Contains("InstanceName"));
    }

    #endregion
}
