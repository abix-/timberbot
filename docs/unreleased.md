## Timberbot Panel

The mod now has a full in-game control surface for the built-in agent.

**Corner widget**
- draggable bottom-right `Timberbot API` widget
- `Start`, `Stop`, and `Settings` buttons always available
- minimize support so the widget can collapse to a smaller status bar
- widget position persists in `settings.json`

**Settings modal**
- centered `Timberbot API - Settings` modal
- `Agent` tab for day-to-day launch settings
- `Startup` tab for advanced/load-time settings
- all settings persist to `settings.json`
- tooltips on every settings row

**Agent tab**
- `Binary`, `Model`, `Effort`, and `Goal`
- text fields with preset pickers instead of hard-locked dropdowns
- Claude and Codex model/effort presets
- `custom` binary support with a freeform command template
- `Start` button in the tab that launches and closes the modal

**Startup tab**
- `debugEndpointEnabled`
- `httpPort`
- `webhooksEnabled`
- `webhookBatchMs`
- `webhookCircuitBreaker`
- `webhookMaxPendingEvents`
- `writeBudgetMs`
- `terminal`
- `pythonCommand`

The `Startup` tab warns that Timberborn must be restarted or the save reloaded after changing those settings.

## Agent

- [feature] the built-in agent is an interactive Claude/Codex/custom-binary session, not an autonomous loop
- [feature] Timberbot now generates a merged per-launch `agent-instructions.md` from `skill/timberbot.md` plus live colony state and launches Claude/Codex against that file
- [feature] Codex launch now uses the correct Codex CLI flags instead of Claude-style prompt-file arguments
- [feature] custom CLI launch templates support placeholders like `{skill}`, `{instructions_file}`, `{prompt}`, `{prompt_file}`, `{model}`, and `{effort}`
- [feature] terminal launch supports templates with `{cwd}` and `{command}`
- [feature] `agent_status` and `agent_stop` CLI commands
- [feature] `top` dashboard shows recent agent turns
- [feature] shipped `skill/timberbot.md` runtime prompt and Claude Code hooks (`pretool-bash.py`, `session-start.py`)
- [fix] Windows brain gathering now runs through Python instead of trying to execute `timberbot.py` directly
- [fix] Windows terminal-wrapped Claude/Codex launches now use a PowerShell wrapper so multiline startup prompts are not mangled
- [fix] terminal presets now save the real command template instead of the display label (for example `wezterm start --cwd {cwd} --` instead of `WezTerm`)
- [fix] the shipped runtime launch prompt in `skill/timberbot.md` is much smaller while keeping the critical operating rules
- [fix] webhook buffering now has a per-webhook cap via `webhookMaxPendingEvents`, dropping the oldest queued payloads when full

## macOS

- [feature] macOS path handling for the mod folder, settings, and helper scripts
- [feature] macOS Python 3 auto-detection for `timberbot.py brain`
- [feature] when `terminal` is blank on macOS, Timberbot opens the built-in agent in Terminal.app by default
- [feature] macOS startup settings now include `pythonCommand` to override the detected Python launcher
- [feature] `timberbot.py launch` on macOS prepares `autoload.json` and then expects the player to open Timberborn manually
- [docs] added a dedicated macOS testing checklist for Steam Workshop testers

## Python client (timberbot.py)

- [feature] `brain` replaced maps with locations (`set_location`, `remove_location`, `list_locations`)
- [feature] brain output compacted to toon format (120 lines to 46)
- [breaking] `map` command no longer accepts `name` parameter (no longer saves to memory)
- [internal] shared path helpers (`_mod_dir`, `_settings_path`, `_saves_dir`)

## Gameplay

- [feature] aquifer drills have their own building category so they no longer clutter the water tab
- [feature] alerts now reflect actual building StatusAlerts instead of hardcoded conditions
- [feature] misspell a prefab name and the API suggests the closest match
- [fix] water-edge buildings (pumps, water dumps) now place at the correct height (CoordinatesAtBaseZ)
- [fix] placement validation matches player behavior: service validation + block checks, skip MatterBelow
- [fix] BaseZ offset applied correctly for buildings with non-zero base (e.g. DeepWaterPump)
- [fix] enriched placement errors with terrain conflicts, occupancy, coordinates instead of generic "placement invalid"
- [fix] game speed display works again (was always showing 0)
- [fix] Windows Steam launch is more reliable -- uses direct applaunch instead of `steam://` protocol
- [internal] shared `TimberbotPaths` helper for mod directory, settings path
