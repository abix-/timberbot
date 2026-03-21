"""Interactive REPL for co-piloting Timberborn."""
import json
import shlex
import sys
import threading
import time

import requests

from timberborn.api import TimberbornAPI

HELP_TEXT = """
Vanilla API (port 8080):
  status              show all levers + adapters
  on <name>           switch lever on
  off <name>          switch lever off
  color <name> <hex>  set lever color
  watch [secs]        poll adapters every N seconds (default 5)
  stop                stop polling

GameStateBridge mod (port 8085):
  summary             full colony snapshot
  resources           resource stocks per district
  population          beaver/bot counts per district
  time                game time info
  weather             weather/drought cycle info
  districts           all districts with resources + population

General:
  ping                check connectivity (game + bridge)
  help                show this message
  quit / exit         exit
"""


def pp(data):
    """Pretty-print JSON data."""
    print(json.dumps(data, indent=2))


class Watcher:
    """Background thread that polls adapters and prints changes."""

    def __init__(self, api, interval=5.0):
        self.api = api
        self.interval = interval
        self._stop = threading.Event()
        self._thread = None
        self._prev_states = {}

    def start(self):
        if self._thread and self._thread.is_alive():
            print("[watch] already running")
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()
        print(f"[watch] polling every {self.interval}s -- type 'stop' to end")

    def stop(self):
        self._stop.set()
        if self._thread:
            self._thread.join(timeout=2)
        print("[watch] stopped")

    def _loop(self):
        while not self._stop.is_set():
            try:
                adapters = self.api.get_adapters()
                for a in adapters:
                    name = a.get("name", "?")
                    state = a.get("state")
                    prev = self._prev_states.get(name)
                    if prev is not None and prev != state:
                        label = "ON" if state else "OFF"
                        prev_label = "ON" if prev else "OFF"
                        print(f"\n[CHANGE] {name}: {prev_label} -> {label}")
                    self._prev_states[name] = state
            except requests.ConnectionError:
                pass
            except Exception as exc:
                print(f"\n[watch] error: {exc}")
            self._stop.wait(self.interval)


def print_status(api):
    """Print all levers and adapters."""
    try:
        levers = api.get_levers()
    except requests.ConnectionError:
        print("  (game not reachable)")
        return
    except Exception as exc:
        print(f"  error: {exc}")
        return

    print(f"\n  Levers ({len(levers)}):")
    if not levers:
        print("    (none)")
    for lev in levers:
        state = "ON" if lev.get("state") else "OFF"
        spring = " [spring]" if lev.get("springReturn") else ""
        print(f"    {lev.get('name', '?'):30s} {state}{spring}")

    try:
        adapters = api.get_adapters()
    except Exception as exc:
        print(f"\n  Adapters: error: {exc}")
        return

    print(f"\n  Adapters ({len(adapters)}):")
    if not adapters:
        print("    (none)")
    for ad in adapters:
        state = "ON" if ad.get("state") else "OFF"
        print(f"    {ad.get('name', '?'):30s} {state}")
    print()


def print_summary(api):
    """Print full colony snapshot from GameStateBridge."""
    try:
        data = api.get_summary()
    except requests.ConnectionError:
        print("  bridge not reachable (is GameStateBridge mod installed?)")
        return

    if "error" in data:
        print(f"  {data['error']}")
        return

    t = data.get("time", {})
    w = data.get("weather", {})
    print(f"\n  Day {t.get('dayNumber', '?')} ({t.get('dayProgress', 0):.0%})")
    print(f"  Cycle {w.get('cycle', '?')}, day {w.get('cycleDay', '?')}")

    districts = data.get("districts", [])
    for d in districts:
        name = d.get("name", "?")
        pop = d.get("population", {})
        adults = pop.get("adults", 0)
        children = pop.get("children", 0)
        print(f"\n  [{name}] population: {adults} adults, {children} children")

        resources = d.get("resources", {})
        if resources:
            # Show non-zero resources
            nonzero = {k: v for k, v in resources.items() if v and v != 0}
            if nonzero:
                for good, amount in sorted(nonzero.items()):
                    print(f"    {good:25s} {amount}")
            else:
                print("    (no resources)")
    print()


def main():
    api = TimberbornAPI()
    watcher = Watcher(api)

    print("=== Timberborn Co-Pilot ===")

    game_ok = api.ping()
    bridge_ok = api.ping_bridge()

    if game_ok:
        print("  game API:   connected (port 8080)")
    else:
        print("  game API:   not detected (port 8080)")

    if bridge_ok:
        print("  bridge mod: connected (port 8085)")
    else:
        print("  bridge mod: not detected (port 8085)")

    print("\nType 'help' for commands.\n")

    while True:
        try:
            line = input("tb> ").strip()
        except (EOFError, KeyboardInterrupt):
            break

        if not line:
            continue

        try:
            parts = shlex.split(line)
        except ValueError:
            parts = line.split()

        cmd = parts[0].lower()
        args = parts[1:]

        try:
            if cmd in ("quit", "exit", "q"):
                break
            elif cmd == "help":
                print(HELP_TEXT)
            elif cmd == "ping":
                game_ok = api.ping()
                bridge_ok = api.ping_bridge()
                print(f"  game:   {'connected' if game_ok else 'not reachable'}")
                print(f"  bridge: {'connected' if bridge_ok else 'not reachable'}")
            elif cmd == "status":
                print_status(api)
            elif cmd == "summary":
                print_summary(api)
            elif cmd == "resources":
                pp(api.get_resources())
            elif cmd == "population":
                pp(api.get_population())
            elif cmd == "time":
                pp(api.get_time())
            elif cmd == "weather":
                pp(api.get_weather())
            elif cmd == "districts":
                pp(api.get_districts())
            elif cmd == "on":
                if not args:
                    print("  usage: on <lever-name>")
                else:
                    name = " ".join(args)
                    api.switch_on(name)
                    print(f"  {name} -> ON")
            elif cmd == "off":
                if not args:
                    print("  usage: off <lever-name>")
                else:
                    name = " ".join(args)
                    api.switch_off(name)
                    print(f"  {name} -> OFF")
            elif cmd == "color":
                if len(args) < 2:
                    print("  usage: color <lever-name> <hex>")
                else:
                    hex_val = args[-1]
                    name = " ".join(args[:-1])
                    api.set_color(name, hex_val)
                    print(f"  {name} -> color {hex_val}")
            elif cmd == "watch":
                interval = float(args[0]) if args else 5.0
                watcher.interval = interval
                watcher.start()
            elif cmd == "stop":
                watcher.stop()
            else:
                print(f"  unknown command: {cmd} (type 'help')")
        except requests.ConnectionError:
            print("  not reachable -- is Timberborn running?")
        except requests.HTTPError as exc:
            print(f"  API error: {exc.response.status_code} {exc.response.text}")
        except Exception as exc:
            print(f"  error: {exc}")

    watcher.stop()
    print("bye!")


if __name__ == "__main__":
    main()
