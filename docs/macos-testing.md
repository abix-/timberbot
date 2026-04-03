# macOS Testing

Use this checklist when validating Timberbot on macOS from the Steam Workshop build.

## Goal

Confirm that a Mac player can:

- install Timberbot from Steam Workshop
- load a save
- open the in-game widget
- start Claude or Codex from the widget
- use the Python helper commands if needed

This is a runtime validation checklist, not a source-build guide.

## Prerequisites

The tester should already have:

- Timberborn running on macOS
- Timberbot API subscribed in Steam Workshop
- `claude` or `codex` installed if they want to test agent launch
- Python 3 installed if the default auto-detect does not work on their machine

## Primary test flow

1. Subscribe to Timberbot API on Steam Workshop.
2. Launch Timberborn on macOS.
3. Enable the mod in the Mod Manager if needed.
4. Load any save.
5. Confirm the green `Timberbot API` widget appears in the bottom-right corner.
6. Click `Settings`.
7. Confirm both tabs exist:
   - `Agent`
   - `Startup`
8. In `Agent`, set:
   - `Binary` to `claude` or `codex`
   - a valid `Model`
   - a valid `Effort`
   - `Goal` to something simple like `print the boot report`
9. In `Startup`, leave these blank for the first test:
   - `terminal`
   - `pythonCommand`
10. Click `Start`.

## Expected result

- Terminal.app opens automatically
- Claude or Codex starts interactively
- the agent prints the boot report first
- the widget status changes to `Running`

## If Start fails

Retry with `Startup -> pythonCommand` set to one of:

- `python3`
- `/opt/homebrew/bin/python3`
- `/usr/local/bin/python3`
- `/usr/bin/python3`

If Terminal launching still fails, report it and include the log file listed below.

## Python helper checks

The Workshop install should place Timberbot under:

```bash
~/Documents/Timberborn/Mods/Timberbot/
```

Run:

```bash
~/Documents/Timberborn/Mods/Timberbot/timberbot.py summary
```

Expected:

- returns colony data while a save is loaded

Run:

```bash
~/Documents/Timberborn/Mods/Timberbot/timberbot.py brain
```

Expected:

- returns live colony state

## Save autoload helper check

Run:

```bash
~/Documents/Timberborn/Mods/Timberbot/timberbot.py launch settlement:<name>
```

Expected on macOS v1:

- Timberbot writes `autoload.json`
- Timberbot does not try to auto-start the game
- Timberbot tells the user to open Timberborn manually

Then:

1. Open Timberborn manually.
2. Reach the main menu.
3. Confirm the selected save auto-loads.

## Stop behavior

1. Start an agent session.
2. Click `Stop` in the widget.
3. Confirm the Timberbot-launched Claude/Codex session closes.
4. Confirm unrelated terminal sessions stay open.

## What to send back if anything fails

Please send:

- macOS version
- whether you tested `claude` or `codex`
- whether `pythonCommand` was blank or manually set
- whether `terminal` was blank or manually set
- exact error text from Terminal.app
- this log file:

```bash
~/Documents/Timberborn/Mods/Timberbot/timberbot.log
```
