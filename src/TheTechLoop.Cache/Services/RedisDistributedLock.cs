using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TheTechLoop.Cache.Abstractions;

namespace TheTechLoop.Cache.Services;

/// <summary>
/// Redis-based distributed lock using SET NX with auto-expiry.
/// Prevents cache stampede when multiple instances attempt to populate the same key.
/// </summary>
public class RedisDistributedLock : IDistributedLock
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisDistributedLock> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisDistributedLock"/> class.
    /// </summary>
    /// <param name="redis"></param>
    /// <param name="logger"></param>
    public RedisDistributedLock(
        IConnectionMultiplexer redis,
        ILogger<RedisDistributedLock> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable?> TryAcquireAsync(
        string key,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            // Include timestamp to prevent reuse across clock skew
            var lockValue = $"{Guid.NewGuid():N}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

            var acquired = await db.StringSetAsync(
                key,
                lockValue,
                expiry,
                When.NotExists);

            if (!acquired)
                return null;

            return new LockHandle(db, key, lockValue, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to acquire distributed lock for key: {Key}", key);
            return null;
        }
    }

    /// <summary>
    /// Disposable lock handle that releases the lock using a Lua script
    /// to ensure only the owner can release it (compare-and-delete).
    /// </summary>
    private sealed class LockHandle : IAsyncDisposable
    {
        private readonly IDatabase _db;
        private readonly string _key;
        private readonly string _value;
        private readonly ILogger _logger;

        // Lua script: only delete if the lock value matches (prevents releasing another owner's lock)
        private const string ReleaseLuaScript = """
            if redis.call('get', KEYS[1]) == ARGV[1] then
                return redis.call('del', KEYS[1])
            else
                return 0
            end
            """;

        public LockHandle(IDatabase db, string key, string value, ILogger logger)
        {
            _db = db;
            _key = key;
            _value = value;
            _logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _db.ScriptEvaluateAsync(
                    ReleaseLuaScript,
                    [(RedisKey)_key],
                    [(RedisValue)_value]);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release distributed lock for key: {Key}", _key);
            }
        }
    }
}

/// <summary>
/// No-op distributed lock for use when Redis IConnectionMultiplexer is not available.
/// Always "acquires" the lock (no stampede protection).
/// </summary>
public class NoOpDistributedLock : IDistributedLock
{
    /// <summary>
    /// Tries to acquire a distributed lock.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="expiry"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IAsyncDisposable?>(NoOpHandle.Instance);
    }

    private sealed class NoOpHandle : IAsyncDisposable
    {
        public static readonly NoOpHandle Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
