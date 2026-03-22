# Developing

## Project structure

```
TimberbornMods/
  timberbot/              C# mod (runs inside the game)
    TimberbotService.cs     game state collection + write actions
    TimberbotHttpServer.cs  background HTTP listener, request queue
    TimberbotConfigurator.cs  Bindito DI registration
    Timberbot.csproj        build config, game DLL references
    manifest.json           mod metadata
    thumbnail.png           Steam Workshop image
  timberbot_cli/          Python client
    api.py                  Timberbot class (all HTTP calls)
    cli.py                  interactive REPL (tb> prompt)
    watch.py                live terminal dashboard
    __main__.py             CLI entry point
    pyproject.toml          Python package config
  docs/                   documentation
  README.md
  .gitignore
```

## Building the mod

Requires .NET SDK 6+ and Timberborn installed.

```bash
cd timberbot
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
4. Add a matching method to `timberbot_cli/api.py`
5. Add the command to `timberbot_cli/cli.py` if it should be in the REPL

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

1. `dotnet build` (auto-deploys to mods folder)
2. Launch Timberborn, open Mod Manager
3. Use the upload panel (accept Workshop ToS on first upload)
4. A `workshop_data.json` is generated -- keep it for updates (gitignored)

### Updating

With `workshop_data.json` present, re-uploading lets you selectively update description, visibility, and preview image. Check "Upload as new" to create a separate entry.

### mod.io

Create a mod entry at https://mod.io/g/timberborn and upload a ZIP containing `Timberbot.dll`, `manifest.json`, and `thumbnail.png`.

Do NOT ship game DLLs. Only your mod's DLL.
