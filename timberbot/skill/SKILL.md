---
name: timberbot
description: Collaborate with a human player on Timberborn via timberbot.py. Help keep beavers alive, wellbeing high, and needs met.
version: "0.7.0"
---
# Timberbot

This is the distributable Claude Code entrypoint for playing Timberborn through `timberbot.py`.

This skill is intentionally thin. The authoritative knowledge lives in the mod docs that ship with the repo or mod folder.

Before acting:

1. Confirm the current working directory contains `docs/timberbot.md`.
2. If that file is missing, stop and tell the user to reopen Claude from the Timberbot repo root or the distributed mod folder root.
3. Use `timberbot.py` directly. In a local clone it lives at `timberbot/script/timberbot.py`; in the distributed mod folder it is shipped alongside the DLL and docs.
4. Read `docs/timberbot.md` first. It is the core operating guide and defines the boot flow and hard rules.
5. Read `docs/api-reference.md` only when you need exact endpoint, parameter, response, pagination, or error details.
6. Read `docs/timberbot-reference.md` only when you need faction-specific building names, gameplay lookup tables, wellbeing details, scaling ratios, or other broad reference material.
7. Read `docs/getting-started.md` only for install, PATH, remote host, or troubleshooting questions.

Runtime rules:

- Use `timberbot.py` directly.
- On first invocation in a session, follow the boot/link flow from `docs/timberbot.md`.
- On later invocations in the same session, skip boot unless the user explicitly wants to restart or clear memory.
- Never run mutating game API calls in parallel.
- Prefer `brain`, `find_placement`, and `find_planting` over ad hoc guessing.
- Keep action batches bounded and re-read state after making changes.

