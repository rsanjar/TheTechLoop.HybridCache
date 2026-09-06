using System.Text.Json;

namespace TheTechLoop.HybridCache.Configuration;

/// <summary>Per-container JSON options for the cache byte serializer, independent of MVC and SignalR options.</summary>
public sealed class CacheSerializationOptions
{
    /// <summary>
    /// Configure converters before resolving cache services. The serializer takes a read-only
    /// copy; later changes do not affect existing services. Defaults preserve the legacy wire format.
    /// Use a new cache key/version scope when changing the serialized representation.
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
