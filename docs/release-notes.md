# v0.7.0 release notes

## spatial memory

AI sessions no longer start blind. Colony state and maps persist to disk in `Documents/Timberborn/Mods/Timberbot/memory/`.

- `save_brain` -- snapshots DC location (with entrance coords), all buildings with IDs/coords, colony summary to `memory/brain.json`
- `load_brain` -- reads it back at session start
- `list_maps` -- lists saved map files
- `map ... name:label` -- saves ANSI map to `memory/map-{name}-{x1}x{y1}y-{x2}x{y2}y.txt` with full encoding preserved

## map improvements

- **delta-encoded ANSI** -- map output dropped from ~35KB to ~6KB for same area. Only emits escape codes when background/foreground changes from previous tile. Renders inline instead of persisting to file.
- **x1/y1/x2/y2 syntax** -- `map` now uses bounding box coordinates consistent with `tiles`, `find_placement`, `place_path`, and all other area commands. Replaces `x/y/radius`.

## uniform schema

All list endpoints now emit identical keys on every object in both toon and json formats. No more conditional/optional fields.

- toon output renders as compact CSV tables (via toons library auto-detection) instead of verbose YAML-like fallback. ~26% token savings across endpoints.
- missing values get defaults: `""` for strings, `0` for numbers

## 0/1 booleans

All boolean values stored as `int` (0/1) at the data layer. Not a format translation -- the cached data itself is 0/1. Applies to both toon and json output across all endpoints.

Affected fields: `finished`, `paused`, `reachable`, `powered`, `isGenerator`, `isConsumer`, `hasMaterials`, `floodgate`, `isClutch`, `clutchEngaged`, `readyToProduce`, `isWonder`, `wonderActive`, `alive`, `grown`, `marked`, `isBot`, `hasHome`, `contaminated`, `isCarrying`, `overburdened`, `anyCritical`, `favorable`, `critical`, `active`, `entrance`, `seedling`, `dead`, `moist`.

## tiles improvements

- **compact CSV** -- tiles toon output is now uniform schema CSV. Same area went from 33KB (YAML-like) to 14KB (CSV with 0/1 booleans).
- **z-range occupants** -- `DistrictCenter:z2-6` instead of repeating the name 5 times
- **occupants last** -- variable-length occupant string moved to last column for readability

## find_placement distance

New `distance` field on find_placement results. Uses the game's flow-field pathfinding (WeightedCoordinates) to report actual path distance from DC entrance. Lower = closer = more efficient hauling.

## breaking changes

- `map` command: `x:X y:Y radius:N` replaced with `x1:X y1:Y x2:X2 y2:Y2`
- all boolean fields now return `0`/`1` instead of `true`/`false`
- `tiles` toon output field order changed (occupants moved to last)
- `tiles` toon output always includes all fields (uniform schema)
