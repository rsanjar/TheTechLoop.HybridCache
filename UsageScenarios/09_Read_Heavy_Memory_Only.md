# Scenario 9: Read-Heavy Workload (Memory Cache Only)

## 📋 Overview

This scenario demonstrates using **L1 memory cache only** for applications that don't require distributed caching. Perfect for single-instance deployments, development environments, or when Redis is not available or needed.

### ⚡ Performance Characteristics

- **Read Latency:** < 1ms (in-memory access)
- **Throughput:** 100,000+ ops/sec per core
- **Memory Efficiency:** Native .NET MemoryCache with automatic eviction
- **Best For:** Read:Write ratios > 100:1 in single-instance scenarios

---

## 🎯 When to Use This Scenario

### ✅ Ideal For

- **Development Environments** — Fast iteration without infrastructure
- **Single-Instance Deployments** — Small applications, internal tools
- **Serverless Functions** — AWS Lambda, Azure Functions with warm instances
- **Edge Computing** — IoT devices, edge servers
- **Desktop Applications** — WPF, WinForms, .NET MAUI apps
- **Unit Testing** — Fast, isolated tests without external dependencies
- **Prototypes/MVPs** — Rapid development without Redis setup

### ❌ Not Suitable For

- **Multi-Instance Deployments** — No cache coherence across instances
- **High-Availability Systems** — Cache lost on restart
- **Distributed Microservices** — Can't share cache between services
- **Large Datasets** — Memory constraints (use Redis for > 1GB)

---

## 🏗️ Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    Single Application Instance           │
│                                                          │
│  ┌──────────────┐         ┌─────────────────────────┐  │
│  │  Controller  │ ──────> │   Memory Cache (L1)    │  │
│  │   /Service   │ <────── │   (MemoryCache)        │  │
│  └──────────────┘         │                         │  │
│         │                 │  • Absolute Expiration  │  │
│         │                 │  • Sliding Expiration   │  │
│         ▼                 │  • Size Limits          │  │
│  ┌──────────────┐         │  • Priority Eviction    │  │
│  │   Database   │         └─────────────────────────┘  │
│  │  (EF Core)   │                                       │
│  └──────────────┘                                       │
└─────────────────────────────────────────────────────────┘

Key Characteristics:
• All cache stored in application memory
• No network calls for cache operations
• Cache lost on application restart
• No cross-instance synchronization
```

---

## 🚀 Implementation Guide

### Step 1: Install NuGet Package

```bash
dotnet add package TheTechLoop.HybridCache
```

**Note:** You only need the base package — no Redis libraries required.

---

### Step 2: Configuration (appsettings.json)

```json
{
  "TheTechLoopCache": {
    "MemoryCache": {
      "Enabled": true,
      "SizeLimit": 1024,
      "CompactionPercentage": 0.25,
      "ExpirationScanFrequency": "00:05:00"
    },
    "Redis": {
      "Enabled": false
    },
    "DefaultAbsoluteExpiration": "01:00:00",
    "DefaultSlidingExpiration": null,
    "EnableCompression": false,
    "CompressionThreshold": 1024,
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
| `MemoryCache.Enabled` | `true` | Enable in-memory caching |
| `Redis.Enabled` | `false` | Disable Redis entirely |
| `SizeLimit` | `1024` | Max entries (use with `Size` in options) |
| `CompactionPercentage` | `0.25` | Remove 25% of entries when limit hit |
| `ExpirationScanFrequency` | `00:05:00` | Check for expired entries every 5 min |
| `DefaultAbsoluteExpiration` | `01:00:00` | Default TTL: 1 hour |
| `EnableEffectivenessMetrics` | `true` | Track hit rate (development) |

---

### Step 3: Service Registration (Program.cs)

```csharp
using TheTechLoop.HybridCache.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register memory cache only
builder.Services.AddTheTechLoopCache(builder.Configuration);

// Optional: Add effectiveness metrics for development
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddTheTechLoopCacheEffectivenessMetrics();
}

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

### Step 4: Using ICacheService in Your Code

#### Example 1: Basic Controller with Caching

```csharp
using Microsoft.AspNetCore.Mvc;
using TheTechLoop.HybridCache.Abstractions;

namespace MyApp.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly ICacheService _cache;
    private readonly IProductRepository _repository;
    private readonly ILogger<ProductsController> _logger;

    public ProductsController(
        ICacheService cache,
        IProductRepository repository,
        ILogger<ProductsController> logger)
    {
        _cache = cache;
        _repository = repository;
        _logger = logger;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Product>> GetProduct(int id, CancellationToken cancellationToken)
    {
        var cacheKey = $"product:{id}";
        
        // Try to get from cache
        var cachedProduct = await _cache.GetAsync<Product>(cacheKey, cancellationToken);
        if (cachedProduct != null)
        {
            _logger.LogInformation("Cache HIT for product {ProductId}", id);
            return Ok(cachedProduct);
        }

        _logger.LogInformation("Cache MISS for product {ProductId}", id);

        // Load from database
        var product = await _repository.GetByIdAsync(id, cancellationToken);
        if (product == null)
            return NotFound();

        // Cache for 1 hour
        await _cache.SetAsync(cacheKey, product, new CacheEntryOptions
        {
            AbsoluteExpiration = TimeSpan.FromHours(1),
            Size = 1 // For memory cache size tracking
        }, cancellationToken);

        return Ok(product);
    }

    [HttpGet]
    public async Task<ActionResult<List<Product>>> GetAllProducts(CancellationToken cancellationToken)
    {
        const string cacheKey = "products:all";

        var cachedProducts = await _cache.GetAsync<List<Product>>(cacheKey, cancellationToken);
        if (cachedProducts != null)
        {
            return Ok(cachedProducts);
        }

        var products = await _repository.GetAllAsync(cancellationToken);

        // Cache list for 15 minutes (more volatile)
        await _cache.SetAsync(cacheKey, products, new CacheEntryOptions
        {
            AbsoluteExpiration = TimeSpan.FromMinutes(15),
            Size = products.Count // Size = number of items
        }, cancellationToken);

        return Ok(products);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateProduct(int id, Product product, CancellationToken cancellationToken)
    {
        await _repository.UpdateAsync(product, cancellationToken);

        // Invalidate cache
        await _cache.RemoveAsync($"product:{id}", cancellationToken);
        await _cache.RemoveAsync("products:all", cancellationToken);

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteProduct(int id, CancellationToken cancellationToken)
    {
        await _repository.DeleteAsync(id, cancellationToken);

        // Invalidate cache
        await _cache.RemoveAsync($"product:{id}", cancellationToken);
        await _cache.RemoveAsync("products:all", cancellationToken);

        return NoContent();
    }
}
```

---

#### Example 2: Service Layer with Caching

```csharp
using TheTechLoop.HybridCache.Abstractions;

public class UserService : IUserService
{
    private readonly ICacheService _cache;
    private readonly IUserRepository _repository;

    public UserService(ICacheService cache, IUserRepository repository)
    {
        _cache = cache;
        _repository = repository;
    }

    public async Task<User?> GetUserByIdAsync(int userId, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"user:{userId}";

        return await _cache.GetOrSetAsync(
            key: cacheKey,
            factory: async () => await _repository.GetByIdAsync(userId, cancellationToken),
            options: new CacheEntryOptions
            {
                AbsoluteExpiration = TimeSpan.FromMinutes(30),
                Size = 1,
                Priority = CacheItemPriority.High // Keep important data longer
            },
            cancellationToken: cancellationToken
        );
    }

    public async Task<List<User>> GetUsersByRoleAsync(string role, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"users:role:{role}";

        return await _cache.GetOrSetAsync(
            key: cacheKey,
            factory: async () => await _repository.GetByRoleAsync(role, cancellationToken),
            options: new CacheEntryOptions
            {
                AbsoluteExpiration = TimeSpan.FromMinutes(10),
                Size = 10, // Estimate
                Priority = CacheItemPriority.Normal
            },
            cancellationToken: cancellationToken
        ) ?? new List<User>();
    }

    public async Task UpdateUserAsync(User user, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(user, cancellationToken);

        // Invalidate user-specific cache
        await _cache.RemoveAsync($"user:{user.Id}", cancellationToken);

        // Invalidate role-based lists
        await _cache.RemovePatternAsync("users:role:*", cancellationToken);
    }
}
```

---

#### Example 3: Using Sliding Expiration for Active Data

```csharp
public class SessionService : ISessionService
{
    private readonly ICacheService _cache;

    public SessionService(ICacheService cache)
    {
        _cache = cache;
    }

    public async Task<SessionData?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"session:{sessionId}";

        var session = await _cache.GetAsync<SessionData>(cacheKey, cancellationToken);

        // Sliding window automatically extends on each access
        return session;
    }

    public async Task SetSessionAsync(string sessionId, SessionData data, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"session:{sessionId}";

        await _cache.SetAsync(cacheKey, data, new CacheEntryOptions
        {
            // Session expires after 30 minutes of inactivity
            SlidingExpiration = TimeSpan.FromMinutes(30),
            Priority = CacheItemPriority.High,
            Size = 1
        }, cancellationToken);
    }

    public async Task RemoveSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _cache.RemoveAsync($"session:{sessionId}", cancellationToken);
    }
}
```

---

### Step 5: Cache Warming (Optional)

For reference data, pre-populate cache on startup:

```csharp
public class CacheWarmupHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CacheWarmupHostedService> _logger;

    public CacheWarmupHostedService(IServiceProvider serviceProvider, ILogger<CacheWarmupHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting cache warmup...");

        using var scope = _serviceProvider.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<ICacheService>();
        var repository = scope.ServiceProvider.GetRequiredService<IReferenceDataRepository>();

        // Load countries
        var countries = await repository.GetAllCountriesAsync(cancellationToken);
        await cache.SetAsync("countries:all", countries, new CacheEntryOptions
        {
            AbsoluteExpiration = TimeSpan.FromHours(24),
            Size = countries.Count,
            Priority = CacheItemPriority.NeverRemove // Keep reference data
        }, cancellationToken);

        // Load categories
        var categories = await repository.GetAllCategoriesAsync(cancellationToken);
        await cache.SetAsync("categories:all", categories, new CacheEntryOptions
        {
            AbsoluteExpiration = TimeSpan.FromHours(24),
            Size = categories.Count,
            Priority = CacheItemPriority.NeverRemove
        }, cancellationToken);

        _logger.LogInformation("Cache warmup completed. Loaded {CountryCount} countries, {CategoryCount} categories.",
            countries.Count, categories.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

// Register in Program.cs
builder.Services.AddHostedService<CacheWarmupHostedService>();
```

---

## ⚙️ Advanced Configuration

### Memory Cache Limits

```json
{
  "TheTechLoopCache": {
    "MemoryCache": {
      "Enabled": true,
      "SizeLimit": 2048,
      "CompactionPercentage": 0.20,
      "ExpirationScanFrequency": "00:02:00"
    }
  }
}
```

#### Understanding Size Limits

- **SizeLimit:** Maximum number of "size units" allowed
- **Size in Options:** Each entry's size (e.g., 1 for small object, 100 for list)
- **Compaction:** When limit reached, remove lowest priority items

```csharp
// Small object
await _cache.SetAsync("user:1", user, new CacheEntryOptions { Size = 1 });

// Large collection
await _cache.SetAsync("products:all", products, new CacheEntryOptions { Size = products.Count });
```

### Priority-Based Eviction

```csharp
public enum CacheItemPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    NeverRemove = 3
}

// Reference data - never evict
await _cache.SetAsync("config:app", config, new CacheEntryOptions
{
    Priority = CacheItemPriority.NeverRemove
});

// User data - evict first under pressure
await _cache.SetAsync("temp:data", data, new CacheEntryOptions
{
    Priority = CacheItemPriority.Low
});
```

---

## 📊 Performance Optimization

### 1. Batch Operations

```csharp
public async Task<Dictionary<int, Product>> GetProductsBatchAsync(
    List<int> productIds,
    CancellationToken cancellationToken)
{
    var result = new Dictionary<int, Product>();
    var missingIds = new List<int>();

    // Try cache first
    foreach (var id in productIds)
    {
        var cached = await _cache.GetAsync<Product>($"product:{id}", cancellationToken);
        if (cached != null)
        {
            result[id] = cached;
        }
        else
        {
            missingIds.Add(id);
        }
    }

    // Load missing from database
    if (missingIds.Any())
    {
        var products = await _repository.GetByIdsAsync(missingIds, cancellationToken);
        
        foreach (var product in products)
        {
            result[product.Id] = product;
            
            // Cache individually
            await _cache.SetAsync($"product:{product.Id}", product, new CacheEntryOptions
            {
                AbsoluteExpiration = TimeSpan.FromHours(1),
                Size = 1
            }, cancellationToken);
        }
    }

    return result;
}
```

### 2. Lazy Loading with Task Cache

For expensive computations:

```csharp
private readonly ICacheService _cache;
private readonly SemaphoreSlim _lock = new(1, 1);

public async Task<ExpensiveData> GetExpensiveDataAsync(CancellationToken cancellationToken)
{
    const string cacheKey = "expensive:data";

    var cached = await _cache.GetAsync<ExpensiveData>(cacheKey, cancellationToken);
    if (cached != null)
        return cached;

    // Prevent thundering herd
    await _lock.WaitAsync(cancellationToken);
    try
    {
        // Check again after acquiring lock
        cached = await _cache.GetAsync<ExpensiveData>(cacheKey, cancellationToken);
        if (cached != null)
            return cached;

        // Compute
        var data = await ComputeExpensiveDataAsync(cancellationToken);

        await _cache.SetAsync(cacheKey, data, new CacheEntryOptions
        {
            AbsoluteExpiration = TimeSpan.FromMinutes(30),
            Size = 1,
            Priority = CacheItemPriority.High
        }, cancellationToken);

        return data;
    }
    finally
    {
        _lock.Release();
    }
}
```

---

## 🧪 Testing

### Unit Tests with In-Memory Cache

```csharp
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TheTechLoop.HybridCache.Services;
using Xunit;

public class ProductServiceTests
{
    private ICacheService CreateCacheService()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 100
        });

        var config = Options.Create(new CacheConfiguration
        {
            MemoryCache = new MemoryCacheConfiguration { Enabled = true },
            Redis = new RedisCacheConfiguration { Enabled = false }
        });

        return new MemoryCacheService(memoryCache, config, NullLogger<MemoryCacheService>.Instance);
    }

    [Fact]
    public async Task GetProduct_CachesResult()
    {
        // Arrange
        var cache = CreateCacheService();
        var repository = new Mock<IProductRepository>();
        var service = new ProductService(cache, repository.Object);

        var product = new Product { Id = 1, Name = "Test" };
        repository.Setup(r => r.GetByIdAsync(1, default)).ReturnsAsync(product);

        // Act
        var result1 = await service.GetProductByIdAsync(1);
        var result2 = await service.GetProductByIdAsync(1);

        // Assert
        Assert.Equal(product.Name, result1?.Name);
        Assert.Equal(product.Name, result2?.Name);
        repository.Verify(r => r.GetByIdAsync(1, default), Times.Once); // Only called once
    }

    [Fact]
    public async Task UpdateProduct_InvalidatesCache()
    {
        // Arrange
        var cache = CreateCacheService();
        var repository = new Mock<IProductRepository>();
        var service = new ProductService(cache, repository.Object);

        var product = new Product { Id = 1, Name = "Original" };
        repository.Setup(r => r.GetByIdAsync(1, default)).ReturnsAsync(product);

        // Cache the product
        await service.GetProductByIdAsync(1);

        // Act - Update
        product.Name = "Updated";
        await service.UpdateProductAsync(product);

        // Verify cache was cleared
        var cached = await cache.GetAsync<Product>("product:1");
        Assert.Null(cached);
    }
}
```

---

## 📈 Monitoring & Metrics

### Enable Effectiveness Metrics

```csharp
// Program.cs
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddTheTechLoopCacheEffectivenessMetrics();
}
```

### View Metrics in Logs

```
[Information] TheTechLoop.HybridCache.Metrics.CacheEffectivenessMetrics: 
Cache Effectiveness Metrics:
  product:* - Hit Rate: 92.5% (370/400 requests), Avg Latency: 0.8ms
  users:* - Hit Rate: 85.3% (171/200 requests), Avg Latency: 0.5ms
  categories:all - Hit Rate: 100.0% (50/50 requests), Avg Latency: 0.3ms
```

### Custom Metrics Endpoint

```csharp
[ApiController]
[Route("api/[controller]")]
public class DiagnosticsController : ControllerBase
{
    private readonly CacheEffectivenessMetrics _metrics;

    public DiagnosticsController(CacheEffectivenessMetrics metrics)
    {
        _metrics = metrics;
    }

    [HttpGet("cache-stats")]
    public ActionResult GetCacheStats()
    {
        var stats = _metrics.GetMetrics();
        return Ok(stats.Select(m => new
        {
            m.KeyPattern,
            HitRate = $"{m.HitRate:P2}",
            m.Hits,
            m.Misses,
            TotalRequests = m.Hits + m.Misses,
            AvgLatency = $"{m.AverageLatency.TotalMilliseconds:F2}ms"
        }));
    }
}
```

---

## 🎯 Best Practices

### 1. Cache Key Conventions

```csharp
// Use consistent naming patterns
"entity:id"           → "product:123"
"entity:all"          → "products:all"
"entity:filter:value" → "products:category:electronics"
"user:id:property"    → "user:123:preferences"
```

### 2. TTL Strategy

```csharp
// Static reference data - 24 hours
TimeSpan.FromHours(24)

// User data - 30 minutes
TimeSpan.FromMinutes(30)

// Frequently changing data - 5 minutes
TimeSpan.FromMinutes(5)

// Session data - 30 minutes sliding
SlidingExpiration = TimeSpan.FromMinutes(30)
```

### 3. Size Management

```csharp
// Assign size based on data complexity
await _cache.SetAsync(cacheKey, data, new CacheEntryOptions
{
    Size = 1,  // Single small object
    // Size = 10,  // Medium object or small collection
    // Size = 100, // Large collection
    Priority = CacheItemPriority.Normal
});
```

### 4. Invalidation Patterns

```csharp
// Invalidate single item
await _cache.RemoveAsync($"product:{productId}");

// Invalidate related items
await _cache.RemoveAsync($"product:{productId}");
await _cache.RemoveAsync("products:all");
await _cache.RemovePatternAsync($"products:category:*");
```

### 5. Error Handling

```csharp
public async Task<Product?> GetProductSafeAsync(int id)
{
    try
    {
        var cacheKey = $"product:{id}";
        var cached = await _cache.GetAsync<Product>(cacheKey);
        if (cached != null)
            return cached;

        var product = await _repository.GetByIdAsync(id);
        if (product != null)
        {
            await _cache.SetAsync(cacheKey, product, new CacheEntryOptions
            {
                AbsoluteExpiration = TimeSpan.FromHours(1),
                Size = 1
            });
        }

        return product;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Cache operation failed for product {ProductId}", id);
        // Fallback to database
        return await _repository.GetByIdAsync(id);
    }
}
```

---

## 🔧 Troubleshooting

### Issue 1: Memory Usage Too High

**Symptom:** Application memory grows continuously

**Solution:**

```json
{
  "TheTechLoopCache": {
    "MemoryCache": {
      "SizeLimit": 1024,  // Reduce limit
      "CompactionPercentage": 0.30  // More aggressive compaction
    }
  }
}
```

### Issue 2: Cache Not Evicting Old Entries

**Symptom:** Expired entries remain in memory

**Solution:**

```json
{
  "TheTechLoopCache": {
    "MemoryCache": {
      "ExpirationScanFrequency": "00:01:00"  // Scan every minute
    }
  }
}
```

### Issue 3: Low Hit Rate

**Symptom:** Most requests miss cache

**Check:**
1. TTL too short?
2. Cache keys changing?
3. Data too large (exceeding size limit)?

**Solution:**

```csharp
// Increase TTL
AbsoluteExpiration = TimeSpan.FromHours(2)

// Use consistent keys
var cacheKey = $"product:{id}"; // Not $"product:{id}:{DateTime.Now}"

// Increase size limit or reduce entry sizes
```

---

## 🚀 Production Readiness

### Health Checks

```csharp
builder.Services.AddHealthChecks()
    .AddCheck("cache", () =>
    {
        var cache = builder.Services.BuildServiceProvider().GetRequiredService<ICacheService>();
        // Simple test
        var testKey = "health:check";
        cache.SetAsync(testKey, "ok", new CacheEntryOptions { AbsoluteExpiration = TimeSpan.FromSeconds(10) });
        var value = cache.GetAsync<string>(testKey);
        return value != null ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded();
    });

app.MapHealthChecks("/health");
```

### Logging

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "TheTechLoop.HybridCache": "Debug"
    }
  }
}
```

---

## 📊 Performance Benchmarks

### Expected Performance (Single Core)

| Operation | Latency | Throughput |
|-----------|---------|------------|
| Get (hit) | 0.5-1ms | 100K+ ops/sec |
| Get (miss) | 0.5-1ms | 100K+ ops/sec |
| Set | 1-2ms | 50K+ ops/sec |
| Remove | 0.5-1ms | 100K+ ops/sec |

### Memory Overhead

- **Per Entry:** ~200-500 bytes (metadata)
- **1,000 entries:** ~1-2 MB
- **10,000 entries:** ~10-20 MB
- **100,000 entries:** ~100-200 MB

---

## 🎓 Summary

### ✅ Advantages

- **Ultra-Fast:** < 1ms latency for all operations
- **No Infrastructure:** No Redis/external dependencies
- **Simple Setup:** Minimal configuration
- **Cost-Free:** No hosting/bandwidth costs
- **Perfect for Dev:** Fast iteration, no setup

### ⚠️ Limitations

- **Single Instance Only:** No distributed cache
- **Lost on Restart:** Cache clears on app restart
- **Memory Constrained:** Limited by application memory
- **No Cross-Service Sharing:** Each app has its own cache

### 🎯 Recommended Use Cases

1. **Development environments** (fastest iteration)
2. **Single-instance production apps** (internal tools, admin panels)
3. **Desktop applications** (WPF, WinForms, MAUI)
4. **Serverless functions** (AWS Lambda, Azure Functions)
5. **Edge computing** (IoT, edge servers)
6. **Unit testing** (fast, isolated tests)

---

## 🔗 Related Scenarios

- **Scenario #1 (CQRS Multi-Level)** — Add L2 Redis for distributed caching
- **Scenario #3 (Session Management)** — Perfect for single-instance sessions
- **Scenario #6 (Cache Warming)** — Pre-populate cache on startup
- **Scenario #7 (Metrics)** — Monitor hit rates and optimize TTL

---

## 📚 Additional Resources

- [TheTechLoop.HybridCache README](../README.md)
- [Microsoft.Extensions.Caching.Memory Documentation](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/memory)
- [.NET Memory Cache Best Practices](https://learn.microsoft.com/en-us/dotnet/core/extensions/caching)

---


**Version:** 1.0.0  
**Status:** Production-Ready ✅
