---
name: timberbot-cli
description: Python CLI and API for reading and controlling a running Timberborn game via the Timberbot mod. Use when interacting with Timberborn over HTTP.
user-invocable: false
version: "1.0"
updated: "2026-03-21"
---
# Timberbot CLI

Single-file Python client for the Timberbot mod. Reads and controls a running Timberborn game over HTTP (port 8085).

## Setup

Requires the Timberbot mod installed in Timberborn and a game loaded.

```bash
pip install requests
python timberbot.py           # list all methods
python timberbot.py summary   # test connection
```

## CLI Usage

```bash
python timberbot.py <method> [args...]
python timberbot.py watch                    # live dashboard
python timberbot.py summary                  # full colony snapshot
python timberbot.py buildings                # list all buildings with IDs
python timberbot.py set_speed 3              # fast forward
python timberbot.py pause_building 12345     # pause a building
python timberbot.py demolish_building -- -12345  # use -- for negative IDs
```

## Python API

```python
from timberbot import Timberbot
bot = Timberbot()
```

### Read state (nouns)

| Method | Returns |
|--------|---------|
| `bot.ping()` | True if mod is reachable |
| `bot.summary()` | time + weather + all districts |
| `bot.time()` | `{dayNumber, dayProgress, partialDayNumber}` |
| `bot.weather()` | `{cycle, cycleDay, isHazardous, temperateWeatherDuration, hazardousWeatherDuration}` |
| `bot.population()` | `[{district, adults, children, bots}]` |
| `bot.resources()` | `{districtName: {goodName: {available, all}}}` |
| `bot.districts()` | `[{name, population, resources}]` |
| `bot.buildings()` | `[{id, name, x, y, z, finished, paused, priority, maxWorkers, desiredWorkers, assignedWorkers}]` |
| `bot.trees()` | `[{id, name, x, y, z, marked, alive}]` |
| `bot.gatherables()` | `[{id, name, x, y, z, alive}]` (berry bushes etc) |
| `bot.prefabs()` | `[{name, sizeX, sizeY, sizeZ}]` |
| `bot.speed()` | `{speed: 0-3}` |
| `bot.map(x1, y1, x2, y2)` | terrain + water for a region |

### Write actions (verb_noun)

| Method | Description |
|--------|-------------|
| `bot.set_speed(0-3)` | 0=pause, 1=normal, 2=fast, 3=fastest |
| `bot.pause_building(id)` | pause a building |
| `bot.unpause_building(id)` | unpause a building |
| `bot.set_priority(id, priority)` | VeryLow / Normal / VeryHigh |
| `bot.set_workers(id, count)` | set desired worker count |
| `bot.set_floodgate(id, height)` | set floodgate height |
| `bot.place_building(prefab, x, y, z, orientation=0)` | place a building (orientation 0-3) |
| `bot.demolish_building(id)` | demolish a building |
| `bot.mark_trees(x1, y1, x2, y2, z)` | mark area for tree cutting |
| `bot.clear_trees(x1, y1, x2, y2, z)` | clear cutting marks |
| `bot.plant_crop(x1, y1, x2, y2, z, crop)` | mark area for planting |
| `bot.clear_planting(x1, y1, x2, y2, z)` | clear planting marks |
| `bot.set_capacity(id, capacity)` | set stockpile capacity |
| `bot.set_good(id, good)` | set allowed good on stockpile |

### Vanilla API (port 8080)

| Method | Description |
|--------|-------------|
| `bot.levers()` | list all levers |
| `bot.adapters()` | list all adapters |
| `bot.lever_on(name)` | turn lever ON |
| `bot.lever_off(name)` | turn lever OFF |

### Helpers

| Method | Description |
|--------|-------------|
| `bot.near(items, x, y, radius=20)` | filter items by proximity, sorted by distance |
| `bot.named(items, name)` | filter items by name (case-insensitive substring) |
| `bot.scan(x, y, radius=10)` | ASCII grid of terrain, water, buildings, trees |
| `bot.find(source, name=None, x=None, y=None, radius=20)` | find entities from buildings/trees/gatherables |

## IDs and Values

- **Building IDs**: Unity instance IDs from `bot.buildings()`. Ephemeral per session
- **Prefab names**: from `bot.prefabs()` (e.g. `LumberjackFlag.IronTeeth`, `DeepWaterPump.IronTeeth`)
- **Good names**: Timberborn internal names (e.g. `Log`, `Plank`, `Water`, `Berries`)
- **Priority**: `VeryLow`, `Normal`, `VeryHigh`
- **Orientation**: 0-3 (rotates 90 degrees each step)
- **Crop names**: `Kohlrabi`, `Cassava`, `Carrot`, `Potato`, `Wheat`, `Sunflower`, etc.

## Raw HTTP

The mod exposes a JSON API on `http://localhost:8085`. You don't need Python:

```bash
curl http://localhost:8085/api/summary
curl -X POST http://localhost:8085/api/speed -d '{"speed": 3}'
curl http://localhost:8085/api/buildings
```

Full endpoint list: GET endpoints use query params, POST endpoints accept JSON bodies. See `bot.prefabs()` for building names and `bot.buildings()` for IDs.
