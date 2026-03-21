# Timberbot

C# mod + Python client that lets AI agents read and control a running Timberborn game over HTTP.

## How it works

The mod runs an HTTP server (port 8085) on a background thread inside the Unity game process. Incoming requests are queued and drained on the main thread (max 10/frame) so game state access is thread-safe.

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
| `/api/buildings` | all buildings with id, name, coords, pause/floodgate/priority state |
| `/api/speed` | current game speed (0-3) |

### Write (POST)

| Endpoint | Body | Description |
|----------|------|-------------|
| `/api/speed` | `{"speed": 0}` | 0=pause, 1/2/3=speed |
| `/api/building/pause` | `{"id": 12345, "paused": true}` | pause/unpause building |
| `/api/floodgate` | `{"id": 12345, "height": 1.5}` | set floodgate height |
| `/api/priority` | `{"id": 12345, "priority": "VeryHigh"}` | VeryLow / Normal / VeryHigh |

Building IDs come from `GET /api/buildings`.

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

### REPL

```
python -m timberborn

=== Timberbot ===
  game API:   connected (port 8080)
  bridge mod: connected (port 8085)

tb> summary
tb> buildings
tb> speed 3
tb> pause 12345
tb> floodgate 12345 2.0
tb> priority 12345 VeryHigh
```

### Live dashboard

```
python watch.py
```

Polls `/api/summary` every 3s. Shows day progress bar, drought countdown, per-district population and resource stocks with ANSI color coding.

### Python API

```python
from timberborn.api import TimberbornAPI

api = TimberbornAPI()
api.set_speed(3)

for b in api.get_buildings():
    if b.get("floodgate"):
        api.set_floodgate_height(b["id"], 2.0)
```

## Requirements

- Timberborn (Steam)
- .NET SDK 6+ (build the mod)
- Python 3.8+ with `requests`
