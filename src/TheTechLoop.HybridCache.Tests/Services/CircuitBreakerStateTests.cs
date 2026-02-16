using FluentAssertions;
using TheTechLoop.HybridCache.Services;

namespace TheTechLoop.HybridCache.Tests.Services;

public class CircuitBreakerStateTests
{
    [Fact]
    public void IsOpen_InitialState_ReturnsFalse()
    {
        var cb = new CircuitBreakerState(breakDurationSeconds: 60, failureThreshold: 3);

        cb.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void IsOpen_BelowThreshold_ReturnsFalse()
    {
        var cb = new CircuitBreakerState(breakDurationSeconds: 60, failureThreshold: 3);

        cb.RecordFailure();
        cb.RecordFailure();

        cb.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void IsOpen_AtThreshold_ReturnsTrue()
    {
        var cb = new CircuitBreakerState(breakDurationSeconds: 60, failureThreshold: 3);

        cb.RecordFailure();
        cb.RecordFailure();
        cb.RecordFailure();

        cb.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void IsOpen_AboveThreshold_ReturnsTrue()
    {
        var cb = new CircuitBreakerState(breakDurationSeconds: 60, failureThreshold: 2);

        cb.RecordFailure();
        cb.RecordFailure();
        cb.RecordFailure();
        cb.RecordFailure();

        cb.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void RecordSuccess_ResetsFailureCount()
    {
        var cb = new CircuitBreakerState(breakDurationSeconds: 60, failureThreshold: 3);

        cb.RecordFailure();
        cb.RecordFailure();
        cb.RecordSuccess();

        cb.IsOpen.Should().BeFalse();

        // Need threshold again after reset
        cb.RecordFailure();
        cb.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void IsOpen_AfterBreakDuration_AutoCloses()
    {
        // Use 1 second break duration for testability
        var cb = new CircuitBreakerState(breakDurationSeconds: 1, failureThreshold: 1);

        cb.RecordFailure();
        cb.IsOpen.Should().BeTrue();

        // Wait for break duration to elapse
        Thread.Sleep(1100);

        cb.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void IsOpen_WithinBreakDuration_StaysOpen()
    {
        var cb = new CircuitBreakerState(breakDurationSeconds: 60, failureThreshold: 1);

        cb.RecordFailure();

        cb.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void RecordFailure_AfterSuccess_CountsFromZero()
    {
        var cb = new CircuitBreakerState(breakDurationSeconds: 60, failureThreshold: 3);

        cb.RecordFailure();
        cb.RecordFailure();
        cb.RecordSuccess();

        // Only 1 failure after reset, not enough to trip
        cb.RecordFailure();
        cb.IsOpen.Should().BeFalse();

        // 2 more needed
        cb.RecordFailure();
        cb.RecordFailure();
        cb.IsOpen.Should().BeTrue();
    }
}
