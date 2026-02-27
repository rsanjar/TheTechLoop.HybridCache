using System.Collections.Concurrent;

namespace TheTechLoop.HybridCache.Services;

/// <summary>
/// Deduplicates concurrent in-process requests for the same cache key.
/// When multiple threads request the same key simultaneously, only one
/// executes the factory; all others await the same <see cref="Task{T}"/>.
/// <para>
/// This eliminates thundering-herd polling against Redis from within a
/// single process instance — the distributed lock handles cross-instance
/// coordination, while this layer handles intra-instance coordination.
/// </para>
/// </summary>
internal sealed class RequestCoalescer
{
    // Stores in-flight Tasks keyed by cache key.  The value is typed as
    // object because GetOrCreateAsync<T> is generic — each key always
    // maps to the same T in practice, so the cast in CoalesceAsync is safe.
    private readonly ConcurrentDictionary<string, object> _flights = new();

    /// <summary>
    /// Returns a coalesced <see cref="Task{T}"/> for <paramref name="key"/>.
    /// If no request is in-flight for the key, <paramref name="factory"/> is
    /// started and its result is shared with all concurrent callers.
    /// If a request is already in-flight, the caller joins that task instead.
    /// </summary>
    public async Task<T> CoalesceAsync<T>(string key, Func<Task<T>> factory)
    {
        // Fast path: join an existing in-flight request
        if (_flights.TryGetValue(key, out var existing) && existing is Task<T> inflight)
            return await inflight;

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_flights.TryAdd(key, tcs.Task))
        {
            // Lost the race — another thread just added an entry; join it
            if (_flights.TryGetValue(key, out existing) && existing is Task<T> joined)
                return await joined;

            // Winner finished between our TryAdd and TryGetValue; run directly
            return await factory();
        }

        try
        {
            var result = await factory();
            tcs.SetResult(result);
            return result;
        }
        catch (OperationCanceledException ex)
        {
            tcs.TrySetCanceled(ex.CancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
            throw;
        }
        finally
        {
            _flights.TryRemove(key, out _);
        }
    }
}
