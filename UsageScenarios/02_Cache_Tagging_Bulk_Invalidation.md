# Usage Scenario 2: Cache Tagging for Bulk Invalidation

## Overview

**Best for:** Scenarios where related data must be invalidated together

**Features Used:**
- ✅ Cache Tagging with Redis Sets
- ✅ Group invalidation (all user data at once)
- ✅ Tag-based cache management
- ✅ O(1) tag membership queries

**Real-World Use Cases:**  
- User logout → invalidate all user sessions + preferences + permissions
- Company update → invalidate company + dealerships + employees
- Role change → invalidate user permissions + menu access
- Tenant data refresh → invalidate all tenant-related caches

---

## Architecture

```
User Data Structure:
--------------------
user:123:profile          ← Tagged: ["User", "User:123"]
user:123:preferences      ← Tagged: ["User", "User:123"]
user:123:permissions      ← Tagged: ["User", "User:123", "Permissions"]
user:123:dealerships      ← Tagged: ["User", "User:123", "Dealership"]
user:123:companies        ← Tagged: ["User", "User:123", "Company"]

Company Data Structure:
-----------------------
company:456:details       ← Tagged: ["Company", "Company:456"]
company:456:dealerships   ← Tagged: ["Company", "Company:456", "Dealership"]
company:456:employees     ← Tagged: ["Company", "Company:456", "Employee"]
company:456:settings      ← Tagged: ["Company", "Company:456"]

Invalidation Scenarios:
-----------------------
User logs out             → RemoveByTagAsync("User:123")
User role changed         → RemoveByTagAsync("Permissions")
Company updated           → RemoveByTagAsync("Company:456")
All reference data        → RemoveByTagAsync("Reference")
```

---

## Step 1: Enable Tagging

### appsettings.json
```json
{
  "TheTechLoopCache": {
    "Configuration": "localhost:6379,password=***",
    "InstanceName": "CORA.Org:",
    "ServiceName": "organization-svc",
    "CacheVersion": "v1",
    "EnableTagging": true,  // ← Enable tagging
    "MemoryCache": {
      "Enabled": true
    }
  }
}
```

### Program.cs
```csharp
using TheTechLoop.HybridCache.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register cache with tagging support
builder.Services.AddTheTechLoopCache(builder.Configuration);
builder.Services.AddTheTechLoopMultiLevelCache(builder.Configuration);

// ICacheTagService is automatically registered when EnableTagging = true

var app = builder.Build();
app.Run();
```

---

## Step 2: User Management with Tagging

### Scenario: User Login/Logout

**UserController.cs:**
```csharp
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Tagging;
using TheTechLoop.HybridCache.Keys;

[ApiController]
[Route("api/[controller]")]
public class UserController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly ICacheService _cache;
    private readonly ICacheTagService _tagService;
    private readonly CacheKeyBuilder _keyBuilder;

    public UserController(
        IUserService userService,
        ICacheService cache,
        ICacheTagService tagService,
        CacheKeyBuilder keyBuilder)
    {
        _userService = userService;
        _cache = cache;
        _tagService = tagService;
        _keyBuilder = keyBuilder;
    }

    /// <summary>
    /// Login user and cache profile with tags
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _userService.AuthenticateAsync(request.Username, request.Password);
        if (user is null) return Unauthorized();

        // Cache user profile with multiple tags
        var profileKey = _keyBuilder.Key("User", user.ID.ToString(), "Profile");
        var options = CacheEntryOptions.Absolute(
            TimeSpan.FromHours(2),
            "User",                    // Generic user tag
            $"User:{user.ID}",        // Specific user tag
            "Session"                  // Session tag for all active sessions
        );

        await _cache.SetAsync(profileKey, user, options);

        // Cache user permissions
        var permissions = await _userService.GetPermissionsAsync(user.ID);
        var permKey = _keyBuilder.Key("User", user.ID.ToString(), "Permissions");
        var permOptions = CacheEntryOptions.Absolute(
            TimeSpan.FromHours(1),
            "User",
            $"User:{user.ID}",
            "Permissions"
        );

        await _cache.SetAsync(permKey, permissions, permOptions);

        // Cache user preferences
        var preferences = await _userService.GetPreferencesAsync(user.ID);
        var prefKey = _keyBuilder.Key("User", user.ID.ToString(), "Preferences");
        await _cache.SetAsync(prefKey, preferences, 
            CacheEntryOptions.Absolute(TimeSpan.FromHours(24), "User", $"User:{user.ID}"));

        return Ok(new
        {
            user,
            token = GenerateJwtToken(user),
            message = "Login successful - all user data cached"
        });
    }

    /// <summary>
    /// Logout user - invalidate ALL user-related caches with one call
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromHeader(Name = "X-User-Id")] int userId)
    {
        // Single call removes ALL caches for this user:
        // - user:123:profile
        // - user:123:permissions
        // - user:123:preferences
        // - user:123:dealerships
        // - user:123:companies
        await _tagService.RemoveByTagAsync($"User:{userId}");

        _logger.LogInformation("Logged out user {UserId} - all caches invalidated", userId);

        return Ok(new { message = "Logged out successfully" });
    }

    /// <summary>
    /// Get user profile (from cache)
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetProfile(int id)
    {
        var cacheKey = _keyBuilder.Key("User", id.ToString(), "Profile");

        var user = await _cache.GetOrCreateAsync(
            cacheKey,
            async () => await _userService.GetByIdAsync(id),
            TimeSpan.FromHours(2));

        return user is null ? NotFound() : Ok(user);
    }

    /// <summary>
    /// Update user - invalidate user-specific caches only
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateProfile(int id, [FromBody] UpdateUserRequest request)
    {
        var user = await _userService.UpdateAsync(id, request);
        if (user is null) return NotFound();

        // Invalidate all caches for this user
        await _tagService.RemoveByTagAsync($"User:{id}");

        return Ok(user);
    }

    /// <summary>
    /// Change user role - invalidate ALL permissions caches (all users)
    /// </summary>
    [HttpPost("{id}/change-role")]
    public async Task<IActionResult> ChangeRole(int id, [FromBody] ChangeRoleRequest request)
    {
        await _userService.ChangeRoleAsync(id, request.NewRole);

        // Invalidate this user's caches
        await _tagService.RemoveByTagAsync($"User:{id}");

        // Invalidate ALL permissions caches (affects all users)
        await _tagService.RemoveByTagAsync("Permissions");

        return Ok(new { message = "Role changed - permissions reloaded for all users" });
    }
}
```

---

## Step 3: Company Management with Tagging

### Scenario: Company with Nested Entities

**CompanyController.cs:**
```csharp
[ApiController]
[Route("api/[controller]")]
public class CompanyController : ControllerBase
{
    private readonly ICompanyService _companyService;
    private readonly IDealershipService _dealershipService;
    private readonly IEmployeeService _employeeService;
    private readonly ICacheService _cache;
    private readonly ICacheTagService _tagService;
    private readonly CacheKeyBuilder _keyBuilder;

    /// <summary>
    /// Get company with full details (cached with tags)
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetCompany(int id)
    {
        // Cache company details
        var companyKey = _keyBuilder.Key("Company", id.ToString(), "Details");
        var company = await _cache.GetOrCreateAsync(
            companyKey,
            async () => await _companyService.GetByIdAsync(id),
            TimeSpan.FromHours(2));

        if (company is null) return NotFound();

        // Cache company dealerships
        var dealershipsKey = _keyBuilder.Key("Company", id.ToString(), "Dealerships");
        var dealerships = await _cache.GetOrCreateAsync(
            dealershipsKey,
            async () => await _dealershipService.GetByCompanyIdAsync(id),
            TimeSpan.FromHours(1));

        // Cache company employees
        var employeesKey = _keyBuilder.Key("Company", id.ToString(), "Employees");
        var employees = await _cache.GetOrCreateAsync(
            employeesKey,
            async () => await _employeeService.GetByCompanyIdAsync(id),
            TimeSpan.FromMinutes(30));

        return Ok(new
        {
            company,
            dealerships,
            employees,
            metadata = new
            {
                cached_keys = new[] { companyKey, dealershipsKey, employeesKey },
                tags = new[] { "Company", $"Company:{id}", "Dealership", "Employee" }
            }
        });
    }

    /// <summary>
    /// Create company - tag all related caches
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateCompany([FromBody] CreateCompanyRequest request)
    {
        var company = await _companyService.CreateAsync(request);

        // Cache new company with tags
        var companyKey = _keyBuilder.Key("Company", company.ID.ToString(), "Details");
        var options = CacheEntryOptions.Absolute(
            TimeSpan.FromHours(2),
            "Company",
            $"Company:{company.ID}"
        );

        await _cache.SetAsync(companyKey, company, options);

        // Invalidate company list caches
        await _tagService.RemoveByTagAsync("CompanyList");

        return CreatedAtAction(nameof(GetCompany), new { id = company.ID }, company);
    }

    /// <summary>
    /// Update company - invalidate ALL company-related caches
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateCompany(int id, [FromBody] UpdateCompanyRequest request)
    {
        var company = await _companyService.UpdateAsync(id, request);
        if (company is null) return NotFound();

        // Single call invalidates:
        // - company:123:details
        // - company:123:dealerships
        // - company:123:employees
        // - company:123:settings
        await _tagService.RemoveByTagAsync($"Company:{id}");

        return Ok(company);
    }

    /// <summary>
    /// Delete company - cascade invalidation
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteCompany(int id)
    {
        // Get dealerships before delete for cascade invalidation
        var dealerships = await _dealershipService.GetByCompanyIdAsync(id);

        var success = await _companyService.DeleteAsync(id);
        if (!success) return NotFound();

        // Invalidate company caches
        await _tagService.RemoveByTagAsync($"Company:{id}");

        // Invalidate each dealership's caches
        foreach (var dealership in dealerships)
        {
            await _tagService.RemoveByTagAsync($"Dealership:{dealership.ID}");
        }

        // Invalidate list caches
        await _tagService.RemoveByTagAsync("CompanyList");
        await _tagService.RemoveByTagAsync("DealershipList");

        return NoContent();
    }
}
```

---

## Step 4: Dealership Management with Company Linking

**DealershipController.cs:**
```csharp
[ApiController]
[Route("api/[controller]")]
public class DealershipController : ControllerBase
{
    private readonly IDealershipService _dealershipService;
    private readonly ICacheService _cache;
    private readonly ICacheTagService _tagService;
    private readonly CacheKeyBuilder _keyBuilder;

    /// <summary>
    /// Get dealership (affects company caches too)
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetDealership(int id)
    {
        var cacheKey = _keyBuilder.Key("Dealership", id.ToString());

        var dealership = await _cache.GetOrCreateAsync(
            cacheKey,
            async () => await _dealershipService.GetByIdAsync(id),
            TimeSpan.FromMinutes(30));

        return dealership is null ? NotFound() : Ok(dealership);
    }

    /// <summary>
    /// Update dealership - invalidate dealership + parent company caches
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateDealership(int id, [FromBody] UpdateDealershipRequest request)
    {
        var dealership = await _dealershipService.UpdateAsync(id, request);
        if (dealership is null) return NotFound();

        // Invalidate dealership caches
        await _tagService.RemoveByTagAsync($"Dealership:{id}");

        // Invalidate parent company's dealership list
        if (dealership.CompanyID.HasValue)
        {
            var companyDealershipsKey = _keyBuilder.Key("Company", dealership.CompanyID.Value.ToString(), "Dealerships");
            await _cache.RemoveAsync(companyDealershipsKey);
        }

        return Ok(dealership);
    }

    /// <summary>
    /// Transfer dealership to another company
    /// </summary>
    [HttpPost("{id}/transfer")]
    public async Task<IActionResult> TransferDealership(int id, [FromBody] TransferDealershipRequest request)
    {
        var dealership = await _dealershipService.GetByIdAsync(id);
        if (dealership is null) return NotFound();

        var oldCompanyId = dealership.CompanyID;
        var newCompanyId = request.NewCompanyId;

        // Transfer dealership
        await _dealershipService.TransferAsync(id, newCompanyId);

        // Invalidate dealership
        await _tagService.RemoveByTagAsync($"Dealership:{id}");

        // Invalidate old company's dealerships
        if (oldCompanyId.HasValue)
        {
            var oldKey = _keyBuilder.Key("Company", oldCompanyId.Value.ToString(), "Dealerships");
            await _cache.RemoveAsync(oldKey);
        }

        // Invalidate new company's dealerships
        var newKey = _keyBuilder.Key("Company", newCompanyId.ToString(), "Dealerships");
        await _cache.RemoveAsync(newKey);

        return Ok(new { message = $"Dealership transferred from company {oldCompanyId} to {newCompanyId}" });
    }
}
```

---

## Step 5: Advanced Tagging Patterns

### Pattern 1: Hierarchical Tags (User → Sessions → Permissions)
```csharp
public class AuthService
{
    private readonly ICacheService _cache;
    private readonly ICacheTagService _tagService;

    /// <summary>
    /// Cache user session with hierarchical tags
    /// </summary>
    public async Task CacheUserSessionAsync(int userId, string sessionId, UserSession session)
    {
        var sessionKey = _keyBuilder.Key("Session", sessionId);

        var options = CacheEntryOptions.Sliding(
            TimeSpan.FromMinutes(30),
            "Session",                 // All sessions
            $"User:{userId}",         // User-specific sessions
            $"Session:{sessionId}"    // Specific session
        );

        await _cache.SetAsync(sessionKey, session, options);
    }

    /// <summary>
    /// Invalidate all sessions for a user
    /// </summary>
    public async Task InvalidateUserSessionsAsync(int userId)
    {
        // Removes ALL sessions for this user
        await _tagService.RemoveByTagAsync($"User:{userId}");
    }

    /// <summary>
    /// Invalidate ALL active sessions (e.g., security breach)
    /// </summary>
    public async Task InvalidateAllSessionsAsync()
    {
        await _tagService.RemoveByTagAsync("Session");
    }
}
```

### Pattern 2: Cross-Entity Tags (Dealership + Company + User)
```csharp
public class CacheOrchestrationService
{
    private readonly ICacheService _cache;
    private readonly ICacheTagService _tagService;

    /// <summary>
    /// Cache dealership with cross-entity tags
    /// </summary>
    public async Task CacheDealershipWithRelationsAsync(Dealership dealership)
    {
        var key = _keyBuilder.Key("Dealership", dealership.ID.ToString());

        var options = CacheEntryOptions.Absolute(
            TimeSpan.FromMinutes(30),
            "Dealership",                              // Entity type
            $"Dealership:{dealership.ID}",            // Specific dealership
            $"Company:{dealership.CompanyID}",        // Parent company
            $"ZipCode:{dealership.BusinessZipCodeID}" // Location
        );

        await _cache.SetAsync(key, dealership, options);
    }

    /// <summary>
    /// Update company address - invalidate all dealerships in that zipcode
    /// </summary>
    public async Task InvalidateDealershipsByZipCodeAsync(int zipCodeId)
    {
        await _tagService.RemoveByTagAsync($"ZipCode:{zipCodeId}");
    }

    /// <summary>
    /// Get all keys with a specific tag
    /// </summary>
    public async Task<IReadOnlyList<string>> GetKeysWithTagAsync(string tag)
    {
        return await _tagService.GetKeysByTagAsync(tag);
    }
}
```

### Pattern 3: Temporary Tags (Bulk Operations)
```csharp
public class BulkOperationService
{
    /// <summary>
    /// Bulk update users - use temporary tag for easy cleanup
    /// </summary>
    public async Task BulkUpdateUsersAsync(List<int> userIds, UpdateUserRequest update)
    {
        var operationId = Guid.NewGuid().ToString();
        var tempTag = $"BulkOp:{operationId}";

        // Cache each user with temporary tag
        foreach (var userId in userIds)
        {
            var user = await _userService.UpdateAsync(userId, update);
            var key = _keyBuilder.Key("User", userId.ToString());

            var options = CacheEntryOptions.Absolute(
                TimeSpan.FromMinutes(5),
                "User",
                $"User:{userId}",
                tempTag  // ← Temporary operation tag
            );

            await _cache.SetAsync(key, user, options);
        }

        // If operation fails, clean up ALL cached data with one call
        if (operationFailed)
        {
            await _tagService.RemoveByTagAsync(tempTag);
        }
    }
}
```

---

## Step 6: Tag Management Service

**CacheTagManager.cs:**
```csharp
public interface ICacheTagManager
{
    Task<TagStatistics> GetTagStatisticsAsync();
    Task<IReadOnlyList<string>> GetAllTagsAsync();
    Task<int> GetKeyCountForTagAsync(string tag);
}

public class CacheTagManager : ICacheTagManager
{
    private readonly ICacheTagService _tagService;
    private readonly IConnectionMultiplexer _redis;

    public async Task<TagStatistics> GetTagStatisticsAsync()
    {
        var db = _redis.GetDatabase();
        var server = _redis.GetServers().FirstOrDefault();

        var tags = new List<TagInfo>();

        // Scan for all tag: keys
        await foreach (var key in server.KeysAsync(pattern: "tag:*"))
        {
            var tagName = key.ToString().Replace("tag:", "");
            var keyCount = await db.SetLengthAsync(key);

            tags.Add(new TagInfo
            {
                Tag = tagName,
                KeyCount = (int)keyCount
            });
        }

        return new TagStatistics
        {
            TotalTags = tags.Count,
            TotalKeys = tags.Sum(t => t.KeyCount),
            Tags = tags.OrderByDescending(t => t.KeyCount).ToList()
        };
    }

    public async Task<int> GetKeyCountForTagAsync(string tag)
    {
        var db = _redis.GetDatabase();
        var count = await db.SetLengthAsync($"tag:{tag}");
        return (int)count;
    }
}

public record TagStatistics
{
    public int TotalTags { get; init; }
    public int TotalKeys { get; init; }
    public List<TagInfo> Tags { get; init; } = new();
}

public record TagInfo
{
    public string Tag { get; init; } = string.Empty;
    public int KeyCount { get; init; }
}
```

### Cache Admin Controller
```csharp
[ApiController]
[Route("api/admin/cache")]
public class CacheAdminController : ControllerBase
{
    private readonly ICacheTagManager _tagManager;
    private readonly ICacheTagService _tagService;

    /// <summary>
    /// Get cache tag statistics
    /// </summary>
    [HttpGet("tags/stats")]
    public async Task<IActionResult> GetTagStatistics()
    {
        var stats = await _tagManager.GetTagStatisticsAsync();
        return Ok(stats);
    }

    /// <summary>
    /// Get all keys with a specific tag
    /// </summary>
    [HttpGet("tags/{tag}/keys")]
    public async Task<IActionResult> GetKeysByTag(string tag)
    {
        var keys = await _tagService.GetKeysByTagAsync(tag);
        return Ok(new { tag, keyCount = keys.Count, keys });
    }

    /// <summary>
    /// Invalidate all caches with a specific tag
    /// </summary>
    [HttpDelete("tags/{tag}")]
    public async Task<IActionResult> InvalidateTag(string tag)
    {
        await _tagService.RemoveByTagAsync(tag);
        return Ok(new { message = $"Invalidated all caches tagged with: {tag}" });
    }
}
```

---

## Real-World Scenarios in CORA.OrganizationService

### Scenario 1: User Role Change
```
User 123's role changed from Employee → Manager

Impact:
- user:123:profile           ← Tagged with User:123
- user:123:permissions       ← Tagged with User:123, Permissions
- user:123:dealerships       ← Tagged with User:123
- menu:123                   ← Tagged with User:123
- dashboard:123              ← Tagged with User:123

Solution:
await _tagService.RemoveByTagAsync("User:123");
await _tagService.RemoveByTagAsync("Permissions");  // All users

Result:
- All caches for user 123 cleared
- All permission caches cleared (affects all users)
- Next request fetches fresh data with new permissions
```

### Scenario 2: Company Acquisition
```
Company 456 acquired by Company 789

Impact:
- company:456:*              ← All company 456 data
- company:789:*              ← All company 789 data
- dealerships under 456      ← Need to be moved
- employees under 456        ← Need to be reassigned

Solution:
await _tagService.RemoveByTagAsync("Company:456");
await _tagService.RemoveByTagAsync("Company:789");
await _tagService.RemoveByTagAsync("DealershipList");

Result:
- All cached data for both companies cleared
- Next request fetches fresh merged data
```

### Scenario 3: Security Breach - Force Logout All Users
```
Security breach detected - force all users to re-authenticate

Solution:
await _tagService.RemoveByTagAsync("Session");

Result:
- ALL active sessions invalidated
- ALL users must login again
- Fresh authentication tokens generated
```

---

## Performance Impact

### Before Tagging
```csharp
// Manual invalidation - must know all related keys
await _cache.RemoveAsync("user:123:profile");
await _cache.RemoveAsync("user:123:permissions");
await _cache.RemoveAsync("user:123:preferences");
await _cache.RemoveAsync("user:123:dealerships");
await _cache.RemoveAsync("user:123:companies");
await _cache.RemoveAsync("user:123:sessions");
// Easy to miss a key!
```

### After Tagging
```csharp
// Single call - guaranteed to remove ALL related data
await _tagService.RemoveByTagAsync("User:123");
// All keys with this tag removed automatically
```

### Tag Storage Overhead
```
Tag Set Storage:
tag:User              → 5,000 members (all user keys)  ≈ 500KB
tag:User:123          → 10 members (one user's keys)   ≈ 1KB
tag:Company:456       → 50 members (company + nested)  ≈ 5KB
tag:Permissions       → 10,000 members                 ≈ 1MB

Total Overhead: ~1% of cache memory
Performance: O(1) tag lookup, O(N) invalidation (N = keys with tag)
```

---

## Best Practices

### ✅ DO:
- Use hierarchical tags (User → Session → Permissions)
- Tag all related entities together
- Use consistent tag naming (Entity:ID format)
- Include generic tags for bulk operations
- Document tag structure in code comments

### ❌ DON'T:
- Create too many tags (limit to 3-5 per key)
- Use dynamic tag names (makes querying difficult)
- Tag every single cache entry (overhead)
- Forget to invalidate tag sets when keys are removed

### Tag Naming Conventions:
```
Generic Entity Tags:
- "User", "Company", "Dealership", "Employee"

Specific Entity Tags:
- "User:123", "Company:456", "Dealership:789"

Feature Tags:
- "Session", "Permissions", "Preferences"

List/Search Tags:
- "UserList", "CompanyList", "DealershipList"

Temporary Tags:
- "BulkOp:guid", "Migration:guid"
```

---

## Summary

Cache tagging provides:
- **Single-call invalidation** for complex relationships
- **Guaranteed consistency** (no missed keys)
- **Hierarchical organization** (User → Company → Dealership)
- **Cross-entity coordination** (sessions + permissions + preferences)
- **Bulk operations** support (temporary tags)

Perfect for microservices with complex entity relationships and nested data structures.
