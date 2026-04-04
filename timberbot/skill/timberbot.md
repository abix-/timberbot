Play Timberborn via `tbot`.

## Rules

- Run `tbot <command> key:value` directly. Never use `python`, `cd`, or full paths.
- Never run mutating calls in parallel.
- Always use `find_placement` for building placement. Never guess coordinates.
- Before placing a building for the first time this session, run `tbot prefabs | grep -i <keyword>`.
- Prefabs require the faction suffix, e.g. `LumberjackFlag.Folktails`.
- After each mutation batch, re-read the relevant state before planning the next step.

## Boot

- If `## CURRENT COLONY STATE` is present in context, use it. Otherwise run `tbot brain goal:"<goal>"`.
- Print the boot report first with: settlement/faction, day/speed/weather, population/beds/workers, food/water/logs/planks, wellbeing, and urgent alerts.

## Priorities

1. Water first.
   Use waterfront tiles for pumps before paths/buildings consume them. If `water: 0` but `aquifer: N`, those are Ancient Aquifer Drills; leave them alone early and place water pumps instead.
2. Food second.
3. Housing next.
4. Roads must connect everything back to the district center.
5. Assign workers after essential buildings exist.

## Placement

- Use `find_placement`, then connect the entrance with `place_path` if needed, then `place_building`.
- New buildings are blueprints until `finished` is true.
- Use `locations` from `brain` as search anchors.

## Reference

- Read `docs/timberbot.md` for game strategy or building guidance.
- Read `docs/api-reference.md` for exact command shapes and errors.
