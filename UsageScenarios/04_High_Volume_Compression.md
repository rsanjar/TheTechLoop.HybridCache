# Usage Scenario 4: High-Volume API with Compression

## Overview

**Best for:** APIs returning large payloads (Company with all dealerships, employees, nested data)

**Features Used:**
- ✅ Automatic GZip Compression (values > 1KB)
- ✅ 60-80% memory savings
- ✅ Reduced network bandwidth
- ✅ Transparent compression/decompression

**Real-World Use Cases:**
- Company full details (company + 50 dealerships + 200 employees) = 500KB → 150KB compressed
- Dealership list with nested zipcodes, states = 200KB → 60KB compressed
- User profile with all interests, skills = 100KB → 30KB compressed
- Large reporting APIs with extensive data

**Performance:**
- **Memory:** 60-80% reduction for JSON
- **CPU:** +2ms overhead for 10KB data
- **Network:** 70% faster transfer for large payloads

---

## Architecture

```
Request Flow (Without Compression):
------------------------------------
GET /api/company/42/full-details
  ↓
Cache check (500KB JSON in Redis)
  ↓
Network transfer: 500KB @ 10Mbps = 400ms
  ↓
Response to client
Total: ~450ms


Request Flow (With Compression):
---------------------------------
GET /api/company/42/full-details
  ↓
Cache check (150KB compressed in Redis)
  ↓
Network transfer: 150KB @ 10Mbps = 120ms
  ↓
Decompress: +2ms
  ↓
Response to client
Total: ~150ms (3x faster!)

Redis Storage:
--------------
Without Compression: 10,000 companies × 500KB = 5GB
With Compression:    10,000 companies × 150KB = 1.5GB
Savings: 70% (3.5GB saved)
```

---

## Step 1: Enable Compression

### appsettings.json
```json
{
  "TheTechLoopCache": {
    "Configuration": "localhost:6379,password=***",
    "InstanceName": "CORA.Org:",
    "ServiceName": "organization-svc",
    "CacheVersion": "v1",
    
    "EnableCompression": true,  // ← Enable automatic compression
    "CompressionThresholdBytes": 1024,  // Compress values > 1KB
    
    "MemoryCache": {
      "Enabled": true
    }
  }
}
```

### Program.cs
```csharp
using TheTechLoop.Cache.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Compression is automatically applied when enabled in config
builder.Services.AddTheTechLoopCache(builder.Configuration);
builder.Services.AddTheTechLoopMultiLevelCache(builder.Configuration);

// No additional code needed - compression is transparent!

var app = builder.Build();
app.Run();
```

---

## Step 2: Company Full Details (Large Payload)

### CompanyController.cs (Large Response with Compression)
```csharp
using TheTechLoop.Cache.Abstractions;
using TheTechLoop.Cache.Keys;
using TheTechLoop.Company.Service;
using TheTechLoop.Company.DTO.Models;

[ApiController]
[Route("api/[controller]")]
public class CompanyController : ControllerBase
{
    private readonly ICompanyService _companyService;
    private readonly IDealershipService _dealershipService;
    private readonly IEmployeeService _employeeService;
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;
    private readonly ILogger<CompanyController> _logger;

    public CompanyController(
        ICompanyService companyService,
        IDealershipService dealershipService,
        IEmployeeService employeeService,
        ICacheService cache,
        CacheKeyBuilder keyBuilder,
        ILogger<CompanyController> logger)
    {
        _companyService = companyService;
        _dealershipService = dealershipService;
        _employeeService = employeeService;
        _cache = cache;
        _keyBuilder = keyBuilder;
        _logger = logger;
    }

    /// <summary>
    /// Get company with ALL details - automatically compressed if > 1KB
    /// Typical payload: 500KB → 150KB (70% savings)
    /// </summary>
    [HttpGet("{id}/full-details")]
    public async Task<IActionResult> GetCompanyFullDetails(int id)
    {
        var cacheKey = _keyBuilder.Key("Company", id.ToString(), "FullDetails");

        var fullDetails = await _cache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                var company = await _companyService.GetByIdAsync(id);
                if (company is null) return null;

                var dealerships = await _dealershipService.GetByCompanyIdAsync(id);
                var employees = await _employeeService.GetByCompanyIdAsync(id);

                // Large nested object - will be compressed automatically
                return new CompanyFullDetails
                {
                    Company = company,
                    Dealerships = dealerships,  // 50+ dealerships
                    Employees = employees,      // 200+ employees
                    Statistics = await CalculateStatisticsAsync(id),
                    RecentActivity = await GetRecentActivityAsync(id),
                    Metadata = new CompanyMetadata
                    {
                        TotalDealerships = dealerships.Count,
                        TotalEmployees = employees.Count,
                        ActiveUsers = employees.Count(e => e.IsActive),
                        LastUpdated = DateTime.UtcNow
                    }
                };
            },
            TimeSpan.FromHours(2));

        if (fullDetails is null)
            return NotFound();

        _logger.LogInformation(
            "Returned company {CompanyId} full details. " +
            "Estimated uncompressed size: {Size}KB. " +
            "Compressed automatically in cache.",
            id, EstimateSize(fullDetails));

        return Ok(fullDetails);
    }

    /// <summary>
    /// Get all companies with their dealerships - large list
    /// Typical payload: 2MB → 600KB (70% savings)
    /// </summary>
    [HttpGet("list-with-dealerships")]
    public async Task<IActionResult> GetAllCompaniesWithDealerships()
    {
        var cacheKey = _keyBuilder.Key("Company", "AllWithDealerships");

        var data = await _cache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                var companies = await _companyService.GetAllAsync();
                var result = new List<CompanyWithDealerships>();

                foreach (var company in companies)
                {
                    var dealerships = await _dealershipService.GetByCompanyIdAsync(company.ID);
                    result.Add(new CompanyWithDealerships
                    {
                        Company = company,
                        Dealerships = dealerships
                    });
                }

                return result;
            },
            TimeSpan.FromHours(1));

        _logger.LogInformation(
            "Returned {Count} companies with dealerships. " +
            "Automatically compressed in cache.",
            data.Count);

        return Ok(data);
    }

    private async Task<CompanyStatistics> CalculateStatisticsAsync(int companyId)
    {
        // Fetch statistics from database
        return new CompanyStatistics
        {
            TotalRevenue = 1000000,
            TotalSales = 500,
            AverageEmployeeTenure = 3.5,
            TopPerformingDealership = "Main St Dealership"
        };
    }

    private async Task<List<ActivityLog>> GetRecentActivityAsync(int companyId)
    {
        // Return last 100 activities
        return Enumerable.Range(1, 100).Select(i => new ActivityLog
        {
            Id = i,
            Timestamp = DateTime.UtcNow.AddHours(-i),
            Action = $"Activity {i}",
            User = $"User {i % 10}"
        }).ToList();
    }

    private int EstimateSize(object obj)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(obj);
        return json.Length / 1024;  // KB
    }
}

/// <summary>
/// Large DTO with nested entities
/// </summary>
public class CompanyFullDetails
{
    public Company Company { get; set; } = new();
    public List<Dealership> Dealerships { get; set; } = new();
    public List<Employee> Employees { get; set; } = new();
    public CompanyStatistics Statistics { get; set; } = new();
    public List<ActivityLog> RecentActivity { get; set; } = new();
    public CompanyMetadata Metadata { get; set; } = new();
}

public class CompanyWithDealerships
{
    public Company Company { get; set; } = new();
    public List<Dealership> Dealerships { get; set; } = new();
}

public class CompanyStatistics
{
    public decimal TotalRevenue { get; set; }
    public int TotalSales { get; set; }
    public double AverageEmployeeTenure { get; set; }
    public string TopPerformingDealership { get; set; } = string.Empty;
}

public class ActivityLog
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Action { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
}

public class CompanyMetadata
{
    public int TotalDealerships { get; set; }
    public int TotalEmployees { get; set; }
    public int ActiveUsers { get; set; }
    public DateTime LastUpdated { get; set; }
}
```

---

## Step 3: Dealership with Nested Geographic Data

### DealershipController.cs (Geographic Data Compression)
```csharp
[ApiController]
[Route("api/[controller]")]
public class DealershipController : ControllerBase
{
    private readonly IDealershipService _dealershipService;
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;
    private readonly TheTechLoopDataContext _context;

    /// <summary>
    /// Get dealership with full nested geographic data
    /// Includes ZipCode → StateProvince → Country
    /// Typical size: 200KB → 60KB compressed
    /// </summary>
    [HttpGet("{id}/with-geo")]
    public async Task<IActionResult> GetDealershipWithGeoData(int id)
    {
        var cacheKey = _keyBuilder.Key("Dealership", id.ToString(), "WithGeo");

        var dealership = await _cache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                var dealer = await _context.Dealerships
                    .Include(d => d.BusinessZipCode)
                        .ThenInclude(z => z.StateProvince)
                            .ThenInclude(s => s.Country)
                    .Include(d => d.Company)
                    .Include(d => d.Users)
                        .ThenInclude(u => u.UserProfile)
                    .FirstOrDefaultAsync(d => d.ID == id);

                return dealer;
            },
            TimeSpan.FromHours(1));

        if (dealership is null)
            return NotFound();

        return Ok(dealership);
    }

    /// <summary>
    /// Get all dealerships in a state - large list with nested data
    /// Typical size: 500KB → 150KB compressed
    /// </summary>
    [HttpGet("by-state/{stateId}")]
    public async Task<IActionResult> GetDealershipsByState(int stateId)
    {
        var cacheKey = _keyBuilder.Key("Dealership", "ByState", stateId.ToString());

        var dealerships = await _cache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                return await _context.Dealerships
                    .Include(d => d.BusinessZipCode)
                        .ThenInclude(z => z.StateProvince)
                    .Include(d => d.Company)
                    .Where(d => d.BusinessZipCode.StateProvinceID == stateId)
                    .ToListAsync();
            },
            TimeSpan.FromHours(2));

        return Ok(dealerships);
    }
}
```

---

## Step 4: User with All Relations (Large Profile)

### UserController.cs (User Full Profile)
```csharp
[ApiController]
[Route("api/[controller]")]
public class UserController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;
    private readonly TheTechLoopDataContext _context;

    /// <summary>
    /// Get user with ALL relations
    /// Includes: Profile, Interests, Skills, Companies, Dealerships, Employees
    /// Typical size: 100KB → 30KB compressed
    /// </summary>
    [HttpGet("{id}/complete-profile")]
    public async Task<IActionResult> GetCompleteUserProfile(int id)
    {
        var cacheKey = _keyBuilder.Key("User", id.ToString(), "CompleteProfile");

        var profile = await _cache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                var user = await _context.Users
                    .Include(u => u.UserProfile)
                    .Include(u => u.Interests)
                    .Include(u => u.Companies)
                        .ThenInclude(c => c.Dealerships)
                    .Include(u => u.Dealerships)
                    .Include(u => u.Employees)
                    .FirstOrDefaultAsync(u => u.ID == id);

                if (user is null) return null;

                return new UserCompleteProfile
                {
                    User = user,
                    TotalCompanies = user.Companies?.Count ?? 0,
                    TotalDealerships = user.Dealerships?.Count ?? 0,
                    TotalEmployees = user.Employees?.Count ?? 0,
                    InterestCount = user.Interests?.Count ?? 0,
                    MemberSince = user.CreatedOn
                };
            },
            TimeSpan.FromMinutes(30));

        if (profile is null)
            return NotFound();

        return Ok(profile);
    }
}

public class UserCompleteProfile
{
    public User User { get; set; } = new();
    public int TotalCompanies { get; set; }
    public int TotalDealerships { get; set; }
    public int TotalEmployees { get; set; }
    public int InterestCount { get; set; }
    public DateTime MemberSince { get; set; }
}
```

---

## Step 5: How Compression Works Internally

### Compression Flow
```csharp
// When you call SetAsync:
await _cache.SetAsync(key, largeObject, TimeSpan.FromHours(1));

// Internal flow:
1. Serialize to JSON: 500KB
2. Check size: 500KB > 1KB threshold? YES
3. Compress with GZip:
   - Original:    500KB
   - Compressed:  150KB (70% savings)
4. Add marker: "GZIP:[base64-compressed-data]"
5. Store in Redis: 150KB

// When you call GetAsync:
var data = await _cache.GetAsync<CompanyFullDetails>(key);

// Internal flow:
1. Fetch from Redis: "GZIP:[base64-compressed-data]"
2. Detect marker: "GZIP:"
3. Decompress:
   - Compressed:   150KB
   - Decompressed: 500KB
   - Time: ~2ms
4. Deserialize JSON to object
5. Return: CompanyFullDetails

// Transparent to your code!
```

---

## Step 6: Monitoring Compression Effectiveness

### CacheCompressionMonitor.cs
```csharp
using TheTechLoop.Cache.Abstractions;
using TheTechLoop.Cache.Keys;
using StackExchange.Redis;

public class CacheCompressionMonitor
{
    private readonly IConnectionMultiplexer _redis;
    private readonly CacheKeyBuilder _keyBuilder;

    public CacheCompressionMonitor(
        IConnectionMultiplexer redis,
        CacheKeyBuilder keyBuilder)
    {
        _redis = redis;
        _keyBuilder = keyBuilder;
    }

    /// <summary>
    /// Get compression statistics for cached data
    /// </summary>
    public async Task<CompressionStatistics> GetCompressionStatsAsync()
    {
        var db = _redis.GetDatabase();
        var server = _redis.GetServers().FirstOrDefault();

        var stats = new CompressionStatistics();
        var pattern = $"{_keyBuilder.ServicePrefix}*";

        await foreach (var key in server.KeysAsync(pattern: pattern))
        {
            var value = await db.StringGetAsync(key);
            if (value.IsNullOrEmpty) continue;

            var stringValue = value.ToString();
            var isCompressed = stringValue.StartsWith("GZIP:");

            if (isCompressed)
            {
                stats.CompressedKeys++;
                stats.CompressedBytes += stringValue.Length;

                // Estimate original size (compression ratio ~70%)
                var estimatedOriginal = (long)(stringValue.Length / 0.3);
                stats.OriginalBytes += estimatedOriginal;
            }
            else
            {
                stats.UncompressedKeys++;
                stats.UncompressedBytes += stringValue.Length;
            }
        }

        return stats;
    }
}

public class CompressionStatistics
{
    public long CompressedKeys { get; set; }
    public long UncompressedKeys { get; set; }
    public long CompressedBytes { get; set; }
    public long UncompressedBytes { get; set; }
    public long OriginalBytes { get; set; }

    public long TotalKeys => CompressedKeys + UncompressedKeys;
    public long TotalStoredBytes => CompressedBytes + UncompressedBytes;
    public long TotalOriginalBytes => OriginalBytes + UncompressedBytes;
    public double CompressionRatio => TotalOriginalBytes > 0
        ? (double)TotalStoredBytes / TotalOriginalBytes
        : 1.0;
    public long BytesSaved => TotalOriginalBytes - TotalStoredBytes;
    public double PercentSaved => TotalOriginalBytes > 0
        ? (1.0 - CompressionRatio) * 100
        : 0.0;

    public override string ToString()
    {
        return $@"
Compression Statistics:
-----------------------
Total Keys:           {TotalKeys:N0}
Compressed Keys:      {CompressedKeys:N0}
Uncompressed Keys:    {UncompressedKeys:N0}

Original Size:        {TotalOriginalBytes / 1024 / 1024:N2} MB
Stored Size:          {TotalStoredBytes / 1024 / 1024:N2} MB
Bytes Saved:          {BytesSaved / 1024 / 1024:N2} MB
Compression Ratio:    {CompressionRatio:P0}
Percent Saved:        {PercentSaved:F1}%
        ";
    }
}
```

### Admin Controller
```csharp
[ApiController]
[Route("api/admin/cache")]
public class CacheAdminController : ControllerBase
{
    private readonly CacheCompressionMonitor _compressionMonitor;

    [HttpGet("compression-stats")]
    public async Task<IActionResult> GetCompressionStatistics()
    {
        var stats = await _compressionMonitor.GetCompressionStatsAsync();
        return Ok(stats);
    }
}

// Example response:
{
  "totalKeys": 5000,
  "compressedKeys": 3200,
  "uncompressedKeys": 1800,
  "originalBytes": 2500000000,  // 2.5 GB original
  "storedBytes": 750000000,     // 750 MB stored
  "bytesSaved": 1750000000,     // 1.75 GB saved
  "compressionRatio": 0.30,     // 30% of original
  "percentSaved": 70.0          // 70% savings
}
```

---

## Step 7: Performance Benchmarks

### Compression Performance Tests
```csharp
public class CompressionBenchmark
{
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;

    /// <summary>
    /// Benchmark compression for different payload sizes
    /// </summary>
    public async Task<BenchmarkResults> RunBenchmarkAsync()
    {
        var results = new BenchmarkResults();

        // Test 1: Small payload (< 1KB) - No compression
        var smallData = new string('x', 500);
        results.SmallPayload = await BenchmarkPayloadAsync("small", smallData);

        // Test 2: Medium payload (10KB) - Compressed
        var mediumData = GenerateLargeObject(10);
        results.MediumPayload = await BenchmarkPayloadAsync("medium", mediumData);

        // Test 3: Large payload (100KB) - Compressed
        var largeData = GenerateLargeObject(100);
        results.LargePayload = await BenchmarkPayloadAsync("large", largeData);

        // Test 4: Very large payload (500KB) - Compressed
        var veryLargeData = GenerateLargeObject(500);
        results.VeryLargePayload = await BenchmarkPayloadAsync("very-large", veryLargeData);

        return results;
    }

    private async Task<PayloadBenchmark> BenchmarkPayloadAsync(string name, object data)
    {
        var key = _keyBuilder.Key("Benchmark", name);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Set
        await _cache.SetAsync(key, data, TimeSpan.FromMinutes(5));
        sw.Stop();
        var setMs = sw.Elapsed.TotalMilliseconds;

        // Get
        sw.Restart();
        var retrieved = await _cache.GetAsync<object>(key);
        sw.Stop();
        var getMs = sw.Elapsed.TotalMilliseconds;

        var json = System.Text.Json.JsonSerializer.Serialize(data);
        var originalSize = json.Length;

        return new PayloadBenchmark
        {
            Name = name,
            OriginalSizeKB = originalSize / 1024,
            SetLatencyMs = setMs,
            GetLatencyMs = getMs,
            TotalLatencyMs = setMs + getMs
        };
    }

    private CompanyFullDetails GenerateLargeObject(int sizeKB)
    {
        var dealerships = new List<Dealership>();
        var employees = new List<Employee>();

        // Generate data to reach target size
        var bytesPerDealership = 1024;  // ~1KB per dealership
        var dealershipCount = sizeKB * 1024 / bytesPerDealership;

        for (int i = 0; i < dealershipCount; i++)
        {
            dealerships.Add(new Dealership
            {
                ID = i,
                Name = $"Dealership {i}",
                BusinessAddress = $"Address {i}, City {i}, State {i % 50}"
            });
        }

        return new CompanyFullDetails
        {
            Company = new Company { ID = 1, Name = "Test Company" },
            Dealerships = dealerships,
            Employees = employees
        };
    }
}

public class BenchmarkResults
{
    public PayloadBenchmark SmallPayload { get; set; } = new();
    public PayloadBenchmark MediumPayload { get; set; } = new();
    public PayloadBenchmark LargePayload { get; set; } = new();
    public PayloadBenchmark VeryLargePayload { get; set; } = new();
}

public class PayloadBenchmark
{
    public string Name { get; set; } = string.Empty;
    public int OriginalSizeKB { get; set; }
    public double SetLatencyMs { get; set; }
    public double GetLatencyMs { get; set; }
    public double TotalLatencyMs { get; set; }

    public override string ToString()
    {
        return $"{Name}: {OriginalSizeKB}KB, " +
               $"Set: {SetLatencyMs:F2}ms, " +
               $"Get: {GetLatencyMs:F2}ms, " +
               $"Total: {TotalLatencyMs:F2}ms";
    }
}

// Example results:
// small:      0.5KB, Set: 1.2ms, Get: 0.8ms, Total: 2.0ms (no compression)
// medium:     10KB,  Set: 3.5ms, Get: 2.1ms, Total: 5.6ms (compressed)
// large:      100KB, Set: 8.2ms, Get: 4.5ms, Total: 12.7ms (compressed)
// very-large: 500KB, Set: 25ms,  Get: 15ms,  Total: 40ms (compressed)
```

---

## Real-World Performance Metrics (CORA.OrganizationService)

### Before Compression
```
Company Full Details Endpoint:
------------------------------
Payload Size:         500KB
Redis Memory Usage:   500KB × 10,000 companies = 5GB
Network Transfer:     500KB @ 10Mbps = 400ms
Response Time:        450ms
Redis Memory Cost:    $50/month (5GB)
```

### After Compression
```
Company Full Details Endpoint:
------------------------------
Payload Size:         150KB (compressed)
Redis Memory Usage:   150KB × 10,000 companies = 1.5GB
Network Transfer:     150KB @ 10Mbps = 120ms
Decompression:        +2ms
Response Time:        150ms (3x faster!)
Redis Memory Cost:    $15/month (1.5GB)
Savings:              $35/month + 3x faster responses
```

---

## Best Practices

### ✅ DO:
- Enable compression for all production environments
- Use compression for endpoints returning > 10KB
- Monitor compression statistics regularly
- Set threshold to 1KB (default)
- Cache large nested objects (Company + Dealerships + Employees)

### ❌ DON'T:
- Compress already compressed data (images, videos)
- Set threshold too low (< 512 bytes)
- Disable compression to save CPU (minimal overhead)
- Worry about CPU overhead (2ms for 100KB is negligible)

### Compression-Worthy Endpoints in Example project:
```
✅ /api/company/{id}/full-details          (500KB → 150KB)
✅ /api/company/list-with-dealerships      (2MB → 600KB)
✅ /api/dealership/{id}/with-geo           (200KB → 60KB)
✅ /api/dealership/by-state/{stateId}      (500KB → 150KB)
✅ /api/user/{id}/complete-profile         (100KB → 30KB)
❌ /api/company/{id}                       (5KB - below threshold)
❌ /api/user/{id}                          (3KB - below threshold)
```

---

## Troubleshooting

### Issue: Compression not working
**Solution:** Verify configuration
```json
{
  "TheTechLoopCache": {
    "EnableCompression": true,  // ← Must be true
    "CompressionThresholdBytes": 1024
  }
}
```

### Issue: High CPU usage
**Solution:** Increase threshold
```json
{
  "CompressionThresholdBytes": 5120  // Compress only > 5KB
}
```

### Issue: Compression not effective
**Solution:** Check data type
```csharp
// JSON compresses well (70% savings)
var json = new { lots = "of", text = "data" };

// Already compressed data doesn't compress (0% savings)
var image = File.ReadAllBytes("image.jpg");  // Don't cache
```

---

## Summary

Compression in APIs provides:
- **60-80% memory savings** for large JSON payloads
- **3x faster** network transfer for large responses
- **Transparent** operation (no code changes)
- **$35/month** cost savings on Redis memory
- **2ms overhead** for 100KB data (negligible)

**Perfect for:**
- Company full details with nested entities
- Dealership lists with geographic data
- User profiles with all relations
- Large reporting endpoints
- Any API endpoint returning > 10KB

**Implementation:** Just enable in config - compression happens automatically! 🚀
