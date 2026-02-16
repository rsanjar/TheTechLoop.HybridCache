# TheTechLoop.Cache.MediatR

MediatR pipeline behaviors for [TheTechLoop.HybridCache](https://github.com/rsanjar/TheTechLoop.HybridCache) — automatic convention-based caching and cache invalidation for your CQRS microservices.

[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![NuGet](https://img.shields.io/nuget/v/TheTechLoop.HybridCache.MediatR)](https://www.nuget.org/packages/TheTechLoop.HybridCache.MediatR)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

---

## Overview

This package provides MediatR pipeline behaviors that integrate with **TheTechLoop.HybridCache** to deliver **zero-boilerplate caching** in CQRS architectures. Instead of writing cache logic in every query handler, simply mark your requests with a marker interface and let the pipeline handle the rest.

### What's Included

| Component | Purpose |
|-----------|---------|
| `ICacheable` | Marker interface for queries that should be automatically cached |
| `ICacheInvalidatable` | Marker interface for commands that should invalidate cache after execution |
| `CachingBehavior<TRequest, TResponse>` | Pipeline behavior that intercepts `ICacheable` queries and caches responses |
| `CacheInvalidationBehavior<TRequest, TResponse>` | Pipeline behavior that invalidates cache entries after `ICacheInvalidatable` commands succeed |
| `AddTheTechLoopCacheBehaviors()` | DI extension method to register both behaviors |

---

## Installation

```bash
dotnet add package TheTechLoop.HybridCache.MediatR
```

> **Prerequisite:** You must also have [TheTechLoop.HybridCache](https://www.nuget.org/packages/TheTechLoop.HybridCache) installed and configured.

---

## Quick Start

### 1. Register Services

```csharp
// Program.cs
using TheTechLoop.Cache.Extensions;
using TheTechLoop.Cache.MediatR.Extensions;

// Register core cache services (from TheTechLoop.HybridCache)
builder.Services.AddTheTechLoopCache(builder.Configuration);

// Register MediatR
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
});

// Register MediatR pipeline behaviors (from this package)
builder.Services.AddTheTechLoopCacheBehaviors();
```

### 2. Auto-Cache Queries with `ICacheable`

Mark any MediatR query with `ICacheable` to enable automatic caching. The handler remains **pure** — no cache logic needed:

```csharp
using TheTechLoop.Cache.MediatR.Abstractions;

// The query declares its cache behavior
public record GetDealershipByIdQuery(int Id) : IRequest<Dealership?>, ICacheable
{
    public string CacheKey => $"Dealership:{Id}";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(30);
}

// The handler has ZERO cache logic
public class GetDealershipByIdQueryHandler : IRequestHandler<GetDealershipByIdQuery, Dealership?>
{
    private readonly IReadRepository<Dealership> _repository;

    public async Task<Dealership?> Handle(GetDealershipByIdQuery request, CancellationToken ct)
    {
        return await _repository.Query
            .FirstOrDefaultAsync(d => d.ID == request.Id, ct);
    }
}
```

**What happens:**
1. `CachingBehavior` intercepts the request before the handler runs
2. It builds a service-scoped key: `"company-svc:v1:Dealership:42"`
3. **Cache hit** → returns cached value immediately (handler is never called)
4. **Cache miss** → calls the handler, caches the result, returns it

### 3. Auto-Invalidate with `ICacheInvalidatable`

Mark MediatR commands to automatically invalidate cache entries after successful execution:

```csharp
using TheTechLoop.Cache.MediatR.Abstractions;

public record UpdateDealershipCommand(int Id, string Name) : IRequest<bool>, ICacheInvalidatable
{
    // Exact keys to remove
    public IReadOnlyList<string> CacheKeysToInvalidate =>
        [$"Dealership:{Id}"];

    // Prefix patterns — all matching keys are removed
    public IReadOnlyList<string> CachePrefixesToInvalidate =>
        ["Dealership:Search", "Dealership:List"];
}
```

**What happens:**
1. The handler executes the command (update, create, delete)
2. `CacheInvalidationBehavior` runs **after** the handler succeeds
3. Removes the specified exact keys from cache
4. Removes all keys matching the prefix patterns
5. Publishes cross-service invalidation via Pub/Sub (if configured)

---

## How It Works

### Read Path (CachingBehavior)

```
Controller
  → MediatR.Send(GetDealershipByIdQuery)
    → CachingBehavior intercepts (ICacheable detected)
      → ICacheService.GetOrCreateAsync("company-svc:v1:Dealership:42")
        → [Cache Hit]  → Return cached value (handler skipped)
        → [Cache Miss] → Execute handler → Cache result → Return
```

### Write Path (CacheInvalidationBehavior)

```
Controller
  → MediatR.Send(UpdateDealershipCommand)
    → Handler executes (DB write)
    → CacheInvalidationBehavior runs (ICacheInvalidatable detected)
      → ICacheService.RemoveAsync("company-svc:v1:Dealership:42")
      → ICacheService.RemoveByPrefixAsync("company-svc:v1:Dealership:Search")
      → ICacheInvalidationPublisher.PublishAsync(key)         ← cross-service
      → ICacheInvalidationPublisher.PublishPrefixAsync(prefix) ← cross-service
```

---

## API Reference

### ICacheable

```csharp
public interface ICacheable
{
    /// <summary>
    /// Cache key for this request. Automatically prefixed with service name and version.
    /// Example: "Dealership:42" → "company-svc:v1:Dealership:42"
    /// </summary>
    string CacheKey { get; }

    /// <summary>
    /// How long the cached response should live.
    /// </summary>
    TimeSpan CacheDuration { get; }
}
```

### ICacheInvalidatable

```csharp
public interface ICacheInvalidatable
{
    /// <summary>
    /// Exact cache keys to remove after the command succeeds.
    /// Example: ["Dealership:42"]
    /// </summary>
    IReadOnlyList<string> CacheKeysToInvalidate { get; }

    /// <summary>
    /// Cache key prefixes for pattern-based invalidation.
    /// Example: ["Dealership:Search", "Dealership:List"]
    /// </summary>
    IReadOnlyList<string> CachePrefixesToInvalidate { get; }
}
```

### AddTheTechLoopCacheBehaviors

```csharp
// Registers both CachingBehavior and CacheInvalidationBehavior
services.AddTheTechLoopCacheBehaviors();
```

---

## Complete DI Registration Example

```csharp
// Program.cs
using TheTechLoop.Cache.Extensions;
using TheTechLoop.Cache.MediatR.Extensions;

var builder = WebApplication.CreateBuilder(args);

// 1. Core cache services
builder.Services.AddTheTechLoopCache(builder.Configuration);

// 2. Optional: Multi-level caching (L1 Memory + L2 Redis)
builder.Services.AddTheTechLoopMultiLevelCache(builder.Configuration);

// 3. Optional: Cross-service cache invalidation via Pub/Sub
builder.Services.AddTheTechLoopCacheInvalidation(builder.Configuration);

// 4. MediatR
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
});

// 5. MediatR cache behaviors (this package)
builder.Services.AddTheTechLoopCacheBehaviors();

var app = builder.Build();
app.Run();
```

---

## Cache Key Best Practices

| Data Type | Key Pattern | TTL |
|-----------|-------------|-----|
| Entity by ID | `"Entity:{Id}"` | 15–30 minutes |
| Search results | `"Entity:Search:{Term}:{PageSize}"` | 3–5 minutes |
| List / pagination | `"Entity:List:{Page}:{Size}"` | 3–5 minutes |
| Reference data | `"Country:{Id}"` | 6–10 hours |

---

## Requirements

- .NET 10 or higher
- [TheTechLoop.HybridCache](https://www.nuget.org/packages/TheTechLoop.HybridCache) (core cache library)
- [MediatR](https://www.nuget.org/packages/MediatR) 12.x

---

## License

MIT
