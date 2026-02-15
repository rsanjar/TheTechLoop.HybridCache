# TheTechLoop.Cache

Enterprise-grade distributed Redis caching library for .NET 9 microservices.

## Features

- **Multi-Level Caching** — L1 in-memory + L2 Redis for optimal latency
- **Distributed Locking** — Prevent cache stampede with Redis-based locks
- **Cache Invalidation Pub/Sub** — Cross-service cache invalidation via Redis channels
- **Circuit Breaker** — Graceful degradation when Redis is unavailable
- **CQRS-Optimized** — Read-through caching with write-through invalidation
- **MediatR Pipeline Behavior** — Convention-based caching via `ICacheable` marker
- **OpenTelemetry Metrics** — Built-in hit/miss/duration metrics
- **Service-Scoped Keys** — Automatic key prefixing per microservice
- **Cache Versioning** — Bump version on breaking DTO changes

## Installation

```bash
dotnet add package TheTechLoop.Cache
```

Or via project reference:

```xml
<ProjectReference Include="..\TheTechLoop.Cache\TheTechLoop.Cache.csproj" />
```

---

## Quick Start

### 1. Register Services

```csharp
// Program.cs
builder.Services.AddTheTechLoopCache(builder.Configuration);

// Optional: Enable cross-service invalidation via Redis Pub/Sub
builder.Services.AddTheTechLoopCacheInvalidation();

// Optional: Multi-level caching (L1 Memory + L2 Redis)
builder.Services.AddTheTechLoopMultiLevelCache(builder.Configuration);
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
    "InvalidationChannel": "cache:invalidation",
    "CircuitBreaker": {
      "Enabled": true,
      "BreakDurationSeconds": 60,
      "FailureThreshold": 5
    },
    "MemoryCache": {
      "Enabled": false,
      "DefaultExpirationSeconds": 30,
      "SizeLimit": 1024
    }
  }
}
```

### 3. Basic Usage

```csharp
using TheTechLoop.Cache.Abstractions;
using TheTechLoop.Cache.Keys;

public class DealershipService
{
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;

    public DealershipService(ICacheService cache, CacheKeyBuilder keyBuilder)
    {
        _cache = cache;
        _keyBuilder = keyBuilder;
    }

    public async Task<Dealership?> GetByIdAsync(int id)
    {
        var cacheKey = _keyBuilder.Key("Dealership", id.ToString());

        return await _cache.GetOrCreateAsync(
            cacheKey,
            () => _repository.GetByIdAsync(id),
            TimeSpan.FromMinutes(30));
    }
}
```

---

## Architecture with CQRS + MediatR

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

**Implementations:**

```csharp
public class ReadRepository<TEntity> : IReadRepository<TEntity> where TEntity : class
{
    private readonly DbContext _context;

    public ReadRepository(DbContext context) => _context = context;

    public IQueryable<TEntity> Query => _context.Set<TEntity>().AsNoTracking();

    public async Task<TEntity?> GetByIdAsync(int id, CancellationToken ct = default)
        => await _context.Set<TEntity>().FindAsync([id], ct);

    public async Task<List<TEntity>> GetAllAsync(CancellationToken ct = default)
        => await Query.ToListAsync(ct);
}

public class WriteRepository<TEntity> : IWriteRepository<TEntity> where TEntity : class
{
    private readonly DbContext _context;

    public WriteRepository(DbContext context) => _context = context;

    public async Task AddAsync(TEntity entity, CancellationToken ct = default)
        => await _context.Set<TEntity>().AddAsync(entity, ct);

    public void Update(TEntity entity)
        => _context.Set<TEntity>().Update(entity);

    public void Remove(TEntity entity)
        => _context.Set<TEntity>().Remove(entity);

    public async Task<TEntity?> GetByIdAsync(int id, CancellationToken ct = default)
        => await _context.Set<TEntity>().FindAsync([id], ct);
}

public class UnitOfWork : IUnitOfWork
{
    private readonly DbContext _context;

    public UnitOfWork(DbContext context) => _context = context;

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _context.SaveChangesAsync(ct);
}
```

---

### CQRS Queries and Commands

**Query contract:**

```csharp
public record GetDealershipByIdQuery(int Id) : IRequest<Dealership?>;
```

**Command contracts:**

```csharp
public record UpdateDealershipCommand(
    int Id,
    string Name,
    string BusinessAddress
) : IRequest<bool>;

public record CreateDealershipCommand(
    string Name,
    string BusinessAddress,
    int BusinessZipCodeId
) : IRequest<Dealership>;
```

---

### Query Handler — Cache on the Read Path

```csharp
using TheTechLoop.Cache.Abstractions;
using TheTechLoop.Cache.Keys;

public class GetDealershipByIdQueryHandler : IRequestHandler<GetDealershipByIdQuery, Dealership?>
{
    private readonly IReadRepository<Data.Models.Dealership> _repository;
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;
    private readonly IMapper _mapper;

    public GetDealershipByIdQueryHandler(
        IReadRepository<Data.Models.Dealership> repository,
        ICacheService cache,
        CacheKeyBuilder keyBuilder,
        IMapper mapper)
    {
        _repository = repository;
        _cache = cache;
        _keyBuilder = keyBuilder;
        _mapper = mapper;
    }

    public async Task<Dealership?> Handle(GetDealershipByIdQuery request, CancellationToken ct)
    {
        // Build service-scoped key: "company-svc:v1:Dealership:42"
        var cacheKey = _keyBuilder.Key("Dealership", request.Id.ToString());

        // Read-through: cache hit → return; miss → query DB → cache → return
        return await _cache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                var entity = await _repository.Query
                    .Include(d => d.BusinessZipCode)
                        .ThenInclude(z => z.StateProvince)
                    .FirstOrDefaultAsync(d => d.ID == request.Id, ct);

                return entity is null ? null : _mapper.Map<Dealership>(entity);
            },
            TimeSpan.FromMinutes(30),
            ct);
    }
}
```

**Search query with shorter TTL:**

```csharp
public record SearchDealershipsQuery(string Term, int PageSize = 5) : IRequest<List<Dealership>>;

public class SearchDealershipsQueryHandler : IRequestHandler<SearchDealershipsQuery, List<Dealership>>
{
    private readonly IReadRepository<Data.Models.Dealership> _repository;
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;
    private readonly IMapper _mapper;

    public SearchDealershipsQueryHandler(
        IReadRepository<Data.Models.Dealership> repository,
        ICacheService cache,
        CacheKeyBuilder keyBuilder,
        IMapper mapper)
    {
        _repository = repository;
        _cache = cache;
        _keyBuilder = keyBuilder;
        _mapper = mapper;
    }

    public async Task<List<Dealership>> Handle(SearchDealershipsQuery request, CancellationToken ct)
    {
        var cacheKey = _keyBuilder.Key(
            "Dealership", "Search",
            CacheKeyBuilder.Sanitize(request.Term),
            request.PageSize.ToString());

        return await _cache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                var entities = await _repository.Query
                    .Where(d => EF.Functions.Like(d.Name.ToLower(), $"%{request.Term.ToLower().Trim()}%"))
                    .OrderBy(d => d.Name)
                    .Take(request.PageSize)
                    .ToListAsync(ct);

                return _mapper.Map<List<Dealership>>(entities);
            },
            TimeSpan.FromMinutes(5),
            ct);
    }
}
```

---

### Command Handler — Invalidate on the Write Path

```csharp
using TheTechLoop.Cache.Abstractions;
using TheTechLoop.Cache.Keys;

public class UpdateDealershipCommandHandler : IRequestHandler<UpdateDealershipCommand, bool>
{
    private readonly IWriteRepository<Data.Models.Dealership> _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICacheService _cache;
    private readonly ICacheInvalidationPublisher _invalidation;
    private readonly CacheKeyBuilder _keyBuilder;

    public UpdateDealershipCommandHandler(
        IWriteRepository<Data.Models.Dealership> repository,
        IUnitOfWork unitOfWork,
        ICacheService cache,
        ICacheInvalidationPublisher invalidation,
        CacheKeyBuilder keyBuilder)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _cache = cache;
        _invalidation = invalidation;
        _keyBuilder = keyBuilder;
    }

    public async Task<bool> Handle(UpdateDealershipCommand request, CancellationToken ct)
    {
        // 1. Load from WriteRepository (EF tracked)
        var entity = await _repository.GetByIdAsync(request.Id, ct);
        if (entity is null) return false;

        // 2. Apply changes
        entity.Name = request.Name;
        entity.BusinessAddress = request.BusinessAddress;

        // 3. Commit
        _repository.Update(entity);
        await _unitOfWork.SaveChangesAsync(ct);

        // 4. Invalidate specific entity cache
        var entityKey = _keyBuilder.Key("Dealership", request.Id.ToString());
        await _cache.RemoveAsync(entityKey, ct);

        // 5. Invalidate search results (prefix pattern)
        var searchPattern = _keyBuilder.Key("Dealership", "Search");
        await _cache.RemoveByPrefixAsync(searchPattern, ct);

        // 6. Notify OTHER microservice instances via Pub/Sub
        await _invalidation.PublishAsync(entityKey, ct);
        await _invalidation.PublishPrefixAsync(searchPattern, ct);

        return true;
    }
}
```

```csharp
public class CreateDealershipCommandHandler : IRequestHandler<CreateDealershipCommand, Dealership>
{
    private readonly IWriteRepository<Data.Models.Dealership> _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICacheService _cache;
    private readonly ICacheInvalidationPublisher _invalidation;
    private readonly CacheKeyBuilder _keyBuilder;
    private readonly IMapper _mapper;

    public CreateDealershipCommandHandler(
        IWriteRepository<Data.Models.Dealership> repository,
        IUnitOfWork unitOfWork,
        ICacheService cache,
        ICacheInvalidationPublisher invalidation,
        CacheKeyBuilder keyBuilder,
        IMapper mapper)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _cache = cache;
        _invalidation = invalidation;
        _keyBuilder = keyBuilder;
        _mapper = mapper;
    }

    public async Task<Dealership> Handle(CreateDealershipCommand request, CancellationToken ct)
    {
        var entity = new Data.Models.Dealership
        {
            Name = request.Name,
            BusinessAddress = request.BusinessAddress,
            BusinessZipCodeID = request.BusinessZipCodeId,
            UniqueKey = Guid.NewGuid()
        };

        await _repository.AddAsync(entity, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        // Invalidate list/search caches (new item affects pagination & search)
        var searchPattern = _keyBuilder.Key("Dealership", "Search");
        await _cache.RemoveByPrefixAsync(searchPattern, ct);
        await _invalidation.PublishPrefixAsync(searchPattern, ct);

        return _mapper.Map<Dealership>(entity);
    }
}
```

---

### Controller — Thin, Delegates to MediatR

```csharp
[ApiController]
[Route("api/[controller]")]
public class DealershipController : ControllerBase
{
    private readonly IMediator _mediator;

    public DealershipController(IMediator mediator) => _mediator = mediator;

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var result = await _mediator.Send(new GetDealershipByIdQuery(id));
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(string term, int pageSize = 5)
    {
        var result = await _mediator.Send(new SearchDealershipsQuery(term, pageSize));
        return Ok(result);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, UpdateDealershipCommand command)
    {
        var success = await _mediator.Send(command with { Id = id });
        return success ? NoContent() : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateDealershipCommand command)
    {
        var result = await _mediator.Send(command);
        return CreatedAtAction(nameof(GetById), new { id = result.ID }, result);
    }
}
```

---

## MediatR Pipeline Behavior — Auto-Cache for Queries

Instead of writing cache logic in every handler, use a pipeline behavior with an `ICacheable` marker:

### ICacheable Marker Interface

```csharp
/// <summary>
/// Apply to any MediatR query that should be automatically cached.
/// </summary>
public interface ICacheable
{
    string CacheKey { get; }
    TimeSpan CacheDuration { get; }
}
```

### CachingBehavior

```csharp
using MediatR;
using TheTechLoop.Cache.Abstractions;
using TheTechLoop.Cache.Keys;

public class CachingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;

    public CachingBehavior(ICacheService cache, CacheKeyBuilder keyBuilder)
    {
        _cache = cache;
        _keyBuilder = keyBuilder;
    }

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

### Usage — Query auto-cached, handler stays pure

```csharp
// The query declares its cache behavior
public record GetDealershipByIdQuery(int Id) : IRequest<Dealership?>, ICacheable
{
    public string CacheKey => $"Dealership:{Id}";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(30);
}

// The handler has ZERO cache logic
public class GetDealershipByIdQueryHandler : IRequestHandler<GetDealershipByIdQuery, Dealership?>
{
    private readonly IReadRepository<Data.Models.Dealership> _repository;
    private readonly IMapper _mapper;

    public GetDealershipByIdQueryHandler(
        IReadRepository<Data.Models.Dealership> repository,
        IMapper mapper)
    {
        _repository = repository;
        _mapper = mapper;
    }

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

## DI Registration (Complete)

```csharp
// Program.cs

// MediatR + Pipeline Behaviors
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(GetDealershipByIdQuery).Assembly);
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>));
});

// TheTechLoop.Cache
builder.Services.AddTheTechLoopCache(builder.Configuration);
builder.Services.AddTheTechLoopCacheInvalidation();
// builder.Services.AddTheTechLoopMultiLevelCache(builder.Configuration); // optional L1+L2

// Repositories + UnitOfWork
builder.Services.AddScoped(typeof(IReadRepository<>), typeof(ReadRepository<>));
builder.Services.AddScoped(typeof(IWriteRepository<>), typeof(WriteRepository<>));
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
```

---

## CacheKeyBuilder API

`CacheKeyBuilder` is registered as a singleton, scoped to the current microservice:

```csharp
// Injected instance (service-scoped, versioned)
var key = _keyBuilder.Key("Dealership", "42");
// → "company-svc:v1:Dealership:42"

var pattern = _keyBuilder.Pattern("Dealership", "Search");
// → "company-svc:v1:Dealership:Search*"

// Static helpers (no service scope)
var sharedKey = CacheKeyBuilder.For("shared", "config");
// → "shared:config"

var entityKey = CacheKeyBuilder.ForEntity("User", 42);
// → "User:42"

var sanitized = CacheKeyBuilder.Sanitize("hello world/test");
// → "hello_world_test"
```

---

## CacheMetrics (OpenTelemetry)

`CacheMetrics` is built into `RedisCacheService` and `MultiLevelCacheService` — all metrics are recorded automatically. You never need to call it directly in application code.

### What's Tracked Automatically

| Operation | Metric | Recorded When |
|-----------|--------|---------------|
| `GetOrCreateAsync` (hit) | `cache.hits` + `cache.duration` | Key found in cache |
| `GetOrCreateAsync` (miss) | `cache.misses` + `cache.duration` | Key not found, factory called |
| `GetOrCreateAsync` (error) | `cache.errors` | Redis throws exception |
| `RemoveAsync` | `cache.evictions` | Key explicitly removed |
| Circuit breaker open | `cache.circuit_breaker.bypasses` | Redis bypassed due to failures |

All metrics include a `cache.key_prefix` tag (first segment of the key, e.g., `"company-svc"`) for low-cardinality grouping. Multi-level cache adds a `cache.level` tag (`"L1"` or `"L2"`).

### Setup — Prometheus

```csharp
// Program.cs — add after AddTheTechLoopCache()
using TheTechLoop.Cache.Metrics;

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(CacheMetrics.MeterName); // "TheTechLoop.Cache"
        metrics.AddPrometheusExporter();
    });

app.MapPrometheusScrapingEndpoint("/metrics");
```

Scrape `GET /metrics`:

```
cache_hits_total{cache_key_prefix="company-svc",cache_level="L2"} 1547
cache_misses_total{cache_key_prefix="company-svc"} 203
cache_errors_total{cache_key_prefix="company-svc"} 2
cache_evictions_total{cache_key_prefix="company-svc"} 45
cache_circuit_breaker_bypasses_total 0
cache_duration_ms_bucket{cache_operation="hit",le="1"} 1200
```

### Setup — OTLP (Azure Monitor, Grafana, Datadog)

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(CacheMetrics.MeterName);
        metrics.AddOtlpExporter();
    });
```

### Setup — Console (development)

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(CacheMetrics.MeterName);
        metrics.AddConsoleExporter();
    });
```

### CLI — dotnet-counters (no code changes)

```bash
dotnet counters monitor --process-id <PID> --counters TheTechLoop.Cache
```

Output:

```
[TheTechLoop.Cache]
    cache.hits (Count / 1 sec)                     12
    cache.misses (Count / 1 sec)                    3
    cache.errors (Count / 1 sec)                    0
    cache.duration (ms)
        Percentile = 50    0.45
        Percentile = 95    2.1
        Percentile = 99    5.3
```

### Custom ICacheService Implementations

If you build your own `ICacheService`, inject `CacheMetrics` to record metrics:

```csharp
public class MyCustomCacheService : ICacheService
{
    private readonly CacheMetrics _metrics;

    public MyCustomCacheService(CacheMetrics metrics)
    {
        _metrics = metrics;
    }

    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiration, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var cached = await TryGetFromCache<T>(key);

        if (cached is not null)
        {
            _metrics.RecordHit(key, sw.Elapsed.TotalMilliseconds);
            return cached;
        }

        _metrics.RecordMiss(key, sw.Elapsed.TotalMilliseconds);
        // ... factory call, cache write
    }
}
```

---

## Cache TTL Guidelines

| Data Type | TTL | Example |
|-----------|-----|---------|
| Static reference data | 6–10 hours | Countries, states, positions |
| Entity by ID | 15–30 minutes | Dealership, User, Company |
| Search / list results | 3–5 minutes | Search results, paginated lists |
| User session data | 1–5 minutes | Active user profile |
| Frequently mutated data | 30–60 seconds | Real-time counters, presence |

---

## Data Flow

```
READ PATH (Query)

  Controller
    → MediatR.Send(GetDealershipByIdQuery)
      → CachingBehavior intercepts
        → ICacheService.GetOrCreateAsync("company-svc:v1:Dealership:42")
          → [Cache Hit] Return cached value
          → [Cache Miss]
              → QueryHandler.Handle()
                → ReadRepository (AsNoTracking)
                  → Database
              → Store in cache (30m TTL)
              → Return value


WRITE PATH (Command)

  Controller
    → MediatR.Send(UpdateDealershipCommand)
      → CommandHandler.Handle()
        → WriteRepository.Update(entity)
        → UnitOfWork.SaveChangesAsync()
        → ICacheService.RemoveAsync("company-svc:v1:Dealership:42")
        → ICacheService.RemoveByPrefixAsync("company-svc:v1:Dealership:Search")
        → ICacheInvalidationPublisher.PublishAsync(key)      ← notifies other instances
        → ICacheInvalidationPublisher.PublishPrefixAsync(prefix) ← notifies other instances
```

---

## Rules of Thumb

| Rule | Why |
|------|-----|
| Cache only in Query Handlers (or `CachingBehavior`) | Reads benefit from cache; writes must always hit DB |
| Invalidate only in Command Handlers | After `UnitOfWork.SaveChangesAsync` succeeds |
| ReadRepository uses `AsNoTracking` | No EF change tracking overhead on cached reads |
| WriteRepository is tracked | EF change tracking needed for updates |
| UnitOfWork wraps the command | Single transaction per command |
| `ICacheInvalidationPublisher` for cross-service | Other microservice instances must clear their L1/L2 |
| Use `ICacheable` marker for convention-based caching | Eliminates cache boilerplate in every handler |
| Short TTL for search/list, long TTL for by-ID | Search results change frequently; single entities are more stable |
| Bump `CacheVersion` on breaking DTO changes | Old cache entries are automatically ignored |
| Always fall back to DB on cache errors | Cache is an optimization, not a dependency |

---

## Project Structure

```
TheTechLoop.Cache/
├── Abstractions/
│   ├── ICacheable.cs                       # Marker for auto-cached queries
│   ├── ICacheInvalidatable.cs              # Marker for auto-invalidating commands
│   ├── ICacheService.cs                    # Core cache contract
│   ├── ICacheInvalidationPublisher.cs      # Cross-service Pub/Sub
│   └── IDistributedLock.cs                 # Stampede prevention
├── Behaviors/
│   ├── CachingBehavior.cs                  # MediatR read-path auto-cache
│   └── CacheInvalidationBehavior.cs        # MediatR write-path auto-invalidate
├── Configuration/
│   └── CacheConfig.cs                     # Full config with circuit breaker, L1/L2
├── Extensions/
│   └── CacheServiceCollectionExtensions.cs # DI registration
├── Keys/
│   └── CacheKeyBuilder.cs                 # Service-scoped, versioned keys
├── Metrics/
│   └── CacheMetrics.cs                    # OpenTelemetry counters + histogram
├── Services/
│   ├── RedisCacheService.cs               # Core Redis + stampede + circuit breaker
│   ├── MultiLevelCacheService.cs          # L1 Memory + L2 Redis
│   ├── RedisDistributedLock.cs            # Redis SET NX with Lua release
│   ├── RedisCacheInvalidationPublisher.cs # Pub/Sub publisher
│   ├── CacheInvalidationSubscriber.cs     # Background Pub/Sub consumer
│   ├── CircuitBreakerState.cs             # Thread-safe circuit breaker
│   └── NoOpCacheService.cs               # No-op for disabled cache
├── README.md
└── TheTechLoop.Cache.csproj
```

## Publishing to NuGet

```bash
cd TheTechLoop.Cache
dotnet pack -c Release
dotnet nuget push bin/Release/TheTechLoop.Cache.1.0.0.nupkg \
  --source https://api.nuget.org/v3/index.json \
  --api-key YOUR_API_KEY
```

## License

MIT
