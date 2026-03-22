"""Timberbot interactive REPL."""
import json
import shlex
import threading

import requests

from timberborn.api import Timberbot

HELP_TEXT = """
Vanilla API (port 8080):
  status              show all levers + adapters
  on <name>           switch lever on
  off <name>          switch lever off
  watch [secs]        poll adapters every N seconds (default 5)
  stop                stop polling

Read (port 8085):
  summary             full colony snapshot
  resources           resource stocks per district
  population          beaver/bot counts per district
  time                game time info
  weather             weather/drought cycle info
  districts           all districts with resources + population
  buildings           list all buildings with IDs
  trees               list all cuttable trees
  prefabs             list available building templates

Write (port 8085):
  speed [0-3]                      get/set game speed (0=pause)
  pause <id>                       pause building
  unpause <id>                     unpause building
  priority <id> <p>                set priority (VeryLow/Normal/VeryHigh)
  workers <id> <n>                 set desired workers
  floodgate <id> <h>               set floodgate height
  place <prefab> <x> <y> <z> [o]   place building (orientation 0-3)
  demolish <id>                    demolish building
  cut <x1> <y1> <x2> <y2> <z>     mark cutting area
  uncut <x1> <y1> <x2> <y2> <z>   clear cutting area
  capacity <id> <n>                set stockpile capacity

General:
  ping                check connectivity
  help                show this message
  quit / exit         exit
"""


def pp(data):
    print(json.dumps(data, indent=2))


class Watcher:
    def __init__(self, bot, interval=5.0):
        self.bot = bot
        self.interval = interval
        self._stop = threading.Event()
        self._thread = None
        self._prev = {}

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
                for a in self.bot.adapters():
                    name = a.get("name", "?")
                    state = a.get("state")
                    prev = self._prev.get(name)
                    if prev is not None and prev != state:
                        print(f"\n[CHANGE] {name}: {'ON' if prev else 'OFF'} -> {'ON' if state else 'OFF'}")
                    self._prev[name] = state
            except requests.ConnectionError:
                pass
            except Exception as exc:
                print(f"\n[watch] error: {exc}")
            self._stop.wait(self.interval)


def main():
    bot = Timberbot()
    watcher = Watcher(bot)

    print("=== Timberbot ===")
    ok = bot.ping()
    print(f"  mod: {'connected' if ok else 'not detected'} (port 8085)")
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
                print(f"  mod: {'connected' if bot.ping() else 'not reachable'}")

            # -- read --
            elif cmd == "summary":
                pp(bot.summary())
            elif cmd == "resources":
                pp(bot.resources())
            elif cmd == "population":
                pp(bot.population())
            elif cmd == "time":
                pp(bot.time())
            elif cmd == "weather":
                pp(bot.weather())
            elif cmd == "districts":
                pp(bot.districts())
            elif cmd == "buildings":
                for b in bot.buildings():
                    flags = []
                    if b.get("paused"): flags.append("PAUSED")
                    if b.get("floodgate"): flags.append(f"h={b.get('height',0)}/{b.get('maxHeight',0)}")
                    if b.get("priority"): flags.append(f"pri={b['priority']}")
                    if "maxWorkers" in b: flags.append(f"w={b.get('assignedWorkers',0)}/{b['maxWorkers']}")
                    f = f"  [{', '.join(flags)}]" if flags else ""
                    c = f" ({b['x']},{b['y']},{b['z']})" if "x" in b else ""
                    print(f"  {b.get('id','?'):>10}  {b.get('name','?')}{c}{f}")
            elif cmd == "trees":
                trees = bot.trees()
                marked = [t for t in trees if t.get("marked")]
                print(f"  {len(trees)} trees, {len(marked)} marked")
                for t in marked:
                    c = f" ({t['x']},{t['y']},{t['z']})" if "x" in t else ""
                    print(f"    {t.get('id','?'):>10}  {t.get('name','?')}{c}")
            elif cmd == "prefabs":
                for p in bot.prefabs():
                    size = f" {p.get('sizeX',0)}x{p.get('sizeY',0)}x{p.get('sizeZ',0)}" if "sizeX" in p else ""
                    print(f"  {p.get('name','?')}{size}")
            elif cmd == "status":
                for lev in bot.levers():
                    state = "ON" if lev.get("state") else "OFF"
                    print(f"  {lev.get('name','?'):30s} {state}")
                for ad in bot.adapters():
                    state = "ON" if ad.get("state") else "OFF"
                    print(f"  {ad.get('name','?'):30s} {state}")

            # -- write --
            elif cmd == "speed":
                pp(bot.set_speed(int(args[0])) if args else bot.speed())
            elif cmd == "pause":
                pp(bot.pause_building(int(args[0])))
            elif cmd == "unpause":
                pp(bot.unpause_building(int(args[0])))
            elif cmd == "priority":
                pp(bot.set_priority(int(args[0]), args[1]))
            elif cmd == "workers":
                pp(bot.set_workers(int(args[0]), int(args[1])))
            elif cmd == "floodgate":
                pp(bot.set_floodgate(int(args[0]), float(args[1])))
            elif cmd == "place":
                o = int(args[4]) if len(args) > 4 else 0
                pp(bot.place_building(args[0], int(args[1]), int(args[2]), int(args[3]), o))
            elif cmd == "demolish":
                pp(bot.demolish_building(int(args[0])))
            elif cmd == "cut":
                pp(bot.mark_trees(int(args[0]), int(args[1]), int(args[2]), int(args[3]), int(args[4])))
            elif cmd == "uncut":
                pp(bot.clear_trees(int(args[0]), int(args[1]), int(args[2]), int(args[3]), int(args[4])))
            elif cmd == "capacity":
                pp(bot.set_capacity(int(args[0]), int(args[1])))

            # -- vanilla --
            elif cmd == "on":
                bot.lever_on(" ".join(args))
                print(f"  {' '.join(args)} -> ON")
            elif cmd == "off":
                bot.lever_off(" ".join(args))
                print(f"  {' '.join(args)} -> OFF")
            elif cmd == "watch":
                watcher.interval = float(args[0]) if args else 5.0
                watcher.start()
            elif cmd == "stop":
                watcher.stop()
            else:
                print(f"  unknown command: {cmd} (type 'help')")
        except IndexError:
            print(f"  missing arguments (type 'help')")
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
