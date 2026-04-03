# Developing

## File structure

```
TimberbornMods/
  timberbot/
    src/                              C# mod (runs inside the game)
      TimberbotService.cs               Lifecycle, settings, orchestration (7 DI params)
      TimberbotEntityRegistry.cs        GUID-backed entity lookup + numeric-ID bridge (4 DI params)
      TimberbotReadV2.cs                All GET read endpoints, tracked refs, and published snapshots
      TimberbotWrite.cs                 All POST write endpoints (22 DI params)
      TimberbotPlacement.cs             Building placement, path routing, terrain (14 DI params)
      TimberbotWebhook.cs               Batched push event notifications, circuit breaker (5 DI params)
      TimberbotDebug.cs                 Reflection inspector and benchmark (1 DI param)
      ITimberbotWriteJob.cs              Write job interface for budgeted main-thread execution
      TimberbotHttpServer.cs            HttpListener, routing, request/response handling
      TimberbotJw.cs                    Fluent zero-alloc JSON writer
      TimberbotLog.cs                   File-based error logging, timestamped, thread-safe
      TimberbotConfigurator.cs          Bindito DI module registration
      TimberbotAutoLoad.cs              Auto-load a save at main menu via autoload.json or CLI args
      TimberbotAutoLoadConfigurator.cs  MainMenu context DI registration for auto-load
      Timberbot.csproj                  Build config, game DLL references
      manifest.json                     Mod metadata (version, name, description)
      settings.json                     Persistent settings store (runtime + agent/UI settings, primarily edited in-game)
      thumbnail.png                     Steam Workshop image
    script/
      timberbot.py                      Python client (API + CLI + dashboard)
      test_v2.py                        Primary test harness (smoke, freshness, write_to_read, performance, concurrency)
      test_v2_specs.py                  Test spec definitions for test_v2
      test_validation.py                Validation test suite (77 tests in 11 groups, any save game)
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

If your Steam install is elsewhere, override `GameManagedDir` when building instead of editing the project file:

```bash
dotnet build /p:GameManagedDir="D:\Steam\steamapps\common\Timberborn\Timberborn_Data\Managed"
dotnet build /p:ModDir="C:\Users\<you>\Documents\Timberborn\Mods\Timberbot"
```

On macOS, pass the platform-specific `GameManagedDir` and `ModDir` the same way.

## How the mod works

1. `TimberbotConfigurator` registers all services as singletons in the `Game` context via Bindito DI
2. On `Load()`, `TimberbotService` starts an `HttpListener` on port 8085 in a background thread
3. GET requests are handled directly on the background listener thread (reads from `ReadV2` published snapshots)
4. POST requests are queued in a `ConcurrentQueue<PendingRequest>` and drained on the main thread
5. `UpdateSingleton()` runs every frame: drains POST queue, services pending fresh publishes, flushes webhooks

For full architecture details see [architecture.md](architecture.md).

## Settings model

The in-game `Settings` modal is the primary configuration surface for Timberbot.

All settings persist to `settings.json`, including:

- runtime settings such as `debugEndpointEnabled`, `httpPort`, `webhooksEnabled`, `webhookBatchMs`, `webhookCircuitBreaker`, `webhookMaxPendingEvents`, `writeBudgetMs`, `terminal`, and `pythonCommand`
- agent/UI settings such as `agentBinary`, `agentModel`, `agentEffort`, `agentGoal`, `widgetLeft`, and `widgetTop`

`TimberbotService` keeps an in-memory settings object and debounces writes back to disk. Editing `settings.json` directly is supported, but it is the manual/advanced path rather than the default workflow.

## Adding a new GET endpoint

1. Add a `Collect*` method to `TimberbotReadV2.cs`
2. Add the route to `RouteRequest()` in `TimberbotHttpServer.cs`
3. If you need new game services, inject them via the `TimberbotReadV2` constructor
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

Primary live harness: `timberbot/script/test_v2.py`

```bash
python timberbot/script/test_v2.py smoke
python timberbot/script/test_v2.py write_to_read
python timberbot/script/test_v2.py performance -n 200
python timberbot/script/test_v2.py concurrency
python timberbot/script/test_v2.py all -n 200
```

Validation test suite: `timberbot/script/test_validation.py`

Tests are organized into groups. Default run excludes `perf` and `wipe`.

```bash
# run all default groups (game must be running with mod loaded)
python timberbot/script/test_validation.py

# run a specific group
python timberbot/script/test_validation.py path

# run multiple groups
python timberbot/script/test_validation.py read write placement

# run individual tests
python timberbot/script/test_validation.py blocker_tracking path_astar_diagonal

# mix groups and individual tests
python timberbot/script/test_validation.py path blocker_tracking

# exclude groups or tests
python timberbot/script/test_validation.py -x perf wipe

# list groups and their tests
python timberbot/script/test_validation.py --list

# performance only (latency across 20 endpoints)
python timberbot/script/test_validation.py --perf
python timberbot/script/test_validation.py --perf -n 500

# in-game benchmark endpoint
python timberbot/script/test_validation.py --benchmark
python timberbot/script/test_validation.py --benchmark -n 10000
```

| Group | Tests | Description |
|---|---|---|
| read | 9 | GET endpoints, projections, map, schema, data accuracy |
| write | 16 | speed, pause, priority, workers, floodgate, recipes, etc. |
| placement | 6 | place/demolish, orientation, find, water, overridable, blockers |
| path | 16 | flat, 1z, 2z (all directions), A* diagonal/obstacle/no-route, sections |
| crops | 6 | crops, tree marking, planting, clear, demolish crop |
| buildings | 6 | detail, inventory, range, recipes, prefab costs, power |
| beavers | 10 | detail, needs, position, district, bots, carrying, durability |
| webhooks | 1 | register, receive, filter, unregister, resilience |
| cli | 2 | CLI commands, error codes |
| perf | 4 | endpoint latency, building perf, brain perf, v2 parity |
| wipe | 1 | demolish all buildings + clear all crops |

### What the tests cover

- **Smoke**: representative coverage of the full `/api/*` read surface
- **Write-to-read**: POST change -> first GET sees it -> restore -> first GET sees restoration
- **Performance**: direct endpoint latency comparisons across the live snapshot path
- **Concurrency**: simultaneous requests against projection-backed endpoints
- **Validation**: `test_validation.py` covers 77 tests across 11 groups (read, write, placement, path, crops, buildings, beavers, webhooks, cli, perf, wipe)
- **Cache invalidation**: place path -> count+1, demolish -> count back (EventBus + fresh-on-request snapshots)
- **Data accuracy**: `validate` endpoint compares cached vs live game state per field. `validate_all` checks all entities, all fields, 0 mismatches
- **Burst**: 7 sequential calls < 3s total (24ms measured)
- **Save-agnostic**: discovery phase detects faction, map bounds, existing buildings
- **Webhooks**: register, receive, filter, unregister, bad URL resilience, payload accuracy

### In-game benchmark

`/api/benchmark` (POST, requires `debugEndpointEnabled: true` in settings.json) runs micro-benchmarks with GC0 tracking. It is queued and stepped under the main-thread write budget, so the request may take multiple frames to complete.

- Collection iteration patterns (foreach vs for-loop, enumerator boxing)
- Game API alloc checks (GetNeeds, Inventories, BreedingPod.Nutrients)
- Lightweight internal helpers like prefab collection

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
