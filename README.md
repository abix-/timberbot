# Timberbot

C# mod + Python client that lets AI agents read and control a running Timberborn game over HTTP.

## How it works

The mod runs an HTTP server (port 8085) on a background thread inside the Unity game process. Incoming requests are queued and drained on the main thread (max 10/frame) so game state access is thread-safe. GET requests for simple data (ping, speed) are handled directly on the listener thread so they work even when the game is paused.

```
Timberborn (Unity)
  |-- Timberbot mod (port 8085)      read + write game state
  |-- Vanilla HTTP API (port 8080)   levers + adapters (built-in)

Python client
  |-- timberborn/api.py              TimberbornAPI wrapper
  |-- timberborn/cli.py              interactive REPL (tb> prompt)
  |-- watch.py                       live ANSI terminal dashboard
```

## API

### Read (GET)

| Endpoint | Returns |
|----------|---------|
| `/api/ping` | health check |
| `/api/summary` | full snapshot: time + weather + all districts |
| `/api/resources` | resource stocks per district |
| `/api/population` | beaver/bot counts per district |
| `/api/time` | day number, progress |
| `/api/weather` | cycle, drought countdown |
| `/api/districts` | districts with resources + population |
| `/api/buildings` | all buildings: id, name, coords, pause, priority, workers |
| `/api/trees` | all cuttable trees: id, name, coords, marked status |
| `/api/prefabs` | available building templates for placement |
| `/api/speed` | current game speed (0-3) |

### Write (POST)

| Endpoint | Body | Description |
|----------|------|-------------|
| `/api/speed` | `{"speed": 0}` | 0=pause, 1/2/3=speed |
| `/api/building/pause` | `{"id": N, "paused": true}` | pause/unpause building |
| `/api/building/demolish` | `{"id": N}` | demolish a building |
| `/api/building/place` | `{"prefab": "Name", "x": N, "y": N, "z": N, "orientation": 0}` | place a new building |
| `/api/floodgate` | `{"id": N, "height": 1.5}` | set floodgate height |
| `/api/priority` | `{"id": N, "priority": "VeryHigh"}` | VeryLow / Normal / VeryHigh |
| `/api/workers` | `{"id": N, "count": 2}` | set desired worker count |
| `/api/cutting/area` | `{"x1": N, "y1": N, "x2": N, "y2": N, "z": N, "marked": true}` | mark/clear cutting area |
| `/api/stockpile/capacity` | `{"id": N, "capacity": 100}` | set stockpile capacity |
| `/api/stockpile/good` | `{"id": N, "good": "Log"}` | set allowed good |

Building IDs come from `GET /api/buildings`. Prefab names come from `GET /api/prefabs`.

## Install

### Build the mod

```
cd mod/Timberbot
dotnet build
```

### Deploy

Copy `Timberbot.dll` + `manifest.json` to your mods folder:

```
cp bin/Debug/netstandard2.1/Timberbot.dll ~/Documents/Timberborn/Mods/Timberbot/
cp manifest.json ~/Documents/Timberborn/Mods/Timberbot/
```

Default path: `C:\Users\<you>\Documents\Timberborn\Mods\Timberbot\`

### Python client

```
pip install -r requirements.txt
```

## Usage

### CLI

```bash
python -m timberbot buildings
python -m timberbot set_speed 3
python -m timberbot place_building LumberjackFlag.IronTeeth 120 130 2
python -m timberbot mark_trees 100 100 110 110 2
python -m timberbot demolish_building -- -12345
python -m timberbot                          # list all methods
```

### Live dashboard

```
python watch.py
```

Polls `/api/summary` every 3s. Shows day progress bar, drought countdown, per-district population and resource stocks with ANSI color coding.

### Python API

```python
from timberbot.api import Timberbot

bot = Timberbot()

# read game state
bot.summary()
bot.buildings()
bot.trees()
bot.prefabs()

# control game speed
bot.set_speed(3)

# manage buildings
bot.pause_building(building_id)
bot.unpause_building(building_id)
bot.set_workers(building_id, 0)
bot.set_priority(building_id, "VeryHigh")
bot.demolish_building(building_id)

# place new buildings
bot.place_building("LumberjackFlag.IronTeeth", x=120, y=130, z=2)

# mark trees for cutting (rectangle region)
bot.mark_trees(100, 100, 110, 110, z=2)
bot.clear_trees(100, 100, 110, 110, z=2)

# floodgate control
bot.set_floodgate(gate_id, 2.0)
```

## Requirements

- Timberborn (Steam)
- .NET SDK 6+ (build the mod)
- Python 3.8+ with `requests`
