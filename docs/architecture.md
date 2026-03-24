# Architecture

How Timberbot's HTTP API works under the hood.

## Thread model

```
MAIN THREAD (Unity)                    BACKGROUND THREAD (HttpListener)
========================               ================================
UpdateSingleton() [60fps]              ListenLoop() [blocking accept]
  |                                      |
  +-- RefreshCachedState() [1s cadence]  +-- GET request arrives
  |     |                                |     |
  |     +-- update _*Write buffers       |     +-- RouteRequest() reads _*Read
  |     +-- swap refs (atomic)           |     +-- StringBuilder serialization
  |     |   _*Read <-> _*Write           |     +-- Respond() sends JSON
  |     |                                |
  +-- DrainRequests() [POST only]        +-- POST request arrives
        |                                      |
        +-- RouteRequest() mutates game        +-- queue to _pending
        +-- Respond() sends JSON               |
                                               (main thread processes next frame)
```

## Double buffer

Two pre-allocated lists per entity type. Main thread writes to one, background reads the other. Ref swap publishes updates.

```
Frame N:   main writes _buildingsWrite    |  background reads _buildingsRead
           main writes _naturalResWrite   |  background reads _naturalResRead
           [swap refs]
Frame N+1: main writes old _buildingsRead |  background reads freshly updated buffer
```

**Rules:**
- `AddToIndexes` / `RemoveFromIndexes`: update BOTH buffers (always same entities)
- `RefreshCachedState`: updates write buffer only, then swaps refs
- No copy-back. Old read buffer (now write) has same entities, 1-cadence-stale values
- Background thread never modifies any buffer. Zero contention.

## Entity lifecycle

```
Entity created (building placed, beaver born, tree spawned)
  -> EntityInitializedEvent (EventBus)
  -> OnEntityInitialized()
  -> AddToIndexes(): resolve component refs, add to both read+write buffers

Entity destroyed (building demolished, beaver died, tree cut)
  -> EntityDeletedEvent (EventBus)
  -> OnEntityDeleted()
  -> RemoveFromIndexes(): remove from both buffers + entity cache
```

## Cached structs

Component references resolved once at entity-add time. Mutable state refreshed on main thread at cadence.

```
CachedBuilding {
  // immutable (set at add-time)
  Entity, Id, Name, BlockObject, Pausable, Floodgate, Workplace, ...
  HasFloodgate, HasClutch, HasWonder, IsGenerator, IsConsumer, ...

  // mutable (refreshed by RefreshCachedState)
  Finished, Paused, Unreachable, Powered, X, Y, Z, Orientation,
  AssignedWorkers, DesiredWorkers, FloodgateHeight, BuildProgress, ...
}

CachedNaturalResource {
  // immutable
  Id, Name, BlockObject, Living, Cuttable, Gatherable, Growable

  // mutable
  X, Y, Z, Alive, Grown, Growth, Marked
}
```

## Serialization

High-volume endpoints (buildings, trees) use `StringBuilder` for manual JSON -- no Dictionary allocation, no Newtonsoft overhead. Pre-allocated `_sbBuildings` and `_sbTrees` fields, `.Clear()` per request.

Other endpoints use `Dictionary<string, object>` + `JsonConvert.SerializeObject` (acceptable for low-volume data).

Pre-serialized strings detected in `Respond()`: `data is string s ? s : JsonConvert.SerializeObject(data)`.

## Settings

`settings.json` in mod folder (`Documents/Timberborn/Mods/Timberbot/`):

```json
{
  "refreshIntervalSeconds": 1.0,
  "debugEndpointEnabled": false,
  "httpPort": 8085
}
```

- `refreshIntervalSeconds`: how often mutable state is snapshotted (default 1s)
- `debugEndpointEnabled`: enable `/api/debug` reflection endpoint (default off)
- `httpPort`: HTTP server port (default 8085)

Loaded once on game load. Missing file or fields use defaults.

## Request flow

### GET (background thread, zero main-thread cost)

```
HTTP request -> ListenLoop -> RouteRequest -> read _*Read buffers
  -> StringBuilder or Dict serialization -> Respond -> HTTP response
```

### POST (main thread via queue)

```
HTTP request -> ListenLoop -> parse body -> enqueue PendingRequest
  -> [next frame] DrainRequests -> RouteRequest -> mutate game state
  -> Respond -> HTTP response
```

## Data staleness

Mutable values (paused, workers, wellbeing) are up to `refreshIntervalSeconds` stale. Entity presence (which buildings/trees exist) is always current via EventBus. For a bot polling once per minute, 1s staleness is imperceptible.
