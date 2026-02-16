using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheTechLoop.HybridCache.Serialization;

/// <summary>
/// JSON serialization options for cache with versioning and error resilience.
/// </summary>
public static class CacheJsonOptions
{
    /// <summary>
    /// Default JSON serialization options for cache storage.
    /// - Ignores unknown properties (forward compatibility)
    /// - Allows reading from mismatched property names
    /// - Handles null values gracefully
    /// </summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,           // ✅ Tolerates case mismatches
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnknownTypeHandling = JsonUnknownTypeHandling.JsonNode,  // ✅ .NET 9 feature
        // Custom converters for common types
        Converters =
        {
            new JsonStringEnumConverter()  // Enums as strings (more resilient)
        }
    };

    /// <summary>
    /// Try to deserialize with fallback to default on error.
    /// </summary>
    public static T? TryDeserialize<T>(string json, JsonSerializerOptions? options = null)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, options ?? Default);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
