using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Tagging;

namespace TheTechLoop.HybridCache.Services;

/// <summary>
/// Invalidates cache entries associated with a tag from local memory cache,
/// distributed cache, and Redis tag metadata.
/// </summary>
public sealed class CacheTagInvalidationService : ICacheTagInvalidationService
{
    private readonly ICacheTagService _tagService;
    private readonly ILogger<CacheTagInvalidationService> _logger;
    private readonly IMemoryCache? _memoryCache;

    /// <summary>
    /// Initializes a new instance of <see cref="CacheTagInvalidationService"/>.
    /// </summary>
    public CacheTagInvalidationService(
        ICacheTagService tagService,
        ILogger<CacheTagInvalidationService> logger,
        IMemoryCache? memoryCache = null)
    {
        _tagService = tagService;
        _logger = logger;
        _memoryCache = memoryCache;
    }

    /// <inheritdoc />
    public async Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        try
        {
            var keys = await _tagService.GetKeysByTagAsync(tag, cancellationToken);

            foreach (var key in keys.Distinct(StringComparer.Ordinal))
            {
                _memoryCache?.Remove(key);
            }

            await _tagService.RemoveByTagAsync(tag, cancellationToken);
            _logger.LogDebug("Invalidated {Count} cache key(s) for tag {Tag}", keys.Count, tag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invalidate cache tag {Tag}", tag);
        }
    }
}
