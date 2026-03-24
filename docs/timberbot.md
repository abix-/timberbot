---
name: timberbot
description: Play Timberborn autonomously via timberbot.py. Keep beavers alive, wellbeing high, needs met.
version: "4.5"
---
# Timberbot API - Game Reference

Play the game using `timberbot.py` commands only. NEVER use inline python or pipe through python -c. See [getting-started.md](getting-started.md) for PATH setup.

Beavers die if food or water hits 0.

## When you don't know something

**API reference:** Read `docs/api-reference.md` for all available endpoints, request/response formats, and parameters. Don't guess at endpoint syntax -- look it up.

**Game mechanics:** Search the Timberborn wiki FIRST before guessing:

- Use `WebSearch` for "timberborn wiki <topic>" (e.g. "timberborn wiki farmhouse range", "timberborn wiki wellbeing needs")
- Use `WebFetch` on wiki pages to read the details
- The wiki covers building stats, ranges, mechanics, and interactions that aren't in this prompt
- NEVER guess at game mechanics you haven't verified -- wrong assumptions cause colony deaths

## API quick reference

| Method | What it does |
|---|---|
| **Read state** | |
| `summary` | Colony snapshot: population, resources, weather, alerts, wellbeing |
| `beavers` | Per-beaver position (x,y,z), district, wellbeing, active needs. `detail:full` for all needs with group category, `detail:id:<id>` for single beaver/bot |
| `wellbeing` | Wellbeing by category with current/max |
| `buildings` | All buildings (compact). `detail:full` for all fields (effectRadius, productionProgress, readyToProduce, inventory, etc), `detail:id:<id>` for single building |
| `alerts` | Unstaffed, unpowered, unreachable buildings |
| `trees` | All cuttable trees with growth and marking status |
| `tree_clusters` | Densest grown tree clusters |
| `gatherables` | Berry bushes and other gatherable resources |
| `science` | Science points and unlock costs |
| `weather` | Drought countdown, hazardous status |
| `time` | Day number and progress |
| `speed` | Current game speed |
| `power` | Power networks: supply, demand, and buildings per connected network |
| `population` | Beaver/bot counts per district |
| `resources` | Resource stocks per district |
| `districts` | Multi-district overview with population and resources |
| `distribution` | Import/export settings per district per good |
| `notifications` | Game event history |
| `workhours` | Current work schedule |
| `prefabs` | Building templates with sizes, material costs, science cost |
| `ping` | Health check -- is the game running? |
| **Search/filter** | |
| `find source:buildings name:X` | Look up building IDs by name |
| `find source:buildings x:X y:Y radius:R` | Find buildings near a point |
| `find source:trees name:Pine` | Find specific tree types |
| `building_range building_id:X` | Work radius tiles for farmhouse, lumberjack, forester, gatherer |
| `scan x:X y:Y radius:10` | Quick area survey -- occupied + water tiles only |
| **Placement** | |
| `find_placement prefab:Name x1:X y1:Y x2:X2 y2:Y2` | Find valid building spots sorted by reachability |
| `find_planting crop:Kohlrabi building_id:X` | Find irrigated spots within farmhouse range |
| `place_building prefab:Name x:X y:Y z:Z orientation:south` | Place a building |
| `place_path x1:X y1:Y x2:X2 y2:Y2` | Roads + auto-stairs + platforms. Straight line only |
| `demolish_building building_id:X` | Remove a building |
| **Map** | |
| `visual x:X y:Y radius:10` | ASCII map with terrain height shading |
| `map x1:X y1:Y x2:X2 y2:Y2` | Raw terrain, water depth, badwater, occupants (with z-stacking), moisture |
| **Crops and trees** | |
| `plant_crop x1:X y1:Y x2:X2 y2:Y2 z:Z crop:Kohlrabi` | Mark area for planting |
| `clear_planting x1:X y1:Y x2:X2 y2:Y2 z:Z` | Clear planting marks |
| `mark_trees x1:X y1:Y x2:X2 y2:Y2 z:Z` | Mark trees for cutting |
| `clear_trees x1:X y1:Y x2:X2 y2:Y2 z:Z` | Unmark trees |
| **Building config** | |
| `set_priority building_id:X priority:VeryHigh type:construction` | Set construction or workplace priority |
| `set_workers building_id:X count:N` | Set desired worker count (0 to max) |
| `pause_building building_id:X` | Pause a building |
| `unpause_building building_id:X` | Resume a paused building |
| `set_good building_id:X good:Water` | Set allowed good on a tank/stockpile |
| `set_capacity building_id:X capacity:N` | Set stockpile max capacity |
| `set_haul_priority building_id:X prioritized:true` | Haulers deliver here first |
| `set_recipe building_id:X recipe:RecipeId` | Set manufactory recipe. Use invalid name to list available |
| `set_farmhouse_action building_id:X action:planting` | Prioritize planting or harvesting |
| `set_plantable_priority building_id:X plantable:Pine` | Forester/gatherer prioritizes this type |
| `set_floodgate building_id:X height:N` | Set floodgate water height |
| `set_clutch building_id:X engaged:true` | Engage/disengage power clutch |
| **Colony config** | |
| `set_speed speed:3` | Game speed: 0=pause, 1/2/3 |
| `set_workhours end_hours:20` | When work ends (1-24) |
| `set_distribution district:X good:Water import_option:Forced` | Import/export per good per district |
| `unlock_building building:Name.IronTeeth` | Unlock building with science points |
| `migrate from_district:X to_district:Y count:N` | Move beavers between districts |
| **Forbidden** | |
| `debug` | Reflection-based game internals inspector. Dev/testing only -- NEVER use during gameplay |

## Building placement

`find_placement` validates terrain height, occupancy, water, orientation, and path connectivity. Results sorted by: reachable > pathAccess > nearPower > pathCount. A result with `reachable: true` is connected to the district center via paths.

## Path and stair placement

`place_path` routes a straight-line path (axis-aligned: x1==x2 or y1==y2). It handles everything: auto-detects terrain height, places stairs at z-level changes, builds platforms for multi-level jumps, and skips occupied tiles. One call replaces dozens of individual `place_building` calls. Returns `{placed, stairs, skipped, errors}`. `place_building` with Path/Stairs prefabs does not handle z-transitions and will silently place at wrong heights.

## Z-level rules

- `visual` shows terrain height: empty ground shows z % 10 digit, background shading encodes height (dark=z0-9, medium=z10-19, bright=z20-22). Height legend at bottom shows exact z values. Use `map` for raw data when needed
- z MUST equal the terrain height at the placement location
- Placing at wrong z causes underground clipping (building invisible/broken)
- Different areas of the map have different terrain heights -- never assume z:2

## Orientation

Entrance directions on the visual map: **north** = +y (up), **south** = -y (down), **east** = +x (right), **west** = -x (left). Point the entrance FROM the building TOWARD the nearest path.

## Building priorities

Buildings have two priority types: `construction` (while building) and `workplace` (when finished). Values: VeryLow, Normal, VeryHigh.

## Building sizes

WoodWorkshop 2x4, HaulingPost 3x2, Barrack 3x2, DC 3x3, Rowhouse 1x2, FarmHouse 2x2, Inventor 2x2, Forester 2x2, flags 1x1, Path 1x1, LargePowerWheel 3x3, IndustrialLumberMill 2x3, DeepWaterPump 3x2, DoubleShower 1x2 (straddles water edge)

## Game mechanics

### Food
- Beavers eat ~1 food/day
- Wild berries are finite -- gatherers deplete them
- Kohlrabi: 3-day growth cycle, eaten raw (no processing needed)
- ~1 farmhouse per 8 beavers with full fields
- Crops need irrigated (moist) soil. Crops grow during drought if soil stays moist near standing water
- `find_planting` finds valid irrigated spots. `building_range` shows farmhouse coverage and moisture

### Water
- Deep Water Pump must straddle land/water edge
- ~2 pumps per 15 beavers, ~3 pumps for 15+
- During drought: water is consumed but NOT produced. Only stored water counts
- Tank storage matters more than pump count for surviving drought

### Trees
- Pine: 12-day growth, 2 logs. Birch: 8-day growth, 1 log
- `tree_clusters` finds densest grown clusters
- `markedGrown` in summary = choppable supply

### Power
- Power transfers through ADJACENT buildings only -- paths don't conduct power
- Powered buildings must form an unbroken chain to the power source
- Large Power Wheel: 300hp, needs workers. Compact Water Wheel needs flowing water
- Oasis maps have standing water (no flow) -- use Large Power Wheel
- Clutch: engages/disengages power transmission. Can segment power networks. Use `set_clutch` to control
- `find_placement` results include `nearPower` for adjacency checking

### Workers
- Hauling (construction delivery, breeding pod feeding) requires idle/unemployed beavers
- Too many staffed buildings with too few workers = nobody hauls = starvation spiral
- Lumberjacks must stay staffed -- no logs means no planks means no construction
- Default work hours end at 18. Adjustable with `set_workhours`

## Wellbeing

Max wellbeing: 77. Each beaver's `beavers` entry shows unmet needs by name.

| Category | Max | Buildings that satisfy |
|---|---|---|
| BasicNeeds | 5 | Food, water, sleep, shelter. WetFur need: DoubleShower |
| SocialLife | 2 | Campfire (+1), RooftopTerrace (+1) |
| Fun | 17 | Scratcher (+1), SwimmingPool (+1), ExercisePlaza (+3), MudBath (+3), WindTunnel (+3), Motivatorium (+5) |
| Nutrition | 17 | Each unique food type: Kohlrabi (+1), Coffee (+3), FermentedSoybean (+2), CornRation (+2), etc. |
| Aesthetics | 10 | Lantern (+1), Brazier (+1), Shrub (+1), Roof (+1), BeaverBust (+1), BeaverStatue (+2), Bell (+1), DecorativeClock (+2) |
| Awe | 26 | LaborerMonument (+3, 7-tile radius), FlameOfUnity (+5), TributeToIngenuity (+8), EarthRepopulator (+10) |

Nutrition requires food VARIETY -- different food types, not more of the same. Each type needs its own production chain. Wellbeing drops fast during crises (-12 possible) and recovers slowly.
