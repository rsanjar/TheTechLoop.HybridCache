using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TheTechLoop.HybridCache.Configuration;

namespace TheTechLoop.HybridCache.Tagging;

/// <summary>
/// Service for managing cache entry tags using Redis Sets.
/// Enables group invalidation by tag.
/// <para>
/// Example: Tag all user-related keys with "User" tag,
/// then invalidate all user data at once via RemoveByTagAsync("User").
/// </para>
/// </summary>
public interface ICacheTagService
{
    /// <summary>
    /// Associates a cache key with one or more tags.
    /// </summary>
    Task AddTagsAsync(string key, IEnumerable<string> tags, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a cache key and its tag associations.
    /// </summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all cache keys associated with a tag.
    /// </summary>
    Task<IReadOnlyList<string>> GetKeysByTagAsync(string tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all cache keys associated with a tag.
    /// </summary>
    Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default);
}

/// <summary>
/// Redis-based implementation of cache tagging.
/// Uses forward indices (tag → keys) for group invalidation and
/// reverse indices (key → tags) for efficient single-key tag removal.
/// <para>
/// Index keys have a configurable TTL (<see cref="CacheConfig.TagIndexTtlMinutes"/>)
/// to prevent unbounded memory growth when cached entries expire naturally.
/// <c>RemoveByTagAsync</c> uses a server-side Lua script for atomicity and
/// lazy-cleans stale members (keys that have already expired in Redis).
/// </para>
/// </summary>
public class RedisCacheTagService : ICacheTagService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisCacheTagService> _logger;
    private readonly CacheConfig _config;

    private const string TagPrefix = "tag:";
    private const string ReverseIndexPrefix = "key:tags:";

    // Lua script: atomically read tag members, delete data + reverse indices,
    // then delete the tag set.  Returns the count of deleted data keys.
    // Stale members (already expired) are silently skipped by redis.unlink.
    private const string RemoveByTagLuaScript = """
        local tagKey = KEYS[1]
        local reversePrefix = ARGV[1]
        local members = redis.call('SMEMBERS', tagKey)
        if #members == 0 then return 0 end
        for i = 1, #members do
            redis.call('UNLINK', reversePrefix .. members[i])
        end
        redis.call('UNLINK', tagKey, unpack(members))
        return #members
        """;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisCacheTagService"/> class.
    /// </summary>
    /// <param name="redis"></param>
    /// <param name="logger"></param>
    /// <param name="config"></param>
    public RedisCacheTagService(
        IConnectionMultiplexer redis,
        ILogger<RedisCacheTagService> logger,
        IOptions<CacheConfig> config)
    {
        _redis = redis;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>
    /// Adds tags to a cache key. Maintains both forward (tag → keys) and
    /// reverse (key → tags) indices for efficient lookups in either direction.
    /// Applies a TTL to every index key so orphaned metadata expires
    /// even if the cached data entry is never explicitly removed.
    /// </summary>
    public async Task AddTagsAsync(string key, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var tagList = tags.ToList();

        if (tagList.Count == 0)
            return;

        try
        {
            var indexTtl = _config.TagIndexTtlMinutes > 0
                ? TimeSpan.FromMinutes(_config.TagIndexTtlMinutes)
                : (TimeSpan?)null;

            var batch = db.CreateBatch();
            var tasks = new List<Task>();

            // Forward index: add key to each tag's Set + refresh TTL
            foreach (var tag in tagList)
            {
                var tagKey = $"{TagPrefix}{tag}";
                tasks.Add(batch.SetAddAsync(tagKey, key));

                if (indexTtl is not null)
                    tasks.Add(batch.KeyExpireAsync(tagKey, indexTtl));
            }

            // Reverse index: record which tags this key belongs to + refresh TTL
            var reverseKey = $"{ReverseIndexPrefix}{key}";
            var tagValues = tagList.Select(t => (RedisValue)t).ToArray();
            tasks.Add(batch.SetAddAsync(reverseKey, tagValues));

            if (indexTtl is not null)
                tasks.Add(batch.KeyExpireAsync(reverseKey, indexTtl));

            batch.Execute();
            await Task.WhenAll(tasks);

            _logger.LogDebug("Added tags {Tags} to key {Key}", string.Join(", ", tagList), key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add tags to key {Key}", key);
        }
    }

    /// <summary>
    /// Removes a cache key from all its tag associations using the reverse index.
    /// O(number of tags for this key) instead of O(total tags in database).
    /// Performs lazy cleanup: if the forward tag set becomes empty after
    /// removing this member, the tag set key is deleted.
    /// </summary>
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();

        try
        {
            // Read reverse index to find only the tags this key belongs to
            var reverseKey = $"{ReverseIndexPrefix}{key}";
            var tags = await db.SetMembersAsync(reverseKey);

            if (tags.Length > 0)
            {
                var batch = db.CreateBatch();
                var tasks = new List<Task>();

                foreach (var tag in tags)
                {
                    var tagKey = $"{TagPrefix}{tag}";
                    tasks.Add(batch.SetRemoveAsync(tagKey, key));
                }

                // Delete the reverse index entry
                tasks.Add(batch.KeyDeleteAsync(reverseKey));

                batch.Execute();
                await Task.WhenAll(tasks);
            }
            else
            {
                // Reverse index is empty/missing — still try to clean it up
                await db.KeyDeleteAsync(reverseKey);
            }

            _logger.LogDebug("Removed key {Key} from {Count} tag(s)", key, tags.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove key {Key} from tags", key);
        }
    }

    /// <summary>
    /// Gets all cache keys associated with a tag.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetKeysByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();

        try
        {
            var members = await db.SetMembersAsync($"{TagPrefix}{tag}");
            return members.Select(m => m.ToString()).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get keys for tag {Tag}", tag);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Atomically removes all cache keys associated with a tag using a
    /// server-side Lua script. Deletes data keys, reverse index entries,
    /// and the tag set in a single round-trip.
    /// </summary>
    public async Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var tagKey = $"{TagPrefix}{tag}";

        try
        {
            var result = await db.ScriptEvaluateAsync(
                RemoveByTagLuaScript,
                [(RedisKey)tagKey],
                [(RedisValue)ReverseIndexPrefix]);

            var count = (int)result;
            _logger.LogDebug("Atomically removed {Count} keys for tag {Tag}", count, tag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove keys by tag {Tag}", tag);
        }
    }
}
