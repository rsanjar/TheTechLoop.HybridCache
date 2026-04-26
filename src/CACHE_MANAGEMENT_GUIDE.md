# Cache Key Management & Invalidation Guide

How to organize cache keys, prefixes, and tags in a project that uses `TheTechLoop.HybridCache.MediatR` so that invalidation is centralized, refactor-safe, and easy to wire into MediatR commands and queries.

## Core idea

The biggest leverage is **leaning on tags as the primary invalidation mechanism** and pulling all tag/key/prefix strings into one centralized catalog. Once you do that, queries and commands just reference catalog members instead of inline strings, drift goes away, and a command no longer has to enumerate every cache shape it might invalidate.

## 1. Central catalog

One static class per aggregate, organized as partial classes so each feature owns its own slice. Tags are short logical names — the library auto-scopes them with `ServiceName:CacheVersion:` so you don't include those.

```csharp
// Application/Caching/CacheCatalog.cs
public static partial class CacheCatalog
{
    public static class Dealership
    {
        // Aggregate-wide tag — invalidates everything dealership-related.
        public const string AggregateTag = "Dealership";

        // Entity-narrow tag — invalidates only entries scoped to one dealership.
        public static string EntityTag(int id) => $"Dealership:{id}";

        // Exact keys
        public static string ById(int id) => $"Dealership:{id}";

        // Prefixes for shape-based invalidation
        public const string ListPrefix   = "Dealership:List";
        public const string SearchPrefix = "Dealership:Search";

        // Parameterized read-shape keys
        public static string Search(string region, int page, int size)
            => $"Dealership:Search:{region}:{page}:{size}";
    }

    public static class User { /* same shape */ }
    public static class Order { /* same shape */ }
}
```

```csharp
// Application/Caching/CacheDurations.cs
public static class CacheDurations
{
    public static readonly TimeSpan Short    = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Medium   = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan Long     = TimeSpan.FromHours(2);
    public static readonly TimeSpan Extended = TimeSpan.FromHours(12);
}
```

## 2. Queries declare tags, not just keys

Every cacheable query gets at least one **aggregate tag** plus optionally an **entity tag**. Commands then invalidate by tag — no need to enumerate the dozens of query shapes that exist.

```csharp
public record GetDealershipByIdQuery(int Id)
    : IRequest<DealershipDto?>, ITaggedCacheable
{
    public string CacheKey         => CacheCatalog.Dealership.ById(Id);
    public TimeSpan CacheDuration  => CacheDurations.Medium;

    public IReadOnlyList<string> CacheTags =>
    [
        CacheCatalog.Dealership.AggregateTag,        // broad
        CacheCatalog.Dealership.EntityTag(Id)        // narrow
    ];
}

public record SearchDealershipsQuery(string Region, int Page, int Size)
    : IRequest<IReadOnlyList<DealershipDto>>, ITaggedCacheable
{
    public string CacheKey         => CacheCatalog.Dealership.Search(Region, Page, Size);
    public TimeSpan CacheDuration  => CacheDurations.Short;

    public IReadOnlyList<string> CacheTags =>
    [
        CacheCatalog.Dealership.AggregateTag         // any update flushes search results
    ];
}
```

## 3. Commands invalidate by tag

This is where the win shows up: an `Update` command no longer needs to know about `ListPrefix`, `SearchPrefix`, individual ID keys, etc. It just invalidates the entity tag (or the aggregate tag if the change is broad).

```csharp
public record UpdateDealershipCommand(int Id, string Name)
    : IRequest<bool>, ICacheTagInvalidatable
{
    public IReadOnlyList<string> CacheTagsToInvalidate =>
    [
        CacheCatalog.Dealership.EntityTag(Id)        // narrow blast radius
    ];
}

public record CreateDealershipCommand(string Name, string Region)
    : IRequest<int>, ICacheTagInvalidatable
{
    // No specific entity exists yet — flush list/search shapes via aggregate.
    public IReadOnlyList<string> CacheTagsToInvalidate =>
    [
        CacheCatalog.Dealership.AggregateTag
    ];
}

public record DeleteDealershipCommand(int Id)
    : IRequest<bool>, ICacheTagInvalidatable
{
    public IReadOnlyList<string> CacheTagsToInvalidate =>
    [
        CacheCatalog.Dealership.AggregateTag,
        CacheCatalog.Dealership.EntityTag(Id)
    ];
}
```

For commands that span aggregates (e.g., `TransferDealershipToOwnerCommand`), just list tags from both:

```csharp
public IReadOnlyList<string> CacheTagsToInvalidate =>
[
    CacheCatalog.Dealership.EntityTag(DealershipId),
    CacheCatalog.User.EntityTag(NewOwnerId),
    CacheCatalog.User.EntityTag(OldOwnerId),
];
```

## 4. Optional: abstract base records

If the boilerplate `CacheDuration` line bothers you, abstract bases cut it down:

```csharp
public abstract record TaggedQuery<TResponse> : IRequest<TResponse>, ITaggedCacheable
{
    public abstract string CacheKey { get; }
    public abstract IReadOnlyList<string> CacheTags { get; }
    public virtual TimeSpan CacheDuration => CacheDurations.Medium;
}

public abstract record TagInvalidatingCommand<TResponse>
    : IRequest<TResponse>, ICacheTagInvalidatable
{
    public abstract IReadOnlyList<string> CacheTagsToInvalidate { get; }
}
```

Then:

```csharp
public sealed record GetDealershipByIdQuery(int Id) : TaggedQuery<DealershipDto?>
{
    public override string CacheKey => CacheCatalog.Dealership.ById(Id);

    public override IReadOnlyList<string> CacheTags =>
    [
        CacheCatalog.Dealership.AggregateTag,
        CacheCatalog.Dealership.EntityTag(Id)
    ];
}
```

Hold off on bases until the inline noise actually annoys you — flat records read better in small projects.

## 5. When you still need keys/prefixes

Tags cover ~90% of cases. Reach for `ICacheInvalidatable` (keys/prefixes) only when:

- A specific cached item must die immediately and you don't want to rely on tag set membership being correct.
- You need to invalidate something in a shape that wasn't tagged (legacy entries, third-party producers).
- The cache type doesn't support tags (e.g., `MemoryOnlyCacheService` without `EnableTagging`).

In those cases, mix both interfaces on one command — the behavior handles the union:

```csharp
public record UpdateDealershipCommand(int Id, string Name)
    : IRequest<bool>, ICacheInvalidatable, ICacheTagInvalidatable
{
    public IReadOnlyList<string> CacheKeysToInvalidate =>
        [CacheCatalog.Dealership.ById(Id)];

    public IReadOnlyList<string> CachePrefixesToInvalidate =>
        []; // empty — tags handle it

    public IReadOnlyList<string> CacheTagsToInvalidate =>
        [CacheCatalog.Dealership.EntityTag(Id)];
}
```

## 6. Suggested project layout

```
src/Application/
  Caching/
    CacheCatalog.cs                     // root partial class
    CacheCatalog.Dealership.cs          // partial per aggregate
    CacheCatalog.User.cs
    CacheCatalog.Order.cs
    CacheDurations.cs
    Abstractions/                       // optional, if you adopt section 4
      TaggedQuery.cs
      TagInvalidatingCommand.cs
  Dealerships/
    Queries/GetDealershipByIdQuery.cs
    Commands/UpdateDealershipCommand.cs
```

Each aggregate's `CacheCatalog.<Aggregate>.cs` lives next to the feature folder mentally, even if it's physically grouped in `Caching/`.

## 7. Migration from your current state

1. Create `CacheCatalog` and `CacheDurations`. Lift the strings out of one aggregate first as a pilot.
2. Add aggregate + entity tags to the queries you migrate. Keep the existing `CacheKey` — it doesn't change.
3. Replace the command's `CacheKeysToInvalidate` / `CachePrefixesToInvalidate` lists with `CacheTagsToInvalidate` referencing the catalog. Remove `ICacheInvalidatable` from the command if tags fully cover it.
4. Verify in Redis (the tag set keys are visible: `tag:<service>:<version>:Dealership` etc.) that the right keys are getting tagged.
5. Roll the rest of the aggregates the same way.

## Gotchas

- **Tags are per-service.** A `company-svc` "Dealership" tag is `company-svc:v1:Dealership` in Redis; an `order-svc` "Dealership" tag is `order-svc:v1:Dealership`. Cross-service invalidation flows through Pub/Sub messages but each service still only sees entries it tagged itself. Don't expect `order-svc` to invalidate `company-svc`'s cache by tag name alone.
- **Don't tag with values that change frequently.** Tags are sets in Redis — every cache write to a tag does an `SADD`. If you tag with something like `Dealership:LastModified:{timestamp}`, your tag set just keeps growing.
- **Bump `CacheVersion` when key formats change.** If you rename `Dealership:Search:{region}:{page}:{size}` to a different shape, old entries still live in Redis until TTL. Bumping the version in config makes them unreachable immediately.
- **Two-level tagging is the sweet spot.** Aggregate tag + entity tag covers virtually every invalidation scenario. Adding more dimensions (shape tags, parameter tags) buys you finer blast radius but also more bookkeeping.
- **Tag invalidation only fires when `EnableTagging = true` and the cache implements `ICacheServiceWithEntryOptions`.** Memory-only/no-op modes silently skip tag work — `CachingBehavior` throws if the cache doesn't support entry options, but the tag service itself just isn't registered in those modes.
