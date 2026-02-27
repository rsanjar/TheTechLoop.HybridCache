namespace TheTechLoop.HybridCache.Services;

/// <summary>
/// Lock-free circuit breaker state machine for cache resilience.
/// <list type="bullet">
///   <item><b>Closed</b> — requests pass through to cache normally.</item>
///   <item><b>Open</b> — after <c>failureThreshold</c> consecutive failures,
///         all requests bypass cache for <c>breakDurationSeconds</c>.</item>
///   <item><b>Half-open</b> — after the break duration, probe requests are
///         allowed through. After <c>halfOpenSuccessThreshold</c> consecutive
///         successes the circuit closes; any failure reopens it immediately.</item>
/// </list>
/// Uses atomic operations to avoid lock contention on hot cache paths.
/// An optional <see cref="Action{String}"/> callback is invoked on every
/// state transition so callers can emit metrics / structured logs.
/// </summary>
internal sealed class CircuitBreakerState
{
    private readonly int _breakDurationSeconds;
    private readonly int _failureThreshold;
    private readonly int _halfOpenSuccessThreshold;
    private readonly Action<string>? _onTransition;
    private int _consecutiveFailures;
    private int _halfOpenSuccesses;
    private long _lastFailureUtcTicks = DateTime.MinValue.Ticks;

    // 0 = Closed, 1 = Open, 2 = HalfOpen
    private int _state;

    private const int StateClosed = 0;
    private const int StateOpen = 1;
    private const int StateHalfOpen = 2;

    public CircuitBreakerState(
        int breakDurationSeconds,
        int failureThreshold,
        int halfOpenSuccessThreshold = 1,
        Action<string>? onTransition = null)
    {
        _breakDurationSeconds = breakDurationSeconds;
        _failureThreshold = failureThreshold;
        _halfOpenSuccessThreshold = Math.Max(1, halfOpenSuccessThreshold);
        _onTransition = onTransition;
    }

    /// <summary>
    /// Returns true if the circuit is open (cache should be bypassed).
    /// Automatically transitions to half-open after the break duration.
    /// </summary>
    public bool IsOpen
    {
        get
        {
            var state = Volatile.Read(ref _state);

            if (state == StateClosed)
                return false;

            if (state == StateHalfOpen)
                return false; // allow probe requests through

            // State is Open — check if break duration has elapsed
            var lastFailureTicks = Interlocked.Read(ref _lastFailureUtcTicks);
            if (DateTime.UtcNow.Ticks - lastFailureTicks > TimeSpan.FromSeconds(_breakDurationSeconds).Ticks)
            {
                // Transition Open → HalfOpen (only one thread wins)
                if (Interlocked.CompareExchange(ref _state, StateHalfOpen, StateOpen) == StateOpen)
                {
                    Interlocked.Exchange(ref _halfOpenSuccesses, 0);
                    _onTransition?.Invoke("half_open");
                }

                return false;
            }

            return true;
        }
    }

    public void RecordFailure()
    {
        Interlocked.Increment(ref _consecutiveFailures);
        Interlocked.Exchange(ref _lastFailureUtcTicks, DateTime.UtcNow.Ticks);

        var failures = Volatile.Read(ref _consecutiveFailures);

        // If in half-open, any failure reopens immediately
        var prevState = Volatile.Read(ref _state);
        if (prevState == StateHalfOpen)
        {
            if (Interlocked.CompareExchange(ref _state, StateOpen, StateHalfOpen) == StateHalfOpen)
                _onTransition?.Invoke("opened");
        }
        else if (prevState == StateClosed && failures >= _failureThreshold)
        {
            if (Interlocked.CompareExchange(ref _state, StateOpen, StateClosed) == StateClosed)
                _onTransition?.Invoke("opened");
        }
    }

    public void RecordSuccess()
    {
        var prevState = Volatile.Read(ref _state);

        if (prevState == StateHalfOpen)
        {
            var successes = Interlocked.Increment(ref _halfOpenSuccesses);
            if (successes >= _halfOpenSuccessThreshold)
            {
                // Enough probes succeeded — close the circuit
                if (Interlocked.CompareExchange(ref _state, StateClosed, StateHalfOpen) == StateHalfOpen)
                {
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    _onTransition?.Invoke("closed");
                }
            }
        }
        else
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
    }
}
