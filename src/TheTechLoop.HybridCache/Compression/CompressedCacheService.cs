using System.IO.Compression;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Serialization;

namespace TheTechLoop.HybridCache.Compression;

/// <summary>
/// Decorator for ICacheService that automatically compresses large values
/// before storing in cache. Values exceeding the configured threshold are
/// compressed with GZip.
/// <para>
/// Binary wire format: <c>[1-byte header][payload]</c>
/// <list type="bullet">
///   <item><c>0x00</c> — payload is raw UTF-8 JSON bytes</item>
///   <item><c>0x01</c> — payload is GZip-compressed UTF-8 JSON bytes</item>
/// </list>
/// Stored as <c>byte[]</c> through the inner service, eliminating the
/// Base64 overhead of the previous string-based format.
/// </para>
/// </summary>
public class CompressedCacheService : ICacheService
{
    private readonly ICacheService _inner;
    private readonly int _compressionThresholdBytes;
    private readonly CompressionLevel _compressionLevel;

    private const byte HeaderRaw = 0x00;
    private const byte HeaderGzip = 0x01;

    /// <summary>
    /// Initializes a new instance of <see cref="CompressedCacheService"/>.
    /// </summary>
    /// <param name="inner">The underlying cache service</param>
    /// <param name="compressionThresholdBytes">Values larger than this are compressed (default: 1024 bytes = 1KB)</param>
    /// <param name="compressionLevel">GZip compression level (default: Fastest for low-latency caching)</param>
    public CompressedCacheService(
        ICacheService inner,
        int compressionThresholdBytes = 1024,
        CompressionLevel compressionLevel = CompressionLevel.Fastest)
    {
        _inner = inner;
        _compressionThresholdBytes = compressionThresholdBytes;
        _compressionLevel = compressionLevel;
    }

    /// <inheritdoc />
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        var cachedBytes = await _inner.GetOrCreateAsync(
            key,
            async () => await PackAsync(await factory(), cancellationToken),
            expiration,
            cancellationToken);

        return Unpack<T>(cachedBytes)!;
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var cachedBytes = await _inner.GetAsync<byte[]>(key, cancellationToken);

        if (cachedBytes is not { Length: > 1 })
            return default;

        return Unpack<T>(cachedBytes);
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        if (value is null)
            return;

        var packed = await PackAsync(value, cancellationToken);
        await _inner.SetAsync(key, packed, expiration, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, CacheEntryOptions options, CancellationToken cancellationToken = default)
    {
        if (value is null)
            return;

        var packed = await PackAsync(value, cancellationToken);
        await _inner.SetAsync(key, packed, options, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => _inner.RemoveAsync(key, cancellationToken);

    /// <inheritdoc />
    public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
        => _inner.RemoveByPrefixAsync(prefix, cancellationToken);

    /// <inheritdoc />
    public Task RefreshAsync(string key, CancellationToken cancellationToken = default)
        => _inner.RefreshAsync(key, cancellationToken);

    /// <inheritdoc />
    public async Task<Dictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var rawResults = await _inner.GetManyAsync<byte[]>(keys, cancellationToken);
        var results = new Dictionary<string, T?>(rawResults.Count);

        foreach (var (key, cachedBytes) in rawResults)
        {
            if (cachedBytes is not { Length: > 1 })
            {
                results[key] = default;
                continue;
            }

            results[key] = Unpack<T>(cachedBytes);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task SetManyAsync<T>(Dictionary<string, T> items, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        var packedItems = new Dictionary<string, byte[]>(items.Count);

        foreach (var (key, value) in items)
        {
            packedItems[key] = await PackAsync(value, cancellationToken);
        }

        await _inner.SetManyAsync(packedItems, expiration, cancellationToken);
    }

    /// <summary>
    /// Serializes a value to UTF-8 JSON bytes and, if the payload exceeds
    /// the compression threshold, GZip-compresses it. The result is prefixed
    /// with a 1-byte header: <c>0x00</c> (raw) or <c>0x01</c> (GZip).
    /// </summary>
    private async Task<byte[]> PackAsync<T>(T value, CancellationToken cancellationToken)
    {
        var jsonBytes = CacheSerializer.Serialize(value);

        if (jsonBytes.Length <= _compressionThresholdBytes)
        {
            var raw = new byte[1 + jsonBytes.Length];
            raw[0] = HeaderRaw;
            jsonBytes.CopyTo(raw, 1);
            return raw;
        }

        using var outputStream = new MemoryStream();
        outputStream.WriteByte(HeaderGzip);

        await using (var gzipStream = new GZipStream(outputStream, _compressionLevel, leaveOpen: true))
        {
            await gzipStream.WriteAsync(jsonBytes, cancellationToken);
        }

        return outputStream.ToArray();
    }

    /// <summary>
    /// Reads the 1-byte header and either returns the raw JSON slice
    /// or GZip-decompresses the payload, then deserializes to <typeparamref name="T"/>.
    /// </summary>
    private static T? Unpack<T>(byte[] data)
    {
        if (data is not { Length: > 1 })
            return default;

        var header = data[0];
        var payload = data.AsSpan(1);

        if (header == HeaderRaw)
            return CacheSerializer.Deserialize<T>(payload);

        if (header == HeaderGzip)
        {
            using var inputStream = new MemoryStream(data, 1, data.Length - 1);
            using var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress);
            using var outputStream = new MemoryStream();

            gzipStream.CopyTo(outputStream);

            return CacheSerializer.Deserialize<T>(outputStream.ToArray());
        }

        return default;
    }
}
