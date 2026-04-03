"""SessionStart hook for timberbot claude sessions.

Injects rules + live colony state as additionalContext.
Rules are read from skill/rules.txt (single source of truth).
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

parts = []

# read rules from skill/rules.txt
rules_path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "skill", "rules.txt")
if os.path.exists(rules_path):
    with open(rules_path) as f:
        parts.append("## SESSION RULES\n\n" + f.read())
else:
    parts.append("## SESSION RULES: rules.txt not found at " + rules_path)

# try to get live colony state
ok, ping = run("timberbot.py ping")
if ok:
    ok2, brain = run("timberbot.py brain", timeout=10)
    if ok2 and brain:
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
