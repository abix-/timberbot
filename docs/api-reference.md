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

### Pagination

List endpoints (buildings, beavers, trees, crops, gatherables, alerts, notifications) support server-side pagination via query params:

| Param | Default | Description |
|-------|---------|-------------|
| `limit` | 100 | Max items to return. `0` = unlimited (all items) |
| `offset` | 0 | Skip first N items |

When `limit > 0`, response wraps in a metadata object:

```json
{"total": 200, "offset": 50, "limit": 10, "items": [...]}
```

When `limit=0`, returns a flat array (backward compatible):

```json
[{...}, {...}, ...]
```

The Python CLI passes `limit=0` by default (AI/scripts typically want all data).

### Server-side Filtering

List endpoints also support server-side filtering via query params:

| Param | Description |
|-------|-------------|
| `name` | Case-insensitive substring match on entity name |
| `x` | X coordinate for proximity filter |
| `y` | Y coordinate for proximity filter |
| `radius` | Manhattan distance radius (requires x and y) |

Filters apply BEFORE pagination. The `total` in paginated responses reflects filtered count.

```
GET /api/buildings?name=Farm                    # all FarmHouses
GET /api/buildings?x=120&y=140&radius=20        # buildings near (120,140)
GET /api/trees?name=Pine&limit=10               # first 10 pine trees
GET /api/beavers?name=Bot&limit=0               # all bots (unlimited)
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

Flat key-value pairs including `day`, `dayProgress`, `cycle`, `cycleDay`, `isHazardous`, `tempDays`, `hazardDays`, `markedGrown`, `markedSeedling`, `unmarkedGrown`, `adults`, `children`, `bots`, resource stocks (e.g. `Water`, `Log`), `foodDays`, `waterDays`, `logDays`, `plankDays`, `gearDays`, `beds`, `homeless`, `workers`, `unemployed`, `wellbeing`, `miserable`, `critical`, `science`, `alerts`.

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

### GET /api/power

Power networks. Groups all powered buildings by their connected network. Buildings sharing a power network have the same supply and demand values.

**CLI:** `python timberbot.py power`

#### Response

| Field | Type | Description |
|-------|------|-------------|
| id | int | Network identity hash |
| supply | float | Current power supply (from generators with workers) |
| demand | float | Total power demand from consumers |
| buildings | array | Buildings on this network |
| buildings[].name | string | Building name |
| buildings[].id | int | Instance ID |
| buildings[].isGenerator | bool | Power source (wheel, engine) |
| buildings[].nominalOutput | float | Max power output capacity |
| buildings[].nominalInput | float | Power draw when running |

Networks with `supply < demand` are underpowered — powered buildings run intermittently. Isolated buildings (no power chain) appear as single-building networks with `supply: 0`.

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

Supports server-side pagination (`?limit=10&offset=20`) and filtering (`?name=Farm`). See [Pagination](#pagination) above.

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
| stock | int | Total items in all inventories |
| capacity | int | Total inventory capacity |
| recipes | array | Available recipe IDs for manufactories |
| currentRecipe | string | Active recipe ID (empty if none) |
| needsNutrients | bool | Breeding pod needs food delivered |
| nutrients | int | Breeding pod nutrient count |
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

Supports server-side pagination (`?limit=10`) and filtering (`?name=Pine`). See [Pagination](#pagination) above.

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

### GET /api/crops

All crops (Kohlrabi, Soybean, Corn, etc) with growth status.

**CLI:** `python timberbot.py crops`

#### Response

Same fields as `/api/trees`. Filters to crop species only.

```json
[
  {"id": 78900, "name": "Kohlrabi", "x": 125, "y": 141, "z": 2, "alive": true, "marked": false, "grown": false, "growth": 0.45}
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

All beavers and bots with wellbeing and needs.

**CLI:** `python timberbot.py beavers` | `python timberbot.py --json beavers`

Supports server-side pagination (`?limit=10`) and filtering (`?name=Bot`). See [Pagination](#pagination) above.

#### Detail modes

| Mode | Description |
|------|-------------|
| `detail=basic` (default) | Active needs only, compact |
| `detail=full` | All needs (active + inactive) with `group` category field |
| `detail=id:<id>` | Single beaver/bot by ID, all needs with `group` field |

**CLI:** `python timberbot.py beavers detail:full` | `python timberbot.py beavers detail:id:-12345`

Bots always show all 3 needs (Energy, ControlTower, Grease) regardless of detail mode.

#### Response (format=json)

| Field | Type | Description |
|-------|------|-------------|
| id | int | Instance ID |
| name | string | Beaver name |
| x, y, z | int | Grid position on the map |
| wellbeing | float | Wellbeing score |
| district | string | District name (e.g. "District 1") |
| needs | array | Per-need breakdown (see below) |
| needs[].id | string | Need name (Hunger, Thirst, Campfire, Scratcher, etc.) |
| needs[].points | float | Current points (0-1, higher = more satisfied) |
| needs[].wellbeing | int | Wellbeing contribution from this need |
| needs[].favorable | bool | Need is satisfied |
| needs[].critical | bool | Need is in critical state |
| needs[].group | string | Need category: BasicNeeds, Fun, Nutrition, Aesthetics, Awe, SocialLife, Boosts (detail=full only) |
| anyCritical | bool | Any need below warning threshold |
| lifeProgress | float | Age progress (0.0-1.0) |
| workplace | string | Assigned workplace name (empty if none) |
| isBot | bool | Mechanical beaver |
| carrying | string | (optional) Good being hauled (e.g. "Water", "Log") |
| carryAmount | int | (optional) Units being carried |
| liftingCapacity | int | Max carry capacity (detail=full only) |
| overburdened | bool | (optional, detail=full) Carrying heavy load, movement slowed |
| deterioration | float | (optional, bots only) Deterioration progress 0-1 (1 = fully degraded) |
| contaminated | bool | Contaminated by badwater |
| activity | string | Current status text (e.g. "Waiting for nutrients", "No available workers") |
| hasHome | bool | Has assigned dwelling |

**Bot needs:** Bots always show all 3 needs regardless of active state: `Energy` (charge from ChargingStation, drains 0.58/day), `ControlTower` (boost from ControlTower building, drains 72/day), `Grease` (from GreaseFactory, drains 0.2/day). Points range 0-1.

```json
[
  {
    "id": 78900,
    "name": "Bucky",
    "wellbeing": 14.2,
    "needs": [
      {"id": "Hunger", "points": 0.8, "wellbeing": 1, "favorable": true, "critical": false},
      {"id": "Thirst", "points": 0.6, "wellbeing": 1, "favorable": true, "critical": false}
    ],
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
| scienceCost | int | Science points to unlock (omitted if 0) |
| unlocked | bool | Whether unlocked (omitted if no science cost) |
| cost | array | Material cost: `[{"good": "Log", "amount": 2}]` |

```json
[
  {
    "name": "FarmHouse.IronTeeth", "sizeX": 2, "sizeY": 2, "sizeZ": 2,
    "cost": [{"good": "Log", "amount": 4}, {"good": "Plank", "amount": 2}]
  }
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

### POST /api/tiles

Terrain, water, occupants, and contamination for a rectangular region.

**CLI:** `python timberbot.py tiles x1:100 y1:100 x2:110 y2:110`

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
| tiles[].occupant | string | (optional) Building/entity name (single occupant) |
| tiles[].occupants | array | (optional) Multiple occupants at different z-levels: `[{name, z}, ...]`. Appears instead of `occupant` when buildings are stacked vertically |
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

## Wellbeing

### GET /api/wellbeing

Population wellbeing breakdown by category. Shows current vs max score for each need group across all beavers.

**CLI:** `python timberbot.py wellbeing`

#### Response

| Field | Type | Description |
|-------|------|-------------|
| beavers | int | Number of beavers counted |
| categories | array | Wellbeing categories |
| categories[].group | string | Category name (BasicNeeds, SocialLife, Fun, Nutrition, Aesthetics, Awe) |
| categories[].current | float | Average current wellbeing for this category |
| categories[].max | float | Average max possible wellbeing for this category |
| categories[].needs | array | Individual needs in this category |
| categories[].needs[].id | string | Need/building name |
| categories[].needs[].favorableWellbeing | float | Wellbeing bonus when satisfied |
| categories[].needs[].unfavorableWellbeing | float | Wellbeing penalty when unmet |

```json
{
  "beavers": 42,
  "categories": [
    {
      "group": "SocialLife",
      "current": 0,
      "max": 2,
      "needs": [
        {"id": "Campfire", "favorableWellbeing": 1, "unfavorableWellbeing": 0},
        {"id": "RooftopTerrace", "favorableWellbeing": 1, "unfavorableWellbeing": 0}
      ]
    }
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

### POST /api/building/clutch

Engage or disengage a clutch on a building.

**CLI:** `timberbot.py set_clutch building_id:12340 engaged:true`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | int | yes | Building instance ID |
| engaged | bool | yes | `true` to engage, `false` to disengage |

#### Response

```json
{"id": 12340, "name": "GravityBattery", "engaged": true}
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
| orientation | string | yes | south, west, north, east |

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
| placements[].flooded | bool | Water on footprint tiles. Flooded buildings are non-functional |

Results sorted by: non-flooded > reachable > pathAccess > nearPower > pathCount.

```json
{
  "prefab": "LumberjackFlag.IronTeeth",
  "sizeX": 2, "sizeY": 2,
  "placements": [
    {"x": 120, "y": 130, "z": 2, "orientation": "south", "pathAccess": true, "pathCount": 2, "reachable": true, "nearPower": false, "flooded": false}
  ]
}
```

#### Response (error)

```json
{"error": "unknown prefab", "prefab": "BadName"}
```

---

### POST /api/building/floodgate

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

### POST /api/building/priority

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

### POST /api/building/workers

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

### POST /api/building/hauling

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

### POST /api/building/recipe

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

### POST /api/building/farmhouse

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

### POST /api/building/plantable

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

### POST /api/planting/find

Find valid planting spots in an area or within a building's work range.

**CLI:** `timberbot.py find_planting crop:Kohlrabi building_id:-514366` or `timberbot.py find_planting crop:Kohlrabi x1:68 y1:128 x2:72 y2:132 z:2`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| crop | string | yes | Crop name (Kohlrabi, Pine, Birch, etc.) |
| building_id | int | no | Farmhouse/forester ID -- returns spots within building range |
| x1, y1, x2, y2, z | int | no | Area scan (used when building_id is 0) |

#### Response

```json
{
  "crop": "Kohlrabi",
  "spots": [
    {"x": 120, "y": 135, "z": 2, "moist": true, "planted": false},
    {"x": 121, "y": 135, "z": 2, "moist": true, "planted": true}
  ]
}
```

---

### POST /api/building/range

Get the work range tiles for a building. Same green circle the player sees when selecting a farmhouse, lumberjack, forester, gatherer, scavenger, or district center.

**CLI:** `timberbot.py building_range building_id:-514366`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | int | yes | Building ID |

#### Response

```json
{
  "id": -514366,
  "name": "FarmHouse",
  "tiles": 45,
  "moist": 32,
  "bounds": {"x1": 118, "y1": 130, "x2": 128, "y2": 145}
}
```

---

### POST /api/path/place

Route a straight-line path from point A to point B, auto-placing stairs at z-level changes. Path must be axis-aligned (x1==x2 or y1==y2). Checks science unlocks: stairs must be unlocked for any z-change, platforms must be unlocked for multi-level jumps (2+ z-levels). Skips z-changes with an error if the required building isn't unlocked.

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

## Webhooks

Push notifications for game events. See [webhooks.md](webhooks.md) for the full list of 68 events.

### POST /api/webhooks

Register a webhook URL.

**CLI:** `python timberbot.py register_webhook url:http://localhost:9000/events events:drought.start,beaver.died`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| url | string | yes | URL to receive POST notifications |
| events | array | no | Event names to subscribe to. Omit for all events |

```json
{"id": "wh_1", "url": "http://localhost:9000/events", "events": ["drought.start", "drought.end"]}
```

---

### GET /api/webhooks

List all registered webhooks.

**CLI:** `python timberbot.py list_webhooks`

```json
[{"Id": "wh_1", "Url": "http://localhost:9000/events", "events": ["drought.start", "drought.end"], "Disabled": false, "failures": 0}]
```

Webhooks deliver batched JSON arrays (one POST per flush, default every 200ms):
```json
[
  {"event": "drought.start", "day": 45, "timestamp": 1711300000, "data": {"duration": 8}},
  {"event": "beaver.died", "day": 45, "timestamp": 1711300000, "data": null}
]
```

---

### POST /api/webhooks/delete

Remove a webhook by ID.

**CLI:** `python timberbot.py unregister_webhook webhook_id:wh_1`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | string | yes | Webhook ID from registration |

---

## Debug

### POST /api/debug

Reflection-based inspector for game internals. Navigates object graphs, lists fields/properties/methods, and calls methods with arguments. Results can be chained with `$`.

**CLI:** `python timberbot.py debug target:help`

#### Request Body

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| target | string | yes | `"help"`, `"get"`, `"fields"`, or `"call"` |
| path | string | varies | Dot-separated path from TimberbotService (e.g. `"_scienceService.SciencePoints"`) |
| filter | string | no | (fields only) Filter members by name substring |
| method | string | varies | (call only) Method name to invoke |
| arg0..argN | string | no | (call only) Method arguments. Vector3Int as `"x,y,z"`. `"$"` = last result |

#### Targets

| Target | Description | Required args |
|--------|-------------|---------------|
| `help` | List available targets, roots, and examples | none |
| `get` | Navigate an object chain and dump the result | `path` |
| `fields` | List fields, properties, and methods on an object | `path` (optional), `filter` (optional) |
| `call` | Call a method on an object with typed arguments | `path`, `method`, `arg0`..`argN` |

#### Response (help)

```json
{
  "targets": ["help -- this message", "get -- navigate object chain", "fields -- list members", "call -- call method"],
  "roots": ["_buildingService", "_entityRegistry", "_districtCenterRegistry", "_navMeshService", "..."],
  "examples": [
    "debug target:fields path:_navMeshService filter:Road",
    "debug target:get path:_scienceService.SciencePoints",
    "debug target:call path:_navMeshService method:AreConnectedRoadInstant arg0:120,142,2 arg1:130,142,2"
  ]
}
```

#### Path syntax

- Dot-separated: `_fieldName.PropertyName.NestedField`
- List indexing: `_entityRegistry.Entities.[0]`
- GetComponent: `~TypeName` (e.g. `~MechanicalNode`)
- Chain from last result: `$.PropertyName`

!!! warning "Debug only"
    This endpoint uses reflection on game internals. It may break on Timberborn updates. Not intended for production automation.

---

## Python CLI Helpers

These are convenience methods in `timberbot.py` that have no direct HTTP equivalent.

### map

Colored ASCII grid with terrain height display. Background shading encodes z-level, foreground characters represent entities.

```bash
python timberbot.py map x:122 y:136 radius:10
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
| `B` | magenta | Berry bush, shrub |
| `/` | yellow | Stairs |
| `_` | yellow | Platform |
| `m` | white | Metalsmith, smelter |
| `g` | white | Gear workshop |
| `b` | magenta | Bot assembler, bot part factory |
| `z` | magenta | Charging station |
| `V` | bright blue | Fluid dump |
| `v` | bright blue | Shower, swimming pool |
| `~` | green | Amenity (scratcher, bench, exercise plaza, medical bed) |
| `*` | red/yellow | Decoration (brazier, lantern, beaver bust) |
| `^` | dim | Roof |
| `R` | dim | Ruins, relics |
| `Q` | bright yellow | Wonders (monuments, flame, tribute, repopulator) |
| `A` | bright blue | Aquifer drill |
| `i` | dim | Automation (lever, sensor, timer, memory, relay) |
| `x` | red | Explosives (dynamite, detonator) |
| `#` | dim | Terrain block, dirt excavator |
| `!` | yellow | Banners, firework launcher |
| `\|` | dim | Fences (metal, wood) |
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

### place_path (CLI-only)

Route a straight-line path with auto-stairs. Wraps `POST /api/path/place`.

```bash
python timberbot.py place_path x1:120 y1:130 x2:120 y2:145
```

### top (CLI-only)

Live colony dashboard. Population, resources, weather, drought countdown, wellbeing breakdown, alerts.

```bash
python timberbot.py top
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
