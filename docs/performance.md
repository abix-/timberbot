# Performance

Single source of truth for Timberbot API performance. All optimization decisions reference here.

## Entity tracking

Event-driven double-buffered indexes via Timberborn's `EventBus`. Zero per-frame allocation, zero `GetComponent` calls per request, zero main-thread cost for reads.

- **Double buffer:** main thread writes to `_*Write` lists, swaps to `_*Read`. Background thread only reads `_*Read`. Zero contention.
- **Cached structs:** `CachedBuilding` (18 component refs + ~25 cached primitives), `CachedNaturalResource` (5 refs + 7 primitives), `CachedBeaver` (10 refs + 14 primitives). Mutable state refreshed at 1Hz (configurable via settings.json).
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

## Endpoint performance (measured, 522 buildings / 2986 trees / 65 beavers / 4161 total, 100 iterations)

### Optimized (cached struct indexes, double-buffered, background thread)

| Endpoint | Items | Min (ms) | GetComponent | Notes |
|---|---|---|---|---|
| `ping` | 1 | **0.7** | 0 | Listener thread |
| `summary` | 3500+ | **0.9** | 0 | Cached primitives only |
| `buildings` | 522 | **2.1** | **0** | StringBuilder + Jw helper |
| `buildings detail:full` | 522 | **3.4** | **0** | StringBuilder + Jw, all fields |
| `trees` | 2983 | **8.6** | **0** | StringBuilder + Jw |
| `gatherables` | 1504 | **6.7** | **0** | Dictionary (low priority to convert) |
| `beavers` | 65 | **1.1** | **0** | CachedBeaver + StringBuilder + Jw |
| `alerts` | 19 | **0.8** | **0** | Cached primitives |
| `resources` | 13 | **0.8** | 0 | District registries |
| `weather` | 1 | **0.8** | 0 | Service fields |
| `time` | 1 | **0.8** | 0 | Service fields |
| `prefabs` | 157 | **2.9** | 0 | Building templates |
| `wellbeing` | 1 | **0.9** | 0 | Cached beaver needs |
| `tree_clusters` | 5 | **0.9** | 0 | Cached natural resources |
| **burst (7 calls)** | -- | **17** | -- | 2ms avg per call |

### New endpoints (0.5.6+)

| Endpoint | Iterates | Notes |
|---|---|---|
| `beavers` (position, district, carrying) | `_beaversRead` | x,y,z from transform, district from Citizen, GoodCarrier for carried goods |
| `beavers detail:full` (all needs + groups) | `_beaversRead` | shows all 38 needs with NeedGroupId, liftingCapacity, deterioration |
| `power` | `_buildingsRead` | groups by cached `PowerNetworkId`. Zero GetComponent |
| `map` (stacking) | `_buildingsRead` + `_naturalResourcesRead` | cached tile footprints. Zero GetComponent, fully thread-safe |

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
| ~~`Orientation` from `OrientNames[]`~~ | ~~226~~ | ~~0~~ | **FIXED** | moved to add-time (immutable after placement) |
| `foreach` over `BreedingPod.Nutrients` | 316 | ~5 enumerator boxes/refresh | **minor** | if `Nutrients` returns `IEnumerable<T>`, foreach boxes enumerator (~40 bytes). Only ~5 breeding pods |
| `foreach` over `Inventories.AllInventories` | 330 | ~500 enumerator boxes/refresh | **minor** | same boxing concern for all buildings with inventories. At 1Hz cadence = ~500 small allocs/sec |
| `foreach` over `inv.Stock` (nested) | 335 | ~500+ enumerator boxes/refresh | **minor** | nested inside AllInventories loop, same boxing concern |
| ~~`CleanName()` per employed beaver~~ | ~~390~~ | ~~50 string allocs/refresh~~ | **FIXED** | reference-compare Workplace + District, only recompute name on change |
| `NeedMgr.GetNeeds()` per beaver | 423 | 65 calls + 2470 List.Add/refresh | **unknown** | may allocate new collection per call. 38 CachedNeed structs x 65 beavers copied to Lists |

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
| ~~3~~ | ~~GetComponent per beaver per refresh~~ | ~~65 calls/sec~~ | ~~GetComponent to get GameObject for position~~ | **FIXED** -- cached `Go` field at add-time |
| ~~4~~ | ~~Building X,Y,Z,Orientation re-read~~ | ~~522 wasted reads/sec~~ | ~~immutable after placement~~ | **FIXED** -- moved to add-time in AddToIndexes |

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
| Alloc-once + SB buildings | 2.0ms | ~1ms | ~1ms | ~20ms est |
| DRY (Jw + DoubleBuffer) | **8.6ms** | **2.1ms** | **3.4ms** | **17ms** |

**A/B test results (trees, 2985 items):** Dictionary 4.7ms, Anonymous objects 13.8ms (worst -- Newtonsoft reflection), StringBuilder **2.0ms** (winner). StringBuilder skips Newtonsoft entirely -- manual JSON via `sb.Append()`. Main-thread cost for reads is **zero**.

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
- **Power networks:** complex power grids fragment into many small networks. `power` endpoint uses cached `PowerNetworkId` but still iterates all buildings per call

## Test coverage

Performance tests in `timberbot/script/test_validation.py`:

- **Latency**: 20 endpoints x 100 iterations each (2000 calls total). All endpoints must be under 50ms min
- **Reliability**: all 2000 responses must be valid (no errors, no corruption)
- **Cache consistency**: same endpoint called twice returns same count (no stale refs)
- **Cache invalidation**: place path -> count+1, demolish -> count back (EventBus + DoubleBuffer)
- **Burst**: 7 sequential calls < 3s total
- **Save-agnostic**: all tests use `find_placement`/`find_building` for dynamic coords
