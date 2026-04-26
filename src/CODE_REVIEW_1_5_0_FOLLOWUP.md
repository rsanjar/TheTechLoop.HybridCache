# Code Review — TheTechLoop.HybridCache 1.5.0 (Follow-up Pass)

Verification pass after the review-driven fixes were applied. Tests not in scope.

## Verification of fixes

All six items called out in the changelog land cleanly:

- **Streams publisher parity** — `RedisCacheInvalidationStreamPublisher` now implements `ICacheInvalidationPublisher` and `ICacheTagInvalidationPublisher` (lines 273-276 of `CacheInvalidationStreamConsumer.cs`), and `AddTheTechLoopCacheInvalidation` registers the same singleton under all three interfaces. The MediatR `CacheInvalidationBehavior` will resolve and publish through it.
- **Streams tag receive** — `ProcessInvalidationMessageAsync` now delegates `case "tag":` to `ICacheTagInvalidationService.RemoveByTagAsync` with a warning when the service isn't registered, mirroring the Pub/Sub subscriber.
- **Multi-level tag-after-L2** — both `SetAsync(options)` and `PopulateMultiLevelAsync` now gate `AddTagsAsync` on `SetL2SafeAsync`'s bool result.
- **L1 absolute honors options** — the absolute branch in `SetL1(key, value, options)` now uses `options.Expiration` instead of falling through to the config default.
- **Tag invalidation dedup** — `CacheTagInvalidationService` no longer takes `IDistributedCache`; it sweeps L1 only and lets the Lua script handle L2 + metadata.
- **Compression docstring + scoping docs** — the misleading "eliminates Base64" line is replaced with an honest "depending on the inner serializer this may still be represented as Base64 JSON at rest" note, and the MediatR README documents the per-service tag scoping ("tag invalidation is per service/version scope unless services intentionally share the same cache scope").

## Remaining issues

### Minor

### 1. New behavior change for L1 absolute is undocumented
Previously `SetL1(key, value, options)` capped L1 at `MemoryCache.DefaultExpirationSeconds` (typically 30s) for absolute entries. After the fix it uses `options.Expiration` directly — which for `CacheEntryOptions.Absolute(TimeSpan.FromMinutes(30))` means a 30-minute L1 entry, even though the L1 cap was historically the safety valve preventing L1 from growing unbounded.

**Fix:** add a CHANGELOG entry, since callers who relied on the implicit short L1 TTL will see longer-lived L1 entries.

### 2. `MultiLevelCacheService.RemoveByPrefixAsync` still no-ops L2
The XML doc was tightened ("Direct calls do not enumerate local L1 entries"), but the same is true for the L2 side: it doesn't even call `_l2.RemoveAsync` for known matching keys (impossible without SCAN, which only the subscriber has). `RedisCacheService.RemoveByPrefixAsync` has the same pattern; both are consistent now, just incomplete.

**Fix:** the summary should also call out that L2 deletion is deferred to the subscriber path.

### 3. `ICacheInvalidationStreamPublisher` is now redundant
It declares the same three method signatures as `ICacheInvalidationPublisher` + `ICacheTagInvalidationPublisher`. With the streams publisher implementing all three, nothing benefits from having the streams-specific interface — except backward compatibility for anyone injecting it directly.

**Fix:** could be marked `[Obsolete]` for a future major version.

### 4. XML doc on `AddTheTechLoopCacheInvalidation` is stale
Says "Registers `ICacheInvalidationPublisher` and starts the background subscriber." It now also registers `ICacheTagInvalidationPublisher` and (in streams mode) `ICacheInvalidationStreamPublisher`.

**Fix:** one-line doc update.

### 5. Parameter-name divergence on streams publisher
`ICacheInvalidationPublisher.PublishAsync(string cacheKey, …)` vs `RedisCacheInvalidationStreamPublisher.PublishAsync(string key, …)`. C# allows it, but XML/doc tools flag it.

**Fix:** rename the impl param to `cacheKey` to silence it.

### Pre-existing, worth noting

### 6. `CacheInvalidationSubscriber` "key:" branch doesn't sweep tag indices
When a service receives a self-published key invalidation, it deletes the data key but leaves orphaned reverse-index/tag-set entries pointing at it. They TTL out via `TagIndexTtlMinutes`. Not introduced by this changeset, but an inconsistency with `RedisCacheService.RemoveAsync` which calls `_tagService.RemoveAsync(key)` after the data delete.

### 7. `IMemoryCache` injection on the subscribers/streams consumer is nullable in the constructor but not optional in DI
Microsoft.Extensions.DependencyInjection does not honor C# default parameter values — if `IMemoryCache` isn't registered (single-level Redis without MultiLevel), construction will throw. Pre-existing risk; the multi-level path always registers it, so it's only a problem in unusual configurations.

## What's good in this round

The constructor change on `CacheTagInvalidationService` (dropping `IDistributedCache`) is a clean simplification — the Lua script is now the single source of truth for L2 + metadata cleanup. The streams publisher's tri-interface implementation removes the silent regression from the prior version. The MultiLevel L1 absolute fix and tag-after-L2 gating bring it into parity with `RedisCacheService`. Net: 1.5.0 is in materially better shape than the first pass.

## Ship recommendation

The four high-priority items from the previous review are all resolved. The remaining items are doc/changelog or pre-existing concerns rather than blockers — ship this and address them in a follow-up.
