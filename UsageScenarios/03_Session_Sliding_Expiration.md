# Usage Scenario 3: Session Management with Sliding Expiration

## Overview

**Best for:** User authentication and session management

**Features Used:**
- ✅ Sliding Expiration (resets on each access)
- ✅ Multi-Level Cache (L1 supports sliding natively)
- ✅ User session persistence
- ✅ Shopping cart / temporary data

**Real-World Use Cases:**
- User login sessions (expire after 30min inactivity)
- UserProfile preferences (reset TTL on access)
- Shopping cart data (keep alive while user active)
- Form wizard state (preserve between steps)

**Performance:**
- Session reads: **< 1ms** (L1 hit)
- Automatic timeout: **After inactivity period**
- No manual refresh needed

---

## Architecture

```
User Session Flow:
------------------
User logs in at 10:00
  ↓
Session cached with 30min sliding expiration
  ↓
User clicks at 10:15 → Timer resets to 10:45
User clicks at 10:30 → Timer resets to 11:00
User clicks at 10:45 → Timer resets to 11:15
  ↓
User idle for 30 minutes
  ↓
Session expires automatically at 11:45

Compare to Absolute Expiration:
--------------------------------
User logs in at 10:00 with 30min absolute
  ↓
Session expires at 10:30 NO MATTER WHAT
Even if user is actively clicking
```

---

## Step 1: Enable Multi-Level Cache (Required for Sliding)

### appsettings.json
```json
{
  "TheTechLoopCache": {
    "Configuration": "localhost:6379,password=***",
    "InstanceName": "CORA.Org:",
    "ServiceName": "organization-svc",
    "CacheVersion": "v1",
    "DefaultExpirationMinutes": 60,
    "Enabled": true,
    
    "MemoryCache": {
      "Enabled": true,  // ← REQUIRED for sliding expiration
      "DefaultExpirationSeconds": 1800,  // 30 minutes
      "SizeLimit": 1024
    }
  }
}
```

### Program.cs
```csharp
using TheTechLoop.HybridCache.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Multi-level cache REQUIRED for sliding expiration
builder.Services.AddTheTechLoopCache(builder.Configuration);
builder.Services.AddTheTechLoopMultiLevelCache(builder.Configuration);  // ← Required

var app = builder.Build();
app.Run();
```

---

## Step 2: User Authentication with Sliding Session

### UserController.cs (Login with Session Cache)
```csharp
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Keys;
using TheTechLoop.Company.Service;
using TheTechLoop.Company.DTO.Models;

[ApiController]
[Route("api/[controller]")]
public class UserController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;
    private readonly ILogger<UserController> _logger;

    public UserController(
        IUserService userService,
        ICacheService cache,
        CacheKeyBuilder keyBuilder,
        ILogger<UserController> logger)
    {
        _userService = userService;
        _cache = cache;
        _keyBuilder = keyBuilder;
        _logger = logger;
    }

    /// <summary>
    /// Login user - create session with sliding expiration
    /// Session expires after 30 minutes of INACTIVITY
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _userService.AuthenticateAsync(request.Username, request.Password);
        if (user is null)
        {
            return Unauthorized(new { message = "Invalid credentials" });
        }

        // Generate session ID
        var sessionId = Guid.NewGuid().ToString();
        var sessionKey = _keyBuilder.Key("Session", sessionId);

        // Create session data
        var sessionData = new UserSession
        {
            UserId = user.ID,
            Username = user.Username,
            Email = user.Email,
            LoginTime = DateTime.UtcNow,
            LastAccessTime = DateTime.UtcNow,
            SessionId = sessionId
        };

        // Cache with SLIDING expiration - resets on each access
        var options = CacheEntryOptions.Sliding(
            TimeSpan.FromMinutes(30),  // Expires after 30min of inactivity
            "Session",
            $"User:{user.ID}"
        );

        await _cache.SetAsync(sessionKey, sessionData, options);

        _logger.LogInformation(
            "User {UserId} logged in. Session {SessionId} created with sliding expiration.",
            user.ID, sessionId);

        return Ok(new
        {
            user,
            sessionId,
            token = GenerateJwtToken(user, sessionId),
            expiresIn = "30 minutes of inactivity",
            message = "Login successful"
        });
    }

    /// <summary>
    /// Get current session - accessing this RESETS the expiration timer
    /// </summary>
    [HttpGet("session")]
    public async Task<IActionResult> GetSession([FromHeader(Name = "X-Session-Id")] string sessionId)
    {
        var sessionKey = _keyBuilder.Key("Session", sessionId);

        // GetAsync automatically resets sliding expiration timer
        var session = await _cache.GetAsync<UserSession>(sessionKey);

        if (session is null)
        {
            return Unauthorized(new { message = "Session expired or invalid" });
        }

        // Update last access time
        session.LastAccessTime = DateTime.UtcNow;
        var options = CacheEntryOptions.Sliding(
            TimeSpan.FromMinutes(30),
            "Session",
            $"User:{session.UserId}"
        );
        await _cache.SetAsync(sessionKey, session, options);

        _logger.LogDebug(
            "Session {SessionId} accessed. Timer reset to 30 minutes from now.",
            sessionId);

        return Ok(new
        {
            session,
            timeUntilExpiry = "30 minutes from now",
            lastAccess = session.LastAccessTime
        });
    }

    /// <summary>
    /// Logout - explicitly remove session
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromHeader(Name = "X-Session-Id")] string sessionId)
    {
        var sessionKey = _keyBuilder.Key("Session", sessionId);
        await _cache.RemoveAsync(sessionKey);

        _logger.LogInformation("Session {SessionId} logged out", sessionId);

        return Ok(new { message = "Logged out successfully" });
    }

    /// <summary>
    /// Get user profile - session access resets timer
    /// </summary>
    [HttpGet("{id}")]
    [SessionRequired]  // Custom attribute that validates session
    public async Task<IActionResult> GetProfile(
        int id,
        [FromHeader(Name = "X-Session-Id")] string sessionId)
    {
        // Every API call that checks session resets the expiration timer
        var sessionKey = _keyBuilder.Key("Session", sessionId);
        var session = await _cache.GetAsync<UserSession>(sessionKey);

        if (session is null)
        {
            return Unauthorized(new { message = "Session expired" });
        }

        // Session automatically refreshed by cache

        var userKey = _keyBuilder.Key("User", id.ToString(), "Profile");
        var user = await _cache.GetOrCreateAsync(
            userKey,
            async () => await _userService.GetUserByIdAsync(id),
            TimeSpan.FromMinutes(15));

        return Ok(user);
    }

    private string GenerateJwtToken(User user, string sessionId)
    {
        // JWT token generation with session ID in claims
        return $"jwt-token-{user.ID}-{sessionId}";
    }
}

/// <summary>
/// User session data structure
/// </summary>
public class UserSession
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime LoginTime { get; set; }
    public DateTime LastAccessTime { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Login request DTO
/// </summary>
public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
```

---

## Step 3: UserProfile Preferences with Sliding Expiration

### UserService.cs (Preferences Cache)
```csharp
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Keys;
using TheTechLoop.Company.Data;

public class UserService : IUserService
{
    private readonly TheTechLoopDataContext _context;
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;

    /// <summary>
    /// Get user preferences - cached with sliding expiration
    /// Preferences stay cached as long as user is active
    /// </summary>
    public async Task<UserPreferences> GetUserPreferencesAsync(int userId)
    {
        var cacheKey = _keyBuilder.Key("UserPreferences", userId.ToString());

        var preferences = await _cache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                var userProfile = await _context.UserProfiles
                    .FirstOrDefaultAsync(up => up.UserID == userId);

                return new UserPreferences
                {
                    UserId = userId,
                    Theme = userProfile?.PreferredTheme ?? "light",
                    Language = userProfile?.PreferredLanguage ?? "en",
                    Timezone = userProfile?.Timezone ?? "UTC",
                    NotificationsEnabled = userProfile?.NotificationsEnabled ?? true
                };
            },
            TimeSpan.FromMinutes(60));  // Initial expiration

        return preferences;
    }

    /// <summary>
    /// Update preferences - save to DB and cache with sliding
    /// </summary>
    public async Task<UserPreferences> UpdateUserPreferencesAsync(
        int userId,
        UpdatePreferencesRequest request)
    {
        var userProfile = await _context.UserProfiles
            .FirstOrDefaultAsync(up => up.UserID == userId);

        if (userProfile is not null)
        {
            userProfile.PreferredTheme = request.Theme;
            userProfile.PreferredLanguage = request.Language;
            userProfile.Timezone = request.Timezone;
            userProfile.NotificationsEnabled = request.NotificationsEnabled;

            await _context.SaveChangesAsync();
        }

        var preferences = new UserPreferences
        {
            UserId = userId,
            Theme = request.Theme,
            Language = request.Language,
            Timezone = request.Timezone,
            NotificationsEnabled = request.NotificationsEnabled
        };

        // Cache with sliding expiration
        var cacheKey = _keyBuilder.Key("UserPreferences", userId.ToString());
        var options = CacheEntryOptions.Sliding(
            TimeSpan.FromHours(2),  // Expires after 2 hours of inactivity
            "UserPreferences",
            $"User:{userId}"
        );

        await _cache.SetAsync(cacheKey, preferences, options);

        return preferences;
    }
}

public class UserPreferences
{
    public int UserId { get; set; }
    public string Theme { get; set; } = "light";
    public string Language { get; set; } = "en";
    public string Timezone { get; set; } = "UTC";
    public bool NotificationsEnabled { get; set; } = true;
}

public class UpdatePreferencesRequest
{
    public string Theme { get; set; } = "light";
    public string Language { get; set; } = "en";
    public string Timezone { get; set; } = "UTC";
    public bool NotificationsEnabled { get; set; } = true;
}
```

---

## Step 4: Shopping Cart / Temporary Data

### ShoppingCartService.cs
```csharp
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Keys;

public interface IShoppingCartService
{
    Task<ShoppingCart> GetCartAsync(string cartId);
    Task<ShoppingCart> AddItemAsync(string cartId, CartItem item);
    Task<ShoppingCart> RemoveItemAsync(string cartId, int itemId);
    Task ClearCartAsync(string cartId);
}

public class ShoppingCartService : IShoppingCartService
{
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;

    public ShoppingCartService(ICacheService cache, CacheKeyBuilder keyBuilder)
    {
        _cache = cache;
        _keyBuilder = keyBuilder;
    }

    /// <summary>
    /// Get cart - accessing it resets the expiration timer
    /// </summary>
    public async Task<ShoppingCart> GetCartAsync(string cartId)
    {
        var cacheKey = _keyBuilder.Key("Cart", cartId);

        // GetAsync resets sliding expiration automatically
        var cart = await _cache.GetAsync<ShoppingCart>(cacheKey);

        if (cart is null)
        {
            // Create new cart with sliding expiration
            cart = new ShoppingCart { CartId = cartId, Items = new() };
            await SaveCartAsync(cartId, cart);
        }

        return cart;
    }

    /// <summary>
    /// Add item to cart - resets expiration timer
    /// </summary>
    public async Task<ShoppingCart> AddItemAsync(string cartId, CartItem item)
    {
        var cart = await GetCartAsync(cartId);

        var existingItem = cart.Items.FirstOrDefault(i => i.ProductId == item.ProductId);
        if (existingItem is not null)
        {
            existingItem.Quantity += item.Quantity;
        }
        else
        {
            cart.Items.Add(item);
        }

        cart.UpdatedAt = DateTime.UtcNow;
        await SaveCartAsync(cartId, cart);

        return cart;
    }

    /// <summary>
    /// Remove item from cart - resets expiration timer
    /// </summary>
    public async Task<ShoppingCart> RemoveItemAsync(string cartId, int itemId)
    {
        var cart = await GetCartAsync(cartId);
        cart.Items.RemoveAll(i => i.ProductId == itemId);
        cart.UpdatedAt = DateTime.UtcNow;

        await SaveCartAsync(cartId, cart);

        return cart;
    }

    /// <summary>
    /// Clear cart - remove from cache
    /// </summary>
    public async Task ClearCartAsync(string cartId)
    {
        var cacheKey = _keyBuilder.Key("Cart", cartId);
        await _cache.RemoveAsync(cacheKey);
    }

    private async Task SaveCartAsync(string cartId, ShoppingCart cart)
    {
        var cacheKey = _keyBuilder.Key("Cart", cartId);

        // Sliding expiration: cart expires after 2 hours of inactivity
        var options = CacheEntryOptions.Sliding(
            TimeSpan.FromHours(2),
            "Cart",
            "ShoppingCart"
        );

        await _cache.SetAsync(cacheKey, cart, options);
    }
}

public class ShoppingCart
{
    public string CartId { get; set; } = string.Empty;
    public List<CartItem> Items { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public decimal TotalPrice => Items.Sum(i => i.Price * i.Quantity);
}

public class CartItem
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Quantity { get; set; }
}
```

---

## Step 5: Form Wizard State (Multi-Step Forms)

### FormWizardService.cs
```csharp
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Keys;

public class FormWizardService
{
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;

    /// <summary>
    /// Save wizard state - sliding expiration keeps data while user progresses
    /// </summary>
    public async Task<WizardState> SaveStepAsync(string wizardId, int stepNumber, object stepData)
    {
        var cacheKey = _keyBuilder.Key("Wizard", wizardId);

        // Get existing wizard state or create new
        var state = await _cache.GetAsync<WizardState>(cacheKey) ?? new WizardState
        {
            WizardId = wizardId,
            Steps = new()
        };

        state.Steps[stepNumber] = stepData;
        state.CurrentStep = stepNumber;
        state.LastUpdated = DateTime.UtcNow;

        // Sliding expiration: expires after 30 minutes of inactivity
        var options = CacheEntryOptions.Sliding(
            TimeSpan.FromMinutes(30),
            "Wizard",
            "FormState"
        );

        await _cache.SetAsync(cacheKey, state, options);

        return state;
    }

    /// <summary>
    /// Get wizard state - accessing it resets timer
    /// </summary>
    public async Task<WizardState?> GetWizardStateAsync(string wizardId)
    {
        var cacheKey = _keyBuilder.Key("Wizard", wizardId);
        return await _cache.GetAsync<WizardState>(cacheKey);
    }

    /// <summary>
    /// Complete wizard - remove from cache
    /// </summary>
    public async Task CompleteWizardAsync(string wizardId)
    {
        var cacheKey = _keyBuilder.Key("Wizard", wizardId);
        await _cache.RemoveAsync(cacheKey);
    }
}

public class WizardState
{
    public string WizardId { get; set; } = string.Empty;
    public int CurrentStep { get; set; }
    public Dictionary<int, object> Steps { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
```

---

## Step 6: Activity Tracking with Sliding Expiration

### UserActivityService.cs
```csharp
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Keys;

public class UserActivityService
{
    private readonly ICacheService _cache;
    private readonly CacheKeyBuilder _keyBuilder;

    /// <summary>
    /// Track user activity - sliding expiration shows active users
    /// </summary>
    public async Task RecordActivityAsync(int userId, string activityType)
    {
        var cacheKey = _keyBuilder.Key("Activity", userId.ToString());

        var activity = await _cache.GetAsync<UserActivity>(cacheKey) ?? new UserActivity
        {
            UserId = userId
        };

        activity.LastActivity = DateTime.UtcNow;
        activity.ActivityType = activityType;
        activity.ActivityCount++;

        // Sliding expiration: user is "active" for 10 minutes after last action
        var options = CacheEntryOptions.Sliding(
            TimeSpan.FromMinutes(10),
            "Activity",
            $"User:{userId}"
        );

        await _cache.SetAsync(cacheKey, activity, options);
    }

    /// <summary>
    /// Check if user is currently active (accessed cache within 10 minutes)
    /// </summary>
    public async Task<bool> IsUserActiveAsync(int userId)
    {
        var cacheKey = _keyBuilder.Key("Activity", userId.ToString());
        var activity = await _cache.GetAsync<UserActivity>(cacheKey);

        return activity is not null;  // If exists, user is active
    }

    /// <summary>
    /// Get all active users (those with cached activity)
    /// </summary>
    public async Task<List<int>> GetActiveUserIdsAsync()
    {
        // This requires scanning or maintaining a separate set
        // For production, consider using Redis Sets with TTL
        return new List<int>();  // Placeholder
    }
}

public class UserActivity
{
    public int UserId { get; set; }
    public DateTime LastActivity { get; set; }
    public string ActivityType { get; set; } = string.Empty;
    public int ActivityCount { get; set; }
}
```

---

## Step 7: Session Validation Middleware

### SessionValidationMiddleware.cs
```csharp
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.Keys;

public class SessionValidationMiddleware
{
    private readonly RequestDelegate _next;

    public SessionValidationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ICacheService cache,
        CacheKeyBuilder keyBuilder)
    {
        // Skip validation for login/public endpoints
        if (context.Request.Path.StartsWithSegments("/api/user/login") ||
            context.Request.Path.StartsWithSegments("/api/public"))
        {
            await _next(context);
            return;
        }

        // Check session header
        if (!context.Request.Headers.TryGetValue("X-Session-Id", out var sessionId))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { message = "Session required" });
            return;
        }

        var sessionKey = keyBuilder.Key("Session", sessionId.ToString());
        var session = await cache.GetAsync<UserSession>(sessionKey);

        if (session is null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { message = "Session expired" });
            return;
        }

        // Session is valid and accessing it reset the sliding expiration timer
        context.Items["UserId"] = session.UserId;
        context.Items["SessionId"] = sessionId.ToString();

        await _next(context);
    }
}

// Register in Program.cs
app.UseMiddleware<SessionValidationMiddleware>();
```

---

## Comparison: Sliding vs Absolute Expiration

### Absolute Expiration (Previous Behavior)
```csharp
// Expires at FIXED time regardless of access
var options = CacheEntryOptions.Absolute(TimeSpan.FromMinutes(30));
await _cache.SetAsync(key, value, options);

Timeline:
10:00 - Cache entry created (expires at 10:30)
10:15 - User accesses (still expires at 10:30)
10:29 - User accesses (still expires at 10:30)
10:30 - EXPIRED (even if user just accessed at 10:29)
```

### Sliding Expiration (New Behavior)
```csharp
// Expires after INACTIVITY period
var options = CacheEntryOptions.Sliding(TimeSpan.FromMinutes(30));
await _cache.SetAsync(key, value, options);

Timeline:
10:00 - Cache entry created (expires at 10:30)
10:15 - User accesses → Timer resets (now expires at 10:45)
10:40 - User accesses → Timer resets (now expires at 11:10)
11:10 - EXPIRED (if no access since 10:40)
```

---

## Performance Metrics

### Session Access Latency
```
L1 Hit (Sliding):     < 1ms   ← Session in memory, timer reset
L2 Hit (Sliding):     2-5ms   ← Session in Redis, promoted to L1
Session Expired:      50ms    ← Re-authenticate from DB
```

### Memory Usage
```
Active Sessions (1000 users):
- L1 Cache: ~10MB (100 most active sessions)
- L2 Cache: ~50MB (all 1000 sessions)
- Timer overhead: Negligible (handled by MemoryCache)
```

---

## Best Practices for CORA.OrganizationService

### ✅ DO:
- Use sliding expiration for sessions and user activity
- Set appropriate timeout (15-30 min for sessions, 2-4 hours for carts)
- Access session frequently to keep user logged in
- Clean up sessions on explicit logout
- Use multi-level cache (required for sliding)

### ❌ DON'T:
- Use sliding for rarely accessed data (defeats purpose)
- Set very long sliding windows (hours for sessions)
- Forget to clean up on logout
- Use sliding for reference data (use absolute instead)
- Mix sliding and absolute for same data type

### Timeout Recommendations
```
User Sessions:        30 minutes sliding
Shopping Cart:        2 hours sliding
Form Wizard State:    30 minutes sliding
User Preferences:     2 hours sliding
Activity Tracking:    10 minutes sliding
```

---

## Troubleshooting

### Issue: Sessions expire too quickly
**Solution:** Increase sliding window
```json
{
  "MemoryCache": {
    "DefaultExpirationSeconds": 3600  // Increase to 60 minutes
  }
}
```

### Issue: Memory usage too high
**Solution:** Reduce L1 size limit
```json
{
  "MemoryCache": {
    "SizeLimit": 512  // Reduce from 1024
  }
}
```

### Issue: Session not resetting on access
**Solution:** Ensure multi-level cache is enabled
```csharp
// Required
builder.Services.AddTheTechLoopMultiLevelCache(builder.Configuration);
```

---

## Summary

Sliding expiration provides:
- **Automatic session timeout** after inactivity
- **User-friendly experience** (stays logged in while active)
- **No manual refresh** needed
- **L1 cache native support** (< 1ms access)
- **Perfect for sessions**, preferences, carts, wizard state

**Timeline Example:**
```
User logs in at 10:00 AM
  ↓
Session valid for 30 minutes of inactivity
  ↓
User accesses profile at 10:20 → Timer resets to 10:50
User clicks button at 10:45 → Timer resets to 11:15
  ↓
User goes to lunch at 11:00
  ↓
Session expires at 11:30 (30 min after last access)
  ↓
User returns at 12:00 → Must login again
```

**Perfect for:** User sessions, shopping carts, temporary form data, and any scenario where "keep alive while active" behavior is desired.
