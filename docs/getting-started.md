# Timberbot API

**Full read/write HTTP API for controlling Timberborn with AI.**

Timberbot API gives Claude, ChatGPT, or your own scripts complete access to your beaver colony over HTTP -- read game state, place buildings, manage workers, plant crops, and keep your beavers alive.

---

## What you can do

| | Read | Write |
|---|---|---|
| **Buildings** | All buildings with workers, power, priority, inventory | Place, demolish, pause, configure |
| **Beavers** | Wellbeing, needs, workplace, contamination | Migrate between districts (in-progress) |
| **Resources** | Per-district stocks, distribution settings | Set import/export, stockpile config |
| **Map** | Terrain, water, occupants, contamination | Plant crops, mark trees, route paths |
| **Colony** | Weather, science, alerts, notifications | Speed, work hours, unlock buildings |

---

## Install the mod

### From Steam Workshop

Subscribe to Timberbot API on the Steam Workshop. The mod installs automatically. Launch Timberborn and enable it in the Mod Manager.

### Manual install

Download `Timberbot.dll`, `manifest.json`, and `thumbnail.png` from the [latest GitHub release](https://github.com/abix-/TimberbornMods/releases) and place them in:

```
C:\Users\<you>\Documents\Timberborn\Mods\Timberbot\
```

Enable the mod in the Mod Manager.

## Verify it works

Start a game (or load a save). Open a browser to:

```
http://localhost:8085/api/ping
```

You should see `{"status": "ok", "ready": true}`. The API is only active while a game is loaded -- it won't respond from the main menu.

## Install the Python client

The CLI lives at `timberbot/script/timberbot.py` in your local clone (or in the mod folder alongside the DLL).

Install dependencies:

```bash
pip install requests toons
```

!!! tip "What are these?"
    `requests` is the HTTP client. `toons` formats the compact TOON output that most commands produce by default. Both are required.

### Add to PATH (recommended)

Add the script directory to your system PATH so you can run `timberbot.py` from anywhere:

1. Add the folder containing `timberbot.py` to your **PATH** environment variable (e.g. `C:\Users\<you>\Documents\Timberborn\Mods\Timberbot` for Steam installs)
2. Add `.PY` to your **PATHEXT** environment variable if it isn't already -- this tells Windows to treat `.py` files as executable without needing to type `python` first

```powershell
# check if .PY is already in PATHEXT
echo $env:PATHEXT

# add .PY to PATHEXT for the current user (persistent)
[Environment]::SetEnvironmentVariable("PATHEXT", "$($env:PATHEXT);.PY", "User")
```

After this, commands work from any directory:

```bash
timberbot.py summary
timberbot.py visual x:120 y:140 radius:10
```

!!! note "Shebang for Git Bash / WSL"
    The script includes `#!/usr/bin/env python` so it runs correctly in Unix-style shells (Git Bash, WSL) when the file is on PATH.

## Output formats

=== "TOON (default)"

    Compact tabular format designed for AI consumption and quick scanning:

    ```bash
    timberbot.py summary
    ```

=== "JSON"

    Full nested data for programmatic access:

    ```bash
    timberbot.py --json summary
    ```

The same applies to the HTTP API: add `?format=json` to GET requests, or `"format": "json"` in POST bodies. Without it, endpoints that support both formats default to TOON.

## First commands

```bash
timberbot.py                                        # list all commands with usage
timberbot.py summary                                # colony snapshot: population, resources, weather, alerts
timberbot.py buildings                              # all buildings with workers, priority, power
timberbot.py beavers                                # wellbeing and critical needs per beaver
timberbot.py set_speed speed:3                      # fast forward (0=pause, 1/2/3)
timberbot.py visual x:120 y:140 radius:10          # ASCII map with terrain height shading
```

### Visual map

`visual` renders a colored ASCII grid of your colony. Background shading shows terrain height, characters represent buildings, trees, water, and crops. A legend is printed below the grid.

```bash
timberbot.py visual x:120 y:140 radius:15
```

### Live dashboard

```bash
timberbot.py top
```

Live colony dashboard. Population, resources, weather, drought countdown, wellbeing breakdown, alerts -- all updating in real time.

### Write commands

Commands that change game state use `key:value` arguments:

```bash
timberbot.py place_building prefab:Path x:120 y:130 z:2 orientation:south
timberbot.py set_priority building_id:12340 priority:VeryHigh
timberbot.py plant_crop x1:110 y1:130 x2:115 y2:135 z:2 crop:Carrot
timberbot.py mark_trees x1:100 y1:120 x2:110 y2:130 z:2
```

Get building IDs from `timberbot.py buildings`. Get prefab names from `timberbot.py prefabs`.

### Raw HTTP

You don't need Python. Any HTTP client works:

```bash
curl http://localhost:8085/api/summary
curl http://localhost:8085/api/buildings
curl -X POST http://localhost:8085/api/speed -d '{"speed": 3}'
curl -X POST http://localhost:8085/api/building/place -d '{"prefab": "Path", "x": 120, "y": 130, "z": 2, "orientation": 0}'
```

## Let AI play your colony

The mod includes an AI prompt ([timberbot.md](timberbot.md)) that teaches Claude, ChatGPT, or any LLM how to run your colony autonomously -- managing food, water, housing, workers, and expansion.

### Claude Code setup

```bash
# copy the prompt as a Claude Code skill
mkdir -p ~/.claude/skills/timberbot
cp docs/timberbot.md ~/.claude/skills/timberbot/SKILL.md

# let Claude play on a loop
/loop 1m /timberbot
```

### Other LLMs

Paste the contents of `docs/timberbot.md` as a system prompt. Then ask the AI to run `timberbot.py summary` and take it from there. The prompt includes a decision loop, placement workflow, and all the API commands it needs.

## Troubleshooting

!!! warning "Connection refused / no response on port 8085"
    - The API only runs while a game is loaded. It won't respond from the main menu or loading screen.
    - Check that the mod is enabled in the Mod Manager.
    - Windows Firewall may block the port. The mod tries `http://+:8085/` first (all interfaces), then falls back to `http://localhost:8085/` if that fails.

!!! warning "No module named 'toons' / 'requests'"
    Run `pip install requests toons`. Both are required for the Python CLI.

!!! bug "Building placement creates ghost buildings"
    Failed placements can sometimes create invisible entities. See [Known Issues](api-reference.md#known-issues) in the API reference.

---

- [API Reference](api-reference.md) -- every endpoint with request/response examples
- [AI Prompt](timberbot.md) -- autonomous colony management
- [Features](features.md) -- what's implemented vs gaps
- [Developing](developing.md) -- build from source
