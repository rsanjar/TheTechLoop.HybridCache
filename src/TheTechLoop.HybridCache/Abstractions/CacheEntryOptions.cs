namespace TheTechLoop.HybridCache.Abstractions;

/// <summary>
/// Defines cache expiration strategies.
/// </summary>
public enum CacheExpirationType
{
    /// <summary>
    /// Cache entry expires at a fixed time from when it was created.
    /// Example: Created at 10:00 with 1 hour expiration → expires at 11:00.
    /// </summary>
    Absolute,

    /// <summary>
    /// Cache entry expires after a period of inactivity.
    /// Each access resets the expiration timer.
    /// Example: 5-minute sliding window → expires only if not accessed for 5 minutes.
    /// Ideal for session data, user preferences.
    /// </summary>
    Sliding
}

/// <summary>
/// Options for cache entry expiration.
/// </summary>
public sealed class CacheEntryOptions
{
    /// <summary>
    /// Expiration duration. Required.
    /// </summary>
    public TimeSpan Expiration { get; set; }

    /// <summary>
    /// Expiration type: Absolute (fixed) or Sliding (reset on access).
    /// </summary>
    public CacheExpirationType ExpirationType { get; set; } = CacheExpirationType.Absolute;

    /// <summary>
    /// Optional tags for group invalidation.
    /// Example: ["User", "Session"] allows invalidating all user/session data at once.
    /// </summary>
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Creates absolute expiration options.
    /// </summary>
    public static CacheEntryOptions Absolute(TimeSpan expiration, params string[] tags)
        => new()
        {
            Expiration = expiration,
            ExpirationType = CacheExpirationType.Absolute,
            Tags = tags
        };

    /// <summary>
    /// Creates sliding expiration options.
    /// </summary>
    public static CacheEntryOptions Sliding(TimeSpan expiration, params string[] tags)
        => new()
        {
            Expiration = expiration,
            ExpirationType = CacheExpirationType.Sliding,
            Tags = tags
        };
}
