# Architecture

How Timberbot works internally. For migration history, see [`fresh-on-request-snapshots.md`](fresh-on-request-snapshots.md).

## Components

The mod has one read stack and one write stack:

- read: [`TimberbotReadV2`](../timberbot/src/TimberbotReadV2.cs) -- all GET endpoints, projection snapshots
- write: [`TimberbotWrite`](../timberbot/src/TimberbotWrite.cs) -- all POST mutations
- entity lookup: [`TimberbotEntityRegistry`](../timberbot/src/TimberbotEntityRegistry.cs) -- GUID/numeric ID bridge
- placement: [`TimberbotPlacement`](../timberbot/src/TimberbotPlacement.cs) -- building placement, A* path routing
- HTTP: [`TimberbotHttpServer`](../timberbot/src/TimberbotHttpServer.cs) -- background listener, routing
- webhooks: [`TimberbotWebhook`](../timberbot/src/TimberbotWebhook.cs) -- batched push notifications
- debug: [`TimberbotDebug`](../timberbot/src/TimberbotDebug.cs) -- reflection inspector, benchmark
- orchestrator: [`TimberbotService`](../timberbot/src/TimberbotService.cs) -- lifecycle, settings, per-frame dispatch
- write jobs: [`ITimberbotWriteJob`](../timberbot/src/ITimberbotWriteJob.cs) -- budgeted write execution

## Thread model

```
MAIN THREAD (Unity)                         BACKGROUND THREAD (HttpListener)
========================                    ================================
UpdateSingleton() [every frame]             ListenLoop() [blocking accept]
  |                                           |
  +-- DrainRequests() [POST only]             +-- GET request arrives
  |     |                                     |     |
  |     +-- RouteRequest() mutates game       |     +-- RouteRequest()
  |     +-- Respond() sends JSON              |     +-- ReadV2 serves from:
  |                                           |           - published snapshots, or
  +-- ReadV2.ProcessPendingRefresh()          |           - explicit thread-safe services
  |     |                                     |     +-- TimberbotJw serialization
  |     +-- advance main-thread capture       |     +-- Respond() sends JSON
  |     +-- queue background finalize/publish |
  |                                           +-- POST request arrives
  +-- ProcessWriteJobs() [budgeted]                 +-- queue to _pending
  |     |
  |     +-- step pending write jobs (2ms budget)
  |
  +-- FlushWebhooks() [every frame]
        |
        +-- batch _pendingEvents -> ThreadPool POST
```

| Location | Thread | Blocks game? |
|---|---|---|
| HTTP listener accept/GET response | background | no |
| GET endpoints | background | no |
| POST endpoints | main thread via `DrainRequests()` | yes, for duration |
| `ReadV2.ProcessPendingRefresh()` | main thread | yes, bounded by capture budget |
| `ProcessWriteJobs()` | main thread | yes, bounded by `writeBudgetMs` (default 2ms) |
| Webhook flush scheduling | main thread | negligible |

## TimberbotService

[`TimberbotService`](../timberbot/src/TimberbotService.cs) is the singleton orchestrator.

It owns:

- settings load from `settings.json`
- HTTP server lifetime
- event bus registration
- `Registry.BuildAllIndexes()`
- `ReadV2.BuildAll()`
- per-frame dispatch:
  - `DrainRequests()`
  - `ReadV2.ProcessPendingRefresh(now)`
  - `_server.ProcessWriteJobs(now, writeBudgetMs)`
  - `WebhookMgr.FlushWebhooks(now)`

## TimberbotReadV2

[`TimberbotReadV2`](../timberbot/src/TimberbotReadV2.cs) is the read service for all GET endpoints.

It owns:

- fresh-on-request projection snapshots for entity collections
- value stores for singleton/aggregate endpoints
- collection/value/paged route helpers
- native serialization via `TimberbotJw`
- a private background finalize thread for snapshot publish work
- direct use of explicit Timberborn thread-safe services (terrain, water, soil)
- field-level reusable collections for derived endpoints (clusters, tiles, alerts, power, wellbeing)

Thread safety rule:

- listener-thread reads must come from published DTO snapshots or explicitly thread-safe game services
- listener-thread code must not walk live Timberborn entity/component graphs

## TimberbotEntityRegistry

[`TimberbotEntityRegistry`](../timberbot/src/TimberbotEntityRegistry.cs) is the entity lookup and ID translation layer.

It owns:

- entity lifecycle tracking via Timberborn `EventBus`
- GUID-backed identity mapping over Timberborn `EntityRegistry`
- `FindEntity(...)` for writes and placement
- shared constants (faction suffix, species lists, priority names)

Identity model:

- canonical internal key: Timberborn `EntityComponent.EntityId` (`Guid`)
- public API key: numeric `id` (Unity `GameObject.GetInstanceID()`)
- mapping: `int <-> Guid` in both directions

The public API uses short numeric IDs for human usability. The registry translates to GUIDs internally.

## TimberbotWrite

[`TimberbotWrite`](../timberbot/src/TimberbotWrite.cs) handles all mutations on the main thread.

Write flow:

- HTTP listener parses request body (background thread)
- request queued to `ConcurrentQueue`
- `DrainRequests()` dequeues on Unity main thread
- write resolves numeric `id` through `TimberbotEntityRegistry`
- mutation runs against live game services/components

## TimberbotPlacement

[`TimberbotPlacement`](../timberbot/src/TimberbotPlacement.cs) handles:

- `find_placement` -- search region for valid building spots with reachability/power/flood scoring
- `place_building` -- origin-correct, validate via `PreviewFactory`, place via `BlockObjectPlacerService`
- `demolish_building` / `demolish_crop`
- `route_path` -- A* pathfinding with auto-stairs across z-levels, budgeted execution via `RoutePathJob`
- `collect_prefabs` -- list building templates

## TimberbotWebhook

[`TimberbotWebhook`](../timberbot/src/TimberbotWebhook.cs) batches event pushes and sends them out-of-band.

- events accumulate on the main thread via `[OnEvent]` handlers
- `FlushWebhooks()` sends batches on a configurable cadence (default 200ms)
- dispatch via `ThreadPool` (non-blocking)
- circuit breaker: N consecutive failures disables the webhook

Settings: `webhooksEnabled`, `webhookBatchMs`, `webhookCircuitBreaker`.

## Read architecture

There are three read patterns inside `ReadV2`.

### 1. Projection-backed collections

Used for entity-style endpoints: `buildings`, `beavers`, `trees`, `crops`, `gatherables`.

Shape:

- main-thread tracked refs (added/removed via `EventBus` lifecycle events)
- `ProjectionSnapshot<TDef, TState, TDetail>` with double-buffered capture arrays
- main thread captures live state into DTO buffers under a per-frame budget (~1ms)
- background finalize thread publishes immutable snapshots
- `CollectionRoute` handles format/pagination/filtering/serialization from published data
- concurrent readers coalesce onto shared publishes

### 2. Value stores

Used for singleton endpoints: `settlement`, `time`, `weather`, `speed`, `workhours`, `science`, `distribution`.

Shape:

- `ValueStore<TCapture, TSnapshot>` with capture/finalize/publish pipeline
- main-thread capture produces a typed DTO
- background finalize converts to published snapshot where useful
- `ValueRoute` handles serialization from published data

### 3. Derived reads

Used for aggregate endpoints: `summary`, `alerts`, `power`, `wellbeing`, `districts`, `resources`, `population`, `tree_clusters`, `food_clusters`.

Built from published snapshots and explicit thread-safe surfaces. Use field-level reusable collections (dicts, lists, arrays) that are cleared-in-place per request for zero steady-state allocation.

## Fresh-on-request behavior

The read contract:

- a GET may wait across one or more frames for the next publish
- the returned data is fresh as of the frame that serviced the request
- concurrent readers coalesce onto shared publishes
- there is no cadence-driven refresh -- snapshots publish only when readers need them
- `ProcessPendingRefresh()` is a bounded capture scheduler, not a periodic loop
- expensive finalize/publish work runs on `ReadV2`'s dedicated background thread

## ID model

### Public ID

- numeric `id` (Unity `GameObject.GetInstanceID()`)
- exposed in GET payloads, accepted by write endpoints
- easy for humans and scripts to type

### Internal ID

- `Guid` (Timberborn `EntityComponent.EntityId`)
- used for canonical identity and bridging into Timberborn `EntityRegistry`

Compatibility mapping lives in `TimberbotEntityRegistry`: `int <-> Guid`.

## Request flow

### GET

```
HTTP GET
  -> ListenLoop() [background thread]
  -> RouteRequest()
  -> ReadV2 endpoint method
     -> request fresh snapshot/value if needed
     -> wait for publish if needed
     -> filter/paginate/serialize from published data
  -> Respond()
```

### POST

```
HTTP POST
  -> ListenLoop() [background thread]
  -> parse JSON body
  -> enqueue PendingRequest
  -> [next frame] DrainRequests() [main thread]
  -> RouteRequest()
  -> Write/Placement/Webhook mutation
  -> Respond()
```

## Serialization

`TimberbotJw` is the core JSON writer. Zero-alloc fluent API that writes directly to a reusable `StringBuilder`.

Usage pattern:

- each request/build path owns its own writer instance
- `Reset()` per request
- staged finalize paths avoid reusing main-thread writers across threads

Major writers: `ReadV2` (main + science/distribution builders), `Write`, `Placement`, `Webhook`, `HttpServer` (error responses).

## Spatial reads

`/api/tiles` reads from:

- `IThreadSafeWaterMap` -- water depth and contamination
- `IThreadSafeColumnTerrainMap` -- terrain height
- safe-wrapped `ISoilContaminationService` / `ISoilMoistureService`
- published building/resource snapshots for occupant data
- field-level reusable occupant lists (cleared-in-place per request)

## Data freshness

### Fresh-on-request

Projection snapshots and value stores. Request-triggered, waits for publish, best freshness guarantee.

### Event-driven

Registry data (GUID-to-ID maps, webhook lifecycle hooks). Updated on `EntityInitializedEvent`/`EntityDeletedEvent`. Used for entity lookup and compatibility only.

## Settings

`settings.json` in the mod folder:

```json
{
  "debugEndpointEnabled": true,
  "httpPort": 8085,
  "webhooksEnabled": true,
  "webhookBatchMs": 200,
  "webhookCircuitBreaker": 30,
  "writeBudgetMs": 2.0
}
```

## Test posture

Primary harness: [`test_v2.py`](../timberbot/script/test_v2.py). Validates the `/api/*` surface against a running game.

Modes: `smoke`, `freshness`, `write_to_read`, `performance`, `concurrency`, `all`.

## Known debt

- `/api/debug` and benchmark surfaces are evolving
- capture budgeting is intentionally conservative and may need tuning per domain
- `BuildAlertsFromBuildings` and `BuildPowerFromBuildings` still allocate `.ToArray()` per call (1 array each)

## Related docs

- [`fresh-on-request-snapshots.md`](fresh-on-request-snapshots.md) -- migration rationale and validation history
- [`thread-safe-surfaces.md`](thread-safe-surfaces.md) -- Timberborn thread-safety guidance
- [`developing.md`](developing.md) -- build, test, file structure
- [`performance.md`](performance.md) -- allocation audit, benchmarks, open issues
