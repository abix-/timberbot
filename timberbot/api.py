"""Timberbot API client for controlling Timberborn.

Usage:
    from timberborn.api import Timberbot
    bot = Timberbot()

Read state (noun methods):
    bot.summary()          -> {time, weather, districts}
    bot.time()             -> {dayNumber, dayProgress, partialDayNumber}
    bot.weather()          -> {cycle, cycleDay, isHazardous, ...}
    bot.population()       -> [{district, adults, children, bots}]
    bot.resources()        -> {districtName: {goodName: {available, all}}}
    bot.districts()        -> [{name, population, resources}]
    bot.buildings()        -> [{id, name, x, y, z, finished, paused, priority, maxWorkers, ...}]
    bot.trees()            -> [{id, name, x, y, z, marked, alive}]
    bot.prefabs()          -> [{name, sizeX, sizeY, sizeZ}]
    bot.speed()            -> {speed}

Write actions (verb_noun methods):
    bot.set_speed(0-3)
    bot.pause_building(id)
    bot.unpause_building(id)
    bot.set_priority(id, "VeryLow"|"Normal"|"VeryHigh")
    bot.set_workers(id, count)
    bot.set_floodgate(id, height)
    bot.place_building(prefab, x, y, z, orientation=0)
    bot.demolish_building(id)
    bot.mark_trees(x1, y1, x2, y2, z)
    bot.clear_trees(x1, y1, x2, y2, z)
    bot.set_capacity(id, capacity)
    bot.set_good(id, good_name)

Vanilla API:
    bot.levers()
    bot.adapters()
    bot.lever_on(name)
    bot.lever_off(name)
"""
import urllib.parse

import requests


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
        r.raise_for_status()
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

    def prefabs(self):
        """Available building templates: [{name, sizeX, sizeY, sizeZ}]."""
        return self._get("/api/prefabs")

    def speed(self):
        """Current game speed: {speed: 0-3}."""
        return self._get("/api/speed")

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
