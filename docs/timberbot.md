---
name: timberbot
description: Play Timberborn autonomously via timberbot.py. Keep beavers alive, wellbeing high, needs met.
version: "3.0"
---
# AI Prompt - Playing Timberborn via API

How to play Timberborn autonomously using `timberbot.py`. Copy this file to `~/.claude/skills/timberbot/SKILL.md` to use as a Claude Code skill.

Play the game using `python timberbot/script/timberbot.py` commands only. NEVER use inline python or pipe through python -c.

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

1. `visual` the area -- see the map with colored tiles. Find a clear rectangle that fits the building footprint (e.g. 3x2 for Barrack)
2. Check terrain height with `map` -- z MUST match terrain height or building clips underground
3. Verify every tile in the footprint is open (ground dots or .dead stumps). Count the tiles against building size
4. Pick orientation so entrance FACES the path
5. `place_building` with correct coords, z, and orientation
6. `visual` again -- confirm entrance faces the path. If not, demolish and redo
- **NEVER skip steps 1, 2, 3, or 6**
- **NEVER guess placement** -- always visually confirm the footprint fits before placing
- Scan suffixes: `.dead` = buildable stump, `.seedling` = growing (blocked), `.entrance` = door tile

## Z-level rules

- ALWAYS check terrain height before placing. Use `map x1:X y1:Y x2:X2 y2:Y2` to read terrain
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

- New buildings have TWO priorities: `construction` (while building) and `workplace` (when finished)
- ALWAYS set BOTH priorities on new buildings: `set_priority type:construction` AND `set_priority type:workplace`
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

- **FOOD IS THE #1 PRIORITY** -- each beaver eats ~1 food/day
- Gatherers only collect wild berries. Berries WILL run out
- Build Farmhouse EARLY (by day 3-4), plant Kohlrabi (3-day cycle, no processing)
- Need ~1 farmhouse per 8 beavers with full kohlrabi fields
- If food drops below 30, prioritize farming
- If food hits 0, beavers die. Pause non-food buildings to free haulers
- Crops grow during drought as long as soil is irrigated (near standing water). Keep planting and farming year-round on oasis maps
- Set VeryHigh priority on food and water buildings

## Water rules

- Deep Water Pump must straddle land/water edge
- Set tank good to Water immediately after placing
- Water pumps need workers
- 2 pumps serve ~15 beavers. Add a third pump before population exceeds 15
- During drought, slow to speed 1 if water below 50

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

## Building sizes

WoodWorkshop 2x4, HaulingPost 3x2, Barrack 3x2, DC 3x3, Rowhouse 1x2, FarmHouse 2x2, Inventor 2x2, Forester 2x2, flags 1x1, Path 1x1, LargePowerWheel 3x3, IndustrialLumberMill 2x3, DeepWaterPump 3x2

## Building configuration

- `set_haul_priority building_id:X prioritized:true` -- haulers deliver goods here first (use on breeding pods, critical buildings)
- `set_recipe building_id:X recipe:RecipeId` -- set manufactory recipe (lumber mill, gear workshop, bot factory). Use invalid name to list available
- `set_farmhouse_action building_id:X action:planting` -- prioritize planting over harvesting. Use `action:harvesting` to reset to default
- `set_plantable_priority building_id:X plantable:Pine` -- forester prioritizes this tree type. Use `plantable:none` to clear

## General rules

- ALWAYS use `python timberbot/script/timberbot.py <method>` for everything
- Use `find source:buildings name:X` to look up building IDs
- Use `scan` before and after every placement
- Set VeryHigh priority on food and water buildings (BOTH construction and workplace)
- Set haul priority on breeding pods so beavers deliver food there
- ALWAYS keep 1-2 idle haulers (unassigned beavers) -- breeding pods and construction need haulers to deliver materials
- If breeding halted, pause non-essential buildings (power wheel, lumber mill, lumberjack) to free haulers
- One action per turn, verify it worked, then next action
- Goods are hauled by idle beavers. Don't over-employ the colony early
- Oasis maps have standing water (no flow). Use Large Power Wheel, not Compact Water Wheel
- When colony is struggling, pause non-essential buildings to free workers for hauling
- Lumberjacks MUST stay staffed -- no logs means no planks means no construction
