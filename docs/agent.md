# AI Agent Loop

Built-in agent loop that spawns claude/codex/custom binary per decision cycle.

## Status: In Progress

Working:
- `TimberbotAgent.cs` -- core loop, process spawning, state machine
- HTTP endpoints: `GET /api/agent/status`, `POST /api/agent/start`, `POST /api/agent/stop`
- Python CLI: `timberbot.py start`, `agent_status`, `agent_stop`
- Build compiles and auto-deploys
- `agent_status` returns correct idle/running/done/error state
- `start` command validates game is reachable before sending POST
- JSON escape for lastResponse/lastError fields (truncated to 2000 chars)
- Agent prompt file: `agent-prompt.md` (loaded from mod folder, fallback built-in)

Known issue -- process resolution on Windows:
- `timberbot.py` is on git bash PATH but NOT on Windows system PATH
- Fix applied: `RunProcess` detects `.py` files and invokes via `python.exe`
- Fix applied: `ResolveTimberbotCommand()` searches known paths for `timberbot.py`
- Candidates: mod folder (`Documents/Timberborn/Mods/Timberbot/timberbot.py`), repo (`C:\code\timberborn\timberbot\script\timberbot.py`)
- NOT YET TESTED after the python.exe fix (game timed out on relaunch)

## Architecture

Each decision cycle (runs on background thread):
1. Spawn `python.exe timberbot.py brain` to get game state (TOON format)
2. Spawn binary (`claude --print` / `codex` / custom) with system prompt + brain state on stdin
3. Read stdout -- lines of `timberbot.py` commands
4. Execute each command via `python.exe timberbot.py <cmd>`
5. Sleep `interval` seconds, increment turn counter
6. Stop after N turns or on `/api/agent/stop`

## Usage

```
timberbot.py start binary:claude turns:5
timberbot.py start binary:codex turns:3 model:codex-mini interval:15
timberbot.py start binary:C:/path/to/custom.exe turns:1
timberbot.py agent_status
timberbot.py agent_stop
```

## Files

| File | Change |
|------|--------|
| `timberbot/src/TimberbotAgent.cs` | NEW -- agent loop, process spawning, state machine |
| `timberbot/src/TimberbotService.cs` | added `Agent` field, instantiate in Load, stop in Unload |
| `timberbot/src/TimberbotHttpServer.cs` | added 3 endpoints: agent/start, agent/stop, agent/status |
| `timberbot/script/timberbot.py` | added `start`, `agent_status`, `agent_stop` commands |
| `timberbot/agent-prompt.md` | NEW -- default system prompt for AI agent |
| `~/.claude/skills/timberborn/SKILL.md` | updated to v5.0 with agent docs, fixed stale entries |

## Next Steps

1. Relaunch game and verify the python.exe + full-path fix works
2. Test a full 1-turn cycle end-to-end (brain -> claude -> execute)
3. Test multi-turn with interval
4. Test agent_stop mid-run
5. Test with codex binary
6. Copy timberbot.py to mod folder so it's always findable
