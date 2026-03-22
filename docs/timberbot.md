---
name: timberbot
description: Play Timberborn autonomously via timberbot.py. Keep beavers alive, wellbeing high, needs met.
version: "2.0"
---
# AI Playbook - Playing Timberborn via API

How to play Timberborn autonomously using `timberbot.py`. Copy this file to `~/.claude/skills/timberbot/SKILL.md` to use as a Claude Code skill.

Play the game using `python timberbot/script/timberbot.py` commands only. NEVER use inline python or pipe through python -c.

## Goals (priority order)

1. **No deaths.** Food and water must never hit zero
2. **Meet critical needs.** Check `beavers` for critical needs, fix them
3. **Grow the colony.** Build infrastructure, expand food/water/housing

## Loop

Every turn:

1. `summary` - check day, resources, population, tree counts
2. `beavers` - check wellbeing and critical needs
3. `alerts` - check unstaffed, unpowered, unreachable buildings
4. Check trees: `markedGrown` is choppable supply. If < 10, run `tree_clusters` and mark trees near the best cluster
5. Decide what to do based on what's critical
6. Take ONE action (place building, set priority, plant crops, mark trees, adjust workers)
7. `set_speed speed:2` or `speed:3` if stable, `speed:1` if struggling

## Placement workflow (MANDATORY every time)

1. `scan` the area -- find paths, buildings, open ground. `.dead` tiles are buildable stumps
2. Check terrain height with `map` -- z MUST match terrain height
3. Pick orientation so entrance FACES the path
4. `place_building` with correct coords, z, and orientation
5. `scan` again -- confirm entrance faces the path. If not, demolish and redo
- **NEVER skip steps 1, 2, or 5**
- Scan suffixes: `.dead` = buildable stump, `.seedling` = growing (blocked), `.entrance` = door tile

## Orientation

Entrance directions on the visual map:
- **north** = +y (up on map)
- **south** = -y (down on map)
- **east** = +x (right on map)
- **west** = -x (left on map)

Pick the direction that points FROM the building TOWARD the path. If the path is above the building on the map, use north. If the path is to the right, use east.

## Decision tree

- **No food?** Place gatherer flags near berry bushes, place farmhouse, plant kohlrabi
- **No water?** Check pump is running, place more tanks, set pump to VeryHigh
- **Beavers homeless?** Place barracks or rowhouses near paths
- **No logs?** Run `tree_clusters` to find densest grown trees, place lumberjack there
- **markedGrown < 10?** Run `tree_clusters`, then `mark_trees` around the best cluster center
- **No power?** Place Large Power Wheel (needs workers) or Compact Water Wheel (needs flowing water)
- **No planks?** Need power first, then Industrial Lumber Mill
- **Everything stable?** Speed up to 3, check back next turn

## Build order (Iron Teeth)

1. Lumberjack flags near grown trees (wood supply)
2. Deep Water Pump + Small Tanks (water). Set tank good to Water immediately
3. Gatherer flags near berry bushes (food)
4. Farmhouse + plant Kohlrabi (sustainable food, 3-day cycle, eaten raw)
5. Housing (Barrack/Rowhouse)
6. Compact Water Wheel or Large Power Wheel (power)
7. Industrial Lumber Mill (planks, needs 75hp power)

## Food rules

- **FOOD IS THE #1 PRIORITY** -- 13 beavers eat ~15 berries/day
- Gatherers only collect wild berries. Berries WILL run out
- Build Farmhouse EARLY (by day 3-4), plant Kohlrabi (3-day cycle, no processing)
- If food drops below 30, slow to speed 1
- If food hits 0, beavers die
- Set VeryHigh priority on food and water buildings

## Water rules

- Deep Water Pump must straddle land/water edge at z=1
- Set tank good to Water immediately after placing
- Water pumps need workers

## Tree rules

- Use `tree_clusters` to find the densest cluster of grown trees
- Place lumberjacks adjacent to paths near the best cluster
- Mark trees in a wide area around lumberjacks (mark_trees x1 y1 x2 y2 z)
- Keep markedGrown above 10 so lumberjacks always have work

## Building sizes

WoodWorkshop 2x4, HaulingPost 3x2, Barrack 3x2, DC 3x3, Rowhouse 1x2, FarmHouse 2x2, flags 1x1, Path 1x1

## General rules

- ALWAYS use `python timberbot/script/timberbot.py <method>` for everything
- Use `find source:buildings name:X` to look up building IDs
- Use `scan` before and after every placement
- Set VeryHigh priority on food and water buildings
- One action per turn, verify it worked, then next action
- Goods are hauled by idle beavers. Don't over-employ the colony early
- Power buildings must touch what they power -- no gaps
- Oasis maps have standing water (no flow). Use Large Power Wheel, not Compact Water Wheel
