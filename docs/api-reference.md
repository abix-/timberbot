# API Reference

Base URL: `http://localhost:8085`

## Read (GET)

| Endpoint | Returns |
|----------|---------|
| `/api/ping` | `{status, ready}` -- health check |
| `/api/summary` | full snapshot: time + weather + all districts |
| `/api/resources` | resource stocks per district |
| `/api/population` | beaver/bot counts per district |
| `/api/time` | day number, progress |
| `/api/weather` | cycle, drought countdown |
| `/api/districts` | districts with resources + population |
| `/api/buildings` | all buildings: id, name, coords, pause, priority, workers |
| `/api/trees` | all cuttable trees: id, name, coords, marked status |
| `/api/gatherables` | gatherable resources (berry bushes etc) |
| `/api/prefabs` | available building templates for placement |
| `/api/speed` | current game speed `{speed: 0-3}` |
| `/api/map` | map size info (no args) |

## Write (POST)

All write endpoints accept JSON bodies.

| Endpoint | Body | Description |
|----------|------|-------------|
| `/api/speed` | `{"speed": 0}` | 0=pause, 1/2/3=speed |
| `/api/building/pause` | `{"id": N, "paused": true}` | pause/unpause building |
| `/api/building/demolish` | `{"id": N}` | demolish a building |
| `/api/building/place` | `{"prefab": "Name", "x": N, "y": N, "z": N, "orientation": 0}` | place a new building |
| `/api/floodgate` | `{"id": N, "height": 1.5}` | set floodgate height |
| `/api/priority` | `{"id": N, "priority": "VeryHigh"}` | VeryLow / Normal / VeryHigh |
| `/api/workers` | `{"id": N, "count": 2}` | set desired worker count |
| `/api/cutting/area` | `{"x1": N, "y1": N, "x2": N, "y2": N, "z": N, "marked": true}` | mark/clear tree cutting area |
| `/api/planting/mark` | `{"x1": N, "y1": N, "x2": N, "y2": N, "z": N, "crop": "Carrot"}` | mark area for planting |
| `/api/planting/clear` | `{"x1": N, "y1": N, "x2": N, "y2": N, "z": N}` | clear planting marks |
| `/api/stockpile/capacity` | `{"id": N, "capacity": 100}` | set stockpile capacity |
| `/api/stockpile/good` | `{"id": N, "good": "Log"}` | set allowed good |
| `/api/map` | `{"x1": N, "y1": N, "x2": N, "y2": N}` | terrain + water for a region |

## IDs and names

- Building IDs are Unity instance IDs, returned by `GET /api/buildings`
- Prefab names come from `GET /api/prefabs`
- Good names match Timberborn internal names (e.g. `Log`, `Plank`, `Water`, `Berries`)
- Priority values: `VeryLow`, `Normal`, `VeryHigh`
- Orientation: 0-3 (0=default, rotates 90 degrees each step)

## Vanilla API (port 8080)

Timberborn's built-in API for levers and adapters. Not part of this mod.

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/levers` | GET | list all levers |
| `/api/levers/{name}` | GET | single lever |
| `/api/switch-on/{name}` | POST | turn lever on |
| `/api/switch-off/{name}` | POST | turn lever off |
| `/api/color/{name}/{hex}` | POST | set lever color |
| `/api/adaptors` | GET | list all adapters |
| `/api/adaptors/{name}` | GET | single adapter |
