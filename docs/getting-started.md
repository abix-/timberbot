# Getting Started

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

`timberbot.py` is in your mods folder alongside the DLL:

```
Documents\Timberborn\Mods\Timberbot\timberbot.py
```

Install dependencies:

```bash
pip install requests toons
```

!!! tip "What are these?"
    `requests` is the HTTP client. `toons` formats the compact TOON output that most commands produce by default. Both are required.

## Output formats

The CLI has two output modes:

**TOON** (default) -- compact tabular format designed for AI consumption and quick scanning:

```bash
python timberbot.py summary
```

**JSON** -- full nested data for programmatic access:

```bash
python timberbot.py --json summary
```

The same applies to the HTTP API: add `?format=json` to GET requests, or `"format": "json"` in POST bodies. Without it, endpoints that support both formats default to TOON.

## First commands

```bash
python timberbot.py                                        # list all commands with usage
python timberbot.py summary                                # colony snapshot: population, resources, weather, alerts
python timberbot.py buildings                              # all buildings with workers, priority, power
python timberbot.py beavers                                # wellbeing and critical needs per beaver
python timberbot.py set_speed speed:3                      # fast forward (0=pause, 1/2/3)
python timberbot.py visual x:120 y:140 radius:10          # ASCII map with terrain height shading
```

### Visual map

`visual` renders a colored ASCII grid of your colony. Background shading shows terrain height, characters represent buildings, trees, water, and crops. A legend is printed below the grid.

```bash
python timberbot.py visual x:120 y:140 radius:15
```

### Live dashboard

```bash
python timberbot.py watch
```

Polls every 3 seconds. Shows day progress, drought countdown, per-district population and resources with color coding.

### Write commands

Commands that change game state use `key:value` arguments:

```bash
python timberbot.py place_building prefab:Path x:120 y:130 z:2 orientation:south
python timberbot.py set_priority building_id:12340 priority:VeryHigh
python timberbot.py plant_crop x1:110 y1:130 x2:115 y2:135 z:2 crop:Carrot
python timberbot.py mark_trees x1:100 y1:120 x2:110 y2:130 z:2
```

Get building IDs from `python timberbot.py buildings`. Get prefab names from `python timberbot.py prefabs`.

### Raw HTTP

You don't need Python. Any HTTP client works:

```bash
curl http://localhost:8085/api/summary
curl http://localhost:8085/api/buildings
curl -X POST http://localhost:8085/api/speed -d '{"speed": 3}'
curl -X POST http://localhost:8085/api/building/place -d '{"prefab": "Path", "x": 120, "y": 130, "z": 2, "orientation": 0}'
```

## Let AI play your colony

The mod includes an AI prompt ([docs/timberbot.md](timberbot.md)) that teaches Claude, ChatGPT, or any LLM how to run your colony autonomously -- managing food, water, housing, workers, and expansion.

### Claude Code setup

```bash
# copy the prompt as a Claude Code skill
mkdir -p ~/.claude/skills/timberbot
cp docs/timberbot.md ~/.claude/skills/timberbot/SKILL.md

# let Claude play on a loop
/loop 1m /timberbot
```

### Other LLMs

Paste the contents of `docs/timberbot.md` as a system prompt. Then ask the AI to run `python timberbot.py summary` and take it from there. The prompt includes a decision loop, placement workflow, and all the API commands it needs.

## Troubleshooting

!!! warning "Connection refused / no response on port 8085"
    - The API only runs while a game is loaded. It won't respond from the main menu or loading screen.
    - Check that the mod is enabled in the Mod Manager.
    - Windows Firewall may block the port. The mod tries `http://+:8085/` first (all interfaces), then falls back to `http://localhost:8085/` if that fails.

!!! warning "No module named 'toons' / 'requests'"
    Run `pip install requests toons`. Both are required for the Python CLI.

!!! bug "Building placement creates ghost buildings"
    Failed placements can sometimes create invisible entities. See [Known Issues](api-reference.md#known-issues) in the API reference.

## Next steps

- [API Reference](api-reference.md) -- all endpoints with request/response examples
- [AI Prompt](timberbot.md) -- teach an LLM to play your colony
- [Developing](developing.md) -- build the mod from source
