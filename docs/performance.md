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
| `_beaverIndex` | `List<EntityComponent>` | same | **zero** | entity add/remove (instant) |
| `_entityCache` | `Dictionary<int, EntityComponent>` | same | **zero** | entity add/remove (instant) |
| `UpdateSingleton` | -- | just `DrainRequests()` | **~0ms** when idle | N/A |

### Cached component refs

`CachedBuilding` and `CachedNaturalResource` structs resolve all component references once at entity-add time. Endpoints read live property values (`.Paused`, `.IsGrown`, `.Wellbeing`) from cached refs without calling `GetComponent<T>()`.

| Struct | Fields cached | GetComponent calls saved per item |
|---|---|---|
| `CachedBuilding` | BlockObject, Pausable, Floodgate, BuilderPrio, Workplace, WorkplacePrio, Reachability, Mechanical, Status, PowerNode, Site, Inventories, Wonder, Dwelling, Clutch, Manufactory, BreedingPod, RangedEffect | **18** |
| `CachedNaturalResource` | BlockObject, Living, Cuttable, Gatherable, Growable | **5** |
| `_beaverIndex` | (not cached -- only 65 items, not worth it) | 0 |

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
| `beavers` | `_beaversRead` | 65 | **2.4ms** | 5-8 | NeedManager still live-read (only 65 items) |
| `alerts` | `_buildingsRead` | 522 | **1.0ms** | **0** | Cached primitives only |
| `resources` | district centers | 13 | **0.9ms** | 0 | Listener thread |
| `weather` | none | 1 | **0.8ms** | 0 | Listener thread |
| `prefabs` | building templates | 157 | **3.8ms** | 0 | Listener thread |

### Still scan all entities (by design)

| Endpoint | What it does | Frequency | Why not indexed |
|---|---|---|---|
| `BuildAllIndexes` | Initial index build | **once on load** | populates all indexes |
| `CollectScan` | Radius-filtered survey | rare | needs all entity types in region |
| `CollectMap` | Region tile occupants | rare | needs all entity types in region |

## Thread model

| Location | Thread | Blocks game? |
|---|---|---|
| HTTP listener (accept + queue) | background | no |
| All GET requests (reads) | background (listener thread) | **no** |
| All POST requests (writes) | main thread via `DrainRequests` | yes, for duration |
| JSON serialization (`Respond`) | same thread as request | no for GETs |
| `RefreshCachedState` (snapshot mutable values) | main thread, every frame | <1ms for 3500 entities |
| Double buffer swap | main thread, every frame | ~0ms (ref swap + value copy) |

All reads served on the listener thread from double-buffered read lists. Zero main-thread cost for GET-only bot turns. Writes (POST) still queue to main thread. Thread-unsafe properties (reachability, powered) cached as primitives on main thread -- background thread never calls Unity component properties directly.

## GC pressure audit

`RefreshCachedState` runs every frame (60fps). Allocations here directly cause GC spikes.

### Per-frame allocations (RefreshCachedState)

| Source | Lines | Allocs/frame | Severity | Fix |
|---|---|---|---|---|
| `Priority.ToString()` x2 per building | 248-249 | ~1000 strings (500 buildings x 2) | **HIGH** -- 60K strings/sec | static `PriorityNames[]` lookup |
| `new Dictionary<string, int>()` for nutrients | 277 | ~5 dicts (breeding pods only) | low | reuse persistent dict in struct, clear+repopulate |
| `CurrentRecipe` string from Manufactory | 268 | ~10 strings | low | cache at add-time, refresh only when changed |
| `Orientation` from `OrientNames[]` | 226 | 0 (array lookup, no alloc) | none | already good |

### Per-request allocations (only when API called, ~1/minute)

| Source | Count | Severity |
|---|---|---|
| `new Dictionary<string, object>` per building | 522 per call | medium -- but only on request |
| `new List<object>` per endpoint | 1 per call | negligible |
| `$"string interpolation"` in alerts/summary | ~20 per call | negligible |
| Trees `sb.ToString()` | 1 x ~320KB | medium but once per request |

### Static values refreshed needlessly every frame

These don't change between frames but are re-read in `RefreshCachedState`:

| Value | Changes when | Should refresh |
|---|---|---|
| `EffectRadius` | never | add-time only |
| `IsGenerator`, `IsConsumer` | never | add-time only |
| `NominalPowerInput`, `NominalPowerOutput` | never | add-time only |
| `HasFloodgate`, `HasClutch`, `HasWonder` | never | add-time only |
| `FloodgateMaxHeight` | never | add-time only |

## Remaining bottlenecks (ordered by impact)

| # | Bottleneck | Cost | Root cause | Fix |
|---|---|---|---|---|
| 1 | **Priority.ToString() per frame** | 60K string allocs/sec | enum ToString allocates in .NET | static string[] lookup |
| 2 | **Unity GC spikes** | random 0.5-2s | Unity garbage collector freezes all threads | reduce alloc pressure (above) |
| 3 | **beavers live reads** | 2.4ms / 65 items | NeedManager iteration still uses GetComponent | cache beaver needs in struct (low priority, only 65 items) |

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
| Pause/unpause missing UI icon | `.Paused` set directly | use `Pause()`/`Resume()` methods |

## Optimization history

| Change | trees | buildings | buildings full | burst (7 calls) |
|---|---|---|---|---|
| Baseline (full entity scan) | ~50ms est | ~20ms est | ~30ms est | ~150ms est |
| Typed entity indexes | 29ms | 9ms | 10ms | 67ms |
| Event-driven (EventBus) | 28ms | 9ms | 13ms | 62ms |
| Cached component refs | 25ms | 8ms | 13ms | 64ms |
| GETs on listener thread | 23ms | 6.5ms | 8ms | 39ms |
| Double buffer + cached primitives | 4.7ms | 2.8ms | 1.3ms | 28ms |
| StringBuilder (trees) | **2.0ms** | **2.8ms** | **1.3ms** | **28ms** |

**A/B test results (trees, 2985 items):** Dictionary 4.7ms, Anonymous objects 13.8ms (worst -- Newtonsoft reflection), StringBuilder **2.0ms** (winner). StringBuilder skips Newtonsoft entirely -- manual JSON via `sb.Append()`. Main-thread cost for reads is **zero**.

## Late-game projections

| Metric | Current | Late-game (est) | Scaling |
|---|---|---|---|
| Buildings | 522 | 1500+ | linear with item count (dict alloc) |
| Trees | 2986 | 5000+ | linear -- ~3.5ms at 5000 (StringBuilder scales well) |
| Beavers | 65 | 200+ | linear but low base count |
| Total entities | 4161 | 10000+ | only affects CollectScan/CollectMap (rare, region-bounded) |
| Burst (7 calls) | 28ms | ~50ms est | zero main-thread cost |

## Test coverage

Performance tests in `timberbot/script/test_validation.py`:

- **Latency**: 10 endpoints x 5 iterations, all must be < 500ms
- **Cache consistency**: same endpoint called twice returns same count (no stale refs)
- **Cache invalidation**: place path -> count+1, demolish -> count back (EventBus works)
- **Burst**: 7 sequential calls < 3s total
