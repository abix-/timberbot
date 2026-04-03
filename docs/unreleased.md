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
- [feature] Timberbot now uses the shipped `skill/timberbot.md` as the static instruction file and sends live colony state as the startup prompt
- [feature] Codex launch now uses the correct Codex CLI flags instead of Claude-style prompt-file arguments
- [feature] custom CLI launch templates support placeholders like `{skill}`, `{prompt}`, `{prompt_file}`, `{model}`, and `{effort}`
- [feature] terminal launch supports templates with `{cwd}` and `{command}`
- [fix] Windows brain gathering now runs through Python instead of trying to execute `timberbot.py` directly
- [fix] webhook buffering now has a per-webhook cap via `webhookMaxPendingEvents`, dropping the oldest queued payloads when full

## macOS

- [feature] macOS path handling for the mod folder, settings, and helper scripts
- [feature] macOS Python 3 auto-detection for `timberbot.py brain`
- [feature] when `terminal` is blank on macOS, Timberbot opens the built-in agent in Terminal.app by default
- [feature] macOS startup settings now include `pythonCommand` to override the detected Python launcher
- [feature] `timberbot.py launch` on macOS prepares `autoload.json` and then expects the player to open Timberborn manually
- [docs] added a dedicated macOS testing checklist for Steam Workshop testers

## Gameplay

- [feature] aquifer drills have their own building category so they no longer clutter the water tab
- [feature] alerts now reflect actual building status instead of hardcoded conditions
- [feature] misspell a prefab name and the API suggests the closest match
- [fix] water-edge buildings (pumps, water dumps) now place at the correct height
- [fix] game speed display works again (was always showing 0)
- [fix] Windows Steam launch is more reliable -- uses direct applaunch instead of `steam://` protocol
