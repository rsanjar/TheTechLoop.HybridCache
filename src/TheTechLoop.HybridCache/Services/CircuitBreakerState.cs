namespace TheTechLoop.HybridCache.Services;

/// <summary>
/// Lock-free circuit breaker state machine for cache resilience.
/// Opens after consecutive failures; auto-closes after a cooldown period.
/// Uses atomic operations to avoid lock contention on hot cache paths.
/// </summary>
internal sealed class CircuitBreakerState
{
    private readonly int _breakDurationSeconds;
    private readonly int _failureThreshold;
    private int _consecutiveFailures;
    private long _lastFailureUtcTicks = DateTime.MinValue.Ticks;

    public CircuitBreakerState(int breakDurationSeconds, int failureThreshold)
    {
        _breakDurationSeconds = breakDurationSeconds;
        _failureThreshold = failureThreshold;
    }

    /// <summary>
    /// Returns true if the circuit is open (cache should be bypassed).
    /// Automatically transitions to half-open after the break duration.
    /// </summary>
    public bool IsOpen
    {
        get
        {
            var failures = Volatile.Read(ref _consecutiveFailures);
            if (failures < _failureThreshold)
                return false;

            // Auto-close after break duration (half-open → let one request through)
            var lastFailureTicks = Interlocked.Read(ref _lastFailureUtcTicks);
            if (DateTime.UtcNow.Ticks - lastFailureTicks > TimeSpan.FromSeconds(_breakDurationSeconds).Ticks)
            {
                // Reset via compare-exchange so only one thread transitions to half-open
                Interlocked.CompareExchange(ref _consecutiveFailures, 0, failures);
                return false;
            }

            return true;
        }
    }

    public void RecordFailure()
    {
        Interlocked.Increment(ref _consecutiveFailures);
        Interlocked.Exchange(ref _lastFailureUtcTicks, DateTime.UtcNow.Ticks);
    }

    public void RecordSuccess()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }
}
