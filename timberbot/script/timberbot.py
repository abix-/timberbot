"""Timberbot -- control Timberborn over HTTP.

Usage:
    python timberbot.py                     list all methods
    python timberbot.py summary             full colony snapshot
    python timberbot.py buildings           list all buildings
    python timberbot.py set_speed 3         fast forward
    python timberbot.py watch               live dashboard
    python timberbot.py place_building LumberjackFlag.IronTeeth 120 130 2
    python timberbot.py demolish_building -- -12345

As a library:
    from timberbot import Timberbot
    bot = Timberbot()
    bot.summary()
"""
import json
import sys
import time
import urllib.parse

import requests


# ---------------------------------------------------------------------------
# API client
# ---------------------------------------------------------------------------

class Timberbot:
    """Client for Timberbot mod (port 8085) + vanilla API (port 8080)."""

    def __init__(self, host="localhost", port=8085, game_port=8080):
        self.url = f"http://{host}:{port}"
        self.game_url = f"http://{host}:{game_port}"
        self.s = requests.Session()
        self.s.headers["Accept"] = "application/json"

    def _get(self, path):
        r = self.s.get(f"{self.url}{path}", timeout=5)
        r.raise_for_status()
        return r.json()

    def _post(self, path, data):
        r = self.s.post(f"{self.url}{path}", json=data, timeout=5)
        return r.json()

    def _game_get(self, path):
        r = self.s.get(f"{self.game_url}{path}", timeout=5)
        r.raise_for_status()
        return r.json()

    def _game_post(self, path):
        r = self.s.post(f"{self.game_url}{path}", timeout=5)
        r.raise_for_status()
        return r.json()

    # -- connection --

    def ping(self):
        """True if Timberbot mod is reachable."""
        try:
            return self._get("/api/ping").get("ready", False)
        except (requests.ConnectionError, requests.Timeout):
            return False

    # -- read state (nouns) --

    def summary(self):
        """Full snapshot: time + weather + districts with resources and population."""
        return self._get("/api/summary")

    def time(self):
        """Game time: {dayNumber, dayProgress, partialDayNumber}."""
        return self._get("/api/time")

    def weather(self):
        """Weather: {cycle, cycleDay, isHazardous, temperateWeatherDuration, hazardousWeatherDuration}."""
        return self._get("/api/weather")

    def population(self):
        """Beaver counts: [{district, adults, children, bots}]."""
        return self._get("/api/population")

    def resources(self):
        """Resource stocks: {districtName: {goodName: {available, all}}}."""
        return self._get("/api/resources")

    def districts(self):
        """Districts: [{name, population: {adults, children, bots}, resources: {...}}]."""
        return self._get("/api/districts")

    def buildings(self):
        """All buildings: [{id, name, x, y, z, finished, paused, priority, maxWorkers, desiredWorkers, assignedWorkers}]."""
        return self._get("/api/buildings")

    def trees(self):
        """All cuttable trees: [{id, name, x, y, z, marked, alive}]."""
        return self._get("/api/trees")

    def gatherables(self):
        """All gatherable resources (berry bushes etc): [{id, name, x, y, z, alive}]."""
        return self._get("/api/gatherables")

    def prefabs(self):
        """Available building templates: [{name, sizeX, sizeY, sizeZ}]."""
        return self._get("/api/prefabs")

    def speed(self):
        """Current game speed: {speed: 0-3}."""
        return self._get("/api/speed")

    def map(self, x1=0, y1=0, x2=0, y2=0):
        """Terrain + water for a region. No args = map size only."""
        if x1 == 0 and y1 == 0 and x2 == 0 and y2 == 0:
            return self._get("/api/map")
        return self._post("/api/map", {"x1": x1, "y1": y1, "x2": x2, "y2": y2})

    # -- write actions (verb_noun) --

    def set_speed(self, speed):
        """Set game speed. 0=pause, 1=normal, 2=fast, 3=fastest."""
        return self._post("/api/speed", {"speed": speed})

    def pause_building(self, building_id):
        """Pause a building."""
        return self._post("/api/building/pause", {"id": building_id, "paused": True})

    def unpause_building(self, building_id):
        """Unpause a building."""
        return self._post("/api/building/pause", {"id": building_id, "paused": False})

    def set_priority(self, building_id, priority):
        """Set building priority. Values: VeryLow, Normal, VeryHigh."""
        return self._post("/api/priority", {"id": building_id, "priority": priority})

    def set_workers(self, building_id, count):
        """Set desired worker count (0 to maxWorkers)."""
        return self._post("/api/workers", {"id": building_id, "count": count})

    def set_floodgate(self, building_id, height):
        """Set floodgate height (clamped to min/max)."""
        return self._post("/api/floodgate", {"id": building_id, "height": height})

    def place_building(self, prefab, x, y, z, orientation=0):
        """Place a building. Get prefab names from prefabs(). orientation: 0-3."""
        return self._post("/api/building/place", {
            "prefab": prefab, "x": x, "y": y, "z": z, "orientation": orientation
        })

    def demolish_building(self, building_id):
        """Demolish a building. Get IDs from buildings()."""
        return self._post("/api/building/demolish", {"id": building_id})

    def mark_trees(self, x1, y1, x2, y2, z):
        """Mark a rectangular area for tree cutting."""
        return self._post("/api/cutting/area", {
            "x1": x1, "y1": y1, "x2": x2, "y2": y2, "z": z, "marked": True
        })

    def plant_crop(self, x1, y1, x2, y2, z, crop):
        """Mark area for planting. Crops: Kohlrabi, Cassava, Carrot, Potato, Wheat, etc."""
        return self._post("/api/planting/mark", {
            "x1": x1, "y1": y1, "x2": x2, "y2": y2, "z": z, "crop": crop
        })

    def clear_planting(self, x1, y1, x2, y2, z):
        """Clear planting marks from a rectangular area."""
        return self._post("/api/planting/clear", {
            "x1": x1, "y1": y1, "x2": x2, "y2": y2, "z": z
        })

    def clear_trees(self, x1, y1, x2, y2, z):
        """Clear tree cutting marks from a rectangular area."""
        return self._post("/api/cutting/area", {
            "x1": x1, "y1": y1, "x2": x2, "y2": y2, "z": z, "marked": False
        })

    def set_capacity(self, building_id, capacity):
        """Set stockpile capacity."""
        return self._post("/api/stockpile/capacity", {"id": building_id, "capacity": capacity})

    def set_good(self, building_id, good):
        """Set allowed good on a single-good stockpile."""
        return self._post("/api/stockpile/good", {"id": building_id, "good": good})

    def place_path(self, x1, y1, x2, y2, z):
        """Place a straight line of paths from (x1,y1) to (x2,y2) at height z. Returns list of results."""
        results = []
        if x1 == x2:
            step = 1 if y2 >= y1 else -1
            for y in range(y1, y2 + step, step):
                results.append(self.place_building("Path", x1, y, z))
        elif y1 == y2:
            step = 1 if x2 >= x1 else -1
            for x in range(x1, x2 + step, step):
                results.append(self.place_building("Path", x, y1, z))
        else:
            return {"error": "path must be a straight line (x1==x2 or y1==y2)"}
        placed = sum(1 for r in results if "id" in r)
        failed = sum(1 for r in results if "error" in r)
        return {"placed": placed, "failed": failed, "total": len(results)}

    # -- vanilla API (levers/adapters) --

    def levers(self):
        """List all levers (vanilla API port 8080)."""
        return self._game_get("/api/levers")

    def adapters(self):
        """List all adapters (vanilla API port 8080)."""
        return self._game_get("/api/adaptors")

    def lever_on(self, name):
        """Turn a lever ON."""
        return self._game_post(f"/api/switch-on/{urllib.parse.quote(name, safe='')}")

    def lever_off(self, name):
        """Turn a lever OFF."""
        return self._game_post(f"/api/switch-off/{urllib.parse.quote(name, safe='')}")

    # -- helpers --

    @staticmethod
    def near(items, x, y, radius=20):
        """Filter items to those within radius of (x,y). Sorted by distance."""
        result = []
        for i in items:
            if "x" not in i:
                continue
            d = abs(i["x"] - x) + abs(i["y"] - y)
            if d <= radius:
                result.append(i)
        result.sort(key=lambda i: abs(i["x"] - x) + abs(i["y"] - y))
        return result

    @staticmethod
    def named(items, name):
        """Filter items whose name contains the given string (case-insensitive)."""
        low = name.lower()
        return [i for i in items if low in i.get("name", "").lower()]

    def scan(self, x, y, radius=10):
        """Scan an area and return a grid showing terrain, water, and occupants."""
        ICONS = {
            "Path": "=", "Pine": "T", "Birch": "T", "Oak": "T", "Maple": "T",
            "Bush": "b", "berry": "b", "Lumberjack": "L", "Gatherer": "G",
            "DistrictCenter": "D", "Rowhouse": "H", "Barrack": "H", "Lodge": "H",
            "Tank": "W", "Pump": "P", "PowerWheel": "E", "PowerShaft": "e",
            "LumberMill": "M", "WoodWorkshop": "M", "IndustrialLumberMill": "M",
            "FarmHouse": "F", "Hauling": "K", "Breeding": "R", "Inventor": "S",
            "Forester": "f", "Warehouse": "$", "Pile": "$", "Campfire": "C",
            "Floodgate": "X", "Dam": "X", "Levee": "X",
        }
        data = self.map(x - radius, y - radius, x + radius, y + radius)
        tiles = {(t["x"], t["y"]): t for t in data.get("tiles", [])}
        lines = []
        legend_items = set()
        for ty in range(y + radius, y - radius - 1, -1):
            row = f"{ty:3d} "
            for tx in range(x - radius, x + radius + 1):
                t = tiles.get((tx, ty))
                if not t:
                    row += "?"
                elif t.get("entrance") and not t.get("occupant"):
                    row += "@"
                    legend_items.add("@ Entrance")
                elif t.get("occupant"):
                    oname = t["occupant"].replace("(Clone)", "").replace(".IronTeeth", "")
                    icon = None
                    for key, sym in ICONS.items():
                        if key.lower() in oname.lower():
                            icon = sym
                            legend_items.add(f"{sym} {key}")
                            break
                    row += icon if icon else oname[0]
                elif t["water"] > 0:
                    row += "~"
                    legend_items.add("~ Water")
                elif t["terrain"] > 0:
                    row += "."
                    legend_items.add(". Empty")
                else:
                    row += " "
            lines.append(row)
        lines.append("    " + "".join(str(i % 10) for i in range(x - radius, x + radius + 1)))
        lines.append("  " + "  ".join(sorted(legend_items)))
        return "\n".join(lines)

    def find(self, source, name=None, x=None, y=None, radius=20):
        """Find entities from a source (buildings/trees/gatherables). Filter by name and/or proximity."""
        items = getattr(self, source)()
        if name:
            items = self.named(items, name)
        if x is not None and y is not None:
            items = self.near(items, x, y, radius)
        return items


# ---------------------------------------------------------------------------
# Live dashboard (watch subcommand)
# ---------------------------------------------------------------------------

_RST = "\033[0m"
_BOLD = "\033[1m"
_DIM = "\033[2m"
_RED = "\033[31m"
_WHT = "\033[37m"
_BRED = "\033[91m"
_BGRN = "\033[92m"
_BYEL = "\033[93m"
_BBLU = "\033[94m"
_BMAG = "\033[95m"
_BCYN = "\033[96m"


def _bar(pct, width=20):
    filled = int(pct * width)
    return f"{_BGRN}{'#' * filled}{_DIM}{'.' * (width - filled)}{_RST}"


def _render(data):
    if not data:
        print(f"  {_RED}-- game not reachable --{_RST}")
        return

    t = data.get("time", {})
    w = data.get("weather", {})

    day = t.get("dayNumber", 0)
    progress = t.get("dayProgress", 0)
    cycle = w.get("cycle", 0)
    cday = w.get("cycleDay", 0)
    hazardous = w.get("isHazardous", False)
    temperate_len = w.get("temperateWeatherDuration", 0)
    hazard_len = w.get("hazardousWeatherDuration", 0)
    days_left = temperate_len - cday + 1 if not hazardous else 0

    season_color = _BRED if hazardous else _BGRN
    season_label = "DROUGHT" if hazardous else "temperate"
    print(f"  {_BOLD}{_BCYN}day {day}{_RST} {_bar(progress)} {_DIM}{progress:.0%}{_RST}")
    print(f"  {season_color}{season_label}{_RST} {_DIM}cycle {cycle} day {cday}/{temperate_len}+{hazard_len}{_RST}", end="")
    if not hazardous:
        if days_left <= 3:
            print(f"  {_BRED}{_BOLD}{days_left}d to drought!{_RST}")
        else:
            print(f"  {_DIM}{days_left}d to drought{_RST}")
    else:
        remaining = temperate_len + hazard_len - cday + 1
        print(f"  {_BRED}{remaining}d remaining{_RST}")
    print()

    for d in data.get("districts", []):
        dname = d.get("name", "?")
        pop = d.get("population", {})
        adults = pop.get("adults", 0)
        children = pop.get("children", 0)
        bots = pop.get("bots", 0)
        resources = d.get("resources", {})

        total = adults + children + bots
        print(f"  {_BOLD}{_BYEL}{dname}{_RST}  {_BCYN}{total}{_RST} pop {_DIM}({adults}a {children}c{f' {bots}b' if bots else ''}){_RST}")

        if resources:
            items = sorted(resources.items(), key=lambda x: -(x[1]["all"] if isinstance(x[1], dict) else x[1]))
            for good, val in items:
                if isinstance(val, dict):
                    avail = val.get("available", 0)
                    total_stock = val.get("all", 0)
                else:
                    avail = total_stock = val

                if "water" in good.lower():
                    color = _BBLU
                elif "berr" in good.lower() or "bread" in good.lower() or "carrot" in good.lower():
                    color = _BGRN
                elif "log" in good.lower() or "plank" in good.lower() or "wood" in good.lower():
                    color = _BYEL
                elif "metal" in good.lower() or "gear" in good.lower() or "scrap" in good.lower():
                    color = _BMAG
                else:
                    color = _WHT

                carried = total_stock - avail
                carried_str = f" {_DIM}(+{carried} in transit){_RST}" if carried > 0 else ""
                print(f"    {color}{good:22s}{_RST} {_BOLD}{avail:>5}{_RST}{carried_str}")
        else:
            print(f"    {_DIM}(no resources){_RST}")
        print()


def _watch():
    bot = Timberbot()
    print(f"\n  {_BOLD}{_BMAG}=== Timberborn Live ==={_RST}\n")

    if not bot.ping():
        print(f"  {_RED}cannot reach Timberbot on port 8085{_RST}")
        print(f"  {_DIM}start Timberborn with the mod loaded{_RST}\n")
        sys.exit(1)

    print(f"  {_BGRN}connected{_RST}  {_DIM}polling every 3s -- ctrl+c to stop{_RST}\n")

    try:
        while True:
            try:
                data = bot.summary()
            except Exception:
                data = None
            print("\033[2J\033[H", end="")
            print(f"\n  {_BOLD}{_BMAG}=== Timberborn Live ==={_RST}\n")
            _render(data)
            time.sleep(3)
    except KeyboardInterrupt:
        print(f"\n  {_DIM}bye!{_RST}\n")


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _cast(a):
    if a.lower() == "true":
        return True
    if a.lower() == "false":
        return False
    try:
        return int(a)
    except ValueError:
        try:
            return float(a)
        except ValueError:
            return a


def main():
    if len(sys.argv) < 2:
        bot = Timberbot()
        print("usage: python timberbot.py <method> [args...]")
        print()
        print("methods:")
        for name in sorted(dir(bot)):
            if name.startswith("_"):
                continue
            method = getattr(bot, name)
            if callable(method):
                doc = (method.__doc__ or "").split("\n")[0].strip()
                print(f"  {name:30s} {doc}")
        print(f"\n  {'watch':30s} live terminal dashboard")
        sys.exit(1)

    raw_args = [a for a in sys.argv[1:] if a != "--"]
    method_name = raw_args[0]
    args = raw_args[1:]

    if method_name == "watch":
        _watch()
        return

    bot = Timberbot()

    if not hasattr(bot, method_name):
        print(f"error: unknown method '{method_name}'", file=sys.stderr)
        sys.exit(1)

    method = getattr(bot, method_name)
    if not callable(method):
        print(json.dumps(method, indent=2))
        sys.exit(0)

    result = method(*[_cast(a) for a in args])
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
