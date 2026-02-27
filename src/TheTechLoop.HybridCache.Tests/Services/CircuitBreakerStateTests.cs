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

        // Transitions to half-open, then single success closes
        cb.IsOpen.Should().BeFalse(); // half-open allows through
        cb.RecordSuccess();           // closes the circuit
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

    #region Half-open probe behavior

    [Fact]
    public void HalfOpen_RequiresMultipleSuccessesToClose()
    {
        var cb = new CircuitBreakerState(
            breakDurationSeconds: 1, failureThreshold: 1, halfOpenSuccessThreshold: 3);

        cb.RecordFailure();
        cb.IsOpen.Should().BeTrue();

        Thread.Sleep(1100);
        cb.IsOpen.Should().BeFalse(); // half-open

        cb.RecordSuccess(); // 1 of 3
        cb.RecordSuccess(); // 2 of 3
        // Still in half-open — not enough successes to fully close
        // Recording a failure here should reopen
        cb.RecordFailure();
        cb.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void HalfOpen_ClosesAfterEnoughSuccesses()
    {
        var cb = new CircuitBreakerState(
            breakDurationSeconds: 1, failureThreshold: 2, halfOpenSuccessThreshold: 2);

        cb.RecordFailure();
        cb.RecordFailure(); // trips at threshold 2
        Thread.Sleep(1100);
        cb.IsOpen.Should().BeFalse(); // half-open

        cb.RecordSuccess(); // 1 of 2
        cb.RecordSuccess(); // 2 of 2 — should close

        // Now fully closed — 1 failure is below threshold of 2
        cb.RecordFailure();
        cb.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void HalfOpen_FailureReopensImmediately()
    {
        var cb = new CircuitBreakerState(
            breakDurationSeconds: 1, failureThreshold: 1, halfOpenSuccessThreshold: 3);

        cb.RecordFailure();
        Thread.Sleep(1100);
        cb.IsOpen.Should().BeFalse(); // half-open

        cb.RecordSuccess(); // 1 of 3
        cb.RecordFailure(); // any failure reopens immediately

        cb.IsOpen.Should().BeTrue();
    }

    #endregion

    #region Transition callbacks

    [Fact]
    public void OnTransition_EmitsOpened_WhenThresholdReached()
    {
        var transitions = new List<string>();
        var cb = new CircuitBreakerState(
            breakDurationSeconds: 60, failureThreshold: 2,
            onTransition: transitions.Add);

        cb.RecordFailure();
        cb.RecordFailure();

        transitions.Should().ContainSingle().Which.Should().Be("opened");
    }

    [Fact]
    public void OnTransition_EmitsHalfOpen_AfterBreakDuration()
    {
        var transitions = new List<string>();
        var cb = new CircuitBreakerState(
            breakDurationSeconds: 1, failureThreshold: 1,
            onTransition: transitions.Add);

        cb.RecordFailure();
        transitions.Should().Contain("opened");

        Thread.Sleep(1100);
        _ = cb.IsOpen; // triggers half-open transition

        transitions.Should().Contain("half_open");
    }

    [Fact]
    public void OnTransition_EmitsClosed_AfterSuccessInHalfOpen()
    {
        var transitions = new List<string>();
        var cb = new CircuitBreakerState(
            breakDurationSeconds: 1, failureThreshold: 1,
            onTransition: transitions.Add);

        cb.RecordFailure();
        Thread.Sleep(1100);
        _ = cb.IsOpen; // half-open
        cb.RecordSuccess();

        transitions.Should().Contain("closed");
    }

    #endregion
}
