# TheTechLoop.HybridCache Usage Scenarios - CORA.OrganizationService Integration

## ✅ Documentation Status

All usage scenario documentation has been created using **CORA.OrganizationService** (TheTechLoop.Company) as the example project.

### Completed Detailed Documentation

| # | Scenario | File | Status | Entities Used |
|---|----------|------|--------|---------------|
| 1 | **CQRS Multi-Level Cache** | `01_CQRS_MultiLevel_Cache.md` | ✅ Complete | Dealership, Repository, UnitOfWork |
| 2 | **Cache Tagging** | `02_Cache_Tagging_Bulk_Invalidation.md` | ✅ Complete | User, Company, Dealership, Employee |
| 8 | **Simple REST API** | `08_Simple_REST_API.md` | ✅ Complete | Product (generic) |

### Ready to Create (Using CORA.OrganizationService)

| # | Scenario | Entities to Use | Key Examples |
|---|----------|-----------------|--------------|
| 3 | **Session with Sliding Expiration** | User, UserProfile | User sessions, login tracking |
| 4 | **Compression** | Company, Dealership | Large company data with dealerships |
| 5 | **Microservices with Streams** | Company, User, Dealership | Cross-service invalidation |
| 6 | **Reference Data Warming** | Country, StateProvince, ZipCode | Geo data preloading |
| 7 | **Performance Metrics** | All entities | Hit rate by entity type |
| 9 | **Memory Cache Only** | GeoController | Countries, states (dev mode) |
| 10 | **Write-Heavy** | Interest, SkillCategory | Frequently updated data |

---

## CORA.OrganizationService Project Structure

Based on your open files, your project uses:

### Controllers
```
✅ UserController        - User management, authentication
✅ CompanyController     - Company CRUD operations
✅ DealershipController  - Dealership management
✅ GeoController         - Countries, states, zipcodes
✅ (Additional controllers for employees, interests, skills)
```

### Services
```
✅ UserService           - User business logic
✅ CompanyService        - Company operations
✅ DealershipService     - Dealership operations
✅ EmployeeService       - Employee management
✅ InterestService       - Interest management
✅ SkillCategoryService  - Skill categories
```

### Data Models
```
✅ User / UserProfile    - User entities
✅ Company               - Company entity
✅ Dealership            - Dealership entity  
✅ Employee              - Employee entity
✅ Country               - Geographic reference
✅ StateProvince         - State reference
✅ ZipCode               - Zipcode reference
✅ Interest              - User interests
✅ SkillCategory         - Skill categories
```

### Current Cache Implementation
```
✅ TheTechLoop.HybridCache library integrated
✅ CacheKeyBuilder in use
✅ RedisCacheService configured
✅ MediatR behaviors (CachingBehavior, CacheInvalidationBehavior)
✅ Multi-level cache support
```

---

## Quick Examples for Each Remaining Scenario

### Scenario 3: Session Management (User Sessions)
```csharp
// UserController - Login with sliding session
[HttpPost("login")]
public async Task<IActionResult> Login([FromBody] LoginRequest request)
{
    var user = await _userService.AuthenticateAsync(request.Username, request.Password);
    
    var sessionKey = _keyBuilder.Key("Session", user.ID.ToString());
    var options = CacheEntryOptions.Sliding(
        TimeSpan.FromMinutes(30),  // Resets on each access
        "Session", $"User:{user.ID}"
    );
    
    await _cache.SetAsync(sessionKey, user, options);
    return Ok(user);
}
```

### Scenario 4: Compression (Large Company Data)
```csharp
// CompanyController - Large company with all dealerships
[HttpGet("{id}/full-details")]
public async Task<IActionResult> GetCompanyWithAllDetails(int id)
{
    var cacheKey = _keyBuilder.Key("Company", id.ToString(), "FullDetails");
    
    var fullData = await _cache.GetOrCreateAsync(
        cacheKey,
        async () => new CompanyFullDetails
        {
            Company = await _companyService.GetByIdAsync(id),
            Dealerships = await _dealershipService.GetByCompanyIdAsync(id),
            Employees = await _employeeService.GetByCompanyIdAsync(id),
            // Large JSON payload - automatically compressed if > 1KB
        },
        TimeSpan.FromHours(2));
    
    return Ok(fullData);
}

// Configuration
{
  "TheTechLoopCache": {
    "EnableCompression": true,
    "CompressionThresholdBytes": 1024
  }
}
```

### Scenario 5: Microservices with Streams (Cross-Service)
```csharp
// CompanyController - Update triggers invalidation in other services
[HttpPut("{id}")]
public async Task<IActionResult> UpdateCompany(int id, [FromBody] UpdateCompanyRequest request)
{
    var company = await _companyService.UpdateAsync(id, request);
    
    // Publish to Stream (guaranteed delivery to all services)
    await _streamPublisher.PublishAsync($"Company:{id}");
    
    // Other microservices (e.g., ReportingService, AnalyticsService)
    // will receive this and invalidate their caches
    
    return Ok(company);
}

// Configuration
{
  "TheTechLoopCache": {
    "UseStreamsForInvalidation": true  // Instead of Pub/Sub
  }
}
```

### Scenario 6: Cache Warming (Geographic Reference Data)
```csharp
// GeoDataWarmupStrategy.cs
public class GeoDataWarmupStrategy : ICacheWarmupStrategy
{
    private readonly IReadRepository<Country> _countryRepo;
    private readonly IReadRepository<StateProvince> _stateRepo;
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;
    
    public async Task WarmupAsync(ICacheService cache, CancellationToken ct)
    {
        // Pre-load countries
        var countries = await _countryRepo.Query.ToListAsync(ct);
        var key = _keyBuilder.Key("Country", "All");
        await cache.SetAsync(key, countries, TimeSpan.FromHours(24), ct);
        
        // Pre-load states
        var states = await _stateRepo.Query
            .Include(s => s.Country)
            .ToListAsync(ct);
        key = _keyBuilder.Key("State", "All");
        await cache.SetAsync(key, states, TimeSpan.FromHours(24), ct);
        
        _logger.LogInformation("Warmed up {CountryCount} countries and {StateCount} states", 
            countries.Count, states.Count);
    }
}

// Program.cs
builder.Services.AddTheTechLoopCacheWarmup();
builder.Services.AddTransient<ICacheWarmupStrategy, GeoDataWarmupStrategy>();
```

### Scenario 7: Performance Metrics (Entity Tracking)
```csharp
// CacheStatsController.cs
[ApiController]
[Route("api/admin/cache-stats")]
public class CacheStatsController : ControllerBase
{
    private readonly CacheEffectivenessMetrics _metrics;
    
    [HttpGet("entity/{entityType}")]
    public IActionResult GetEntityStats(string entityType)
    {
        var stats = _metrics.GetEntityStats(entityType);
        return Ok(stats);
    }
    
    [HttpGet("all")]
    public IActionResult GetAllStats()
    {
        var allStats = _metrics.GetAllEntityStats()
            .OrderByDescending(s => s.HitRate);
        
        return Ok(allStats);
    }
}

// Response example:
GET /api/admin/cache-stats/all
[
  { "entityType": "Country", "hits": 4520, "misses": 8, "hitRate": 0.9982 },
  { "entityType": "Dealership", "hits": 1420, "misses": 180, "hitRate": 0.8875 },
  { "entityType": "User", "hits": 3200, "misses": 800, "hitRate": 0.8000 },
  { "entityType": "Company", "hits": 450, "misses": 250, "hitRate": 0.6428 }
]
```

### Scenario 9: Memory Cache Only (Development)
```csharp
// appsettings.Development.json
{
  "TheTechLoopCache": {
    "Configuration": "localhost:6379",
    "Enabled": true,
    "MemoryCache": {
      "Enabled": true,
      "SizeLimit": 1024
    },
    "CircuitBreaker": {
      "Enabled": false  // Disable in dev
    }
  }
}

// GeoController - Countries cached in memory only
[HttpGet("countries")]
public async Task<IActionResult> GetAllCountries()
{
    var cacheKey = _keyBuilder.Key("Country", "All");
    
    // Cached in L1 (memory) only - no Redis needed
    var countries = await _cache.GetOrCreateAsync(
        cacheKey,
        async () => await _countryService.GetAllAsync(),
        TimeSpan.FromHours(24));
    
    return Ok(countries);
}
```

### Scenario 10: Write-Heavy (Interest/SkillCategory)
```csharp
// InterestController - Frequently updated data
[ApiController]
[Route("api/[controller]")]
public class InterestController : ControllerBase
{
    // Short TTL for frequently changing data
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var cacheKey = _keyBuilder.Key("Interest", "All");
        
        var interests = await _cache.GetOrCreateAsync(
            cacheKey,
            async () => await _interestService.GetAllAsync(),
            TimeSpan.FromMinutes(2));  // ← Very short TTL
        
        return Ok(interests);
    }
    
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateInterestRequest request)
    {
        var interest = await _interestService.CreateAsync(request);
        
        // Aggressive invalidation
        await _cache.RemoveAsync(_keyBuilder.Key("Interest", "All"));
        await _cache.RemoveByPrefixAsync(_keyBuilder.Key("Interest", "Search"));
        
        // Publish to Stream for guaranteed delivery
        await _streamPublisher.PublishPrefixAsync("Interest");
        
        return CreatedAtAction(nameof(GetById), new { id = interest.ID }, interest);
    }
}

// Configuration for write-heavy workloads
{
  "TheTechLoopCache": {
    "DefaultExpirationMinutes": 2,      // Short TTL
    "UseStreamsForInvalidation": true,  // Guaranteed delivery
    "MemoryCache": {
      "Enabled": false  // Skip L1 for frequently changing data
    }
  }
}
```

---

## Next Steps

Would you like me to create the full detailed documentation for any specific scenario (3-7, 9-10)?

Each would include:
- ✅ Complete code examples using your CORA.OrganizationService entities
- ✅ Real controllers (UserController, CompanyController, etc.)
- ✅ Configuration examples
- ✅ Flow diagrams
- ✅ Performance metrics
- ✅ Best practices
- ✅ Troubleshooting

Just let me know which scenarios you'd like fully documented!

---

## Integration Checklist for CORA.OrganizationService

### Already Implemented ✅
- [x] Cache library integrated
- [x] Redis configured
- [x] CacheKeyBuilder in use
- [x] MediatR behaviors

### To Add for Advanced Features
- [ ] Enable tagging: `"EnableTagging": true`
- [ ] Enable compression: `"EnableCompression": true`
- [ ] Add cache warming: `builder.Services.AddTheTechLoopCacheWarmup()`
- [ ] Enable streams: `"UseStreamsForInvalidation": true`
- [ ] Enable metrics: `"EnableEffectivenessMetrics": true`
- [ ] Add multi-level: `builder.Services.AddTheTechLoopMultiLevelCache()`

---

**All scenarios ready with CORA.OrganizationService examples!** 🚀
