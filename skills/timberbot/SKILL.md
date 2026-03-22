---
name: timberbot-cli
description: Python script for reading and controlling a running Timberborn game via the Timberbot mod. Use when interacting with Timberborn over HTTP.
user-invocable: false
version: "2.0"
updated: "2026-03-21"
---
# Timberbot

`timberbot.py` is a single Python script that talks to the Timberbot mod over HTTP (port 8085). Download it, run it. No install needed beyond `pip install requests`.

## Usage

Every public method on the `Timberbot` class is a CLI command. Pass method name + args:

```bash
python timberbot.py                              # list all methods
python timberbot.py summary                      # full colony snapshot
python timberbot.py buildings                    # list all buildings with IDs
python timberbot.py set_speed 3                  # fast forward
python timberbot.py place_building Path 100 130 2
python timberbot.py place_path 100 130 110 130 2 # line of paths
python timberbot.py demolish_building -- -12345  # -- allows negative IDs
python timberbot.py watch                        # live terminal dashboard
```

Output is always JSON (except `watch` and `scan`).

## Read methods

| Command | Returns |
|---------|---------|
| `ping` | true/false if mod is reachable |
| `summary` | time + weather + all districts with resources and population |
| `time` | `{dayNumber, dayProgress, partialDayNumber}` |
| `weather` | `{cycle, cycleDay, isHazardous, temperateWeatherDuration, hazardousWeatherDuration}` |
| `population` | `[{district, adults, children, bots}]` |
| `resources` | `{districtName: {goodName: {available, all}}}` |
| `districts` | `[{name, population, resources}]` |
| `buildings` | `[{id, name, x, y, z, finished, paused, priority, maxWorkers, desiredWorkers, assignedWorkers}]` |
| `trees` | `[{id, name, x, y, z, marked, alive}]` |
| `gatherables` | `[{id, name, x, y, z, alive}]` (berry bushes etc) |
| `prefabs` | `[{name, sizeX, sizeY, sizeZ}]` |
| `speed` | `{speed: 0-3}` |
| `map x1 y1 x2 y2` | terrain + water tiles for a region |

## Write methods

| Command | Description |
|---------|-------------|
| `set_speed 0-3` | 0=pause, 1=normal, 2=fast, 3=fastest |
| `pause_building ID` | pause a building |
| `unpause_building ID` | unpause a building |
| `set_priority ID VeryHigh` | VeryLow / Normal / VeryHigh |
| `set_workers ID 2` | set desired worker count |
| `set_floodgate ID 1.5` | set floodgate height |
| `place_building PREFAB X Y Z [ORIENTATION]` | place a building (orientation 0-3, default 0) |
| `demolish_building ID` | demolish a building |
| `mark_trees X1 Y1 X2 Y2 Z` | mark rectangular area for cutting |
| `clear_trees X1 Y1 X2 Y2 Z` | clear cutting marks |
| `plant_crop X1 Y1 X2 Y2 Z CROP` | mark area for planting |
| `clear_planting X1 Y1 X2 Y2 Z` | clear planting marks |
| `set_capacity ID 100` | set stockpile capacity |
| `set_good ID Log` | set allowed good on stockpile |
| `place_path X1 Y1 X2 Y2 Z` | place straight line of paths (horizontal or vertical) |

## Helpers

| Command | Description |
|---------|-------------|
| `scan X Y [RADIUS]` | ASCII grid of terrain, water, buildings, trees (default radius 10) |
| `find SOURCE [NAME] [X] [Y] [RADIUS]` | find from buildings/trees/gatherables by name and/or proximity |
| `watch` | live terminal dashboard (polls every 3s) |

## Vanilla API (port 8080)

| Command | Description |
|---------|-------------|
| `levers` | list all levers |
| `adapters` | list all adapters |
| `lever_on NAME` | turn lever ON |
| `lever_off NAME` | turn lever OFF |

## IDs and values

- **Building IDs**: from `buildings` output. Ephemeral per game session
- **Prefab names**: from `prefabs` (e.g. `LumberjackFlag.IronTeeth`, `DeepWaterPump.IronTeeth`)
- **Good names**: `Log`, `Plank`, `Water`, `Berries`, etc.
- **Priority**: `VeryLow`, `Normal`, `VeryHigh`
- **Orientation**: 0-3 (rotates 90 degrees each step)
- **Crops**: `Kohlrabi`, `Cassava`, `Carrot`, `Potato`, `Wheat`, `Sunflower`, etc.

## Raw HTTP (no Python needed)

```bash
curl http://localhost:8085/api/summary
curl http://localhost:8085/api/buildings
curl -X POST http://localhost:8085/api/speed -d '{"speed": 3}'
curl -X POST http://localhost:8085/api/building/place -d '{"prefab": "Path", "x": 100, "y": 130, "z": 2, "orientation": 0}'
```
