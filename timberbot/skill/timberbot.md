---
name: timberbot
description: Play Timberborn via timberbot.py. Use when controlling a running Timberborn game.
version: "1.0"
---
# Timberbot

You are playing Timberborn via `timberbot.py`. The human player is also building in real time. Game state changes between API calls.

## Rules

- `timberbot.py` is on PATH. Run directly: `timberbot.py <command> key:value`. Never use `python` prefix. Never `cd`. Never use full paths.
- Never run mutating calls in parallel. Each changes state the next depends on.
- Always use `find_placement` before placing buildings. Never guess coordinates.
- Always run `timberbot.py prefabs | grep -i <keyword>` before placing a building you haven't placed this session.
- Prefabs require faction suffix (e.g. `LumberjackFlag.Folktails`, NOT `LumberjackFlag`).

## Boot

Colony state may be pre-loaded in your system prompt (look for `## CURRENT COLONY STATE`). If present, print the readout below from it. If not, run `timberbot.py brain goal:"<goal>"` first.

```
> **settlement** <name> | <faction>
> **day** <N> speed:<S> | weather: <temperate/drought> <N>t/<N>d
> **pop** <adults>a <children>c | beds: <occ>/<total> | workers: <assigned>/<vacancies> idle:<unemployed>
> **supply** food:<F>d water:<W>d logs:<L> planks:<P>
> **wellbeing** <avg>/77 | miserable:<N> critical:<N>
> **alerts** <unstaffed> unstaffed | <unpowered> unpowered | <unreachable> unreachable
```

## Priority order

1. **Water** -- beavers die without water faster than food. Place pumps FIRST. Waterfront tiles are limited -- once a path or building takes a waterfront tile, no pump can go there. If `waterDays: 0` but water buildings exist, those are likely Ancient Aquifer Drills (need 400hp power, not early-game). Place water pumps -- they work immediately with no power.
2. **Food** -- gatherers for berries, then farms. 1 farmhouse per 8 beavers.
3. **Housing** -- homeless beavers have zero wellbeing.
4. **Roads** -- every building needs a path connection to the DC. Build roads from DC outward.
5. **Workers** -- assign workers to buildings. Check `unemployed` and `vacancies` in summary.

## Speed

- Unpause AFTER giving beavers work, not before. If all workers are idle, unpausing wastes food/water.
- Speed 0 = plan and place. Speed 1-3 = execute. Speed 0 for too long = nothing happens.

## Placement workflow

1. `find_placement prefab:<name> x1:... y1:... x2:... y2:...` -- find valid spots
2. Pick best result (reachable > lower distance > pathAccess > nearPower)
3. `place_path` from DC to the entrance coords if not already connected
4. `place_building` at the coords and orientation from find_placement
5. `set_priority` and `set_workers` as needed

## Locations

`brain` output includes `locations` -- named spatial anchors (dc, forest, berries). Use these coordinates for search areas in `find_placement`.

## Deep reference (read on demand)

For building tables, crop details, wellbeing, manufacturing chains, faction-specific info:
- Read `docs/timberbot.md` in the mod folder

For exact endpoint shapes, error codes, pagination, response formats:
- Read `docs/api-reference.md` in the mod folder
