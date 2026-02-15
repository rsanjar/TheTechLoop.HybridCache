using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TheTechLoop.Cache.Abstractions;

namespace TheTechLoop.Cache.Compression;

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

    private const string CompressionMarker = "GZIP:";

    /// <summary>
    /// Initializes a new instance of <see cref="CompressedCacheService"/>.
    /// </summary>
    /// <param name="inner">The underlying cache service</param>
    /// <param name="compressionThresholdBytes">Values larger than this are compressed (default: 1024 bytes = 1KB)</param>
    public CompressedCacheService(ICacheService inner, int compressionThresholdBytes = 1024)
    {
        _inner = inner;
        _compressionThresholdBytes = compressionThresholdBytes;
    }

    /// <summary>
    /// Gets or creates a cache entry.
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
        // Delegate to inner - compression happens in Get/SetAsync
        return await _inner.GetOrCreateAsync(key, factory, expiration, cancellationToken);
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

        // Check if compressed
        if (cachedData.StartsWith(CompressionMarker))
        {
            var compressedBase64 = cachedData[CompressionMarker.Length..];
            var compressedBytes = Convert.FromBase64String(compressedBase64);

            using var inputStream = new MemoryStream(compressedBytes);
            using var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress);
            using var outputStream = new MemoryStream();

            await gzipStream.CopyToAsync(outputStream, cancellationToken);
            var decompressedJson = Encoding.UTF8.GetString(outputStream.ToArray());

            return JsonSerializer.Deserialize<T>(decompressedJson);
        }

        // Not compressed, deserialize directly
        return JsonSerializer.Deserialize<T>(cachedData);
    }

    /// <summary>
    /// Gets a cache entry.
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

        var json = JsonSerializer.Serialize(value);
        var jsonBytes = Encoding.UTF8.GetBytes(json);

        // Compress if larger than threshold
        if (jsonBytes.Length > _compressionThresholdBytes)
        {
            using var outputStream = new MemoryStream();
            await using (var gzipStream = new GZipStream(outputStream, CompressionLevel.Fastest))
            {
                await gzipStream.WriteAsync(jsonBytes, cancellationToken);
            }

            var compressedBytes = outputStream.ToArray();
            var compressedBase64 = Convert.ToBase64String(compressedBytes);
            var markedData = CompressionMarker + compressedBase64;

            await _inner.SetAsync(key, markedData, expiration, cancellationToken);
        }
        else
        {
            // Store uncompressed
            await _inner.SetAsync(key, json, expiration, cancellationToken);
        }
    }

    /// <summary>
    /// Sets a cache entry.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <param name="options"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public Task SetAsync<T>(string key, T value, CacheEntryOptions options, CancellationToken cancellationToken = default)
    {
        // Compression doesn't support advanced options - delegate to inner
        return _inner.SetAsync(key, value, options, cancellationToken);
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
    /// Gets multiple cache entries.
    /// </summary>
    /// <param name="keys"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public Task<Dictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys, CancellationToken cancellationToken = default)
        => _inner.GetManyAsync<T>(keys, cancellationToken);

    /// <summary>
    /// Sets multiple cache entries.
    /// </summary>
    /// <param name="items"></param>
    /// <param name="expiration"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public Task SetManyAsync<T>(Dictionary<string, T> items, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
        => _inner.SetManyAsync(items, expiration, cancellationToken);
}
