namespace TheTechLoop.Cache.Services;

/// <summary>
/// Thread-safe circuit breaker state machine for cache resilience.
/// Opens after consecutive failures; auto-closes after a cooldown period.
/// </summary>
internal sealed class CircuitBreakerState
{
    private readonly int _breakDurationSeconds;
    private readonly int _failureThreshold;
    private int _consecutiveFailures;
    private DateTime _lastFailureUtc = DateTime.MinValue;
    private readonly Lock _lock = new();

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
            lock (_lock)
            {
                if (_consecutiveFailures < _failureThreshold)
                    return false;

                // Auto-close after break duration (half-open → let one request through)
                if (DateTime.UtcNow - _lastFailureUtc > TimeSpan.FromSeconds(_breakDurationSeconds))
                {
                    _consecutiveFailures = 0;
                    return false;
                }

                return true;
            }
        }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            _lastFailureUtc = DateTime.UtcNow;
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
        }
    }
}
