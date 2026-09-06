using System.Text.Json;

namespace TheTechLoop.HybridCache.Serialization;

/// <summary>
/// Legacy static byte serializer with fixed defaults, retained for compatibility.
/// Cache services use the DI-registered ICacheSerializer instead; configuring it does not
/// change these static helpers. Both paths avoid intermediate JSON string allocations.
/// </summary>
public static class CacheSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Serializes a value directly to a UTF-8 byte array.
    /// Avoids the intermediate <see cref="string"/> allocation that
    /// <see cref="JsonSerializer.Serialize{T}(T, JsonSerializerOptions)"/> +
    /// <see cref="System.Text.Encoding.UTF8"/> would produce.
    /// </summary>
    public static byte[] Serialize<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    /// <summary>
    /// Deserializes a UTF-8 byte array directly to <typeparamref name="T"/>.
    /// Avoids the intermediate <see cref="string"/> allocation that
    /// <see cref="System.Text.Encoding.UTF8"/> + <see cref="JsonSerializer.Deserialize{T}(string, JsonSerializerOptions)"/>
    /// would produce.
    /// </summary>
    public static T? Deserialize<T>(byte[] utf8Bytes)
        => JsonSerializer.Deserialize<T>(utf8Bytes, Options);

    /// <summary>
    /// Deserializes a UTF-8 byte span directly to <typeparamref name="T"/>.
    /// </summary>
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Bytes)
        => JsonSerializer.Deserialize<T>(utf8Bytes, Options);
}
