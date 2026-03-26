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
- `DoubleBuffer.Add(writeItem, readItem)`: queued via `ConcurrentQueue`, applied at `Swap()` time
- `DoubleBuffer.Add(item)`: same deferral, safe for value-only types
- `DoubleBuffer.RemoveAll()`: queued, applied at `Swap()` time
- `RefreshCachedState`: updates `.Write` only, then `.Swap()` (which applies pending adds/removes first)
- No copy-back. Old read buffer (now write) has same entities, 1-cadence-stale values
- Structural changes (entity create/delete) have up to 1-cadence delay, same staleness as mutable fields
- Neither thread modifies `.Read` during iteration. Background foreach is always safe.

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

**Booleans are stored as `int` (0/1), not `bool`.** This is intentional -- the data layer stores 0/1 natively so both toon and JSON output emit integers without format translation. Game API bools are converted at assignment time with `? 1 : 0`. When reading these fields in C#, use `!= 0` / `== 0` instead of truthy/falsy.

```
CachedBuilding {
  // immutable refs (set at add-time, never refreshed)
  Entity, Id, Name, BlockObject, Pausable, Floodgate, Workplace, ...
  HasFloodgate(int), HasClutch(int), HasWonder(int), IsGenerator(int), IsConsumer(int), ...
  EffectRadius, NominalPower, X, Y, Z, Orientation
  OccupiedTiles (immutable List, safe to share between buffers)

  // mutable primitives (refreshed by RefreshCachedState at 1Hz)
  Finished(int), Paused(int), Unreachable(int), Powered(int),
  AssignedWorkers, DesiredWorkers, FloodgateHeight, BuildProgress, ...

  // mutable reference types (SEPARATE instances per buffer!)
  Recipes (List<string>), Inventory (Dict), NutrientStock (Dict)
}

CachedNaturalResource {
  // immutable refs
  Id, Name, BlockObject, Living, Cuttable, Gatherable, Growable, X, Y, Z
  // mutable primitives (all value types -- safe to share)
  Alive(int), Grown(int), Growth(float), Marked(int)
}

CachedBeaver {
  // immutable refs
  Id, Name, IsBot(int), NeedMgr, WbTracker, Worker, Life, Carrier, ...
  // mutable primitives
  Wellbeing, X, Y, Z, Workplace, District, HasHome(int), ...
  // mutable reference type (SEPARATE instance per buffer!)
  Needs (List<CachedNeed>)  -- CachedNeed.Favorable/Critical/Active are int 0/1
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

## Data formats

All endpoints accept a `format` parameter: `toon` (default) or `json`.

- **toon**: compact token-efficient output for LLM/AI consumption. The `toons` library auto-detects uniform arrays and renders them as CSV tables with a header row.
- **json**: full nested objects for programmatic access. Arrays of objects with named keys.

### CRITICAL: uniform schema rule

**Every object in an array MUST have identical keys in BOTH formats at ALL detail levels.** No conditional/optional fields. Missing components get defaults: `""` for strings, `0` for numbers, `false` for bools. JSON collections get empty `{}` or `[]` when absent.

Why: the `toons` library detects uniform arrays -> compact CSV tables. Non-uniform schemas (different objects having different keys) break detection and fall back to verbose YAML-like output (10-17x larger). Non-uniform schemas also make programmatic parsing fragile.

**When adding new fields to any list endpoint: add them to EVERY object in the array with appropriate defaults.**

### format differences

The schema is identical between toon and json. The only structural difference is how collections are represented:

| field | toon | json |
|---|---|---|
| `occupants` (tiles) | `"Path:z2/Lodge:z2-4"` (flat string, z-ranges) | `[{"name":"Path","z":2},...]` (array) |
| `inventory` (buildings full) | `"Water:30/Logs:5"` (flat string) | `{"Water":30,"Logs":5}` (object) |
| `recipes` (buildings full) | `"Recipe1/Recipe2"` (flat string) | `["Recipe1","Recipe2"]` (array) |
| `find_placement` booleans | 0/1 integers | 0/1 integers |

### Client design

The Python client (`timberbot.py`) defaults to toon format for CLI output. Internal methods that parse data programmatically (e.g. `map()`) force JSON via `_post_json()` to get structured arrays.

### Test suite bots

| Bot | Mode | Purpose |
|---|---|---|
| `self.bot` | JSON | All functional tests -- structured data for assertions |
| `self.strict_bot` | JSON | Error tests -- raises TimberbotError |
| `self.toon_bot` | toon | Format validation -- verifies toon output is compact |
| `jbot` (local) | JSON | json_schema test -- validates JSON structure |
| `tbot` (local) | toon | toon_schema test -- validates toon structure |

Rule: functional tests that parse data use JSON. Tests that validate output format use toon.

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
  "httpHost": "127.0.0.1",
  "webhooksEnabled": true,
  "webhookBatchMs": 200
}
```

- `httpHost`: host address for Python client remote connections (read by timberbot.py, not the server). Default `"127.0.0.1"`
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

## Spatial memory

Persistent colony knowledge in `~/Documents/Timberborn/Mods/Timberbot/memory/`:

- **`brain.toon`** -- colony index in toon format: faction, DC, summary, building counts by role, treeClusters, foodClusters, maps index, task queue. Updated by `brain` command (always fresh from game).
- **`map-{name}-{x1}x{y1}y-{x2}x{y2}y.txt`** -- named ANSI map files with full encoding (z-level bg shading, moisture color, building/water/tree characters). Saved via `map ... name:label`, listed via `list_maps`.

Map rendering uses delta-encoded ANSI (only emits escape codes when bg/fg changes from previous tile) to keep output compact (~6KB for 41x41 area vs ~35KB with per-tile encoding).

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
