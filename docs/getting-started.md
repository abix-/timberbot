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

Download `timberbot.py` from the latest release, or just use it from the repo. Requires `requests`:

```bash
pip install requests
```

## First steps

### CLI

```bash
python timberbot.py summary                              # colony dashboard (TOON format)
python timberbot.py --json summary                       # full JSON output
python timberbot.py buildings                            # list all buildings
python timberbot.py set_speed speed:3                    # fast forward
python timberbot.py place_building prefab:Path x:100 y:130 z:2
python timberbot.py                                      # list all methods with usage
```

### Live dashboard

```bash
python timberbot.py watch
```

Polls every 3 seconds. Shows day progress, drought countdown, per-district population and resources with color coding.

### Raw HTTP

You don't need Python. Any HTTP client works:

```bash
curl http://localhost:8085/api/summary
curl http://localhost:8085/api/buildings
curl -X POST http://localhost:8085/api/speed -d '{"speed": 3}'
```

See [API Reference](api-reference.md) for all endpoints.
