# Scenario 10: Write-Heavy Workload (Invalidation-Focused)

## 📋 Overview

This scenario demonstrates caching strategies for **write-heavy workloads** where data changes frequently and cache invalidation must be fast and reliable across all instances. Optimized for systems with low read:write ratios (1:1 to 10:1) where data freshness is critical.

### ⚡ Performance Characteristics

- **Cache Strategy:** Aggressive invalidation over stale data
- **TTL Strategy:** Short absolute expiration (1-5 minutes)
- **Consistency Model:** Eventual consistency with fast propagation
- **Invalidation:** Cross-instance via Pub/Sub or Streams
- **Best For:** Read:Write ratios < 10:1

---

## 🎯 When to Use This Scenario

### ✅ Ideal For

- **Real-Time Systems** — Live sports scores, stock tickers, IoT dashboards
- **Collaborative Editing** — Google Docs-style apps, shared whiteboards
- **Live Dashboards** — Admin panels, monitoring dashboards, analytics
- **Social Media Feeds** — Activity streams, notifications, comments
- **Chat Applications** — Messaging, presence status, typing indicators
- **E-Commerce Inventory** — Stock levels, pricing updates, availability
- **Booking Systems** — Seat/room availability with frequent updates
- **Gaming Leaderboards** — Real-time rankings, scores, achievements

### ❌ Not Suitable For

- **Read-Heavy Systems** — Use Scenario #1 (CQRS Multi-Level) instead
- **Static Reference Data** — Use Scenario #6 (Cache Warming) instead
- **Archival Data** — Use longer TTL with standard caching
- **Systems Requiring Strong Consistency** — Consider cache-aside or write-through only

---

## 🏗️ Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    Write-Heavy Architecture                      │
│                                                                  │
│  ┌──────────────┐         ┌──────────────────────────┐         │
│  │  Instance 1  │         │    Redis (Pub/Sub)       │         │
│  │              │ ──pub──>│   or Streams             │         │
│  │  Controller  │         │                          │         │
│  │      ↓       │         │   invalidation:product   │         │
│  │   [Update]   │         └────────────┬─────────────┘         │
│  │      ↓       │                      │                        │
│  │  Invalidate  │ <──────sub───────────┘                        │
│  │   L1 + L2    │                      │                        │
│  └──────────────┘                      │                        │
│                                        │                        │
│  ┌──────────────┐                      │                        │
│  │  Instance 2  │ <──────sub───────────┘                        │
│  │              │                                               │
│  │  Controller  │         ┌──────────────────────────┐         │
│  │      ↓       │         │    Database (Source      │         │
│  │   [Read]     │ ───────>│     of Truth)            │         │
│  │      ↓       │ <───────│                          │         │
│  │  Cache Miss  │         │  • All writes here first │         │
│  │  (98% miss)  │         │  • Cache populated on    │         │
│  └──────────────┘         │    cache miss            │         │
│                           └──────────────────────────┘         │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘

Key Characteristics:
• Short TTL (1-5 minutes) for automatic expiration
• Aggressive invalidation on every write
• Pub/Sub or Streams for cross-instance invalidation
• Low hit rate expected (50-70%)
• Cache used to reduce DB load spikes, not for long-term storage
```

---

## 🚀 Implementation Guide

### Step 1: Install NuGet Packages

```bash
dotnet add package TheTechLoop.HybridCache
dotnet add package StackExchange.Redis
```

---

### Step 2: Configuration (appsettings.json)

```json
{
  "TheTechLoopCache": {
    "MemoryCache": {
      "Enabled": true,
      "SizeLimit": 500,
      "CompactionPercentage": 0.30,
      "ExpirationScanFrequency": "00:01:00"
    },
    "Redis": {
      "Enabled": true,
      "Configuration": "localhost:6379",
      "InstanceName": "RealTimeApp:",
      "DefaultDatabase": 0
    },
    "DefaultAbsoluteExpiration": "00:02:00",
    "DefaultSlidingExpiration": null,
    "EnableCompression": false,
    "UseStreamsForInvalidation": true,
    "StreamConfiguration": {
      "StreamName": "cache:invalidations",
      "ConsumerGroup": "cache-invalidation-group",
      "ConsumerName": "instance-{MachineName}",
      "MaxStreamLength": 1000,
      "PollInterval": "00:00:01"
    },
    "EnableEffectivenessMetrics": true
  },
  
  "Logging": {
    "LogLevel": {
      "TheTechLoop.HybridCache": "Information"
    }
  }
}
```

#### Configuration Breakdown

| Setting | Value | Purpose |
|---------|-------|---------|
| `DefaultAbsoluteExpiration` | `00:02:00` | Short TTL (2 minutes) — data changes frequently |
| `UseStreamsForInvalidation` | `true` | Guaranteed delivery (Pub/Sub alternative) |
| `PollInterval` | `00:00:01` | Check for invalidations every 1 second |
| `MemoryCache.SizeLimit` | `500` | Small cache (write-heavy = low benefit) |
| `EnableCompression` | `false` | Skip compression (short-lived data) |
| `ExpirationScanFrequency` | `00:01:00` | Aggressive cleanup of expired entries |

---

### Step 3: Service Registration (Program.cs)

```csharp
using TheTechLoop.HybridCache.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register cache with Streams for guaranteed invalidation
builder.Services.AddTheTechLoopCache(builder.Configuration);
builder.Services.AddTheTechLoopMultiLevelCache(builder.Configuration);

// Optional: Metrics to track hit rate
builder.Services.AddTheTechLoopCacheEffectivenessMetrics();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
```

---

### Step 4: Write-Heavy Controller with Aggressive Invalidation

#### Example 1: Live Dashboard (Stock Prices)

```csharp
using Microsoft.AspNetCore.Mvc;
using TheTechLoop.HybridCache.Abstractions;

namespace RealTimeApp.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StockPricesController : ControllerBase
{
    private readonly ICacheService _cache;
    private readonly IStockPriceRepository _repository;
    private readonly ILogger<StockPricesController> _logger;

    public StockPricesController(
        ICacheService cache,
        IStockPriceRepository repository,
        ILogger<StockPricesController> logger)
    {
        _cache = cache;
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Get current stock price (read - may hit cache)
    /// </summary>
    [HttpGet("{symbol}")]
    public async Task<ActionResult<StockPrice>> GetStockPrice(
        string symbol,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"stock:price:{symbol}";

        // Try cache first (short TTL)
        var cached = await _cache.GetAsync<StockPrice>(cacheKey, cancellationToken);
        if (cached != null)
        {
            _logger.LogDebug("Cache HIT for stock {Symbol}", symbol);
            return Ok(cached);
        }

        _logger.LogDebug("Cache MISS for stock {Symbol}", symbol);

        // Load from database
        var price = await _repository.GetCurrentPriceAsync(symbol, cancellationToken);
        if (price == null)
            return NotFound();

        // Cache for only 2 minutes (frequent updates expected)
        await _cache.SetAsync(cacheKey, price, new CacheEntryOptions
        {
            AbsoluteExpiration = TimeSpan.FromMinutes(2),
            Size = 1,
            Priority = CacheItemPriority.Low // Evict easily under pressure
        }, cancellationToken);

        return Ok(price);
    }

    /// <summary>
    /// Update stock price (write - invalidate immediately)
    /// </summary>
    [HttpPut("{symbol}")]
    public async Task<IActionResult> UpdateStockPrice(
        string symbol,
        [FromBody] decimal newPrice,
        CancellationToken cancellationToken)
    {
        var stockPrice = new StockPrice
        {
            Symbol = symbol,
            Price = newPrice,
            Timestamp = DateTime.UtcNow
        };

        // Write to database first (source of truth)
        await _repository.UpdateAsync(stockPrice, cancellationToken);

        // Invalidate cache IMMEDIATELY across all instances
        var cacheKey = $"stock:price:{symbol}";
        await _cache.RemoveAsync(cacheKey, cancellationToken);

        _logger.LogInformation("Stock {Symbol} updated to {Price} and cache invalidated", symbol, newPrice);

        return NoContent();
    }

    /// <summary>
    /// Batch update (market data feed)
    /// </summary>
    [HttpPost("batch")]
    public async Task<IActionResult> UpdateStockPricesBatch(
        [FromBody] List<StockPrice> prices,
        CancellationToken cancellationToken)
    {
        // Write to database in batch
        await _repository.UpdateBatchAsync(prices, cancellationToken);

        // Invalidate all affected cache keys
        var invalidationTasks = prices.Select(p =>
            _cache.RemoveAsync($"stock:price:{p.Symbol}", cancellationToken)
        );

        await Task.WhenAll(invalidationTasks);

        _logger.LogInformation("Batch updated {Count} stock prices and invalidated cache", prices.Count);

        return NoContent();
    }
}
```

---

#### Example 2: Collaborative Editing (Document Status)

```csharp
[ApiController]
[Route("api/[controller]")]
public class DocumentsController : ControllerBase
{
    private readonly ICacheService _cache;
    private readonly IDocumentRepository _repository;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(
        ICacheService cache,
        IDocumentRepository repository,
        ILogger<DocumentsController> logger)
    {
        _cache = cache;
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Get document metadata (title, owner, status)
    /// </summary>
    [HttpGet("{documentId}")]
    public async Task<ActionResult<Document>> GetDocument(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"document:{documentId}";

        // Check cache
        var cached = await _cache.GetAsync<Document>(cacheKey, cancellationToken);
        if (cached != null)
        {
            return Ok(cached);
        }

        // Load from database
        var document = await _repository.GetByIdAsync(documentId, cancellationToken);
        if (document == null)
            return NotFound();

        // Cache for 5 minutes (collaborative editing = frequent changes)
        await _cache.SetAsync(cacheKey, document, new CacheEntryOptions
        {
            AbsoluteExpiration = TimeSpan.FromMinutes(5),
            Size = 1
        }, cancellationToken);

        return Ok(document);
    }

    /// <summary>
    /// Update document content (write operation)
    /// </summary>
    [HttpPut("{documentId}")]
    public async Task<IActionResult> UpdateDocument(
        Guid documentId,
        [FromBody] DocumentUpdateRequest request,
        CancellationToken cancellationToken)
    {
        // Update database
        await _repository.UpdateContentAsync(documentId, request.Content, cancellationToken);

        // Invalidate cache across all instances
        await _cache.RemoveAsync($"document:{documentId}", cancellationToken);

        // Also invalidate related caches
        await _cache.RemoveAsync($"document:{documentId}:versions", cancellationToken);

        _logger.LogInformation("Document {DocumentId} updated and cache invalidated", documentId);

        return NoContent();
    }

    /// <summary>
    /// Get active users editing this document (highly volatile)
    /// </summary>
    [HttpGet("{documentId}/active-users")]
    public async Task<ActionResult<List<string>>> GetActiveUsers(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"document:{documentId}:active-users";

        // Very short TTL (30 seconds) - users join/leave frequently
        var cached = await _cache.GetAsync<List<string>>(cacheKey, cancellationToken);
        if (cached != null)
        {
            return Ok(cached);
        }

        var activeUsers = await _repository.GetActiveUsersAsync(documentId, cancellationToken);

        await _cache.SetAsync(cacheKey, activeUsers, new CacheEntryOptions
        {
            AbsoluteExpiration = TimeSpan.FromSeconds(30), // Very short TTL
            Size = 1
        }, cancellationToken);

        return Ok(activeUsers);
    }

    /// <summary>
    /// User joins document (write + invalidate)
    /// </summary>
    [HttpPost("{documentId}/join")]
    public async Task<IActionResult> JoinDocument(
        Guid documentId,
        [FromBody] string userId,
        CancellationToken cancellationToken)
    {
        await _repository.AddActiveUserAsync(documentId, userId, cancellationToken);

        // Invalidate active users cache
        await _cache.RemoveAsync($"document:{documentId}:active-users", cancellationToken);

        return NoContent();
    }
}
```

---

#### Example 3: Social Media Feed (Activity Stream)

```csharp
[ApiController]
[Route("api/[controller]")]
public class ActivityFeedController : ControllerBase
{
    private readonly ICacheService _cache;
    private readonly IActivityRepository _repository;
    private readonly ILogger<ActivityFeedController> _logger;

    public ActivityFeedController(
        ICacheService cache,
        IActivityRepository repository,
        ILogger<ActivityFeedController> logger)
    {
        _cache = cache;
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Get user's activity feed (paginated)
    /// </summary>
    [HttpGet("user/{userId}")]
    public async Task<ActionResult<List<Activity>>> GetUserFeed(
        int userId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"feed:user:{userId}:page:{page}";

        // Check cache (short TTL - feed updates frequently)
        var cached = await _cache.GetAsync<List<Activity>>(cacheKey, cancellationToken);
        if (cached != null)
        {
            return Ok(cached);
        }

        // Load from database
        var activities = await _repository.GetUserFeedAsync(userId, page, pageSize, cancellationToken);

        // Cache for only 1 minute (very volatile data)
        await _cache.SetAsync(cacheKey, activities, new CacheEntryOptions
        {
            AbsoluteExpiration = TimeSpan.FromMinutes(1),
            Size = activities.Count,
            Priority = CacheItemPriority.Low
        }, cancellationToken);

        return Ok(activities);
    }

    /// <summary>
    /// Post new activity (write + invalidate followers' feeds)
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> PostActivity(
        [FromBody] CreateActivityRequest request,
        CancellationToken cancellationToken)
    {
        // Create activity in database
        var activity = await _repository.CreateAsync(request, cancellationToken);

        // Get user's followers
        var followerIds = await _repository.GetFollowerIdsAsync(request.UserId, cancellationToken);

        // Invalidate feeds for all followers (fan-out on write)
        var invalidationTasks = new List<Task>();

        foreach (var followerId in followerIds)
        {
            // Invalidate all pages of follower's feed
            invalidationTasks.Add(
                _cache.RemovePatternAsync($"feed:user:{followerId}:page:*", cancellationToken)
            );
        }

        await Task.WhenAll(invalidationTasks);

        _logger.LogInformation(
            "Activity posted by user {UserId}, invalidated {FollowerCount} follower feeds",
            request.UserId,
            followerIds.Count
        );

        return Ok(activity);
    }

    /// <summary>
    /// Like activity (write + invalidate)
    /// </summary>
    [HttpPost("{activityId}/like")]
    public async Task<IActionResult> LikeActivity(
        int activityId,
        [FromBody] int userId,
        CancellationToken cancellationToken)
    {
        await _repository.AddLikeAsync(activityId, userId, cancellationToken);

        // Invalidate activity detail cache
        await _cache.RemoveAsync($"activity:{activityId}", cancellationToken);

        // Invalidate all feed pages that might contain this activity
        // (This is expensive but necessary for consistency)
        await _cache.RemovePatternAsync("feed:user:*", cancellationToken);

        return NoContent();
    }
}
```

---

### Step 5: Service Layer with Invalidation Patterns

```csharp
public class InventoryService : IInventoryService
{
    private readonly ICacheService _cache;
    private readonly IInventoryRepository _repository;
    private readonly ILogger<InventoryService> _logger;

    public InventoryService(
        ICacheService cache,
        IInventoryRepository repository,
        ILogger<InventoryService> logger)
    {
        _cache = cache;
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Get current stock level (read)
    /// </summary>
    public async Task<int> GetStockLevelAsync(
        int productId,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"inventory:stock:{productId}";

        var cached = await _cache.GetAsync<int?>(cacheKey, cancellationToken);
        if (cached.HasValue)
        {
            return cached.Value;
        }

        var stockLevel = await _repository.GetStockLevelAsync(productId, cancellationToken);

        // Cache for 3 minutes (inventory changes frequently)
        await _cache.SetAsync(cacheKey, stockLevel, new CacheEntryOptions
        {
            AbsoluteExpiration = TimeSpan.FromMinutes(3),
            Size = 1
        }, cancellationToken);

        return stockLevel;
    }

    /// <summary>
    /// Reserve stock (write operation - critical path)
    /// </summary>
    public async Task<bool> ReserveStockAsync(
        int productId,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        // Update database (atomic operation)
        var success = await _repository.ReserveStockAsync(productId, quantity, cancellationToken);

        if (success)
        {
            // IMMEDIATE invalidation - other instances must see updated stock
            await _cache.RemoveAsync($"inventory:stock:{productId}", cancellationToken);

            _logger.LogInformation(
                "Reserved {Quantity} units of product {ProductId}, cache invalidated",
                quantity,
                productId
            );
        }

        return success;
    }

    /// <summary>
    /// Restore stock (write operation)
    /// </summary>
    public async Task RestoreStockAsync(
        int productId,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        await _repository.RestoreStockAsync(productId, quantity, cancellationToken);

        // Invalidate cache
        await _cache.RemoveAsync($"inventory:stock:{productId}", cancellationToken);

        _logger.LogInformation(
            "Restored {Quantity} units of product {ProductId}, cache invalidated",
            quantity,
            productId
        );
    }
}
```

---

## ⚙️ Advanced Configuration

### Using Redis Streams for Guaranteed Delivery

**Why Streams?** Pub/Sub can lose messages if an instance is down. Streams guarantee delivery.

```json
{
  "TheTechLoopCache": {
    "UseStreamsForInvalidation": true,
    "StreamConfiguration": {
      "StreamName": "cache:invalidations",
      "ConsumerGroup": "cache-invalidation-group",
      "ConsumerName": "instance-{MachineName}",
      "MaxStreamLength": 1000,
      "PollInterval": "00:00:01",
      "AckTimeout": "00:00:30"
    }
  }
}
```

#### Stream Advantages

- **Guaranteed Delivery:** Messages persisted until acknowledged
- **Consumer Groups:** Load balancing across instances
- **Replay:** Can reprocess messages if needed
- **No Message Loss:** Even during instance downtime

---

### Fallback to Pub/Sub (Simpler Setup)

```json
{
  "TheTechLoopCache": {
    "UseStreamsForInvalidation": false,
    "Redis": {
      "PubSubChannel": "cache:invalidations"
    }
  }
}
```

**Trade-offs:**
- ✅ Simpler setup
- ✅ Lower latency
- ❌ Messages lost if instance down
- ❌ No replay capability

---

### Tuning TTL for Your Workload

```json
{
  "TheTechLoopCache": {
    "DefaultAbsoluteExpiration": "00:01:00"
  }
}
```

| Data Type | Recommended TTL | Reasoning |
|-----------|-----------------|-----------|
| Stock prices | 1-2 minutes | High frequency updates |
| Inventory levels | 2-5 minutes | Moderate frequency |
| User sessions | 15-30 minutes | Balanced read/write |
| Live dashboards | 30-60 seconds | Near real-time |
| Chat presence | 15-30 seconds | Very volatile |

---

## 📊 Monitoring & Metrics

### Expected Metrics for Write-Heavy Systems

```
Cache Effectiveness Metrics (Write-Heavy Workload):
  inventory:stock:* - Hit Rate: 55.2% (220/400 requests), Avg Latency: 2.1ms
  document:* - Hit Rate: 48.7% (195/400 requests), Avg Latency: 1.8ms
  feed:user:* - Hit Rate: 62.1% (248/400 requests), Avg Latency: 2.5ms

Note: Hit rates 50-70% are EXPECTED and ACCEPTABLE for write-heavy systems.
The goal is to reduce database load spikes, not maximize hit rate.
```

### Custom Metrics Endpoint

```csharp
[ApiController]
[Route("api/[controller]")]
public class CacheMetricsController : ControllerBase
{
    private readonly CacheEffectivenessMetrics _metrics;

    public CacheMetricsController(CacheEffectivenessMetrics metrics)
    {
        _metrics = metrics;
    }

    [HttpGet("stats")]
    public ActionResult GetCacheStats()
    {
        var stats = _metrics.GetMetrics();
        
        return Ok(new
        {
            Timestamp = DateTime.UtcNow,
            CacheType = "Write-Heavy (Invalidation-Focused)",
            ExpectedHitRate = "50-70%",
            Metrics = stats.Select(m => new
            {
                m.KeyPattern,
                HitRate = $"{m.HitRate:P2}",
                m.Hits,
                m.Misses,
                TotalRequests = m.Hits + m.Misses,
                AvgLatency = $"{m.AverageLatency.TotalMilliseconds:F2}ms",
                Status = m.HitRate switch
                {
                    >= 0.7 => "Excellent (unexpectedly high for write-heavy)",
                    >= 0.5 => "Good (expected for write-heavy)",
                    >= 0.3 => "Fair (high write frequency)",
                    _ => "Low (consider longer TTL or reduce writes)"
                }
            }).OrderBy(m => m.KeyPattern)
        });
    }

    [HttpGet("invalidations")]
    public async Task<ActionResult> GetInvalidationStats(
        [FromServices] ICacheService cache)
    {
        // Custom tracking (you would implement this)
        // Track invalidation counts, latency, etc.
        
        return Ok(new
        {
            Message = "Track invalidation metrics in your service layer",
            Example = new
            {
                TotalInvalidations = 1234,
                InvalidationsPerSecond = 5.2,
                AverageInvalidationLatency = "12ms",
                CrossInstanceInvalidations = 987
            }
        });
    }
}
```

---

## 🎯 Best Practices

### 1. Write-First, Invalidate-Second

```csharp
public async Task UpdateProductAsync(Product product, CancellationToken cancellationToken)
{
    // ✅ CORRECT ORDER
    
    // 1. Write to database first (source of truth)
    await _repository.UpdateAsync(product, cancellationToken);
    
    // 2. Then invalidate cache
    await _cache.RemoveAsync($"product:{product.Id}", cancellationToken);
    
    // ❌ WRONG ORDER - could lead to stale data
    // await _cache.RemoveAsync(...);
    // await _repository.UpdateAsync(...); // If this fails, cache already cleared
}
```

### 2. Invalidate Related Caches

```csharp
public async Task UpdateProductAsync(Product product, CancellationToken cancellationToken)
{
    await _repository.UpdateAsync(product, cancellationToken);
    
    // Invalidate product-specific cache
    await _cache.RemoveAsync($"product:{product.Id}", cancellationToken);
    
    // Invalidate related caches
    await _cache.RemoveAsync("products:all", cancellationToken);
    await _cache.RemoveAsync($"products:category:{product.CategoryId}", cancellationToken);
    await _cache.RemoveAsync($"products:featured", cancellationToken);
}
```

### 3. Batch Invalidations

```csharp
public async Task UpdateProductsBatchAsync(List<Product> products, CancellationToken cancellationToken)
{
    // Write to database
    await _repository.UpdateBatchAsync(products, cancellationToken);
    
    // Batch invalidate
    var keys = products.Select(p => $"product:{p.Id}").ToList();
    keys.Add("products:all");
    
    await _cache.RemoveBatchAsync(keys, cancellationToken);
}
```

### 4. Handle Invalidation Failures Gracefully

```csharp
public async Task UpdateProductAsync(Product product, CancellationToken cancellationToken)
{
    await _repository.UpdateAsync(product, cancellationToken);
    
    try
    {
        await _cache.RemoveAsync($"product:{product.Id}", cancellationToken);
    }
    catch (Exception ex)
    {
        // Log but don't fail the operation
        _logger.LogWarning(ex, 
            "Cache invalidation failed for product {ProductId}. Data will expire naturally via TTL.",
            product.Id
        );
        
        // TTL will eventually expire stale data
    }
}
```

### 5. Use Circuit Breaker for Cache Operations

```csharp
public async Task<Product?> GetProductAsync(int productId, CancellationToken cancellationToken)
{
    Product? cached = null;
    
    try
    {
        cached = await _cache.GetAsync<Product>($"product:{productId}", cancellationToken);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Cache read failed, falling back to database");
        // Continue to database query
    }
    
    if (cached != null)
        return cached;
    
    // Database is source of truth
    return await _repository.GetByIdAsync(productId, cancellationToken);
}
```

---

## 🧪 Testing

### Integration Test: Verify Cross-Instance Invalidation

```csharp
using Xunit;
using Microsoft.Extensions.DependencyInjection;

public class CrossInstanceInvalidationTests : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _redisFixture;

    public CrossInstanceInvalidationTests(RedisFixture redisFixture)
    {
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task UpdateProduct_InvalidatesCacheAcrossInstances()
    {
        // Arrange - Create two "instances" (two cache services)
        var instance1 = _redisFixture.CreateCacheService("instance1");
        var instance2 = _redisFixture.CreateCacheService("instance2");

        var product = new Product { Id = 1, Name = "Original", Price = 100 };
        var cacheKey = $"product:{product.Id}";

        // Both instances cache the product
        await instance1.SetAsync(cacheKey, product, new CacheEntryOptions
        {
            AbsoluteExpiration = TimeSpan.FromMinutes(5)
        });

        var cachedInInstance2 = await instance2.GetAsync<Product>(cacheKey);
        Assert.NotNull(cachedInInstance2);
        Assert.Equal("Original", cachedInInstance2.Name);

        // Act - Instance 1 invalidates cache
        await instance1.RemoveAsync(cacheKey);

        // Wait for invalidation to propagate (Streams/Pub-Sub)
        await Task.Delay(100);

        // Assert - Instance 2 should see invalidation
        var afterInvalidation = await instance2.GetAsync<Product>(cacheKey);
        Assert.Null(afterInvalidation);
    }

    [Fact]
    public async Task HighFrequencyWrites_InvalidatesCorrectly()
    {
        // Arrange
        var instance = _redisFixture.CreateCacheService();
        var productId = 1;
        var cacheKey = $"product:{productId}";

        // Act - Simulate 100 rapid writes
        for (int i = 0; i < 100; i++)
        {
            var product = new Product { Id = productId, Name = $"Version-{i}", Price = i };
            
            // Write to cache
            await instance.SetAsync(cacheKey, product, new CacheEntryOptions
            {
                AbsoluteExpiration = TimeSpan.FromMinutes(2)
            });
            
            // Immediately invalidate
            await instance.RemoveAsync(cacheKey);
        }

        // Assert - Cache should be empty
        var final = await instance.GetAsync<Product>(cacheKey);
        Assert.Null(final);
    }
}
```

---

## 🔧 Troubleshooting

### Issue 1: Invalidation Lag Between Instances

**Symptom:** Instance A writes, but Instance B still serves stale data

**Diagnosis:**
```csharp
// Add logging to track invalidation timing
_logger.LogInformation("Invalidation sent at {Timestamp}", DateTime.UtcNow);

// In receiving instance:
_logger.LogInformation("Invalidation received at {Timestamp}", DateTime.UtcNow);
```

**Solutions:**
1. **Use Streams instead of Pub/Sub** (guaranteed delivery)
2. **Reduce PollInterval** (e.g., 500ms instead of 1s)
3. **Add explicit invalidation delays** for critical operations

```json
{
  "TheTechLoopCache": {
    "UseStreamsForInvalidation": true,
    "StreamConfiguration": {
      "PollInterval": "00:00:00.500"
    }
  }
}
```

---

### Issue 2: Too Many Invalidations Overwhelming Redis

**Symptom:** Redis CPU spikes, slow invalidation propagation

**Solution 1: Batch Invalidations**

```csharp
// Instead of individual removes:
foreach (var id in productIds)
{
    await _cache.RemoveAsync($"product:{id}"); // ❌ N network calls
}

// Use batch:
var keys = productIds.Select(id => $"product:{id}").ToList();
await _cache.RemoveBatchAsync(keys); // ✅ 1 network call
```

**Solution 2: Use Pattern-Based Invalidation**

```csharp
// Instead of:
await _cache.RemoveAsync("products:page:1");
await _cache.RemoveAsync("products:page:2");
await _cache.RemoveAsync("products:page:3");
// ... 100 more

// Use:
await _cache.RemovePatternAsync("products:page:*"); // ✅ Single operation
```

---

### Issue 3: Low Hit Rate (< 30%)

**Symptom:** Hit rate lower than expected even for write-heavy system

**Analysis:**
```csharp
// Enable detailed metrics
builder.Services.AddTheTechLoopCacheEffectivenessMetrics();

// Check logs for patterns
// Low hit rate could indicate:
// 1. TTL too short
// 2. Cache keys include timestamps (unique every time)
// 3. Query parameters changing frequently
```

**Solutions:**
1. **Increase TTL slightly** (if data freshness allows)
2. **Normalize cache keys** (remove volatile parameters)
3. **Use canonical keys**

```csharp
// ❌ BAD - timestamp makes key unique every time
var cacheKey = $"product:{id}:{DateTime.Now.Ticks}";

// ✅ GOOD - stable key
var cacheKey = $"product:{id}";
```

---

## 📊 Performance Benchmarks

### Expected Performance (Write-Heavy Workload)

| Metric | Value | Notes |
|--------|-------|-------|
| Hit Rate | 50-70% | Lower than read-heavy (expected) |
| Read Latency (hit) | 1-3ms | L1 + L2 cache |
| Read Latency (miss) | 10-50ms | Database query |
| Write Latency | 5-15ms | DB write + invalidation |
| Invalidation Latency | 1-10ms | Depends on Pub/Sub vs Streams |
| Cross-Instance Propagation | 100ms-2s | Depends on PollInterval |

### Comparison: With vs Without Caching

**Without Caching:**
- All reads hit database: 10-50ms per read
- Database load: 1000 queries/sec
- Database CPU: 80%+

**With Write-Heavy Caching (50% hit rate):**
- 50% reads from cache: 1-3ms
- 50% reads from database: 10-50ms
- Database load: 500 queries/sec (50% reduction)
- Database CPU: 40-50%

**Benefit:** Even with low hit rate, caching reduces database load by ~50%.

---

## 🎓 Summary

### ✅ Key Takeaways

1. **Short TTL** — 1-5 minutes for frequently changing data
2. **Aggressive Invalidation** — Invalidate on every write
3. **Cross-Instance Sync** — Use Streams or Pub/Sub for distributed invalidation
4. **Low Hit Rate OK** — 50-70% is acceptable for write-heavy systems
5. **Database First** — Always write to database before invalidating cache
6. **Fallback Safety** — Handle cache failures gracefully with TTL backup

### ⚠️ When NOT to Use This Pattern

- **Read:Write > 100:1** → Use Scenario #1 (CQRS Multi-Level) instead
- **Strong Consistency Required** → Consider cache-aside or no caching
- **Static Data** → Use Scenario #6 (Cache Warming) instead

### 🎯 Recommended Use Cases

1. **Real-time dashboards** (live data updates)
2. **Collaborative editing** (Google Docs-style apps)
3. **Social media feeds** (activity streams, comments)
4. **E-commerce inventory** (stock levels, pricing)
5. **Chat applications** (messages, presence)
6. **Gaming leaderboards** (real-time rankings)
7. **Booking systems** (seat/room availability)

---

## 🔗 Related Scenarios

- **Scenario #1 (CQRS Multi-Level)** — For read-heavy workloads
- **Scenario #2 (Cache Tagging)** — Bulk invalidation patterns
- **Scenario #5 (Microservices Streams)** — Guaranteed invalidation delivery
- **Scenario #7 (Metrics)** — Monitor invalidation effectiveness

---

## 📚 Additional Resources

- [TheTechLoop.HybridCache README](../README.md)
- [Redis Streams Documentation](https://redis.io/docs/data-types/streams/)
- [Cache Invalidation Strategies](https://learn.microsoft.com/en-us/azure/architecture/patterns/cache-aside)
- [Eventual Consistency Patterns](https://learn.microsoft.com/en-us/azure/architecture/patterns/eventual-consistency)

---

**Version:** 1.0.0  
**Status:** Production-Ready ✅
