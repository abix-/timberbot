## Error messages

Every API error response is now rich and actionable. The AI gets enough context to correct the next call without guessing.

**Write endpoints (TimberbotWrite.cs)**
- [fix] `not_found` errors explain that ids are ephemeral and tell the caller to re-query buildings
- [fix] `invalid_type` errors include the building name and explain which building types support the operation
- [fix] `invalid_param` errors echo the bad value and list all valid options
- [fix] `no_population` includes requested vs available count
- [fix] `insufficient_science` includes cost and current points with a human-readable message
- [fix] district errors (`not_found`, `SetDistribution`) list all available district names
- [fix] recipe and plantable errors list all available options for that building

**Placement (TimberbotPlacement.cs)**
- [fix] placement validation errors name the blocking object (e.g. "occupied by Lumberjack") instead of generic "occupied"
- [fix] every validation error includes a suggestion (e.g. "demolish it or try a different location")
- [fix] `not_unlocked` tells the caller to use science/unlock first
- [internal] extracted `FindBlockerAt()` helper for DRY blocker lookup across buildings, natural resources, and tracked blockers

**HTTP server (TimberbotHttpServer.cs)**
- [fix] `invalid_body` explains the expected format
- [fix] `unknown_endpoint` now lists all GET and POST endpoints (was only 13, now 55+)

**Python client (timberbot.py)**
- [fix] unknown CLI parameters now show the bad param, valid params, and full usage line
- [fix] toon error output shows the full response dict instead of just the error string

## Agent

- [removed] shipped Claude Code hooks (`pretool-bash.py`, `session-start.py`) deleted. they blocked parallel tool calls and are no longer needed

## Panel defaults

- [fix] model and effort defaults extracted to named constants (`DefaultClaudeModel`, `DefaultCodexModel`, etc.) instead of scattered string literals
- [fix] switching binary (claude/codex) auto-selects the correct default model and effort

## Testing

- [new] 141 xUnit unit tests for `TimberbotJw` (serialization, commas, nesting, reuse) and `TimberbotPure` (orientation, name cleanup, assertions, normalization, quoting)
- [new] `timberbot/test/` project (net8.0, xUnit) shares source files with the main project, no Unity deps required
- [internal] extracted pure static helpers from 5 Unity-dependent files into `TimberbotPure.cs`

## Docs

- [docs] "Timberbot AI" pages renamed to "Timberbot Guide"
- [docs] getting-started page updated with setup instructions
- [docs] Steam Workshop description rewritten and trimmed to fit the character limit
- [docs] Workshop description mentions Python prerequisite
