namespace TheTechLoop.HybridCache.Keys;

/// <summary>
/// Builds consistent, service-scoped, versioned cache keys.
/// Keys follow the pattern: {service}:{version}:{entity}:{id}
/// Example: "company-svc:v1:Dealership:Id:42"
/// </summary>
public sealed class CacheKeyBuilder
{
    private const string Separator = ":";

    private readonly string _prefix;

    /// <summary>
    /// Creates a CacheKeyBuilder with a service-scoped, versioned prefix.
    /// </summary>
    /// <param name="serviceName">Microservice name (e.g., "company-svc")</param>
    /// <param name="version">Cache version (e.g., "v1"). Bump on breaking DTO changes.</param>
    public CacheKeyBuilder(string serviceName, string version = "v1")
    {
        _prefix = string.IsNullOrEmpty(serviceName)
            ? version
            : $"{serviceName}{Separator}{version}";
    }

    /// <summary>
    /// Builds a scoped cache key from parts.
    /// Example: builder.Key("User", "Id", "42") → "company-svc:v1:User:Id:42"
    /// </summary>
    public string Key(params string[] parts)
    {
        var joined = string.Join(Separator, parts.Where(p => !string.IsNullOrEmpty(p)));
        return string.IsNullOrEmpty(_prefix)
            ? joined
            : $"{_prefix}{Separator}{joined}";
    }

    /// <summary>
    /// Builds a wildcard pattern for SCAN-based prefix deletion.
    /// Example: builder.Pattern("User") → "company-svc:v1:User:*"
    /// </summary>
    public string Pattern(params string[] parts)
    {
        return Key(parts) + "*";
    }

    /// <summary>
    /// Static helper for quick key building without service scope.
    /// Useful for shared/cross-service cache keys.
    /// </summary>
    public static string For(params string[] parts)
    {
        return string.Join(Separator, parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    /// <summary>
    /// Static helper for building an entity key by ID.
    /// Example: CacheKeyBuilder.ForEntity("User", 42) → "User:42"
    /// </summary>
    public static string ForEntity(string entity, int id)
    {
        return $"{entity}{Separator}{id}";
    }

    /// <summary>
    /// Static helper for building an entity key by string key.
    /// </summary>
    public static string ForEntity(string entity, string key)
    {
        return $"{entity}{Separator}{Sanitize(key)}";
    }

    /// <summary>
    /// Sanitizes a key segment by replacing characters that are
    /// problematic in Redis keys.
    /// </summary>
    public static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "empty";

        return value
            .Replace(" ", "_")
            .Replace(":", "_")
            .Replace("/", "_")
            .Replace("\\", "_")
            .ToLowerInvariant()
            .Trim();
    }
}
