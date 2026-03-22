# API Reference

Base URL: `http://localhost:8085`

CLI output uses [TOON format](https://github.com/toon-format/toon) (Token-Oriented Object Notation) for compact, token-efficient output. Requires `pip install toons`. Falls back to JSON if not installed.

## Read (GET)

| Endpoint | CLI format | Returns |
|----------|-----------|---------|
| `/api/ping` | flat kv | `{status, ready}` -- health check |
| `/api/summary` | flat kv | day, weather, population, resources (flattened) |
| `/api/time` | flat kv | day number, progress |
| `/api/weather` | flat kv | cycle, drought countdown |
| `/api/population` | tabular | `[N]{district,adults,children,bots}` |
| `/api/resources` | tabular | `[N]{district,good,available,all}` |
| `/api/districts` | tabular | `[N]{name,adults,children,bots,Water,Log,...}` |
| `/api/buildings` | tabular | `[N]{id,name,x,y,z,orientation,finished,paused,priority,workers,reachable,powered,isGenerator,isConsumer,nominalPowerInput,nominalPowerOutput,powerDemand,powerSupply,buildProgress,materialProgress,hasMaterials,inventory,statuses,isWonder,wonderActive,dwellers,isClutch,clutchEngaged}` |
| `/api/trees` | tabular | `[N]{id,name,x,y,z,marked,alive,grown,growth}` |
| `/api/gatherables` | tabular | `[N]{id,name,x,y,z,alive}` |
| `/api/beavers` | tabular | `[N]{id,name,wellbeing,needs,anyCritical,lifeProgress,workplace,isBot,contaminated,hasHome}` |
| `/api/prefabs` | tabular | `[N]{name,sizeX,sizeY,sizeZ}` |
| `/api/distribution` | nested tabular | `[N]{district, goods[N]{good,importOption,exportThreshold}}` |
| `/api/science` | nested | `{points, unlockables[N]{name,cost,unlocked}}` |
| `/api/notifications` | tabular | `[N]{subject,description,cycle,cycleDay}` |
| `/api/workhours` | flat kv | `{endHours, areWorkingHours}` |
| `/api/speed` | flat kv | `speed: 0-3` (0=pause, 1=normal, 2=fast, 3=fastest) |
| `/api/map` | tabular | `[N]{x,y,terrain,water,occupant,entrance}` |

## Write (POST)

All write endpoints accept JSON bodies.

| Endpoint | Body | Description |
|----------|------|-------------|
| `/api/speed` | `{"speed": 0}` | 0=pause, 1=normal, 2=fast, 3=fastest |
| `/api/science/unlock` | `{"building": "Name"}` | unlock a building using science points |
| `/api/distribution` | `{"district": "Name", "good": "Log", "import": "Forced", "exportThreshold": 50}` | set import/export per good |
| `/api/workhours` | `{"endHours": 14}` | set when work ends (1-24) |
| `/api/district/migrate` | `{"from": "District 1", "to": "District 2", "count": 3}` | move adult beavers between districts |
| `/api/building/pause` | `{"id": N, "paused": true}` | pause/unpause building |
| `/api/building/demolish` | `{"id": N}` | demolish a building |
| `/api/building/place` | `{"prefab": "Name", "x": N, "y": N, "z": N, "orientation": 0}` | place a building (validates all tiles, origin-corrected) |
| `/api/floodgate` | `{"id": N, "height": 1.5}` | set floodgate height |
| `/api/priority` | `{"id": N, "priority": "VeryHigh"}` | VeryLow / Normal / VeryHigh |
| `/api/workers` | `{"id": N, "count": 2}` | set desired worker count |
| `/api/cutting/area` | `{"x1": N, "y1": N, "x2": N, "y2": N, "z": N, "marked": true}` | mark/clear tree cutting area |
| `/api/planting/mark` | `{"x1": N, "y1": N, "x2": N, "y2": N, "z": N, "crop": "Carrot"}` | mark area for planting (skips occupied/water/invalid tiles) |
| `/api/planting/clear` | `{"x1": N, "y1": N, "x2": N, "y2": N, "z": N}` | clear planting marks |
| `/api/stockpile/capacity` | `{"id": N, "capacity": 100}` | set stockpile capacity |
| `/api/stockpile/good` | `{"id": N, "good": "Log"}` | set allowed good |
| `/api/map` | `{"x1": N, "y1": N, "x2": N, "y2": N}` | terrain + water + occupants for a region |

## IDs and names

- Building IDs are Unity instance IDs, returned by `GET /api/buildings`
- Prefab names come from `GET /api/prefabs`
- Good names match Timberborn internal names (e.g. `Log`, `Plank`, `Water`, `Berries`)
- Priority values: `VeryLow`, `Normal`, `VeryHigh`
- Orientation: south, west, north, east (Python client only accepts names, C# API accepts 0-3)
  - Coords always refer to the bottom-left corner of the footprint regardless of orientation
- Crop names: `Kohlrabi`, `Cassava`, `Carrot`, `Potato`, `Wheat`, `Sunflower`, etc.

## Python-only helpers

These are convenience methods in `timberbot.py`, not HTTP endpoints:

| Method | Description |
|--------|-------------|
| `beavers` | beaver wellbeing + critical needs (flattened to tabular TOON) |
| `tree_clusters` | top 5 clusters of grown trees with coords and counts |
| `scan x:N y:N radius:10` | occupied tiles + water, skipping empty ground (tabular TOON) |
| `visual x:N y:N radius:10` | colored ASCII grid for humans, roguelike style with ANSI colors |
| `find source:buildings name:NAME x:N y:N radius:20` | find entities by name and/or proximity |
| `place_path x1:N y1:N x2:N y2:N z:N` | place a straight line of paths |
| `watch` | live terminal dashboard (polls every 3s) |

### scan output

`scan` returns occupied and water tiles in tabular TOON, skipping empty ground:

```
center: "122,136"
radius: 10
default: ground
occupied[47]{x,y,what}:
  119,131,SmallTank.entrance
  120,133,Path
  121,133,DeepWaterPump
  123,138,Kohlrabi.seedling
water[89]{x,y}:
  122,131
  123,131
```

Suffixes: `.seedling` = not fully grown, `.dead` = dead stump (buildable), `.entrance` = building entrance tile.

### visual color legend

| Char | Color | Meaning |
|------|-------|---------|
| `.` | dim | empty ground |
| `~` | blue | water |
| `@` | white | entrance |
| `=` | yellow | path |
| `D` | bright yellow | district center |
| `H` | yellow | housing |
| `F` | cyan | farmhouse |
| `M` | white | lumber mill |
| `E` | bright yellow | power |
| `L` | red | lumberjack |
| `G` | magenta | gatherer |
| `K` | red | hauling |
| `P` | bright blue | pump |
| `W` | bright blue | tank |
| `X` | cyan | floodgate/dam |
| `T` | green | grown tree |
| `t` | dim green | seedling |
| `B` | magenta | berry bush |
| `k` | bright green | kohlrabi |
| `c` | bright green | carrot |

