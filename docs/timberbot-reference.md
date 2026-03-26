---
title: Timberbot AI Reference
description: Conditional reference material for Timberbot gameplay, faction buildings, lookup tables, and long-form mechanics.
version: "0.7.0"
---
# Timberbot AI Reference

Read this only when needed. This doc holds the lookup-heavy material that used to live inline with the main Timberbot prompt.

For exact endpoint, parameter, response, and pagination details, use [API Reference](api-reference.md).

## Factions -- building names differ

Timberborn has two factions: **Folktails** and **Iron Teeth**. Every prefab except `Path` requires a faction suffix (`.Folktails` or `.IronTeeth`). Never use a bare name like `GathererFlag`; it must be `GathererFlag.Folktails` or `GathererFlag.IronTeeth`.

When in doubt: `timberbot.py prefabs | grep -i <keyword>`.

### Identifying the current faction

Run `timberbot.py prefabs | grep -c Folktails` -- if `> 0`, you're playing Folktails. Otherwise Iron Teeth.

### Folktails key buildings (prefab names)

| Role | Prefab name | Notes |
|---|---|---|
| **Housing** | Lodge.Folktails | 2x2, starter |
| | MiniLodge.Folktails | 1x1, needs science |
| | DoubleLodge.Folktails | needs science |
| | TripleLodge.Folktails | needs science |
| **Farming** | EfficientFarmHouse.Folktails | land crops (Carrots, Sunflower, Potato, etc); there is no `FarmHouse.Folktails` |
| | AquaticFarmhouse.Folktails | aquatic crops (Cattail, Spadderdock), needs science |
| **Water** | WaterPump.Folktails | basic, must straddle land/water edge |
| | LargeWaterPump.Folktails | needs science |
| **Power** | WaterWheel.Folktails | needs flowing water |
| | WindTurbine.Folktails | needs science |
| | LargeWindTurbine.Folktails | needs science |
| **Wood** | LumberjackFlag.Folktails | chops trees |
| | LumberMill.Folktails | logs -> planks |
| | Forester.Folktails | plants trees, needs science |
| **Food processing** | Grill.Folktails | grilled foods |
| | Gristmill.Folktails | flour |
| | Bakery.Folktails | bread |
| **Storage** | SmallWarehouse.Folktails | starter |
| | SmallTank.Folktails | starter water storage |
| | SmallPile.Folktails | log pile |
| **Science** | Inventor.Folktails | generates science |
| **Leisure** | Campfire.Folktails | SocialLife +1 |
| **Infrastructure** | DistrictCenter.Folktails | 3x3, colony hub |
| | HaulingPost.Folktails | 3x2, hauler workplace |
| | Dam.Folktails | 1x1, blocks water to 0.65 height |
| | Levee.Folktails | 1x1, fully blocks water, needs science |
| | Stairs.Folktails | z-level transition, needs science |
| | Platform.Folktails | multi-level jump, needs science |
| | PowerShaft.Folktails | power transmission |
| | GathererFlag.Folktails | gathers berries |
| | ScavengerFlag.Folktails | collects scrap metal from ruins |
| | RooftopTerrace.Folktails | SocialLife +1, needs roof access |
| | TeethGrindstone.Folktails | fixes chipped teeth |
| | MedicalBed.Folktails | heals injuries |

### Iron Teeth key buildings (prefab names)

| Role | Prefab name | Notes |
|---|---|---|
| **Housing** | Rowhouse.IronTeeth | starter |
| | Barrack.IronTeeth | needs science |
| **Farming** | FarmHouse.IronTeeth | land crops |
| | HydroponicGarden.IronTeeth | indoor, needs power+science |
| **Water** | DeepWaterPump.IronTeeth | 3x2, must straddle land/water edge |
| **Power** | LargePowerWheel.IronTeeth | 300hp, hamster wheel |
| | SteamEngine.IronTeeth | needs science |
| **Wood** | IndustrialLumberMill.IronTeeth | logs -> planks |

### Shared buildings (no faction suffix)

Path, AncientAquiferDrill, ReservePile, ReserveTank, ReserveWarehouse

All other buildings require a faction suffix. `not_found` with a `prefab` field usually means the prefab name is wrong for this faction. `not_unlocked` means it needs science first.

## Error codes

API errors return JSON with an `error` field in `"code: detail"` format:

```json
{"error": "not_found", "id": 42}
{"error": "invalid_type: not a floodgate", "id": 42}
{"error": "invalid_param: speed must be 0-3"}
{"error": "insufficient_science", "building": "LargePowerWheel", "scienceCost": 60, "currentPoints": 10}
```

Parse the prefix before `:` to switch on the code. Everything after `:` is human context.

| Code prefix | Meaning |
|---|---|
| `not_found` | Entity, building, district, or prefab does not exist |
| `invalid_type` | Entity exists but is the wrong type for this operation |
| `invalid_param` | Parameter value out of range or invalid |
| `not_unlocked` | Building requires science unlock first |
| `insufficient_science` | Not enough science points to unlock |
| `no_population` | No beavers available to migrate |
| `operation_failed` | Game service threw an exception |
| `disabled` | Feature disabled in settings.json |
| `unknown_endpoint` | Route not found |

Context fields (`id`, `prefab`, `building`, `available`, `scienceCost`, `currentPoints`) vary by endpoint. Exact error payload details also live in [API Reference](api-reference.md).

## API quick reference

For exact request and response details, use [API Reference](api-reference.md). This section is the fast lookup version.

| Method | What it does |
|---|---|
| **Brain** | |
| `brain [goal:"text"]` | Live summary + persistent goal/tasks/maps. Sets goal if provided. |
| **Read state** | |
| `beavers` | Per-beaver position, district, wellbeing, active needs. |
| `wellbeing` | Wellbeing by category with current/max. |
| `buildings` | All buildings. `detail:full` for all fields, `detail:id:<id>` for one. |
| `alerts` | Unstaffed, unpowered, unreachable buildings. |
| `trees` | Trees only, with growth/marking/alive state. |
| `crops` | Crops only, with growth/alive state. |
| `tree_clusters` | Densest grown tree clusters. |
| `food_clusters` | Densest gatherable food clusters. |
| `settlement` | Current settlement name. |
| `gatherables` | Berry bushes and other gatherable resources. |
| `science` | Science points and unlock costs. |
| `weather` | Hazardous status and days remaining. |
| `time` | Day number and progress. |
| `speed` | Current game speed. |
| `power` | Power networks: supply, demand, buildings per network. |
| `population` | Beaver/bot counts per district. |
| `resources` | Resource stocks per district. |
| `districts` | Multi-district overview. |
| `distribution` | Import/export settings per district per good. |
| `notifications` | Game event history. |
| `workhours` | Current work schedule. |
| `prefabs` | Building templates with sizes and unlock costs. |
| `ping` | Health check. |
| **Search/filter** | |
| `find source:buildings name:X` | Server-side name filter. |
| `find source:buildings x:X y:Y radius:R` | Server-side proximity filter. |
| `find source:trees name:Pine` | Find specific tree types. |
| `building_range building_id:X` | Work radius tiles for certain workplaces. |
| **Placement** | |
| `find_placement prefab:Name x1:X y1:Y x2:X2 y2:Y2` | Find valid building spots sorted by reachability. |
| `find_planting crop:Name building_id:X` | Find irrigated spots within farmhouse range. |
| `place_building prefab:Name x:X y:Y z:Z orientation:south` | Place a building. |
| `place_path x1:X y1:Y x2:X2 y2:Y2` | Route paths/stairs/platforms. |
| `demolish_building building_id:X` | Remove a building. |
| **Map** | |
| `map x1:X y1:Y x2:X2 y2:Y2 [name:label]` | ANSI map; `name` saves to memory. |
| `tiles x1:X y1:Y x2:X2 y2:Y2` | Per-tile terrain, water, badwater, occupants, moisture. |
| **Brain (memory)** | |
| `list_maps` | List saved map files. |
| `add_task action:"description"` | Add a pending task. |
| `update_task id:N status:done|failed [error:"reason"]` | Update task status. |
| `list_tasks` | Show all tasks. |
| `clear_tasks [status:done]` | Remove tasks by status. |
| `clear_brain` | Wipe memory for the current settlement. |
| **Crops and trees** | |
| `plant_crop ...` | Mark area for planting. |
| `clear_planting ...` | Clear planting marks. |
| `mark_trees ...` | Mark trees for cutting. |
| `clear_trees ...` | Unmark trees. |
| **Building config** | |
| `set_priority building_id:X priority:VeryHigh type:construction` | Set construction or workplace priority. |
| `set_workers building_id:X count:N` | Set desired worker count. |
| `pause_building building_id:X` | Pause a building. |
| `unpause_building building_id:X` | Resume a building. |
| `set_good building_id:X good:Water` | Set allowed good on storage. |
| `set_capacity building_id:X capacity:N` | Set storage capacity. |
| `set_haul_priority building_id:X prioritized:true` | Haulers deliver here first. |
| `set_recipe building_id:X recipe:RecipeId` | Set manufactory recipe. |
| `set_farmhouse_action building_id:X action:planting` | Prioritize planting or harvesting. |
| `set_plantable_priority building_id:X plantable:Pine` | Set forester/gatherer target. |
| `set_floodgate building_id:X height:N` | Set floodgate water height. |
| `set_clutch building_id:X engaged:true` | Engage or disengage a power clutch. |
| **Colony config** | |
| `set_speed speed:3` | Set game speed. |
| `set_workhours end_hours:20` | Set end of workday. |
| `set_distribution district:X good:Water import_option:Forced` | Set import/export per good per district. |
| `unlock_building building:Name.IronTeeth` | Unlock with science. |
| `migrate from_district:X to_district:Y count:N` | Move beavers between districts. |
| **Webhooks** | |
| `register_webhook url:URL events:a,b` | Register webhook. |
| `unregister_webhook webhook_id:wh_1` | Delete webhook. |
| `list_webhooks` | List webhooks. |
| **Forbidden** | |
| `debug` | Reflection-based game internals inspector. Disabled by default. |

## Paths

`place_path` routes a straight-line path (axis-aligned: `x1 == x2` or `y1 == y2`). It uses two passes: plan the full route first, then place it. Stairs go on the lower `z` tile. For 2-level jumps, it uses a platform plus stairs stacked at the cliff edge.

Return shape: `{placed: {paths, stairs, platforms}, skipped, errors}`. Errors are structured as `{prefab, error}` with game-validator reasons.

Paths cost nothing; use them freely. Stairs and platforms require science unlocks. Without stairs unlocked, `place_path` only builds flat paths and stops at z-changes.

## Flooding

Buildings placed in water become flooded and completely non-functional. Beavers and bots cannot access them.

- Most buildings flood when any water touches their footprint tile.
- Paths, power shafts, landscaping, stream gauges, Zipline/Tubeway Stations, Gravity Battery, Numbercruncher, and Control Tower are immune.
- Flooded tiles with badwater contamination also poison beavers who walk through them.
- Flooded buildings resume when water recedes; there is no permanent damage.
- `find_placement.flooded` is the detection signal. Terrain height alone is not reliable.

## Building priorities

Buildings have two priority types:

- `construction` while being built
- `workplace` when finished

Priority values: `VeryLow`, `Normal`, `VeryHigh`.

## Building sizes

Use `timberbot.py prefabs | grep -A3 <name>` for exact sizes.

Common sizes:

- Common: DistrictCenter 3x3, HaulingPost 3x2, Inventor 2x2, Forester 2x2, flags 1x1, Path 1x1
- Folktails: Lodge 2x2, EfficientFarmHouse 2x2, WaterPump 2x3, LumberMill 2x3, Shower 1x2
- Iron Teeth: Rowhouse 1x2, Barrack 3x2, FarmHouse 2x2, DeepWaterPump 3x2, LargePowerWheel 3x3, IndustrialLumberMill 2x3, DoubleShower 1x2

## Game mechanics

### Food

- Beavers eat about 1 food/day.
- Wild berries are finite; gatherers deplete them.
- Rough rule: 1 farmhouse per 8 beavers with full fields.
- Crops need irrigated soil. Crops continue growing during drought if the soil stays moist.
- Use `find_planting`; do not guess irrigated zones.

**Folktails crops:**

| Crop | Growth | Processing | Nutrition |
|---|---|---|---|
| Carrot | 4 days (2.8 w/ beehive) | raw | +1 |
| Sunflower | 5 days (3.5 w/ beehive) | raw (seeds) | +1 |
| Potato | 6 days (4.2 w/ beehive) | Grill -> GrilledPotatoes | +2 |
| Wheat | 10 days (7 w/ beehive) | Gristmill -> flour -> Bakery -> Bread | +2 |
| Cattail | 8 days (5.6 w/ beehive) | aquatic -> CattailCracker | +2 |
| Spadderdock | 12 days (8.4 w/ beehive) | aquatic, Grill -> GrilledSpadderdock | +2 |
| Chestnut | tree, 24 days | Grill -> GrilledChestnuts | +2 |
| Maple | tree, 28 days | tree tap -> MaplePastry | +3 |

**Iron Teeth crops:**

| Crop | Growth | Processing | Nutrition |
|---|---|---|---|
| Kohlrabi | 3 days | raw | +1 |
| Mangrove Fruit | tree, 10 days | raw | +1 |
| Cassava | 5 days | Fermenter -> FermentedCassava | +2 |
| Soybean | 8 days | Fermenter -> FermentedSoybean | +2 |
| Corn | 10 days | FoodFactory -> CornRation | +2 |
| Eggplant | 12 days | FoodFactory -> EggplantRation | +2 |
| Canola | 9 days | -> oil (processing) | +2 |
| Coffee | bush (Forester) | roasted, doesn't satisfy hunger | +3 |

Berries are a bridge to farming, not a long-term food plan.

**Beehive** (Folktails only): boosts crop growth about 30% in a 3-tile radius. It does not boost trees or bushes.

Folktails food processing: `Grill.Folktails`, `Gristmill.Folktails`, `Bakery.Folktails`

Iron Teeth food processing: `FoodFactory.IronTeeth`, `Fermenter.IronTeeth`

### Water

- Pumps must straddle the land/water edge.
- Folktails: `WaterPump.Folktails`, `LargeWaterPump.Folktails`
- Iron Teeth: `DeepWaterPump.IronTeeth`
- Rough rule: 2 pumps per 15 beavers, 3 pumps once you are above that and preparing for drought.
- During drought, water is consumed but not produced; only stored water and aquifer drills help.

### Irrigation and moisture

- Water tiles irrigate nearby ground.
- A 1x1 pond irrigates only a short radius; larger connected bodies irrigate farther.
- Elevation changes reduce irrigation range significantly.
- If soil dries out, plants wither and die.
- `tiles` output includes `moist: true`.

### Water management structures

- **Dam**: cheap early retention, spills above 0.65 height
- **Levee**: watertight wall, stackable, beavers can walk on it
- **Floodgate**: adjustable water control, no walking on top

### Aquifer drills

- **Ancient Aquifer Drills** are map-placed and need 400hp.
- **Aquifer Drill** is the player-built version and also needs 400hp.
- Aquifer drills keep producing during drought.

### Badwater and contamination

- Badwater contaminates beavers and kills plants.
- It is also a mid-game resource for extract/explosives chains.
- Folktails clean contamination with Herbalists; Iron Teeth use Decontamination Pods.

### Trees

| Tree | Growth | Logs | Special | Faction |
|---|---|---|---|---|
| Birch | 7 days | 1 | - | both |
| Pine | 12 days | 2 | Pine Resin | both |
| Oak | 30 days | 8 | - | both |
| Chestnut | 24 days | 4 | Chestnuts (food) | Folktails |
| Maple | 28 days | 6 | Maple Syrup (food) | Folktails |
| Mangrove | 10 days | 2 | Mangrove Fruits (food) | Iron Teeth |

Birch is best early. Oak is strongest long-term.

`tree_clusters` finds the densest grown tree clusters. `markedGrown` in `brain` is the immediately choppable supply.

**Forester** plants trees and bushes on moist soil. One forester keeps up with about four lumberjacks.

### Power

- Power transfers through adjacent buildings only.
- Paths do not conduct power.
- Oasis maps have standing water, so manual power is often better than water wheels.
- `find_placement.nearPower` helps with adjacency checks.

### Manufacturing chains

Construction supply chain:

```text
Trees -> Logs -> Planks -> most buildings
Planks -> Gears -> advanced buildings and bot parts
ScrapMetal -> MetalBlocks -> late-game buildings
```

Planks are usually the first bottleneck. Gears are the second.

Folktails knowledge chain: Paper -> Books

Extract chain: Badwater + Logs -> Extract

Biofuel chain (Folktails): Crops + Water -> Biofuel. Timberbots consume about 2 biofuel/day.

### Beaver lifecycle

- Lifespan is about 50 days.
- Kits mature in 6 days base, faster with high wellbeing.
- Beavers sleep at home during non-work hours.
- `TeethGrindstone` fixes chipped teeth.

### Weather cycles

- Every cycle has one temperate phase and one hazardous phase.
- Hazardous weather is drought or badtide.
- Durations escalate over time.
- Games always start in temperate weather.

### Population growth

- Folktails reproduce naturally through housing.
- Iron Teeth use Breeding Pods.

### Bots

- Bots work 24/7 and ignore food, water, sleep, and wellbeing.
- Folktails bots need Biofuel.
- Iron Teeth bots need Charging Stations.
- Bots cannot work at Power Wheels or Inventors.

### Scaling ratios (approximate)

| Per-capita | Ratio | Notes |
|---|---|---|
| Farmhouse | 1 per 8 beavers | with full irrigated fields |
| Water pump | 1 per 7 beavers | more during drought prep |
| Housing | depends on building | Lodge (FT) holds ~4, Rowhouse (IT) holds 2 |
| Lumberjack | 1 per 15 beavers | keep staffed always |
| Water tanks | 30 water per drought-day per beaver | plan for longest expected drought |

### Storage

Three storage families:

- Piles for logs, planks, metal blocks, dirt
- Warehouses for food, gears, manufactured goods
- Tanks for water, badwater, extract, syrup, biofuel

Each storage holds one good type at a time. Use `set_good` and `set_capacity`.

### Districts

Districts are separate colonies connected by District Crossings. Resources do not automatically flow between them.

- Expand when work clusters too far from the DC.
- Use `set_distribution` for goods flow.
- Use `migrate` for population transfers.
- Every district needs its own basic survival chain.

### Death spiral

When food or water hits zero: beavers die -> fewer workers -> less production -> more die.

### Workers

- Hauling requires idle or unemployed beavers.
- Too many staffed buildings with too few workers means nobody hauls.
- Lumberjacks must stay staffed.
- Default work hours end at 18.

## Wellbeing

Wellbeing categories and caps differ by faction. Run `timberbot.py wellbeing` for the exact current game's breakdown.

### Folktails wellbeing (max 77)

| Category | Max | Needs (building/food -> bonus) |
|---|---|---|
| BasicNeeds | 5 | Hunger (+1), Thirst (+1), Sleep (+1), Shelter (+1), WetFur (+1 via Shower.Folktails) |
| SocialLife | 11 | Campfire (+1), ContemplationSpot (+1), RooftopTerrace (+1), Agora (+3), DanceHall (+5) |
| Fun | 8 | Detailer (+1), Lido (+1), Carousel (+3), MudPit (+3) |
| Nutrition | 15 | Carrots (+1), SunflowerSeeds (+1), GrilledPotatoes (+2), GrilledChestnuts (+2), GrilledSpadderdock (+2), Bread (+2), CattailCracker (+2), MaplePastry (+3) |
| Aesthetics | 9 | Shrub (+1), Lantern (+1), Roof (+1), Scarecrow (+1), Weathervane (+1), BeaverStatue (+2), BulletinPole (+2) |
| Knowledge | 3 | Books (+3 via PrintingPress) |
| Awe | 26 | FarmerMonument (+3), BrazierOfBonding (+5), FountainOfJoy (+8), EarthRecultivator (+10) |

### Iron Teeth wellbeing (max ~77)

| Category | Max | Needs (building/food -> bonus) |
|---|---|---|
| BasicNeeds | 5 | Hunger (+1), Thirst (+1), Sleep (+1), Shelter (+1), WetFur (+1 via DoubleShower.IronTeeth) |
| SocialLife | 2 | Campfire (+1), RooftopTerrace (+1) |
| Fun | 17 | Scratcher (+1), SwimmingPool (+1), ExercisePlaza (+3), MudBath (+3), WindTunnel (+3), Motivatorium (+5) |
| Nutrition | 17 | Kohlrabi (+1), MangroveFruits (+1), FermentedCassava (+2), FermentedSoybean (+2), FermentedMushroom (+2), CornRation (+2), EggplantRation (+2), AlgaeRation (+2), Coffee (+3, no hunger) |
| Aesthetics | 10 | Lantern (+1), Brazier (+1), Shrub (+1), Roof (+1), BeaverBust (+1), BeaverStatue (+2), Bell (+1), DecorativeClock (+2) |
| Awe | 26 | LaborerMonument (+3), FlameOfUnity (+5), TributeToIngenuity (+8), EarthRepopulator (+10) |

Nutrition requires food variety. Different food types matter; more of one food does not.

### Wellbeing building placement

- Each wellbeing building has an `effectRadius`.
- Use `buildings detail:id:<id>` to inspect it.
- Place wellbeing buildings near high-traffic areas.
- Different wellbeing types can overlap productively.
- Duplicating the same wellbeing building in the same area is usually wasted coverage.

### Recipe switching

- `set_recipe` destroys in-progress items and consumed materials.
- Setting the same recipe again also resets progress.
- Single-recipe buildings already have their recipe set.
- Multi-recipe buildings need one explicit setup.

### Rooftop buildings

- Decorative roofs provide aesthetics just by existing and do not need path access.
- `RooftopTerrace` is an actual visited building and does need path or stair access.

## Settings

`settings.json` lives in the mod folder.

| Setting | Default | Description |
|---|---|---|
| `refreshIntervalSeconds` | 1.0 | Cache refresh cadence; higher means less CPU and more stale reads |
| `debugEndpointEnabled` | false | Enable `/api/debug` |
| `httpPort` | 8085 | HTTP server port |

Mutable values like paused state, workers, and wellbeing can be up to `refreshIntervalSeconds` stale. Entity presence remains current through the event bus.
