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
  |     +-- swap refs (atomic)           |     +-- TimberbotJw serialization
  |     |   _*Read <-> _*Write           |     +-- Respond() sends JSON
  |     |                                |
  +-- DrainRequests() [POST only]        +-- POST request arrives
        |                                      |
        +-- RouteRequest() mutates game        +-- queue to _pending
        +-- Respond() sends JSON               |
                                               (main thread processes next frame)
```

## Double buffer

`DoubleBuffer<T>` generic class manages two pre-allocated lists per entity type. Main thread writes to `.Write`, background reads from `.Read`. Ref swap publishes updates.

```
Cadence N:   main refreshes _buildings.Write  |  background reads _buildings.Read
             [_buildings.Swap()]
Cadence N+1: main refreshes old .Read         |  background reads freshly updated buffer
```

**Rules:**
- `DoubleBuffer.Add(writeItem, readItem)`: add to both buffers with separate reference-type instances
- `DoubleBuffer.Add(item)`: safe for value-only structs (no reference fields)
- `DoubleBuffer.RemoveAll()`: removes from both buffers
- `RefreshCachedState`: updates `.Write` only, then `.Swap()`
- No copy-back. Old read buffer (now write) has same entities, 1-cadence-stale values
- Background thread never modifies any buffer. Zero contention.

**Reference-type fields** (`List<T>`, `Dictionary<K,V>`) must be separate instances per buffer. Shared references cause mutation-during-read corruption. Use `Add(writeItem, readItem)` with distinct instances. Immutable-after-add fields (e.g. `OccupiedTiles`) are safe to share.

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

## Cached classes

Component references resolved once at entity-add time. Mutable state refreshed on main thread at 1Hz cadence. All classes (not structs) -- modified in-place, `Clone()` via `MemberwiseClone` for double-buffer independence.

```
CachedBuilding {
  // immutable refs (set at add-time, never refreshed)
  Entity, Id, Name, BlockObject, Pausable, Floodgate, Workplace, ...
  HasFloodgate, HasClutch, HasWonder, IsGenerator, IsConsumer, ...
  EffectRadius, NominalPower, X, Y, Z, Orientation
  OccupiedTiles (immutable List, safe to share between buffers)

  // mutable primitives (refreshed by RefreshCachedState at 1Hz)
  Finished, Paused, Unreachable, Powered,
  AssignedWorkers, DesiredWorkers, FloodgateHeight, BuildProgress, ...

  // mutable reference types (SEPARATE instances per buffer!)
  Recipes (List<string>), Inventory (Dict), NutrientStock (Dict)
}

CachedNaturalResource {
  // immutable refs
  Id, Name, BlockObject, Living, Cuttable, Gatherable, Growable, X, Y, Z
  // mutable primitives (all value types -- safe to share)
  Alive, Grown, Growth, Marked
}

CachedBeaver {
  // immutable refs
  Id, Name, IsBot, NeedMgr, WbTracker, Worker, Life, Carrier, ...
  // mutable primitives
  Wellbeing, X, Y, Z, Workplace, District, HasHome, ...
  // mutable reference type (SEPARATE instance per buffer!)
  Needs (List<CachedNeed>)
}

CachedDistrict {
  Name, Adults, Children, Bots
  Resources (Dict<string, int>) -- goodId -> availableStock
  // not double-buffered (tiny list, 1-3 items, refreshed in place)
}
```

## Serialization

All endpoints use a single shared `TimberbotJw` instance -- fluent zero-alloc JSON writer with depth-aware auto-separator handling. Allocated once (300KB), `Reset()` per request. Serial on the listener thread, never concurrent.

```csharp
// single shared instance
private readonly TimberbotJw _jw = new TimberbotJw(300000);

// usage -- auto-commas, nesting-aware, fluent chaining
var jw = _jw.Reset().OpenArr();
foreach (var c in _buildings.Read)
{
    jw.OpenObj()
        .Key("id").Int(c.Id)
        .Key("name").Str(c.Name)
        .Key("finished").Bool(c.Finished)
        .CloseObj();
}
jw.CloseArr();
return jw.ToString();
```

Pre-serialized strings detected in `Respond()`: `data is string s ? s : JsonConvert.SerializeObject(data)`.

## Webhooks

68 event handlers registered on Timberborn's `EventBus`. Events accumulate in `_pendingEvents` list on the main thread. `FlushWebhooks()` runs every `webhookBatchMs` (default 200ms) from `UpdateSingleton`, sending ONE batched JSON array POST per webhook via `ThreadPool.QueueUserWorkItem`.

- **Batching:** Configurable via `webhookBatchMs` in settings.json (0 = immediate, default 200ms)
- **Circuit breaker:** N consecutive failures (default 30, configurable) disables the webhook, logged via `TimberbotLog`
- **Zero allocations with no subscribers:** `PushEvent()` early-exits if `_webhooks.Count == 0`

## Settings

`settings.json` in mod folder (`Documents/Timberborn/Mods/Timberbot/`):

```json
{
  "refreshIntervalSeconds": 1.0,
  "debugEndpointEnabled": false,
  "httpPort": 8085,
  "webhooksEnabled": true,
  "webhookBatchMs": 200
}
```

- `webhookBatchMs`: batching window in milliseconds (default 200, 0 = immediate dispatch)

Loaded once on game load. Missing file or fields use defaults.

## Pagination & Filtering

List endpoints support server-side pagination and filtering via query params:

- **Pagination:** `?limit=100` (default), `?offset=0`. `limit=0` = unlimited (flat array).
- **Filtering:** `?name=Farm` (substring), `?x=120&y=140&radius=20` (proximity).
- Filters apply BEFORE pagination. `total` reflects filtered count.
- `PassesFilter()` helper in TimberbotRead keeps filtering DRY across all list endpoints.
- Paginated response: `{total, offset, limit, items:[...]}`. Unlimited: flat `[...]`.

## Faction Detection

`FactionService.Current.Id` (from `Timberborn.GameFactionSystem`) detects the active faction
at startup. The suffix (e.g. `.IronTeeth`, `.Folktails`) is cached in `TimberbotEntityCache.FactionSuffix`
and used by `CleanName()` (strip faction from entity names) and `RoutePath()` (correct stairs/platform prefabs).

## Request flow

### GET (background thread, zero main-thread cost)

```
HTTP request -> ListenLoop -> parse query params (format, detail, limit, offset, name, x, y, radius)
  -> RouteRequest -> read _*Read buffers -> PassesFilter -> pagination
  -> TimberbotJw serialization -> Respond -> HTTP response
```

### POST (main thread via queue)

```
HTTP request -> ListenLoop -> parse body + query params -> enqueue PendingRequest
  -> [next frame] DrainRequests -> RouteRequest -> mutate game state
  -> Respond -> HTTP response
```

## Data staleness

Mutable values (paused, workers, wellbeing) are up to `refreshIntervalSeconds` stale. Entity presence (which buildings/trees exist) is always current via EventBus. For a bot polling once per minute, 1s staleness is imperceptible.

## File structure

```
TimberbotService.cs           -- Lifecycle, settings, orchestration (7 DI params)
TimberbotEntityCache.cs       -- Double-buffered entity caching, cached classes, indexes (3 DI params)
TimberbotRead.cs              -- All GET read endpoints (10 DI params)
TimberbotWrite.cs             -- All POST write endpoints (20 DI params)
TimberbotPlacement.cs         -- Building placement, path routing, terrain (13 DI params)
TimberbotWebhook.cs           -- Batched push event notifications, circuit breaker (5 DI params)
TimberbotDebug.cs             -- Reflection inspector and benchmark (1 DI param)
TimberbotHttpServer.cs        -- HttpListener, routing, request/response handling
TimberbotJw.cs                -- Fluent zero-alloc JSON writer
TimberbotDoubleBuffer.cs      -- Generic double-buffer with Add/RemoveAll/Swap
TimberbotLog.cs               -- File-based error logging, timestamped, thread-safe
TimberbotConfigurator.cs      -- Bindito DI module registration
```
