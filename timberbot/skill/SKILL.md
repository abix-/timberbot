---
name: timberbot
description: Collaborate with a human player on Timberborn via timberbot.py. Help keep beavers alive, wellbeing high, and needs met.
version: "0.8.1"
---
# Timberbot

Play Timberborn through `timberbot.py`.

ALWAYS prefer a local docs copy.
NEVER invent rules when the docs are unavailable.

1. Check `docs/timberbot.md` in the current working directory.
2. Otherwise check `%USERPROFILE%\Documents\Timberborn\Mods\Timberbot\docs\` (for example `C:\Users\Abix\Documents\Timberborn\Mods\Timberbot\docs\`).
3. If neither exists, stop. Tell the user to reopen Claude from the Timberbot repo root or the Steam Workshop mod folder root. Tell them the GitHub repo contains the same docs.

ALWAYS use `timberbot.py` directly.
NEVER assume the repo and Workshop layouts are identical.

- Local clone: `timberbot/script/timberbot.py`
- Workshop install: `timberbot.py` sits beside the DLL and docs

ALWAYS read `docs/timberbot.md` first.
NEVER treat any other doc as the AI authority.

ALWAYS use `docs/api-reference.md` only for exact commands, parameters, responses, helpers, and errors.
NEVER rely on memory for API contract details.

ALWAYS use `docs/getting-started.md` only for install, PATH, remote host, Workshop path, and troubleshooting.
NEVER pull setup rules from the AI guide.

ALWAYS follow the boot/link flow from `docs/timberbot.md` on the first invocation in a session.
NEVER skip boot on the first invocation.

ALWAYS skip boot later in the same session unless the user explicitly wants to restart or clear memory.
NEVER re-run boot just because the task changed.

ALWAYS run mutating game actions sequentially.
NEVER run mutating game API calls in parallel.

ALWAYS prefer `brain`, `find_placement`, and `find_planting`.
NEVER guess state, coordinates, faction prefabs, or irrigated tiles.

ALWAYS keep action batches bounded and re-read state after changes.
NEVER assume earlier observations are still current after a mutation.
