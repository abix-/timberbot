# Project State. Timberborn

> Git-tracked. NEVER put secrets, tokens, or credentials in this file.
> Updated by Claude at session end. Shared across all agent clones.

## Current Focus
Improving AI player experience. error messages and hook cleanup

## Design Goals
- Timberbot API errors should be actionable: tell the caller what went wrong AND what to do about it
- Both toon and json error output should include the full structured response (not just error string)
- No Claude Code hooks shipped with the mod. they interfere with parallel tool calls

## Last Session (2026-04-03)
1. Deleted `timberbot/hooks/` (pretool-bash.py, session-start.py). hooks were blocking parallel mutating commands and annoying AI players
2. Removed hook copy targets from Timberbot.csproj, added RemoveDir to clean deployed hooks
3. Made all ValidatePlacement error messages actionable with blocker names and suggestions:
   - "occupied at (x,y,z)" -> "occupied by <name> at (x,y,z). demolish it or try a different location"
   - terrain, blocked above/below, underground, out of map. all get actionable suffixes
4. Extracted FindBlockerAt() helper to DRY up blocker lookup (was duplicated in two code paths)
5. Fixed toon error output in timberbot.py to print full response dict (via toons.dumps) instead of bare error string

## Next Steps
- Review TimberbotWrite.cs error messages for actionable suffixes (placement done, write endpoints not yet)
- Consider if find_placement errors also need actionable messages
- Test error output with a live game to verify toon formatting looks right

## Open Questions
- Should the `-- suggestion` suffix style be formalized (e.g. always after `--`)?
- Are there other error paths that AI players hit frequently that need improvement?
