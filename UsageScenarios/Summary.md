# TheTechLoop.HybridCache - Usage Scenarios Index

## 📚 Complete List of Usage Scenarios

This directory contains comprehensive documentation for all major usage scenarios of the TheTechLoop.HybridCache library. Each scenario includes complete setup instructions, code examples, and best practices.

---

## 🎯 Scenarios

### 1. **CQRS Pattern with Multi-Level Cache** ⭐ Most Popular
📄 **File:** `01_CQRS_MultiLevel_Cache.md`

**Best For:** Microservices using MediatR, Repository pattern, high read-to-write ratio

**Features:**
- Multi-Level Cache (L1 Memory + L2 Redis)
- Automatic caching via `ICacheable`
- Automatic invalidation via `ICacheInvalidatable`
- Read/Write repositories with UnitOfWork
- 10-50x performance improvement

**Use Cases:**
- E-commerce product catalogs
- User profile management
- Dealership/company management systems
- Any CQRS microservice

---

### 2. **Cache Tagging for Bulk Invalidation**
📄 **File:** `02_Cache_Tagging_Bulk_Invalidation.md`

**Best For:** Complex invalidation scenarios where related data must be cleared together

**Features:**
- Group cache entries by tags
- Invalidate entire user session with one call
- Tag-based group invalidation
- Redis Sets for O(1) tag queries

**Use Cases:**
- User logout (invalidate all user data)
- Role changes (invalidate permissions, menus)
- Tenant data refresh (multi-tenant SaaS)
- Cascading invalidation (user → sessions → preferences)

---

### 3. **Session Management with Sliding Expiration**
📄 **File:** `03_Session_Sliding_Expiration.md`

**Best For:** User sessions, shopping carts, temporary user data

**Features:**
- Sliding expiration (resets on each access)
- Perfect for session data
- L1 cache native sliding support
- Automatic session timeout after inactivity

**Use Cases:**
- User login sessions
- Shopping cart persistence
- Temporary form data
- User activity tracking

---

### 4. **High-Volume API with Compression**
📄 **File:** `04_High_Volume_Compression.md`

**Best For:** APIs returning large payloads, bandwidth-constrained environments

**Features:**
- Automatic GZip compression for values > 1KB
- 60-80% memory savings
- Reduced network bandwidth
- Transparent compression/decompression

**Use Cases:**
- Large JSON responses
- Document APIs
- Media metadata caching
- Reporting APIs with large result sets

---

### 5. **Distributed Microservices with Streams**
📄 **File:** `05_Microservices_Streams.md`

**Best For:** Production microservices requiring guaranteed cache coherence

**Features:**
- Redis Streams instead of Pub/Sub
- Guaranteed message delivery
- Consumer groups for load balancing
- No message loss during downtime

**Use Cases:**
- Critical invalidation scenarios
- Multi-region deployments
- High-availability systems
- Financial/healthcare applications

---

### 6. **Reference Data with Cache Warming**
📄 **File:** `06_Reference_Data_Warming.md`

**Best For:** Applications with static reference data that must be available immediately

**Features:**
- Pre-load data on startup
- Zero cold-start latency
- Strategy pattern for extensibility
- Background warmup service

**Use Cases:**
- Countries, states, categories
- Configuration settings
- Lookup tables
- Product categories and attributes

---

### 7. **Performance Monitoring with Effectiveness Metrics**
📄 **File:** `07_Performance_Metrics.md`

**Best For:** Data-driven cache optimization and performance analysis

**Features:**
- Track hit rate per entity type
- Latency and size metrics
- OpenTelemetry integration
- Prometheus/Grafana dashboards

**Use Cases:**
- Identify caching candidates
- Optimize TTL values
- Capacity planning
- Performance troubleshooting

---

### 8. **Simple REST API with Basic Cache**
📄 **File:** `08_Simple_REST_API.md`

**Best For:** Simple APIs without CQRS, minimal setup required

**Features:**
- Single-level Redis cache
- Direct controller usage (no MediatR)
- Manual cache management
- Minimal dependencies

**Use Cases:**
- Legacy API migration
- Simple microservices
- Prototypes and MVPs
- Third-party API facades

---

### 9. **Read-Heavy Workload (Memory Cache Only)**
📄 **File:** `09_Read_Heavy_Memory_Only.md`

**Best For:** Single-instance applications or when Redis is not available

**Features:**
- L1 memory cache only
- No Redis dependency
- Fastest possible reads (< 1ms)
- Perfect for development

**Use Cases:**
- Development environments
- Single-instance deployments
- Serverless functions
- Edge computing scenarios

---

### 10. **Write-Heavy Workload (Invalidation-Focused)**
📄 **File:** `10_Write_Heavy_Invalidation.md`

**Best For:** Systems with frequent updates and aggressive invalidation requirements

**Features:**
- Minimal caching, aggressive invalidation
- Pub/Sub or Streams for cross-instance invalidation
- Short TTL values
- Eventual consistency patterns

**Use Cases:**
- Real-time systems
- Collaborative editing
- Live dashboards
- Social media feeds

---

## 🗂️ Quick Selection Guide

### By Read/Write Ratio

| Read:Write Ratio | Recommended Scenario |
|------------------|---------------------|
| 100:1 (read-heavy) | #1 CQRS Multi-Level or #9 Memory Only |
| 10:1 (balanced) | #1 CQRS Multi-Level |
| 1:1 (write-heavy) | #10 Write-Heavy Invalidation |

### By Architecture

| Architecture | Recommended Scenario |
|--------------|---------------------|
| CQRS + MediatR | #1 CQRS Multi-Level |
| Simple REST API | #8 Simple REST API |
| Microservices | #5 Microservices Streams |
| Monolith | #9 Memory Only |

### By Feature Need

| Feature Needed | Recommended Scenario |
|----------------|---------------------|
| Session management | #3 Session Sliding |
| Large payloads | #4 Compression |
| Bulk invalidation | #2 Cache Tagging |
| Static data | #6 Cache Warming |
| Performance analysis | #7 Metrics |

### By Performance Requirement

| Latency Target | Recommended Scenario |
|----------------|---------------------|
| < 1ms | #9 Memory Only |
| 1-5ms | #1 CQRS Multi-Level |
| 5-10ms | #8 Simple REST API (Redis only) |

### By Reliability Need

| Reliability Need | Recommended Scenario |
|------------------|---------------------|
| Mission-critical | #5 Streams (guaranteed delivery) |
| Production | #1 CQRS Multi-Level + Pub/Sub |
| Development | #9 Memory Only |

---

## 📖 How to Use This Guide

### 1. **Identify Your Scenario**
- Review the descriptions above
- Match your requirements to a scenario
- Use the selection guides if unsure

### 2. **Read the Detailed Documentation**
- Open the corresponding `.md` file
- Follow the step-by-step setup
- Copy and adapt the code examples

### 3. **Customize for Your Needs**
- Adjust TTL values based on your data
- Modify cache keys to match your entities
- Combine features from multiple scenarios

### 4. **Test and Monitor**
- Use the provided performance metrics
- Monitor cache hit rates
- Adjust configuration as needed

---

## 🔄 Combining Scenarios

Many scenarios can be combined:

### Example 1: CQRS + Compression + Metrics
```json
{
  "TheTechLoopCache": {
    "EnableCompression": true,
    "EnableEffectivenessMetrics": true,
    "MemoryCache": { "Enabled": true }
  }
}
```

### Example 2: Multi-Level + Tagging + Warming
```csharp
builder.Services.AddTheTechLoopCache(builder.Configuration);
builder.Services.AddTheTechLoopMultiLevelCache(builder.Configuration);
builder.Services.AddTheTechLoopCacheWarmup();
// Tagging enabled via configuration
```

### Example 3: Streams + Compression + Metrics
```json
{
  "TheTechLoopCache": {
    "UseStreamsForInvalidation": true,
    "EnableCompression": true,
    "EnableEffectivenessMetrics": true
  }
}
```

---

## 📊 Feature Comparison Matrix

| Scenario | Multi-Level | Tagging | Compression | Streaming | Warming | Metrics |
|----------|-------------|---------|-------------|-----------|---------|---------|
| 1. CQRS | ✅ | ➖ | ➖ | ➖ | ➖ | ✅ |
| 2. Tagging | ➖ | ✅ | ➖ | ➖ | ➖ | ➖ |
| 3. Session | ✅ | ➖ | ➖ | ➖ | ➖ | ➖ |
| 4. Compression | ➖ | ➖ | ✅ | ➖ | ➖ | ➖ |
| 5. Streams | ➖ | ➖ | ➖ | ✅ | ➖ | ✅ |
| 6. Warming | ➖ | ➖ | ➖ | ➖ | ✅ | ➖ |
| 7. Metrics | ➖ | ➖ | ➖ | ➖ | ➖ | ✅ |
| 8. Simple | ➖ | ➖ | ➖ | ➖ | ➖ | ➖ |
| 9. Memory | ✅ (L1) | ➖ | ➖ | ➖ | ➖ | ➖ |
| 10. Write-Heavy | ➖ | ➖ | ➖ | ✅ | ➖ | ➖ |

Legend:
- ✅ Primary feature
- ➖ Can be added

> **Note:** As of v1.3.0, operational metrics (`cache.lock.wait_duration`, `cache.batch.size`, `cache.scan.duration`, `cache.scan.deleted_keys`) are always emitted — no configuration required. `EnableEffectivenessMetrics` controls only the per-entity effectiveness tracking meter (`TheTechLoop.Cache.Effectiveness`).

---

## 🛠️ Getting Started

1. **Choose your scenario** from the list above
2. **Open the detailed documentation** file
3. **Follow the setup steps** in order
4. **Copy the code examples** into your project
5. **Test with your data** and adjust as needed
6. **Monitor performance** using the provided metrics

---

## 📝 Additional Resources

- **[../README.md](../README.md)** — Project overview, features, API reference, and architecture
- **[../src/TheTechLoop.HybridCache.MediatR/README.md](../src/TheTechLoop.HybridCache.MediatR/README.md)** — MediatR pipeline behaviors guide

---

## 💡 Tips

- **Start simple:** Begin with scenario #8 (Simple REST API) then add features
- **Measure first:** Use scenario #7 (Metrics) to identify what to cache
- **Iterate:** Start with basic caching, optimize based on metrics
- **Combine features:** Most scenarios work great together
- **Test thoroughly:** Verify cache invalidation works correctly

---

## 🆘 Support

For questions or issues:
1. Check the troubleshooting section in each scenario doc
2. Review the main README.md
3. Open an issue on GitHub
4. Consult the Microsoft documentation links

---


**Version:** 1.3.0  
**Status:** Production-Ready ✅
