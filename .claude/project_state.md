# Project State. Timberborn

> Git-tracked. NEVER put secrets, tokens, or credentials in this file.
> Updated by Claude at session end. Shared across all agent clones.

## Current Focus
v0.7.1 release. blocked on test failures in path routing

## Design Goals
- Timberbot API errors should be actionable: tell the caller what went wrong AND what to do about it
- Both toon and json error output should include the full structured response (not just error string)
- No Claude Code hooks shipped with the mod. they interfere with parallel tool calls

## Last Session (2026-04-03)
Attempted v0.7.1 release. Version already bumped in both files. Build succeeded (0 warnings, 0 errors). Tests: 464 passed, 5 failed, 48 skipped.

Failed tests (all path routing / map verification):
1. `verify demolish via map` . map state not refreshed after demolish
2. `diagonal: no errors` . path routing errors during diagonal A* placement
3. `diagonal2: no errors` . same issue, different diagonal case
4. `obstacle: detour taken` . obstacle avoidance returned paths=4 but expected >8 (straight=8)
5. `sections: paths placed` . placed 0 paths when some were expected

Release blocked at step 3 (tests). Did not commit, tag, or publish.

## Gameplay Session (2026-04-03) -- Colony: Potato Tomato (IronTeeth)
Goal: reach 50 beavers with 77 well-being. Day 331, drought arriving in ~6 days (5d duration).

Actions taken:
- Unpaused DeepWaterPump at (109,130,z5) -- was silently paused, losing water production
- Placed SmallWarehouse at (141,138,z2) set to obtain:Kohlrabi for drought food stockpile
- Placed SmallWarehouse at (140,152,z4) set to obtain:FermentedSoybean for processed food stockpile
- Note: command renamed from timberbot.py to tbot

State at session end:
- Water: 625 available / good for drought (tanks mostly full)
- Food: 404 stored + 233 ready in fields (borderline for drought, harvesting needed)
- Wellbeing: 17/77 -- critical. Nutrition 2.9/17 is the main drag (need FermentedSoybean output)
- FoodFactory idle (no Corn/Eggplant/Algae inputs, Corn takes 10d to grow)
- Fermenter running on Soybean -- key for nutrition improvement
- Game was STILL PAUSED at session end (speed:0) -- player needs to unpause

## Next Steps
- Diagnose path routing test failures (likely in A* pathfinding or path placement logic in the mod)
- Fix failures and re-run full test suite
- Resume release from step 3 onward (test, commit, push, release, notes, Steam Workshop reminder)
- Gameplay: unpause game, monitor food harvest and fermenter output before drought

## Open Questions
- Are path routing failures a regression from recent changes or a pre-existing issue with map/game state?
- Is the `verify demolish via map` failure related to the path routing issues or independent?
- Why is nutrition 2.9/17 when food is available? Possible: beavers not reaching food, or processed food stock is zero
