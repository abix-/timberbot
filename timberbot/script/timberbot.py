#!/usr/bin/env python
"""Timberbot -- control Timberborn over HTTP.

CLI for the Timberbot API (port 8085). Talks to the C# mod running inside the game.
The API does all data processing; this client is a thin wrapper that formats output.

Output formats:
    TOON (default): compact tabular format optimized for AI token efficiency
    JSON (--json):  full nested data for programmatic access

Usage:
    python timberbot.py                     list all methods
    python timberbot.py summary             colony dashboard (one call, all stats)
    python timberbot.py buildings           list all buildings
    python timberbot.py --json summary      full JSON output
    python timberbot.py watch               live terminal dashboard
    python timberbot.py place_building prefab:LumberjackFlag.IronTeeth x:120 y:130 z:2

As a library:
    from timberbot import Timberbot
    bot = Timberbot()                       # toon format (flat)
    bot = Timberbot(json_mode=True)         # json format (full)
    bot.summary()
"""
import json
import sys
import time
import requests


# ---------------------------------------------------------------------------
# API client
# ---------------------------------------------------------------------------

class Timberbot:
    """Client for Timberbot API (port 8085).

    All data processing happens server-side in the C# mod. This client sends
    a format param ("toon" or "json") and passes the response straight through.
    No client-side transformation of API data.
    """

    def __init__(self, host="localhost", port=8085, json_mode=False):
        self.url = f"http://{host}:{port}"
        self._format = "json" if json_mode else "toon"
        self.s = requests.Session()
        self.s.headers["Accept"] = "application/json"

    def _get(self, path):
        r = self.s.get(f"{self.url}{path}", params={"format": self._format}, timeout=5)
        r.raise_for_status()
        return r.json()

    def _post(self, path, data):
        data["format"] = self._format
        r = self.s.post(f"{self.url}{path}", json=data, timeout=5)
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

    def buildings(self, limit=0, offset=0):
        """All buildings with coords, workers, reachability, and power status."""
        data = self._get("/api/buildings")
        if offset: data = data[offset:]
        if limit: data = data[:limit]
        return data

    def trees(self, limit=0, offset=0):
        """All cuttable trees: [{id, name, x, y, z, marked, alive}]."""
        data = self._get("/api/trees")
        if offset: data = data[offset:]
        if limit: data = data[:limit]
        return data

    def gatherables(self, limit=0, offset=0):
        """All gatherable resources (berry bushes etc): [{id, name, x, y, z, alive}]."""
        data = self._get("/api/gatherables")
        if offset: data = data[offset:]
        if limit: data = data[:limit]
        return data

    def beavers(self, limit=0, offset=0):
        """All beavers with wellbeing and needs: [{id, name, wellbeing, needs, anyCritical}]."""
        data = self._get("/api/beavers")
        if offset: data = data[offset:]
        if limit: data = data[:limit]
        return data

    def workhours(self):
        """Work schedule: {endHours, areWorkingHours, hoursPassedToday}."""
        return self._get("/api/workhours")

    def migrate(self, from_district, to_district, count=1):
        """Move beavers between districts."""
        return self._post("/api/district/migrate", {
            "from": from_district, "to": to_district, "count": count
        })

    def set_workhours(self, end_hours):
        """Set when work ends (1-24). Beavers work from dawn until endHours."""
        return self._post("/api/workhours", {"endHours": end_hours})

    def science(self):
        """Science points and unlockable buildings: {points, unlockables: [{name, cost, unlocked}]}."""
        return self._get("/api/science")

    def wellbeing(self):
        """Population wellbeing breakdown by category: {beavers, categories: [{group, current, max, needs}]}."""
        return self._get("/api/wellbeing")

    def unlock_building(self, building):
        """Unlock a building using science points."""
        return self._post("/api/science/unlock", {"building": building})

    def notifications(self):
        """Game notification history: [{subject, description, entityId, cycle, cycleDay}]."""
        return self._get("/api/notifications")

    def alerts(self):
        """Alerts: unstaffed, unpowered, unreachable, status issues."""
        return self._get("/api/alerts")

    def distribution(self):
        """Distribution settings per district: [{district, goods: [{good, importOption, exportThreshold}]}]."""
        return self._get("/api/distribution")

    def set_distribution(self, district, good, import_option="", export_threshold=-1):
        """Set import/export for a good in a district. import_option: Forced, Auto, None."""
        return self._post("/api/distribution", {
            "district": district, "good": good,
            "import": import_option, "exportThreshold": export_threshold
        })

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

    def set_priority(self, building_id, priority, type=""):
        """Set building priority. Values: VeryLow, Normal, VeryHigh. Type: workplace (finished) or construction (building)."""
        return self._post("/api/priority", {"id": building_id, "priority": priority, "type": type})

    def set_haul_priority(self, building_id, prioritized=True):
        """Set hauler priority on a building. Haulers will deliver goods here first."""
        return self._post("/api/hauling/priority", {"id": building_id, "prioritized": prioritized})

    def set_recipe(self, building_id, recipe):
        """Set manufactory recipe. Use 'none' to clear. Lists available recipes on error."""
        return self._post("/api/recipe", {"id": building_id, "recipe": recipe})

    def set_farmhouse_action(self, building_id, action):
        """Set farmhouse priority action: 'planting' or 'harvesting'."""
        return self._post("/api/farmhouse/action", {"id": building_id, "action": action})

    def set_plantable_priority(self, building_id, plantable):
        """Set prioritized plantable on forester/gatherer. Use 'none' to clear."""
        return self._post("/api/plantable/priority", {"id": building_id, "plantable": plantable})

    def set_workers(self, building_id, count):
        """Set desired worker count (0 to maxWorkers)."""
        return self._post("/api/workers", {"id": building_id, "count": count})

    def set_floodgate(self, building_id, height):
        """Set floodgate height (clamped to min/max)."""
        return self._post("/api/floodgate", {"id": building_id, "height": height})

    def debug(self, target="help", **kwargs):
        """Debug inspect game internals. Targets: help, fields, inspect, preview, entity. Pass extra key:value args."""
        body = {"target": target}
        body.update(kwargs)
        return self._post("/api/debug", body)

    def find_placement(self, prefab, x1, y1, x2, y2):
        """Find valid placements for a building in an area. Returns spots sorted by path access."""
        return self._post("/api/placement/find", {"prefab": prefab, "x1": x1, "y1": y1, "x2": x2, "y2": y2})

    def place_building(self, prefab, x, y, z, orientation="south"):
        """Place a building. Orientation: south, west, north, east."""
        return self._post("/api/building/place", {
            "prefab": prefab, "x": x, "y": y, "z": z,
            "orientation": str(orientation).lower()
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

    def place_path(self, x1, y1, x2, y2, z=0):
        """Route a straight-line path with auto-stairs at z-level changes. z param ignored (auto-detected)."""
        return self._post("/api/path/route", {"x1": x1, "y1": y1, "x2": x2, "y2": y2})

    # -- helpers --

    def tree_clusters(self):
        """Find clusters of grown trees. Returns top clusters by grown count."""
        return self._get("/api/tree_clusters")

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
        """Scan an area. Returns occupied tiles + water tiles, skipping empty ground."""
        return self._post("/api/scan", {"x": x, "y": y, "radius": radius})

    def visual(self, x, y, radius=10):
        """Colored ASCII map for humans. Same data as scan() but rendered as a roguelike grid."""
        R = "\033[0m"
        DIM = "\033[2m"
        RED = "\033[31m"
        GRN = "\033[32m"
        YEL = "\033[33m"
        BLU = "\033[34m"
        MAG = "\033[35m"
        CYN = "\033[36m"
        BGRN = "\033[92m"
        BYEL = "\033[93m"
        BBLU = "\033[94m"
        BWHT = "\033[97m"
        BOLD = "\033[1m"

        STYLE = {
            "Path": ("=", YEL),
            "DistrictCenter": ("D", BOLD + BYEL),
            "Rowhouse": ("H", YEL), "Barrack": ("H", YEL), "Lodge": ("H", YEL),
            "Breeding": ("R", YEL),
            "LumberMill": ("M", BWHT), "WoodWorkshop": ("M", BWHT),
            "IndustrialLumberMill": ("M", BWHT),
            "FarmHouse": ("F", CYN), "Forester": ("f", GRN),
            "PowerWheel": ("E", BYEL), "PowerShaft": ("E", BYEL),
            "Inventor": ("S", BWHT), "Numbercruncher": ("S", BWHT),
            "Lumberjack": ("L", RED), "Gatherer": ("G", MAG),
            "Hauling": ("K", RED), "Scavenger": ("G", RED),
            "Pump": ("P", BBLU), "Tank": ("W", BBLU),
            "Floodgate": ("X", CYN), "Dam": ("X", CYN),
            "Levee": ("X", CYN), "Sluice": ("X", CYN),
            "Warehouse": ("$", YEL), "Pile": ("$", YEL),
            "Pine": ("T", GRN), "Birch": ("T", GRN), "Oak": ("T", GRN),
            "Maple": ("T", GRN), "Chestnut": ("T", GRN),
            "Bush": ("B", MAG), "berry": ("B", MAG),
            "Kohlrabi": ("k", BGRN), "Carrot": ("c", BGRN),
            "Potato": ("p", BGRN), "Wheat": ("w", BGRN),
            "Cassava": ("a", BGRN), "Sunflower": ("s", BGRN),
            "Corn": ("n", BGRN), "Eggplant": ("e", BGRN),
            "Cattail": ("l", BGRN), "Spadderdock": ("d", BGRN),
            "Soybean": ("y", BGRN), "Canola": ("o", BGRN),
            "Campfire": ("C", RED),
        }

        def _zbg(z):
            # gradient within tens bands: 0-9 dark(234-242), 10-19 bright(244-252), 20-22 brightest(254+)
            if z < 10:
                shade = 234 + z
            elif z < 20:
                shade = 244 + (z - 10)
            else:
                shade = 254 + min(z - 20, 1)
            return f"\033[48;5;{min(shade, 255)}m"

        data = self.map(x - radius, y - radius, x + radius, y + radius)
        tiles = {(t["x"], t["y"]): t for t in data.get("tiles", [])}
        legend = {}
        z_levels = set()

        lines = []
        for ty in range(y + radius, y - radius - 1, -1):
            row = f"{DIM}{ty:3d}{R} "
            for tx in range(x - radius, x + radius + 1):
                t = tiles.get((tx, ty))
                if not t:
                    row += f"{DIM}?{R}"
                elif t.get("entrance") and not t.get("occupant"):
                    bg = _zbg(t["terrain"])
                    z_levels.add(t["terrain"])
                    row += f"{bg}{BWHT}@{R}"
                    legend["@"] = (BWHT, "entrance")
                elif t.get("occupant"):
                    oname = t["occupant"]
                    bg = _zbg(t["terrain"])
                    z_levels.add(t["terrain"])
                    ch, co = None, None
                    for key, (c, s) in STYLE.items():
                        if key.lower() in oname.lower():
                            ch, co = c, s
                            legend[c] = (s, key)
                            break
                    if ch == "T" and t.get("seedling"):
                        ch, co = "t", DIM + GRN
                        legend["t"] = (co, "seedling")
                    if ch:
                        row += f"{bg}{co}{ch}{R}"
                    else:
                        row += f"{bg}{DIM}{oname[0]}{R}"
                elif t["water"] > 0:
                    bg = _zbg(t["terrain"])
                    z_levels.add(t["terrain"])
                    row += f"{bg}{BLU}~{R}"
                    legend["~"] = (BLU, "water")
                elif t["terrain"] > 0:
                    bg = _zbg(t["terrain"])
                    z_levels.add(t["terrain"])
                    zch = str(t["terrain"] % 10)
                    zco = GRN if t.get("moist") else DIM
                    row += f"{bg}{zco}{zch}{R}"
                else:
                    row += " "
            lines.append(row)

        axis = f"    {DIM}" + "".join(str(i % 10) for i in range(x - radius, x + radius + 1)) + R
        lines.append(axis)

        leg = "  "
        for ch, (co, label) in sorted(legend.items(), key=lambda x: x[1][1]):
            leg += f" {co}{ch}{R} {label}"
        lines.append(leg)

        if len(z_levels) > 1:
            zleg = "   height:"
            for z in sorted(z_levels):
                zleg += f" {_zbg(z)} z={z} {R}"
            lines.append(zleg)

        # print directly to terminal instead of returning as JSON
        print("\n".join(lines))
        return {"rendered": True, "tiles": len(tiles)}

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

import inspect







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


def _method_params(method):
    """Get parameter names (excluding self) for a method."""
    sig = inspect.signature(method)
    return [p.name for p in sig.parameters.values() if p.name != "self"]


def _format_usage(name, method):
    """Format usage string showing key:value pairs."""
    params = []
    sig = inspect.signature(method)
    for p in sig.parameters.values():
        if p.name == "self":
            continue
        if p.default is inspect.Parameter.empty:
            params.append(f"{p.name}:VALUE")
        else:
            params.append(f"[{p.name}:{p.default}]")
    return f"  {name} {' '.join(params)}"


def main():
    if len(sys.argv) < 2:
        bot = Timberbot()
        print("usage: python timberbot.py <method> key:value ...")
        print()
        print("methods:")
        for name in sorted(dir(bot)):
            if name.startswith("_"):
                continue
            method = getattr(bot, name)
            if callable(method):
                doc = (method.__doc__ or "").split("\n")[0].strip()
                print(f"  {name:30s} {doc}")
                usage = _format_usage(name, method)
                if "VALUE" in usage:
                    print(f"    {usage.strip()}")
        print(f"\n  {'watch':30s} live terminal dashboard")
        sys.exit(1)

    json_mode = "--json" in sys.argv
    raw_args = [a for a in sys.argv[1:] if a not in ("--", "--json")]
    method_name = raw_args[0]
    args = raw_args[1:]

    if method_name == "watch":
        _watch()
        return

    bot = Timberbot(json_mode=json_mode)

    if not hasattr(bot, method_name):
        print(f"error: unknown method '{method_name}'", file=sys.stderr)
        sys.exit(1)

    method = getattr(bot, method_name)
    if not callable(method):
        print(json.dumps(method, indent=2))
        sys.exit(0)

    params = _method_params(method)
    kwargs = {}
    for a in args:
        if ":" in a:
            key, val = a.split(":", 1)
            kwargs[key] = _cast(val)
        else:
            print(f"error: expected key:value, got '{a}'", file=sys.stderr)
            print(f"usage: {_format_usage(method_name, method).strip()}", file=sys.stderr)
            sys.exit(1)

    result = method(**kwargs)
    if isinstance(result, str):
        print(result)
    elif isinstance(result, dict) and result.get("rendered"):
        pass  # visual() already printed
    elif json_mode:
        print(json.dumps(result, indent=2))
    else:
        try:
            import toons
            print(toons.dumps(result))
        except ImportError:
            print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
