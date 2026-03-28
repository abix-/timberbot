#!/usr/bin/env python
"""Timberbot -- control Timberborn over HTTP.

CLI for the Timberbot API (port 8085). Talks to the C# mod running inside the game.
The API does all data processing; this client is a thin wrapper that formats output.

Output formats:
    TOON (default): compact tabular format optimized for AI token efficiency
    JSON (--json):  full nested data for programmatic access

Usage:
    timberbot.py                     list all methods
    timberbot.py summary             colony dashboard (one call, all stats)
    timberbot.py buildings           list all buildings
    timberbot.py --json summary      full JSON output
    timberbot.py top                 live colony dashboard
    timberbot.py place_building prefab:LumberjackFlag.IronTeeth x:120 y:130 z:2

As a library:
    from timberbot import Timberbot
    bot = Timberbot()                       # toon format (flat)
    bot = Timberbot(json_mode=True)         # json format (full)
    bot.summary()
"""
import json
import os
import re
import subprocess
import sys
import time
import requests

_MEMORY_BASE = os.path.join(os.path.expanduser("~"), "Documents", "Timberborn", "Mods", "Timberbot", "memory")
_MEMORY_DIR = _MEMORY_BASE  # overridden per-settlement by brain()


def _sanitize_name(name):
    """Sanitize settlement name for filesystem."""
    return re.sub(r'[<>:"/\\|?*]', '_', name).strip() or "unknown"


def _load_brain_file(mdir=None):
    """Load brain.toon or return empty dict."""
    d = mdir or _MEMORY_DIR
    bpath = os.path.join(d, "brain.toon")
    if os.path.exists(bpath):
        try:
            import toons
            with open(bpath) as f:
                return toons.load(f)
        except Exception:
            pass
    return {}


def _save_brain_file(brain, mdir=None):
    """Write brain.toon."""
    d = mdir or _MEMORY_DIR
    os.makedirs(d, exist_ok=True)
    import toons
    with open(os.path.join(d, "brain.toon"), "w") as f:
        toons.dump(brain, f)


def _update_brain_maps(region, x1, y1, x2, y2, fname, mdir=None):
    """Update the maps index in brain.toon when a map is saved."""
    brain = _load_brain_file(mdir)
    maps = brain.get("maps", {})
    entry = maps.get(region, {"x1": x1, "y1": y1, "x2": x2, "y2": y2, "files": []})
    entry["x1"] = x1
    entry["y1"] = y1
    entry["x2"] = x2
    entry["y2"] = y2
    if fname not in entry["files"]:
        entry["files"].append(fname)
    maps[region] = entry
    brain["maps"] = maps
    _save_brain_file(brain, mdir)


# ---------------------------------------------------------------------------
# API client
# ---------------------------------------------------------------------------

class TimberbotError(Exception):
    """API returned an error response. e.code is the prefix before ':', e.response is the full dict."""
    def __init__(self, response):
        self.response = response
        self.error = response.get("error", "unknown")
        self.code = self.error.split(":")[0].strip()
        super().__init__(self.error)


class Timberbot:
    """Client for Timberbot API (port 8085).

    All data processing happens server-side in the C# mod. This client sends
    a format param ("toon" or "json") and passes the response straight through.
    No client-side transformation of API data.
    """

    def __init__(self, host=None, port=None, json_mode=False, write_timeout=60):
        if host is None or port is None:
            try:
                spath = os.path.join(os.path.expanduser("~"), "Documents", "Timberborn", "Mods", "Timberbot", "settings.json")
                with open(spath) as f:
                    settings = json.load(f)
                if host is None:
                    host = settings.get("httpHost", "127.0.0.1")
                if port is None:
                    port = settings.get("httpPort", 8085)
            except Exception:
                host = host or "127.0.0.1"
                port = port or 8085
        self.url = f"http://{host}:{port}"
        self._format = "json" if json_mode else "toon"
        self._write_timeout = write_timeout
        self.s = requests.Session()
        self.s.headers["Accept"] = "application/json"

    def _check(self, data):
        if isinstance(data, dict) and "error" in data:
            raise TimberbotError(data)
        return data

    def _get(self, path, params=None):
        p = {"format": self._format}
        if params:
            p.update(params)
        r = self.s.get(f"{self.url}{path}", params=p, timeout=5)
        r.raise_for_status()
        return self._check(r.json())

    def _post(self, path, data):
        data["format"] = self._format
        r = self.s.post(f"{self.url}{path}", json=data, timeout=self._write_timeout)
        return self._check(r.json())

    def _post_json(self, path, data):
        """Force JSON format for internal programmatic use."""
        data["format"] = "json"
        r = self.s.post(f"{self.url}{path}", json=data, timeout=self._write_timeout)
        return self._check(r.json())

    def _get_json(self, path, params=None):
        """Force JSON format for internal programmatic use."""
        p = {"format": "json"}
        if params:
            p.update(params)
        r = self.s.get(f"{self.url}{path}", params=p, timeout=5)
        r.raise_for_status()
        return self._check(r.json())

    # -- connection --

    def ping(self):
        """True if Timberbot mod is reachable."""
        try:
            return self._get("/api/ping").get("ready", False)
        except (requests.ConnectionError, requests.Timeout):
            return False

    # -- webhooks --

    def register_webhook(self, url, events=None):
        """Register a webhook URL to receive push notifications for game events.
        events: list of event names to subscribe to (None = all events).
        Available: drought.start, drought.end, building.placed, building.demolished,
                   beaver.born, beaver.died, day.start, night.start"""
        data = {"url": url}
        if events:
            data["events"] = events
        return self._post("/api/webhooks", data)

    def unregister_webhook(self, id):
        """Unregister a webhook by ID."""
        return self._post("/api/webhooks/delete", {"id": id})

    def list_webhooks(self):
        """List all registered webhooks."""
        return self._get("/api/webhooks")

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

    def buildings(self, limit=0, offset=0, detail="basic", id=0):
        """All buildings. detail: basic (compact), full (all fields). id selects a single building.
        Server defaults to limit=100. CLI passes limit=0 (unlimited) by default."""
        params = {"limit": limit, "offset": offset}
        if id:
            params["id"] = id
        if detail != "basic":
            params["detail"] = detail
        return self._get("/api/buildings", params=params)

    def buildings_v2(self, limit=0, offset=0, detail="basic", id=0):
        """Compatibility alias for the native /api/buildings snapshot path."""
        params = {"limit": limit, "offset": offset}
        if id:
            params["id"] = id
        if detail != "basic":
            params["detail"] = detail
        return self._get("/api/buildings", params=params)

    def trees(self, limit=0, offset=0):
        """Trees: [{id, name, x, y, z, marked, alive, grown, growth}]."""
        return self._get("/api/trees", params={"limit": limit, "offset": offset})

    def crops(self, limit=0, offset=0):
        """Crops in the ground: [{id, name, x, y, z, marked, alive, grown, growth}]."""
        return self._get("/api/crops", params={"limit": limit, "offset": offset})

    def gatherables(self, limit=0, offset=0):
        """All gatherable resources (berry bushes etc): [{id, name, x, y, z, alive}]."""
        return self._get("/api/gatherables", params={"limit": limit, "offset": offset})

    def beavers(self, limit=0, offset=0, detail="basic", id=0):
        """All beavers with wellbeing and needs. detail:full shows all needs with group category. id selects a single beaver."""
        params = {"limit": limit, "offset": offset}
        if id:
            params["id"] = id
        if detail != "basic":
            params["detail"] = detail
        return self._get("/api/beavers", params=params)

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

    def power(self):
        """Power networks: [{id, supply, demand, buildings}]."""
        return self._get("/api/power")

    def speed(self):
        """Current game speed: {speed: 0-3}."""
        return self._get("/api/speed")

    def tiles(self, x1=0, y1=0, x2=0, y2=0):
        """Tile data for a region: terrain, water, occupants, moisture, contamination. No args = map size only."""
        return self._get("/api/tiles", {"x1": x1, "y1": y1, "x2": x2, "y2": y2})

    # -- write actions (verb_noun) --

    def set_speed(self, speed):
        """Set game speed. 0=pause, 1=normal, 2=fast, 3=fastest."""
        return self._post("/api/speed", {"speed": speed})

    def pause_building(self, id):
        """Pause a building."""
        return self._post("/api/building/pause", {"id": id, "paused": True})

    def unpause_building(self, id):
        """Unpause a building."""
        return self._post("/api/building/pause", {"id": id, "paused": False})

    def set_clutch(self, id, engaged):
        """Engage or disengage a clutch. engaged: True/False."""
        return self._post("/api/building/clutch", {"id": id, "engaged": engaged})

    def set_priority(self, id, priority, type=""):
        """Set building priority. Values: VeryLow, Normal, VeryHigh. Type: workplace (finished) or construction (building)."""
        return self._post("/api/building/priority", {"id": id, "priority": priority, "type": type})

    def set_haul_priority(self, id, prioritized=True):
        """Set hauler priority on a building. Haulers will deliver goods here first."""
        return self._post("/api/building/hauling", {"id": id, "prioritized": prioritized})

    def set_recipe(self, id, recipe):
        """Set manufactory recipe. Use 'none' to clear. Lists available recipes on error."""
        return self._post("/api/building/recipe", {"id": id, "recipe": recipe})

    def set_farmhouse_action(self, id, action):
        """Set farmhouse priority action: 'planting' or 'harvesting'."""
        return self._post("/api/building/farmhouse", {"id": id, "action": action})

    def set_plantable_priority(self, id, plantable):
        """Set prioritized plantable on forester/gatherer. Use 'none' to clear."""
        return self._post("/api/building/plantable", {"id": id, "plantable": plantable})

    def set_workers(self, id, count):
        """Set desired worker count (0 to maxWorkers)."""
        return self._post("/api/building/workers", {"id": id, "count": count})

    def set_floodgate(self, id, height):
        """Set floodgate height (clamped to min/max)."""
        return self._post("/api/building/floodgate", {"id": id, "height": height})

    def debug(self, target="help", **kwargs):
        """Generic live debug surface. Targets include help, roots, get, fields, describe, call, compare, assert, validate, validate_all."""
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

    def demolish_building(self, id):
        """Demolish a building. Get IDs from buildings()."""
        return self._post("/api/building/demolish", {"id": id})

    def demolish_crop(self, id):
        """Demolish a planted crop entity by ID. Get IDs from crops()."""
        return self._post("/api/crop/demolish", {"id": id})

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

    def find_planting(self, crop, id=0, x1=0, y1=0, x2=0, y2=0, z=0):
        """Find valid planting spots. Use id for farmhouse range, or x1/y1/x2/y2/z for area."""
        return self._post("/api/planting/find", {
            "crop": crop, "id": id,
            "x1": x1, "y1": y1, "x2": x2, "y2": y2, "z": z
        })

    def building_range(self, id):
        """Get work range tiles for a building (farmhouse, lumberjack, forester)."""
        return self._post("/api/building/range", {"id": id})

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

    def set_capacity(self, id, capacity):
        """Set stockpile capacity."""
        return self._post("/api/stockpile/capacity", {"id": id, "capacity": capacity})

    def set_good(self, id, good):
        """Set allowed good on a single-good stockpile."""
        return self._post("/api/stockpile/good", {"id": id, "good": good})

    def place_path(self, x1, y1, x2, y2, z=0, style="direct", sections=0, timings=False):
        """Route a path using A* to avoid obstacles, with auto-stairs at z-level changes. z param ignored. style: 'direct' (staircase) or 'straight' (minimize turns). sections: 0=all, N=place N stair crossings then stop."""
        body = {"x1": x1, "y1": y1, "x2": x2, "y2": y2, "style": style}
        if sections: body["sections"] = sections
        if timings: body["timings"] = True
        return self._post("/api/path/place", body)

    # -- helpers --

    def tree_clusters(self):
        """Find clusters of grown trees. Returns top clusters by grown count."""
        return self._get("/api/tree_clusters")

    def food_clusters(self):
        """Find clusters of gatherable food (berries, bushes). Returns top clusters by grown count."""
        return self._get("/api/food_clusters")

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

    def map(self, x1, y1, x2, y2, name=None):
        """Colored ASCII map with terrain height shading, buildings, water, trees. name: save to memory/."""
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
        BMAG = "\033[95m"
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
            "Stairs": ("/", YEL), "Platform": ("_", YEL),
            "Metalsmith": ("m", BWHT), "Smelter": ("m", BWHT),
            "GearWorkshop": ("g", BWHT),
            "BotAssembler": ("b", BMAG), "BotPartFactory": ("b", BMAG),
            "ChargingStation": ("z", BMAG),
            "FluidDump": ("V", BBLU), "DoubleShower": ("v", BBLU),
            "SwimmingPool": ("v", BBLU),
            "Scratcher": ("~", GRN), "Bench": ("~", GRN),
            "ExercisePlaza": ("~", GRN), "MedicalBed": ("~", GRN),
            "Brazier": ("*", RED), "Lantern": ("*", YEL),
            "BeaverBust": ("*", YEL), "Roof": ("^", DIM),
            "Ruin": ("R", DIM), "Relic": ("R", DIM),
            "FoodFactory": ("F", CYN),
            "Slope": ("/", DIM),
            "AncientAquiferDrill": ("A", BBLU),
            "Shrub": ("B", MAG), "Geothermal": ("G", RED),
            # water
            "CompactWaterWheel": ("P", BBLU), "LargeWaterWheel": ("P", BBLU),
            "BadwaterDischarge": ("V", BBLU), "Centrifuge": ("V", BBLU),
            "Valve": ("X", CYN), "FillValve": ("X", CYN),
            "AquiferDrill": ("A", BBLU), "IrrigationBarrier": ("X", CYN),
            # power
            "SteamEngine": ("E", BYEL), "GravityBattery": ("E", BYEL),
            "Clutch": ("E", BYEL),
            # production
            "CoffeeBrewery": ("F", CYN), "OilPress": ("F", CYN),
            "Fermenter": ("F", CYN), "TappersShack": ("F", CYN),
            "ExplosivesFactory": ("F", CYN), "HydroponicGarden": ("F", CYN),
            "EfficientMine": ("F", CYN), "GreaseFactory": ("F", CYN),
            "WoodWorkshop": ("M", BWHT),
            # amenities
            "Detailer": ("~", GRN), "MudBath": ("~", GRN),
            "WindTunnel": ("~", GRN), "Motivatorium": ("~", GRN),
            "TeethGrindstone": ("~", GRN), "DecontaminationPod": ("~", GRN),
            # decorations
            "BeaverStatue": ("*", YEL), "Bell": ("*", YEL),
            "DecorativeClock": ("*", YEL), "MetalFence": ("|", DIM),
            "WoodFence": ("|", DIM), "PoleBanner": ("!", YEL),
            "SquareBanner": ("!", YEL), "FireworkLauncher": ("!", YEL),
            "StreamGauge": ("*", DIM),
            # infrastructure
            "Gate": ("=", YEL), "Tunnel": ("=", YEL),
            "DistrictCrossing": ("=", YEL),
            "Tubeway": ("=", BMAG), "TubewayStation": ("=", BMAG),
            "VerticalTubeway": ("=", BMAG),
            "SuspensionBridge": ("=", YEL), "Overhang": ("_", DIM),
            "ImpermeableFloor": ("_", DIM), "TerrainBlock": ("#", DIM),
            "DirtExcavator": ("#", DIM),
            # automation
            "Lever": ("i", DIM), "Sensor": ("i", DIM), "Timer": ("i", DIM),
            "Memory": ("i", DIM), "Relay": ("i", DIM), "Indicator": ("i", DIM),
            "Speaker": ("i", DIM), "HttpAdapter": ("i", DIM), "HttpLever": ("i", DIM),
            "Chronometer": ("i", DIM), "Counter": ("i", DIM),
            "WeatherStation": ("i", DIM), "PowerMeter": ("i", DIM),
            # wonders
            "LaborerMonument": ("Q", BYEL), "FlameOfUnity": ("Q", BYEL),
            "TributeToIngenuity": ("Q", BYEL), "EarthRepopulator": ("Q", BYEL),
            # explosives
            "Dynamite": ("x", RED), "DoubleDynamite": ("x", RED),
            "TripleDynamite": ("x", RED), "Detonator": ("x", RED),
            # misc
            "BuildersHut": ("K", RED), "ControlTower": ("b", BMAG),
            "Numbercruncher": ("S", BWHT),
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

        data = self._get_json("/api/tiles", {"x1": x1, "y1": y1, "x2": x2, "y2": y2})
        tiles = {(t["x"], t["y"]): t for t in data.get("tiles", [])}
        legend = {}
        z_levels = set()

        lines = []
        for ty in range(y2, y1 - 1, -1):
            row = f"{DIM}{ty:3d}{R} "
            pbg = pco = ""
            for tx in range(x1, x2 + 1):
                t = tiles.get((tx, ty))
                if not t:
                    if pbg or pco:
                        row += R
                        pbg = pco = ""
                    row += f"{DIM}?{R}"
                    continue
                occ = t.get("occupants")
                occupant = max(occ, key=lambda o: o["z"])["name"] if occ else None
                entrance = t.get("entrance", False)
                bg = co = ch = None
                if entrance and not occupant:
                    bg = _zbg(t["terrain"])
                    z_levels.add(t["terrain"])
                    co = BWHT
                    ch = "@"
                    legend["@"] = (BWHT, "entrance")
                elif occupant:
                    oname = occupant
                    bg = _zbg(t["terrain"])
                    z_levels.add(t["terrain"])
                    for key, (c, s) in STYLE.items():
                        if key.lower() in oname.lower():
                            ch, co = c, s
                            legend[c] = (s, key)
                            break
                    if ch == "T" and t.get("seedling"):
                        ch, co = "t", DIM + GRN
                        legend["t"] = (co, "seedling")
                    if not ch:
                        ch = oname[0]
                        co = DIM
                        legend[ch] = (DIM, oname)
                elif t.get("water", 0) > 0:
                    bg = _zbg(t["terrain"])
                    z_levels.add(t["terrain"])
                    co = BLU
                    ch = "~"
                    legend["~"] = (BLU, "water")
                elif t["terrain"] > 0:
                    bg = _zbg(t["terrain"])
                    z_levels.add(t["terrain"])
                    ch = str(t["terrain"] % 10)
                    co = GRN if t.get("moist") else DIM
                else:
                    if pbg or pco:
                        row += R
                        pbg = pco = ""
                    row += " "
                    continue
                delta = ""
                if bg != pbg:
                    delta += bg
                if co != pco:
                    delta += co
                row += delta + ch
                pbg = bg
                pco = co
            if pbg or pco:
                row += R
            lines.append(row)

        axis = f"    {DIM}" + "".join(str(i % 10) for i in range(x1, x2 + 1)) + R
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
        result = {"rendered": True, "tiles": len(tiles)}
        if name:
            os.makedirs(_MEMORY_DIR, exist_ok=True)
            fname = f"map-{name}-{x1}x{y1}y-{x2}x{y2}y.txt"
            fpath = os.path.join(_MEMORY_DIR, fname)
            with open(fpath, "w") as f:
                f.write("\n".join(lines) + "\n")
            # update brain.toon maps index
            _update_brain_maps(name, x1, y1, x2, y2, fname)
            print(f"saved: {fpath}", file=sys.stderr)
            result["saved"] = fpath
        return result

    # ------------------------------------------------------------------
    # Spatial memory
    # ------------------------------------------------------------------

    def brain(self, goal=None):
        """Live summary + persistent goal/tasks/maps. Summary is never persisted (always stale)."""
        global _MEMORY_DIR

        summary = self._get_json("/api/summary")

        # set per-settlement memory dir
        settlement = _sanitize_name(summary.get("settlement", summary.get("settlementName", "unknown")) if isinstance(summary, dict) else "unknown")
        _MEMORY_DIR = os.path.join(_MEMORY_BASE, settlement)

        # load persistent data
        existing_goal = ""
        tasks = []
        maps = {}
        bpath = os.path.join(_MEMORY_DIR, "brain.toon")
        if os.path.exists(bpath):
            try:
                import toons as _t
                with open(bpath) as f:
                    old = _t.load(f)
                    existing_goal = old.get("goal", "")
                    tasks = old.get("tasks", [])
                    maps = old.get("maps", {})
            except Exception:
                pass

        # goal: new param overwrites, otherwise keep existing
        current_goal = goal if goal else existing_goal

        # persist brain.toon with consistent schema
        import toons as _t
        os.makedirs(_MEMORY_DIR, exist_ok=True)
        from datetime import datetime
        brain_data = {"timestamp": datetime.now().isoformat(), "goal": current_goal, "tasks": tasks, "maps": maps}
        with open(bpath, "w") as f:
            _t.dump(brain_data, f)

        # auto-map DC area on first run
        districts = summary.get("districts", []) if isinstance(summary, dict) else []
        dc = next((d.get("dc") for d in districts if d.get("dc")), None)
        if dc and not maps:
            self.map(dc["x"] - 20, dc["y"] - 20, dc["x"] + 20, dc["y"] + 20, name="districtcenter")
            with open(bpath) as f:
                maps = _t.load(f).get("maps", {})

        return {"summary": summary, "goal": current_goal, "tasks": tasks, "maps": maps}

    def list_maps(self):
        """List saved map files in memory/."""
        self._ensure_settlement_dir()
        if not os.path.isdir(_MEMORY_DIR):
            return []
        return sorted(f for f in os.listdir(_MEMORY_DIR) if f.startswith("map-") and f.endswith(".txt"))

    def clear_brain(self):
        """Wipe memory for current settlement. Run brain again to start fresh."""
        self._ensure_settlement_dir()
        import shutil
        if os.path.isdir(_MEMORY_DIR) and _MEMORY_DIR != _MEMORY_BASE:
            shutil.rmtree(_MEMORY_DIR)
            return {"cleared": _MEMORY_DIR}
        return {"error": "no settlement memory to clear"}

    # ------------------------------------------------------------------
    # Tasks
    # ------------------------------------------------------------------

    def _ensure_settlement_dir(self):
        """Set _MEMORY_DIR to the correct settlement folder. Call before any disk operation."""
        global _MEMORY_DIR
        if _MEMORY_DIR != _MEMORY_BASE:
            return  # already set by brain()
        try:
            r = self.s.get(f"{self.url}/api/settlement", timeout=5)
            name = _sanitize_name(r.json().get("name", "unknown"))
            _MEMORY_DIR = os.path.join(_MEMORY_BASE, name)
        except Exception:
            pass

    def add_task(self, action):
        """Add a pending task to brain.toon. Returns the new task."""
        self._ensure_settlement_dir()
        brain = _load_brain_file()
        tasks = brain.get("tasks", [])
        next_id = max((t["id"] for t in tasks), default=0) + 1
        task = {"id": next_id, "status": "pending", "action": action}
        tasks.append(task)
        brain["tasks"] = tasks
        _save_brain_file(brain)
        return task

    def update_task(self, id, status, error=None):
        """Update task status. status: pending/active/done/failed. Optional error for failed."""
        self._ensure_settlement_dir()
        brain = _load_brain_file()
        tasks = brain.get("tasks", [])
        for t in tasks:
            if t["id"] == id:
                t["status"] = status
                if error:
                    t["error"] = error
                elif "error" in t and status != "failed":
                    del t["error"]
                brain["tasks"] = tasks
                _save_brain_file(brain)
                return t
        return {"error": f"task {id} not found"}

    def list_tasks(self):
        """List all tasks from brain.toon."""
        self._ensure_settlement_dir()
        brain = _load_brain_file()
        return brain.get("tasks", [])

    def clear_tasks(self, status="done"):
        """Remove tasks with given status (default: done). Returns count cleared."""
        self._ensure_settlement_dir()
        brain = _load_brain_file()
        tasks = brain.get("tasks", [])
        before = len(tasks)
        brain["tasks"] = [t for t in tasks if t["status"] != status]
        _save_brain_file(brain)
        return {"cleared": before - len(brain["tasks"]), "remaining": len(brain["tasks"])}

    def find(self, source, name=None, x=None, y=None, radius=20, limit=0):
        """Find entities from a source (buildings/trees/gatherables/beavers). Filters server-side."""
        params = {"limit": limit}
        if name:
            params["name"] = name
        if x is not None and y is not None:
            params["x"] = x
            params["y"] = y
            params["radius"] = radius
        return self._get(f"/api/{source}", params=params)


# ---------------------------------------------------------------------------
# Live dashboard (top subcommand)
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

W = 86  # total width

# ensure UTF-8 output on Windows
import sys as _sys
if _sys.stdout.encoding != 'utf-8':
    _sys.stdout.reconfigure(encoding='utf-8')


def _cv(val, warn, crit, fmt=".0f"):
    """color a value: green/yellow/red based on thresholds"""
    c = _BRED if val < crit else _BYEL if val < warn else _BGRN
    return f"{c}{_BOLD}{val:{fmt}}{_RST}"


def _bar(cur, mx, w=12):
    """progress bar with gradient: ████░░░░"""
    if mx <= 0:
        return f"{_DIM}{'░' * w}{_RST}"
    ratio = max(0.0, min(cur / mx, 1.0))
    filled = int(ratio * w)
    c = _BRED if ratio < 0.25 else _BYEL if ratio < 0.5 else _BGRN
    return f"{c}{'█' * filled}{_DIM}{'░' * (w - filled)}{_RST}"


def _hline():
    return f" {_DIM}{'─' * W}{_RST}"


def _row(left, right=None, split=43):
    """row with optional two-column layout. No side borders."""
    import re
    if right is None:
        return f"  {left}"
    else:
        plain_l = re.sub(r'\033\[[0-9;]*m', '', left)
        pad_l = max(0, split - len(plain_l))
        return f"  {left}{' ' * pad_l}  {right}"


def _top_render(summary, wellbeing_data=None, trees_data=None, crops_data=None, interval=5):
    if not summary:
        print(f"\n {_RED}-- game not reachable --{_RST}\n")
        return

    t = summary.get("time", {})
    w = summary.get("weather", {})
    day = t.get("dayNumber", 0)
    hazardous = w.get("isHazardous", False)
    temp_len = w.get("temperateWeatherDuration", 0)
    haz_len = w.get("hazardousWeatherDuration", 0)
    cday = w.get("cycleDay", 0)
    remaining = temp_len + haz_len - cday + 1 if hazardous else temp_len - cday + 1

    day_progress = t.get("dayProgress", 0)
    season_str = f"{_BRED}{_BOLD}DROUGHT{_RST}" if hazardous else f"{_BGRN}Temperate{_RST}"
    day_bar = _bar(day_progress, 1.0, 8)
    day_str = f"Day {_BCYN}{_BOLD}{day}{_RST} {day_bar}  {season_str} {_DIM}{cday}/{temp_len}+{haz_len}{_RST} ({_BOLD}{remaining}d{_RST})"

    # header
    print(f" {_DIM}{'─' * W}{_RST}")
    print(_row(f"{_BCYN}{_BOLD}Timberbot API{_RST}                            {day_str}"))
    print(_hline())

    # population
    districts = summary.get("districts", [])
    total_adults = sum(d.get("population", {}).get("adults", 0) for d in districts)
    total_children = sum(d.get("population", {}).get("children", 0) for d in districts)
    total_bots = sum(d.get("population", {}).get("bots", 0) for d in districts)
    total_pop = total_adults + total_children + total_bots

    resources = {}
    for d in districts:
        for good, val in d.get("resources", {}).items():
            amt = val.get("available", val) if isinstance(val, dict) else val
            resources[good] = resources.get(good, 0) + amt

    # aggregate housing/employment from per-district data
    occ_beds = sum(d.get("housing", {}).get("occupiedBeds", 0) for d in districts)
    tot_beds = sum(d.get("housing", {}).get("totalBeds", 0) for d in districts)
    assigned = sum(d.get("employment", {}).get("assigned", 0) for d in districts)
    vacancies = sum(d.get("employment", {}).get("vacancies", 0) for d in districts)
    unemployed = sum(d.get("employment", {}).get("unemployed", 0) for d in districts)
    wb_obj = summary.get("wellbeing", {})
    wb_avg = wb_obj.get("average", 0) if isinstance(wb_obj, dict) else 0
    critical = wb_obj.get("critical", 0) if isinstance(wb_obj, dict) else 0

    pop_parts = f"{_BOLD}{total_adults}{_RST} adults  {_BOLD}{total_children}{_RST} children"
    if total_bots:
        pop_parts += f"  {_BOLD}{total_bots}{_RST} bots"

    homeless = sum(d.get("housing", {}).get("homeless", 0) for d in districts)
    miserable = wb_obj.get("miserable", 0) if isinstance(wb_obj, dict) else 0
    science = summary.get("science", 0)
    idle_c = _BRED if unemployed == 0 else _BGRN if unemployed <= 4 else _BYEL
    crit_str = f"  {_BRED}{_BOLD}● {critical} critical{_RST}" if critical > 0 else ""
    homeless_str = f"  {_BRED}{_BOLD}{homeless} homeless{_RST}" if homeless > 0 else ""
    miserable_str = f"  {_BYEL}{miserable} miserable{_RST}" if miserable > 0 else ""

    print(_row(f"{_BCYN}{_BOLD}{total_pop}{_RST} beavers  {_DIM}({pop_parts}{_DIM}){_RST}", f"Beds {_BOLD}{occ_beds}{_RST}/{tot_beds}  Workers {_BOLD}{assigned}{_RST}/{vacancies}  Idle {idle_c}{_BOLD}{unemployed}{_RST}"))
    print(_row(f"Wellbeing {_bar(wb_avg, 77, 20)} {_cv(wb_avg, 8, 4, '.1f')}/77{crit_str}{miserable_str}{homeless_str}"))
    print(_hline())

    # food + water (left) | wellbeing categories (right)
    _EDIBLE = ["Berries", "Kohlrabi", "Bread", "Carrot", "CornRation", "AlgaeRation",
                "EggplantRation", "FermentedSoybean", "FermentedMushroom", "FermentedCassava",
                "Coffee", "MangroveFruit"]
    _RAW_CROPS = ["Soybean", "Corn", "Sunflower", "Eggplant", "Algae", "Cassava", "Mushroom"]
    total_food = sum(resources.get(g, 0) for g in _EDIBLE)
    total_raw = sum(resources.get(g, 0) for g in _RAW_CROPS)
    total_water = resources.get("Water", 0)
    food_days = round(total_food / total_pop, 1) if total_pop > 0 else 0
    water_days = round(total_water / (total_pop * 2), 1) if total_pop > 0 else 0

    food_items = [(g, resources.get(g, 0)) for g in _EDIBLE if resources.get(g, 0) > 0]
    raw_crops = [(g, resources.get(g, 0)) for g in _RAW_CROPS if resources.get(g, 0) > 0]

    wb_cats = []
    # prefer categories from summary (avoids extra API call), fall back to separate wellbeing_data
    wb_source = wb_obj.get("categories", []) if isinstance(wb_obj, dict) and "categories" in wb_obj else (
        wellbeing_data.get("categories", []) if wellbeing_data and isinstance(wellbeing_data, dict) else [])
    for cat in wb_source:
        wb_cats.append((cat.get("group", "?"), cat.get("current", 0), cat.get("max", 0)))

    # food header
    left_lines = [f"{_BCYN}{_BOLD}FOOD{_RST}  {_cv(food_days, 3, 1, '.1f')} days  {_DIM}({total_food} total){_RST}"]
    for i, (g, amt) in enumerate(food_items):
        branch = "└─" if i == len(food_items) - 1 else "├─"
        left_lines.append(f"  {_DIM}{branch}{_RST} {g:16s} {_BOLD}{amt:>5}{_RST}")

    left_lines.append(f"{_BCYN}{_BOLD}WATER{_RST} {_cv(water_days, 2, 0.5, '.1f')} days  {_BBLU}{_BOLD}{total_water}{_RST}")
    left_lines.append("")

    right_lines = [f"{_BCYN}{_BOLD}WELLBEING{_RST}"]
    for g, cur, mx in wb_cats:
        right_lines.append(f"{g:13s} {_bar(cur, mx, 10)} {_cv(cur, mx * 0.5, mx * 0.1, '.1f')}{_DIM}/{mx:.0f}{_RST}")

    max_rows = max(len(left_lines), len(right_lines))
    for i in range(max_rows):
        l = left_lines[i] if i < len(left_lines) else ""
        r = right_lines[i] if i < len(right_lines) else ""
        print(_row(l, r))

    print(_hline())

    # materials (left) | alerts + projections (right)
    mat_lines = [f"{_BCYN}{_BOLD}MATERIALS{_RST}"]
    for good in ["Log", "Plank", "Gear", "ScrapMetal", "MetalPart"]:
        if good in resources:
            mat_lines.append(f"  {good:16s} {_BOLD}{resources[good]:>5}{_RST}")
    mat_lines.append(f"  {'Science':16s} {_BCYN}{_BOLD}{science:>5}{_RST}")

    alerts_obj = summary.get("alerts", {})
    alert_lines = [f"{_BCYN}{_BOLD}ALERTS{_RST}"]
    if isinstance(alerts_obj, dict):
        for k, v in alerts_obj.items():
            if v > 0:
                alert_lines.append(f"  {_BYEL}⚠ {v} {k}{_RST}")
    if len(alert_lines) == 1:
        alert_lines.append(f"  {_BGRN}● all clear{_RST}")

    max_rows = max(len(mat_lines), len(alert_lines))
    for i in range(max_rows):
        l = mat_lines[i] if i < len(mat_lines) else ""
        r = alert_lines[i] if i < len(alert_lines) else ""
        print(_row(l, r))

    # trees section -- prefer per-species from summary, fall back to full trees_data
    trees_obj = summary.get("trees", {})
    tree_species = trees_obj.get("species", []) if isinstance(trees_obj, dict) else []
    if tree_species:
        tree_counts = {}
        for s in tree_species:
            n = s.get("name", "")
            tree_counts[n] = {"marked_grown": s.get("markedGrown", 0), "unmarked_grown": s.get("unmarkedGrown", 0), "seedling": s.get("seedling", 0)}
    elif trees_data and isinstance(trees_data, list):
        tree_counts = {}
        for t in trees_data:
            n = t.get("name", "")
            if n not in tree_counts:
                tree_counts[n] = {"marked_grown": 0, "unmarked_grown": 0, "seedling": 0}
            if not t.get("alive"):
                continue
            if t.get("grown"):
                if t.get("marked"):
                    tree_counts[n]["marked_grown"] += 1
                else:
                    tree_counts[n]["unmarked_grown"] += 1
            elif t.get("marked"):
                tree_counts[n]["seedling"] += 1
    else:
        tree_counts = {}
    if tree_counts:
        print(_hline())
        tree_left = [f"{_BCYN}{_BOLD}TREES{_RST}"]
        tree_right = []
        total_chop = sum(c["marked_grown"] for c in tree_counts.values())
        total_unmarked = sum(c["unmarked_grown"] for c in tree_counts.values())
        total_seed = sum(c["seedling"] for c in tree_counts.values())
        tree_left.append(f"  {_BGRN}{_BOLD}{total_chop}{_RST} choppable  {_DIM}{total_unmarked} unmarked  {total_seed} seedlings{_RST}")
        for name in sorted(tree_counts, key=lambda n: tree_counts[n]["marked_grown"], reverse=True):
            c = tree_counts[name]
            if c["marked_grown"] + c["unmarked_grown"] + c["seedling"] > 0:
                tree_left.append(f"  {_DIM}{name:10s}{_RST} {_BGRN}{_BOLD}{c['marked_grown']:>4}{_RST} marked  {_DIM}{c['unmarked_grown']} free  {c['seedling']} growing{_RST}")
        for i in range(len(tree_left)):
            l = tree_left[i] if i < len(tree_left) else ""
            r = tree_right[i] if i < len(tree_right) else ""
            print(_row(l, r))

    # crops section -- prefer per-species from summary, fall back to full crops_data
    crops_obj = summary.get("crops", {})
    crop_species = crops_obj.get("species", []) if isinstance(crops_obj, dict) else []
    if crop_species:
        crop_counts = {}
        for s in crop_species:
            n = s.get("name", "")
            crop_counts[n] = {"alive": s.get("ready", 0) + s.get("growing", 0), "grown": s.get("ready", 0)}
    elif crops_data and isinstance(crops_data, list):
        crop_counts = {}
        for t in crops_data:
            name = t.get("name", "")
            if name not in crop_counts:
                crop_counts[name] = {"alive": 0, "grown": 0}
            if t.get("alive"):
                crop_counts[name]["alive"] += 1
            if t.get("grown"):
                crop_counts[name]["grown"] += 1
    else:
        crop_counts = {}
    if crop_counts:
        print(_hline())
        crop_left = [f"{_BCYN}{_BOLD}CROPS{_RST}  {_DIM}(in ground){_RST}"]
        crop_right = []
        items = sorted(crop_counts.items(), key=lambda x: x[1]["alive"], reverse=True)
        for name, c in items:
            grown_c = _BGRN if c["grown"] > 0 else _DIM
            crop_left.append(f"  {name:14s} {grown_c}{_BOLD}{c['grown']:>4}{_RST} ready  {_DIM}{c['alive'] - c['grown']} growing{_RST}")
        for i in range(len(crop_left)):
            l = crop_left[i] if i < len(crop_left) else ""
            r = crop_right[i] if i < len(crop_right) else ""
            print(_row(l, r))

    # districts
    if len(districts) > 0:
        print(_hline())
        print(_row(f"{_BCYN}{_BOLD}DISTRICTS{_RST}"))
        for d in districts:
            name = d.get("name", "?")
            pop = d.get("population", {})
            dpop = pop.get("adults", 0) + pop.get("children", 0) + pop.get("bots", 0)
            dres = d.get("resources", {})
            dwater = dres.get("Water", 0)
            dw = dwater.get("available", 0) if isinstance(dwater, dict) else dwater
            dlog = dres.get("Log", 0)
            dl = dlog.get("available", 0) if isinstance(dlog, dict) else dlog
            print(_row(f"  {name:16s} {_BOLD}{dpop:>3}{_RST} pop   Water {_BBLU}{_BOLD}{dw:>4}{_RST}   Log {_BOLD}{dl:>4}{_RST}"))

    print(f" {_DIM}{'─' * W}{_RST}")
    print(f"{'':30s}{_DIM}refreshing every {interval}s  ·  ctrl+c to stop{_RST}")


def _top(interval=5):
    bot = Timberbot(json_mode=True)

    if not bot.ping():
        print(f"  {_RED}cannot reach Timberbot on port 8085{_RST}")
        print(f"  {_DIM}start Timberborn with the mod loaded{_RST}\n")
        sys.exit(1)

    try:
        while True:
            try:
                summary = bot.summary()
            except Exception:
                summary = None
            print("\033[2J\033[H", end="")
            print()
            _top_render(summary, interval=interval)
            time.sleep(interval)
    except KeyboardInterrupt:
        print(f"\n  {_DIM}bye!{_RST}\n")


# Workforce manager (manage subcommand)
# ---------------------------------------------------------------------------

_ESSENTIAL = {"FarmHouse", "DeepWaterPump", "LumberjackFlag", "ScavengerFlag",
              "GathererFlag", "BreedingPod", "SmallTank", "MediumTank", "LargeTank"}
_LOW_PRIORITY = ["Inventor", "Metalsmith", "BotPartFactory", "BotAssembler",
                 "GearWorkshop", "Scratcher", "FluidDump", "Forester",
                 "IndustrialLumberMill", "LargePowerWheel", "DistrictCenter"]


def _is_essential(name):
    return any(e in name for e in _ESSENTIAL)


def _manage():
    bot = Timberbot(json_mode=True)

    if not bot.ping():
        print(f"  {_RED}cannot reach Timberbot on port 8085{_RST}")
        sys.exit(1)

    print(f"  {_BOLD}{_BMAG}timberbot manage{_RST}  {_DIM}keeping 1-4 idle haulers -- ctrl+c to stop{_RST}\n")

    # track what we paused so we unpause in reverse order
    paused_by_us = []

    try:
        while True:
            try:
                summary = bot.summary()
                idle = sum(d.get("employment", {}).get("unemployed", 0) for d in summary.get("districts", []))
                buildings = bot.buildings()
            except Exception:
                print(f"  {_RED}-- connection lost --{_RST}")
                time.sleep(10)
                continue

            idle_color = _BRED if idle == 0 else _BGRN if idle <= 4 else _BYEL
            ts = time.strftime("%H:%M:%S")

            if idle == 0:
                # find something to pause from low-priority list
                acted = False
                for prio_name in _LOW_PRIORITY:
                    for b in buildings:
                        if (prio_name in b.get("name", "") and
                                not b.get("paused") and
                                b.get("assignedWorkers", 0) > 0 and
                                not _is_essential(b.get("name", ""))):
                            bot.pause_building(b["id"])
                            paused_by_us.append(b["id"])
                            print(f"  {ts}  {_BRED}0 idle{_RST}  paused {_BYEL}{b['name']}{_RST} id:{b['id']}")
                            acted = True
                            break
                    if acted:
                        break
                if not acted:
                    print(f"  {ts}  {_BRED}0 idle{_RST}  {_DIM}nothing left to pause{_RST}")

            elif idle > 4 and paused_by_us:
                # unpause the last thing we paused
                bid = paused_by_us.pop()
                name = "?"
                for b in buildings:
                    if b.get("id") == bid:
                        name = b.get("name", "?")
                        break
                bot.unpause_building(bid)
                print(f"  {ts}  {_BYEL}{idle} idle{_RST}  unpaused {_BGRN}{name}{_RST} id:{bid}")

            else:
                print(f"  {ts}  {idle_color}{idle} idle{_RST}  {_DIM}ok{_RST}")

            time.sleep(10)
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


_SAVES_DIR = os.path.join(os.path.expanduser("~"), "Documents", "Timberborn", "Saves")


def _launch(args):
    """Launch Timberborn and auto-load a save.

    Usage: timberbot.py launch settlement:<name> [save:<filename>] [timeout:120]
    """
    settlement = None
    save_name = None
    timeout = 120

    for a in args:
        if ":" in a:
            key, val = a.split(":", 1)
            if key == "settlement":
                settlement = val
            elif key == "save":
                save_name = val
            elif key == "timeout":
                try:
                    timeout = int(val)
                except ValueError:
                    pass

    if not settlement:
        print(f"  {_RED}error: settlement:<name> is required{_RST}", file=sys.stderr)
        print("  usage: timberbot.py launch settlement:<name> [save:<filename>] [timeout:120]", file=sys.stderr)
        sys.exit(1)

    # validate settlement exists
    sdir = os.path.join(_SAVES_DIR, settlement)
    if not os.path.isdir(sdir):
        print(f"  {_RED}error: settlement folder not found: {sdir}{_RST}", file=sys.stderr)
        avail = [d for d in os.listdir(_SAVES_DIR)
                 if os.path.isdir(os.path.join(_SAVES_DIR, d))]
        if avail:
            print(f"  available: {', '.join(sorted(avail))}", file=sys.stderr)
        sys.exit(1)

    # validate or pick save
    if save_name:
        # strip .timber extension if provided
        if save_name.endswith(".timber"):
            save_name = save_name[:-7]
        spath = os.path.join(sdir, save_name + ".timber")
        if not os.path.isfile(spath):
            print(f"  {_RED}error: save not found: {spath}{_RST}", file=sys.stderr)
            sys.exit(1)
    else:
        # pick most recent .timber file
        timbers = [f for f in os.listdir(sdir) if f.endswith(".timber")]
        if not timbers:
            print(f"  {_RED}error: no saves in {sdir}{_RST}", file=sys.stderr)
            sys.exit(1)
        timbers.sort(key=lambda f: os.path.getmtime(os.path.join(sdir, f)), reverse=True)
        save_name = timbers[0][:-7]  # strip .timber

    # write autoload.json for the mod to pick up (avoids Steam CLI arg dialog)
    mod_dir = os.path.join(os.path.expanduser("~"), "Documents", "Timberborn", "Mods", "Timberbot")
    autoload = {"settlement": settlement, "save": save_name}
    with open(os.path.join(mod_dir, "autoload.json"), "w") as f:
        json.dump(autoload, f)

    # kill existing Timberborn process if running
    try:
        subprocess.run(["taskkill", "/f", "/im", "Timberborn.exe"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(2)  # wait for process to fully exit
    except Exception:
        pass

    # launch via Steam protocol (no CLI args = no Steam dialog)
    print(f"  {_BOLD}launching{_RST} {settlement} / {save_name}")
    os.startfile("steam://rungameid/1062090")

    # poll until the mod's HTTP API responds
    print(f"  {_DIM}waiting for game to load (timeout {timeout}s)...{_RST}")
    start = time.time()
    bot = Timberbot(json_mode=True)
    while time.time() - start < timeout:
        try:
            s = bot.summary()
            name = ""
            for d in s.get("districts", []):
                if d.get("name"):
                    name = d["name"]
                    break
            print(f"  {_BGRN}ready{_RST}  settlement: {name or settlement}")
            return
        except Exception:
            time.sleep(3)

    print(f"  {_RED}timeout after {timeout}s -- game may still be loading{_RST}", file=sys.stderr)
    sys.exit(1)


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
        print("usage: timberbot.py <method> key:value ...")
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
        print(f"\n  {'top':30s} live colony dashboard")
        print(f"  {'manager':30s} auto-manage haulers (keep 1-4 idle)")
        print(f"  {'launch':30s} launch game and auto-load a save")
        sys.exit(1)

    json_mode = "--json" in sys.argv
    host_override = None
    port_override = None
    for a in sys.argv[1:]:
        if a.startswith("--host="):
            host_override = a.split("=", 1)[1]
        elif a.startswith("--port="):
            try: port_override = int(a.split("=", 1)[1])
            except ValueError: pass
    skip = {"--", "--json"}
    raw_args = [a for a in sys.argv[1:] if a not in skip and not a.startswith("--host=") and not a.startswith("--port=")]
    method_name = raw_args[0]
    args = raw_args[1:]

    if method_name == "top":
        # parse optional interval: top interval:3
        interval = 5
        for a in args:
            if a.startswith("interval:"):
                try: interval = int(a.split(":", 1)[1])
                except ValueError: pass
        _top(interval)
        return

    if method_name == "manager":
        _manage()
        return

    if method_name == "launch":
        _launch(args)
        return

    bot = Timberbot(host=host_override, port=port_override, json_mode=json_mode)

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

    try:
        result = method(**kwargs)
    except TimberbotError as e:
        print(json.dumps(e.response, indent=2) if json_mode else e.error, file=sys.stderr)
        sys.exit(1)
    if isinstance(result, str):
        print(result)
    elif isinstance(result, dict) and result.get("rendered"):
        pass  # map() already printed to terminal
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
