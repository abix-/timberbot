# v0.7.0 release notes

## brain

One command for everything: `timberbot.py brain`. Always fresh from game. Replaces `summary`, `save_brain`, `load_brain` as separate commands. Persists to `memory/brain.toon` in compact toon format.

What brain gives you:
- **faction** -- auto-detected from prefabs (Folktails or IronTeeth)
- **dc** -- district center coords, z-level, orientation, entrance position
- **summary** -- full structured colony snapshot (population, resources, weather, drought, alerts, wellbeing)
- **buildings** -- counts by role (water, food, housing, wood, storage, power, science, production, leisure, paths)
- **treeClusters** -- top tree clusters on DC z-level within 40 tiles, sorted by grown count
- **foodClusters** -- top gatherable food clusters (berries, bushes) on DC z-level within 40 tiles
- **maps** -- region index with file paths and bounding box coords
- **tasks** -- ordered work queue with status tracking (pending/active/done/failed)

First run auto-creates brain and saves a 41x41 ANSI map centered on DC.

### food_clusters endpoint

New server-side endpoint (`/api/food_clusters`). Grid-clustered gatherable food sources (berries, bushes) excluding trees. Same format as tree_clusters.

### task system

Persistent ordered work queue in brain. Track multi-step plans across sessions.
- `add_task action:"description"` -- add pending task
- `update_task id:N status:done|failed [error:"reason"]` -- update status
- `list_tasks` / `clear_tasks` -- manage queue

### toon persistence

brain.toon uses `toons.dump`/`toons.load` for roundtrip persistence. 43% smaller than JSON (1.6KB vs 2.8KB). buildings.json stores the slim building index separately.

## map improvements

- **delta-encoded ANSI** -- map output dropped from ~35KB to ~6KB. Only emits escape codes when background/foreground changes from previous tile.
- **x1/y1/x2/y2 syntax** -- replaces `x/y/radius`, consistent with all other area commands.
- **name param** -- `map ... name:label` saves ANSI map to `memory/` and indexes it in brain.

## data format improvements

### uniform schema
All list endpoints emit identical keys on every object in both toon and json. No conditional/optional fields. ~26% token savings from toon CSV rendering.

### 0/1 booleans
All boolean values stored as `int` (0/1) at the data layer. Not a format translation -- the cached classes use `int` fields converted from game bools with `? 1 : 0`.

### tiles
- compact CSV with uniform schema (33KB -> 14KB for same area)
- z-range occupants: `DistrictCenter:z2-6` instead of repeating 5 times
- occupants column moved to last for readability

### find_placement distance
New `distance` field. Uses game's flow-field pathfinding to report actual path cost from DC entrance. Lower = closer = better hauling efficiency.

## breaking changes

- `summary` still works but `brain` is the preferred command for AI players -- brain adds persistence, task tracking, resource clusters, faction detection, spatial memory
- `save_brain` / `load_brain` removed -- use `brain`
- `map` syntax: `x:X y:Y radius:N` -> `x1:X y1:Y x2:X2 y2:Y2`
- all boolean fields return `0`/`1` instead of `true`/`false`
- `tiles` toon field order changed (occupants last)
- `tiles` toon always includes all fields (uniform schema)
- brain persists as `brain.toon` (toon format), not `brain.json`
