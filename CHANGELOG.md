# Changelog

All notable changes to Timberbot are documented here. Links point to the commit where each feature was added.

[Unreleased]: https://github.com/abix-/TimberbornMods/compare/v0.6.0...HEAD

## [Unreleased]

### Breaking
- **Building endpoints moved under `/api/building/`**:
  - `/api/floodgate` -> `/api/building/floodgate`
  - `/api/priority` -> `/api/building/priority`
  - `/api/workers` -> `/api/building/workers`
  - `/api/recipe` -> `/api/building/recipe`
  - `/api/hauling/priority` -> `/api/building/hauling`
  - `/api/farmhouse/action` -> `/api/building/farmhouse`
  - `/api/plantable/priority` -> `/api/building/plantable`
- **Other endpoint renames**:
  - `/api/path/route` -> `/api/path/place`
  - `/api/map` (POST) -> `/api/tiles`
  - `/api/scan` removed (use `/api/map` GET or `/api/tiles`)
- **Error format changed**: `error` field is now `"code: detail"` (e.g. `"not_found"`, `"invalid_type: not a floodgate"`). Previously prose like `"building not found"`. Parse prefix before `:` for the code.
- **`/api/natural_resources` removed**: use `/api/trees` and `/api/crops` instead
- **List endpoints return paginated wrapper**: `{total, offset, limit, items:[...]}` instead of flat array. Use `limit=0` for old flat array behavior.

### Architecture
- Extract 8 classes from god object ([`8e0c841`][8e0c841], [`63655ec`][63655ec], [`6caf19c`][6caf19c], [`558b156`][558b156], [`55e7501`][55e7501], [`67904d6`][67904d6])
- TimberbotJw: fluent zero-alloc JSON writer ([`329d3ac`][329d3ac])
- TimberbotLog: file-based error logging ([`a73cf1a`][a73cf1a])
- Zero-alloc hot path confirmed ([`8b191b2`][8b191b2])
- Cached classes (struct to class, eliminate 144K field copies/sec) ([`13da06a`][13da06a])
- District population/resources cached in RefreshCachedState ([`e50c432`][e50c432])
- Faction detection via `FactionService.Current.Id` ([`9d29f3e`][9d29f3e])

### Features
- 68 webhook push events with 200ms batching and circuit breaker ([`ff4fb12`][ff4fb12], [`a9d5fcb`][a9d5fcb], [`f47484e`][f47484e])
- Separate `/api/trees` and `/api/crops` endpoints ([`c25de95`][c25de95])
- `/api/benchmark` endpoint ([`c24c4b5`][c24c4b5])
- Live `top` dashboard ([`cfec1a5`][cfec1a5])
- Flood validation in `find_placement` ([`04feca0`][04feca0])
- Server-side pagination on list endpoints ([`dea094e`][dea094e])
- Server-side name and proximity filtering ([`7c7fb80`][7c7fb80])
- Structured error codes: `"code: detail"` format ([`05b5a0d`][05b5a0d], [`24ba215`][24ba215])
- RoutePath validates stairs/platform unlock before placing ([`668a44b`][668a44b])
- Python client `TimberbotError` with `.code` and `.response` ([`1670124`][1670124])

### Fixes
- JsonWriter double-comma bug and UTF-8 BOM ([`38597be`][38597be], [`e65f7ed`][e65f7ed])
- Districts TOON format double-comma from `Raw(",")` + AutoSep
- Two placement error messages not migrated to structured error format
- `GetBuildingTemplate` throwing on unknown prefabs instead of returning structured error ([`4804450`][4804450])
- Eliminate all remaining anonymous object error returns ([`ee773ec`][ee773ec])

### Skill (timberbot.md v5.5)
- Reframe as human-AI collaborative play (not autonomous)
- Add webhook, crops, tiles, pagination, filtering to API table
- Add early game road network bootstrapping guide
- Add flood placement rules
- Add faction-aware building tables (Folktails, Iron Teeth, shared)
- Add per-faction wellbeing tables with all needs and bonuses
- Add per-faction crop tables with growth times and processing chains
- Add all 6 tree types with yields and faction-specific products
- Add manufacturing chains (logs->planks->gears->metal, extract, biofuel, paper/books)
- Add population growth mechanics (FT natural reproduction, IT breeding pod)
- Add bot mechanics (build chain, fuel, lifespan)
- Add weather cycles (temperate/drought/badtide, escalation)
- Add beaver lifecycle (lifespan, kit maturation, sleep/shelter)
- Add water management (dam/levee/floodgate, irrigation, aquifer drills)
- Add storage types and capacities
- Add district expansion mechanics
- Add structured error codes table
- Remove all behavioral directives, facts only

### Internal
- TimberbotJw `Result()`/`Error()` one-call builders ([`f97f8d8`][f97f8d8])
- `BeginArr`/`BeginObj`/`End` shortcuts, migrate 45 builders ([`0938b0c`][0938b0c])
- Migrate 200+ calls to Prop/Obj/Arr/RawProp ([`1fa9cd1`][1fa9cd1])
- Cache cross-validation: 3876 fields, 0 mismatches ([`f7990b9`][f7990b9])
- 100% endpoint schema coverage: 57 checks ([`b81e951`][b81e951])

[dea094e]: https://github.com/abix-/TimberbornMods/commit/dea094e
[7c7fb80]: https://github.com/abix-/TimberbornMods/commit/7c7fb80
[05b5a0d]: https://github.com/abix-/TimberbornMods/commit/05b5a0d
[668a44b]: https://github.com/abix-/TimberbornMods/commit/668a44b
[9d29f3e]: https://github.com/abix-/TimberbornMods/commit/9d29f3e
[e50c432]: https://github.com/abix-/TimberbornMods/commit/e50c432
[f97f8d8]: https://github.com/abix-/TimberbornMods/commit/f97f8d8
[0938b0c]: https://github.com/abix-/TimberbornMods/commit/0938b0c
[1fa9cd1]: https://github.com/abix-/TimberbornMods/commit/1fa9cd1
[f7990b9]: https://github.com/abix-/TimberbornMods/commit/f7990b9
[b81e951]: https://github.com/abix-/TimberbornMods/commit/b81e951
[8e0c841]: https://github.com/abix-/TimberbornMods/commit/8e0c841
[63655ec]: https://github.com/abix-/TimberbornMods/commit/63655ec
[6caf19c]: https://github.com/abix-/TimberbornMods/commit/6caf19c
[558b156]: https://github.com/abix-/TimberbornMods/commit/558b156
[55e7501]: https://github.com/abix-/TimberbornMods/commit/55e7501
[67904d6]: https://github.com/abix-/TimberbornMods/commit/67904d6
[329d3ac]: https://github.com/abix-/TimberbornMods/commit/329d3ac
[a73cf1a]: https://github.com/abix-/TimberbornMods/commit/a73cf1a
[8b191b2]: https://github.com/abix-/TimberbornMods/commit/8b191b2
[13da06a]: https://github.com/abix-/TimberbornMods/commit/13da06a
[ff4fb12]: https://github.com/abix-/TimberbornMods/commit/ff4fb12
[a9d5fcb]: https://github.com/abix-/TimberbornMods/commit/a9d5fcb
[f47484e]: https://github.com/abix-/TimberbornMods/commit/f47484e
[c25de95]: https://github.com/abix-/TimberbornMods/commit/c25de95
[c24c4b5]: https://github.com/abix-/TimberbornMods/commit/c24c4b5
[cfec1a5]: https://github.com/abix-/TimberbornMods/commit/cfec1a5
[24ba215]: https://github.com/abix-/TimberbornMods/commit/24ba215
[1670124]: https://github.com/abix-/TimberbornMods/commit/1670124
[4804450]: https://github.com/abix-/TimberbornMods/commit/4804450
[ee773ec]: https://github.com/abix-/TimberbornMods/commit/ee773ec
[04feca0]: https://github.com/abix-/TimberbornMods/commit/04feca0
[38597be]: https://github.com/abix-/TimberbornMods/commit/38597be
[e65f7ed]: https://github.com/abix-/TimberbornMods/commit/e65f7ed

## [v0.6.0] (2026-03-24)

Performance overhaul. Double-buffered caching, background GET serving, zero main-thread cost for reads.

### Breaking
- **`buildings` and `beavers` default to compact output**: use `detail:full` to get all fields (previously returned everything by default)
- **`watch` command renamed to `top`**

### Architecture
- Event-driven entity indexes via EventBus ([`22e1ef4`][22e1ef4])
- Double-buffered indexes, background GET serving ([`0dea90b`][0dea90b])
- All GETs on background listener thread ([`4582b96`][4582b96])
- Cached component refs, eliminate GetComponent per request ([`a8bfc58`][a8bfc58])
- Cached beavers with zero live GetComponent ([`cf64b52`][cf64b52])
- Cadenced cache refresh with `settings.json` config ([`17469fa`][17469fa])
- RefChanged helper, building coords to add-time ([`0a6ab2f`][0a6ab2f])
- DoubleBuffer\<T\> generic, JsonWriter helper ([`daf384d`][daf384d])

### Features
- Carried goods, bot durability, power networks, beaver position, district, map stacking, detail modes ([`79ccde1`][79ccde1])
- Resource projection: logDays, plankDays, gearDays ([`f0a3ccf`][f0a3ccf])
- Per-good inventory, recipes, liftingCapacity on beavers ([`c55ed30`][c55ed30])
- `manager` command: auto-manage haulers ([`15f5bcc`][15f5bcc])
- Live `top` dashboard replaces `watch` ([`7a758bf`][7a758bf])

### Fixes
- Pause/unpause uses game methods for proper UI icon ([`57e7323`][57e7323])
- Unemployed count uses adults only ([`f8b8bd2`][f8b8bd2])
- Double-buffer race condition on entity add/remove ([`f9a3ffe`][f9a3ffe])
- Shared reference-type fields between buffers ([`e781c3e`][e781c3e])
- Map occupant checks for new array format ([`71b8ebf`][71b8ebf])

### Skill (timberbot.md v4.7)
- Add detail modes for buildings and beavers (`detail:full`, `detail:id:<id>`)
- Add power networks, beaver position, district, map stacking to API table
- Add wellbeing building placement rules (effect radius, overlap, spreading)
- Add settings.json documentation

[v0.6.0]: https://github.com/abix-/TimberbornMods/releases/tag/v0.6.0
[22e1ef4]: https://github.com/abix-/TimberbornMods/commit/22e1ef4
[0dea90b]: https://github.com/abix-/TimberbornMods/commit/0dea90b
[4582b96]: https://github.com/abix-/TimberbornMods/commit/4582b96
[a8bfc58]: https://github.com/abix-/TimberbornMods/commit/a8bfc58
[cf64b52]: https://github.com/abix-/TimberbornMods/commit/cf64b52
[17469fa]: https://github.com/abix-/TimberbornMods/commit/17469fa
[0a6ab2f]: https://github.com/abix-/TimberbornMods/commit/0a6ab2f
[daf384d]: https://github.com/abix-/TimberbornMods/commit/daf384d
[79ccde1]: https://github.com/abix-/TimberbornMods/commit/79ccde1
[f0a3ccf]: https://github.com/abix-/TimberbornMods/commit/f0a3ccf
[c55ed30]: https://github.com/abix-/TimberbornMods/commit/c55ed30
[15f5bcc]: https://github.com/abix-/TimberbornMods/commit/15f5bcc
[7a758bf]: https://github.com/abix-/TimberbornMods/commit/7a758bf
[57e7323]: https://github.com/abix-/TimberbornMods/commit/57e7323
[f8b8bd2]: https://github.com/abix-/TimberbornMods/commit/f8b8bd2
[f9a3ffe]: https://github.com/abix-/TimberbornMods/commit/f9a3ffe
[e781c3e]: https://github.com/abix-/TimberbornMods/commit/e781c3e
[71b8ebf]: https://github.com/abix-/TimberbornMods/commit/71b8ebf

## [v0.5.5] (2026-03-24)

- Building material costs and unlock status on prefabs endpoint
- Per-building stock and capacity for tanks, warehouses, stockpiles
- Available recipes and current recipe on manufactories
- Breeding pod nutrient status
- Beaver activity from game status system
- Clutch engage/disengage endpoint
- Per-beaver need breakdown (every unmet need by name)
- `find_planting`: irrigated spots within farmhouse range or area
- `building_range`: work radius for farmhouse, lumberjack, forester, gatherer, scavenger, DC
- 118 integration tests
- Skill (timberbot.md v4.4): rewrite as game reference, add full API quick reference table, add clutch/prefabs/beavers fields

[v0.5.5]: https://github.com/abix-/TimberbornMods/releases/tag/v0.5.5

## [v0.5.3] (2026-03-24)

- Compass names for orientation everywhere (south, west, north, east)
- Remove number and single-letter orientation fallbacks
- PATH setup for timberbot.py CLI

[v0.5.3]: https://github.com/abix-/TimberbornMods/releases/tag/v0.5.3

## [v0.5.2] (2026-03-24)

- Wellbeing breakdown endpoint (per-category: Social, Fun, Nutrition, Aesthetics, Awe)
- Fix building placement for water buildings (SwimmingPool, DoubleShower)
- Fix crop planting validation to match player UI behavior
- Fix placement on dead standing trees
- 91 integration tests
- Skill: add wiki lookup guidance, "when you don't know something" section

[v0.5.2]: https://github.com/abix-/TimberbornMods/releases/tag/v0.5.2

## [v0.5.1] (2026-03-23)

- Fix unlock_building deducting science twice
- Skill: add wellbeing and worker management rules

[v0.5.1]: https://github.com/abix-/TimberbornMods/releases/tag/v0.5.1

## [v0.5.0] (2026-03-23)

- `find_placement`: valid building spots with reachability, path access, power adjacency
- `place_path`: auto-builds stairs and platforms for z-level changes
- `summary` includes `foodDays` and `waterDays` resource projections
- `map` returns `moist` field for irrigated tiles
- Generic `debug` endpoint for inspecting game internals via reflection
- Fix crash when reloading a save (HTTP server port conflict)
- `PlaceBuilding` validates stackable blocks as valid build surfaces
- 81 integration tests
- Skill v3.0: replace manual placement with `find_placement` workflow, add priority rules, food/water urgency

[v0.5.0]: https://github.com/abix-/TimberbornMods/releases/tag/v0.5.0

## [v0.4.8] (2026-03-23)

- Terrain height shading on map tiles (darker = lower, lighter = higher)
- Empty ground displays z-level digit instead of dots
- Height legend when multiple z-levels are in view
- Skill: use visual for placement checks instead of scan

[v0.4.8]: https://github.com/abix-/TimberbornMods/releases/tag/v0.4.8

## [v0.4.7] (2026-03-23)

- `--json` flag for full JSON output alongside default TOON format
- Summary includes housing, employment, wellbeing, science, alerts in one call
- New endpoints: hauler priority, manufactory recipes, farmhouse planting priority, forester tree priority
- Alerts, tree clusters, and scan now run server-side
- Clean names in all output (no more Clone/IronTeeth suffixes)
- Fix building unlock for all buildings
- Fix critical needs count (only truly low needs)
- 88 integration tests
- Skill: add hauler priority, recipe, farmhouse action, plantable priority; visual placement workflow

[v0.4.7]: https://github.com/abix-/TimberbornMods/releases/tag/v0.4.7

## [v0.4.6] (2026-03-22)

- Badwater detection: `badwater` field on water tiles (0-1 contamination)
- Soil contamination: `contaminated` field on land tiles near badwater
- Reject placement when z doesn't match terrain height
- Dead trees (stumps) no longer block placement
- Skill v3.0: add z-level rules, placement workflow, dead tree handling

[v0.4.6]: https://github.com/abix-/TimberbornMods/releases/tag/v0.4.6

## [v0.4.5] (2026-03-22)

- Science endpoint returns all 126 unlockable buildings with name, cost, unlock status
- `unlock_building` updates the game UI toolbar immediately
- Placing locked buildings blocked with error showing science cost
- 72 integration tests

[v0.4.5]: https://github.com/abix-/TimberbornMods/releases/tag/v0.4.5

## [v0.4.4] (2026-03-22)

- Separate `constructionPriority` and `workplacePriority` on buildings
- `set_priority` accepts `type:workplace` or `type:construction`

[v0.4.4]: https://github.com/abix-/TimberbornMods/releases/tag/v0.4.4

## [v0.4.3] (2026-03-22)

- Soil contamination on map tiles
- Per-building nominal power input/output
- District migration between districts
- Dwelling occupants (dwellers/maxDwellers)
- Clutch status on buildings
- Beaver home field
- Wellbeing tiers in TOON output
- 67 integration tests
- Skill: add alerts, notifications, workhours, pagination to bot loop

[v0.4.3]: https://github.com/abix-/TimberbornMods/releases/tag/v0.4.3

## [v0.4.2] (2026-03-22)

- Pagination on list endpoints (limit/offset)
- Beavers: isBot, contaminated fields
- Buildings: isWonder/wonderActive
- Work schedule read/write
- 60 integration tests

[v0.4.2]: https://github.com/abix-/TimberbornMods/releases/tag/v0.4.2

## [v0.4.1] (2026-03-22)

- Construction progress on buildings (buildProgress, materialProgress, hasMaterials)
- Building inventory contents
- Beaver workplace assignment
- Notifications endpoint
- Alerts helper (unstaffed, unpowered, unreachable)

[v0.4.1]: https://github.com/abix-/TimberbornMods/releases/tag/v0.4.1

## [v0.4.0] (2026-03-22)

- Science points endpoint, unlock buildings via API
- Distribution read/write (import/export per good per district)
- Buildings: reachable, powered, power network fields
- Tree cluster finder for optimal lumberjack placement
- AI playbook (docs/timberbot.md) works as Claude Code skill
- Fix beaver needs filter (only active needs)
- Fix speed scale to match game UI
- Skill v2.0: initial AI playbook with science, distribution, bot loop

[v0.4.0]: https://github.com/abix-/TimberbornMods/releases/tag/v0.4.0

## [v0.3.8] (2026-03-22)

- TOON format CLI output (compact, token-efficient for AI)
- `beavers` command with wellbeing and critical needs
- Requires `pip install toons` (falls back to JSON)

[v0.3.8]: https://github.com/abix-/TimberbornMods/releases/tag/v0.3.8

## [v0.3.7] (2026-03-22)

- Named orientations (south/west/north/east instead of numbers)
- Orientation origin correction for multi-tile buildings
- 39 integration tests

[v0.3.7]: https://github.com/abix-/TimberbornMods/releases/tag/v0.3.7

## [v0.3.6] (2026-03-22)

- Crop planting validation (skips buildings, water, invalid terrain)
- Initial regression test suite (test_validation.py)

[v0.3.6]: https://github.com/abix-/TimberbornMods/releases/tag/v0.3.6

## [v0.3.5] (2026-03-22)

- C#-side placement validation (occupancy, water, terrain, off-map checks)
- Orientation-aware footprint computation
- Demolition debris no longer blocks placement

[v0.3.5]: https://github.com/abix-/TimberbornMods/releases/tag/v0.3.5

## [v0.3.4] (2026-03-22)

- TOON format map output
- Colored roguelike map visualization
- Unique crop type letters

[v0.3.4]: https://github.com/abix-/TimberbornMods/releases/tag/v0.3.4

## [v0.3.3] (2026-03-22)

- Summary includes tree stats (marked grown, marked seedlings, unmarked grown)

[v0.3.3]: https://github.com/abix-/TimberbornMods/releases/tag/v0.3.3

## [v0.3.2] (2026-03-22)

- Full building footprints on map
- Seedling vs grown tree distinction
- Building entrance coordinates
- Planting fix: crops now appear in-game
- Water building placement validation

[v0.3.2]: https://github.com/abix-/TimberbornMods/releases/tag/v0.3.2

## [v0.3.1] (2026-03-22)

- Initial release with timberbot.py client included

[v0.3.1]: https://github.com/abix-/TimberbornMods/releases/tag/v0.3.1
