using Microsoft.Extensions.Logging;
using StackExchange.Redis;

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
/// </summary>
public class RedisCacheTagService : ICacheTagService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisCacheTagService> _logger;

    private const string TagPrefix = "tag:";
    private const string ReverseIndexPrefix = "key:tags:";

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisCacheTagService"/> class.
    /// </summary>
    /// <param name="redis"></param>
    /// <param name="logger"></param>
    public RedisCacheTagService(
        IConnectionMultiplexer redis,
        ILogger<RedisCacheTagService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// Adds tags to a cache key. Maintains both forward (tag → keys) and
    /// reverse (key → tags) indices for efficient lookups in either direction.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="tags"></param>
    /// <param name="cancellationToken"></param>
    public async Task AddTagsAsync(string key, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var tagList = tags.ToList();

        if (tagList.Count == 0)
            return;

        try
        {
            // Forward index: add key to each tag's Set
            var forwardTasks = tagList.Select(tag =>
                db.SetAddAsync($"{TagPrefix}{tag}", key)
            );

            // Reverse index: record which tags this key belongs to
            var tagValues = tagList.Select(t => (RedisValue)t).ToArray();
            var reverseTask = db.SetAddAsync($"{ReverseIndexPrefix}{key}", tagValues);

            await Task.WhenAll([.. forwardTasks, reverseTask]);

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
    /// </summary>
    /// <param name="key"></param>
    /// <param name="cancellationToken"></param>
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
                // Remove key from each of its tag sets
                var removeTasks = tags.Select(tag =>
                    db.SetRemoveAsync($"{TagPrefix}{tag}", key)
                );

                await Task.WhenAll(removeTasks);
            }

            // Delete the reverse index entry
            await db.KeyDeleteAsync(reverseKey);

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
    /// <param name="tag"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
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
    /// Removes all cache keys associated with a tag, including their reverse index entries.
    /// </summary>
    /// <param name="tag"></param>
    /// <param name="cancellationToken"></param>
    public async Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();

        try
        {
            // Get all keys with this tag
            var keys = await GetKeysByTagAsync(tag, cancellationToken);

            if (keys.Count == 0)
            {
                _logger.LogDebug("No keys found for tag {Tag}", tag);
                return;
            }

            // Delete all cache entries
            var redisKeys = keys.Select(k => (RedisKey)k).ToArray();
            await db.KeyDeleteAsync(redisKeys);

            // Clean up reverse index entries for the deleted keys
            var reverseKeys = keys.Select(k => (RedisKey)$"{ReverseIndexPrefix}{k}").ToArray();
            await db.KeyDeleteAsync(reverseKeys);

            // Delete the tag set itself
            await db.KeyDeleteAsync($"{TagPrefix}{tag}");

            _logger.LogDebug("Removed {Count} keys for tag {Tag}", keys.Count, tag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove keys by tag {Tag}", tag);
        }
    }
}
