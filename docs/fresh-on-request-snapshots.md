# Fresh-on-Request Snapshots

Implementation design for migrating Timberbot reads away from the current cadence-driven double buffer toward demand-driven published snapshots.

This document is intended to be detailed enough that the work can be implemented later without reconstructing the design from chat history.

Related background:

- [`architecture.md`](architecture.md) -- current double-buffer design
- [`thread-safe-surfaces.md`](thread-safe-surfaces.md) -- what Timberborn exposes as actually thread-safe
- [`performance.md`](performance.md) -- current benchmark baseline

## Goal

Preserve the important property of the current design:

- normal GET endpoints do not read live Timberborn objects on the listener thread

while removing the part that feels wrong:

- continuous periodic cache refresh even when nobody is reading

Target behavior:

- a normal read request asks for fresh data
- the request waits for the next main-thread refresh for that entity type
- the refreshed data is published as an immutable/read-only snapshot
- the request then responds from that snapshot off-thread
- no migrated entity type performs continuous refresh work while idle

## Core idea

Replace:

- one long-lived mirrored cache object graph that is refreshed on cadence

with:

- main-thread-only live trackers
- immutable/read-only published snapshots
- per-entity-type fresh-read coordination

This is a better fit for what Timberborn appears to do internally:

- use explicit thread-safe game surfaces where available
- use narrow thread-safe projections where needed
- keep normal live gameplay state on the main thread

## Endstate behavior

### Freshness contract

For projection-backed endpoints, a response is fresh **as of the main-thread frame that serviced the request**.

That means:

- requests do not return stale data just because the system was idle
- requests may wait up to one frame for the next publish
- multiple requests arriving before that publish share the same refresh result

This is intentionally not the old “return whatever was last published” behavior.

### Idle behavior

While there is no read demand for a migrated entity type:

- no periodic mutable-state refresh runs for that entity type

Structural tracking still remains event-driven:

- entity create/delete
- any index maintenance required to keep the tracker set valid

### Throughput behavior

The main thread performs **at most one refresh per frame per migrated entity type**.

This bounds worst-case gameplay cost under polling:

- many requests in one frame share one refresh
- the system does not refresh once per request

## Architectural split

Every migrated entity type should have four layers.

### 1. `Tracked*Ref`

Main-thread-only live data used to gather state.

Responsibilities:

- hold component refs
- resolve refs once at add time
- participate in create/delete event handling
- never leave the main thread

These are not published to readers.

### 2. `*Definition`

Static or event-driven data.

Updated:

- when the entity is created
- when structural facts change

Examples:

- ID, name
- stable coordinates
- orientation
- type flags
- cached geometry/footprints

### 3. `*State`

Flat mutable runtime state.

Updated:

- only when fresh readers are waiting

Examples:

- paused/powered/workers for buildings
- wellbeing/position/carrying for beavers
- alive/grown/marked for natural resources

### 4. `*DetailState`

Heavy nested payloads needed only for full-detail reads.

Updated:

- only when a waiting request requires that detail level

Examples:

- building inventory/recipes/nutrients
- beaver needs

### Published snapshot

Each entity type gets one published snapshot object, for example:

- `PublishedBuildingsSnapshot`
- `PublishedBeaversSnapshot`
- `PublishedNaturalResourcesSnapshot`

Properties:

- immutable/read-only after publish
- contains only DTO data
- no live Timberborn refs
- atomically swapped by reference

## First migration spike

The first spike should be:

- `GET /api/buildings_v2`

Reason:

- it is the most complex current read model
- it carries the most nested data
- if the projection split works here, the beaver and natural-resource cases should be easier

### `buildings_v2` requirements

The new endpoint should match current `GET /api/buildings` behavior for:

- `format`
- `detail`
- `limit`
- `offset`
- `name`
- `x`, `y`, `radius`

Supported detail levels:

- `basic`
- `full`
- `id:<id>`

Schema compatibility requirement:

- same schema as current `buildings` output in both `toon` and `json`

The current `GET /api/buildings` remains in place during the spike.

## Building projection design

### `TrackedBuildingRef`

Main-thread-only live refs:

- entity/component refs used to gather mutable building state
- any refs needed for event-driven structural maintenance

This is the only place where building live refs should remain once the v2 path is implemented.

### `BuildingDefinition`

Static or event-driven data:

- `Id`, `Name`
- `X`, `Y`, `Z`
- `Orientation`
- `HasFloodgate`, `FloodgateMaxHeight`
- `HasClutch`, `HasWonder`
- `IsGenerator`, `IsConsumer`
- `NominalPowerInput`, `NominalPowerOutput`
- `EffectRadius`
- `OccupiedTiles`
- `HasEntrance`, `EntranceX`, `EntranceY`

### `BuildingState`

Mutable flat snapshot data:

- `Finished`, `Paused`, `Unreachable`, `Powered`
- `District`
- `AssignedWorkers`, `DesiredWorkers`, `MaxWorkers`
- `Dwellers`, `MaxDwellers`
- `FloodgateHeight`
- `ConstructionPriority`, `WorkplacePriorityStr`
- `BuildProgress`, `MaterialProgress`, `HasMaterials`
- `ClutchEngaged`, `WonderActive`
- `PowerDemand`, `PowerSupply`, `PowerNetworkId`
- `CurrentRecipe`, `ProductionProgress`, `ReadyToProduce`
- `NeedsNutrients`
- `Stock`, `Capacity`

### `BuildingDetailState`

Nested full-detail data only:

- `Inventory`
- `Recipes`
- `NutrientStock`

### Not part of the published read model

Do not publish these off-thread:

- `Entity`
- `BlockObject`
- `Pausable`
- `Floodgate`
- `BuilderPrio`
- `Workplace`
- `WorkplacePriority`
- `Reachability`
- `Mechanical`
- `Status`
- `PowerNode`
- `Site`
- `Inventories`
- `Wonder`
- `Dwelling`
- `Clutch`
- `Manufactory`
- `BreedingPod`
- `RangedEffect`
- `DistrictBuilding`
- `LastDistrictRef`

## Fresh-read coordination

Each migrated entity type should have a small coordination object.

For buildings, responsibilities are:

- accept fresh-read requests from listener-thread GETs
- record the highest required detail level among waiting requests
- indicate to the main thread that a refresh is pending
- wake waiting requests after publish

### Request flow

For `GET /api/buildings_v2`:

1. Listener thread parses request
2. Listener thread registers a fresh-read request with the building coordinator
3. Listener thread waits
4. Main thread refreshes buildings on next frame
5. Main thread publishes new `PublishedBuildingsSnapshot`
6. Waiting requests wake
7. Listener thread filters/paginates/serializes from the published snapshot

### Coalescing rule

- one refresh per frame max
- all requests waiting during that frame share the same publish
- if any waiting request asks for `detail=full`, build detail data for that publish

### Timeout rule

If a request cannot be satisfied because the game is not advancing frames:

- wait up to 2 seconds
- respond `503 refresh_timeout`

Do not block forever.

## Main-thread frame order

For migrated fresh-read endpoints, `UpdateSingleton()` should conceptually run in this order:

1. Drain queued writes
2. Apply structural entity changes already captured by event handlers
3. Process pending fresh-read refreshes
4. Publish snapshots and release waiters
5. Flush webhooks

This order matters because:

- a fresh read after a write should see the newest state
- snapshot publish should happen after write-side mutations for that frame

## Listener-thread rules

For migrated endpoints, the listener thread may:

- read the published snapshot
- filter
- paginate
- serialize

The listener thread may not:

- call Timberborn services
- touch live component refs
- read tracker objects
- call `GetComponent<T>()`
- traverse live entity graphs

## Debug and benchmark support

The spike should include enough observability to answer whether the design is working.

Expose or record:

- latest publish timestamp or sequence for each migrated entity type
- number of refreshes performed
- number of structural republishes
- whether detail payload was built for a publish
- item counts in the current published snapshot

This should go through existing debug/benchmark surfaces rather than inventing a second diagnostics system.

## Acceptance criteria

### Functional

- `buildings_v2 basic` matches `buildings basic`
- `buildings_v2 full` matches `buildings full`
- `detail=id:<id>` behavior matches current endpoint
- pagination and filtering semantics match
- toon/json schema matches current endpoint exactly

### Freshness

- after a write or player action that changes building state, `buildings_v2` returns data refreshed on the servicing frame
- requests arriving in the same frame share one refresh

### Thread safety

- no live building data is read on the listener thread for `buildings_v2`
- published snapshots contain DTOs only

### Performance

Compared to current `buildings full`:

- no periodic idle refresh cost for buildings-v2
- no more than one building refresh per frame under active demand
- no material latency regression in the endpoint
- code complexity is reduced by removing published live refs and clone-driven buffer semantics from the read path

## Current spike results

The buildings spike has been implemented and measured against the live game.

### Functional status

- `GET /api/buildings_v2` exists and is live
- `buildings_v2` basic matches legacy `buildings`
- `buildings_v2` full matches legacy `buildings`
- sampled `detail=id:<id>` parity checks passed

Dedicated parity test:

```powershell
python timberbot/script/test_validation.py buildings_v2_parity
```

Latest passing parity result file:

- [20260327-093637-buildings_v2_parity.txt](/C:/code/timberborn/timberbot/test-results/20260327-093637-buildings_v2_parity.txt)

### Performance status

There are now two relevant perf entry points:

- broad perf suite: `performance`
- building-only benchmark: `building_endpoint_perf`

Recommended command for this spike:

```powershell
python timberbot/script/test_validation.py building_endpoint_perf -n 200
```

Latest stable 200-iteration result:

- [20260327-095537-building_endpoint_perf.txt](/C:/code/timberborn/timberbot/test-results/20260327-095537-building_endpoint_perf.txt)

| Endpoint | Avg | Min | Max | Success |
|---|---:|---:|---:|---:|
| `buildings` | 274 ms | 237 ms | 1267 ms | 200 / 200 |
| `buildings full` | 298 ms | 257 ms | 1269 ms | 200 / 200 |
| `buildings_v2` | 284 ms | 238 ms | 1291 ms | 200 / 200 |
| `buildings_v2 full` | 299 ms | 260 ms | 1248 ms | 200 / 200 |

Legacy vs v2 from that run:

| Scenario | Legacy | V2 | Delta | Ratio |
|---|---:|---:|---:|---:|
| basic | 274 ms | 284 ms | +10 ms | 1.04x |
| full | 298 ms | 299 ms | +1 ms | 1.00x |

Interpretation:

- request latency is effectively at parity
- the fresh-on-request model is not showing a meaningful regression for buildings
- the main architectural advantage is now freshness and idle-cost reduction, not raw speedup

### Earlier unstable run

One earlier 50-iteration run showed transient instability in `buildings_v2` basic:

- [20260327-094047-buildings_v2_performance.txt](/C:/code/timberborn/timberbot/test-results/20260327-094047-buildings_v2_performance.txt)

That run reported:

- `buildings_v2`: 42 / 50 successful, 635 ms avg, 3771 ms max
- `buildings_v2 full`: 50 / 50 successful, 252 ms avg

That specific failure mode has not reproduced in later isolated `building_endpoint_perf` runs:

- 50 iterations: [20260327-094915-building_endpoint_perf.txt](/C:/code/timberborn/timberbot/test-results/20260327-094915-building_endpoint_perf.txt)
- 200 iterations: [20260327-095537-building_endpoint_perf.txt](/C:/code/timberborn/timberbot/test-results/20260327-095537-building_endpoint_perf.txt)

Current conclusion:

- the spike currently looks viable
- there was at least one transient bad run, so burst/concurrency behavior still deserves investigation before declaring the architecture finished

## Follow-on order if the spike succeeds

If `buildings_v2` proves the model:

1. `beavers_v2`
2. natural resources v2
3. rebuild higher-level endpoints from the new projections:
   - `summary`
   - `alerts`
   - `power`
   - any other endpoints still leaning on the old mirrored cache

## Explicit non-goals for the spike

The first spike should not:

- remove the old building endpoint
- migrate all entity types at once
- redesign water/terrain reads that are already backed by Timberborn `ThreadSafe*` services
- attempt a single giant world snapshot

The spike is only meant to prove that fresh-on-request published snapshots are the right replacement for cadence-driven mirrored caches.
