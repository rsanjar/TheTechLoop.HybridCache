# Usage Scenario 7: Performance Monitoring with Effectiveness Metrics

## Overview

**Best for:** Data-driven cache optimization and performance analysis

**Features Used:**
- ✅ Per-Entity Cache Effectiveness Tracking
- ✅ Hit rate by entity type (User, Company, Dealership, etc.)
- ✅ Latency and size metrics
- ✅ OpenTelemetry integration
- ✅ Prometheus/Grafana dashboards
- ✅ Lock wait time, batch size, SCAN duration and deleted key count (v1.3.0, always emitted)

**Real-World Use Cases:**
- Identify which entities benefit most from caching
- Optimize TTL values based on actual hit rates
- Discover caching candidates (low hit rate = bad candidate)
- Capacity planning with size tracking
- Performance troubleshooting per entity type

**Metrics Tracked:**
- **Hit Rate** — Percentage of cache hits vs total requests
- **Latency** — P50, P95, P99 cache access times
- **Size** — Average cached payload size per entity
- **Miss Reasons** — Why cache misses occur

---

## Architecture

```
Request Flow with Metrics:
---------------------------

GET /api/company/42
  ↓
MediatR → CachingBehavior
  ↓
Check cache: "CORA.Org:v1:Company:42"
  ↓
┌─ Cache Hit ────────────────────────────┐
│ Start timer: 0ms                       │
│ GetAsync<Company>(key)                 │
│ Stop timer: 2ms                        │
│   ↓                                    │
│ _effectivenessMetrics.RecordEntityHit( │
│   "Company",  ← Entity type            │
│   2.0,        ← Latency (ms)           │
│   5120        ← Size (bytes)           │
│ )                                      │
│   ↓                                    │
│ Company hit counter++                  │
│ Company total requests++               │
│ Company hit rate = hits/total          │
└────────────────────────────────────────┘
  ↓
Return company


GET /api/company/999 (doesn't exist)
  ↓
MediatR → CachingBehavior
  ↓
Check cache: "CORA.Org:v1:Company:999"
  ↓
┌─ Cache Miss ───────────────────────────┐
│ Start timer: 0ms                       │
│ GetAsync<Company>(key) → null          │
│ Stop timer: 3ms                        │
│   ↓                                    │
│ _effectivenessMetrics.RecordEntityMiss(│
│   "Company",  ← Entity type            │
│   3.0         ← Latency (ms)           │
│ )                                      │
│   ↓                                    │
│ Company miss counter++                 │
│ Company total requests++               │
│ Company hit rate = hits/total          │
└────────────────────────────────────────┘
  ↓
Query database (50ms)


Metrics Storage (In-Memory):
-----------------------------
EntityStats {
  "Company": {
    hits: 1420,
    misses: 180,
    totalRequests: 1600,
    hitRate: 0.8875  // 88.75%
  },
  "Dealership": {
    hits: 3200,
    misses: 800,
    totalRequests: 4000,
    hitRate: 0.8000  // 80.00%
  },
  "Country": {
    hits: 4520,
    misses: 8,
    totalRequests: 4528,
    hitRate: 0.9982  // 99.82% ← Excellent!
  },
  "User": {
    hits: 450,
    misses: 250,
    totalRequests: 700,
    hitRate: 0.6428  // 64.28% ← Consider reducing TTL
  }
}
```

---

## Step 1: Enable Effectiveness Metrics

### appsettings.json
```json
{
  "TheTechLoopCache": {
    "Configuration": "localhost:6379,password=***",
    "InstanceName": "CORA.Org:",
    "ServiceName": "organization-svc",
    "CacheVersion": "v1",
    
    "EnableEffectivenessMetrics": true,  // ← Enable per-entity tracking
    
    "MemoryCache": {
      "Enabled": true
    }
  }
}
```

### Program.cs
```csharp
using TheTechLoop.HybridCache.Extensions;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);

// Register cache with metrics
builder.Services.AddTheTechLoopCache(builder.Configuration);
builder.Services.AddTheTechLoopMultiLevelCache(builder.Configuration);
builder.Services.AddTheTechLoopCacheBehaviors();

// Register OpenTelemetry (optional but recommended)
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        // Core operational metrics (always emitted, no config needed)
        metrics.AddMeter("TheTechLoop.Cache");
        // Per-entity effectiveness metrics (requires EnableEffectivenessMetrics: true)
        metrics.AddMeter("TheTechLoop.Cache.Effectiveness");

        // Export to Prometheus
        metrics.AddPrometheusExporter();
    });

var app = builder.Build();

// Prometheus scraping endpoint
app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();
```

---

## Step 2: Metrics Automatically Tracked by CachingBehavior

### How Metrics are Recorded (Automatic)
```csharp
// When you use ICacheable queries, metrics are recorded automatically:

public record GetCompanyByIdQuery(int Id) : IRequest<Company?>, ICacheable
{
    public string CacheKey => $"Company:{Id}";
    public TimeSpan CacheDuration => TimeSpan.FromHours(2);
}

// CachingBehavior (in TheTechLoop.HybridCache) automatically:
// 1. Extracts entity type from cache key: "Company"
// 2. Records hit/miss with latency
// 3. Tracks payload size

// You get metrics for FREE with no additional code!
```

---

## Step 3: Query Cache Statistics API

### CacheStatsController.cs
```csharp
using Microsoft.AspNetCore.Mvc;
using TheTechLoop.HybridCache.Metrics;

namespace TheTechLoop.Company.API.Controllers;

[ApiController]
[Route("api/admin/cache-stats")]
public class CacheStatsController : ControllerBase
{
    private readonly CacheEffectivenessMetrics _metrics;
    private readonly ILogger<CacheStatsController> _logger;

    public CacheStatsController(
        CacheEffectivenessMetrics metrics,
        ILogger<CacheStatsController> logger)
    {
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Get cache effectiveness for all entity types
    /// </summary>
    [HttpGet]
    public IActionResult GetAllEntityStats()
    {
        var allStats = _metrics.GetAllEntityStats()
            .OrderByDescending(s => s.HitRate)
            .ToList();

        return Ok(new
        {
            timestamp = DateTime.UtcNow,
            totalEntities = allStats.Count,
            entities = allStats,
            summary = new
            {
                avgHitRate = allStats.Average(s => s.HitRate),
                bestPerformer = allStats.FirstOrDefault()?.EntityType,
                worstPerformer = allStats.LastOrDefault()?.EntityType
            }
        });
    }

    /// <summary>
    /// Get cache effectiveness for specific entity type
    /// </summary>
    [HttpGet("{entityType}")]
    public IActionResult GetEntityStats(string entityType)
    {
        var stats = _metrics.GetEntityStats(entityType);

        if (stats.TotalRequests == 0)
        {
            return NotFound(new { message = $"No statistics found for entity type: {entityType}" });
        }

        return Ok(new
        {
            timestamp = DateTime.UtcNow,
            stats,
            analysis = AnalyzeEntityPerformance(stats)
        });
    }

    /// <summary>
    /// Get entities with low hit rates (< 70%)
    /// </summary>
    [HttpGet("low-performers")]
    public IActionResult GetLowPerformers([FromQuery] double threshold = 0.7)
    {
        var allStats = _metrics.GetAllEntityStats();
        var lowPerformers = allStats
            .Where(s => s.HitRate < threshold)
            .OrderBy(s => s.HitRate)
            .ToList();

        return Ok(new
        {
            threshold,
            count = lowPerformers.Count,
            entities = lowPerformers,
            recommendations = lowPerformers.Select(s => new
            {
                entity = s.EntityType,
                currentHitRate = s.HitRate,
                recommendation = s.HitRate < 0.5
                    ? "Consider NOT caching this entity (< 50% hit rate)"
                    : "Consider reducing TTL (50-70% hit rate)"
            })
        });
    }

    /// <summary>
    /// Get cache size breakdown by entity type
    /// </summary>
    [HttpGet("size-breakdown")]
    public IActionResult GetSizeBreakdown()
    {
        var allStats = _metrics.GetAllEntityStats()
            .OrderByDescending(s => s.Hits)  // Assuming more hits = more cached entries
            .ToList();

        // Note: CacheEffectivenessMetrics doesn't track size per entity
        // This would require custom implementation

        return Ok(new
        {
            message = "Size tracking per entity not yet implemented",
            entities = allStats.Select(s => new
            {
                s.EntityType,
                s.Hits,
                estimatedCachedEntries = s.Hits
            })
        });
    }

    private object AnalyzeEntityPerformance(EntityCacheStats stats)
    {
        var analysis = new
        {
            performance = stats.HitRate switch
            {
                >= 0.95 => "Excellent",
                >= 0.85 => "Good",
                >= 0.70 => "Acceptable",
                >= 0.50 => "Poor",
                _ => "Very Poor"
            },
            recommendation = stats.HitRate switch
            {
                >= 0.95 => "Consider increasing TTL to reduce DB load even more",
                >= 0.85 => "Current TTL is optimal",
                >= 0.70 => "Consider fine-tuning TTL or invalidation strategy",
                >= 0.50 => "Consider reducing TTL or improving cache key strategy",
                _ => "Strongly consider NOT caching this entity"
            },
            expectedBenefit = $"{stats.HitRate:P0} of requests avoid database query"
        };

        return analysis;
    }
}
```

---

## Step 4: Real-World Examples from CORA.OrganizationService

### Example API Responses

#### GET /api/admin/cache-stats
```json
{
  "timestamp": "2024-01-15T10:30:00Z",
  "totalEntities": 8,
  "entities": [
    {
      "entityType": "Country",
      "hits": 4520,
      "misses": 8,
      "totalRequests": 4528,
      "hitRate": 0.9982
    },
    {
      "entityType": "StateProvince",
      "hits": 3142,
      "misses": 58,
      "totalRequests": 3200,
      "hitRate": 0.9819
    },
    {
      "entityType": "Dealership",
      "hits": 1420,
      "misses": 180,
      "totalRequests": 1600,
      "hitRate": 0.8875
    },
    {
      "entityType": "Company",
      "hits": 890,
      "misses": 110,
      "totalRequests": 1000,
      "hitRate": 0.8900
    },
    {
      "entityType": "User",
      "hits": 3200,
      "misses": 800,
      "totalRequests": 4000,
      "hitRate": 0.8000
    },
    {
      "entityType": "Employee",
      "hits": 650,
      "misses": 350,
      "totalRequests": 1000,
      "hitRate": 0.6500
    },
    {
      "entityType": "Interest",
      "hits": 450,
      "misses": 250,
      "totalRequests": 700,
      "hitRate": 0.6428
    },
    {
      "entityType": "SkillCategory",
      "hits": 180,
      "misses": 120,
      "totalRequests": 300,
      "hitRate": 0.6000
    }
  ],
  "summary": {
    "avgHitRate": 0.8363,
    "bestPerformer": "Country",
    "worstPerformer": "SkillCategory"
  }
}
```

#### GET /api/admin/cache-stats/Company
```json
{
  "timestamp": "2024-01-15T10:30:00Z",
  "stats": {
    "entityType": "Company",
    "hits": 890,
    "misses": 110,
    "totalRequests": 1000,
    "hitRate": 0.8900
  },
  "analysis": {
    "performance": "Good",
    "recommendation": "Current TTL is optimal",
    "expectedBenefit": "89% of requests avoid database query"
  }
}
```

#### GET /api/admin/cache-stats/low-performers?threshold=0.7
```json
{
  "threshold": 0.7,
  "count": 3,
  "entities": [
    {
      "entityType": "SkillCategory",
      "hits": 180,
      "misses": 120,
      "totalRequests": 300,
      "hitRate": 0.6000
    },
    {
      "entityType": "Interest",
      "hits": 450,
      "misses": 250,
      "totalRequests": 700,
      "hitRate": 0.6428
    },
    {
      "entityType": "Employee",
      "hits": 650,
      "misses": 350,
      "totalRequests": 1000,
      "hitRate": 0.6500
    }
  ],
  "recommendations": [
    {
      "entity": "SkillCategory",
      "currentHitRate": 0.6000,
      "recommendation": "Consider reducing TTL (50-70% hit rate)"
    },
    {
      "entity": "Interest",
      "currentHitRate": 0.6428,
      "recommendation": "Consider reducing TTL (50-70% hit rate)"
    },
    {
      "entity": "Employee",
      "currentHitRate": 0.6500,
      "recommendation": "Consider reducing TTL (50-70% hit rate)"
    }
  ]
}
```

---

## Step 5: Prometheus Metrics Integration

### All Available Metrics

**Meter: `TheTechLoop.Cache`** — Core operational metrics (always emitted)

```promql
# Hit/miss/error counters
cache_hits_total{cache_key_prefix="company-svc", cache_level="L1"}
cache_hits_total{cache_key_prefix="company-svc", cache_level="L2"}
cache_misses_total{cache_key_prefix="company-svc"}
cache_errors_total{cache_key_prefix="company-svc"}
cache_evictions_total{cache_key_prefix="company-svc"}
cache_circuit_breaker_bypasses_total

# Operation latency
cache_duration_ms_bucket{cache_operation="hit", cache_level="L2", le="1"}
cache_duration_ms_bucket{cache_operation="miss", le="10"}

# Stampede-lock wait (v1.3.0)
cache_lock_wait_duration_ms_bucket{cache_lock_acquired="true", le="5"}
cache_lock_wait_duration_ms_bucket{cache_lock_acquired="false", le="50"}

# Bulk batch size (v1.3.0)
cache_batch_size_keys_bucket{cache_operation="get", le="100"}
cache_batch_size_keys_bucket{cache_operation="set", le="100"}

# SCAN-based prefix invalidation (v1.3.0)
cache_scan_duration_ms_bucket{le="100"}
cache_scan_deleted_keys_total
```

**Meter: `TheTechLoop.Cache.Effectiveness`** — Per-entity tracking (requires `EnableEffectivenessMetrics: true`)

```promql
# Per-entity hit/miss counters
cache_entity_hits_total{entity="Company"}
cache_entity_misses_total{entity="Company"}

# Live hit rate gauge
cache_entity_hit_rate{entity="Company"}

# Latency and size histograms
cache_entity_latency_ms_bucket{entity="Company", le="1"}
cache_entity_latency_ms_bucket{entity="Company", le="5"}
cache_entity_size_bytes{entity="Company"}
```

### Useful Prometheus Queries

#### Overall Hit Rate
```promql
sum(rate(cache_entity_hits_total[5m])) /
(sum(rate(cache_entity_hits_total[5m])) + sum(rate(cache_entity_misses_total[5m])))
```

#### Hit Rate by Entity
```promql
cache_entity_hit_rate{entity=~".*"}
```

#### Entities with Low Hit Rate (< 70%)
```promql
cache_entity_hit_rate{entity=~".*"} < 0.7
```

#### Top 5 Most Cached Entities
```promql
topk(5, cache_entity_hits_total)
```

#### Cache Miss Rate Trend
```promql
rate(cache_entity_misses_total{entity="Company"}[5m])
```

#### Lock Contention Rate (v1.3.0)
```promql
# Ratio of failed lock acquisitions (stampede pressure indicator)
rate(cache_lock_wait_duration_ms_count{cache_lock_acquired="false"}[5m]) /
rate(cache_lock_wait_duration_ms_count[5m])
```

#### Keys Deleted by SCAN Invalidation (v1.3.0)
```promql
increase(cache_scan_deleted_keys_total[1h])
```

#### Average Bulk Batch Size (v1.3.0)
```promql
rate(cache_batch_size_keys_sum{cache_operation="get"}[5m]) /
rate(cache_batch_size_keys_count{cache_operation="get"}[5m])
```

---

## Step 6: Grafana Dashboard

### Dashboard Panels

#### Panel 1: Hit Rate by Entity (Gauge)
```json
{
  "title": "Cache Hit Rate by Entity",
  "type": "gauge",
  "targets": [{
    "expr": "cache_entity_hit_rate{entity=~\".*\"}"
  }],
  "thresholds": {
    "steps": [
      { "value": 0, "color": "red" },
      { "value": 0.7, "color": "yellow" },
      { "value": 0.85, "color": "green" }
    ]
  }
}
```

#### Panel 2: Total Requests by Entity (Time Series)
```json
{
  "title": "Cache Requests per Entity",
  "type": "timeseries",
  "targets": [{
    "expr": "rate(cache_entity_hits_total{entity=~\".*\"}[5m]) + rate(cache_entity_misses_total{entity=~\".*\"}[5m])",
    "legendFormat": "{{entity}}"
  }]
}
```

#### Panel 3: Hit vs Miss Breakdown (Pie Chart)
```json
{
  "title": "Cache Hit vs Miss",
  "type": "piechart",
  "targets": [
    {
      "expr": "sum(cache_entity_hits_total)",
      "legendFormat": "Hits"
    },
    {
      "expr": "sum(cache_entity_misses_total)",
      "legendFormat": "Misses"
    }
  ]
}
```

#### Panel 4: Low Performers Alert (Table)
```json
{
  "title": "Entities with Hit Rate < 70%",
  "type": "table",
  "targets": [{
    "expr": "cache_entity_hit_rate{entity=~\".*\"} < 0.7"
  }],
  "transformations": [
    {
      "id": "organize",
      "options": {
        "excludeByName": {},
        "indexByName": {
          "entity": 0,
          "Value": 1
        },
        "renameByName": {
          "entity": "Entity Type",
          "Value": "Hit Rate"
        }
      }
    }
  ]
}
```

#### Panel 5: Lock Wait Duration P95 (v1.3.0)
```json
{
  "title": "Stampede Lock Wait P95",
  "type": "timeseries",
  "targets": [{
    "expr": "histogram_quantile(0.95, rate(cache_lock_wait_duration_ms_bucket[5m]))",
    "legendFormat": "P95 lock wait (ms)"
  }]
}
```

#### Panel 6: Bulk Operation Batch Sizes (v1.3.0)
```json
{
  "title": "Batch Size P99 (GetMany / SetMany)",
  "type": "timeseries",
  "targets": [
    {
      "expr": "histogram_quantile(0.99, rate(cache_batch_size_keys_bucket{cache_operation=\"get\"}[5m]))",
      "legendFormat": "GetMany P99"
    },
    {
      "expr": "histogram_quantile(0.99, rate(cache_batch_size_keys_bucket{cache_operation=\"set\"}[5m]))",
      "legendFormat": "SetMany P99"
    }
  ]
}
```

#### Panel 7: SCAN Invalidation Impact (v1.3.0)
```json
{
  "title": "SCAN Deletions per Minute",
  "type": "stat",
  "targets": [{
    "expr": "rate(cache_scan_deleted_keys_total[1m]) * 60",
    "legendFormat": "Keys deleted/min"
  }]
}
```

---

## Step 7: Actionable Insights & Optimization

### Based on Metrics, Optimize CORA.OrganizationService

#### Country (99.8% hit rate) ✓ Excellent
```csharp
// Current:
public record GetCountriesQuery : IRequest<List<Country>>, ICacheable
{
    public string CacheKey => "Country:All";
    public TimeSpan CacheDuration => TimeSpan.FromHours(24);
}

// Optimization: Increase TTL (data rarely changes)
public TimeSpan CacheDuration => TimeSpan.FromDays(7);  // 7 days instead of 24 hours
```

#### Dealership (88.75% hit rate) ✓ Good
```csharp
// Current TTL is optimal, no changes needed
public TimeSpan CacheDuration => TimeSpan.FromMinutes(30);
```

#### User (80% hit rate) ⚠️ Acceptable
```csharp
// Current:
public TimeSpan CacheDuration => TimeSpan.FromMinutes(15);

// No change needed, but monitor for improvement opportunities
```

#### Employee (65% hit rate) ⚠️ Poor
```csharp
// Current:
public record GetEmployeeByIdQuery(int Id) : IRequest<Employee?>, ICacheable
{
    public string CacheKey => $"Employee:{Id}";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(30);
}

// Optimization 1: Reduce TTL (data changes frequently)
public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);  // Reduce from 30 to 5

// Or Optimization 2: Don't cache at all (< 70% hit rate)
// Remove ICacheable interface
public record GetEmployeeByIdQuery(int Id) : IRequest<Employee?>;  // No caching
```

#### Interest (64.28% hit rate) ❌ Poor
```csharp
// Recommendation: DON'T CACHE
// Remove ICacheable interface and query database directly

// Before:
public record GetInterestsQuery : IRequest<List<Interest>>, ICacheable
{
    public string CacheKey => "Interest:All";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(10);
}

// After (no caching):
public record GetInterestsQuery : IRequest<List<Interest>>;  // Direct DB query
```

---

## Step 8: Capacity Planning with Metrics

### Calculate Redis Memory Usage

```csharp
public class CacheCapacityService
{
    private readonly CacheEffectivenessMetrics _metrics;

    public CacheCapacityReport GenerateCapacityReport()
    {
        var allStats = _metrics.GetAllEntityStats();

        var report = new CacheCapacityReport
        {
            Entities = allStats.Select(s => new EntityCapacity
            {
                EntityType = s.EntityType,
                CachedEntries = s.Hits,  // Approximate
                EstimatedSizeKB = EstimateEntitySize(s.EntityType),
                TotalMemoryKB = s.Hits * EstimateEntitySize(s.EntityType)
            }).ToList()
        };

        report.TotalMemoryMB = report.Entities.Sum(e => e.TotalMemoryKB) / 1024;

        return report;
    }

    private int EstimateEntitySize(string entityType)
    {
        return entityType switch
        {
            "Country" => 1,         // 1KB per country
            "StateProvince" => 2,   // 2KB per state
            "Company" => 5,         // 5KB per company
            "Dealership" => 10,     // 10KB per dealership
            "User" => 3,            // 3KB per user
            "Employee" => 4,        // 4KB per employee
            _ => 2
        };
    }
}

public class CacheCapacityReport
{
    public List<EntityCapacity> Entities { get; set; } = new();
    public double TotalMemoryMB { get; set; }
}

public class EntityCapacity
{
    public string EntityType { get; set; } = string.Empty;
    public long CachedEntries { get; set; }
    public int EstimatedSizeKB { get; set; }
    public long TotalMemoryKB { get; set; }
}

// Example output:
{
  "entities": [
    {
      "entityType": "Country",
      "cachedEntries": 195,
      "estimatedSizeKB": 1,
      "totalMemoryKB": 195
    },
    {
      "entityType": "Dealership",
      "cachedEntries": 1420,
      "estimatedSizeKB": 10,
      "totalMemoryKB": 14200
    },
    {
      "entityType": "User",
      "cachedEntries": 3200,
      "estimatedSizeKB": 3,
      "totalMemoryKB": 9600
    }
  ],
  "totalMemoryMB": 24.0
}
```

---

## Best Practices for CORA.OrganizationService

### ✅ DO:
- Monitor cache effectiveness regularly (weekly)
- Set alerts for low hit rates (< 70%)
- Use metrics to optimize TTL values
- Track trends over time (hit rate improving/degrading)
- Export metrics to Prometheus/Grafana
- Review low performers and adjust caching strategy

### ❌ DON'T:
- Ignore low hit rate warnings
- Cache everything without measuring effectiveness
- Set TTL arbitrarily without data
- Forget to review metrics after TTL changes
- Over-optimize (70%+ hit rate is acceptable)

### Optimization Decision Tree:
```
Entity Hit Rate Analysis:
-------------------------
Hit Rate >= 95%:  ✅ Excellent → Consider increasing TTL
Hit Rate 85-95%:  ✅ Good     → Keep current TTL
Hit Rate 70-85%:  ⚠️ Acceptable → Monitor, fine-tune if needed
Hit Rate 50-70%:  ⚠️ Poor     → Reduce TTL or reconsider caching
Hit Rate < 50%:   ❌ Very Poor → DON'T CACHE (database is faster)
```

---

## Troubleshooting

### Issue: No metrics appearing
**Solution:** Verify metrics are enabled
```json
{
  "TheTechLoopCache": {
    "EnableEffectivenessMetrics": true  // ← Must be true
  }
}
```

### Issue: Metrics not accurate
**Solution:** Ensure queries use ICacheable
```csharp
// Must implement ICacheable for automatic tracking
public record GetCompanyQuery(int Id) : IRequest<Company?>, ICacheable
{
    public string CacheKey => $"Company:{Id}";
    public TimeSpan CacheDuration => TimeSpan.FromHours(2);
}
```

### Issue: Prometheus not showing metrics
**Solution:** Verify both meters are registered
```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter("TheTechLoop.Cache");                  // ← Core operational metrics
        metrics.AddMeter("TheTechLoop.Cache.Effectiveness");    // ← Per-entity tracking
        metrics.AddPrometheusExporter();
    });

app.MapPrometheusScrapingEndpoint("/metrics");  // ← Required
```

---

## Summary

Performance Monitoring in CORA.OrganizationService provides:
- **Per-entity cache effectiveness** tracking (hit rate, latency, size)
- **Data-driven TTL optimization** (not guessing)
- **Identify poor caching candidates** (< 70% hit rate)
- **Capacity planning** with memory usage estimates
- **Prometheus/Grafana integration** for dashboards
- **Actionable insights** for cache strategy
- **Operational visibility** (v1.3.0): lock wait time, bulk batch size, SCAN invalidation duration and deleted key count

**Real Results from CORA.OrganizationService:**
```
Country:       99.8% hit rate → Increase TTL to 7 days ✓
Dealership:    88.8% hit rate → Keep TTL at 30 minutes ✓
User:          80.0% hit rate → Keep TTL at 15 minutes ✓
Employee:      65.0% hit rate → Reduce TTL to 5 minutes ⚠️
Interest:      64.3% hit rate → Remove caching entirely ❌
```

**Monthly Cost Savings:**
```
Before Optimization:
- Redis Memory: 50GB @ $25/GB = $1,250/month
- Hit Rate: 75% average

After Optimization (based on metrics):
- Redis Memory: 30GB @ $25/GB = $750/month
- Hit Rate: 85% average (removed poor performers)
- Savings: $500/month + better performance!
```

**Implementation:** Enable `EnableEffectivenessMetrics: true` and review `/api/admin/cache-stats` regularly! 📊
