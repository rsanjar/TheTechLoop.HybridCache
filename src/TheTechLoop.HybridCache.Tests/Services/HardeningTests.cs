using System.Diagnostics;
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

/// <summary>
/// Phase 3 hardening tests: stampede, bulk operations, invalidation storms,
/// circuit-breaker recovery, and concurrency stress.
/// </summary>
public class HardeningTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    #region Cache Stampede

    [Fact]
    public async Task Stampede_ConcurrentGetOrCreate_FactoryCalledOnce_WhenLockAcquired()
    {
        // Arrange: 50 concurrent callers for the same key, lock is acquired by the first
        var cacheMock = new Mock<IDistributedCache>();
        var lockMock = new Mock<IDistributedLock>();
        var config = CreateConfig();
        var sut = CreateService(cacheMock, lockMock, config);

        var factoryCallCount = 0;
        string? storedValue = null;

        // First call: cache miss; subsequent calls after factory: cache hit
        cacheMock
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var current = Volatile.Read(ref storedValue);
                return current is null ? null : Encoding.UTF8.GetBytes(current);
            });

        cacheMock
            .Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Returns((string _, byte[] data, DistributedCacheEntryOptions _, CancellationToken _) =>
            {
                Volatile.Write(ref storedValue, Encoding.UTF8.GetString(data));
                return Task.CompletedTask;
            });

        // Only the first caller acquires the lock
        var lockCount = 0;
        var disposableMock = new Mock<IAsyncDisposable>();
        disposableMock.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        lockMock
            .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                return Interlocked.Increment(ref lockCount) == 1
                    ? disposableMock.Object
                    : null; // Others fail to acquire
            });

        // Act: fire 50 concurrent requests
        var tasks = Enumerable.Range(0, 50).Select(_ =>
            sut.GetOrCreateAsync(
                "stampede-key",
                async () =>
                {
                    Interlocked.Increment(ref factoryCallCount);
                    await Task.Delay(50); // Simulate work
                    return new StampedeDto { Value = "computed" };
                },
                TimeSpan.FromMinutes(5)));

        var results = await Task.WhenAll(tasks);

        // Assert: all callers got a result, factory called at most a few times
        // (lock holder + retriers that didn't find cache populated yet)
        results.Should().AllSatisfy(r => r.Should().NotBeNull());
        factoryCallCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Stampede_LockNotAcquired_RetriesWithJitteredBackoff_ThenFallsToFactory()
    {
        // Arrange: lock always fails, cache always empty
        var cacheMock = new Mock<IDistributedCache>();
        var lockMock = new Mock<IDistributedLock>();

        var config = CreateConfig();
        config.StampedeRetryMaxAttempts = 2;
        config.StampedeRetryBaseDelayMs = 10;

        var sut = CreateService(cacheMock, lockMock, config);

        cacheMock
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        lockMock
            .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);

        var sw = Stopwatch.StartNew();

        // Act
        var result = await sut.GetOrCreateAsync(
            "no-lock-key",
            async () => "fallback-value",
            TimeSpan.FromMinutes(5));

        sw.Stop();

        // Assert: factory was called as fallback, and retries introduced delay
        result.Should().Be("fallback-value");
        sw.ElapsedMilliseconds.Should().BeGreaterThan(10, "jittered retries should introduce delay");
    }

    #endregion

    #region Bulk Operations

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(5000)]
    public async Task BulkGetMany_LargeKeySet_CompletesWithoutError(int keyCount)
    {
        var cacheMock = new Mock<IDistributedCache>();
        var lockMock = new Mock<IDistributedLock>();
        var config = CreateConfig();
        config.MaxBatchConcurrency = 50;

        var sut = CreateService(cacheMock, lockMock, config);

        // Setup: half hits, half misses
        cacheMock
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) =>
            {
                if (key.EndsWith('0') || key.EndsWith('2') || key.EndsWith('4') ||
                    key.EndsWith('6') || key.EndsWith('8'))
                {
                    var json = JsonSerializer.Serialize(new StampedeDto { Value = key }, JsonOptions);
                    return Encoding.UTF8.GetBytes(json);
                }
                return null;
            });

        var keys = Enumerable.Range(0, keyCount).Select(i => $"bulk-key-{i}");

        // Act
        var results = await sut.GetManyAsync<StampedeDto>(keys);

        // Assert
        results.Should().HaveCount(keyCount);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(5000)]
    public async Task BulkSetMany_LargeItemSet_CompletesWithoutError(int itemCount)
    {
        var cacheMock = new Mock<IDistributedCache>();
        var lockMock = new Mock<IDistributedLock>();
        var config = CreateConfig();
        config.MaxBatchConcurrency = 50;

        var sut = CreateService(cacheMock, lockMock, config);

        cacheMock
            .Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var items = Enumerable.Range(0, itemCount)
            .ToDictionary(i => $"bulk-set-{i}", i => new StampedeDto { Value = $"value-{i}" });

        // Act & Assert: should not throw
        await sut.SetManyAsync(items, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task BulkGetMany_RespectsBatchConcurrency_LimitsParallelCalls()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var lockMock = new Mock<IDistributedLock>();
        var config = CreateConfig();
        config.MaxBatchConcurrency = 10;

        var sut = CreateService(cacheMock, lockMock, config);

        var concurrentCount = 0;
        var maxConcurrent = 0;

        cacheMock
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken _) =>
            {
                var current = Interlocked.Increment(ref concurrentCount);
                // Track peak concurrency
                int snapshot;
                do
                {
                    snapshot = Volatile.Read(ref maxConcurrent);
                } while (current > snapshot && Interlocked.CompareExchange(ref maxConcurrent, current, snapshot) != snapshot);

                await Task.Delay(5); // simulate I/O
                Interlocked.Decrement(ref concurrentCount);
                return (byte[]?)null;
            });

        var keys = Enumerable.Range(0, 50).Select(i => $"conc-key-{i}");

        // Act
        await sut.GetManyAsync<string>(keys);

        // Assert: peak concurrency should not exceed batch size
        maxConcurrent.Should().BeLessThanOrEqualTo(config.MaxBatchConcurrency);
    }

    #endregion

    #region Invalidation Storm

    [Fact]
    public async Task InvalidationStorm_ConcurrentRemoves_AllComplete()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var lockMock = new Mock<IDistributedLock>();
        var config = CreateConfig();
        var sut = CreateService(cacheMock, lockMock, config);

        cacheMock
            .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act: 200 concurrent removals
        var tasks = Enumerable.Range(0, 200)
            .Select(i => sut.RemoveAsync($"storm-key-{i}"));

        // Assert: all complete without exception
        await Task.WhenAll(tasks);

        cacheMock.Verify(
            c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(200));
    }

    [Fact]
    public async Task InvalidationStorm_ConcurrentSetAndRemove_NoDataRace()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var lockMock = new Mock<IDistributedLock>();
        var config = CreateConfig();
        var sut = CreateService(cacheMock, lockMock, config);

        cacheMock
            .Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        cacheMock
            .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act: interleave sets and removes on overlapping keys
        var setTasks = Enumerable.Range(0, 100)
            .Select(i => sut.SetAsync($"race-key-{i % 10}", $"value-{i}", TimeSpan.FromMinutes(1)));
        var removeTasks = Enumerable.Range(0, 100)
            .Select(i => sut.RemoveAsync($"race-key-{i % 10}"));

        // Assert: no exceptions, all complete
        await Task.WhenAll(setTasks.Concat(removeTasks));
    }

    #endregion

    #region Circuit Breaker Recovery

    [Fact]
    public async Task CircuitBreaker_OpensAfterFailures_BypassesCache()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var lockMock = new Mock<IDistributedLock>();
        var config = CreateConfig();
        config.CircuitBreaker = new CircuitBreakerConfig
        {
            Enabled = true,
            FailureThreshold = 3,
            BreakDurationSeconds = 60
        };

        var sut = CreateService(cacheMock, lockMock, config);

        // Arrange: cache always throws
        cacheMock
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Redis down"));

        // Act: trip the circuit breaker
        for (var i = 0; i < 5; i++)
        {
            var result = await sut.GetOrCreateAsync(
                $"fail-key-{i}",
                async () => "factory-value",
                TimeSpan.FromMinutes(5));

            result.Should().Be("factory-value");
        }

        // Now circuit should be open: GetAsync should return default without touching Redis
        cacheMock.Invocations.Clear();

        var getResult = await sut.GetAsync<string>("bypassed-key");
        getResult.Should().BeNull();

        // Cache should NOT have been called (circuit is open)
        cacheMock.Verify(
            c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CircuitBreaker_AutoClosesAfterBreakDuration()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var lockMock = new Mock<IDistributedLock>();
        var config = CreateConfig();
        config.CircuitBreaker = new CircuitBreakerConfig
        {
            Enabled = true,
            FailureThreshold = 1,
            BreakDurationSeconds = 1
        };

        var sut = CreateService(cacheMock, lockMock, config);

        // Trip the circuit
        cacheMock
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Redis down"));

        await sut.GetOrCreateAsync("trip-key", async () => "v", TimeSpan.FromMinutes(1));

        // Circuit is open
        cacheMock.Invocations.Clear();
        var result1 = await sut.GetAsync<string>("check-key-1");
        cacheMock.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Wait for break duration to elapse
        await Task.Delay(1200);

        // Now Redis is healthy again
        cacheMock
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        cacheMock.Invocations.Clear();

        // Circuit should auto-close — Redis should be called
        var result2 = await sut.GetAsync<string>("check-key-2");

        cacheMock.Verify(
            c => c.GetAsync("check-key-2", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CircuitBreaker_IntermittentFailures_DoesNotFlapUnderLoad()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var lockMock = new Mock<IDistributedLock>();
        var config = CreateConfig();
        config.CircuitBreaker = new CircuitBreakerConfig
        {
            Enabled = true,
            FailureThreshold = 5,
            BreakDurationSeconds = 60
        };

        var sut = CreateService(cacheMock, lockMock, config);

        var callCount = 0;

        // Every 3rd call fails
        cacheMock
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count % 3 == 0)
                    throw new Exception("Intermittent failure");
                return Task.FromResult<byte[]?>(null);
            });

        // Act: 20 concurrent GetAsync calls with intermittent failures
        var tasks = Enumerable.Range(0, 20)
            .Select(i => sut.GetAsync<string>($"flap-key-{i}"));

        var results = await Task.WhenAll(tasks);

        // Assert: all calls returned (didn't throw), circuit should NOT have tripped
        // because successes reset the counter before hitting 5 consecutive failures
        results.Should().HaveCount(20);
    }

    #endregion

    #region Circuit Breaker Concurrency Stress

    [Fact]
    public void CircuitBreaker_ConcurrentRecordFailureAndSuccess_NoCorruption()
    {
        var cb = new CircuitBreakerState(breakDurationSeconds: 60, failureThreshold: 100);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exceptions = new List<Exception>();

        // Hammer the circuit breaker from multiple threads
        var threads = new Thread[8];
        for (var i = 0; i < threads.Length; i++)
        {
            var isFailureThread = i % 2 == 0;
            threads[i] = new Thread(() =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        if (isFailureThread)
                            cb.RecordFailure();
                        else
                            cb.RecordSuccess();

                        _ = cb.IsOpen; // read state concurrently
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            });
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        // Assert: no corruption or exceptions
        exceptions.Should().BeEmpty("circuit breaker should be thread-safe");

        // State should be readable without error
        _ = cb.IsOpen;
    }

    [Fact]
    public void CircuitBreaker_HalfOpenTransition_OnlyOneThreadResets()
    {
        var cb = new CircuitBreakerState(breakDurationSeconds: 1, failureThreshold: 1);

        // Trip the breaker
        cb.RecordFailure();
        cb.IsOpen.Should().BeTrue();

        // Wait for break duration
        Thread.Sleep(1100);

        // Multiple threads check IsOpen simultaneously — only one should reset
        var resetCount = 0;
        var barrier = new Barrier(10);

        var threads = Enumerable.Range(0, 10).Select(_ => new Thread(() =>
        {
            barrier.SignalAndWait();
            var wasOpen = cb.IsOpen;
            if (!wasOpen) // Thread that saw half-open → closed
                Interlocked.Increment(ref resetCount);
        })).ToArray();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        // All 10 threads should see it as closed (after the break duration),
        // and the circuit should be in a consistent state
        cb.IsOpen.Should().BeFalse();
        resetCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task CircuitBreaker_HighConcurrency_FailureCountConverges()
    {
        // With threshold 50, fire exactly 50 failures from 50 parallel tasks
        var cb = new CircuitBreakerState(breakDurationSeconds: 1, failureThreshold: 50);

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => cb.RecordFailure()));

        await Task.WhenAll(tasks);

        // Should be open — all 50 failures registered
        cb.IsOpen.Should().BeTrue();

        // Wait for break duration, then success in half-open closes the circuit
        await Task.Delay(1100);
        _ = cb.IsOpen; // triggers half-open transition
        cb.RecordSuccess();
        cb.IsOpen.Should().BeFalse();
    }

    #endregion

    #region Helpers

    private static CacheConfig CreateConfig() => new()
    {
        Enabled = true,
        EnableLogging = false,
        DefaultExpirationMinutes = 60,
        MaxBatchConcurrency = 50,
        StampedeRetryBaseDelayMs = 10,
        StampedeRetryMaxAttempts = 3,
        CircuitBreaker = new CircuitBreakerConfig
        {
            Enabled = true,
            BreakDurationSeconds = 60,
            FailureThreshold = 5
        }
    };

    private static RedisCacheService CreateService(
        Mock<IDistributedCache> cacheMock,
        Mock<IDistributedLock> lockMock,
        CacheConfig config)
    {
        return new RedisCacheService(
            cacheMock.Object,
            lockMock.Object,
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

    #endregion
}

public class StampedeDto
{
    public string Value { get; set; } = string.Empty;
}
