using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Configuration;
using TheTechLoop.HybridCache.Metrics;
using TheTechLoop.HybridCache.Serialization;
using TheTechLoop.HybridCache.Services;

namespace TheTechLoop.HybridCache.Benchmarks;

/// <summary>
/// Synthetic cache benchmarks using in-memory fakes (no real Redis).
/// Measures the overhead of the caching layer itself: serialization,
/// lock acquisition, coalescing, L1/L2 lookup, and circuit-breaker logic.
/// <para>
/// Run with: <c>dotnet run -c Release -- --filter *</c>
/// </para>
/// </summary>
[MemoryDiagnoser]
[Config(typeof(Config))]
public class CacheBenchmarks
{
    private class Config : ManualConfig
    {
        public Config() => AddJob(Job.ShortRun);
    }

    private RedisCacheService _redisSvc = null!;
    private MultiLevelCacheService _multiLevelSvc = null!;
    private Mock<IDistributedCache> _cacheMock = null!;
    private string[] _keys = null!;

    // Payloads of varying sizes
    private SmallDto _smallPayload = null!;
    private LargeDto _largePayload = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cacheMock = new Mock<IDistributedCache>();
        var lockMock = new Mock<IDistributedLock>();
        var disposableMock = new Mock<IAsyncDisposable>();
        disposableMock.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var config = new CacheConfig
        {
            Enabled = true,
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
                SizeLimit = 10000
            }
        };

        var meterFactory = new TestMeterFactory();
        var metrics = new CacheMetrics(meterFactory);

        // Lock always acquired (no contention in benchmarks)
        lockMock.Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(disposableMock.Object);

        _redisSvc = new RedisCacheService(
            _cacheMock.Object, lockMock.Object,
            NullLogger<RedisCacheService>.Instance,
            Options.Create(config), metrics);

        var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = config.MemoryCache.SizeLimit });

        _multiLevelSvc = new MultiLevelCacheService(
            memoryCache, _cacheMock.Object, lockMock.Object,
            NullLogger<MultiLevelCacheService>.Instance,
            Options.Create(config), metrics);

        _keys = Enumerable.Range(0, 1000).Select(i => $"svc:v1:Entity:{i}").ToArray();

        _smallPayload = new SmallDto { Id = 1, Name = "Test" };
        _largePayload = new LargeDto
        {
            Id = 1,
            Name = "Test",
            Description = new string('A', 5000),
            Tags = Enumerable.Range(0, 100).Select(i => $"tag-{i}").ToList(),
            Metadata = Enumerable.Range(0, 50).ToDictionary(i => $"key-{i}", i => $"value-{i}")
        };
    }

    // ───────────────────────────────────────────────
    //  Scenario 1: Hot key contention
    //  All threads hit the same key — measures coalescer + L1 fast path.
    // ───────────────────────────────────────────────

    [Benchmark]
    public async Task<SmallDto> HotKey_L1Hit_MultiLevel()
    {
        const string key = "svc:v1:HotKey:1";
        return await _multiLevelSvc.GetOrCreateAsync(
            key,
            () => Task.FromResult(_smallPayload),
            TimeSpan.FromMinutes(5));
    }

    [Benchmark]
    public async Task<SmallDto> HotKey_L2Hit_Redis()
    {
        const string key = "svc:v1:HotKey:1";
        var serialized = CacheSerializer.Serialize(_smallPayload);

        _cacheMock.Setup(c => c.GetAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(serialized);

        return await _redisSvc.GetOrCreateAsync(
            key,
            () => Task.FromResult(_smallPayload),
            TimeSpan.FromMinutes(5));
    }

    // ───────────────────────────────────────────────
    //  Scenario 2: Mixed payload sizes
    //  Measures serialization/deserialization overhead across sizes.
    // ───────────────────────────────────────────────

    [Benchmark]
    public byte[] Serialize_SmallPayload()
        => CacheSerializer.Serialize(_smallPayload);

    [Benchmark]
    public byte[] Serialize_LargePayload()
        => CacheSerializer.Serialize(_largePayload);

    [Benchmark]
    public SmallDto? Deserialize_SmallPayload()
    {
        var bytes = CacheSerializer.Serialize(_smallPayload);
        return CacheSerializer.Deserialize<SmallDto>(bytes);
    }

    [Benchmark]
    public LargeDto? Deserialize_LargePayload()
    {
        var bytes = CacheSerializer.Serialize(_largePayload);
        return CacheSerializer.Deserialize<LargeDto>(bytes);
    }

    // ───────────────────────────────────────────────
    //  Scenario 3: High miss rates
    //  Every request is a cache miss — measures factory + write path.
    // ───────────────────────────────────────────────

    [Benchmark]
    public async Task<SmallDto> CacheMiss_Redis()
    {
        var key = _keys[Random.Shared.Next(_keys.Length)];

        _cacheMock.Setup(c => c.GetAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);
        _cacheMock.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return await _redisSvc.GetOrCreateAsync(
            key,
            () => Task.FromResult(_smallPayload),
            TimeSpan.FromMinutes(5));
    }

    [Benchmark]
    public async Task<SmallDto> CacheMiss_MultiLevel()
    {
        var key = _keys[Random.Shared.Next(_keys.Length)];

        _cacheMock.Setup(c => c.GetAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);
        _cacheMock.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return await _multiLevelSvc.GetOrCreateAsync(
            key,
            () => Task.FromResult(_smallPayload),
            TimeSpan.FromMinutes(5));
    }

    // ───────────────────────────────────────────────
    //  Scenario 4: Bulk operations (batch throughput)
    // ───────────────────────────────────────────────

    [Benchmark]
    public async Task<Dictionary<string, SmallDto?>> BulkGet_100Keys()
    {
        var batchKeys = _keys.Take(100);

        _cacheMock.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CacheSerializer.Serialize(_smallPayload));

        return await _redisSvc.GetManyAsync<SmallDto>(batchKeys);
    }

    [Benchmark]
    public async Task BulkSet_100Keys()
    {
        var items = _keys.Take(100).ToDictionary(k => k, _ => _smallPayload);

        _cacheMock.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _redisSvc.SetManyAsync(items, TimeSpan.FromMinutes(5));
    }

    // ───────────────────────────────────────────────
    //  DTOs
    // ───────────────────────────────────────────────

    public class SmallDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class LargeDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = [];
        public Dictionary<string, string> Metadata { get; set; } = [];
    }
}
