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
import requests


# ---------------------------------------------------------------------------
# API client
# ---------------------------------------------------------------------------

class Timberbot:
    """Client for Timberbot API (port 8085)."""

    def __init__(self, host="localhost", port=8085):
        self.url = f"http://{host}:{port}"
        self.s = requests.Session()
        self.s.headers["Accept"] = "application/json"

    def _get(self, path):
        r = self.s.get(f"{self.url}{path}", timeout=5)
        r.raise_for_status()
        return r.json()

    def _post(self, path, data):
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

    def unlock_building(self, building):
        """Unlock a building using science points."""
        return self._post("/api/science/unlock", {"building": building})

    def notifications(self):
        """Game notification history: [{subject, description, entityId, cycle, cycleDay}]."""
        return self._get("/api/notifications")

    def alerts(self):
        """Computed alerts from building data: unstaffed, unpowered, unreachable."""
        buildings = self.buildings()
        issues = []
        for b in buildings:
            name = b.get("name", "").replace("(Clone)", "")
            bid = b.get("id", 0)
            if b.get("desiredWorkers", 0) > 0 and b.get("assignedWorkers", 0) < b.get("desiredWorkers", 0):
                issues.append({"type": "unstaffed", "id": bid, "name": name,
                               "workers": f"{b.get('assignedWorkers', 0)}/{b.get('desiredWorkers', 0)}"})
            if b.get("isConsumer") and not b.get("powered"):
                issues.append({"type": "unpowered", "id": bid, "name": name})
            if b.get("reachable") is False:
                issues.append({"type": "unreachable", "id": bid, "name": name})
            for s in b.get("statuses", []):
                if s not in ("", "Normal"):
                    issues.append({"type": "status", "id": bid, "name": name, "status": s})
        return issues

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

    def set_workers(self, building_id, count):
        """Set desired worker count (0 to maxWorkers)."""
        return self._post("/api/workers", {"id": building_id, "count": count})

    def set_floodgate(self, building_id, height):
        """Set floodgate height (clamped to min/max)."""
        return self._post("/api/floodgate", {"id": building_id, "height": height})

    _ORIENTATIONS = {"south": 0, "west": 1, "north": 2, "east": 3,
                     "s": 0, "w": 1, "n": 2, "e": 3}

    def place_building(self, prefab, x, y, z, orientation="south"):
        """Place a building. Orientation: south, west, north, east (or s/w/n/e)."""
        o = str(orientation).lower()
        if o not in self._ORIENTATIONS:
            return {"error": f"invalid orientation '{orientation}', use: south, west, north, east"}
        return self._post("/api/building/place", {
            "prefab": prefab, "x": x, "y": y, "z": z,
            "orientation": self._ORIENTATIONS[o]
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

    # -- helpers --

    def tree_clusters(self, radius=10, top=5):
        """Find clusters of grown trees. Returns top clusters by grown count."""
        all_trees = self.trees()
        grown = [t for t in all_trees if t.get("grown") and t.get("alive")]
        if not grown:
            return []

        # grid-based clustering: divide map into cells of `radius` size
        cells = {}
        for t in grown:
            cx = t["x"] // radius * radius + radius // 2
            cy = t["y"] // radius * radius + radius // 2
            key = (cx, cy, t.get("z", 0))
            if key not in cells:
                cells[key] = {"x": cx, "y": cy, "z": key[2], "grown": 0, "total": 0}
            cells[key]["grown"] += 1

        # count total trees (including seedlings) in each cell
        for t in all_trees:
            if not t.get("alive"):
                continue
            cx = t["x"] // radius * radius + radius // 2
            cy = t["y"] // radius * radius + radius // 2
            key = (cx, cy, t.get("z", 0))
            if key in cells:
                cells[key]["total"] += 1

        clusters = sorted(cells.values(), key=lambda c: -c["grown"])
        return clusters[:top]

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
        """Scan an area. Returns structured data: occupied tiles + water tiles, skipping empty ground."""
        data = self.map(x - radius, y - radius, x + radius, y + radius)
        tiles = data.get("tiles", [])

        occupied = []
        water = []

        for t in tiles:
            tx, ty = t["x"], t["y"]
            has_occupant = t.get("occupant")
            has_water = t.get("water", 0) > 0
            is_entrance = t.get("entrance", False)
            is_seedling = t.get("seedling", False)
            is_dead = t.get("dead", False)

            if has_occupant:
                name = has_occupant.replace("(Clone)", "").replace(".IronTeeth", "").replace(".Folktails", "")
                if is_dead:
                    name += ".dead"
                elif is_seedling:
                    name += ".seedling"
                if is_entrance:
                    name += ".entrance"
                occupied.append({"x": tx, "y": ty, "what": name})
            elif is_entrance:
                occupied.append({"x": tx, "y": ty, "what": "entrance"})

            if has_water and not has_occupant:
                water.append({"x": tx, "y": ty})

        return {
            "center": f"{x},{y}",
            "radius": radius,
            "default": "ground",
            "occupied": occupied,
            "water": water,
        }

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

        data = self.map(x - radius, y - radius, x + radius, y + radius)
        tiles = {(t["x"], t["y"]): t for t in data.get("tiles", [])}
        legend = {}

        lines = []
        for ty in range(y + radius, y - radius - 1, -1):
            row = f"{DIM}{ty:3d}{R} "
            for tx in range(x - radius, x + radius + 1):
                t = tiles.get((tx, ty))
                if not t:
                    row += f"{DIM}?{R}"
                elif t.get("entrance") and not t.get("occupant"):
                    row += f"{BWHT}@{R}"
                    legend["@"] = (BWHT, "entrance")
                elif t.get("occupant"):
                    oname = t["occupant"].replace("(Clone)", "").replace(".IronTeeth", "").replace(".Folktails", "")
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
                        row += f"{co}{ch}{R}"
                    else:
                        row += f"{DIM}{oname[0]}{R}"
                elif t["water"] > 0:
                    row += f"{BLU}~{R}"
                    legend["~"] = (BLU, "water")
                elif t["terrain"] > 0:
                    row += f"{DIM}.{R}"
                else:
                    row += " "
            lines.append(row)

        axis = f"    {DIM}" + "".join(str(i % 10) for i in range(x - radius, x + radius + 1)) + R
        lines.append(axis)

        leg = "  "
        for ch, (co, label) in sorted(legend.items(), key=lambda x: x[1][1]):
            leg += f" {co}{ch}{R} {label}"
        lines.append(leg)

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


def _flatten_for_toon(method, data):
    """Flatten nested structures so TOON renders them as tables."""
    if method == "summary" and isinstance(data, dict):
        t = data.get("time", {})
        w = data.get("weather", {})
        tr = data.get("trees", {})
        flat = {
            "day": t.get("dayNumber", 0),
            "dayProgress": round(t.get("dayProgress", 0), 2),
            "cycle": w.get("cycle", 0),
            "cycleDay": w.get("cycleDay", 0),
            "isHazardous": w.get("isHazardous", False),
            "tempDays": w.get("temperateWeatherDuration", 0),
            "hazardDays": w.get("hazardousWeatherDuration", 0),
            "markedGrown": tr.get("markedGrown", 0),
            "markedSeedling": tr.get("markedSeedling", 0),
            "unmarkedGrown": tr.get("unmarkedGrown", 0),
        }
        for d in data.get("districts", []):
            pop = d.get("population", {})
            flat["adults"] = pop.get("adults", 0)
            flat["children"] = pop.get("children", 0)
            flat["bots"] = pop.get("bots", 0)
            for good, val in d.get("resources", {}).items():
                flat[good] = val.get("available", 0) if isinstance(val, dict) else val
        return flat

    if method == "map" and isinstance(data, dict) and "tiles" in data:
        tiles = data.get("tiles", [])
        flat = []
        for t in tiles:
            row = {"x": t["x"], "y": t["y"],
                   "terrain": t.get("terrain", 0),
                   "water": t.get("water", 0)}
            occ = t.get("occupant", "")
            if occ:
                occ = occ.replace("(Clone)", "").replace(".IronTeeth", "").replace(".Folktails", "")
            row["occupant"] = occ
            if t.get("entrance"):
                row["entrance"] = True
            if t.get("seedling"):
                row["seedling"] = True
            flat.append(row)
        if flat:
            # make uniform -- add missing keys
            all_keys = set()
            for r in flat:
                all_keys.update(r.keys())
            for r in flat:
                for k in all_keys:
                    if k not in r:
                        r[k] = False if k in ("entrance", "seedling") else ""
        return {"mapSize": data.get("mapSize", {}), "region": data.get("region", {}), "tiles": flat} if not flat else flat

    if method == "resources" and isinstance(data, dict):
        flat = []
        for district, goods in data.items():
            if isinstance(goods, dict):
                for good, val in goods.items():
                    if isinstance(val, dict):
                        flat.append({"district": district, "good": good,
                                     "available": val.get("available", 0),
                                     "all": val.get("all", 0)})
        return flat or data

    if method == "districts" and isinstance(data, list):
        flat = []
        for d in data:
            row = {"name": d.get("name", "?")}
            pop = d.get("population", {})
            row["adults"] = pop.get("adults", 0)
            row["children"] = pop.get("children", 0)
            row["bots"] = pop.get("bots", 0)
            for good, val in d.get("resources", {}).items():
                row[good] = val.get("available", 0) if isinstance(val, dict) else val
            flat.append(row)
        return flat or data

    if method == "buildings" and isinstance(data, list):
        flat = []
        for b in data:
            row = {"id": b["id"], "name": b.get("name", "").replace("(Clone)", ""),
                   "x": b.get("x", 0), "y": b.get("y", 0), "z": b.get("z", 0),
                   "orientation": b.get("orientation", 0),
                   "finished": b.get("finished", False),
                   "paused": b.get("paused", False),
                   "priority": b.get("priority", "")}
            if "desiredWorkers" in b:
                row["workers"] = f"{b.get('assignedWorkers', 0)}/{b.get('desiredWorkers', 0)}"
            else:
                row["workers"] = ""
            flat.append(row)
        return flat or data

    if method == "beavers" and isinstance(data, list):
        flat = []
        for b in data:
            critical = [k for k, v in b.get("needs", {}).items() if v.get("isCritical")]
            wb = round(b.get("wellbeing", 0), 2)
            # wellbeing tiers: 0-3=miserable, 4-7=unhappy, 8-11=okay, 12-15=happy, 16+=ecstatic
            if wb >= 16: tier = "ecstatic"
            elif wb >= 12: tier = "happy"
            elif wb >= 8: tier = "okay"
            elif wb >= 4: tier = "unhappy"
            else: tier = "miserable"
            flat.append({"id": b["id"],
                         "name": b.get("name", "").replace("(Clone)", ""),
                         "wellbeing": wb,
                         "tier": tier,
                         "isBot": b.get("isBot", False),
                         "critical": "+".join(critical) if critical else ""})
        return flat or data

    return data


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
    else:
        result = _flatten_for_toon(method_name, result)
        try:
            import toons
            print(toons.dumps(result))
        except ImportError:
            print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
