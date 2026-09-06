using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.Metrics;
using Ardalis.SmartEnum;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Moq;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Configuration;
using TheTechLoop.HybridCache.Extensions;
using TheTechLoop.HybridCache.Serialization;
using TheTechLoop.HybridCache.Services;

namespace TheTechLoop.HybridCache.Tests.Serialization;

public sealed class CacheSerializationTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task ConfiguredSerializer_CoversReadThroughDirectAndBatchPaths_WithoutFallingBack(int mode, bool custom)
    {
        using var provider = BuildProvider(mode, "topic:", custom);
        var cache = provider.GetRequiredService<ICacheServiceWithEntryOptions>();
        var payload = new Payload(7, Topic.General, [Topic.General]);
        var calls = 0;
        Task<Payload> Factory() { calls++; return Task.FromResult(payload); }
        var expiration = TimeSpan.FromMinutes(1);

        (await cache.GetOrCreateAsync("read", Factory, expiration)).Should().BeEquivalentTo(payload);
        (await cache.GetOrCreateAsync("read", Factory, expiration)).Should().BeEquivalentTo(payload);
        calls.Should().Be(1, "a real serialized cache hit must not silently fall back to its factory");
        (await cache.GetOrCreateAsync("options", Factory, CacheEntryOptions.Absolute(expiration))).Should().BeEquivalentTo(payload);
        (await cache.GetOrCreateAsync("options", Factory, CacheEntryOptions.Absolute(expiration))).Should().BeEquivalentTo(payload);
        calls.Should().Be(2);
        await cache.SetAsync("direct", payload, expiration);
        (await cache.GetAsync<Payload>("direct")).Should().BeEquivalentTo(payload);
        await cache.SetAsync("direct-options", payload, CacheEntryOptions.Absolute(expiration));
        (await cache.GetAsync<Payload>("direct-options")).Should().BeEquivalentTo(payload);
        await cache.SetManyAsync(new Dictionary<string, Payload> { ["batch-a"] = payload, ["batch-b"] = payload }, expiration);
        var batch = await cache.GetManyAsync<Payload>(["batch-a", "batch-b", "missing"]);
        batch["batch-a"].Should().BeEquivalentTo(payload);
        batch["batch-b"].Should().BeEquivalentTo(payload);
        batch["missing"].Should().BeNull();

        if (custom)
        {
            var serializer = provider.GetRequiredService<ICacheSerializer>().Should().BeOfType<CountingSerializer>().Subject;
            serializer.Reads.Should().BeGreaterThan(0);
            serializer.Writes.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void Defaults_PreserveExistingBytesAndStaticApi_WithoutChangingGlobalOptions()
    {
        var payload = new { PropertyName = "hello", State = DayOfWeek.Monday, Optional = (string?)null, Bytes = new byte[] { 1, 2 } };
        var serializer = new SystemTextJsonCacheSerializer();
        serializer.Serialize(payload).Should().Equal(CacheSerializer.Serialize(payload));
        Encoding.UTF8.GetString(serializer.Serialize(payload)).Should().Be("{\"propertyName\":\"hello\",\"state\":1,\"optional\":null,\"bytes\":\"AQI=\"}");
        serializer.Deserialize<byte[]>(serializer.Serialize(new byte[] { 1, 2 })).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Providers_AreIsolated_AndSupportConcurrentSmartEnumRoundTrips()
    {
        using var first = BuildProvider(0, "first:", false);
        using var second = BuildProvider(0, "second:", false);
        var a = first.GetRequiredService<ICacheSerializer>();
        var b = second.GetRequiredService<ICacheSerializer>();
        Encoding.UTF8.GetString(a.Serialize(Topic.General)).Should().Be("\"first:general\"");
        Encoding.UTF8.GetString(b.Serialize(Topic.General)).Should().Be("\"second:general\"");
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
        {
            a.Deserialize<Topic>(a.Serialize(Topic.General)).Should().BeSameAs(Topic.General);
            b.Deserialize<Topic>(b.Serialize(Topic.General)).Should().BeSameAs(Topic.General);
        })));
    }

    [Fact]
    public void Serializer_CopiesOptionsBeforeFirstUse_AndDoesNotFreezeCallerOptions()
    {
        var options = new CacheSerializationOptions();
        options.JsonSerializerOptions.Converters.Add(new TopicConverter("original:"));
        var serializer = new SystemTextJsonCacheSerializer(Options.Create(options));
        options.JsonSerializerOptions.Converters.Clear();
        options.JsonSerializerOptions.PropertyNamingPolicy = null;
        var bytes = serializer.Serialize(new Payload(1, Topic.General, []));
        Encoding.UTF8.GetString(bytes).Should().Contain("\"topic\":\"original:general\"");
        serializer.Deserialize<Payload>(bytes.AsSpan())!.Topic.Should().BeSameAs(Topic.General);
    }

    [Fact]
    public void DefaultInterfaceSliceReader_SupportsCustomSerializersAndRejectsMalformedJson()
    {
        ICacheSerializer serializer = new CountingSerializer(new SystemTextJsonCacheSerializer());
        serializer.Deserialize<int>(Encoding.UTF8.GetBytes("12").AsSpan()).Should().Be(12);
        var malformed = () => serializer.Deserialize<int>(Encoding.UTF8.GetBytes("broken"));
        malformed.Should().Throw<JsonException>();
    }

    private static ServiceProvider BuildProvider(int mode, string prefix, bool custom)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TheTechLoopCache:Configuration"] = "unused:6379",
            ["TheTechLoopCache:InstanceName"] = "serializer-test:",
            ["TheTechLoopCache:Enabled"] = "true",
            ["TheTechLoopCache:EnableTagging"] = "false",
            ["TheTechLoopCache:EnableCompression"] = (mode >= 2).ToString(),
            ["TheTechLoopCache:CompressionThresholdBytes"] = mode == 3 ? "1" : "10000",
            // Force multi-level reads through L2 serialization, never a typed L1 shortcut.
            ["TheTechLoopCache:MemoryCache:Enabled"] = "false"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        var meters = new Mock<IMeterFactory>();
        meters.Setup(factory => factory.Create(It.IsAny<MeterOptions>())).Returns((MeterOptions options) => new Meter(options));
        services.AddSingleton(meters.Object);
        services.Configure<CacheSerializationOptions>(options => options.JsonSerializerOptions.Converters.Add(new TopicConverter(prefix)));
        if (custom)
            services.AddSingleton<ICacheSerializer>(sp => new CountingSerializer(
                new SystemTextJsonCacheSerializer(sp.GetRequiredService<IOptions<CacheSerializationOptions>>())));
        services.AddTheTechLoopCache(configuration);
        if (mode == 1) services.AddTheTechLoopMultiLevelCache(configuration);
        // Exercise real cache-service serialization and DI without requiring Redis in unit tests.
        services.RemoveAll<IDistributedCache>();
        services.AddDistributedMemoryCache();
        services.RemoveAll<IDistributedLock>();
        services.AddSingleton<IDistributedLock, NoOpDistributedLock>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    public sealed class Topic : SmartEnum<Topic, int>
    {
        public static readonly Topic General = new("general", 1);
        private Topic(string name, int value) : base(name, value) { }
    }

    public sealed record Payload(int Id, Topic Topic, IReadOnlyList<Topic> Topics);

    private sealed class TopicConverter(string prefix) : JsonConverter<Topic>
    {
        public override Topic Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (value != prefix + Topic.General.Name) throw new JsonException("Unknown topic");
            return Topic.General;
        }
        public override void Write(Utf8JsonWriter writer, Topic value, JsonSerializerOptions options) => writer.WriteStringValue(prefix + value.Name);
    }

    private sealed class CountingSerializer(ICacheSerializer inner) : ICacheSerializer
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public byte[] Serialize<T>(T value) { Writes++; return inner.Serialize(value); }
        public T? Deserialize<T>(byte[] bytes) { Reads++; return inner.Deserialize<T>(bytes); }
    }
}
