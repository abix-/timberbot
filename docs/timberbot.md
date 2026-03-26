---
name: timberbot
description: Collaborate with a human player on Timberborn via timberbot.py. Help keep beavers alive, wellbeing high, needs met.
version: "0.6.6"
---
# Timberbot - Game Reference

## FIRST RUN: Boot Sequence

On the FIRST invocation of /timberbot per session, you MUST complete TWO phases in order. The boot report is NOT a game action -- it proves that YOU, Claude, have read and internalized the rules. No API calls until after the boot report is printed.

### Phase 1: Boot (rules confirmation -- NO API calls)

1. Read this ENTIRE skill file top to bottom (not just the first 30 lines -- ALL of it)
2. IMMEDIATELY print this boot report to prove you loaded the rules. Do NOT run any commands first. The boot report has two sections: RULES (hard rules you must follow) and INVENTORY (counts extracted from the skill file that prove you read the whole thing -- if you only read the top, you cannot fill these in). Fill in every count by scanning the skill file content you just read:

Print the boot output below as markdown (Claude Code renders markdown, NOT ANSI escapes). Use lowercase throughout for robotic terminal feel. Format:

```
## TIMBERBOT v0.6.6

`[___]` DC entrance = root of all path distances
`[___]` unpathed buildings cannot be staffed or built
`[___]` find_placement for ALL placement
`[___]` find_planting for crops
`[___]` paths occupy tiles and block building placement
`[___]` entrance must face path
`[___]` beavers die at 0 food/water
`[___]` never guess coords
`[___]` co-op: human changes state between calls
`[___]` placement and pathing work at speed 0
`[___]` sequential mutating calls only
`[___]` brain = live state + persistent goal/tasks/maps
`[___]` tasks persist across sessions, failed tasks keep error context
`[___]` prefabs FT:___ IT:___
`[___]` endpoints ___
`[___]` crops FT:___ IT:___
`[___]` trees ___ (___ shared)
`[___]` wellbeing FT:___ IT:___ max
`[___]` errors ___
`[___]` skill ___ ln / ___ sec
`[___]` last "___"
```

Fill EVERY `___` -- both the rule status markers (replace with `OK`) and the inventory counts. NONE are pre-filled. Claude filling them in IS the confirmation of readiness. Wrong/missing/skipped = not ready to play.

### Phase 2: Link (one command)

3. Run `timberbot.py brain goal:"<player's request>"`. This is the ONLY boot API call. The player's prompt becomes the persistent goal. Memory is per-settlement (stored in `memory/<settlement>/`).

**If existing memory found for this settlement:** Ask the human: "found existing brain for `<settlement>` with `<N>` tasks and `<M>` maps. load it or start fresh?" If they say fresh, run `timberbot.py clear_brain` to wipe the settlement folder, then `brain` again.

**If no existing memory:** Brain auto-creates it with DC map. Print readout:

```
> **settlement** `<name>` | `<faction>` | <"new" or "loaded">
> **goal** `<goal text or "none -- awaiting orders">`
>
> **day** `<N>` | **speed** `<S>` | **weather** <temperate/drought> `<N>d` remain
> **pop** `<adults>` adults, `<children>` children | **beds** `<occ>`/`<total>` | **workers** `<assigned>`/`<vacancies>`
> **supply** food `<F>d` | water `<W>d` | logs `<L>` | planks `<P>`
> **wellbeing** `<avg>`/77 | `<miserable>` miserable | `<critical>` critical needs
> **nearby** `<N>` trees (`<species>`) | `<N>` food (`<species>`)
> **alerts** `<unstaffed>` unstaffed | `<unpowered>` unpowered | `<unreachable>` unreachable
>
> **tasks** <count pending/active/failed or "none">
```

If food or water <= 1d, append ` CRITICAL` after the value. If alerts are all 0, show `all clear`.

4. If there are failed/active tasks from a previous session, list them and assess whether to retry or re-plan before starting new work.

Only AFTER both phases are complete should you begin working on the user's request.

On subsequent invocations in the same session, skip the boot sequence and go straight to work.

---

This is a human-AI co-op game. The human player is also building, demolishing, and changing settings in real time. Game state can change between API calls.

`timberbot.py` is on PATH. Call it directly (e.g. `timberbot.py brain`). See [getting-started](https://abix-.github.io/TimberbornMods/getting-started/) for setup details.

Beavers die if food or water hits 0.

## HARD RULE: Sequential execution

**NEVER run game API calls in parallel.** Every placement, path, or config call changes the map state that the next call depends on. A failed placement invalidates every subsequent call that assumed it succeeded. Run each call sequentially, confirm it worked, then proceed. Read-only calls (`brain`, `buildings`, `find_placement`, `map`, `tiles`, `weather`, etc.) CAN run in parallel with each other since they don't mutate state. But any mutating call (`place_building`, `place_path`, `demolish_building`, `set_*`, `plant_crop`, `mark_trees`, etc.) must complete and succeed before the next action.

## Roads

Roads (paths) are the circulatory system of the colony. They cost nothing (zero logs, zero planks, zero science) and can be placed freely. Every building, workplace, and resource site must connect back to the district center via an unbroken chain of path tiles. Without roads, beavers are trapped at the DC -- they can't reach workplaces, haul materials, or access water.

**Tree structure.** Think of the road network as a tree. The DC entrance is the root. Trunk roads extend outward from it. Branch roads fork off the trunk toward resources, water, and building sites. Buildings attach to branches via their entrance tile. Every path tile must trace back to the root through connected tiles.

**DC entrance (the root).** The entrance is on the side matching the DC's orientation. For a south-facing DC at (x, y), the entrance is at the middle tile of the south edge: `(x+1, y-1)` (DC is 3x3, entrance is center of the oriented side). The first path tiles radiate outward from this point.

**Reachability.** The `reachable` field in `find_placement` means "path-connected to DC." The `distance` field is the path cost from the DC entrance via the game's flow field (-1 if unreachable). A building with `reachable:0` cannot be staffed or supplied. Lower distance = shorter hauler trips.


## Placement

**Use `find_placement` for ALL building placement.** Never manually search tiles, grep for water, or scan the map. It checks terrain, water depth, flooding, orientation, path adjacency, and reachability -- all in one call. Use the x, y, z, and orientation it returns. No exceptions.

**Brain has the map.** `brain` returns DC coords, entrance, z-level, and a 41x41 ANSI map centered on DC. The saved map file (`memory/map-districtcenter-*.txt`) shows terrain, water, trees, and buildings.

**Paths block footprints.** Paths are 1x1 entities that occupy tiles. A path on a tile prevents any building from being placed there.

**Entrance must face a path.** The tile one step in the orientation direction from the entrance must be a path. A building whose entrance doesn't face a path cannot be accessed. `find_placement` returns `orientation` pointing the entrance toward the nearest path.

Entrance directions: **north** = +y (up), **south** = -y (down), **east** = +x (right), **west** = -x (left). Example: building at (10,10) with orientation south -> entrance faces -y -> tile (10,9) must be a path.

**Response fields:** `entranceX`/`entranceY` (tile where a path must go), `flooded` (0/1, flooded sort to bottom), `waterDepth` (water buildings sort deepest first), `reachable` (connected to DC), `pathAccess`, `nearPower`, `distance` (path cost from DC entrance via flow field, -1 if unreachable -- lower = closer). Sort: non-flooded > reachable > distance (closer) > pathAccess > nearPower. Boolean fields are 0/1 integers.

**Z-level:** z must equal terrain height at the placement location. Wrong z = invisible/broken building. The brain's DC map shows terrain height via digit (z % 10) + background shading (dark=z0-9, medium=z10-19, bright=z20-22). Use `tiles` for raw data. Different map areas have different heights -- never assume z:2.

**New game state:** A new game starts with only a district center and no roads. Paths cost nothing. Stairs and platforms require science unlocks.

## Game Speed

| Level | Name | Effect |
|-------|------|--------|
| 0 | **Paused** | No time passes. Placement, pathing, and priority changes all work. No resources consumed. |
| 1 | **Normal** | 1x speed. |
| 2 | **Fast** | 2x speed. |
| 3 | **Fastest** | 4x speed. |

## References

- **API reference:** `https://abix-.github.io/TimberbornMods/api-reference/` -- all endpoints, request/response formats, parameters
- **Game wiki:** `https://timberborn.wiki.gg/wiki/<topic>` -- building stats, ranges, mechanics not covered here
- **Prefab lookup:** `timberbot.py prefabs | grep -i <keyword>` -- valid building names for current faction

## Brain

`brain` = live colony state + persistent memory. One command, always fresh.

`timberbot.py brain` -- returns live summary from game + goal/tasks/maps from disk.
`timberbot.py brain goal:"<text>"` -- sets/overwrites the persistent goal.

### What brain returns

**Live (from `/api/summary`, never persisted):**

| Field | Content |
|---|---|
| `settlement` | Save name |
| `faction` | Folktails or IronTeeth |
| `time` | dayNumber, dayProgress, speed |
| `weather` | cycle, cycleDay, isHazardous, durations |
| `districts[]` | Per district: `population` (adults/children/bots), `resources` (flat totals, e.g. `Water: 170`), `housing` (occupiedBeds/totalBeds/homeless), `employment` (assigned/vacancies/unemployed), `wellbeing` (average/miserable/critical), `dc` (x/y/z/orientation/entranceX/entranceY) |
| `trees` | markedGrown, markedSeedling, unmarkedGrown + `species[]` per-species breakdown |
| `crops` | ready, growing + `species[]` per-species breakdown |
| `wellbeing` | Global average/miserable/critical + `categories[]` (group/current/max per need group) |
| `science` | Current science points |
| `alerts` | unstaffed, unpowered, unreachable counts |
| `buildings` | Count by role: water, food, housing, wood, storage, power, science, production, leisure, paths |
| `treeClusters` | Top 5 densest tree clusters on DC z-level within 40 tiles. x/y/z/grown/total + `species` (e.g. `{Pine: 45}`) |
| `foodClusters` | Top 5 gatherable food clusters. Same format + `species` (e.g. `{BlueberryBush: 55}`) |

**Persisted (in `memory/<settlement>/brain.toon`):**

| Field | Content |
|---|---|
| `timestamp` | When brain.toon was last written |
| `goal` | Player's intent. Set via `brain goal:"..."`. Persists across sessions. New goal overwrites. Empty string if unset. |
| `tasks` | Ordered work queue. Each: id, status (pending/active/done/failed), action. Failed tasks include error field. |
| `maps` | Region index. Each region: bounding box coords + array of saved map files. |

### Districts

Each district is self-contained: population, resources, housing, employment, wellbeing, and DC coords. Multi-district colonies show all districts. Single-district shows one.

### Clusters

treeClusters and foodClusters are filtered to same z-level as first DC and within 40 Manhattan distance -- only resources beavers can reach without stairs. `species` shows what's growing in each cluster. `grown` = harvestable now, `total` = including seedlings.

### DC map

`brain` auto-saves a 41x41 ANSI map centered on DC on first run. Read from `maps.districtcenter.files[0]`. Shows terrain height (digits + background shading), water, trees, buildings, paths.

### Task statuses

| Status | Meaning |
|---|---|
| `pending` | Not started |
| `active` | In progress |
| `done` | Completed |
| `failed` | Failed -- `error` field explains why |

### Task methods

| Method | What |
|---|---|
| `add_task action:"description"` | Add pending task |
| `update_task id:N status:active` | Mark in progress |
| `update_task id:N status:done` | Mark complete |
| `update_task id:N status:failed error:"reason"` | Mark failed with reason |
| `list_tasks` | Show all tasks |
| `clear_tasks [status:done]` | Remove tasks by status |

### Map methods

| Method | What |
|---|---|
| `map x1:X y1:Y x2:X2 y2:Y2 name:label` | Save ANSI map, auto-updates brain.toon maps index |
| `list_maps` | List saved map files |
| `clear_brain` | Wipe all memory for current settlement |

## Factions -- building names differ

Timberborn has two factions: **Folktails** and **Iron Teeth**. **EVERY prefab except `Path` requires a faction suffix** (`.Folktails` or `.IronTeeth`). Never use a bare name like `GathererFlag` -- it must be `GathererFlag.Folktails` or `GathererFlag.IronTeeth`. When in doubt: `timberbot.py prefabs | grep -i <keyword>`.

### Identifying the current faction
Run `timberbot.py prefabs | grep -c Folktails` -- if >0, you're playing Folktails. Otherwise Iron Teeth.

### Folktails key buildings (prefab names)
| Role | Prefab name | Notes |
|---|---|---|
| **Housing** | Lodge.Folktails | 2x2, starter |
| | MiniLodge.Folktails | 1x1, needs science |
| | DoubleLodge.Folktails | needs science |
| | TripleLodge.Folktails | needs science |
| **Farming** | EfficientFarmHouse.Folktails | land crops (Carrots, Sunflower, Potato, etc) -- NO "FarmHouse.Folktails" exists |
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

All other buildings require a faction suffix (`.Folktails` or `.IronTeeth`). Use `timberbot.py prefabs | grep -i <keyword>` to find the exact name.

`not_found` with a `prefab` field means the prefab name is wrong for this faction. `not_unlocked` means it needs science first (response includes `scienceCost` and `currentPoints`).

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

Context fields (`id`, `prefab`, `building`, `available`, `scienceCost`, `currentPoints`) vary by endpoint.

## API quick reference

| Method | What it does |
|---|---|
| **Brain** | |
| `brain [goal:"text"]` | Live summary + persistent goal/tasks/maps. Sets goal if provided. Run at boot with player prompt and after changes |
| **Read state** | |
| `beavers` | Per-beaver position (x,y,z), district, wellbeing, active needs. `detail:full` for all needs with group category, `detail:id:<id>` for single beaver/bot |
| `wellbeing` | Wellbeing by category with current/max |
| `buildings` | All buildings (compact). `detail:full` for all fields (effectRadius, productionProgress, readyToProduce, inventory, etc), `detail:id:<id>` for single building |
| `alerts` | Unstaffed, unpowered, unreachable buildings |
| `trees` | Trees only (Pine, Birch, Oak, etc) with growth, marking, alive status |
| `crops` | Crops only (Kohlrabi, Soybean, Corn, etc) with growth and alive status |
| `tree_clusters` | Densest grown tree clusters |
| `food_clusters` | Densest gatherable food clusters (berries, bushes) |
| `settlement` | Current settlement name (lightweight, no computation) |
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
| `find source:buildings name:X` | Server-side name filter (case-insensitive substring) |
| `find source:buildings x:X y:Y radius:R` | Server-side proximity filter (Manhattan distance) |
| `find source:trees name:Pine` | Find specific tree types |
| `building_range building_id:X` | Work radius tiles for farmhouse, lumberjack, forester, gatherer |
| **Pagination** | List endpoints default to 100 items. `limit:0` for all. Response: `{total, offset, limit, items}` |
| **Placement** | |
| `find_placement prefab:Name x1:X y1:Y x2:X2 y2:Y2` | Find valid building spots sorted by reachability |
| `find_planting crop:Kohlrabi building_id:X` | Find irrigated spots within farmhouse range |
| `place_building prefab:Name x:X y:Y z:Z orientation:south` | Place a building |
| `place_path x1:X y1:Y x2:X2 y2:Y2` | Returns `{placed:{paths,stairs,platforms}, skipped, errors}`. Stairs on lower z, platforms stack at cliff edge |
| `demolish_building building_id:X` | Remove a building |
| **Map** | |
| `map x1:X y1:Y x2:X2 y2:Y2 [name:label]` | ANSI map. `name` saves to memory/ for persistent spatial reference |
| `tiles x1:X y1:Y x2:X2 y2:Y2` | Per-tile terrain, water, badwater, occupants (z-stacking), moisture, contamination |
| **Brain (memory)** | |
| `list_maps` | List saved map files |
| `add_task action:"description"` | Add pending task to brain |
| `update_task id:N status:done\|failed [error:"reason"]` | Update task status |
| `list_tasks` | Show all tasks |
| `clear_tasks [status:done]` | Remove tasks by status |
| `clear_brain` | Wipe memory for current settlement. Run brain again to start fresh |
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
| `set_speed speed:3` | Game speed: 0=pause, 1=normal, 2=fast, 3=fastest. See Game Speed |
| `set_workhours end_hours:20` | When work ends (1-24) |
| `set_distribution district:X good:Water import_option:Forced` | Import/export per good per district |
| `unlock_building building:Name.IronTeeth` | Unlock building with science points |
| `migrate from_district:X to_district:Y count:N` | Move beavers between districts |
| **Webhooks** | |
| `register_webhook url:URL events:drought.start,beaver.died` | POST /api/webhooks. 68 push events. See [webhooks.md](webhooks.md) |
| `unregister_webhook webhook_id:wh_1` | POST /api/webhooks/delete |
| `list_webhooks` | GET /api/webhooks |
| **Forbidden** | |
| `debug` | Reflection-based game internals inspector. Disabled by default -- enable in `settings.json` |

## Paths

`place_path` routes a straight-line path (axis-aligned: x1==x2 or y1==y2). Two-pass: plans the full route first, then places. Stairs go on the LOWER z tile; for 2-level jumps: platform + stairs stacked at cliff edge. Returns `{placed: {paths, stairs, platforms}, skipped, errors}`. Errors are structured: `{prefab, error}` with game validator reasons. Paths cost nothing -- place freely.

Stairs and platforms require science unlocks. Without stairs unlocked, `place_path` only builds flat paths on the same z-level -- stops at z-changes and reports the error.

## Flooding

Buildings placed in water become **flooded** and completely non-functional. Beavers and bots cannot access them.

- **Flooded on contact:** Most buildings flood when ANY water touches their footprint tile -- housing, production, storage, leisure, monuments, farms
- **Immune:** Paths, power shafts, landscaping, stream gauges, Zipline/Tubeway Stations, Gravity Battery, Numbercruncher, Control Tower
- **Badwater:** Flooded tiles with badwater contamination also poison beavers who walk through them
- **Not destroyed:** Flooded buildings resume when water recedes. No permanent damage
- **Detection:** `find_placement` includes `flooded` field. Any z-level can flood -- terrain height is not a reliable indicator

## Building priorities

Buildings have two priority types: `construction` (while building) and `workplace` (when finished). Values: VeryLow, Normal, VeryHigh.

## Building sizes

Use `timberbot.py prefabs | grep -A3 <name>` for exact sizes. Common sizes:

**Common (faction-suffixed):** DistrictCenter 3x3, HaulingPost 3x2, Inventor 2x2, Forester 2x2, flags 1x1, Path 1x1
**Folktails:** Lodge 2x2, EfficientFarmHouse 2x2, WaterPump 2x3, LumberMill 2x3, Shower 1x2
**Iron Teeth:** Rowhouse 1x2, Barrack 3x2, FarmHouse 2x2, DeepWaterPump 3x2, LargePowerWheel 3x3, IndustrialLumberMill 2x3, DoubleShower 1x2

## Game mechanics

### Food
- Beavers eat ~1 food/day
- Wild berries are finite -- gatherers deplete them
- ~1 farmhouse per 8 beavers with full fields
- Crops need irrigated (moist) soil. Crops grow during drought if soil stays moist near standing water
- `find_planting` finds valid irrigated spots. `building_range` shows farmhouse coverage and moisture

**Folktails crops:**
| Crop | Growth | Processing | Nutrition |
|---|---|---|---|
| Carrot | 4 days (2.8 w/ beehive) | raw | +1 |
| Sunflower | 5 days (3.5 w/ beehive) | raw (seeds) | +1 |
| Potato | 6 days (4.2 w/ beehive) | Grill -> GrilledPotatoes | +2 |
| Wheat | 10 days (7 w/ beehive) | Gristmill -> flour -> Bakery -> Bread | +2 |
| Cattail | 8 days (5.6 w/ beehive) | aquatic, -> CattailCracker | +2 |
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

Berries are finite (gathered from wild bushes) -- bridge to farming, don't rely long-term.

**Beehive** (Folktails only, 1x1, 400 science, 10 logs + 15 planks + 20 paper): Boosts crop growth ~30% in 3-tile radius. Boosts 3 crops every 2 hours. Does NOT boost trees or bushes. Causes bee stings (-1 wellbeing) -- bots are immune.

**Folktails food processing chain:** Grill.Folktails (raw -> grilled), Gristmill.Folktails (wheat -> flour), Bakery.Folktails (flour -> bread)
**Iron Teeth food processing:** FoodFactory.IronTeeth, Fermenter.IronTeeth

### Water
- Water pumps must straddle land/water edge
- **Folktails:** WaterPump.Folktails (basic), LargeWaterPump.Folktails (science)
- **Iron Teeth:** DeepWaterPump.IronTeeth (3x2)
- ~2 pumps per 15 beavers, ~3 pumps for 15+
- During drought: water is consumed but NOT produced. Only stored water counts
- Tank storage matters more than pump count for surviving drought

### Irrigation and moisture
- Water tiles irrigate nearby ground. Irrigated soil turns green and allows crops/trees to grow
- Irrigation range depends on water body size: 1x1 pond irrigates ~4 tiles, 3x3 irrigates ~13+
- Larger connected water bodies irrigate further. Shape is circular, not diamond
- Elevation changes reduce irrigation range by ~6 tiles per z-level
- If soil dries out (drought, water recedes), plants wither and die
- `find_planting` shows irrigated spots within farmhouse range -- use this instead of guessing
- `tiles` output shows `moist: true` for irrigated tiles

### Water management structures
- **Dam** (1x1, 20 logs, starter): Blocks water up to 0.65 height. Water spills over the top. Beavers can walk on top
- **Levee** (1x1, 12 logs, 120 science): Completely blocks water passage. Can be stacked vertically. Beavers can walk on top. Replace with Terrain Blocks later for performance
- **Floodgate** (1x1 height 2, 10 logs + 5 planks, 150 science): Adjustable height 0-1 in 0.05 increments. Water above set height spills through, below does not. Beavers CANNOT walk on top. Use `set_floodgate` to control. Adjacent floodgates sync height by default
- **Use cases:** Dam = cheap early water retention. Levee = watertight walls. Floodgate = precise water level control, drought reservoirs, irrigation management

### Aquifer drills
- **Ancient Aquifer Drills** are pre-placed on maps over natural aquifer sources. They need power (400hp) to produce flowing water. Visible in `buildings` output
- **Aquifer Drill** (3x3, 40 planks + 25 gears + 15 metal blocks, 400 science): Player-built version, placed over aquifer tiles. Also needs 400hp power
- Aquifer drills produce water even during drought -- valuable late-game water security

### Badwater and contamination
- Badwater is toxic fluid that contaminates beavers on contact (-10 wellbeing). It kills plants and poisons soil
- Badwater is also a mid-game resource: BadwaterPump (FT) or DeepBadwaterPump (IT) collects it for processing
- Processing: Centrifuge (badwater + logs -> Extract), Explosives Factory (badwater -> Explosives)
- **Folktails cleanup:** Herbalist (300 science) produces Antidote from Dandelion + Berries + Paper. Cures contaminated beavers
- **Iron Teeth cleanup:** Decontamination Pod
- Contamination Barrier (FT, 400 science) blocks badwater spread

### Trees

| Tree | Growth | Logs | Special | Faction |
|---|---|---|---|---|
| Birch | 7 days | 1 | - | both |
| Pine | 12 days | 2 | Pine Resin | both |
| Oak | 30 days | 8 | - | both |
| Chestnut | 24 days | 4 | Chestnuts (food) | Folktails |
| Maple | 28 days | 6 | Maple Syrup (food) | Folktails |
| Mangrove | 10 days | 2 | Mangrove Fruits (food) | Iron Teeth |

Birch is best for early planting (fastest). Oak has best yield/time ratio long-term. Faction-specific trees only obtainable via Forester.

- `tree_clusters` finds densest grown clusters
- `markedGrown` in brain summary = choppable supply

**Forester** (2x2, 30 science): Plants trees and bushes on moist soil. Work radius: 21 tiles ahead of entrance, 20 in other directions. One forester keeps up with ~4 lumberjacks. Use `set_plantable_priority` to choose which tree type to plant. Trees don't spread naturally -- forester must replant. Can also plant: Dandelion Bush (FT), Coffee Bush (IT).

### Power
- Power transfers through ADJACENT buildings only -- paths don't conduct power
- Powered buildings must form an unbroken chain to the power source
- **Folktails:** WaterWheel.Folktails (needs flowing water), WindTurbine.Folktails (science), PowerWheel.Folktails (manual)
- **Iron Teeth:** LargePowerWheel.IronTeeth (300hp, hamster wheel), SteamEngine.IronTeeth (science)
- Oasis maps have standing water (no flow) -- use manual power wheels, not water wheels
- Clutch: engages/disengages power transmission. Can segment power networks. Use `set_clutch` to control
- `find_placement` results include `nearPower` for adjacency checking

### Manufacturing chains

**Construction supply chain (both factions):**
```
Trees -> Logs (LumberjackFlag) -> Planks (LumberMill) -> most buildings
Planks -> Gears (GearWorkshop, 3hr/gear) -> advanced buildings, bot parts
ScrapMetal (ScavengerFlag from ruins) -> MetalBlocks (Smelter, 2 scrap + 0.2 log -> 1 block, 4hr)
```
Planks are the #1 bottleneck -- nearly every building needs them. Gears are the #2 bottleneck for mid-game.

**Folktails knowledge chain:** Planks -> Paper (PaperMill) -> Books (PrintingPress) -> Knowledge wellbeing (+3)

**Metal chain:** Surface ruins (ScavengerFlag) or underground ruins (Mine) -> ScrapMetal -> Smelter -> MetalBlocks. Smelter itself costs 30 scrap metal to build.

**Extract chain:** Badwater (BadwaterPump) + Logs -> Centrifuge -> Extract. Used for: catalyst/agora fuel (FT), grease/advanced breeding (IT), detailers (both).

**Biofuel chain (Folktails only):** Crops (potato/carrot/spadderdock) + Water -> Refinery -> Biofuel. Timberbots consume ~2 biofuel/day. Without it they refuse to work and move 75% slower.

### Beaver lifecycle
- Lifespan: ~50 days (+-10% random). Beavers age 1 year per dawn and can die of old age
- Kits mature into adults in 6 days (base). High wellbeing gives up to +75% growth speed; starvation gives -40% penalty
- Beavers sleep at home during non-work hours. Without housing they sleep outside (lose Shelter wellbeing, -3)
- ChippedTeeth: beavers with chipped teeth work at 25% effectiveness cutting trees. TeethGrindstone (1x1, 5 logs, starter) fixes this

### Weather cycles
- Each cycle = 1 temperate season + 1 hazardous season (drought or badtide)
- **Temperate:** Normal conditions. Water flows, crops grow. Time to stockpile
- **Drought:** All water sources stop flowing. Only stored water and aquifer drills produce water. Evaporation reduces standing water
- **Badtide:** Water and badwater sources emit contaminated water. Contamination ramps to 100% over 12 hours. Kills plants, poisons beavers
- Durations escalate over time. Early droughts are short (1-3 days), later ones grow longer (6+ days on hard)
- `weather` command shows current state, days remaining, and whether hazardous
- Games always start with temperate weather

### Population growth

**Folktails:** Natural reproduction. Two adults with no critical needs (hunger, thirst) sharing a Lodge will produce kits. Population is controlled by available housing -- build/pause Lodges to control growth. Growth lags due to kit maturation time.

**Iron Teeth:** Breeding Pod (pre-unlocked, costs 10 logs). Takes 5 days per kit, requires constant supply of 5 water + 5 berries per cycle. Pod stores max 2 water + 2 berries at a time -- haulers must keep it fed. Advanced Breeding Pod (needs Metal + Extract) produces adults instead of kits.

### Bots

Bots are mechanical beavers that work 24/7 with no food, water, sleep, or wellbeing needs. Built in BotAssembler from parts made in BotPartFactory (gears -> bot parts). Assembly takes ~36 hours. Fixed 70-day lifespan.

- **Folktails (Timberbots):** Need Biofuel stored in tanks
- **Iron Teeth (Ironbots):** Need Energy from Charging Stations (require constant power)
- **Cannot work at:** Power Wheels, Inventor
- **Performance:** Large bot populations cause game lag

### Scaling ratios (approximate)

| Per-capita | Ratio | Notes |
|---|---|---|
| Farmhouse | 1 per 8 beavers | with full irrigated fields |
| Water pump | 1 per 7 beavers | more during drought prep |
| Housing | depends on building | Lodge (FT) holds ~4, Rowhouse (IT) holds 2 |
| Lumberjack | 1 per 15 beavers | keep staffed always |
| Water tanks | 30 water per drought-day per beaver | plan for longest expected drought |

### Storage
Three types -- each good requires a specific type:
- **Piles** (logs, planks, metal blocks, dirt): SmallPile (20), LargePile (180), UndergroundPile (1000)
- **Warehouses** (food, gears, manufactured goods): SmallWarehouse (30), MediumWarehouse (200), LargeWarehouse (1200)
- **Tanks** (water, badwater, extract, syrup, biofuel): SmallTank (30), MediumTank (300), LargeTank (1200)

Each storage holds one good type at a time. Use `set_good` to assign which good. Use `set_capacity` to limit fill level.

### Districts

Districts are separate colonies connected by District Crossings. Each district has its own workers, storage, and buildings. Resources don't automatically flow between districts.

- **When to expand:** When workplaces cluster far from the DC, or to access remote water/resources
- **How:** Place a new DistrictCenter, connect via DistrictCrossing. Workers at crossings (max 10 per side) haul goods between districts
- **Import/export:** Use `set_distribution` to control which goods flow between districts (Forced import, allowed, disabled)
- **Migration:** Use `migrate` to move beavers between districts
- Districts are self-sufficient -- each needs its own water, food, housing, and wood production

### Death spiral

When food or water hits 0: beavers die -> fewer workers -> less production -> more die.

### Workers
- Hauling (construction delivery, breeding pod feeding) requires idle/unemployed beavers
- Too many staffed buildings with too few workers = nobody hauls = starvation spiral
- Lumberjacks must stay staffed -- no logs means no planks means no construction
- Default work hours end at 18. Adjustable with `set_workhours`

## Wellbeing

Wellbeing categories and max values differ by faction. Run `timberbot.py wellbeing` for the current game's exact breakdown. Each beaver's `beavers` entry shows unmet needs by name.

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

Nutrition requires food VARIETY -- different food types, not more of the same. Each type needs its own production chain. Wellbeing drops fast during crises (-12 possible) and recovers slowly.

### Wellbeing building placement
- Each wellbeing building has an **effect radius** -- beavers must be within range to get the benefit
- Use `buildings detail:id:<id>` to see `effectRadius` for a specific building
- Place wellbeing buildings near high-traffic areas (paths, workplaces, housing) for maximum coverage
- Different wellbeing types CAN overlap -- a Lantern and Campfire covering the same area is good (different needs)
- Two identical wellbeing buildings covering the same area is wasted -- beavers only get the bonus once
- Spread identical buildings apart so their effect radii cover different parts of the colony

### Recipe switching
- `set_recipe` destroys in-progress items AND the materials consumed so far. Materials are flushed permanently
- Setting the same recipe that is already active also resets progress and destroys materials
- Single-recipe buildings (e.g. BotAssembler) already have their recipe set by default
- Multi-recipe buildings (BotPartFactory, Fermenter, FoodFactory) need `set_recipe` once on first setup
- Read building state with `buildings detail:id:<id>` to check current recipe before changing

### Rooftop buildings
- **Roof** (Roof1x1, etc.): decorative, placed on top of buildings. Does NOT need path access -- provides Aesthetics just by existing
- **RooftopTerrace**: has an entrance, beavers physically visit it for SocialLife. DOES need path/stair access to the rooftop

## Settings

`settings.json` in mod folder (`Documents/Timberborn/Mods/Timberbot/`):

| Setting | Default | Description |
|---|---|---|
| `refreshIntervalSeconds` | 1.0 | cache refresh cadence (seconds). Higher = less CPU, more stale |
| `debugEndpointEnabled` | false | enable `/api/debug` reflection endpoint |
| `httpPort` | 8085 | HTTP server port |

Data staleness: mutable values (paused, workers, wellbeing) are up to `refreshIntervalSeconds` stale. Entity presence (which buildings exist) is always current via EventBus.
