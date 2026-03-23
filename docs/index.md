# Timberbot API

**Full read/write HTTP API for controlling Timberborn with AI.**

Timberbot gives Claude, ChatGPT, or your own scripts complete access to your beaver colony over HTTP -- read game state, place buildings, manage workers, plant crops, and keep your beavers alive.

---

## Quick start

```bash
pip install requests toons
python timberbot.py ping                    # verify connection
python timberbot.py summary                 # colony snapshot
python timberbot.py visual x:120 y:140 radius:10  # ASCII map
```

!!! tip "New here?"
    Head to [Getting Started](getting-started.md) for installation and first steps.

---

## What you can do

| | Read | Write |
|---|---|---|
| **Buildings** | All buildings with workers, power, priority, inventory | Place, demolish, pause, configure |
| **Beavers** | Wellbeing, needs, workplace, contamination | Migrate between districts |
| **Resources** | Per-district stocks, distribution settings | Set import/export, stockpile config |
| **Map** | Terrain, water, occupants, contamination | Plant crops, mark trees, route paths |
| **Colony** | Weather, science, alerts, notifications | Speed, work hours, unlock buildings |

---

## Two ways to use it

=== "Python CLI"

    ```bash
    python timberbot.py summary
    python timberbot.py buildings
    python timberbot.py set_speed speed:3
    python timberbot.py place_building prefab:Path x:120 y:130 z:2 orientation:south
    ```

=== "Raw HTTP"

    ```bash
    curl http://localhost:8085/api/summary
    curl http://localhost:8085/api/buildings
    curl -X POST http://localhost:8085/api/speed -d '{"speed": 3}'
    ```

---

## Let AI play your colony

The mod includes an [AI prompt](timberbot.md) that teaches any LLM to autonomously manage food, water, housing, workers, and expansion.

```bash
# Claude Code setup
cp docs/timberbot.md ~/.claude/skills/timberbot/SKILL.md
/loop 1m /timberbot
```

---

## Links

- [API Reference](api-reference.md) -- every endpoint with request/response examples
- [Getting Started](getting-started.md) -- install, verify, first commands
- [AI Prompt](timberbot.md) -- autonomous colony management
- [Coverage](coverage.md) -- what's implemented vs gaps
- [Developing](developing.md) -- build from source
- [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=YOUR_WORKSHOP_ID)
- [GitHub](https://github.com/abix-/TimberbornMods)
