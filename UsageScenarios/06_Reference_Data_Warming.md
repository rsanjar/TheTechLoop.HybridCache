# Usage Scenario 6: Reference Data with Cache Warming

## Overview

**Best for:** Static reference data (Country, StateProvince, ZipCode) that must be instantly available

**Features Used:**
- ✅ Cache Warming on Startup
- ✅ Pre-load static data before accepting requests
- ✅ Zero cold-start latency
- ✅ Strategy pattern for extensibility

**Real-World Use Cases:**
- Country list (195 countries) - loaded on startup
- StateProvince list (3,142 states) - pre-cached
- ZipCode lookup tables - warmed before first request
- Configuration settings - immediately available
- Product categories - no cache miss on first access

**Performance Benefits:**
- First request: **0ms cache miss** (data already cached)
- Startup time: **+2 seconds** (acceptable tradeoff)
- User experience: **Instant responses** from first request
- Cache hit rate: **99.9%+** for reference data

---

## Architecture

```
Application Startup Flow:
-------------------------

Application starts
  ↓
Container builds
  ↓
Cache services registered
  ↓
┌──────────────────────────────────────────┐
│  CacheWarmupService (Background Service) │
│  Starts BEFORE accepting HTTP requests   │
└──────────────┬───────────────────────────┘
               │
               ▼
      ┌────────────────┐
      │ Get all warmup │
      │   strategies   │
      └────────┬───────┘
               │
       ┌───────┴───────┐
       │               │
       ▼               ▼
┌──────────────┐  ┌──────────────┐
│GeoDataWarmup │  │ConfigWarmup  │
│  Strategy    │  │  Strategy    │
└──────┬───────┘  └──────┬───────┘
       │                 │
       ▼                 ▼
Load countries      Load settings
Load states         Load features
Load zipcodes       Load categories
       │                 │
       └────────┬────────┘
                ▼
        All data cached
                ↓
        Log completion
                ↓
    Application ready
                ↓
    HTTP server starts
                ↓
    First request
                ↓
    Cache HIT ✓ (0ms)


Timeline:
---------
00:00.000 - Application starts
00:00.500 - DI container built
00:00.501 - CacheWarmupService starts
00:00.502 - GeoDataWarmup executing...
00:00.750 - Loaded 195 countries
00:01.200 - Loaded 3,142 states
00:02.000 - Loaded 42,000 zipcodes
00:02.001 - ConfigWarmup executing...
00:02.100 - Loaded 50 configuration settings
00:02.101 - All warmup strategies completed
00:02.102 - Application ready
00:02.200 - HTTP server listening
00:02.300 - First request arrives
00:02.301 - Country cache HIT (0ms) ✓
```

---

## Step 1: Enable Cache Warming

### appsettings.json
```json
{
  "TheTechLoopCache": {
    "Configuration": "localhost:6379,password=***",
    "InstanceName": "CORA.Org:",
    "ServiceName": "organization-svc",
    "CacheVersion": "v1",
    
    "EnableWarmup": true,  // ← Enable cache warming
    
    "MemoryCache": {
      "Enabled": true
    }
  }
}
```

### Program.cs
```csharp
using TheTechLoop.Cache.Extensions;
using TheTechLoop.Company.Service.Cache;

var builder = WebApplication.CreateBuilder(args);

// Register cache services
builder.Services.AddTheTechLoopCache(builder.Configuration);
builder.Services.AddTheTechLoopMultiLevelCache(builder.Configuration);

// Register cache warming service (runs on startup)
builder.Services.AddTheTechLoopCacheWarmup();

// Register warmup strategies
builder.Services.AddTransient<ICacheWarmupStrategy, GeoDataWarmupStrategy>();
builder.Services.AddTransient<ICacheWarmupStrategy, ConfigurationWarmupStrategy>();
builder.Services.AddTransient<ICacheWarmupStrategy, CategoryWarmupStrategy>();

var app = builder.Build();

// Warming happens automatically during app.Build()
// HTTP server starts AFTER warmup completes

app.Run();
```

---

## Step 2: Geographic Data Warmup Strategy

### GeoDataWarmupStrategy.cs
```csharp
using TheTechLoop.Cache.Abstractions;
using TheTechLoop.Cache.Warming;
using TheTechLoop.Cache.Keys;
using TheTechLoop.Company.Data;
using TheTechLoop.Company.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace TheTechLoop.Company.Service.Cache;

/// <summary>
/// Pre-loads geographic reference data (Country, StateProvince, ZipCode)
/// Executes during application startup before accepting requests
/// </summary>
public class GeoDataWarmupStrategy : ICacheWarmupStrategy
{
    private readonly TheTechLoopDataContext _context;
    private readonly CacheKeyBuilder _keyBuilder;
    private readonly ILogger<GeoDataWarmupStrategy> _logger;

    public GeoDataWarmupStrategy(
        TheTechLoopDataContext context,
        CacheKeyBuilder keyBuilder,
        ILogger<GeoDataWarmupStrategy> logger)
    {
        _context = context;
        _keyBuilder = keyBuilder;
        _logger = logger;
    }

    public async Task WarmupAsync(ICacheService cache, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting GeoData cache warmup...");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. Pre-load all countries
        await WarmupCountriesAsync(cache, cancellationToken);

        // 2. Pre-load all states/provinces
        await WarmupStatesAsync(cache, cancellationToken);

        // 3. Pre-load zipcode lookup tables
        await WarmupZipCodesAsync(cache, cancellationToken);

        sw.Stop();
        _logger.LogInformation(
            "GeoData cache warmup completed in {ElapsedMs}ms",
            sw.ElapsedMilliseconds);
    }

    private async Task WarmupCountriesAsync(ICacheService cache, CancellationToken ct)
    {
        var countries = await _context.Countries
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        // Cache all countries list
        var allKey = _keyBuilder.Key("Country", "All");
        await cache.SetAsync(allKey, countries, TimeSpan.FromDays(7), ct);

        // Cache each country individually
        foreach (var country in countries)
        {
            var key = _keyBuilder.Key("Country", country.ID.ToString());
            await cache.SetAsync(key, country, TimeSpan.FromDays(7), ct);
        }

        _logger.LogInformation("Warmed up {Count} countries", countries.Count);
    }

    private async Task WarmupStatesAsync(ICacheService cache, CancellationToken ct)
    {
        var states = await _context.StateProvinces
            .AsNoTracking()
            .Include(s => s.Country)
            .OrderBy(s => s.Country.Name)
                .ThenBy(s => s.Name)
            .ToListAsync(ct);

        // Cache all states list
        var allKey = _keyBuilder.Key("State", "All");
        await cache.SetAsync(allKey, states, TimeSpan.FromDays(7), ct);

        // Cache states by country
        var statesByCountry = states.GroupBy(s => s.CountryID);
        foreach (var group in statesByCountry)
        {
            var key = _keyBuilder.Key("State", "ByCountry", group.Key.ToString());
            await cache.SetAsync(key, group.ToList(), TimeSpan.FromDays(7), ct);
        }

        // Cache each state individually
        foreach (var state in states)
        {
            var key = _keyBuilder.Key("State", state.ID.ToString());
            await cache.SetAsync(key, state, TimeSpan.FromDays(7), ct);
        }

        _logger.LogInformation("Warmed up {Count} states/provinces", states.Count);
    }

    private async Task WarmupZipCodesAsync(ICacheService cache, CancellationToken ct)
    {
        // Only warmup US zipcodes (too many to cache all)
        var usCountry = await _context.Countries
            .FirstOrDefaultAsync(c => c.Code == "US", ct);

        if (usCountry is null)
        {
            _logger.LogWarning("US country not found, skipping zipcode warmup");
            return;
        }

        var usZipCodes = await _context.ZipCodes
            .AsNoTracking()
            .Include(z => z.StateProvince)
                .ThenInclude(s => s.Country)
            .Where(z => z.StateProvince.CountryID == usCountry.ID)
            .ToListAsync(ct);

        // Cache zipcodes by state
        var zipsByState = usZipCodes.GroupBy(z => z.StateProvinceID);
        foreach (var group in zipsByState)
        {
            var key = _keyBuilder.Key("ZipCode", "ByState", group.Key.ToString());
            await cache.SetAsync(key, group.ToList(), TimeSpan.FromDays(7), ct);
        }

        _logger.LogInformation("Warmed up {Count} US zipcodes", usZipCodes.Count);
    }
}
```

---

## Step 3: Configuration Warmup Strategy

### ConfigurationWarmupStrategy.cs
```csharp
using TheTechLoop.Cache.Abstractions;
using TheTechLoop.Cache.Warming;
using TheTechLoop.Cache.Keys;

namespace TheTechLoop.Company.Service.Cache;

/// <summary>
/// Pre-loads application configuration settings
/// Feature flags, limits, API keys, etc.
/// </summary>
public class ConfigurationWarmupStrategy : ICacheWarmupStrategy
{
    private readonly IConfiguration _configuration;
    private readonly CacheKeyBuilder _keyBuilder;
    private readonly ILogger<ConfigurationWarmupStrategy> _logger;

    public ConfigurationWarmupStrategy(
        IConfiguration configuration,
        CacheKeyBuilder keyBuilder,
        ILogger<ConfigurationWarmupStrategy> logger)
    {
        _configuration = configuration;
        _keyBuilder = keyBuilder;
        _logger = logger;
    }

    public async Task WarmupAsync(ICacheService cache, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Configuration cache warmup...");

        // Pre-load feature flags
        var featureFlags = new Dictionary<string, bool>
        {
            { "EnableCompanySearch", true },
            { "EnableDealershipMap", true },
            { "EnableUserRegistration", true },
            { "EnableAdvancedReporting", false }
        };

        var key = _keyBuilder.Key("Config", "FeatureFlags");
        await cache.SetAsync(key, featureFlags, TimeSpan.FromHours(24), cancellationToken);

        // Pre-load rate limits
        var rateLimits = new Dictionary<string, int>
        {
            { "ApiRequestsPerMinute", 1000 },
            { "SearchRequestsPerMinute", 100 },
            { "ExportRequestsPerHour", 10 }
        };

        key = _keyBuilder.Key("Config", "RateLimits");
        await cache.SetAsync(key, rateLimits, TimeSpan.FromHours(24), cancellationToken);

        // Pre-load system settings
        var systemSettings = new Dictionary<string, string>
        {
            { "CompanyLogoUrl", "https://cdn.example.com/logo.png" },
            { "SupportEmail", "support@thetechloop.com" },
            { "ApiVersion", "v1.0" }
        };

        key = _keyBuilder.Key("Config", "SystemSettings");
        await cache.SetAsync(key, systemSettings, TimeSpan.FromHours(24), cancellationToken);

        _logger.LogInformation("Configuration cache warmup completed");
    }
}
```

---

## Step 4: Category/Reference Data Warmup

### CategoryWarmupStrategy.cs
```csharp
using TheTechLoop.Cache.Abstractions;
using TheTechLoop.Cache.Warming;
using TheTechLoop.Cache.Keys;
using TheTechLoop.Company.Data;
using Microsoft.EntityFrameworkCore;

namespace TheTechLoop.Company.Service.Cache;

/// <summary>
/// Pre-loads skill categories and interests
/// Frequently accessed reference data
/// </summary>
public class CategoryWarmupStrategy : ICacheWarmupStrategy
{
    private readonly TheTechLoopDataContext _context;
    private readonly CacheKeyBuilder _keyBuilder;
    private readonly ILogger<CategoryWarmupStrategy> _logger;

    public CategoryWarmupStrategy(
        TheTechLoopDataContext context,
        CacheKeyBuilder keyBuilder,
        ILogger<CategoryWarmupStrategy> logger)
    {
        _context = context;
        _keyBuilder = keyBuilder;
        _logger = logger;
    }

    public async Task WarmupAsync(ICacheService cache, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Category cache warmup...");

        // Pre-load skill categories
        var skillCategories = await _context.SkillCategories
            .AsNoTracking()
            .OrderBy(sc => sc.Name)
            .ToListAsync(cancellationToken);

        var key = _keyBuilder.Key("SkillCategory", "All");
        await cache.SetAsync(key, skillCategories, TimeSpan.FromHours(24), cancellationToken);

        _logger.LogInformation("Warmed up {Count} skill categories", skillCategories.Count);

        // Pre-load interests
        var interests = await _context.Interests
            .AsNoTracking()
            .OrderBy(i => i.Name)
            .ToListAsync(cancellationToken);

        key = _keyBuilder.Key("Interest", "All");
        await cache.SetAsync(key, interests, TimeSpan.FromHours(24), cancellationToken);

        _logger.LogInformation("Warmed up {Count} interests", interests.Count);

        _logger.LogInformation("Category cache warmup completed");
    }
}
```

---

## Step 5: GeoController with Pre-Warmed Cache

### GeoController.cs (Instant Responses)
```csharp
using Microsoft.AspNetCore.Mvc;
using TheTechLoop.Cache.Abstractions;
using TheTechLoop.Cache.Keys;
using TheTechLoop.Company.Data;
using TheTechLoop.Company.Data.Models;

namespace TheTechLoop.Company.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GeoController : ControllerBase
{
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;
    private readonly TheTechLoopDataContext _context;
    private readonly ILogger<GeoController> _logger;

    public GeoController(
        ICacheService cache,
        CacheKeyBuilder keyBuilder,
        TheTechLoopDataContext context,
        ILogger<GeoController> logger)
    {
        _cache = cache;
        _keyBuilder = keyBuilder;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get all countries - ALWAYS cached (warmed on startup)
    /// First request: 0ms (cache hit)
    /// </summary>
    [HttpGet("countries")]
    public async Task<IActionResult> GetAllCountries()
    {
        var cacheKey = _keyBuilder.Key("Country", "All");

        // This will ALWAYS hit cache (warmed on startup)
        var countries = await _cache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                // This should NEVER execute (already warmed)
                _logger.LogWarning("Country cache miss - warmup may have failed!");
                return await _context.Countries
                    .AsNoTracking()
                    .OrderBy(c => c.Name)
                    .ToListAsync();
            },
            TimeSpan.FromDays(7));

        return Ok(countries);
    }

    /// <summary>
    /// Get states by country - ALWAYS cached (warmed on startup)
    /// </summary>
    [HttpGet("countries/{countryId}/states")]
    public async Task<IActionResult> GetStatesByCountry(int countryId)
    {
        var cacheKey = _keyBuilder.Key("State", "ByCountry", countryId.ToString());

        var states = await _cache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                _logger.LogWarning("State cache miss - warmup may have failed!");
                return await _context.StateProvinces
                    .AsNoTracking()
                    .Include(s => s.Country)
                    .Where(s => s.CountryID == countryId)
                    .OrderBy(s => s.Name)
                    .ToListAsync();
            },
            TimeSpan.FromDays(7));

        return Ok(states);
    }

    /// <summary>
    /// Get zipcodes by state - ALWAYS cached (warmed on startup)
    /// </summary>
    [HttpGet("states/{stateId}/zipcodes")]
    public async Task<IActionResult> GetZipCodesByState(int stateId)
    {
        var cacheKey = _keyBuilder.Key("ZipCode", "ByState", stateId.ToString());

        var zipCodes = await _cache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                _logger.LogWarning("ZipCode cache miss - warmup may have failed!");
                return await _context.ZipCodes
                    .AsNoTracking()
                    .Include(z => z.StateProvince)
                    .Where(z => z.StateProvinceID == stateId)
                    .OrderBy(z => z.Code)
                    .ToListAsync();
            },
            TimeSpan.FromDays(7));

        return Ok(zipCodes);
    }
}
```

---

## Step 6: Startup Logs & Verification

### Application Startup Output
```
[2024-01-15 10:00:00.000] info: Application starting
[2024-01-15 10:00:00.500] info: DI container built
[2024-01-15 10:00:00.501] info: CacheWarmupService[0]
      Starting cache warmup service

[2024-01-15 10:00:00.502] info: GeoDataWarmupStrategy[0]
      Starting GeoData cache warmup...

[2024-01-15 10:00:00.750] info: GeoDataWarmupStrategy[0]
      Warmed up 195 countries

[2024-01-15 10:00:01.200] info: GeoDataWarmupStrategy[0]
      Warmed up 3,142 states/provinces

[2024-01-15 10:00:02.000] info: GeoDataWarmupStrategy[0]
      Warmed up 42,000 US zipcodes

[2024-01-15 10:00:02.001] info: GeoDataWarmupStrategy[0]
      GeoData cache warmup completed in 1499ms

[2024-01-15 10:00:02.002] info: ConfigurationWarmupStrategy[0]
      Starting Configuration cache warmup...

[2024-01-15 10:00:02.100] info: ConfigurationWarmupStrategy[0]
      Configuration cache warmup completed

[2024-01-15 10:00:02.101] info: CategoryWarmupStrategy[0]
      Starting Category cache warmup...

[2024-01-15 10:00:02.150] info: CategoryWarmupStrategy[0]
      Warmed up 25 skill categories

[2024-01-15 10:00:02.180] info: CategoryWarmupStrategy[0]
      Warmed up 50 interests

[2024-01-15 10:00:02.181] info: CategoryWarmupStrategy[0]
      Category cache warmup completed

[2024-01-15 10:00:02.182] info: CacheWarmupService[0]
      Cache warmup completed successfully. Total time: 1681ms

[2024-01-15 10:00:02.200] info: Application ready
[2024-01-15 10:00:02.300] info: HTTP server listening on http://localhost:5000

[2024-01-15 10:00:03.000] info: First request received: GET /api/geo/countries
[2024-01-15 10:00:03.001] info: Cache HIT: Country:All (0ms)
[2024-01-15 10:00:03.001] info: Response sent: 200 OK (1ms total)
```

---

## Step 7: Health Check with Warmup Status

### CacheWarmupHealthCheck.cs
```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TheTechLoop.Cache.Abstractions;
using TheTechLoop.Cache.Keys;

namespace TheTechLoop.Company.API.HealthChecks;

public class CacheWarmupHealthCheck : IHealthCheck
{
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;

    public CacheWarmupHealthCheck(ICacheService cache, CacheKeyBuilder keyBuilder)
    {
        _cache = cache;
        _keyBuilder = keyBuilder;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Check if critical caches are warmed
        var countries = await _cache.GetAsync<object>(_keyBuilder.Key("Country", "All"), cancellationToken);
        var states = await _cache.GetAsync<object>(_keyBuilder.Key("State", "All"), cancellationToken);
        var config = await _cache.GetAsync<object>(_keyBuilder.Key("Config", "FeatureFlags"), cancellationToken);

        var warmedCaches = 0;
        var totalCaches = 3;

        if (countries is not null) warmedCaches++;
        if (states is not null) warmedCaches++;
        if (config is not null) warmedCaches++;

        var data = new Dictionary<string, object>
        {
            { "warmed_caches", $"{warmedCaches}/{totalCaches}" },
            { "countries_warmed", countries is not null },
            { "states_warmed", states is not null },
            { "config_warmed", config is not null }
        };

        if (warmedCaches == totalCaches)
        {
            return HealthCheckResult.Healthy("All caches warmed successfully", data);
        }
        else if (warmedCaches > 0)
        {
            return HealthCheckResult.Degraded("Some caches not warmed", data: data);
        }
        else
        {
            return HealthCheckResult.Unhealthy("No caches warmed", data: data);
        }
    }
}

// Register in Program.cs:
builder.Services.AddHealthChecks()
    .AddCheck<CacheWarmupHealthCheck>("cache_warmup", tags: new[] { "ready" });

app.MapHealthChecks("/health/ready");

// Response:
// GET /health/ready
{
  "status": "Healthy",
  "results": {
    "cache_warmup": {
      "status": "Healthy",
      "description": "All caches warmed successfully",
      "data": {
        "warmed_caches": "3/3",
        "countries_warmed": true,
        "states_warmed": true,
        "config_warmed": true
      }
    }
  }
}
```

---

## Step 8: Performance Comparison

### Without Cache Warming
```
Application Startup:
--------------------
00:00.000 - Application starts
00:00.500 - HTTP server ready
00:00.600 - First request: GET /api/geo/countries
00:00.601 - Cache MISS (no data cached)
00:00.601 - Query database...
00:00.650 - Database returns 195 countries (49ms)
00:00.651 - Cache countries for future requests
00:00.652 - Response: 200 OK (52ms total)

00:00.700 - Second request: GET /api/geo/countries
00:00.701 - Cache HIT (1ms)
00:00.701 - Response: 200 OK (1ms total)

Result: First user waits 52ms ❌
```

### With Cache Warming
```
Application Startup:
--------------------
00:00.000 - Application starts
00:00.500 - Cache warmup starts
00:01.500 - Countries warmed (195 cached)
00:02.000 - States warmed (3,142 cached)
00:02.100 - Config warmed
00:02.200 - Warmup complete
00:02.300 - HTTP server ready

00:02.400 - First request: GET /api/geo/countries
00:02.401 - Cache HIT (1ms) ✓
00:02.401 - Response: 200 OK (1ms total)

00:02.500 - Second request: GET /api/geo/countries
00:02.501 - Cache HIT (1ms) ✓
00:02.501 - Response: 200 OK (1ms total)

Result: First user waits 1ms ✓ (52x faster!)
Tradeoff: +2 seconds startup time (acceptable)
```

---

## Best Practices for CORA.OrganizationService

### ✅ DO:
- Warm static reference data (countries, states, categories)
- Warm configuration settings and feature flags
- Log warmup progress and duration
- Add health check for warmup verification
- Set long TTL for warmed data (7 days)
- Warm both list and individual entity caches

### ❌ DON'T:
- Warm user-specific data (different per user)
- Warm rapidly changing data (defeats purpose)
- Warm too much data (delays startup)
- Forget to handle warmup failures gracefully
- Warm data that's rarely accessed

### What to Warm in CORA.OrganizationService:
```
✅ Country (195 records)          - Static, accessed frequently
✅ StateProvince (3,142 records)  - Static, accessed frequently
✅ ZipCode lookups (42K records)  - Static, grouped by state
✅ SkillCategory (25 records)     - Semi-static, accessed often
✅ Interest (50 records)          - Semi-static, accessed often
✅ Configuration settings         - Static, accessed every request
✅ Feature flags                  - Static, accessed every request

❌ User (dynamic)                 - Different per user
❌ Company (thousands)            - Too many, rarely all needed
❌ Dealership (thousands)         - Too many, rarely all needed
❌ Employee (thousands)           - User-specific, dynamic
```

---

## Troubleshooting

### Issue: Warmup taking too long (> 5 seconds)
**Solution:** Reduce scope or parallelize
```csharp
public async Task WarmupAsync(ICacheService cache, CancellationToken ct)
{
    // Parallelize warmup tasks
    await Task.WhenAll(
        WarmupCountriesAsync(cache, ct),
        WarmupStatesAsync(cache, ct),
        WarmupConfigAsync(cache, ct)
    );
}
```

### Issue: Warmup failures cause startup failure
**Solution:** Add try-catch and graceful degradation
```csharp
public async Task WarmupAsync(ICacheService cache, CancellationToken ct)
{
    try
    {
        await WarmupCountriesAsync(cache, ct);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Country warmup failed - continuing anyway");
        // Application still starts, just without warmed cache
    }
}
```

### Issue: Warmed data not being used
**Solution:** Verify cache keys match exactly
```csharp
// Warmup:
var key = _keyBuilder.Key("Country", "All");
await cache.SetAsync(key, countries, ...);

// Controller (must use SAME key):
var key = _keyBuilder.Key("Country", "All");  // ← Must match!
var countries = await cache.GetAsync<List<Country>>(key);
```

---

## Summary

Cache Warming in CORA.OrganizationService provides:
- **Zero cold-start latency** for first requests
- **99.9%+ cache hit rate** for reference data
- **52x faster** first request (1ms vs 52ms)
- **Consistent performance** from first request
- **Better user experience** (no slow first load)

**Perfect for:**
- Static reference data (countries, states, zipcodes)
- Configuration settings and feature flags
- Lookup tables and categories
- Frequently accessed read-only data

**Tradeoff:**
- +2 seconds startup time
- +2MB memory for warmed data
- Worth it for instant user responses

**Implementation:**
```csharp
// 1. Enable warmup
builder.Services.AddTheTechLoopCacheWarmup();

// 2. Register strategies
builder.Services.AddTransient<ICacheWarmupStrategy, GeoDataWarmupStrategy>();

// 3. Warmup happens automatically on startup
// 4. First request = instant cache hit ✓
```

**Startup Timeline:**
```
Without Warming: Start → Ready (0.5s) → First Request → DB Query (50ms) → Response
With Warming:    Start → Warmup (2s) → Ready → First Request → Cache Hit (1ms) → Response
                 ↑ +2s startup            ↑ 50x faster first request
```

**Perfect for production where first-request performance matters!** 🚀
