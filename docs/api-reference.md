# Timberbot API Reference

## Overview

| | |
|---|---|
| **Base URL** | `http://localhost:8085` |
| **Content-Type** | `application/json` |
| **Authentication** | None |
| **CORS** | `Access-Control-Allow-Origin: *` |

### Output Format

Some endpoints support two output formats via `?format=` query param or `"format"` in POST body. Endpoints that support both formats are noted in their sections. All others return a single JSON shape.

| Format | Description |
|--------|-------------|
| `toon` | Default. Flat tabular data for [TOON format](https://github.com/toon-format/toon) output. Requires `pip install toons` |
| `json` | Full nested data for programmatic access |

### Error Format

All errors return JSON with an `error` field:

```json
{"error": "description of what went wrong"}
```

!!! info "Error context"
    Some errors include additional fields for debugging: `id`, `building`, `available`, `scienceCost`, `currentPoints`, etc.

### Python CLI

All HTTP endpoints are accessible via the Python client:

```bash
python timberbot.py <command>              # TOON format
python timberbot.py --json <command>       # JSON format
python timberbot.py <command> key:value    # with parameters
```

---

## Game State

### GET /api/ping

Health check. Answered on listener thread (works even when game is paused/loading).

**CLI:** `python timberbot.py ping`

#### Response

| Field | Type | Description |
|-------|------|-------------|
| status | string | Always `"ok"` |
| ready | bool | Always `true` |

```json
{"status": "ok", "ready": true}
```

---

### GET /api/summary

Full game state snapshot: time, weather, population, resources, trees, housing, employment, wellbeing, science, and alerts.

**CLI:** `python timberbot.py summary` | `python timberbot.py --json summary`

#### Response (format=json)

| Field | Type | Description |
|-------|------|-------------|
| time | object | See [GET /api/time](#get-apitime) |
| weather | object | See [GET /api/weather](#get-apiweather) |
| districts | array | See [GET /api/districts](#get-apidistricts) |
| trees | object | `{markedGrown, markedSeedling, unmarkedGrown}` |
| housing | object | `{occupiedBeds, totalBeds, homeless}` |
| employment | object | `{assigned, vacancies, unemployed}` |
| wellbeing | object | `{average, miserable, critical}` |
| science | int | Current science points |
| alerts | object | `{unstaffed, unpowered, unreachable}` counts |

??? example "Example response"

    ```json
    {
      "time": {"dayNumber": 42, "dayProgress": 0.65, "partialDayNumber": 42.65},
      "weather": {"cycle": 3, "cycleDay": 5, "isHazardous": false, "temperateWeatherDuration": 12, "hazardousWeatherDuration": 6, "cycleLengthInDays": 18},
      "districts": [{"name": "District 1", "population": {"adults": 20, "children": 5, "bots": 2}, "resources": {"Water": {"available": 150, "all": 200}, "Log": {"available": 80, "all": 80}}}],
      "trees": {"markedGrown": 5, "markedSeedling": 2, "unmarkedGrown": 120},
      "housing": {"occupiedBeds": 25, "totalBeds": 30, "homeless": 0},
      "employment": {"assigned": 18, "vacancies": 22, "unemployed": 2},
      "wellbeing": {"average": 12.3, "miserable": 0, "critical": 1},
      "science": 450,
      "alerts": {"unstaffed": 3, "unpowered": 1, "unreachable": 0}
    }
    ```

#### Response (format=toon)

Flat key-value pairs including `day`, `dayProgress`, `cycle`, `cycleDay`, `isHazardous`, `tempDays`, `hazardDays`, `markedGrown`, `markedSeedling`, `unmarkedGrown`, `adults`, `children`, `bots`, resource stocks (e.g. `Water`, `Log`), `foodDays`, `waterDays`, `beds`, `homeless`, `workers`, `unemployed`, `wellbeing`, `miserable`, `critical`, `science`, `alerts`.

---

### GET /api/time

Current day number and progress.

**CLI:** `python timberbot.py time`

#### Response

| Field | Type | Description |
|-------|------|-------------|
| dayNumber | int | Current day number |
| dayProgress | float | Progress through current day (0.0-1.0) |
| partialDayNumber | float | Fractional day number |

```json
{"dayNumber": 42, "dayProgress": 0.65, "partialDayNumber": 42.65}
```

---

### GET /api/weather

Current weather cycle and drought info.

**CLI:** `python timberbot.py weather`

#### Response

| Field | Type | Description |
|-------|------|-------------|
| cycle | int | Current cycle number |
| cycleDay | int | Day within current cycle |
| isHazardous | bool | Currently in drought/badtide |
| temperateWeatherDuration | int | Days of temperate weather this cycle |
| hazardousWeatherDuration | int | Days of hazardous weather this cycle |
| cycleLengthInDays | int | Total cycle length |

```json
{
  "cycle": 3,
  "cycleDay": 5,
  "isHazardous": false,
  "temperateWeatherDuration": 12,
  "hazardousWeatherDuration": 6,
  "cycleLengthInDays": 18
}
```

---

### GET /api/speed

Current game speed. Answered on listener thread (works when paused).

**CLI:** `python timberbot.py speed`

#### Response

| Field | Type | Description |
|-------|------|-------------|
| speed | int | 0=pause, 1=normal, 2=fast, 3=fastest |

```json
{"speed": 1}
```

---

### POST /api/speed

Set game speed.

**CLI:** `python timberbot.py set_speed speed:2`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| speed | int | yes | 0=pause, 1=normal, 2=fast, 3=fastest |

```json
{"speed": 2}
```

#### Response (success)

```json
{"speed": 2}
```

#### Response (error)

```json
{"error": "speed must be 0-3 (0=pause, 1=normal, 2=fast, 3=fastest)"}
```

---

### GET /api/workhours

Current work schedule.

**CLI:** `python timberbot.py workhours`

#### Response

| Field | Type | Description |
|-------|------|-------------|
| endHours | int | Hour when work ends (1-24) |
| areWorkingHours | bool | Whether beavers are currently working |

```json
{"endHours": 16, "areWorkingHours": true}
```

---

### POST /api/workhours

Set when beavers stop working.

**CLI:** `python timberbot.py set_workhours end_hours:14`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| endHours | int | yes | When work ends (1-24) |

#### Response (success)

```json
{"endHours": 14}
```

#### Response (error)

```json
{"error": "endHours must be 1-24"}
```

---

### GET /api/notifications

Game event history.

**CLI:** `python timberbot.py notifications`

#### Response

Array of notification objects.

| Field | Type | Description |
|-------|------|-------------|
| subject | string | Event title |
| description | string | Event details |
| cycle | int | Cycle when event occurred |
| cycleDay | int | Day within cycle |

```json
[
  {"subject": "Drought ended", "description": "The drought is over.", "cycle": 2, "cycleDay": 18}
]
```

---

### GET /api/alerts

Buildings with problems: unstaffed, unpowered, unreachable, or status messages.

**CLI:** `python timberbot.py alerts`

#### Response

Array of alert objects.

| Field | Type | Description |
|-------|------|-------------|
| type | string | `"unstaffed"`, `"unpowered"`, `"unreachable"`, or `"status"` |
| id | int | Building instance ID |
| name | string | Building name |
| workers | string | (unstaffed only) `"assigned/desired"` |
| status | string | (status only) Status description |

```json
[
  {"type": "unstaffed", "id": 12340, "name": "LumberjackFlag", "workers": "0/1"},
  {"type": "unpowered", "id": 12350, "name": "Gristmill"},
  {"type": "unreachable", "id": 12360, "name": "SmallWarehouse"}
]
```

---

## Districts & Resources

### GET /api/population

Beaver and bot counts per district.

**CLI:** `python timberbot.py population`

#### Response

Array of district population objects.

| Field | Type | Description |
|-------|------|-------------|
| district | string | District name |
| adults | int | Adult beaver count |
| children | int | Child beaver count |
| bots | int | Mechanical beaver count |

```json
[
  {"district": "District 1", "adults": 20, "children": 5, "bots": 2}
]
```

---

### GET /api/resources

Resource stocks per district.

**CLI:** `python timberbot.py resources` | `python timberbot.py --json resources`

#### Response (format=toon)

Flat array of `{district, good, available, all}` per resource with stock > 0.

```json
[
  {"district": "District 1", "good": "Water", "available": 150, "all": 200},
  {"district": "District 1", "good": "Log", "available": 80, "all": 80}
]
```

#### Response (format=json)

Keyed by district name, each containing resource objects with `{available, all}`.

```json
{
  "District 1": {
    "Water": {"available": 150, "all": 200},
    "Log": {"available": 80, "all": 80}
  }
}
```

---

### GET /api/districts

Districts with population and resource data combined.

**CLI:** `python timberbot.py districts` | `python timberbot.py --json districts`

#### Response (format=json)

| Field | Type | Description |
|-------|------|-------------|
| name | string | District name |
| population | object | `{adults, children, bots}` |
| resources | object | Keyed by good name: `{available, all}` |

```json
[
  {
    "name": "District 1",
    "population": {"adults": 20, "children": 5, "bots": 2},
    "resources": {"Water": {"available": 150, "all": 200}, "Log": {"available": 80, "all": 80}}
  }
]
```

#### Response (format=toon)

Flat rows with `name`, `adults`, `children`, `bots`, plus one key per resource with stock > 0.

---

### GET /api/distribution

Import/export settings per good per district.

**CLI:** `python timberbot.py distribution`

#### Response

| Field | Type | Description |
|-------|------|-------------|
| district | string | District name |
| goods | array | Per-good settings |
| goods[].good | string | Good name |
| goods[].importOption | string | `"None"`, `"Allowed"`, or `"Forced"` |
| goods[].exportThreshold | int | Export when stock exceeds this |

```json
[
  {
    "district": "District 1",
    "goods": [
      {"good": "Water", "importOption": "Allowed", "exportThreshold": 50},
      {"good": "Log", "importOption": "None", "exportThreshold": 0}
    ]
  }
]
```

---

### POST /api/distribution

Set import/export for a specific good in a district.

**CLI:** `python timberbot.py set_distribution district:"District 1" good:Log import_option:Forced export_threshold:50`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| district | string | yes | District name |
| good | string | yes | Good name (e.g. `"Log"`) |
| import | string | no | `"None"`, `"Allowed"`, or `"Forced"` |
| exportThreshold | int | no | Export threshold (-1 to skip) |

#### Response (success)

```json
{"district": "District 1", "good": "Log", "importOption": "Forced", "exportThreshold": 50}
```

#### Response (error)

```json
{"error": "district not found", "district": "Bad Name"}
```

---

### POST /api/district/migrate

Move adult beavers between districts.

**CLI:** `python timberbot.py migrate from_district:"District 1" to_district:"District 2" count:3`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| from | string | yes | Source district name |
| to | string | yes | Destination district name |
| count | int | yes | Number of adults to move |

#### Response (success)

```json
{"from": "District 1", "to": "District 2", "migrated": 3}
```

#### Response (error)

```json
{"error": "from district not found", "from": "Bad Name"}
```

```json
{"error": "no population to migrate", "from": "District 1", "available": 0}
```

---

## Entities

### GET /api/buildings

All placed buildings with state.

**CLI:** `python timberbot.py buildings` | `python timberbot.py --json buildings`

Pagination available in Python client only: `bot.buildings(limit=10, offset=20)`

#### Response (format=json)

Each building includes all applicable fields (absent fields mean the component doesn't exist on that building):

| Field | Type | Description |
|-------|------|-------------|
| id | int | Unity instance ID (ephemeral per session) |
| name | string | Building name (cleaned of faction suffix) |
| x, y, z | int | Origin coordinates |
| orientation | int | 0=south, 1=west, 2=north, 3=east |
| finished | bool | Construction complete |
| pausable | bool | Can be paused |
| paused | bool | Currently paused |
| floodgate | bool | Is a floodgate |
| height | float | (floodgate) Current gate height |
| maxHeight | float | (floodgate) Maximum gate height |
| constructionPriority | string | Priority while building |
| workplacePriority | string | Priority when finished |
| maxWorkers | int | Maximum worker slots |
| desiredWorkers | int | Desired worker count |
| assignedWorkers | int | Currently assigned workers |
| reachable | bool | Connected to district via paths |
| powered | bool | Has power (mechanical buildings) |
| isGenerator | bool | Generates power |
| isConsumer | bool | Consumes power |
| nominalPowerInput | int | Rated power input |
| nominalPowerOutput | int | Rated power output |
| powerDemand | int | Current grid power demand |
| powerSupply | int | Current grid power supply |
| buildProgress | float | Construction time progress (0-1) |
| materialProgress | float | Material delivery progress (0-1) |
| hasMaterials | bool | Has materials to resume |
| inventory | object | Goods in stock: `{"Log": 5, "Plank": 3}` |
| statuses | array | Active status messages |
| isWonder | bool | Is a monument/wonder |
| wonderActive | bool | Wonder is activated |
| dwellers | int | Current residents |
| maxDwellers | int | Max residents |
| isClutch | bool | Has a clutch mechanism |
| clutchEngaged | bool | Clutch is engaged |
| entranceX, entranceY, entranceZ | int | Doorstep coordinates |

```json
[
  {
    "id": 12340,
    "name": "LumberjackFlag",
    "x": 120, "y": 130, "z": 2,
    "orientation": 0,
    "finished": true,
    "pausable": true,
    "paused": false,
    "workplacePriority": "Normal",
    "maxWorkers": 1,
    "desiredWorkers": 1,
    "assignedWorkers": 1,
    "reachable": true,
    "entranceX": 120, "entranceY": 129, "entranceZ": 2
  }
]
```

#### Response (format=toon)

Flat rows with: `id`, `name`, `x`, `y`, `z`, `orientation`, `finished`, `paused`, `priority`, `workers` (as `"assigned/desired"`).

---

### GET /api/trees

All trees (alive and dead).

**CLI:** `python timberbot.py trees`

Pagination available in Python client only: `bot.trees(limit=50)`

#### Response

| Field | Type | Description |
|-------|------|-------------|
| id | int | Instance ID |
| name | string | Tree species name |
| x, y, z | int | Coordinates |
| alive | bool | Not a dead stump |
| marked | bool | Marked for cutting |
| grown | bool | Fully grown |
| growth | float | Growth progress (0.0-1.0) |

```json
[
  {"id": 45600, "name": "Pine", "x": 115, "y": 140, "z": 2, "alive": true, "marked": false, "grown": true, "growth": 1.0}
]
```

---

### GET /api/gatherables

Berry bushes and other gatherable resources.

**CLI:** `python timberbot.py gatherables`

#### Response

| Field | Type | Description |
|-------|------|-------------|
| id | int | Instance ID |
| name | string | Resource name |
| x, y, z | int | Coordinates |
| alive | bool | Not dead |

```json
[
  {"id": 45700, "name": "BlueberryBush", "x": 110, "y": 135, "z": 2, "alive": true}
]
```

---

### GET /api/beavers

All beavers with wellbeing and needs.

**CLI:** `python timberbot.py beavers` | `python timberbot.py --json beavers`

Pagination available in Python client only: `bot.beavers(limit=10)`

#### Response (format=json)

| Field | Type | Description |
|-------|------|-------------|
| id | int | Instance ID |
| name | string | Beaver name |
| wellbeing | float | Wellbeing score |
| needs | object | Active needs: `{"Hunger": {"points": 0.8, "isCritical": false, "isBelowWarning": false}}` |
| anyCritical | bool | Any need below warning threshold |
| lifeProgress | float | Age progress (0.0-1.0) |
| workplace | string | Assigned workplace name (empty if none) |
| isBot | bool | Mechanical beaver |
| contaminated | bool | Contaminated by badwater |
| hasHome | bool | Has assigned dwelling |

```json
[
  {
    "id": 78900,
    "name": "Bucky",
    "wellbeing": 14.2,
    "needs": {
      "Hunger": {"points": 0.8, "isCritical": false, "isBelowWarning": false},
      "Thirst": {"points": 0.6, "isCritical": false, "isBelowWarning": false}
    },
    "anyCritical": false,
    "lifeProgress": 0.35,
    "workplace": "LumberjackFlag",
    "isBot": false,
    "contaminated": false,
    "hasHome": true
  }
]
```

#### Response (format=toon)

Flat rows with: `id`, `name`, `wellbeing`, `tier` (ecstatic/happy/okay/unhappy/miserable), `isBot`, `workplace`, `critical` (need names joined with `+`).

Tier thresholds: >= 16 ecstatic, >= 12 happy, >= 8 okay, >= 4 unhappy, < 4 miserable.

---

### GET /api/prefabs

All building templates with dimensions.

**CLI:** `python timberbot.py prefabs`

#### Response

| Field | Type | Description |
|-------|------|-------------|
| name | string | Prefab name (e.g. `"LumberjackFlag.IronTeeth"`) |
| sizeX | int | Width |
| sizeY | int | Depth |
| sizeZ | int | Height |

```json
[
  {"name": "LumberjackFlag.IronTeeth", "sizeX": 2, "sizeY": 2, "sizeZ": 1}
]
```

---

### GET /api/tree_clusters

Top 5 clusters of grown trees by density.

**CLI:** `python timberbot.py tree_clusters`

#### Response

| Field | Type | Description |
|-------|------|-------------|
| x | int | Cluster center X |
| y | int | Cluster center Y |
| z | int | Cluster Z level |
| grown | int | Fully grown trees in cluster |
| total | int | Total trees in cluster |

```json
[
  {"x": 125, "y": 145, "z": 2, "grown": 15, "total": 22}
]
```

---

## Map & Terrain

### GET /api/map

Returns map dimensions when called without parameters.

**CLI:** `python timberbot.py map` (returns map size only)

#### Response

```json
{"mapSize": {"x": 256, "y": 256, "z": 22}}
```

---

### POST /api/map

Terrain, water, occupants, and contamination for a rectangular region.

**CLI:** `python timberbot.py map x1:100 y1:100 x2:110 y2:110`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| x1 | int | yes | Region min X |
| y1 | int | yes | Region min Y |
| x2 | int | yes | Region max X |
| y2 | int | yes | Region max Y |

#### Response

| Field | Type | Description |
|-------|------|-------------|
| mapSize | object | `{x, y, z}` total map dimensions |
| region | object | `{x1, y1, x2, y2}` clamped region |
| tiles | array | Per-tile data |
| tiles[].x, y | int | Tile coordinates |
| tiles[].terrain | int | Terrain height |
| tiles[].water | float | Water height |
| tiles[].badwater | float | (optional) Water contamination 0-1 |
| tiles[].occupant | string | (optional) Building/entity name |
| tiles[].entrance | bool | (optional) Is an entrance tile |
| tiles[].seedling | bool | (optional) Has a seedling |
| tiles[].dead | bool | (optional) Dead tree stump (buildable) |
| tiles[].contaminated | bool | (optional) Soil contamination |
| tiles[].moist | bool | (optional) Irrigated soil |

```json
{
  "mapSize": {"x": 256, "y": 256, "z": 22},
  "region": {"x1": 100, "y1": 100, "x2": 110, "y2": 110},
  "tiles": [
    {"x": 100, "y": 100, "terrain": 2, "water": 0.0},
    {"x": 100, "y": 101, "terrain": 2, "water": 1.5, "badwater": 0.3},
    {"x": 100, "y": 102, "terrain": 2, "water": 0.0, "occupant": "Path", "moist": true}
  ]
}
```

---

### POST /api/scan

Occupied and water tiles in a circular area, skipping empty ground.

**CLI:** `python timberbot.py scan x:122 y:136 radius:10`

#### Request Body

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| x | int | no | 128 | Center X |
| y | int | no | 128 | Center Y |
| radius | int | no | 10 | Scan radius |

#### Response

| Field | Type | Description |
|-------|------|-------------|
| center | string | `"x,y"` center coordinates |
| radius | int | Scan radius |
| default | string | Always `"ground"` (tiles not listed are empty ground) |
| occupied | array | `[{x, y, what}]` -- entity name with suffixes |
| water | array | `[{x, y}]` or `[{x, y, badwater}]` for contaminated |

Occupant suffixes: `.dead` = dead stump (buildable), `.seedling` = growing, `.entrance` = building entrance.

```json
{
  "center": "122,136",
  "radius": 10,
  "default": "ground",
  "occupied": [
    {"x": 119, "y": 131, "what": "SmallTank.entrance"},
    {"x": 120, "y": 133, "what": "Path"},
    {"x": 123, "y": 138, "what": "Kohlrabi.seedling"}
  ],
  "water": [
    {"x": 122, "y": 131},
    {"x": 123, "y": 131, "badwater": 0.45}
  ]
}
```

---

## Science

### GET /api/science

Science points and unlockable buildings.

**CLI:** `python timberbot.py science`

#### Response

| Field | Type | Description |
|-------|------|-------------|
| points | int | Current science points |
| unlockables | array | Buildings that cost science |
| unlockables[].name | string | Building prefab name |
| unlockables[].cost | int | Science cost |
| unlockables[].unlocked | bool | Already unlocked |

```json
{
  "points": 450,
  "unlockables": [
    {"name": "Gristmill.IronTeeth", "cost": 200, "unlocked": true},
    {"name": "Engine.IronTeeth", "cost": 600, "unlocked": false}
  ]
}
```

---

### POST /api/science/unlock

Unlock a building using science points. Matches the exact UI flow (cost deduction + events + UI refresh).

**CLI:** `python timberbot.py unlock_building building:"Engine.IronTeeth"`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| building | string | yes | Prefab name from `GET /api/science` |

#### Response (success)

```json
{"building": "Engine.IronTeeth", "unlocked": true, "remaining": 250}
```

#### Response (already unlocked)

```json
{"building": "Engine.IronTeeth", "unlocked": true, "remaining": 450, "note": "already unlocked"}
```

#### Response (error -- insufficient points)

```json
{"error": "not enough science", "building": "Engine.IronTeeth", "scienceCost": 600, "currentPoints": 450}
```

#### Response (error -- not found)

```json
{"error": "building not found in toolbar", "building": "BadName"}
```

---

## Building Actions

### POST /api/building/pause

Pause or unpause a building.

**CLI:** `python timberbot.py pause_building building_id:12340` | `python timberbot.py unpause_building building_id:12340`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | int | yes | Building instance ID |
| paused | bool | yes | `true` to pause, `false` to unpause |

#### Response (success)

```json
{"id": 12340, "name": "LumberjackFlag", "paused": true}
```

#### Response (error)

```json
{"error": "building not found", "id": 99999}
```

```json
{"error": "building is not pausable", "id": 12340}
```

---

### POST /api/building/demolish

Remove a building from the world.

**CLI:** `python timberbot.py demolish_building building_id:12340`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | int | yes | Building instance ID |

#### Response (success)

```json
{"id": 12340, "name": "LumberjackFlag", "demolished": true}
```

#### Response (error)

```json
{"error": "entity not found", "id": 99999}
```

---

### POST /api/building/place

Place a building in the world. Validates all tiles before placing: occupancy, terrain height, water, unlock status, underground clipping. Coordinates refer to the bottom-left corner regardless of orientation.

**CLI:** `python timberbot.py place_building prefab:Path x:120 y:130 z:2 orientation:south`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| prefab | string | yes | Prefab name from `GET /api/prefabs` |
| x | int | yes | Bottom-left X |
| y | int | yes | Bottom-left Y |
| z | int | yes | Terrain height (must match) |
| orientation | int | yes | 0=south, 1=west, 2=north, 3=east |

!!! danger "Ghost buildings"
    Failed placements may create invisible entities. See [Known Issues](#known-issues).

#### Response (success)

```json
{"id": 12340, "name": "LumberjackFlag", "x": 120, "y": 130, "z": 2, "orientation": 0}
```

#### Response (error)

```json
{"error": "unknown prefab", "prefab": "BadName"}
```

```json
{"error": "building not unlocked", "prefab": "Engine.IronTeeth", "scienceCost": 600, "currentPoints": 450}
```

```json
{"error": "tile (121,131,2) already occupied", "prefab": "LumberjackFlag", "x": 120, "y": 130, "z": 2, "orientation": 0}
```

```json
{"error": "tile (120,130) is water", "prefab": "LumberjackFlag", "x": 120, "y": 130, "z": 2, "orientation": 0}
```

```json
{"error": "terrain too low at (120,130): height 1 < 2", "prefab": "LumberjackFlag", "x": 120, "y": 130, "z": 2, "orientation": 0}
```

---

### POST /api/placement/find

Find valid placements for a building within a rectangular area. Results sorted by: district reachability > path access > power adjacency > path count. Returns at most 10 results.

**CLI:** `python timberbot.py find_placement prefab:LumberjackFlag x1:110 y1:125 x2:130 y2:145`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| prefab | string | yes | Prefab name |
| x1 | int | yes | Search area min X |
| y1 | int | yes | Search area min Y |
| x2 | int | yes | Search area max X |
| y2 | int | yes | Search area max Y |

#### Response (success)

| Field | Type | Description |
|-------|------|-------------|
| prefab | string | Requested prefab |
| sizeX | int | Building width |
| sizeY | int | Building depth |
| placements | array | Valid positions (max 10) |
| placements[].x, y, z | int | Bottom-left coordinates |
| placements[].orientation | string | Best orientation name |
| placements[].pathAccess | bool | Adjacent to paths |
| placements[].pathCount | int | Number of adjacent path tiles |
| placements[].reachable | bool | Connected to district road network |
| placements[].nearPower | bool | Adjacent to power building |

```json
{
  "prefab": "LumberjackFlag.IronTeeth",
  "sizeX": 2, "sizeY": 2,
  "placements": [
    {"x": 120, "y": 130, "z": 2, "orientation": "south", "pathAccess": true, "pathCount": 2, "reachable": true, "nearPower": false}
  ]
}
```

#### Response (error)

```json
{"error": "unknown prefab", "prefab": "BadName"}
```

---

### POST /api/floodgate

Set floodgate water gate height. Value is clamped to max.

**CLI:** `python timberbot.py set_floodgate building_id:12340 height:1.5`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | int | yes | Building instance ID |
| height | float | yes | Desired gate height (clamped to 0-max) |

#### Response (success)

```json
{"id": 12340, "name": "Floodgate", "height": 1.5, "maxHeight": 3.0}
```

#### Response (error)

```json
{"error": "not a floodgate", "id": 12340}
```

---

### POST /api/priority

Set construction or workplace priority.

**CLI:** `python timberbot.py set_priority building_id:12340 priority:VeryHigh`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | int | yes | Building instance ID |
| priority | string | yes | `"VeryLow"`, `"Normal"`, or `"VeryHigh"` |
| type | string | no | `"construction"` or `"workplace"`. If empty, sets whichever exists |

#### Response (success)

```json
{"id": 12340, "name": "LumberjackFlag", "workplacePriority": "VeryHigh"}
```

```json
{"id": 12340, "name": "LumberjackFlag", "constructionPriority": "VeryHigh"}
```

#### Response (error)

```json
{"error": "invalid priority, use: VeryLow, Normal, VeryHigh", "value": "Bad"}
```

---

### POST /api/workers

Set desired worker count for a workplace.

**CLI:** `python timberbot.py set_workers building_id:12340 count:2`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | int | yes | Building instance ID |
| count | int | yes | Desired workers (clamped to 0-maxWorkers) |

#### Response (success)

```json
{"id": 12340, "name": "LumberjackFlag", "desiredWorkers": 2, "maxWorkers": 3, "assignedWorkers": 1}
```

#### Response (error)

```json
{"error": "not a workplace", "id": 12340}
```

---

### POST /api/hauling/priority

Prioritize hauling deliveries to a building.

**CLI:** `python timberbot.py set_haul_priority building_id:12340 prioritized:true`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | int | yes | Building instance ID |
| prioritized | bool | yes | `true` to prioritize, `false` to clear |

#### Response (success)

```json
{"id": 12340, "name": "SmallWarehouse", "haulPrioritized": true}
```

#### Response (error)

```json
{"error": "building has no haul priority", "id": 12340}
```

---

### POST /api/recipe

Set which recipe a manufactory produces.

**CLI:** `python timberbot.py set_recipe building_id:12340 recipe:PlankRecipe`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | int | yes | Building instance ID |
| recipe | string | yes | Recipe ID, or `"none"` to clear |

#### Response (success)

```json
{"id": 12340, "name": "LumberMill", "recipe": "PlankRecipe"}
```

```json
{"id": 12340, "name": "LumberMill", "recipe": "none"}
```

#### Response (error -- recipe not found)

```json
{"error": "recipe not found", "recipeId": "BadRecipe", "available": ["PlankRecipe", "TreatedPlankRecipe"]}
```

---

### POST /api/farmhouse/action

Prioritize planting or harvesting for a farmhouse.

**CLI:** `python timberbot.py set_farmhouse_action building_id:12340 action:planting`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | int | yes | Building instance ID |
| action | string | yes | `"planting"` or `"harvesting"` (harvesting = default behavior) |

#### Response (success)

```json
{"id": 12340, "name": "FarmHouse", "action": "planting"}
```

```json
{"id": 12340, "name": "FarmHouse", "action": "default"}
```

#### Response (error)

```json
{"error": "building is not a farmhouse", "id": 12340}
```

```json
{"error": "invalid action, use: planting or harvesting", "action": "bad"}
```

---

### POST /api/plantable/priority

Prioritize which tree/resource type a forester plants.

**CLI:** `python timberbot.py set_plantable_priority building_id:12340 plantable:Pine`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | int | yes | Building instance ID |
| plantable | string | yes | Plantable template name, or `"none"` to clear |

#### Response (success)

```json
{"id": 12340, "name": "Forester", "prioritized": "Pine"}
```

```json
{"id": 12340, "name": "Forester", "prioritized": "none"}
```

#### Response (error -- not found)

```json
{"error": "plantable not found", "plantableName": "BadTree", "available": ["Pine", "Birch", "Oak"]}
```

---

### POST /api/stockpile/capacity

Set maximum capacity on a stockpile.

**CLI:** `python timberbot.py set_capacity building_id:12340 capacity:100`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | int | yes | Building instance ID |
| capacity | int | yes | Max storage capacity |

#### Response (success)

```json
{"id": 12340, "name": "SmallWarehouse", "capacity": 100}
```

---

### POST /api/stockpile/good

Set which good a single-good stockpile accepts.

**CLI:** `python timberbot.py set_good building_id:12340 good:Log`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | int | yes | Building instance ID |
| good | string | yes | Good name (e.g. `"Log"`) |

#### Response (success)

```json
{"id": 12340, "name": "SmallWarehouse", "good": "Log"}
```

#### Response (error)

```json
{"error": "not a single-good stockpile", "id": 12340}
```

---

## Area Actions

### POST /api/cutting/area

Mark or clear a rectangular area for tree cutting.

**CLI:** `python timberbot.py mark_trees x1:110 y1:130 x2:120 y2:140 z:2` | `python timberbot.py clear_trees x1:110 y1:130 x2:120 y2:140 z:2`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| x1 | int | yes | Area min X |
| y1 | int | yes | Area min Y |
| x2 | int | yes | Area max X |
| y2 | int | yes | Area max Y |
| z | int | yes | Z level |
| marked | bool | yes | `true` to mark, `false` to clear |

#### Response

```json
{"x1": 110, "y1": 130, "x2": 120, "y2": 140, "z": 2, "marked": true, "tiles": 121}
```

---

### POST /api/planting/mark

Mark an area for crop planting. Validates tiles: skips occupied, water, and wrong terrain.

**CLI:** `python timberbot.py plant_crop x1:110 y1:130 x2:115 y2:135 z:2 crop:Carrot`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| x1 | int | yes | Area min X |
| y1 | int | yes | Area min Y |
| x2 | int | yes | Area max X |
| y2 | int | yes | Area max Y |
| z | int | yes | Z level |
| crop | string | yes | Crop name (see [Crop Names](#crop-names)) |

#### Response

```json
{"x1": 110, "y1": 130, "x2": 115, "y2": 135, "z": 2, "crop": "Carrot", "planted": 28, "skipped": 8}
```

---

### POST /api/planting/clear

Clear planting marks from an area.

**CLI:** `python timberbot.py clear_planting x1:110 y1:130 x2:115 y2:135 z:2`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| x1 | int | yes | Area min X |
| y1 | int | yes | Area min Y |
| x2 | int | yes | Area max X |
| y2 | int | yes | Area max Y |
| z | int | yes | Z level |

#### Response

```json
{"x1": 110, "y1": 130, "x2": 115, "y2": 135, "z": 2, "cleared": true, "tiles": 36}
```

---

### POST /api/path/route

Route a straight-line path from point A to point B, auto-placing stairs at z-level changes. Path must be axis-aligned (x1==x2 or y1==y2).

**CLI:** `python timberbot.py place_path x1:120 y1:130 x2:120 y2:145`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| x1 | int | yes | Start X |
| y1 | int | yes | Start Y |
| x2 | int | yes | End X |
| y2 | int | yes | End Y |

#### Response (success)

```json
{"placed": 12, "stairs": 1, "skipped": 3, "errors": null}
```

#### Response (error -- not straight)

```json
{"error": "path must be a straight line (x1==x2 or y1==y2)"}
```

---

## Python CLI Helpers

These are convenience methods in `timberbot.py` that have no direct HTTP equivalent.

### visual

Colored ASCII grid with terrain height display. Background shading encodes z-level, foreground characters represent entities.

```bash
python timberbot.py visual x:122 y:136 radius:10
```

| Char | Color | Meaning |
|------|-------|---------|
| `0`-`9` | dim (green if moist) | Empty ground (digit = z % 10, background band = tens) |
| `~` | blue | Water |
| `@` | white | Entrance |
| `=` | yellow | Path |
| `D` | bright yellow | District center |
| `H` | yellow | Housing (Rowhouse, Barrack, Lodge) |
| `R` | yellow | Breeding pod |
| `F` | cyan | Farmhouse |
| `f` | green | Forester |
| `M` | white | Lumber mill / wood workshop |
| `S` | white | Science (Inventor, Numbercruncher) |
| `E` | bright yellow | Power (wheel, shaft) |
| `L` | red | Lumberjack |
| `G` | magenta | Gatherer |
| `K` | red | Hauling |
| `$` | yellow | Warehouse / pile |
| `P` | bright blue | Pump |
| `W` | bright blue | Tank |
| `X` | cyan | Floodgate / dam / levee / sluice |
| `C` | red | Campfire |
| `T` | green | Grown tree (Pine, Birch, Oak, Maple, Chestnut) |
| `t` | dim green | Seedling |
| `B` | magenta | Berry bush |
| `k` | bright green | Kohlrabi |
| `c` | bright green | Carrot |
| `p` | bright green | Potato |
| `w` | bright green | Wheat |
| `a` | bright green | Cassava |
| `s` | bright green | Sunflower |
| `n` | bright green | Corn |
| `e` | bright green | Eggplant |
| `y` | bright green | Soybean |
| `o` | bright green | Canola |
| `l` | bright green | Cattail |
| `d` | bright green | Spadderdock |

Background bands: z=0-9 (dark grays), z=10-19 (medium grays), z=20-22 (bright).

### find (CLI-only)

Find entities by name and/or proximity.

```bash
python timberbot.py find source:buildings name:Pump x:120 y:130 radius:20
```

### debug (CLI + HTTP)

Generic reflection endpoint for inspecting and calling methods on any game service. Supports object chaining with `$`.

```bash
python timberbot.py debug target:help                              # list targets and services
python timberbot.py debug target:fields path:_navMeshService       # list members on a service
python timberbot.py debug target:get path:_scienceService.SciencePoints  # read a value
python timberbot.py debug target:call path:_navMeshService._nodeIdService method:GridToId arg0:120,142,2  # call a method
python timberbot.py debug target:call path:$ method:HasNode arg0:12345   # chain: call method on last result
```

Targets: `help`, `get`, `fields`, `call`. Path navigation supports `.field`, `.Property`, `.[N]` (list index), `.~TypeName` (GetComponent), `$` (last result). Args auto-parse to int, float, bool, string, Vector3Int, Vector3.

### place_path (CLI-only)

Route a straight-line path with auto-stairs. Wraps `POST /api/path/route`.

```bash
python timberbot.py place_path x1:120 y1:130 x2:120 y2:145
```

### watch (CLI-only)

Live terminal dashboard that polls every 3 seconds.

```bash
python timberbot.py watch
```

---

## Reference

### IDs and Names

- **Building IDs** are Unity `GameObject.GetInstanceID()` -- ephemeral, change every game session. Get current IDs from `GET /api/buildings`.
- **Prefab names** come from `GET /api/prefabs`. Include faction suffix (e.g. `"LumberjackFlag.IronTeeth"`).
- **Good names** match Timberborn internal names: `Water`, `Log`, `Plank`, `Berries`, `Bread`, etc.
- **Building names** in responses are cleaned: `(Clone)`, `.IronTeeth`, `.Folktails` suffixes removed.

### Priority Values

| Value | Description |
|-------|-------------|
| `VeryLow` | Lowest priority |
| `Normal` | Default |
| `VeryHigh` | Highest priority |

Two priority types exist per building: `construction` (while building) and `workplace` (when finished). Set both on new buildings.

### Orientation

| Value | Name | Description |
|-------|------|-------------|
| 0 | south | Default facing |
| 1 | west | 90 degrees clockwise |
| 2 | north | 180 degrees |
| 3 | east | 270 degrees clockwise |

Coordinates always refer to the bottom-left corner of the footprint regardless of orientation. Python CLI accepts names (`south`, `west`, `north`, `east` or `s`/`w`/`n`/`e`).

### Crop Names

`Kohlrabi`, `Cassava`, `Carrot`, `Potato`, `Wheat`, `Sunflower`, `Corn`, `Eggplant`, `Soybean`

### Import Options

| Value | Description |
|-------|-------------|
| `None` | No importing |
| `Allowed` | Import when needed |
| `Forced` | Always import |

---

## Known Issues

!!! bug "Ghost Buildings"
    `POST /api/building/place` may create ghost buildings on invalid spots. The `Place()` callback fires and creates an entity even when placement is invalid. Python-side validation blocks most cases, but multi-tile overlaps or bad terrain can still ghost.

    **Never test placement carelessly** -- every failed `Place()` may create a ghost that needs manual cleanup.
