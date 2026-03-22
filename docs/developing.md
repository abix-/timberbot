# Developing

## Project structure

```
TimberbornMods/
  timberbot/
    src/                    C# mod (runs inside the game)
      TimberbotService.cs     game state collection + write actions
      TimberbotHttpServer.cs  background HTTP listener, request queue
      TimberbotConfigurator.cs  Bindito DI registration
      Timberbot.csproj        build config, game DLL references
      manifest.json           mod metadata
      thumbnail.png           Steam Workshop image
    script/
      timberbot.py            Python client (API + CLI + dashboard)
  release.py              build + package + GitHub release script
  docs/                   documentation
  README.md
  .gitignore
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

1. `TimberbotConfigurator` registers `TimberbotService` as a singleton in the `Game` context via Bindito DI
2. On `Load()`, the service starts an `HttpListener` on port 8085 in a background thread
3. Incoming requests are queued in a `ConcurrentQueue<PendingRequest>`
4. `UpdateSingleton()` drains up to 10 requests per frame on the Unity main thread
5. `/api/ping` is handled directly on the listener thread (no main-thread round-trip)
6. All game state reads and writes happen on the main thread via `DrainRequests()`

## Adding a new endpoint

1. Add a `Collect*` or action method to `TimberbotService.cs`
2. Add the route to `RouteRequest()` in `TimberbotHttpServer.cs`
3. If you need new game services, inject them via the constructor and add the DLL reference to `Timberbot.csproj` with `Publicize="true"` and `<Private>false</Private>`
4. Add a matching method to the `Timberbot` class in `timberbot/script/timberbot.py` (distributed as `timberbot.py`)

## Adding new game DLL references

```xml
<Reference Include="Timberborn.NewSystem" Publicize="true">
  <Private>false</Private>
  <HintPath>$(GameManagedDir)\Timberborn.NewSystem.dll</HintPath>
</Reference>
```

`Publicize="true"` makes internal types accessible. `<Private>false</Private>` prevents copying the DLL to output (the game already has it).

## Steam Workshop

### First publish

1. `dotnet build` (auto-deploys DLL + manifest + thumbnail to mods folder)
2. Launch Timberborn, open Mod Manager from main menu
3. Find Timberbot in your local mods, click the upload/publish button
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

### mod.io

Create a mod entry at https://mod.io/g/timberborn and upload a ZIP containing `Timberbot.dll`, `manifest.json`, and `thumbnail.png`.

Do NOT ship game DLLs. Only your mod's DLL.
