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
| `summary` | 3500+ | **0.9** | 0 | TimberbotJw, cached primitives |
| `buildings` | 522 | **2.1** | **0** | TimberbotJw |
| `buildings detail:full` | 522 | **3.4** | **0** | TimberbotJw, all fields |
| `trees` | 2983 | **8.6** | **0** | TimberbotJw |
| `gatherables` | 1504 | **6.7** | **0** | TimberbotJw |
| `beavers` | 65 | **1.1** | **0** | TimberbotJw, position/district/carrying |
| `beavers detail:full` | 65 | ~1.5 | **0** | all 38 needs, NeedGroupId, deterioration |
| `alerts` | 19 | **0.8** | **0** | TimberbotJw, cached primitives |
| `resources` | 13 | **0.8** | 0 | TimberbotJw, district registries |
| `power` | cached | **0.8** | **0** | TimberbotJw, groups by cached PowerNetworkId |
| `weather` | 1 | **0.8** | 0 | TimberbotJw, service fields |
| `time` | 1 | **0.8** | 0 | TimberbotJw, service fields |
| `prefabs` | 157 | **2.9** | 0 | TimberbotJw, building templates |
| `wellbeing` | 1 | **0.9** | 0 | TimberbotJw, cached beaver needs |
| `tree_clusters` | 5 | **0.9** | 0 | TimberbotJw, cached natural resources |
| `map` (stacking) | varies | ~10 | **0** | TimberbotJw, cached tile footprints, thread-safe |
| **burst (7 calls)** | -- | **17** | -- | 2ms avg per call |

### Optimization gaps

None. All GET endpoints read entirely from cached double buffers. Zero live `GetComponent` calls on the HTTP thread.

## Serialization

All endpoints use a single shared `TimberbotJw` instance -- fluent zero-alloc JSON writer with auto-separator handling. One 300KB pre-allocated instance, `Reset()` per request. Serial on listener thread, never concurrent.

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

**Summary:** Hot path (RefreshCachedState) confirmed **zero-alloc** via 10K-iteration benchmark (0 GC0 collections across 760K+ game API calls). `GetNeeds()`, `GetNeed()`, `GetNeedWellbeing()` all return cached objects. All containers reused via Clear(). Structs for stack alloc. RefChanged to skip string derivation. Indexed for-loops to avoid enumerator boxing. One unavoidable ToString() per HTTP response. Webhooks zero-alloc when no subscribers. Only remaining micro-optimization: `Math.Round()` boxing (2400 calls/sec, ~96KB/sec).

## Remaining bottlenecks

| # | Bottleneck | Cost | Notes |
|---|---|---|---|
| 1 | **Unity GC spikes** | random 0.5-2s | Unity GC freezes all threads. Reduced alloc pressure but unavoidable from mod |
| 2 | **sb.ToString() alloc** | 1 string per request (~100-500KB) | StringBuilder must create final string. Unavoidable, once per request |

## Backlog

| # | Issue | Effort | Details |
|---|---|---|---|
| ~~1~~ | ~~Webhook rate limiting~~ | -- | **FIXED** -- 200ms batching window (configurable via `webhookBatchMs`). Events accumulate, one POST per webhook per flush |
| ~~2~~ | ~~Webhook circuit breaker~~ | -- | **FIXED** -- 30 consecutive failures (configurable) disables webhook, logged via TimberbotLog |
| ~~3~~ | ~~TimberbotService split~~ | -- | **FIXED** -- 8 independent classes (TimberbotService 7 DI params, TimberbotRead 10, TimberbotWrite 20, TimberbotPlacement 13, TimberbotEntityCache 5, TimberbotWebhook 5, TimberbotDebug 1) |
| ~~4~~ | ~~RefreshCachedState error isolation~~ | -- | **Already done** -- per-entity try/catch in all 3 loops |
| ~~5~~ | ~~NeedMgr.GetNeeds() allocation~~ | -- | **CONFIRMED zero-alloc** via 10K benchmark (0 GC0 across 760K calls) |

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
| `GetNeeds()` IEnumerable per beaver | ~0.01ms | 0 (confirmed) | **Benchmarked: 0 GC0 across 760K calls** |
| 3x `Swap()` pointer swap | O(1) | 0 | Good |

### HTTP response (per request, background thread)

| Issue | Severity | Status |
|---|---|---|
| `Formatting.Indented` whitespace bloat | Medium | **FIXED** -- switched to `Formatting.None` (~30% smaller) |
| `GetBytes()` byte array alloc per response | Medium | **FIXED** -- `StreamWriter` writes directly to output stream |
| UTF-8 BOM prefix from StreamWriter | Critical | **FIXED** -- `new UTF8Encoding(false)` |
| TimberbotJw responses bypass Newtonsoft entirely | Good | Already optimized |

### Webhook flush (every 200ms)

| Issue | Severity | Status |
|---|---|---|
| `new StringBuilder(256)` per webhook per flush | Medium | **FIXED** -- reuses field-level `_webhookSb` |
| ~~`JsonConvert.SerializeObject` per event~~ | -- | **FIXED** -- TimberbotJw writes directly, zero Newtonsoft on hot path |
| Batching (200ms window) | Good | Reduces ThreadPool items for high-frequency events |
| Circuit breaker (30 failures, configurable) | Good | Disables dead URLs automatically |

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

- **51 tests** covering all Python client methods, any save game, any faction
- **Latency**: 20 endpoints x 100 iterations each (2000 calls total). All endpoints under 50ms min
- **Reliability**: all 2000 responses valid (no errors, no corruption)
- **Cache consistency**: same endpoint called twice returns same count (no stale refs)
- **Cache invalidation**: place path -> count+1, demolish -> count back (EventBus + DoubleBuffer)
- **Data accuracy**: `validate` endpoint compares cached vs live game state per field. `validate_all` checks all 621 entities, 3876 fields, 0 mismatches
- **Burst**: 7 sequential calls < 3s total (24ms measured)
- **Save-agnostic**: discovery phase detects faction, map bounds, existing buildings
- **Webhooks**: register, receive, filter, unregister, bad URL resilience, payload accuracy
- **CLI args**: `--perf`, `--benchmark`, `--list`, `-n`, individual test names
