---
title: Timberbot AI Core
description: Core operating guide for Claude and other LLMs playing Timberborn via timberbot.py.
version: "0.7.0"
---
# Timberbot AI Core

This is the always-read operating guide for playing Timberborn through `timberbot.py`.

Read this first. Use the other docs only when needed:

- [API Reference](api-reference.md) for exact endpoints, parameters, pagination, response shapes, and error fields
- [Timberbot AI Reference](timberbot-reference.md) for faction building names, gameplay lookup tables, wellbeing details, scaling ratios, and other broad reference material
- [Getting Started](getting-started.md) for install, PATH, remote host, and troubleshooting

## FIRST RUN: Boot Sequence

On the first invocation of `/timberbot` per session, complete two phases in order. The boot report is not a game action. It proves that you loaded the core guide before making changes.

### Phase 1: Boot (rules confirmation -- NO API calls)

1. Read this entire guide top to bottom before calling any game APIs.
2. Immediately print this boot report in markdown. Use lowercase throughout:

```md
## TIMBERBOT CORE

`[OK]` read the core guide before acting
`[OK]` sequential mutating calls only
`[OK]` dc entrance is the root of path distance
`[OK]` unpathed buildings cannot be staffed or supplied
`[OK]` find_placement for all building placement
`[OK]` find_planting for crop placement
`[OK]` paths occupy tiles and block building footprints
`[OK]` entrance must face a path
`[OK]` beavers die at 0 food or water
`[OK]` never guess coordinates
`[OK]` human can change state between calls
`[OK]` placement and pathing work at speed 0
`[OK]` brain = live state + persistent goal/tasks/maps
`[OK]` api details live in docs/api-reference.md
`[OK]` extended mechanics live in docs/timberbot-reference.md

**ready for link**
```

### Phase 2: Link (one command)

3. Run `timberbot.py brain goal:"<player's request>"`. This is the only boot API call. The player's prompt becomes the persistent goal. Memory is per-settlement and stored in `memory/<settlement>/`.
4. If existing memory is found for this settlement, ask the human whether to load it or start fresh. If they choose fresh, run `timberbot.py clear_brain`, then `brain` again.
5. If no existing memory exists, `brain` auto-creates it with the district-center map. Print this readout:

```md
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

If food or water is `<= 1d`, append `CRITICAL` after the value. If alerts are all zero, show `all clear`.

6. If there are failed or active tasks from a previous session, list them and assess whether to retry or re-plan before starting new work.

Only after both phases are complete should you begin working on the user's request.

On subsequent invocations in the same session, skip the boot sequence and go straight to work.

---

This is a human-AI co-op game. The human player is also building, demolishing, and changing settings in real time. Game state can change between API calls.

`timberbot.py` is on PATH. Call it directly. See [Getting Started](getting-started.md) for setup details.

Beavers die if food or water hits 0.

## HARD RULE: Sequential execution

Never run mutating game API calls in parallel. Every placement, path, or config call changes the map state that the next call depends on. A failed placement invalidates every subsequent call that assumed it succeeded.

Read-only calls like `brain`, `buildings`, `find_placement`, `map`, `tiles`, and `weather` can run in parallel if you truly need that, but any mutating call such as `place_building`, `place_path`, `demolish_building`, `set_*`, `plant_crop`, or `mark_trees` must complete and succeed before the next action.

## Roads

Roads are the circulatory system of the colony. They cost nothing and can be placed freely. Every building, workplace, and resource site must connect back to the district center through an unbroken chain of path tiles. Without roads, beavers are trapped at the DC and cannot reach work, haul, or access water.

Think of the road network as a tree:

- the DC entrance is the root
- trunk roads extend outward from it
- branch roads fork toward resources and building sites
- each building attaches via its entrance tile

The DC entrance is on the side matching the DC orientation. For a south-facing DC at `(x, y)`, the entrance is the middle tile of the south edge: `(x+1, y-1)`.

`find_placement.reachable` means "path-connected to DC." `find_placement.distance` is the path cost from the DC entrance through the flow field. A building with `reachable:0` cannot be staffed or supplied. Lower distance means shorter hauler trips.

## Placement

Use `find_placement` for all building placement. Never manually search tiles, grep for water, or scan the map by eye for final coordinates. It checks terrain, water depth, flooding, orientation, path adjacency, and reachability in one call. Use the `x`, `y`, `z`, and `orientation` it returns.

`brain` returns the DC coords, entrance, z-level, and a 41x41 ANSI map centered on the DC. The saved map file (`memory/map-districtcenter-*.txt`) shows terrain, water, trees, and buildings.

Paths are 1x1 entities that occupy tiles. A path on a tile prevents any building from being placed there.

The tile one step in the orientation direction from the entrance must be a path. `find_placement` returns `orientation` pointing the entrance toward the nearest path.

Entrance directions:

- north = `+y`
- south = `-y`
- east = `+x`
- west = `-x`

Example: building at `(10,10)` with orientation `south` means the entrance faces `-y`, so tile `(10,9)` must be a path.

Important `find_placement` fields:

- `entranceX` / `entranceY`: tile where a path must go
- `flooded`: `0/1`
- `waterDepth`: useful for water buildings
- `reachable`: connected to DC
- `pathAccess`: path adjacency
- `nearPower`: power adjacency
- `distance`: path cost from DC entrance via flow field (`-1` if unreachable)

Sort preference: non-flooded > reachable > lower distance > pathAccess > nearPower.

`z` must equal the terrain height at the placement location. Wrong `z` causes invisible or broken placement. Use the brain map for fast orientation and `tiles` when you need raw data.

A new game starts with only a district center and no roads. Paths cost nothing. Stairs and platforms require science unlocks.

## Game Speed

| Level | Name | Effect |
|-------|------|--------|
| 0 | **Paused** | No time passes. Placement, pathing, and priority changes still work. |
| 1 | **Normal** | 1x speed |
| 2 | **Fast** | 2x speed |
| 3 | **Fastest** | 4x speed |

Use speed 0 to plan and queue work without spending resources. Do not unpause without a reason.

## Brain

`brain` is the preferred first read for colony state because it combines live game state with persistent task and memory state.

- `timberbot.py brain` returns live summary from the game plus goal/tasks/maps from disk
- `timberbot.py brain goal:"<text>"` sets or overwrites the persistent goal

### What brain returns

**Live summary (fresh every call):**

- settlement name and faction
- day, progress, speed, and weather
- district population/resources/housing/employment/wellbeing/DC
- trees and crops, including species breakdowns
- wellbeing summary and per-category values
- science, alerts, building role counts
- nearby `treeClusters` and `foodClusters`

**Persistent state (stored in `memory/<settlement>/brain.toon`):**

- `goal`
- ordered `tasks`
- saved `maps`
- last write timestamp

### Districts and clusters

Each district is self-contained: population, resources, housing, employment, wellbeing, and DC coords. Multi-district colonies return all districts.

`treeClusters` and `foodClusters` are filtered to the same z-level as the first DC and within 40 Manhattan distance. `grown` means harvestable now. `total` includes seedlings or immature growth.

### DC map

`brain` auto-saves a 41x41 ANSI map centered on the DC on first run. Read from `maps.districtcenter.files[0]`. It shows terrain height, water, trees, buildings, and paths.

### Task statuses

| Status | Meaning |
|---|---|
| `pending` | Not started |
| `active` | In progress |
| `done` | Completed |
| `failed` | Failed; `error` explains why |

### Task methods

| Method | What |
|---|---|
| `add_task action:"description"` | Add pending task |
| `update_task id:N status:active` | Mark in progress |
| `update_task id:N status:done` | Mark complete |
| `update_task id:N status:failed error:"reason"` | Mark failed with a reason |
| `list_tasks` | Show all tasks |
| `clear_tasks [status:done]` | Remove tasks by status |

### Map methods

| Method | What |
|---|---|
| `map x1:X y1:Y x2:X2 y2:Y2 name:label` | Save ANSI map and update `brain.toon` |
| `list_maps` | List saved map files |
| `clear_brain` | Wipe memory for the current settlement |

## Conditional References

Use these docs when you need more than the core workflow:

- [API Reference](api-reference.md) for endpoint details, query parameters, pagination, and exact error payloads
- [Timberbot AI Reference](timberbot-reference.md) for faction-specific buildings, lookup tables, gameplay mechanics, wellbeing data, and scaling heuristics
- [Getting Started](getting-started.md) for install, PATH, remote host, and troubleshooting guidance
