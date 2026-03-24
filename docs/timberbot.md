---
name: timberbot
description: Play Timberborn autonomously via timberbot.py. Keep beavers alive, wellbeing high, needs met.
version: "4.2"
---
# AI Prompt - Playing Timberborn via API

How to play Timberborn autonomously using `timberbot.py`. Copy this file to `~/.claude/skills/timberbot/SKILL.md` to use as a Claude Code skill.

Play the game using `timberbot.py` commands only. NEVER use inline python or pipe through python -c. See [getting-started.md](getting-started.md) for PATH setup.

## When you don't know something

**API reference:** Read `docs/api-reference.md` for all available endpoints, request/response formats, and parameters. Don't guess at endpoint syntax -- look it up.

**Game mechanics:** Search the Timberborn wiki FIRST before guessing:

- Use `WebSearch` for "timberborn wiki <topic>" (e.g. "timberborn wiki farmhouse range", "timberborn wiki wellbeing needs")
- Use `WebFetch` on wiki pages to read the details
- The wiki covers building stats, ranges, mechanics, and interactions that aren't in this prompt
- NEVER guess at game mechanics you haven't verified -- wrong assumptions cause colony deaths

## Goals (priority order)

1. **No deaths.** Food and water must never hit zero
2. **Meet critical needs.** Check `beavers` for critical needs, fix them
3. **Grow the colony.** Build infrastructure, expand food/water/housing

## Loop

Every turn:

1. `summary` - ONE call has everything: day, resources, population, trees, housing, employment, wellbeing, alerts, science, critical needs
2. If summary shows critical/miserable > 0: run `beavers` for detail on who needs help
3. If summary shows alerts > 0: run `alerts` for detail on which buildings
4. Check trees: `markedGrown` is choppable supply. If < 10, run `tree_clusters` and mark trees near the best cluster
5. Decide what to do based on what's critical
6. Take ONE action (place building, set priority, plant crops, mark trees, adjust workers)
7. `set_speed speed:2` or `speed:3` if stable, `speed:1` if struggling

## Placement workflow (MANDATORY every time)

!!! warning "ALWAYS use find_placement"
    Never guess coordinates or z-level. The server validates terrain, occupancy, water, and picks the best orientation.

1. `find_placement prefab:Name x1:X y1:Y x2:X2 y2:Y2` -- server finds all valid spots with correct z, orientation, reachability, and power adjacency
2. Pick the first result with `reachable: true` -- this means it's connected to the district center via roads
3. `place_building` with the coords, z, and orientation from the result
4. `visual` the area -- confirm building placed correctly
- Results sorted by: reachable > pathAccess > nearPower > pathCount
- If find_placement returns no reachable results, widen the search area or build paths to connect

## Path and stair placement

!!! warning "NEVER place paths or stairs with place_building"
    Use `place_path` for ALL roads and stairs. It auto-places stairs at z-level changes and platforms for multi-level transitions.

- `place_path x1:X y1:Y x2:X2 y2:Y2` -- route a straight-line path (axis-aligned: x1==x2 or y1==y2)
- Auto-places stairs at z-level transitions (e.g. z=2 to z=3)
- Auto-places platforms for multi-level jumps
- Returns `{placed, stairs, skipped, errors}`
- To extend the path network, run `place_path` from an existing path tile toward the target area
- After placing paths, recheck `find_placement` -- previously unreachable spots may now be reachable

## Z-level rules

- `visual` shows terrain height: empty ground shows z % 10 digit, background shading encodes height (dark=z0-9, medium=z10-19, bright=z20-22). Height legend at bottom shows exact z values. Use `map` for raw data when needed
- z MUST equal the terrain height at the placement location
- If terrain is 2, place at z:2. If terrain is 4, place at z:4
- Placing at wrong z causes underground clipping (building invisible/broken)
- Different areas of the map have different terrain heights -- never assume z:2

## Orientation

Entrance directions on the visual map:
- **north** = +y (up on map)
- **south** = -y (down on map)
- **east** = +x (right on map)
- **west** = -x (left on map)

Pick the direction that points FROM the building TOWARD the path. If the path is above the building on the map, use north. If the path is to the right, use east.

## Priority rules

!!! tip "Two priorities per building"
    New buildings have TWO priorities: `construction` (while building) and `workplace` (when finished). ALWAYS set BOTH on new buildings.
- Food and water buildings get VeryHigh on both
- When workers are scarce, pause non-essential buildings (lumber mill, power wheel) to free workers for critical tasks

## Decision tree

- **No food?** Place gatherer flags near berry bushes, place farmhouse, plant kohlrabi
- **No water?** Check pump is running, place more tanks, set pump to VeryHigh. 2 pumps for <15 beavers, 3 pumps for 15+
- **Beavers homeless?** Place barracks or rowhouses near paths
- **No logs?** Run `tree_clusters` to find densest grown trees, place lumberjack there
- **markedGrown < 10?** Run `tree_clusters`, then `mark_trees` around the best cluster center
- **No power?** Place Large Power Wheel (needs workers) or Compact Water Wheel (needs flowing water)
- **No planks?** Need power first, then Industrial Lumber Mill
- **Trees running low?** Plant Pine near lumberjacks: `plant_crop x1:X y1:Y x2:X2 y2:Y2 z:Z crop:Pine` (12-day growth, 2 logs each)
- **Need science?** Build Inventor (2x2), set both priorities VeryHigh. Multiple inventors stack
- **Unlock buildings?** `unlock_building building:Name.IronTeeth` -- check `science` for costs and points
- **Everything stable?** Speed up to 3, check back next turn

## Build order (Iron Teeth)

1. Lumberjack flags near grown trees (wood supply)
2. Deep Water Pump + Small Tanks (water). Set tank good to Water immediately
3. Gatherer flags near berry bushes (food)
4. Farmhouse + plant Kohlrabi (sustainable food, 3-day cycle, eaten raw)
5. Housing (Barrack/Rowhouse)
6. Compact Water Wheel or Large Power Wheel (power)
7. Industrial Lumber Mill (planks, needs 75hp power)
8. Forester near lumberjacks (unlock 30 science) + plant Pine for sustainable wood
9. Additional Inventors for faster science
10. Third water pump when population exceeds 15

## Food rules

!!! danger "FOOD IS THE #1 PRIORITY"
    Each beaver eats ~1 food/day. If food hits 0, beavers die. Pause non-food buildings to free haulers.

- Gatherers only collect wild berries. Berries WILL run out
- Build Farmhouse EARLY (by day 3-4), plant Kohlrabi (3-day cycle, no processing)
- Need ~1 farmhouse per 8 beavers with full kohlrabi fields
- Check `foodDays` in summary -- if below 3, prioritize farming
- Crops grow during drought as long as soil is irrigated (near standing water). Keep planting and farming year-round on oasis maps
- Use `find_planting crop:Kohlrabi building_id:X` to find valid irrigated spots within a farmhouse's range
- Use `building_range building_id:X` to see how many tiles (and moist tiles) a farmhouse covers
- Set VeryHigh priority on food and water buildings

## Water rules

- Deep Water Pump must straddle land/water edge
- Set tank good to Water immediately after placing
- Water pumps need workers -- check staffing regularly, VeryHigh priority doesn't guarantee assignment when colony is small
- 2 pumps serve ~15 beavers. Add a third pump before population exceeds 15
- Check `waterDays` in summary -- if below 2 during drought, slow to speed 1
- Build enough water STORAGE before drought. 3 pumps producing during temperate weather means nothing if tanks are too small. Target waterDays > 5 before drought hits
- During drought, water is consumed but not produced. Every stored unit counts

## Tree rules

- Use `tree_clusters` to find the densest cluster of grown trees
- Place lumberjacks adjacent to paths near the best cluster
- Mark trees in a wide area around lumberjacks (mark_trees x1 y1 x2 y2 z)
- Keep markedGrown above 10 so lumberjacks always have work
- Plant Pine for sustainability: `plant_crop crop:Pine` near lumberjacks (12-day growth, 2 logs)
- Birch grows faster (8 days) but yields only 1 log

## Power rules

- Power transfers through ADJACENT buildings only -- no gaps, paths don't conduct
- All powered buildings must form an unbroken chain touching the power source
- Plan layout so power wheel -> lumber mill -> other buildings are all touching
- Large Power Wheel: 300hp, needs workers
- Check `nominalPowerInput` on buildings to plan power budget
- `find_placement` results include `nearPower` -- use it to place powered buildings adjacent to the power chain

## Building sizes

WoodWorkshop 2x4, HaulingPost 3x2, Barrack 3x2, DC 3x3, Rowhouse 1x2, FarmHouse 2x2, Inventor 2x2, Forester 2x2, flags 1x1, Path 1x1, LargePowerWheel 3x3, IndustrialLumberMill 2x3, DeepWaterPump 3x2, DoubleShower 1x2 (1 tile on water, 1 tile on land -- straddles water edge like a pump)

## Building configuration

- `set_haul_priority building_id:X prioritized:true` -- haulers deliver goods here first (use on breeding pods, critical buildings)
- `set_recipe building_id:X recipe:RecipeId` -- set manufactory recipe (lumber mill, gear workshop, bot factory). Use invalid name to list available
- `set_farmhouse_action building_id:X action:planting` -- prioritize planting over harvesting. Use `action:harvesting` to reset to default
- `set_plantable_priority building_id:X plantable:Pine` -- forester prioritizes this tree type. Use `plantable:none` to clear

## API quick reference -- when to use each method

| Situation | Method |
|---|---|
| Colony status check | `summary` |
| Beaver needs detail | `beavers` |
| Wellbeing breakdown | `wellbeing` |
| Building alerts | `alerts` |
| Find berry bushes | `gatherables` |
| Find building by name | `find source:buildings name:X` |
| Check farmhouse coverage | `building_range building_id:X` |
| Find irrigated crop spots | `find_planting crop:Kohlrabi building_id:X` |
| Find building placement | `find_placement prefab:Name x1:X y1:Y x2:X2 y2:Y2` |
| Place a building | `place_building prefab:Name x:X y:Y z:Z orientation:south` |
| Place roads/stairs | `place_path x1:X y1:Y x2:X2 y2:Y2` |
| Remove a building | `demolish_building building_id:X` |
| Set priority | `set_priority building_id:X priority:VeryHigh type:construction` |
| Adjust workers | `set_workers building_id:X count:N` |
| Pause/unpause | `pause_building building_id:X` / `unpause_building building_id:X` |
| Set tank good | `set_good building_id:X good:Water` |
| Set hauler priority | `set_haul_priority building_id:X prioritized:true` |
| Set recipe | `set_recipe building_id:X recipe:RecipeId` |
| Set farmhouse mode | `set_farmhouse_action building_id:X action:planting` |
| Set forester tree | `set_plantable_priority building_id:X plantable:Pine` |
| Mark trees for cutting | `mark_trees x1:X y1:Y x2:X2 y2:Y2 z:Z` |
| Stop cutting trees | `clear_trees x1:X y1:Y x2:X2 y2:Y2 z:Z` |
| Plant crops | `plant_crop x1:X y1:Y x2:X2 y2:Y2 z:Z crop:Kohlrabi` |
| Find tree clusters | `tree_clusters` |
| Unlock building | `unlock_building building:Name.IronTeeth` |
| Check science | `science` |
| Set game speed | `set_speed speed:3` |
| Extend work hours | `set_workhours end_hours:20` |
| View area | `visual x:X y:Y radius:10` |
| Check terrain | `map x1:X y1:Y x2:X2 y2:Y2` |

## General rules

- ALWAYS use `timberbot.py <method>` for everything
- Use `find source:buildings name:X` to look up building IDs
- Use `visual` before and after every placement
- Set VeryHigh priority on food and water buildings (BOTH construction and workplace)
- Set haul priority on breeding pods so beavers deliver food there
- ALWAYS keep 1-2 idle haulers (unassigned beavers) -- breeding pods and construction need haulers to deliver materials
- If breeding halted, `pause_building` non-essential buildings (power wheel, lumber mill, lumberjack) to free haulers. `unpause_building` gradually as population recovers
- One action per turn, verify it worked, then next action
- Goods are hauled by idle beavers. Don't over-employ the colony early
- Oasis maps have standing water (no flow). Use Large Power Wheel, not Compact Water Wheel
- When colony is struggling, pause non-essential buildings to free workers for hauling
- Lumberjacks MUST stay staffed -- no logs means no planks means no construction
- Use `demolish_building` to remove misplaced or unnecessary buildings
- Use `set_workhours end_hours:20` to extend work during crises (default is 18)
- NEVER use the `/api/debug` endpoint during gameplay -- it is for development and testing only, not for cheating or bypassing game mechanics

## Wellbeing rules

Wellbeing can go up to 77. Target 15+ for a thriving colony. Use `wellbeing` endpoint to see per-category breakdown.

**Categories and what satisfies them:**

| Category | Max | How to improve |
|---|---|---|
| BasicNeeds | 5 | Food, water, sleep, shelter -- keep these full first |
| SocialLife | 2 | Campfire (+1), RooftopTerrace (+1) |
| Fun | 17 | Scratcher (+1), SwimmingPool (+1), ExercisePlaza (+3), MudBath (+3), WindTunnel (+3), Motivatorium (+5) |
| Nutrition | 17 | Each unique food type: Kohlrabi (+1), Coffee (+3), FermentedSoybean (+2), CornRation (+2), etc. |
| Aesthetics | 10 | Lantern (+1), Brazier (+1), Roof (+1), BeaverBust (+1), BeaverStatue (+2), Bell (+1), DecorativeClock (+2) |
| Awe | 26 | Wonders: LaborerMonument (+3), FlameOfUnity (+5), TributeToIngenuity (+8), EarthRepopulator (+10) |

**Workflow:**
1. Run `wellbeing` to see which categories are low across the population
1a. Run `beavers` to see per-beaver unmet needs (each beaver shows `unmet` field with specific needs like Campfire, Detailer)
2. Pick the category with the biggest gap between current and max
3. Build/unlock the cheapest building that satisfies that category
4. Nutrition requires food VARIETY -- not just more kohlrabi, but different food types (need processing buildings)
- During crises, wellbeing drops fast (-12 possible). Recovery takes many days
- Do NOT neglect wellbeing for economy -- miserable beavers work slower and may die

## Worker management

- **Death spiral warning**: too many buildings competing for too few workers causes starvation. Beavers can't haul food/water if all assigned to workplaces
- ALWAYS keep 2-4 idle/unemployed beavers for hauling. Check `unemployed` in summary
- When population drops, immediately pause non-essential buildings: inventors, metalsmith, bot factory, gear workshop, foresters
- Keep only critical buildings staffed: farmhouses, water pumps, 1 lumberjack
- Set `set_haul_priority` on breeding pods so food gets delivered for breeding
- Unpause buildings gradually as population recovers
