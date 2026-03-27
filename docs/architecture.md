# Architecture

How Timberbot works after the native `ReadV2` cutover.

This document describes the current live architecture, not the historical double-buffer design. For the migration rationale and validation history, see [`fresh-on-request-snapshots.md`](fresh-on-request-snapshots.md).

## Current state

Timberbot now has one live read stack:

- canonical read surface: `/api/*`
- read implementation: [`TimberbotReadV2`](../timberbot/src/TimberbotReadV2.cs)
- write implementation: [`TimberbotWrite`](../timberbot/src/TimberbotWrite.cs)
- entity lookup / compatibility layer: [`TimberbotEntityRegistry`](../timberbot/src/TimberbotEntityRegistry.cs)

Removed:

- `TimberbotRead`
- `/api/v1/*`
- the old public split between legacy and v2 routes

Still present:

- `TimberbotEntityRegistry` still keeps some older cache/index state for debug/benchmark support code

So the important distinction is:

- **the public read API is no longer served by the old read class**
- **the registry still contains transitional support/index responsibilities**

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
  +-- FlushWebhooks() [every frame]                 +-- queue to _pending
        |
        +-- batch _pendingEvents -> ThreadPool POST
```

| Location | Thread | Blocks game? |
|---|---|---|
| HTTP listener accept/GET response | background | no |
| Canonical GET endpoints | background | no |
| POST endpoints | main thread via `DrainRequests()` | yes, for duration |
| `ReadV2.ProcessPendingRefresh()` | main thread | yes, bounded by capture budget |
| Webhook flush scheduling | main thread | negligible |

## High-level pieces

### `TimberbotService`

[`TimberbotService`](../timberbot/src/TimberbotService.cs) is the singleton orchestrator.

It owns:

- settings load
- HTTP server lifetime
- event bus registration
- `Registry.BuildAllIndexes()`
- `ReadV2.BuildAll()`
- per-frame:
  - `DrainRequests()`
  - `ReadV2.ProcessPendingRefresh(now)`
  - `WebhookMgr.FlushWebhooks(now)`

### `TimberbotReadV2`

[`TimberbotReadV2`](../timberbot/src/TimberbotReadV2.cs) is now the only read service.

It owns:

- staged fresh-on-request projection snapshots
- staged value stores for singleton/aggregate endpoints
- collection/value/paged route helpers
- native serialization for `/api/*`
- a private background finalize worker for snapshot publish work
- some direct use of explicit Timberborn thread-safe services:
  - terrain
  - water
  - soil moisture / contamination safety-wrapped reads

Important rule:

- listener-thread reads must come from published DTO snapshots or explicitly thread-safe game services
- listener-thread code must not walk live Timberborn entity/component graphs

### `TimberbotEntityRegistry`

[`TimberbotEntityRegistry`](../timberbot/src/TimberbotEntityRegistry.cs) is the compatibility and lookup layer.

It currently owns:

- entity lifecycle tracking via Timberborn events
- legacy numeric ID compatibility
- GUID-backed identity mapping over Timberborn `EntityRegistry`
- `FindEntity(...)` for writes and placement
- static/tracked support data still used by debug/benchmark paths

Internal identity model:

- canonical internal entity key: Timberborn `EntityComponent.EntityId` (`Guid`)
- public API entity key: legacy numeric `id`

That split exists because:

- Timberborn’s real registry is GUID-based
- the public API is still intentionally human-usable with short numeric IDs

### `TimberbotWrite`

[`TimberbotWrite`](../timberbot/src/TimberbotWrite.cs) handles all mutations on the main thread.

Write flow:

- HTTP listener parses request
- request is queued
- `DrainRequests()` executes on Unity thread
- write resolves numeric `id` through `TimberbotEntityRegistry`
- mutation runs against live game services/components

### `TimberbotPlacement`

[`TimberbotPlacement`](../timberbot/src/TimberbotPlacement.cs) handles:

- `find_placement`
- `place_building`
- `demolish_building`
- path routing helpers

It still uses registry/state helpers and explicit thread-safe terrain/water services where appropriate.

### `TimberbotWebhook`

[`TimberbotWebhook`](../timberbot/src/TimberbotWebhook.cs) batches event pushes and sends them out-of-band.

## Read architecture

There are three main read patterns inside `ReadV2`.

### 1. Projection-backed collections

Used for entity-style endpoints like:

- `buildings`
- `beavers`
- `trees`
- `crops`
- `gatherables`

Shape:

- main-thread tracked refs
- staged capture buffers
- published DTO snapshot
- listener-thread filtering / pagination / serialization

For buildings specifically, `ReadV2` uses a fresh-on-request `ProjectionSnapshot<BuildingDefinition, BuildingState, BuildingDetailState>`.

Properties:

- requests ask for fresh data
- main thread coalesces waiting readers
- main thread captures live state into DTO buffers under a per-frame budget
- background worker finalizes and publishes immutable snapshots
- one publish satisfies multiple readers
- responses serialize from the published snapshot off-thread

### 2. Value stores

Used for singleton-ish endpoints like:

- `settlement`
- `time`
- `weather`
- `speed`
- `workhours`
- `science`
- `distribution`

Shape:

- main-thread capture produces a typed DTO/capture payload
- background finalize turns that into the published snapshot where useful
- waiting readers block until the next publish for that store
- listener thread serializes the published result

### 3. Derived reads

Used for aggregate endpoints like:

- `summary`
- `alerts`
- `power`
- `wellbeing`
- `districts`
- `resources`
- `population`
- `tree_clusters`
- `food_clusters`

These are built from:

- published snapshots
- explicit thread-safe surfaces
- a small amount of safe direct service data where needed

## Fresh-on-request behavior

The fresh-read contract is:

- a GET may wait across one or more frames for the next publish
- the returned data is fresh as of the frame that serviced the request
- concurrent readers are intended to coalesce onto shared publishes when possible

This is different from the old always-on cache mirror:

- old model: continuously refresh a mirrored read graph
- current model: publish snapshots when readers need them

Current state:

- `ReadV2` owns the live read contract
- there is no cadence-driven read refresh in the main update loop
- `ProcessPendingRefresh()` is now a bounded capture scheduler, not a full publish loop
- expensive finalize/publish work can run on `ReadV2`'s internal background worker

## ID model

Timberbot uses two ID forms.

### Public ID

- numeric `id`
- exposed in GET payloads
- accepted by write endpoints
- easy for humans and scripts to type

Source:

- Unity-style instance handle associated with the entity’s `GameObject`

### Internal ID

- `Guid`
- Timberborn `EntityComponent.EntityId`

Used for:

- canonical internal entity identity
- bridging into Timberborn `EntityRegistry`

Compatibility mapping lives in `TimberbotEntityRegistry`:

- `int -> Guid`
- `Guid -> int`

This lets the mod align with Timberborn internally without forcing GUIDs into the public API.

## Current request flow

### GET

```
HTTP GET
  -> ListenLoop()
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
  -> ListenLoop()
  -> parse JSON body
  -> enqueue PendingRequest
  -> [next frame] DrainRequests()
  -> RouteRequest()
  -> Write/Placement/Webhook mutation
  -> Respond()
```

## Serialization

`TimberbotJw` is still the core JSON writer.

Current usage pattern:

- each live request/build path owns its own writer instance
- `Reset()` per request/build
- staged finalize paths avoid reusing main-thread writers across threads

Major live writers:

- `ReadV2` main collection/value writer
- smaller dedicated builders inside `ReadV2` for science/distribution
- `Write`
- `Placement`
- `Webhook`
- `HttpServer` for small error responses

## Spatial reads

`/api/tiles` is now served by `ReadV2`.

It uses:

- `IThreadSafeWaterMap`
- `IThreadSafeColumnTerrainMap`
- safe-wrapped soil reads
- registry-backed cached building/resource occupancy data

That means tile queries no longer depend on the removed legacy read class.

## Data freshness and staleness

Not all data in Timberbot is fresh for the same reason.

### Fresh-on-request

Examples:

- buildings collection snapshots
- value-store-backed singleton endpoints

Properties:

- request-triggered
- waits for publish
- best freshness guarantee

### Registry support data

Examples:

- GUID-to-legacy-ID compatibility maps
- webhook lifecycle hooks
- tree-cutting and goods helper accessors

Properties:

- event-driven, not cadence-driven
- used for entity lookup and compatibility only
- no published read DTO buffers live in the registry anymore

## Webhooks

Webhooks remain event-driven and batched.

Behavior:

- events accumulate on the main thread
- `FlushWebhooks()` sends batches on a configurable cadence
- dispatch happens through background work items

Settings:

- `webhooksEnabled`
- `webhookBatchMs`
- `webhookCircuitBreaker`

## Settings

`settings.json` in the mod folder:

```json
{
  "refreshIntervalSeconds": 1.0,
  "debugEndpointEnabled": false,
  "httpPort": 8085,
  "httpHost": "127.0.0.1",
  "webhooksEnabled": true,
  "webhookBatchMs": 200,
  "webhookCircuitBreaker": 30
}
```

Current meaning:

- `refreshIntervalSeconds` is retained for settings compatibility
- it no longer affects public read freshness or any live read refresh loop

## Test posture

The live harness is now [`test_v2.py`](../timberbot/script/test_v2.py), despite the old name.

It now validates the current `/api/*` surface directly.

Main modes:

- `smoke`
- `freshness`
- `write_to_read`
- `performance`
- `concurrency`
- `all`

Historical oracle data was preserved before legacy removal:

- final live `v1` parity run
- full dumped legacy fixtures under `timberbot/test-results/v1-fixtures/`

## Transitional debt still present

The architecture is much cleaner than before, but it is not fully finished.

Still transitional:

- `/api/debug` and benchmark surfaces are still evolving around the new `ReadV2` vocabulary
- some docs and historical notes still reference the removed cache architecture
- fixture/history artifacts still describe the legacy migration path because they are preserved intentionally
- capture budgeting is intentionally conservative and may still need tuning per domain

The likely endstate from here is:

- keep `TimberbotEntityRegistry` as a thin lookup/identity adapter
- keep all published read data and debug snapshot access in `ReadV2`
- continue hardening write-to-read freshness and concurrency validation

## File map

Core runtime files:

- [`TimberbotService.cs`](../timberbot/src/TimberbotService.cs)
- [`TimberbotReadV2.cs`](../timberbot/src/TimberbotReadV2.cs)
- [`TimberbotEntityRegistry.cs`](../timberbot/src/TimberbotEntityRegistry.cs)
- [`TimberbotWrite.cs`](../timberbot/src/TimberbotWrite.cs)
- [`TimberbotPlacement.cs`](../timberbot/src/TimberbotPlacement.cs)
- [`TimberbotHttpServer.cs`](../timberbot/src/TimberbotHttpServer.cs)
- [`TimberbotWebhook.cs`](../timberbot/src/TimberbotWebhook.cs)
- [`TimberbotDebug.cs`](../timberbot/src/TimberbotDebug.cs)

Related docs:

- [`fresh-on-request-snapshots.md`](fresh-on-request-snapshots.md)
- [`thread-safe-surfaces.md`](thread-safe-surfaces.md)
- [`developing.md`](developing.md)
