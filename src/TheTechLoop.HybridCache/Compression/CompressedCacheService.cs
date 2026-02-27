using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TheTechLoop.HybridCache.Abstractions;

namespace TheTechLoop.HybridCache.Compression;

/// <summary>
/// Decorator for ICacheService that automatically compresses large values
/// before storing in cache. Values > 1KB are compressed with GZip.
/// <para>
/// Compression reduces Redis memory usage and network bandwidth at the cost
/// of CPU cycles. Best for text-heavy data (JSON, XML, HTML).
/// </para>
/// </summary>
public class CompressedCacheService : ICacheService
{
    private readonly ICacheService _inner;
    private readonly int _compressionThresholdBytes;
    private readonly CompressionLevel _compressionLevel;

    private const string CompressionMarker = "GZIP:";

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

    /// <summary>
    /// Gets or creates a cache entry. Uses compressed Get/Set paths so the
    /// inner service's stampede protection is preserved while values are
    /// transparently compressed.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="factory"></param>
    /// <param name="expiration"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        var cachedData = await _inner.GetOrCreateAsync(
            key,
            async () => await SerializeAndCompressAsync(await factory(), cancellationToken),
            expiration,
            cancellationToken);

        return (await DecompressAndDeserializeAsync<T>(cachedData, cancellationToken))!;
    }

    /// <summary>
    /// Gets a cache entry.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var cachedData = await _inner.GetAsync<string>(key, cancellationToken);

        if (string.IsNullOrEmpty(cachedData))
            return default;

        return await DecompressAndDeserializeAsync<T>(cachedData, cancellationToken);
    }

    /// <summary>
    /// Sets a cache entry.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <param name="expiration"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    public async Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        if (value is null)
            return;

        var serialized = await SerializeAndCompressAsync(value, cancellationToken);
        await _inner.SetAsync(key, serialized, expiration, cancellationToken);
    }

    /// <summary>
    /// Sets a cache entry with advanced expiration options.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <param name="options"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task SetAsync<T>(string key, T value, CacheEntryOptions options, CancellationToken cancellationToken = default)
    {
        if (value is null)
            return;

        var serialized = await SerializeAndCompressAsync(value, cancellationToken);
        await _inner.SetAsync(key, serialized, options, cancellationToken);
    }

    /// <summary>
    /// Removes a cache entry.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => _inner.RemoveAsync(key, cancellationToken);

    /// <summary>
    /// Removes a cache entry by its prefix.
    /// </summary>
    /// <param name="prefix"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
        => _inner.RemoveByPrefixAsync(prefix, cancellationToken);

    /// <summary>
    /// Refreshes a cache entry.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task RefreshAsync(string key, CancellationToken cancellationToken = default)
        => _inner.RefreshAsync(key, cancellationToken);

    /// <summary>
    /// Gets multiple cache entries with decompression support.
    /// </summary>
    /// <param name="keys"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task<Dictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var rawResults = await _inner.GetManyAsync<string>(keys, cancellationToken);
        var results = new Dictionary<string, T?>(rawResults.Count);

        foreach (var (key, cachedData) in rawResults)
        {
            if (string.IsNullOrEmpty(cachedData))
            {
                results[key] = default;
                continue;
            }

            results[key] = await DecompressAndDeserializeAsync<T>(cachedData, cancellationToken);
        }

        return results;
    }

    /// <summary>
    /// Sets multiple cache entries with compression support.
    /// </summary>
    /// <param name="items"></param>
    /// <param name="expiration"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task SetManyAsync<T>(Dictionary<string, T> items, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        var serializedItems = new Dictionary<string, string>(items.Count);

        foreach (var (key, value) in items)
        {
            serializedItems[key] = await SerializeAndCompressAsync(value, cancellationToken);
        }

        await _inner.SetManyAsync(serializedItems, expiration, cancellationToken);
    }

    /// <summary>
    /// Serializes a value to a JSON string, compressing with GZip if the
    /// serialized size exceeds the configured threshold.
    /// </summary>
    private async Task<string> SerializeAndCompressAsync<T>(T value, CancellationToken cancellationToken)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value);

        if (jsonBytes.Length <= _compressionThresholdBytes)
            return Encoding.UTF8.GetString(jsonBytes);

        using var outputStream = new MemoryStream();
        await using (var gzipStream = new GZipStream(outputStream, _compressionLevel, leaveOpen: true))
        {
            await gzipStream.WriteAsync(jsonBytes, cancellationToken);
        }

        var compressedBase64 = Convert.ToBase64String(
            outputStream.GetBuffer(), 0, (int)outputStream.Length);
        return string.Concat(CompressionMarker, compressedBase64);
    }

    /// <summary>
    /// Decompresses (if needed) and deserializes a cached string value.
    /// </summary>
    private static async Task<T?> DecompressAndDeserializeAsync<T>(string cachedData, CancellationToken cancellationToken)
    {
        if (!cachedData.StartsWith(CompressionMarker, StringComparison.Ordinal))
            return JsonSerializer.Deserialize<T>(cachedData);

        var compressedBase64 = cachedData[CompressionMarker.Length..];
        var compressedBytes = Convert.FromBase64String(compressedBase64);

        using var inputStream = new MemoryStream(compressedBytes);
        using var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress);
        using var outputStream = new MemoryStream();

        await gzipStream.CopyToAsync(outputStream, cancellationToken);
        outputStream.Position = 0;

        return await JsonSerializer.DeserializeAsync<T>(outputStream, cancellationToken: cancellationToken);
    }
}
