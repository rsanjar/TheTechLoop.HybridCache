# TheTechLoop.HybridCache

Enterprise-grade distributed Redis caching library for .NET microservices with production-ready features for high-performance, scalable applications.

[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![Redis](https://img.shields.io/badge/Redis-7.0+-DC382D)](https://redis.io/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

> 📚 **Check out the `/UsageScenarios` folder** for comprehensive real-world examples including CQRS with MediatR, multi-level caching, cache tagging, compression, Redis Streams, and more.
>
> <b>!! NOTE: CORA.Organization is a fictional company used for demonstration purposes in this library.</b>

---

## ✨ Key Features

### Core Caching
- **Multi-Level Caching** — L1 in-memory + L2 Redis for optimal latency (1-5ms reads)
- **Distributed Locking** — Prevent cache stampede with Redis-based locks
- **Circuit Breaker** — Lock-free graceful degradation when Redis is unavailable (atomic `Interlocked` operations)
- **Service-Scoped Keys** — Automatic key prefixing per microservice
- **Cache Versioning** — Bump version on breaking DTO changes

### Advanced Features
- **Cache Tagging** — Group and invalidate related cache entries with Redis Sets (O(1) operations)
- **Cache Warming** — Pre-load reference data on startup for zero cold-start latency
- **Compression** — Automatic GZip compression for large payloads (60-80% memory savings)
- **Sliding Expiration** — Auto-extend cache lifetime on each access (perfect for sessions)
- **Redis Streams** — Guaranteed cache invalidation delivery across microservices (no message loss)

### Invalidation & Coherence
- **Pub/Sub Invalidation** — Cross-service cache invalidation via Redis channels
- **Bulk Invalidation** — Invalidate by prefix pattern or tags (e.g., all user data at once)
- **Automatic Invalidation** — Convention-based invalidation via `ICacheInvalidatable` marker

### Integration & Observability
- **CQRS-Optimized** — Read-through caching with write-through invalidation
- **MediatR Pipeline Behavior** — Convention-based caching via `ICacheable` marker
- **OpenTelemetry Metrics** — Built-in hit/miss/duration/size metrics per entity type
- **Effectiveness Tracking** — Per-entity cache hit rate analysis for optimization

### Performance & Reliability
- **10-50x Performance Improvement** — Typical read latency: < 5ms (vs 50-200ms database queries)
- **Memory-Only Mode** — Zero-dependency local cache (`UseMemoryOnly: true`) — no Redis required
- **High Availability** — Automatic Redis reconnection with exponential backoff
- **Thread-Safe** — Concurrent-safe operations with minimal lock contention
- **Production-Ready** — Battle-tested in enterprise microservices environments

---

## 📦 Installation

```bash
dotnet add package TheTechLoop.HybridCache
```

Or via project reference:

```xml
<ProjectReference Include="..\TheTechLoop.HybridCache\TheTechLoop.HybridCache.csproj" />
```

**Requirements:**
- .NET 10 or higher
- Redis 6.0+ (7.0+ recommended for Streams) — *not required when `UseMemoryOnly: true`*
- StackExchange.Redis 2.11+

---

## 🚀 Quick Start

### 1. Register Services

```csharp
// Program.cs
builder.Services.AddTheTechLoopCache(builder.Configuration);

// Optional: Enable cross-service invalidation via Redis Pub/Sub
builder.Services.AddTheTechLoopCacheInvalidation();

// Optional: Multi-level caching (L1 Memory + L2 Redis)
builder.Services.AddTheTechLoopMultiLevelCache(builder.Configuration);

// Alternative: Memory-only mode — no Redis required
// builder.Services.AddTheTechLoopCache(builder.Configuration); // with UseMemoryOnly: true
```

### 2. Configuration (appsettings.json)

```json
{
  "TheTechLoopCache": {
    "Configuration": "localhost:6379,password=yourpassword,defaultDatabase=0,ssl=false,abortConnect=false",
    "InstanceName": "TheTechLoop:Company:",
    "ServiceName": "company-svc",
    "CacheVersion": "v1",
    "DefaultExpirationMinutes": 60,
    "EnableLogging": true,
    "Enabled": true,

    "UseMemoryOnly": false,

    "InvalidationChannel": "cache:invalidation",

    "CircuitBreaker": {
      "Enabled": true,
      "BreakDurationSeconds": 60,
      "FailureThreshold": 5,
      "HalfOpenSuccessThreshold": 1
    },

    "MemoryCache": {
      "Enabled": true,
      "DefaultExpirationSeconds": 30,
      "SizeLimit": 1024
    },

    "EnableTagging": false,
    "EnableCompression": false,
    "CompressionThresholdBytes": 1024,
    "EnableEffectivenessMetrics": false,
    "UseStreamsForInvalidation": false,
    "EnableWarmup": false
  }
}
```

**Configuration Options Explained:**

| Option | Description | Default |
|--------|-------------|--------|
| `Configuration` | Redis connection string — not required when `UseMemoryOnly: true` | Required* |
| `ServiceName` | Unique name for your microservice (used in key prefixes) | `""` |
| `InstanceName` | Global prefix for all cache keys — not required when `UseMemoryOnly: true` | Required* |
| `CacheVersion` | Version for cache keys (bump to invalidate all) | `"v1"` |
| `Enabled` | Master switch to enable/disable caching | `true` |
| `UseMemoryOnly` | In-process `IMemoryCache` only — no Redis, no lock, no network | `false` |
| `EnableTagging` | Enable cache tagging for bulk invalidation | `false` |
| `EnableCompression` | Auto-compress values > threshold | `false` |
| `EnableEffectivenessMetrics` | Track per-entity hit rates | `false` |
| `UseStreamsForInvalidation` | Use Redis Streams instead of Pub/Sub | `false` |
| `EnableWarmup` | Pre-load cache on startup | `false` |

\* Required only when `UseMemoryOnly: false` and `Enabled: true`.

---

## 📋 Usage Scenarios

TheTechLoop.HybridCache supports 10 comprehensive usage scenarios. Visit the `/UsageScenarios` folder for detailed documentation with complete code examples.

### Quick Selection Guide

| Scenario | Best For | Key Features |
|----------|----------|--------------|
| **[01 - CQRS Multi-Level Cache](UsageScenarios/01_CQRS_MultiLevel_Cache.md)** ⭐ | Microservices with MediatR, high read-to-write ratio | L1+L2 cache, automatic caching/invalidation, 10-50x performance |
| **[02 - Cache Tagging](UsageScenarios/02_Cache_Tagging_Bulk_Invalidation.md)** | Complex invalidation (e.g., user logout) | Bulk invalidation, Redis Sets, O(1) tag queries |
| **[03 - Session Management](UsageScenarios/03_Session_Sliding_Expiration.md)** | User sessions, shopping carts | Sliding expiration, auto-extend on access |
| **[04 - Compression](UsageScenarios/04_High_Volume_Compression.md)** | Large payloads, bandwidth-constrained | GZip compression, 60-80% memory savings |
| **[05 - Microservices Streams](UsageScenarios/05_Microservices_Streams.md)** | Mission-critical invalidation | Redis Streams, guaranteed delivery, no message loss |
| **[06 - Cache Warming](UsageScenarios/06_Reference_Data_Warming.md)** | Static reference data | Pre-load on startup, zero cold-start latency |
| **[07 - Performance Metrics](UsageScenarios/07_Performance_Metrics.md)** | Data-driven optimization | Per-entity hit rates, latency tracking, OpenTelemetry |
| **[08 - Simple REST API](UsageScenarios/08_Simple_REST_API.md)** | Simple APIs without CQRS | Single-level cache, minimal setup |
| **[09 - Memory Only](UsageScenarios/09_Read_Heavy_Memory_Only.md)** | Single-instance apps, development | L1 cache only, no Redis dependency |
| **[10 - Write-Heavy](UsageScenarios/10_Write_Heavy_Invalidation.md)** | Frequent updates, real-time systems | Aggressive invalidation, short TTL |

### Selection by Architecture

- **CQRS + MediatR:** Use Scenario #1 (CQRS Multi-Level Cache)
- **Simple REST API:** Use Scenario #8 (Simple REST API)
- **Microservices:** Use Scenario #5 (Microservices Streams)
- **Monolith:** Use Scenario #9 (Memory Only)

### Selection by Feature Need

- **Session management:** Scenario #3
- **Large payloads:** Scenario #4
- **Bulk invalidation:** Scenario #2
- **Static data:** Scenario #6
- **Performance analysis:** Scenario #7

---

## 🏗️ Architecture with CQRS + MediatR

### Overview

```
Controller → MediatR → CachingBehavior → QueryHandler → ReadRepository → DB
                                                           ↑
                                                     ICacheService
                                                   (read-through)

Controller → MediatR → CommandHandler → WriteRepository → UnitOfWork → DB
                                              ↓
                                        ICacheService (invalidate)
                                              ↓
                                  ICacheInvalidationPublisher (Pub/Sub)
```

### Data Layer: Read/Write Repositories + UnitOfWork

```csharp
// Read-only repository (CQRS query-side). Uses AsNoTracking for performance.
public interface IReadRepository<TEntity> where TEntity : class
{
    IQueryable<TEntity> Query { get; } // AsNoTracking
    Task<TEntity?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<List<TEntity>> GetAllAsync(CancellationToken ct = default);
}

// Write repository (CQRS command-side). Tracked by EF.
public interface IWriteRepository<TEntity> where TEntity : class
{
    Task AddAsync(TEntity entity, CancellationToken ct = default);
    void Update(TEntity entity);
    void Remove(TEntity entity);
    Task<TEntity?> GetByIdAsync(int id, CancellationToken ct = default);
}

// Commits all pending changes from write repositories.
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
```

### Query Handler — Cache on the Read Path

```csharp
public class GetDealershipByIdQueryHandler : IRequestHandler<GetDealershipByIdQuery, Dealership?>
{
    private readonly IReadRepository<Data.Models.Dealership> _repository;
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;
    private readonly IMapper _mapper;

    public async Task<Dealership?> Handle(GetDealershipByIdQuery request, CancellationToken ct)
    {
        var cacheKey = _keyBuilder.Key("Dealership", request.Id.ToString());

        return await _cache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                var entity = await _repository.Query
                    .Include(d => d.BusinessZipCode)
                    .FirstOrDefaultAsync(d => d.ID == request.Id, ct);

                return entity is null ? null : _mapper.Map<Dealership>(entity);
            },
            TimeSpan.FromMinutes(30),
            ct);
    }
}
```

### Command Handler — Invalidate on the Write Path

```csharp
public class UpdateDealershipCommandHandler : IRequestHandler<UpdateDealershipCommand, bool>
{
    private readonly IWriteRepository<Data.Models.Dealership> _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICacheService _cache;
    private readonly ICacheInvalidationPublisher _invalidation;
    private readonly CacheKeyBuilder _keyBuilder;

    public async Task<bool> Handle(UpdateDealershipCommand request, CancellationToken ct)
    {
        var entity = await _repository.GetByIdAsync(request.Id, ct);
        if (entity is null) return false;

        entity.Name = request.Name;
        entity.BusinessAddress = request.BusinessAddress;

        _repository.Update(entity);
        await _unitOfWork.SaveChangesAsync(ct);

        // Invalidate specific entity cache
        var entityKey = _keyBuilder.Key("Dealership", request.Id.ToString());
        await _cache.RemoveAsync(entityKey, ct);

        // Invalidate search results (prefix pattern)
        var searchPattern = _keyBuilder.Key("Dealership", "Search");
        await _cache.RemoveByPrefixAsync(searchPattern, ct);

        // Notify OTHER microservice instances via Pub/Sub
        await _invalidation.PublishAsync(entityKey, ct);
        await _invalidation.PublishPrefixAsync(searchPattern, ct);

        return true;
    }
}
```

---

## 🎯 Advanced Features

### Multi-Level Caching (L1 + L2)

Combine in-memory (L1) and Redis (L2) for optimal performance:

```csharp
// Program.cs
builder.Services.AddTheTechLoopMultiLevelCache(builder.Configuration);

// Configuration
{
  "TheTechLoopCache": {
    "MemoryCache": {
      "Enabled": true,
      "DefaultExpirationSeconds": 30,
      "SizeLimit": 1024
    }
  }
}
```

**Performance:**
- L1 hit: < 1ms (in-process memory)
- L2 hit: 1-5ms (Redis network call)
- Database: 50-200ms

### Cache Tagging for Bulk Invalidation

Group related cache entries and invalidate them together:

```csharp
// Enable in configuration
{
  "TheTechLoopCache": {
    "EnableTagging": true
  }
}

// Usage
var options = CacheEntryOptions.Absolute(
    TimeSpan.FromHours(2),
    "User",                    // Generic user tag
    $"User:{user.ID}",        // Specific user tag
    "Session"                  // Session tag
);

await _cache.SetAsync(profileKey, user, options);

// Invalidate all user data with one call
await _tagService.RemoveByTagAsync($"User:{userId}");
```

**Use Cases:**
- User logout (invalidate all user sessions + preferences + permissions)
- Role change (invalidate user permissions + menu access)
- Company update (invalidate company + dealerships + employees)

### Compression for Large Payloads

Automatically compress cache values larger than threshold:

```csharp
// Configuration
{
  "TheTechLoopCache": {
    "EnableCompression": true,
    "CompressionThresholdBytes": 1024  // Compress values > 1KB
  }
}

// Automatic compression - no code changes needed!
var company = await _cache.GetOrCreateAsync(
    cacheKey,
    async () => await GetCompanyWithAllDetails(id),
    TimeSpan.FromHours(2));
// 500KB → 150KB (70% savings)
```

**Benefits:**
- 60-80% memory savings for JSON payloads
- Reduced network bandwidth
- Transparent compression/decompression
- Small CPU overhead (+2ms for 10KB data)

### Redis Streams for Guaranteed Invalidation

Use Redis Streams instead of Pub/Sub for mission-critical invalidation:

```csharp
// Configuration
{
  "TheTechLoopCache": {
    "UseStreamsForInvalidation": true
  }
}

// Same API - guaranteed delivery
await _invalidation.PublishAsync(key);
```

**Streams vs Pub/Sub:**
| Feature | Pub/Sub | Streams |
|---------|---------|---------|
| Delivery | Fire-and-forget | Guaranteed |
| Persistence | No | Yes (until ACK) |
| Consumer Offline | Message lost | Message queued |
| Acknowledgment | No | Required |
| Production Use | Dev/Staging | Production |

### Cache Warming for Zero Cold-Start

Pre-load reference data on application startup:

```csharp
// Program.cs
builder.Services.AddTheTechLoopCacheWarmup();
builder.Services.AddTransient<ICacheWarmupStrategy, GeoDataWarmupStrategy>();

// Configuration
{
  "TheTechLoopCache": {
    "EnableWarmup": true
  }
}

// Strategy implementation
public class GeoDataWarmupStrategy : ICacheWarmupStrategy
{
    public async Task WarmupAsync(ICacheService cache, CancellationToken ct)
    {
        var countries = await _repository.GetAllCountriesAsync(ct);
        foreach (var country in countries)
        {
            var key = _keyBuilder.Key("Country", country.ID.ToString());
            await cache.SetAsync(key, country, TimeSpan.FromHours(24), ct);
        }
    }
}
```

**Benefits:**
- First request: 0ms cache miss (data already cached)
- Zero cold-start latency
- 99.9%+ cache hit rate for reference data

### Performance Metrics and Effectiveness Tracking

Track cache performance per entity type:

```csharp
// Configuration
{
  "TheTechLoopCache": {
    "EnableEffectivenessMetrics": true
  }
}

// Automatic metrics collection
// Query cache statistics
GET /api/cache/stats

{
  "Company": {
    "hits": 1420,
    "misses": 180,
    "hitRate": 0.8875,  // 88.75%
    "avgLatencyMs": 2.3
  },
  "Country": {
    "hits": 4520,
    "misses": 8,
    "hitRate": 0.9982,  // 99.82% - Excellent!
    "avgLatencyMs": 0.8
  }
}
```

**Use Cases:**
- Identify which entities benefit most from caching
- Optimize TTL values based on hit rates
- Discover caching candidates (low hit rate = bad candidate)
- Capacity planning with size tracking

### Sliding Expiration for Sessions

Auto-extend cache lifetime on each access:

```csharp
var options = CacheEntryOptions.Sliding(TimeSpan.FromMinutes(30));
await _cache.SetAsync(sessionKey, sessionData, options);

// Each access extends the TTL by 30 minutes
await _cache.GetAsync<SessionData>(sessionKey);
```

**Perfect for:**
- User login sessions
- Shopping cart persistence
- Temporary form data
- User activity tracking

---

## 🔌 MediatR Pipeline Behavior — Auto-Cache for Queries

Eliminate cache boilerplate with convention-based caching:

### ICacheable Marker Interface

```csharp
public interface ICacheable
{
    string CacheKey { get; }
    TimeSpan CacheDuration { get; }
}
```

### CachingBehavior

```csharp
public class CachingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        if (request is not ICacheable cacheable)
            return await next(ct);

        var scopedKey = _keyBuilder.Key(cacheable.CacheKey);

        return await _cache.GetOrCreateAsync(
            scopedKey,
            async () => await next(ct),
            cacheable.CacheDuration,
            ct);
    }
}
```

### Usage — Handler Stays Pure

```csharp
// Query declares cache behavior
public record GetDealershipByIdQuery(int Id) : IRequest<Dealership?>, ICacheable
{
    public string CacheKey => $"Dealership:{Id}";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(30);
}

// Handler has ZERO cache logic - pure data access
public class GetDealershipByIdQueryHandler : IRequestHandler<GetDealershipByIdQuery, Dealership?>
{
    private readonly IReadRepository<Data.Models.Dealership> _repository;
    private readonly IMapper _mapper;

    public async Task<Dealership?> Handle(GetDealershipByIdQuery request, CancellationToken ct)
    {
        var entity = await _repository.Query
            .Include(d => d.BusinessZipCode)
            .FirstOrDefaultAsync(d => d.ID == request.Id, ct);

        return entity is null ? null : _mapper.Map<Dealership>(entity);
    }
}
```

---

## 🔧 API Reference

### ICacheService

```csharp
public interface ICacheService
{
    // Get or create with factory (stampede-protected)
    Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiration, CancellationToken ct = default);

    // Direct get
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);

    // Direct set
    Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CacheEntryOptions options, CancellationToken ct = default);

    // Bulk operations
    Task<Dictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys, CancellationToken ct = default);
    Task SetManyAsync<T>(Dictionary<string, T> items, TimeSpan? expiration = null, CancellationToken ct = default);

    // Remove operations
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task RemoveByPrefixAsync(string keyPrefix, CancellationToken ct = default);
    Task RefreshAsync(string key, CancellationToken ct = default);
}
```

### CacheKeyBuilder

```csharp
// Injected instance (service-scoped, versioned)
var key = _keyBuilder.Key("Dealership", "42");
// → "company-svc:v1:Dealership:42"

var pattern = _keyBuilder.Pattern("Dealership", "Search");
// → "company-svc:v1:Dealership:Search*"

// Static helpers (no service scope)
var sharedKey = CacheKeyBuilder.For("shared", "config");
var entityKey = CacheKeyBuilder.ForEntity("User", 42);
var sanitized = CacheKeyBuilder.Sanitize("hello world/test");
```

### CacheEntryOptions

```csharp
// Absolute expiration
var options = CacheEntryOptions.Absolute(TimeSpan.FromHours(1));

// Sliding expiration
var options = CacheEntryOptions.Sliding(TimeSpan.FromMinutes(30));

// With tags
var options = CacheEntryOptions.Absolute(
    TimeSpan.FromHours(2),
    "User", $"User:{userId}", "Session"
);
```

---

## 📊 OpenTelemetry Metrics

All metrics are recorded automatically. No manual instrumentation needed.

### Built-in Metrics

**Meter: `TheTechLoop.Cache`** (core operations)

| Metric | Type | Tags | Description |
|--------|------|------|-------------|
| `cache.hits` | Counter | `cache.key_prefix`, `cache.level` | Total cache hits |
| `cache.misses` | Counter | `cache.key_prefix` | Total cache misses |
| `cache.errors` | Counter | `cache.key_prefix` | Redis exceptions |
| `cache.evictions` | Counter | `cache.key_prefix` | Explicit removals |
| `cache.circuit_breaker.bypasses` | Counter | — | Requests bypassed due to open circuit |
| `cache.duration` | Histogram (ms) | `cache.operation`, `cache.level` | Operation latency |
| `cache.lock.wait_duration` | Histogram (ms) | `cache.lock.acquired` | Stampede-lock wait time |
| `cache.batch.size` | Histogram (keys) | `cache.operation` | Keys per `GetManyAsync`/`SetManyAsync` call |
| `cache.scan.duration` | Histogram (ms) | — | Prefix SCAN deletion duration |
| `cache.scan.deleted_keys` | Counter | — | Keys removed by SCAN invalidation |

**Meter: `TheTechLoop.Cache.Effectiveness`** (per-entity tracking, requires `EnableEffectivenessMetrics: true`)

| Metric | Type | Tags | Description |
|--------|------|------|-------------|
| `cache.entity.hits` | Counter | `entity` | Cache hits per entity type |
| `cache.entity.misses` | Counter | `entity` | Cache misses per entity type |
| `cache.entity.latency` | Histogram (ms) | `entity` | Access latency per entity |
| `cache.entity.size` | Histogram (bytes) | `entity` | Cached payload size per entity |
| `cache.entity.hit_rate` | Gauge (ratio) | `entity` | Live hit rate per entity type |

### Setup — Prometheus

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter("TheTechLoop.Cache");
        metrics.AddMeter("TheTechLoop.Cache.Effectiveness"); // if EnableEffectivenessMetrics: true
        metrics.AddPrometheusExporter();
    });

app.MapPrometheusScrapingEndpoint("/metrics");
```

### CLI — dotnet-counters

```bash
dotnet counters monitor --process-id <PID> --counters TheTechLoop.Cache

[TheTechLoop.Cache]
    cache.hits (Count / 1 sec)                12
    cache.misses (Count / 1 sec)               3
    cache.circuit_breaker.bypasses (Count)     0
    cache.duration (ms) P50                 0.45
    cache.duration (ms) P95                 2.10
    cache.lock.wait_duration (ms) P95        8.0
    cache.batch.size ({keys}) P99            500
    cache.scan.duration (ms) P99            42.0
    cache.scan.deleted_keys (Count / 1 sec)    0
```

---

## 💡 Best Practices

### Cache TTL Guidelines

| Data Type | TTL | Example |
|-----------|-----|---------|
| Static reference data | 6–10 hours | Countries, states, positions |
| Entity by ID | 15–30 minutes | Dealership, User, Company |
| Search / list results | 3–5 minutes | Search results, paginated lists |
| User session data | 1–5 minutes | Active user profile |
| Frequently mutated data | 30–60 seconds | Real-time counters, presence |

### Rules of Thumb

| Rule | Why |
|------|-----|
| Cache only in Query Handlers | Reads benefit from cache; writes must always hit DB |
| Invalidate only in Command Handlers | After `UnitOfWork.SaveChangesAsync` succeeds |
| ReadRepository uses `AsNoTracking` | No EF change tracking overhead on cached reads |
| WriteRepository is tracked | EF change tracking needed for updates |
| Use `ICacheable` marker | Eliminates cache boilerplate in every handler |
| Short TTL for search, long for by-ID | Search results change frequently |
| Bump `CacheVersion` on breaking DTO changes | Old cache entries are automatically ignored |
| Always fall back to DB on cache errors | Cache is an optimization, not a dependency |

### Data Flow

```
READ PATH (Query)
  Controller
    → MediatR.Send(Query)
      → CachingBehavior
        → ICacheService.GetOrCreateAsync()
          → [Cache Hit] Return cached value
          → [Cache Miss] → QueryHandler → Database → Cache → Return

WRITE PATH (Command)
  Controller
    → MediatR.Send(Command)
      → CommandHandler
        → WriteRepository.Update()
        → UnitOfWork.SaveChangesAsync()
        → ICacheService.RemoveAsync()
        → ICacheInvalidationPublisher.PublishAsync() ← notify other instances
```

---

## 🗂️ Project Structure

```
TheTechLoop.HybridCache/
├── Abstractions/
│   ├── ICacheService.cs                    # Core cache contract
│   ├── ICacheInvalidationPublisher.cs      # Cross-service Pub/Sub contract
│   ├── IDistributedLock.cs                 # Stampede-prevention lock contract
│   └── CacheEntryOptions.cs               # Absolute/sliding expiration options + tags
├── Compression/
│   └── CompressedCacheService.cs          # ICacheService decorator: transparent GZip
├── Configuration/
│   └── CacheConfig.cs                     # Full configuration model
├── Extensions/
│   └── CacheServiceCollectionExtensions.cs # DI registration
├── Keys/
│   └── CacheKeyBuilder.cs                 # Service-scoped, versioned keys + sanitization
├── Metrics/
│   ├── CacheMetrics.cs                    # OpenTelemetry counters/histograms (10 instruments)
│   └── CacheEffectivenessMetrics.cs       # Per-entity hit rate / latency / size tracking
├── Serialization/
│   └── CacheJsonOptions.cs                # Resilient JSON options (null, enum, camelCase)
├── Services/
│   ├── RedisCacheService.cs               # Core Redis implementation
│   ├── MultiLevelCacheService.cs          # L1 Memory + L2 Redis
│   ├── RedisDistributedLock.cs            # Redis SET NX distributed lock
│   ├── RedisCacheInvalidationPublisher.cs # Pub/Sub key & prefix publisher
│   ├── CacheInvalidationSubscriber.cs     # Background Pub/Sub consumer
│   ├── CircuitBreakerState.cs             # Lock-free circuit breaker (Interlocked)
│   └── NoOpCacheService.cs               # No-op implementation when disabled
├── Streams/
│   └── CacheInvalidationStreamConsumer.cs # Redis Streams consumer + stream publisher
├── Tagging/
│   └── RedisCacheTagService.cs            # ICacheTagService + Redis Sets implementation
├── Warming/
│   └── CacheWarmupService.cs              # ICacheWarmupStrategy + background warmup
└── TheTechLoop.HybridCache.csproj

TheTechLoop.HybridCache.MediatR/
├── Abstractions/
│   ├── ICacheable.cs                      # Marker for auto-cached queries
│   └── ICacheInvalidatable.cs             # Marker for auto-invalidating commands
├── Behaviors/
│   ├── CachingBehavior.cs                 # MediatR read-path auto-cache behavior
│   └── CacheInvalidationBehavior.cs       # MediatR write-path auto-invalidate behavior
├── Extensions/
│   └── MediatRCacheServiceCollectionExtensions.cs # AddTheTechLoopCacheBehaviors()
└── TheTechLoop.HybridCache.MediatR.csproj
```

---

## 📚 Additional Resources

- **[/UsageScenarios](UsageScenarios/)** — 10 comprehensive usage scenarios with complete examples
- **[Summary.md](UsageScenarios/Summary.md)** — Quick reference guide for all scenarios
- **[01_CQRS_MultiLevel_Cache.md](UsageScenarios/01_CQRS_MultiLevel_Cache.md)** ⭐ Most popular scenario

---

## 🚀 Performance

**Typical Results:**
- Database query: 50-200ms
- Redis cache hit: 1-5ms
- Memory cache hit: < 1ms
- **10-50x performance improvement** for read-heavy workloads

**Compression:**
- 60-80% memory savings for JSON payloads
- 70% faster network transfer for large payloads

**Cache Hit Rates:**
- Reference data: 99.9%+
- Entity by ID: 85-95%
- Search results: 70-85%

---

## 🤝 Contributing

Contributions are welcome! Please open an issue or submit a pull request.

---

## 📄 License

MIT License - see [LICENSE](LICENSE) file for details.

---

## 🆘 Support

For questions or issues:
1. Check the troubleshooting section in relevant usage scenario
2. Review this README and configuration options
3. Open an issue on GitHub

---

## 📝 What's New in v1.4.0

- **`UseMemoryOnly` mode** — Set `UseMemoryOnly: true` to activate `MemoryOnlyCacheService`, a full `ICacheService` backed solely by `IMemoryCache`. No Redis, no distributed lock, no network dependency. Ideal for development, unit tests, serverless, and single-instance deployments.
- **`Configuration` and `InstanceName` no longer unconditionally required** — `CacheConfig` implements `IValidatableObject` and only enforces Redis fields when `UseMemoryOnly` is `false` and `Enabled` is `true`.
- **Pluggable `ICacheSizeEstimator`** — Replace L1 size heuristics per payload family. Default implementation accounts for `string` (2 bytes/char), `byte[]`, `IDictionary`, and `ICollection`.
- **`HalfOpenSuccessThreshold`** — Configure how many consecutive successes in the half-open state are required before the circuit closes. Default is 1 (previous behaviour preserved).
- **Circuit breaker state-transition metrics** — New `cache.circuit_breaker.transitions` counter with `cache.circuit_breaker.state` tag (`opened`/`half_open`/`closed`). Both services also emit `LogWarning` on every transition.
- **Normalized metric dimensions** — `cache.level` tag now present on hits, misses, errors, evictions, and duration histogram consistently.
- **Shared `StampedeProtection` helper** — Lock acquisition timing, jittered retry loop, and `GetJitteredDelay` extracted from both `RedisCacheService` and `MultiLevelCacheService`.
- **`BenchmarkDotNet` project** — `TheTechLoop.HybridCache.Benchmarks` with hot-key contention, mixed payload, high miss rate, and bulk-operation scenarios.

---

## 📝 What's New in v1.3.0

- **New metrics** — Added `cache.lock.wait_duration`, `cache.batch.size`, `cache.scan.duration`, and `cache.scan.deleted_keys` instruments for full operational visibility
- **Lock-free circuit breaker** — `CircuitBreakerState` now uses `Interlocked` + `Volatile` atomic operations — no monitor contention on hot paths
- **SCAN metrics in invalidation subscribers** — Both `CacheInvalidationSubscriber` (Pub/Sub) and `CacheInvalidationStreamConsumer` (Streams) now record scan duration and deleted key count
- **Compression fix** — `CompressedCacheService` correctly keeps the underlying `MemoryStream` open during GZip serialization (`leaveOpen: true`)
- **Near-100% test coverage** — 174 tests across all services, behaviors, metrics, compression, serialization, warmup, and hardening scenarios

---

**Version:** 1.4.0  
**Status:** Production-Ready ✅  

