---
name: timberbot-cli
description: Python script for reading and controlling a running Timberborn game via the Timberbot mod. Use when interacting with Timberborn over HTTP.
user-invocable: false
version: "3.0"
updated: "2026-03-21"
---
# Timberbot

`timberbot.py` is a single Python script that talks to the Timberbot mod over HTTP (port 8085). Download it, run it. No install needed beyond `pip install requests`.

## Usage

All args are `key:value` pairs. No positional arguments.

```bash
python timberbot.py                                          # list all methods with usage
python timberbot.py summary                                  # full colony snapshot
python timberbot.py buildings                                # list all buildings with IDs
python timberbot.py set_speed speed:3                        # fast forward
python timberbot.py place_building prefab:Path x:100 y:130 z:2
python timberbot.py place_path x1:100 y1:130 x2:110 y2:130 z:2
python timberbot.py demolish_building building_id:12345
python timberbot.py watch                                    # live terminal dashboard
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
| `map x1:N y1:N x2:N y2:N` | terrain + water tiles for a region |

## Write methods

| Command | Description |
|---------|-------------|
| `set_speed speed:0-3` | 0=pause, 1=normal, 2=fast, 3=fastest |
| `pause_building building_id:ID` | pause a building |
| `unpause_building building_id:ID` | unpause a building |
| `set_priority building_id:ID priority:VeryHigh` | VeryLow / Normal / VeryHigh |
| `set_workers building_id:ID count:2` | set desired worker count |
| `set_floodgate building_id:ID height:1.5` | set floodgate height |
| `place_building prefab:NAME x:N y:N z:N orientation:0` | place a building (orientation 0-3) |
| `demolish_building building_id:ID` | demolish a building |
| `mark_trees x1:N y1:N x2:N y2:N z:N` | mark rectangular area for cutting |
| `clear_trees x1:N y1:N x2:N y2:N z:N` | clear cutting marks |
| `plant_crop x1:N y1:N x2:N y2:N z:N crop:Carrot` | mark area for planting |
| `clear_planting x1:N y1:N x2:N y2:N z:N` | clear planting marks |
| `set_capacity building_id:ID capacity:100` | set stockpile capacity |
| `set_good building_id:ID good:Log` | set allowed good on stockpile |
| `place_path x1:N y1:N x2:N y2:N z:N` | place straight line of paths (horizontal or vertical) |

## Helpers

| Command | Description |
|---------|-------------|
| `scan x:N y:N radius:10` | ASCII grid of terrain, water, buildings, trees |
| `find source:buildings name:Lumber x:100 y:130 radius:20` | find entities by name and/or proximity |
| `watch` | live terminal dashboard (polls every 3s) |

## Vanilla API (port 8080)

| Command | Description |
|---------|-------------|
| `levers` | list all levers |
| `adapters` | list all adapters |
| `lever_on name:MyLever` | turn lever ON |
| `lever_off name:MyLever` | turn lever OFF |

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
