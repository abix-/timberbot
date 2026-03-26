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

Every heap allocation in a Unity mod contributes to GC pressure. Unity uses a stop-the-world garbage collector -- when enough garbage accumulates, ALL threads pause (0.5-50ms). Our mod runs inside someone else's game. Every allocation we make adds to the pile that triggers the game's GC stutter.

Goal: allocate once at game load, reuse forever. The only per-request allocation should be the final `ToString()` to produce the HTTP response.

### Allocation architecture

```
Game loads
  -> allocate TimberbotDoubleBuffer x3, TimberbotJw, Dictionaries, Lists
  -> done allocating

Every 1 second (main thread)
  -> read building/beaver/tree properties into existing objects (zero alloc)
  -> refresh district population + resources into CachedDistrict list
  -> swap buffer pointers (zero alloc)

Every HTTP request (background thread)
  -> read from cached buffers (zero alloc)
  -> write into existing StringBuilder via TimberbotJw (zero alloc)
  -> ToString() to create response string (1 alloc, unavoidable)
```

### Hot path: RefreshCachedState (1Hz, main thread)

**Zero-alloc (confirmed):**

| What | Count/sec | How |
|---|---|---|
| Building property reads (Finished, Paused, Workers, Powered, etc) | 500 | Direct field reads from cached component refs |
| Natural resource reads (Alive, Grown, Growth, Marked) | 3000 | Direct field reads |
| Beaver reads (Wellbeing, Position, Carrying, Contaminated) | 80 | Direct field reads |
| `CleanName()` for workplace/district | 80 | RefChanged skips unless reference changes. 99% of calls are a pointer compare returning false |
| `Inventory.Clear()` + repopulate | 500 | Dict allocated once on first refresh, reused via Clear() |
| `NutrientStock.Clear()` + repopulate | 5 | Same pattern, only breeding pods |
| `Needs.Clear()` + repopulate | 80 | List allocated once, reused via Clear() |
| `new CachedNeed{...}` struct | 2400 | Struct = stack alloc, not heap. Goes directly into the List |
| Inventory for-loop (indexed) | 500 | `for (int ii = 0; ...)` -- no enumerator boxing |
| `Swap()` x3 | 3 | Pointer swap, O(1), zero copy |
| `Recipes = new List<string>()` | Rare | Guarded by null check -- allocates once per building, first refresh only |

**Known allocations (accepted):**

| What | Count/sec | Bytes/sec | Severity | Status |
|---|---|---|---|---|
| `Math.Round(need.Points, 2)` | 2400 | ~96KB | Medium | TODO -- replace with manual rounding |
| `foreach GetNeeds()` enumerator | 80 | 0 | None | Confirmed zero-alloc (10K benchmark, 0 GC0) |
| `GetNeed(id)` return value | 2400 | 0 | None | Confirmed zero-alloc (returns cached) |
| `GetNeedWellbeing(id)` | 2400 | 0 | None | Confirmed zero-alloc (returns int) |
| `foreach BreedingPod.Nutrients` | 5 | 0 | None | Confirmed zero-alloc (10K benchmark, 0 GC0) |

**Previously fixed:**

| What was allocating | Fix | Savings |
|---|---|---|
| `Priority.ToString()` per building per refresh | Static lookup array | ~500 string allocs/sec |
| `CleanName()` per employed beaver per refresh | RefChanged pattern (ref compare) | ~50 string allocs/sec |
| `GetComponent<EntityComponent>()` per beaver per refresh | Cached `Go` field at add-time | ~80 GetComponent calls/sec |
| Building X/Y/Z/Orientation re-read per refresh | Moved to add-time (immutable) | ~2000 property reads/sec |
| `foreach Inventories.AllInventories` | Indexed for-loop | Enumerator boxing eliminated |
| `foreach inv.Stock` | Indexed for-loop | Enumerator boxing eliminated |
| 60fps refresh rate | Cadenced to 1Hz (configurable) | 59 out of 60 refreshes eliminated |

### HTTP response (per request, background thread)

**Zero-alloc:**

| What | How |
|---|---|
| `_jw.Reset()` | Clears existing 300KB StringBuilder, no new alloc |
| `jw.Key().Int().Str().Bool()` | Appends to existing SB |
| `jw.Float()` | Zero-alloc digit writing (no ToString) |
| `jw.OpenObj().CloseObj()` | Appends `{` `}` to existing SB |
| TimberbotJw auto-separator commas | `_hasValue` flag, no string alloc |

**Accepted allocations:**

| What | Count | Bytes | Why accepted |
|---|---|---|---|
| `jw.ToString()` | 1 per request | 100-500KB | Unavoidable -- HTTP needs the string as bytes |
| `StreamWriter` internal buffer | 1 per request | ~1KB | .NET runtime, small |
| `$"{interpolation}"` in summary alerts | ~5 per summary | ~200B total | Negligible |
| `JsonConvert.SerializeObject` for non-Jw endpoints | 1 per debug/validate | Varies | Rare endpoints only |

### Webhooks (main thread, only with subscribers)

No subscribers (common case): every `[OnEvent]` handler checks `_webhooks.Count > 0` before doing anything. Zero allocations when nobody is listening.

With subscribers:

| What | Count | When | Why accepted |
|---|---|---|---|
| `TimberbotJw` payload string | 1 per event | PushEvent | ~200 byte string via `_jw.ToString()`. No Newtonsoft |
| `_webhookSb.Clear()` per flush | 0 alloc | Every 200ms | Reuses field-level SB |
| `sb.ToString()` per flush per webhook | 1 | Every 200ms | Unavoidable |
| `new StringContent()` per flush | 1 | Every 200ms | On ThreadPool, off main thread |

### Entity lifecycle (per add/remove)

| What | When | Why accepted |
|---|---|---|
| `new CachedBuilding{...}` | Building placed | Once per entity lifetime |
| `new CachedBeaver{...}` | Beaver born | Once per entity lifetime |
| `new List<(int,int,int)>` OccupiedTiles | Building placed | Once, holds tile footprint |
| `CleanName(go.name)` | Entity created | One string per entity |
| Webhook `PushEvent("building.placed", ...)` | Entity created | Only with webhook subscribers |

### Benchmark results (10K iterations, 76 beavers, 546 buildings)

Measured via `/api/benchmark` with 10,000 iterations to ensure GC0 detection sensitivity.

| Test | GC0 | ms/call | Total calls | Verdict |
|---|---|---|---|---|
| `NeedMgr.GetNeeds.foreach` | **0** | 0.110 | 760,000 | Zero-alloc. Returns cached collection |
| `NeedMgr.FullNeedLoop` (GetNeeds + GetNeed + GetNeedWellbeing) | **0** | 0.319 | 760,000 | All three calls zero-alloc |
| `BreedingPod.Nutrients` foreach | **0** | 0.006 | 60,000 | Zero-alloc. IEnumerable but no boxing |
| `Inventories.foreach` (all buildings) | **0** | 0.056 | 522,000 | Zero-alloc |
| `Inventories.forLoop` (all buildings) | **0** | 0.045 | 522,000 | Zero-alloc. 24% faster than foreach |
| `Inventories.AllInventories.only` | **0** | 0.020 | 522,000 | Just accessing inventories, no stock |
| `Inventories.FullRefreshSim` (forLoop + dict) | **0** | 0.058 | 522,000 | Full production loop with dict insert |

All hot path game API calls confirmed zero-alloc across 760K+ invocations.

### Overall allocation grade

| Layer | Frequency | Allocs/sec (steady state) | Grade |
|---|---|---|---|
| RefreshCachedState | 1Hz | **0** (confirmed by 10K benchmark) | **A+** |
| HTTP GET response | On demand | 1 (ToString) + ~5 small | **A-** |
| Webhook (no subscribers) | N/A | 0 | **A+** |
| Webhook (with subscribers) | 5Hz flush | ~5-20 (TimberbotJw strings only) | **A-** |
| Entity lifecycle | Rare | N per entity | **A** (expected) |

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
