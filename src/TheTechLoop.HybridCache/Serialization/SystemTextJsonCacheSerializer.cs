using System.Text.Json;
using Microsoft.Extensions.Options;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Configuration;

namespace TheTechLoop.HybridCache.Serialization;

/// <summary>Default configurable cache serializer. Configuration is isolated per service provider, never global.</summary>
public sealed class SystemTextJsonCacheSerializer : ICacheSerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>Creates a serializer with the legacy camel-case JSON defaults.</summary>
    public SystemTextJsonCacheSerializer() : this(Options.Create(new CacheSerializationOptions())) { }

    /// <summary>Copies and freezes configured options for safe concurrent use.</summary>
    public SystemTextJsonCacheSerializer(IOptions<CacheSerializationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Value.JsonSerializerOptions);
        _options = new JsonSerializerOptions(options.Value.JsonSerializerOptions);
        _options.MakeReadOnly(populateMissingResolver: true);
    }

    /// <inheritdoc />
    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, _options);

    /// <inheritdoc />
    public T? Deserialize<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, _options);

    /// <inheritdoc />
    public T? Deserialize<T>(ReadOnlySpan<byte> bytes) => JsonSerializer.Deserialize<T>(bytes, _options);
}
