"""SessionStart hook for timberbot claude sessions.

Injects the runtime prompt plus live colony state as additionalContext.
Tries PATH first, then local timberbot.py launch fallbacks per OS.
"""
import json
import os
import subprocess
import sys


def run(cmd, timeout=5):
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, shell=True)
        return r.returncode == 0, r.stdout.strip()
    except Exception as e:
        return False, str(e)


def command_candidates(mod_dir):
    script = os.path.join(mod_dir, "timberbot.py")
    cmds = ["timberbot.py"]
    if os.name == "nt":
        cmds.extend([
            f'py -3 "{script}"',
            f'python "{script}"',
        ])
    else:
        cmds.extend([
            f'python3 "{script}"',
            f'python "{script}"',
        ])
    return cmds


def resolve_timberbot_cmd(mod_dir):
    for base in command_candidates(mod_dir):
        ok, _ = run(f"{base} ping")
        if ok:
            return base
    return None


parts = []
mod_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

skill_path = os.path.join(mod_dir, "skill", "timberbot.md")
if os.path.exists(skill_path):
    with open(skill_path, encoding="utf-8") as f:
        parts.append(f.read())

tb = resolve_timberbot_cmd(mod_dir)
if tb:
    ok, brain = run(f'{tb} brain', timeout=10)
    if ok and brain:
        parts.append("## CURRENT COLONY STATE\n\n" + brain)
    else:
        parts.append("## COLONY STATE: game reachable but brain failed. Run `timberbot.py brain` manually.")
else:
    parts.append("## COLONY STATE: game not reachable. Run `timberbot.py ping` to check.")

output = {
    "hookSpecificOutput": {
        "hookEventName": "SessionStart",
        "additionalContext": "\n\n".join(parts)
    }
}
print(json.dumps(output))
