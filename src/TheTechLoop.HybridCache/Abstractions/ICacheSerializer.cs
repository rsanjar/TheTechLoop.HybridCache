namespace TheTechLoop.HybridCache.Abstractions;

/// <summary>Singleton, thread-safe byte serialization used by Redis, L2, batch and compressed cache paths.</summary>
public interface ICacheSerializer
{
    /// <summary>Serializes a typed value, including byte arrays used by the compression decorator.</summary>
    byte[] Serialize<T>(T value);

    /// <summary>Deserializes a previously serialized typed value.</summary>
    T? Deserialize<T>(byte[] bytes);

    /// <summary>Reads a payload slice; implementations may override to avoid copying uncompressed data.</summary>
    T? Deserialize<T>(ReadOnlySpan<byte> bytes) => Deserialize<T>(bytes.ToArray());
}
