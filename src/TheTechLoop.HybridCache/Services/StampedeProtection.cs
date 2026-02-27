using System.Diagnostics;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Configuration;
using TheTechLoop.HybridCache.Metrics;

namespace TheTechLoop.HybridCache.Services;

/// <summary>
/// Shared helper for the lock-acquire → jittered-retry hot path
/// used by both <see cref="RedisCacheService"/> and
/// <see cref="MultiLevelCacheService"/> during cache population.
/// <para>
/// Consolidates lock timing, metric recording, circuit-breaker-aware
/// retry polling, and jitter calculation into a single reusable flow.
/// </para>
/// </summary>
internal static class StampedeProtection
{
    /// <summary>
    /// Acquires the distributed lock for <paramref name="key"/> and, if the lock
    /// is not obtained, retries by polling via <paramref name="pollCacheAsync"/>
    /// with jittered exponential backoff.
    /// </summary>
    /// <typeparam name="T">The cached value type.</typeparam>
    /// <param name="key">Cache key being populated.</param>
    /// <param name="lockProvider">Distributed lock provider.</param>
    /// <param name="config">Cache configuration (lock timeout, retry settings).</param>
    /// <param name="metrics">Metrics sink for lock-wait recording.</param>
    /// <param name="isCircuitOpen">Returns <c>true</c> when the circuit breaker is open.</param>
    /// <param name="pollCacheAsync">
    ///     Called on each retry attempt. Should re-check L1/L2 and return
    ///     <c>(true, value)</c> if the entry appeared, or <c>(false, default)</c>
    ///     to continue retrying.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    ///     A tuple of <c>(lockHandle, found, cachedValue)</c>.
    ///     <list type="bullet">
    ///       <item>If <c>found</c> is <c>true</c>, <c>cachedValue</c> was populated
    ///             by another instance during the retry window — skip the factory.</item>
    ///       <item>If <c>found</c> is <c>false</c>, the caller must invoke the factory.
    ///             <c>lockHandle</c> may be <c>null</c> if the lock was never acquired
    ///             (retries exhausted).</item>
    ///     </list>
    /// </returns>
    public static async Task<(IAsyncDisposable? LockHandle, bool Found, T? Value)>
        AcquireOrPollAsync<T>(
            string key,
            IDistributedLock lockProvider,
            CacheConfig config,
            CacheMetrics metrics,
            Func<bool> isCircuitOpen,
            Func<Task<(bool Found, T? Value)>> pollCacheAsync,
            CancellationToken cancellationToken)
    {
        // Acquire distributed lock with timing
        var lockSw = Stopwatch.StartNew();
        var lockHandle = await lockProvider.TryAcquireAsync(
            $"lock:{key}",
            TimeSpan.FromSeconds(config.LockTimeoutSeconds),
            cancellationToken);
        lockSw.Stop();
        metrics.RecordLockWait(lockSw.Elapsed.TotalMilliseconds, lockHandle is not null);

        if (lockHandle is not null)
            return (lockHandle, false, default);

        // Lock not acquired — poll with jittered backoff
        for (var attempt = 0; attempt < config.StampedeRetryMaxAttempts; attempt++)
        {
            if (isCircuitOpen())
                break;

            var jitteredDelay = GetJitteredDelay(config.StampedeRetryBaseDelayMs, attempt);

            try
            {
                await Task.Delay(jitteredDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var (found, value) = await pollCacheAsync();
            if (found)
                return (null, true, value);
        }

        return (null, false, default);
    }

    /// <summary>
    /// Returns a jittered delay for stampede-protection retries.
    /// Delay grows exponentially (baseMs × 2^attempt) with random jitter
    /// in [delay .. delay × 2) to avoid lock-step contention.
    /// </summary>
    internal static TimeSpan GetJitteredDelay(int baseMs, int attempt)
    {
        var delayMs = baseMs * (1 << attempt);
        var jitter = Random.Shared.Next(0, delayMs);
        return TimeSpan.FromMilliseconds(delayMs + jitter);
    }
}
