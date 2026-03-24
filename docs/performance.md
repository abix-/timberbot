# Performance

Single source of truth for Timberbot API performance. All optimization decisions reference here.

## Entity tracking

Event-driven double-buffered indexes via Timberborn's `EventBus`. Zero per-frame allocation, zero `GetComponent` calls per request, zero main-thread cost for reads.

- **Double buffer:** main thread writes to `_*Write` lists, swaps to `_*Read`. Background thread only reads `_*Read`. Zero contention.
- **Cached structs:** `CachedBuilding` (21 component refs + ~20 cached primitives) and `CachedNaturalResource` (5 refs + 7 primitives). All mutable state refreshed on main thread each frame.
- **Background GET serving:** all reads served on HTTP listener thread. Only POST (writes) queue to main thread.

| Index | Type | Mechanism | Per-frame cost | Rebuild trigger |
|---|---|---|---|---|
| `_buildingIndex` | `List<CachedBuilding>` | `EntityInitializedEvent` / `EntityDeletedEvent` | **zero** | entity add/remove (instant) |
| `_naturalResourceIndex` | `List<CachedNaturalResource>` | same | **zero** | entity add/remove (instant) |
| `_beaverIndex` | `List<CachedBeaver>` | same | **zero** | entity add/remove (instant) |
| `_entityCache` | `Dictionary<int, EntityComponent>` | same | **zero** | entity add/remove (instant) |
| `UpdateSingleton` | -- | just `DrainRequests()` | **~0ms** when idle | N/A |

### Cached component refs

`CachedBuilding`, `CachedNaturalResource`, and `CachedBeaver` structs resolve all component references once at entity-add time. Endpoints read cached primitives (refreshed at 1Hz) without calling `GetComponent<T>()`.

| Struct | Fields cached | GetComponent calls saved per item |
|---|---|---|
| `CachedBuilding` | BlockObject, Pausable, Floodgate, BuilderPrio, Workplace, WorkplacePrio, Reachability, Mechanical, Status, PowerNode, Site, Inventories, Wonder, Dwelling, Clutch, Manufactory, BreedingPod, RangedEffect | **18** |
| `CachedNaturalResource` | BlockObject, Living, Cuttable, Gatherable, Growable | **5** |
| `CachedBeaver` | NeedMgr, WbTracker, Worker, Life, Carrier, Deteriorable, Contaminable, Dweller, Citizen, Bot | **10** |

## Endpoint performance (measured, 522 buildings / 2986 trees / 65 beavers / 4161 total)

### Optimized (cached struct indexes)

| Endpoint | Iterates | Items | Measured | GetComponent/item | Notes |
|---|---|---|---|---|---|
| `ping` | none | 1 | **1ms** | 0 | Listener thread |
| `summary` | all 3 read buffers | 3000+500+65 | **1.2ms** | 0 | Cached primitives only |
| `buildings` | `_buildingsRead` | 522 | **2.8ms** | **0** | Cached primitives, TOON dict |
| `buildings detail:full` | `_buildingsRead` | 522 | **1.3ms** | **0** | Cached primitives, full dict |
| `trees` | `_naturalResourcesRead` | 2985 | **2.0ms** | **0** | StringBuilder serialization, no Newtonsoft |
| `gatherables` | `_naturalResourcesRead` | ~150 | **<1ms** | **0** | Cached primitives |
| `beavers` | `_beaversRead` | 65 | **0.9ms** | **0** | CachedBeaver struct + StringBuilder |
| `alerts` | `_buildingsRead` | 522 | **1.0ms** | **0** | Cached primitives only |
| `resources` | district centers | 13 | **0.9ms** | 0 | Listener thread |
| `weather` | none | 1 | **0.8ms** | 0 | Listener thread |
| `prefabs` | building templates | 157 | **3.8ms** | 0 | Listener thread |

### New endpoints (0.5.6+)

| Endpoint | Iterates | Notes |
|---|---|---|
| `beavers` (position, district, carrying) | `_beaversRead` | x,y,z from transform, district from Citizen, GoodCarrier for carried goods |
| `beavers detail:full` (all needs + groups) | `_beaversRead` | shows all 38 needs with NeedGroupId, liftingCapacity, deterioration |
| `power` | all entities | groups buildings by MechanicalNode.Graph identity. Per-request only, no caching |
| `map` (stacking) | all entities | occupants now `List<(name, z)>` per tile instead of last-wins string |

### Still scan all entities (not cached -- optimization gaps)

| Endpoint | What it does | Frequency | Why not indexed | Could cache? |
|---|---|---|---|---|
| `BuildAllIndexes` | Initial index build | **once on load** | populates all indexes | N/A |
| ~~`CollectScan`~~ | ~~Radius-filtered survey~~ | ~~scanned all entities~~ | **FIXED** | uses `_buildingsRead` + `_naturalResourcesRead` with cached footprints |
| ~~`CollectMap`~~ | ~~Region tile occupants~~ | ~~scanned all entities~~ | **FIXED** | uses `_buildingsRead` + `_naturalResourcesRead` with cached footprints |
| ~~`CollectPowerNetworks`~~ | ~~Group by power graph~~ | ~~scanned all entities~~ | **FIXED** | now uses `_buildingsRead` with cached `PowerNetworkId` (Graph hashcode) |
| `CollectSummary` (districts) | District resource counts | every bot turn | iterates district centers live | partially cached, district loop still live |

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

## GC pressure audit

`RefreshCachedState` runs every 1s (cadenced, configurable via settings.json). Allocations here cause GC pressure proportional to refresh rate.

### Per-refresh allocations (RefreshCachedState, every 1s)

| Source | Lines | Allocs/refresh | Severity | Status |
|---|---|---|---|---|
| ~~`Priority.ToString()` x2 per building~~ | 248-249 | ~~60K strings/sec~~ | **FIXED** | static `PriorityNames[]` lookup, zero alloc |
| ~~`new Dictionary` for nutrients~~ | 277 | ~~5 dicts/frame~~ | **FIXED** | persistent dict in struct, `.Clear()` + repopulate |
| ~~Static values re-read every frame~~ | various | ~~wasted cycles~~ | **FIXED** | moved to add-time only |
| ~~60fps refresh~~ | UpdateSingleton | ~~60x/sec~~ | **FIXED** | cadenced to 1s (configurable) |
| `Orientation` from `OrientNames[]` | 226 | 0 (array lookup, no alloc) | none | already good |
| `foreach` over `BreedingPod.Nutrients` | 316 | ~5 enumerator boxes/refresh | **minor** | if `Nutrients` returns `IEnumerable<T>`, foreach boxes enumerator (~40 bytes). Only ~5 breeding pods |
| `foreach` over `Inventories.AllInventories` | 330 | ~500 enumerator boxes/refresh | **minor** | same boxing concern for all buildings with inventories. At 1Hz cadence = ~500 small allocs/sec |
| `foreach` over `inv.Stock` (nested) | 335 | ~500+ enumerator boxes/refresh | **minor** | nested inside AllInventories loop, same boxing concern |
| `GetComponent<EntityComponent>()` per beaver | 381 | 65 GetComponent calls/refresh | **medium** | should cache `GameObject` ref at add-time instead of calling GetComponent every refresh |
| `CleanName()` per employed beaver | 390 | ~50 string ops/refresh | **medium** | string Replace/Contains every refresh for workplace name. Should cache and only update on change |
| Building X,Y,Z,Orientation re-read | 260-263 | wasted reads (immutable) | **low** | coordinates and orientation don't change after placement. Move to add-time |
| `NeedMgr.GetNeeds()` per beaver | 423 | 65 calls/refresh | **unknown** | may allocate a new collection per call. ~38 needs x 65 beavers = 2470 CachedNeed structs added to Lists |

### Per-request allocations (only when API called)

| Source | Count | Severity |
|---|---|---|
| ~~`new Dictionary` per building~~ | ~~522 per call~~ | **FIXED** -- StringBuilder like trees |
| ~~`new List<object>` per endpoint~~ | ~~1 per call~~ | **FIXED** -- StringBuilder returns string |
| `$"string interpolation"` in alerts/summary | ~20 per call | negligible |
| `sb.ToString()` for trees/buildings | 1 per request | reusable `_sb` field, pre-allocated 500KB |
| ~~LINQ `.Select().ToList()` in map stacking~~ | ~~per stacked tile~~ | **FIXED** | replaced with simple loop + Dictionary |
| ~~`new Dictionary` per beaver~~ | ~~65 per call~~ | **FIXED** -- CachedBeaver + StringBuilder |
| `new Dictionary` per power network | ~17 per call | expected | response-building, per-request only |

### Static values (resolved)

All static values moved to add-time only: EffectRadius, IsGenerator, IsConsumer, NominalPower, HasFloodgate, HasClutch, HasWonder, FloodgateMaxHeight.

## Remaining bottlenecks (ordered by impact)

| # | Bottleneck | Cost | Root cause | Fix |
|---|---|---|---|---|
| 1 | **Unity GC spikes** | random 0.5-2s | Unity garbage collector freezes all threads | reduced alloc pressure, but unavoidable from mod |
| 2 | **sb.ToString() alloc** | 1 string per request (~100-500KB) | StringBuilder must create final string | unavoidable but once per request |

## Resolved bottlenecks

| Bottleneck | Was | Fix applied |
|---|---|---|
| GetComponent per item (trees) | 5 calls x 2986 items/request | cached component refs in `CachedNaturalResource` struct |
| GetComponent per item (buildings) | 18 calls x 522 items/request | cached component refs in `CachedBuilding` struct |
| Full entity scan per endpoint | O(4161) every call | event-driven typed indexes via `EventBus` |
| Per-frame index rebuild | O(4161) every frame | eliminated -- indexes update on entity add/remove only |
| Per-frame entity cache rebuild | O(4161) every frame | eliminated -- `_entityCache` updates via EventBus |
| tree_clusters full scan | O(4161) | switched to `_naturalResourceIndex` with cached refs |
| wellbeing full scan | O(4161) | switched to `_beaverIndex` |
| find_placement full scan | O(4161) | switched to `_buildingIndex` with cached refs |
| demolish_path_at full scan | O(4161) x up to 6 | switched to `_buildingIndex` with cached refs |
| All reads blocking main thread | ~7ms overhead per call | GETs served on listener thread, zero main-thread cost |
| JSON serialization on main thread | ~1-3ms/response | now serializes on listener thread for GETs |
| Thread-unsafe property reads from background | game crash (nav mesh recalc) | all mutable state cached as primitives on main thread, double-buffered |
| Trees Dictionary + Newtonsoft overhead | 23ms for 3000 trees | StringBuilder serialization: 2ms (11.5x faster) |
| Buildings Dictionary + Newtonsoft overhead | 8ms for 522 buildings | StringBuilder serialization |
| Priority.ToString() 60K allocs/sec | per-frame GC pressure | static PriorityNames[] lookup, zero alloc |
| RefreshCachedState 60x/sec | wasted CPU on main thread | cadenced to 1s (configurable via settings.json) |
| Beavers live GetComponent from background | 2.4ms + thread safety risk | CachedBeaver struct with needs list, StringBuilder serialization |
| Double-buffer copy-back race | "Collection was modified" errors | removed copy-back, add/remove updates both buffers |
| new Dictionary per breeding pod per frame | ~5 allocs/frame | persistent dict, clear+repopulate |
| Static values refreshed every frame | wasted cycles | moved to add-time only (EffectRadius, IsGenerator, etc.) |
| Pause/unpause missing UI icon | `.Paused` set directly | use `Pause()`/`Resume()` methods |
| LINQ `.Select().ToList()` in map stacking | anonymous objects + LINQ alloc per stacked tile | simple loop with Dictionary |

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
| Alloc-once + SB buildings | **2.0ms** | **~1ms** | **~1ms** | **~20ms est** |

**A/B test results (trees, 2985 items):** Dictionary 4.7ms, Anonymous objects 13.8ms (worst -- Newtonsoft reflection), StringBuilder **2.0ms** (winner). StringBuilder skips Newtonsoft entirely -- manual JSON via `sb.Append()`. Main-thread cost for reads is **zero**.

## Late-game projections

| Endpoint | Current items | Current time | Per-item rate | Late-game items | Projected time |
|---|---|---|---|---|---|
| `buildings` | 522 | 2.8ms | 5.4us/item | 1500 | ~8ms |
| `buildings detail:full` | 522 | 1.3ms | 2.5us/item | 1500 | ~4ms |
| `trees` | 2986 | 2.0ms | 0.67us/item | 5000 | ~3.5ms |
| `beavers` | 65 | 0.9ms | 13.8us/item | 250 | ~3.5ms |
| `beavers detail:full` | 65 | 2.0ms | 30.8us/item | 250 | ~8ms |
| `alerts` | 522 | 1.0ms | 1.9us/item | 1500 | ~3ms |
| `summary` | 3500 | 1.2ms | 0.34us/item | 10000 | ~3.5ms |
| `power` | 17 networks | ~5ms | not indexed | 50+ networks | ~15ms (full scan) |
| `map` (region) | varies | ~10ms | region-bounded | larger builds | ~20ms |
| Burst (7 calls) | -- | 28ms total | -- | -- | ~50ms est |

All scaling is linear. Zero main-thread cost for GET-only bot turns.

### Late-game risk factors

- **Bots scale free:** bots don't eat/drink/sleep but DO add to beaver index. 50+ bots = more entities to cache and serialize
- **Multi-district:** each district has its own resource counters. 3+ districts increases summary/resources iteration
- **Vertical builds:** heavy platform/stair stacking increases map `occupants` arrays (more allocs per tile)
- **Power networks:** complex power grids fragment into many small networks. `power` endpoint scans all buildings every call (no caching)

## Test coverage

Performance tests in `timberbot/script/test_validation.py`:

- **Latency**: 10 endpoints x 5 iterations, all must be < 500ms
- **Cache consistency**: same endpoint called twice returns same count (no stale refs)
- **Cache invalidation**: place path -> count+1, demolish -> count back (EventBus works)
- **Burst**: 7 sequential calls < 3s total
