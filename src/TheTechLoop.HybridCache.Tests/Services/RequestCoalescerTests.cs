using FluentAssertions;
using TheTechLoop.HybridCache.Services;

namespace TheTechLoop.HybridCache.Tests.Services;

public class RequestCoalescerTests
{
    [Fact]
    public async Task CoalesceAsync_SingleCaller_ExecutesFactory()
    {
        var sut = new RequestCoalescer();

        var result = await sut.CoalesceAsync("key", () => Task.FromResult("hello"));

        result.Should().Be("hello");
    }

    [Fact]
    public async Task CoalesceAsync_ConcurrentCallers_SameKey_FactoryCalledOnce()
    {
        var sut = new RequestCoalescer();
        var factoryCallCount = 0;
        var gate = new TaskCompletionSource();

        // Factory that blocks until we release the gate
        async Task<string> SlowFactory()
        {
            Interlocked.Increment(ref factoryCallCount);
            await gate.Task;
            return "shared-result";
        }

        // Fire 50 concurrent requests for the same key
        var tasks = Enumerable.Range(0, 50)
            .Select(_ => sut.CoalesceAsync("same-key", SlowFactory))
            .ToArray();

        // Allow a moment for all tasks to be registered
        await Task.Delay(50);

        // Factory should have been called exactly once
        factoryCallCount.Should().Be(1);

        // Release the factory
        gate.SetResult();

        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.Should().Be("shared-result"));
        factoryCallCount.Should().Be(1);
    }

    [Fact]
    public async Task CoalesceAsync_DifferentKeys_FactoriesRunIndependently()
    {
        var sut = new RequestCoalescer();
        var callCount = 0;

        var tasks = Enumerable.Range(0, 10)
            .Select(i => sut.CoalesceAsync($"key-{i}", async () =>
            {
                Interlocked.Increment(ref callCount);
                await Task.Yield();
                return $"value-{i}";
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        callCount.Should().Be(10);
        results.Should().BeEquivalentTo(
            Enumerable.Range(0, 10).Select(i => $"value-{i}"));
    }

    [Fact]
    public async Task CoalesceAsync_FactoryThrows_AllCallersGetException()
    {
        var sut = new RequestCoalescer();
        var gate = new TaskCompletionSource();

        async Task<string> FailingFactory()
        {
            await gate.Task;
            throw new InvalidOperationException("boom");
        }

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => sut.CoalesceAsync("fail-key", FailingFactory))
            .ToArray();

        await Task.Delay(20);

        gate.SetResult();

        foreach (var task in tasks)
        {
            var act = () => task;
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("boom");
        }
    }

    [Fact]
    public async Task CoalesceAsync_AfterFailure_NextCallerGetsNewFactory()
    {
        var sut = new RequestCoalescer();
        var callIndex = 0;

        // First call: factory throws
        var firstAct = () => sut.CoalesceAsync("retry-key", () =>
        {
            Interlocked.Increment(ref callIndex);
            return Task.FromException<string>(new InvalidOperationException("first"));
        });

        await firstAct.Should().ThrowAsync<InvalidOperationException>();

        // Second call: factory succeeds (should NOT get stale exception)
        var result = await sut.CoalesceAsync("retry-key", () =>
        {
            Interlocked.Increment(ref callIndex);
            return Task.FromResult("recovered");
        });

        result.Should().Be("recovered");
        callIndex.Should().Be(2, "each call should get its own factory execution");
    }

    [Fact]
    public async Task CoalesceAsync_AfterCompletion_NextCallerGetsNewFactory()
    {
        var sut = new RequestCoalescer();

        // First batch
        var first = await sut.CoalesceAsync("key", () => Task.FromResult("batch-1"));
        first.Should().Be("batch-1");

        // Second batch should invoke factory again (not cached)
        var second = await sut.CoalesceAsync("key", () => Task.FromResult("batch-2"));
        second.Should().Be("batch-2");
    }
}
