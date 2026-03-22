# Getting Started

## Install the mod

### From Steam Workshop

Subscribe to Timberbot on the Steam Workshop. The mod will be installed automatically.

### Manual install

Download `Timberbot.dll`, `manifest.json`, and `thumbnail.png` and place them in:

```
C:\Users\<you>\Documents\Timberborn\Mods\Timberbot\
```

Launch Timberborn and enable the mod in the Mod Manager.

## Verify it works

With the mod loaded and a game running, open a browser to:

```
http://localhost:8085/api/ping
```

You should see `{"status": "ok", "ready": true}`.

## Install the Python client (optional)

```bash
pip install ./timberbot_cli
```

This gives you a `timberbot` command and a Python library.

## First steps

### CLI

```bash
timberbot summary              # full colony snapshot
timberbot buildings            # list all buildings with IDs
timberbot speed                # check game speed
timberbot set_speed 3          # fast forward
timberbot                      # list all available methods
```

### Live dashboard

```bash
timberbot watch
```

Polls every 3 seconds. Shows day progress, drought countdown, per-district population and resources with color coding.

### Interactive REPL

```bash
python -m timberbot_cli.cli
```

Gives you a `tb>` prompt with tab-friendly commands like `summary`, `buildings`, `speed 3`, `pause <id>`.

### Python API

```python
from timberbot_cli.api import Timberbot

bot = Timberbot()

# read
bot.summary()
bot.buildings()
bot.trees()

# write
bot.set_speed(3)
bot.pause_building(building_id)
bot.place_building("LumberjackFlag.IronTeeth", x=120, y=130, z=2)
bot.mark_trees(100, 100, 110, 110, z=2)
bot.set_floodgate(gate_id, 2.0)
```

### Raw HTTP

You don't need Python. Any HTTP client works:

```bash
curl http://localhost:8085/api/summary
curl -X POST http://localhost:8085/api/speed -d '{"speed": 3}'
```

See [API Reference](api-reference.md) for all endpoints.
