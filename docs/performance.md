# Performance

Single source of truth for Timberbot API performance. All optimization decisions reference here.

## Entity tracking

Event-driven double-buffered indexes via Timberborn's `EventBus`. Zero per-frame allocation, zero `GetComponent` calls per request, zero main-thread cost for reads.

- **Double buffer:** main thread writes to `_*Write` lists, swaps to `_*Read`. Background thread only reads `_*Read`. Zero contention.
- **Cached classes:** `CachedBuilding` (18 component refs + ~25 cached primitives), `CachedNaturalResource` (5 refs + 7 primitives), `CachedBeaver` (10 refs + 14 primitives). Modified in-place, `Clone()` for double-buffer. Refreshed at 1Hz (configurable via settings.json).
- **Background GET serving:** all reads served on HTTP listener thread. Only POST (writes) queue to main thread.
- **Static values at add-time:** EffectRadius, IsGenerator, IsConsumer, NominalPower, HasFloodgate, HasClutch, HasWonder, FloodgateMaxHeight, X, Y, Z, Orientation -- all set once in entity-add handler, never re-read.

| Index | Type | Mechanism | Per-frame cost | Rebuild trigger |
|---|---|---|---|---|
| `_buildingIndex` | `List<CachedBuilding>` | `EntityInitializedEvent` / `EntityDeletedEvent` | **zero** | entity add/remove |
| `_naturalResourceIndex` | `List<CachedNaturalResource>` | same | **zero** | entity add/remove |
| `_beaverIndex` | `List<CachedBeaver>` | same | **zero** | entity add/remove |
| `_entityCache` | `Dictionary<int, EntityComponent>` | same | **zero** | entity add/remove |

### Cached component refs

`CachedBuilding`, `CachedNaturalResource`, and `CachedBeaver` classes resolve all component references once at entity-add time. Endpoints read cached primitives (refreshed at 1Hz) without calling `GetComponent<T>()`.

| Class | Fields cached | GetComponent calls saved per item |
|---|---|---|
| `CachedBuilding` | BlockObject, Pausable, Floodgate, BuilderPrio, Workplace, WorkplacePrio, Reachability, Mechanical, Status, PowerNode, Site, Inventories, Wonder, Dwelling, Clutch, Manufactory, BreedingPod, RangedEffect | **18** |
| `CachedNaturalResource` | BlockObject, Living, Cuttable, Gatherable, Growable | **5** |
| `CachedBeaver` | NeedMgr, WbTracker, Worker, Life, Carrier, Deteriorable, Contaminable, Dweller, Citizen, Bot | **10** |

## Endpoint performance (measured, 522 buildings / 2986 trees / 65 beavers / 4161 total, 100 iterations)

All endpoints use cached class indexes, double-buffered reads on background thread.

| Endpoint | Items | Min (ms) | GetComponent | Notes |
|---|---|---|---|---|
| `ping` | 1 | **0.7** | 0 | Listener thread |
| `summary` | 3500+ | **0.9** | 0 | JwWriter, cached primitives |
| `buildings` | 522 | **2.1** | **0** | JwWriter |
| `buildings detail:full` | 522 | **3.4** | **0** | JwWriter, all fields |
| `trees` | 2983 | **8.6** | **0** | JwWriter |
| `gatherables` | 1504 | **6.7** | **0** | JwWriter |
| `beavers` | 65 | **1.1** | **0** | JwWriter, position/district/carrying |
| `beavers detail:full` | 65 | ~1.5 | **0** | all 38 needs, NeedGroupId, deterioration |
| `alerts` | 19 | **0.8** | **0** | JwWriter, cached primitives |
| `resources` | 13 | **0.8** | 0 | JwWriter, district registries |
| `power` | cached | **0.8** | **0** | JwWriter, groups by cached PowerNetworkId |
| `weather` | 1 | **0.8** | 0 | JwWriter, service fields |
| `time` | 1 | **0.8** | 0 | JwWriter, service fields |
| `prefabs` | 157 | **2.9** | 0 | JwWriter, building templates |
| `wellbeing` | 1 | **0.9** | 0 | JwWriter, cached beaver needs |
| `tree_clusters` | 5 | **0.9** | 0 | JwWriter, cached natural resources |
| `map` (stacking) | varies | ~10 | **0** | JwWriter, cached tile footprints, thread-safe |
| **burst (7 calls)** | -- | **17** | -- | 2ms avg per call |

### Optimization gaps

| Endpoint | What it does | Frequency | Notes |
|---|---|---|---|
| `BuildAllIndexes` | Initial index build | **once on load** | populates all indexes, N/A to optimize |
| `CollectSummary` (districts) | District resource counts | every bot turn | iterates district centers live, partially cached |

## Serialization

All endpoints use a single shared `JwWriter` instance -- fluent zero-alloc JSON writer with auto-separator handling. One 300KB pre-allocated instance, `Reset()` per request. Serial on listener thread, never concurrent.

**A/B test results (trees, 2985 items):** Dictionary 4.7ms, Anonymous objects 13.8ms (worst -- Newtonsoft reflection), StringBuilder **2.0ms** (winner). Main-thread cost for reads is **zero**.

## Thread model

| Location | Thread | Blocks game? |
|---|---|---|
| HTTP listener (accept + queue) | background | no |
| All GET requests (reads) | background (listener thread) | **no** |
| All POST requests (writes) | main thread via `DrainRequests` | yes, for duration |
| JSON serialization (`Respond`) | same thread as request | no for GETs |
| `RefreshCachedState` (snapshot mutable values) | main thread, cadenced (default 1s) | <1ms for 3500 entities |
| Double buffer swap | main thread, after refresh | ~0ms (ref swap, no copy-back) |

All reads served on the listener thread from double-buffered read lists. Zero main-thread cost for GET-only bot turns. Writes (POST) still queue to main thread. Thread-unsafe properties (reachability, powered) cached as primitives on main thread -- background thread never calls Unity component properties directly.

## GC pressure

See [zero-alloc.md](zero-alloc.md) for the full allocation audit with per-field analysis, grades, and remaining gaps.

**Summary:** Hot path (RefreshCachedState) is 98% zero-alloc. All containers reused via Clear(). Structs for stack alloc. RefChanged to skip string derivation. Indexed for-loops to avoid enumerator boxing. One unavoidable ToString() per HTTP response. Webhooks zero-alloc when no subscribers.

## Remaining bottlenecks

| # | Bottleneck | Cost | Notes |
|---|---|---|---|
| 1 | **Unity GC spikes** | random 0.5-2s | Unity GC freezes all threads. Reduced alloc pressure but unavoidable from mod |
| 2 | **sb.ToString() alloc** | 1 string per request (~100-500KB) | StringBuilder must create final string. Unavoidable, once per request |

## Backlog

| # | Issue | Effort | Details |
|---|---|---|---|
| ~~1~~ | ~~Webhook rate limiting~~ | -- | **FIXED** -- 200ms batching window (configurable via `webhookBatchMs`). Events accumulate, one POST per webhook per flush |
| ~~2~~ | ~~Webhook circuit breaker~~ | -- | **FIXED** -- 5 consecutive failures disables webhook, logged via TimberbotLog |
| 3 | TimberbotService split | 3-4 hr | 35 constructor params, 4668 lines across 7 partial files. Extract `WebhookManager`, `EntityCache`. Move DI params to only the services that need them |
| ~~4~~ | ~~RefreshCachedState error isolation~~ | -- | **Already done** -- per-entity try/catch in all 3 loops (buildings, natural resources, beavers). Bad entity logs error, loop continues |
| 5 | NeedMgr.GetNeeds() allocation | 1 hr | Marked "unknown" severity. 65 calls + 2470 List.Add per refresh. May allocate new collection per call. Profile and fix or document as acceptable |

## Optimization history

| Change | trees | buildings | buildings full | burst (7 calls) |
|---|---|---|---|---|
| Baseline (full entity scan) | ~50ms est | ~20ms est | ~30ms est | ~150ms est |
| Typed entity indexes | 29ms | 9ms | 10ms | 67ms |
| Event-driven (EventBus) | 28ms | 9ms | 13ms | 62ms |
| Cached component refs | 25ms | 8ms | 13ms | 64ms |
| GETs on listener thread | 23ms | 6.5ms | 8ms | 39ms |
| Double buffer + cached primitives | 4.7ms | 2.8ms | 1.3ms | 28ms |
| StringBuilder (trees) | 2.0ms | 2.8ms | 1.3ms | 28ms |
| Alloc-once + SB buildings | 2.0ms | ~1ms | ~1ms | ~20ms est |
| DRY (Jw + DoubleBuffer) | **8.6ms** | **2.1ms** | **3.4ms** | **17ms** |

## Late-game projections

| Endpoint | Current items | Min (ms) | Per-item | Late-game items | Projected |
|---|---|---|---|---|---|
| `buildings` | 522 | 2.1 | 4.0us | 1500 | ~6ms |
| `buildings full` | 522 | 3.4 | 6.5us | 1500 | ~10ms |
| `trees` | 2983 | 8.6 | 2.9us | 5000 | ~14ms |
| `gatherables` | 1504 | 6.7 | 4.5us | 3000 | ~13ms |
| `beavers` | 65 | 1.1 | 16.9us | 250 | ~4ms |
| `alerts` | 19 | 0.8 | -- | 50 | ~1ms |
| `summary` | 3500+ | 0.9 | 0.26us | 10000 | ~3ms |
| `prefabs` | 157 | 2.9 | 18.5us | 200 | ~4ms |
| `wellbeing` | 65 beavers | 0.9 | -- | 250 | ~3ms |
| `power` | cached | 0.8 | -- | 50+ networks | ~2ms |
| `map` (region) | varies | ~10 | region-bounded | larger builds | ~20ms |
| **Burst (7 calls)** | -- | **17** | -- | -- | **~30ms** |

All scaling is linear with item count. Zero main-thread cost for GET-only bot turns. Bot polling at 1/min cadence -- even 30ms burst is imperceptible. RefreshCachedState runs once per second on main thread (<1ms for 3500 entities).

### Late-game risk factors

- **Bots scale free:** bots don't eat/drink/sleep but DO add to beaver index. 50+ bots = more entities to cache and serialize
- **Multi-district:** each district has its own resource counters. 3+ districts increases summary/resources iteration
- **Vertical builds:** heavy platform/stair stacking increases map `occupants` arrays (more allocs per tile)
- **Power networks:** complex power grids fragment into many small networks. `power` endpoint iterates all buildings per call

## Pre-release audit (v0.7.0)

### Hot path: RefreshCachedState (every 1s on main thread)

Total measured cost: ~0.4ms/sec (0.04% of frame budget at 60fps).

| What | Cost | Allocs | Status |
|---|---|---|---|
| ~500 building property reads | ~0.05ms | 0 | Good |
| ~3000 natural resource reads | ~0.1ms | 0 | Good |
| ~80 beaver reads + needs | ~0.2ms | 0 | Good |
| Inventory for-loop (indexed) | ~0.05ms | 0 | **FIXED** -- was foreach, benchmarked 25% faster |
| `GetNeeds()` IEnumerable per beaver | ~0.01ms | ~80 enumerator boxes | Can't fix -- game API returns IEnumerable |
| 3x `Swap()` pointer swap | O(1) | 0 | Good |

### HTTP response (per request, background thread)

| Issue | Severity | Status |
|---|---|---|
| `Formatting.Indented` whitespace bloat | Medium | **FIXED** -- switched to `Formatting.None` (~30% smaller) |
| `GetBytes()` byte array alloc per response | Medium | **FIXED** -- `StreamWriter` writes directly to output stream |
| UTF-8 BOM prefix from StreamWriter | Critical | **FIXED** -- `new UTF8Encoding(false)` |
| JwWriter responses bypass Newtonsoft entirely | Good | Already optimized |

### Webhook flush (every 200ms)

| Issue | Severity | Status |
|---|---|---|
| `new StringBuilder(256)` per webhook per flush | Medium | **FIXED** -- reuses field-level `_webhookSb` |
| `JsonConvert.SerializeObject` per event in PushEvent | Medium | Acceptable -- only fires with subscribers |
| Batching (200ms window) | Good | Reduces ThreadPool items for high-frequency events |
| Circuit breaker (5 failures) | Good | Disables dead URLs automatically |

### JsonWriter bugs and fixes

| Issue | Severity | Status |
|---|---|---|
| `Key().OpenArr()` double-comma (`"key":,[`) | Critical | **FIXED** -- `_hasValue[_depth] = false` after Key() |
| Value methods missing `AutoSep()` (`"a""b"` in arrays) | Critical | **FIXED** -- all value methods now call AutoSep() |
| `Float(v).ToString(fmt)` string alloc | Low | **FIXED** -- zero-alloc digit writing to SB |
| Collect-then-emit for prefab costs | Medium | **FIXED** -- prevents partial JSON on exception |

### Production verdict

- Main thread: 0.4ms/sec for 3500 entities. Scales linearly, projected ~1ms at late-game (10K entities)
- Zero GC0 collections across all endpoints (confirmed by /api/benchmark)
- All JSON output validated via test suite (51 tests, any-save-game compatible)
- No blocking issues remaining

## Test coverage

Performance tests in `timberbot/script/test_validation.py`:

- **Latency**: 20 endpoints x 100 iterations each (2000 calls total). All endpoints must be under 50ms min
- **Reliability**: all 2000 responses must be valid (no errors, no corruption)
- **Cache consistency**: same endpoint called twice returns same count (no stale refs)
- **Cache invalidation**: place path -> count+1, demolish -> count back (EventBus + DoubleBuffer)
- **Burst**: 7 sequential calls < 3s total
- **Save-agnostic**: all tests use `find_placement`/`find_building` for dynamic coords
