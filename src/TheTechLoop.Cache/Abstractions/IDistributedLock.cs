namespace TheTechLoop.Cache.Abstractions;

/// <summary>
/// Distributed lock contract for preventing cache stampede
/// and coordinating cache population across multiple service instances.
/// </summary>
public interface IDistributedLock
{
    /// <summary>
    /// Attempts to acquire a distributed lock on the given key.
    /// </summary>
    /// <param name="key">The resource key to lock</param>
    /// <param name="expiry">Auto-expiration for the lock (prevents deadlocks)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An IAsyncDisposable lock handle if acquired; null if the lock is held by another process</returns>
    Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan expiry, CancellationToken cancellationToken = default);
}
