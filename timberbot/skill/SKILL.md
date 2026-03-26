---
name: timberbot
description: Collaborate with a human player on Timberborn via timberbot.py. Help keep beavers alive, wellbeing high, and needs met.
version: "0.8.4"
---
# Timberbot

Play Timberborn through `timberbot.py`.

ALWAYS use local docs when available.
NEVER switch to GitHub docs without user approval.

1. Check `docs/timberbot.md` in the current working directory.
2. Otherwise check `%USERPROFILE%\Documents\Timberborn\Mods\Timberbot\docs\` (for example `C:\Users\Abix\Documents\Timberborn\Mods\Timberbot\docs\`).
3. If neither exists, ask the user if it is okay to use the GitHub docs at `https://github.com/abix-/TimberbornMods/tree/master/docs`.

ALWAYS use `timberbot.py` directly.
NEVER infer repo paths from Workshop paths or Workshop paths from repo paths.

- Local clone: `timberbot/script/timberbot.py`
- Workshop install: `timberbot.py` sits beside the DLL and docs

ALWAYS read `docs/timberbot.md` first.
NEVER read another doc before the AI guide.

ALWAYS use `docs/api-reference.md` for exact commands, parameters, responses, helpers, and errors.
NEVER improvise API contract details.

ALWAYS use `docs/getting-started.md` for install, PATH, remote host, Workshop path, and troubleshooting.
NEVER treat setup docs as gameplay docs.

ALWAYS run the boot/link flow once at session start, and only run it again if the user explicitly wants to restart or clear memory.
NEVER act before boot completes or repeat boot just because the task changed.

ALWAYS run mutating game actions sequentially.
NEVER overlap mutating game API calls.

ALWAYS prefer `brain`, `find_placement`, and `find_planting`.
NEVER guess state, coordinates, faction prefabs, or irrigated tiles.

ALWAYS re-read state after each mutation batch.
NEVER trust pre-mutation observations after state changes.

