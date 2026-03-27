# Developing

## File structure

```
TimberbornMods/
  timberbot/
    src/                              C# mod (runs inside the game)
      TimberbotService.cs               Lifecycle, settings, orchestration (7 DI params)
      TimberbotEntityCache.cs           Double-buffered entity caching, cached classes, indexes (5 DI params)
      TimberbotRead.cs                  All GET read endpoints (19 DI params)
      TimberbotWrite.cs                 All POST write endpoints (22 DI params)
      TimberbotPlacement.cs             Building placement, path routing, terrain (14 DI params)
      TimberbotWebhook.cs               Batched push event notifications, circuit breaker (5 DI params)
      TimberbotDebug.cs                 Reflection inspector and benchmark (1 DI param)
      TimberbotHttpServer.cs            HttpListener, routing, request/response handling
      TimberbotJw.cs                    Fluent zero-alloc JSON writer
      TimberbotDoubleBuffer.cs          Generic double-buffer with Add/RemoveAll/Swap
      TimberbotLog.cs                   File-based error logging, timestamped, thread-safe
      TimberbotConfigurator.cs          Bindito DI module registration
      TimberbotAutoLoad.cs              Auto-load a save at main menu via autoload.json or CLI args
      TimberbotAutoLoadConfigurator.cs  MainMenu context DI registration for auto-load
      Timberbot.csproj                  Build config, game DLL references
      manifest.json                     Mod metadata (version, name, description)
      settings.json                     Runtime config (port, refresh rate, debug, webhooks)
      thumbnail.png                     Steam Workshop image
    script/
      timberbot.py                      Python client (API + CLI + dashboard)
      test_validation.py                Test suite (63 tests, any save game)
      release.py                        Build + package + GitHub release script
  docs/                               Documentation
    architecture.md                     How the mod works (thread model, caching, serialization)
    performance.md                      Measurements, benchmarks, GC pressure, optimization history
    developing.md                       This file (building, testing, contributing)
    api-reference.md                    Endpoint documentation
```

## Building the mod

Requires .NET SDK 6+ and Timberborn installed.

```bash
cd timberbot/src
dotnet build
```

This compiles `Timberbot.dll` and auto-deploys to `Documents\Timberborn\Mods\Timberbot\`.

Game DLLs are referenced from:
```
C:\Games\Steam\steamapps\common\Timberborn\Timberborn_Data\Managed
```

If your Steam install is elsewhere, edit `GameManagedDir` in `Timberbot.csproj`.

## How the mod works

1. `TimberbotConfigurator` registers all services as singletons in the `Game` context via Bindito DI
2. On `Load()`, `TimberbotService` starts an `HttpListener` on port 8085 in a background thread
3. GET requests are handled directly on the background listener thread (reads from double-buffered cache)
4. POST requests are queued in a `ConcurrentQueue<PendingRequest>` and drained on the main thread
5. `UpdateSingleton()` runs every frame: refreshes cache (1s cadence), drains POST queue, flushes webhooks

For full architecture details see [architecture.md](architecture.md).

## Adding a new GET endpoint

1. Add a `Collect*` method to `TimberbotRead.cs` -- reads from `_cache.Buildings.Read` / `_cache.Beavers.Read` / `_cache.NaturalResources.Read`
2. Add the route to `RouteRequest()` in `TimberbotHttpServer.cs`
3. If you need new game services, inject them via the `TimberbotRead` constructor
4. Add a matching method to the `Timberbot` class in `timberbot/script/timberbot.py`

## Adding a new POST endpoint

1. Add an action method to `TimberbotWrite.cs` or `TimberbotPlacement.cs`
2. Add the route to `RouteRequest()` in `TimberbotHttpServer.cs` (POST routes run on main thread)
3. If you need new game services, inject them via the constructor
4. Add a matching method to `timberbot.py`

## Adding new game DLL references

```xml
<Reference Include="Timberborn.NewSystem" Publicize="true">
  <Private>false</Private>
  <HintPath>$(GameManagedDir)\Timberborn.NewSystem.dll</HintPath>
</Reference>
```

`Publicize="true"` makes internal types accessible. `<Private>false</Private>` prevents copying the DLL to output (the game already has it).

## Testing

### Test suite

`timberbot/script/test_validation.py` -- 63 tests covering all Python client methods, any save game, any faction.

```bash
# run all tests (game must be running with mod loaded)
python timberbot/script/test_validation.py

# run specific tests
python timberbot/script/test_validation.py speed webhooks

# performance only (latency across 20 endpoints)
python timberbot/script/test_validation.py --perf
python timberbot/script/test_validation.py --perf -n 500

# in-game benchmark endpoint (micro-benchmarks + endpoint profiling)
python timberbot/script/test_validation.py --benchmark
python timberbot/script/test_validation.py --benchmark -n 10000

# list all test names
python timberbot/script/test_validation.py --list
```

### What the tests cover

- **Latency**: 20 endpoints x 100 iterations each (2000 calls total). All endpoints under 50ms min
- **Reliability**: all 2000 responses valid (no errors, no corruption)
- **Cache consistency**: same endpoint called twice returns same count (no stale refs)
- **Cache invalidation**: place path -> count+1, demolish -> count back (EventBus + DoubleBuffer)
- **Data accuracy**: `validate` endpoint compares cached vs live game state per field. `validate_all` checks all entities, all fields, 0 mismatches
- **Burst**: 7 sequential calls < 3s total (24ms measured)
- **Save-agnostic**: discovery phase detects faction, map bounds, existing buildings
- **Webhooks**: register, receive, filter, unregister, bad URL resilience, payload accuracy

### In-game benchmark

`/api/benchmark` (POST, requires `debugEndpointEnabled: true` in settings.json) runs micro-benchmarks with GC0 tracking:

- Collection iteration patterns (foreach vs for-loop, enumerator boxing)
- Game API alloc checks (GetNeeds, Inventories, BreedingPod.Nutrients)
- String interpolation and concat patterns
- All endpoint profiling (CollectSummary, CollectBuildings, etc. in both json and toon formats)

Results include per-test GC0 count, ms/call, and pass/fail. See [performance.md](performance.md#benchmarks) for recorded results.

## Steam Workshop

### First publish

1. `dotnet build` (auto-deploys DLL + manifest + thumbnail to mods folder)
2. Launch Timberborn, open Mod Manager from main menu
3. Find Timberbot API in your local mods, click the upload/publish button
4. Accept Steam Workshop ToS on first upload
5. A `workshop_data.json` is generated in your mods folder -- this links your local mod to the Workshop item ID

### Updating

1. Bump version in `manifest.json` and `Timberbot.csproj`
2. `dotnet build` (auto-deploys updated files to mods folder)
3. Launch Timberborn, open Mod Manager
4. Your mod shows an update option because `workshop_data.json` is present
5. Check the boxes for what to update (files, description, preview image)
6. Upload

**Important:** Keep `workshop_data.json` in your mods folder (it's gitignored). Without it, uploading creates a NEW Workshop entry instead of updating the existing one.

### GitHub release

```bash
python timberbot/script/release.py --release
```

This builds a Release DLL, packages a ZIP (DLL + manifest + thumbnail + timberbot.py), tags the version, and creates a GitHub release.
