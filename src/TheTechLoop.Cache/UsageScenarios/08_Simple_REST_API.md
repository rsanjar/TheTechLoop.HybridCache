# Usage Scenario 8: Simple REST API with Basic Cache

## Overview

**Best for:** Simple APIs without CQRS, minimal setup, legacy systems, prototypes

**Features Used:**
- ✅ Single-level Redis cache
- ✅ Direct cache access (no MediatR)
- ✅ Manual cache management in controllers
- ✅ Minimal dependencies

**Setup Time:** 5 minutes  
**Complexity:** Low  
**Performance:** 5-10x improvement

---

## Step 1: Minimal Setup

### Program.cs
```csharp
using TheTechLoop.Cache.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Just add cache - nothing else required
builder.Services.AddTheTechLoopCache(builder.Configuration);

builder.Services.AddControllers();

var app = builder.Build();
app.MapControllers();
app.Run();
```

### appsettings.json
```json
{
  "TheTechLoopCache": {
    "Configuration": "localhost:6379",
    "InstanceName": "MyAPI:",
    "ServiceName": "my-api",
    "CacheVersion": "v1",
    "DefaultExpirationMinutes": 30,
    "Enabled": true
  }
}
```

---

## Step 2: Controller with Direct Cache Usage

```csharp
using TheTechLoop.Cache.Abstractions;
using TheTechLoop.Cache.Keys;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;
    private readonly IProductRepository _repository;

    public ProductsController(
        ICacheService cache,
        CacheKeyBuilder keyBuilder,
        IProductRepository repository)
    {
        _cache = cache;
        _keyBuilder = keyBuilder;
        _repository = repository;
    }

    // GET api/products/42
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var cacheKey = _keyBuilder.Key("Product", id.ToString());

        // GetOrCreateAsync: Check cache first, DB if miss
        var product = await _cache.GetOrCreateAsync(
            cacheKey,
            async () => await _repository.GetByIdAsync(id),
            TimeSpan.FromMinutes(30));

        return product is null ? NotFound() : Ok(product);
    }

    // GET api/products
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var cacheKey = _keyBuilder.Key("Product", "All");

        var products = await _cache.GetOrCreateAsync(
            cacheKey,
            async () => await _repository.GetAllAsync(),
            TimeSpan.FromMinutes(10));

        return Ok(products);
    }

    // POST api/products
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateProductDto dto)
    {
        var product = await _repository.CreateAsync(dto);

        // Invalidate list cache after create
        await _cache.RemoveAsync(_keyBuilder.Key("Product", "All"));

        return CreatedAtAction(nameof(GetById), new { id = product.Id }, product);
    }

    // PUT api/products/42
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateProductDto dto)
    {
        var product = await _repository.UpdateAsync(id, dto);
        if (product is null) return NotFound();

        // Invalidate both entity and list caches
        await _cache.RemoveAsync(_keyBuilder.Key("Product", id.ToString()));
        await _cache.RemoveAsync(_keyBuilder.Key("Product", "All"));

        return Ok(product);
    }

    // DELETE api/products/42
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var success = await _repository.DeleteAsync(id);
        if (!success) return NotFound();

        // Invalidate both caches
        await _cache.RemoveAsync(_keyBuilder.Key("Product", id.ToString()));
        await _cache.RemoveAsync(_keyBuilder.Key("Product", "All"));

        return NoContent();
    }
}
```

---

## Step 3: Repository (Simple)

```csharp
public interface IProductRepository
{
    Task<Product?> GetByIdAsync(int id);
    Task<List<Product>> GetAllAsync();
    Task<Product> CreateAsync(CreateProductDto dto);
    Task<Product?> UpdateAsync(int id, UpdateProductDto dto);
    Task<bool> DeleteAsync(int id);
}

public class ProductRepository : IProductRepository
{
    private readonly DbContext _context;

    public ProductRepository(DbContext context)
    {
        _context = context;
    }

    public async Task<Product?> GetByIdAsync(int id)
    {
        return await _context.Products.FindAsync(id);
    }

    public async Task<List<Product>> GetAllAsync()
    {
        return await _context.Products.ToListAsync();
    }

    public async Task<Product> CreateAsync(CreateProductDto dto)
    {
        var product = new Product { Name = dto.Name, Price = dto.Price };
        _context.Products.Add(product);
        await _context.SaveChangesAsync();
        return product;
    }

    public async Task<Product?> UpdateAsync(int id, UpdateProductDto dto)
    {
        var product = await _context.Products.FindAsync(id);
        if (product is null) return null;

        product.Name = dto.Name;
        product.Price = dto.Price;
        await _context.SaveChangesAsync();
        return product;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var product = await _context.Products.FindAsync(id);
        if (product is null) return false;

        _context.Products.Remove(product);
        await _context.SaveChangesAsync();
        return true;
    }
}
```

---

## Flow Diagram

```
GET /api/products/42
    ↓
Controller.GetById(42)
    ↓
Check cache: "my-api:v1:Product:42"
    ↓
┌─ Cache Hit (5ms) ──────────┐
│ Return cached product      │ ← Repository NEVER called
└────────────────────────────┘
    ↓
┌─ Cache Miss ───────────────────┐
│ Repository.GetByIdAsync(42)    │
│   ↓                             │
│ Database query (50ms)           │
│   ↓                             │
│ Store in cache (30min TTL)     │
│   ↓                             │
│ Return product                 │
└─────────────────────────────────┘
    ↓
Return to client


POST /api/products
    ↓
Controller.Create(dto)
    ↓
Repository.CreateAsync(dto)
    ↓
Database INSERT
    ↓
Remove cache: "my-api:v1:Product:All"
    ↓
Return created product


PUT /api/products/42
    ↓
Controller.Update(42, dto)
    ↓
Repository.UpdateAsync(42, dto)
    ↓
Database UPDATE
    ↓
Remove cache: "my-api:v1:Product:42"
Remove cache: "my-api:v1:Product:All"
    ↓
Return updated product
```

---

## Advanced: Search with Cache

```csharp
[HttpGet("search")]
public async Task<IActionResult> Search([FromQuery] string term)
{
    // Sanitize term for cache key
    var sanitizedTerm = CacheKeyBuilder.Sanitize(term);
    var cacheKey = _keyBuilder.Key("Product", $"Search:{sanitizedTerm}");

    var results = await _cache.GetOrCreateAsync(
        cacheKey,
        async () => await _repository.SearchAsync(term),
        TimeSpan.FromMinutes(5));  // Shorter TTL for search

    return Ok(results);
}
```

---

## Advanced: Batch Invalidation

```csharp
[HttpPost("bulk-update")]
public async Task<IActionResult> BulkUpdate([FromBody] List<UpdateProductDto> dtos)
{
    var products = await _repository.BulkUpdateAsync(dtos);

    // Invalidate multiple keys at once
    var keysToInvalidate = products.Select(p => 
        _keyBuilder.Key("Product", p.Id.ToString())
    ).ToList();

    keysToInvalidate.Add(_keyBuilder.Key("Product", "All"));

    // Invalidate all search results
    await _cache.RemoveByPrefixAsync(_keyBuilder.Key("Product", "Search"));

    return Ok(products);
}
```

---

## Performance Metrics

### Before Cache
```
GET /api/products/42
Average: 50ms
P95: 80ms
P99: 120ms
```

### After Cache
```
GET /api/products/42 (cached)
Average: 5ms
P95: 8ms
P99: 12ms

Improvement: 10x faster
```

---

## Best Practices

### ✅ DO:
- Use `GetOrCreateAsync` for reads (automatic caching)
- Always invalidate cache after writes
- Use appropriate TTL (short for search, long for entities)
- Sanitize user input in cache keys

### ❌ DON'T:
- Cache POST/PUT/DELETE responses
- Use unsanitized user input in keys
- Set TTL longer than data staleness tolerance
- Forget to invalidate related caches (lists, searches)

---

## When to Use This Scenario

✅ **Use when:**
- Simple REST API without CQRS
- Minimal dependencies desired
- Rapid prototyping
- Legacy system migration
- Learning/testing

❌ **Don't use when:**
- Complex business logic (use CQRS instead)
- Need automatic invalidation
- High write volume
- Multi-service coordination required

---

## Upgrade Path

Start with this simple scenario, then add features as needed:

1. **Add Multi-Level Cache:**
   ```csharp
   builder.Services.AddTheTechLoopMultiLevelCache(builder.Configuration);
   ```

2. **Add Compression:**
   ```json
   { "EnableCompression": true }
   ```

3. **Add Metrics:**
   ```json
   { "EnableEffectivenessMetrics": true }
   ```

4. **Migrate to CQRS:**
   - See Scenario #1 for full CQRS pattern

---

## Summary

This scenario provides:
- **Minimal setup** (5 minutes)
- **Simple usage** (direct injection)
- **5-10x performance** improvement
- **Easy to understand** for beginners
- **No dependencies** on MediatR or CQRS

Perfect for simple APIs, prototypes, and learning the library basics.
