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

- `TimberbotEntityRegistry` still keeps some older cache/index state for support code
- `RefreshCachedState()` still runs on cadence for registry-backed data used by writes, placement, debug, and some aggregate reads

So the important distinction is:

- **the public read API is no longer served by the old read class**
- **the registry still contains transitional cache/index responsibilities**

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
  |     +-- satisfy waiting fresh reads       |     +-- Respond() sends JSON
  |     +-- publish immutable snapshots       |
  |                                           +-- POST request arrives
  +-- Registry.RefreshCachedState() [1s]            |
  |     |                                           +-- queue to _pending
  |     +-- refresh registry-backed support data
  |
  +-- FlushWebhooks() [every frame]
        |
        +-- batch _pendingEvents -> ThreadPool POST
```

| Location | Thread | Blocks game? |
|---|---|---|
| HTTP listener accept/GET response | background | no |
| Canonical GET endpoints | background | no |
| POST endpoints | main thread via `DrainRequests()` | yes, for duration |
| `ReadV2.ProcessPendingRefresh()` | main thread | yes, bounded by snapshot build work |
| `Registry.RefreshCachedState()` | main thread, cadence | yes, small ongoing cost |
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
  - `Registry.RefreshCachedState()`
  - `WebhookMgr.FlushWebhooks(now)`

### `TimberbotReadV2`

[`TimberbotReadV2`](../timberbot/src/TimberbotReadV2.cs) is now the only read service.

It owns:

- fresh-on-request building snapshots
- value stores for singleton/aggregate endpoints
- collection/value/paged route helpers
- native serialization for `/api/*`
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
- registry-backed cached/indexed data still used by support paths

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
- published DTO snapshot
- listener-thread filtering / pagination / serialization

For buildings specifically, `ReadV2` uses a fresh-on-request `ProjectionSnapshot<BuildingDefinition, BuildingState, BuildingDetailState>`.

Properties:

- requests ask for fresh data
- main thread coalesces waiting readers
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

- main-thread builder produces a DTO or raw JSON snapshot
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
- registry-backed cached/indexed data
- explicit thread-safe surfaces
- a small amount of safe direct service data where needed

## Fresh-on-request behavior

The fresh-read contract is:

- a GET may wait for the next main-thread publish
- the returned data is fresh as of the frame that serviced the request
- concurrent readers are intended to coalesce onto shared publishes when possible

This is different from the old always-on cache mirror:

- old model: continuously refresh a mirrored read graph
- current model: publish snapshots when readers need them

Current compromise:

- `ReadV2` owns the live read contract
- `TimberbotEntityRegistry` still refreshes some support data on cadence

So the architecture is already post-legacy, but not yet fully “zero background refresh everywhere.”

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

- each component owns its own writer instance
- `Reset()` per request/build
- no shared listener-thread/main-thread writer instance for live API paths

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

### Cadence-refreshed support data

Examples:

- registry-backed caches/indexes still maintained by `RefreshCachedState()`

Properties:

- still refreshed on the main thread every `refreshIntervalSeconds`
- used by support paths and some remaining derived computations
- transitional, not the desired final endstate

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

- `refreshIntervalSeconds` still affects `Registry.RefreshCachedState()`
- it is no longer the main public read freshness mechanism

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

- `TimberbotEntityRegistry` still carries older cache/index responsibilities
- `RefreshCachedState()` still runs every cadence
- some aggregate reads still depend on registry-backed cached data rather than purely published snapshots

The likely endstate from here is:

- shrink `TimberbotEntityRegistry` into a thinner lookup/identity adapter
- move more remaining read-shaping data fully into `ReadV2`
- further reduce or remove cadence refresh where it no longer serves writes/tooling

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
