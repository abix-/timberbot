"""Thin wrapper over the Timberborn HTTP API."""
import urllib.parse

import requests


class TimberbornAPI:
    """Client for vanilla Timberborn HTTP API (port 8080) + Timberbot mod (port 8085)."""

    def __init__(self, game_url="http://localhost:8080", bridge_url="http://localhost:8085"):
        self.game_url = game_url.rstrip("/")
        self.bridge_url = bridge_url.rstrip("/")
        self.session = requests.Session()
        self.session.headers["Accept"] = "application/json"

    def _game(self, path):
        return f"{self.game_url}{path}"

    def _bridge(self, path):
        return f"{self.bridge_url}{path}"

    @staticmethod
    def _encode(name):
        return urllib.parse.quote(name, safe="")

    def _get_game(self, path):
        resp = self.session.get(self._game(path), timeout=5)
        resp.raise_for_status()
        return resp

    def _post_game(self, path):
        resp = self.session.post(self._game(path), timeout=5)
        resp.raise_for_status()
        return resp

    def _get_bridge(self, path):
        resp = self.session.get(self._bridge(path), timeout=5)
        resp.raise_for_status()
        return resp

    def _post_bridge(self, path, data):
        resp = self.session.post(self._bridge(path), json=data, timeout=5)
        resp.raise_for_status()
        return resp

    # -- connection check --

    def ping(self):
        """Return True if the game API is reachable."""
        try:
            self._get_game("/api/levers")
            return True
        except (requests.ConnectionError, requests.Timeout):
            return False

    def ping_bridge(self):
        """Return True if the Timberbot mod is reachable."""
        try:
            data = self._get_bridge("/api/ping").json()
            return data.get("ready", False)
        except (requests.ConnectionError, requests.Timeout):
            return False

    # -- levers (read + write) via vanilla API --

    def get_levers(self):
        """Return list of all levers."""
        return self._get_game("/api/levers").json()

    def get_lever(self, name):
        """Return a single lever by name."""
        return self._get_game(f"/api/levers/{self._encode(name)}").json()

    def switch_on(self, name):
        """Turn a lever ON. Returns True on success."""
        self._post_game(f"/api/switch-on/{self._encode(name)}")
        return True

    def switch_off(self, name):
        """Turn a lever OFF. Returns True on success."""
        self._post_game(f"/api/switch-off/{self._encode(name)}")
        return True

    def set_color(self, name, hex_color):
        """Set lever color (6-char hex, no #). Returns True on success."""
        color = hex_color.lstrip("#")
        self._post_game(f"/api/color/{self._encode(name)}/{color}")
        return True

    # -- adapters (read-only) via vanilla API --

    def get_adapters(self):
        """Return list of all adapters."""
        return self._get_game("/api/adaptors").json()

    def get_adapter(self, name):
        """Return a single adapter by name."""
        return self._get_game(f"/api/adaptors/{self._encode(name)}").json()

    # -- rich game state via Timberbot mod --

    def get_summary(self):
        """Full colony snapshot: time, weather, districts with resources + population."""
        return self._get_bridge("/api/summary").json()

    def get_resources(self):
        """All resource stocks per district."""
        return self._get_bridge("/api/resources").json()

    def get_population(self):
        """Beaver/bot counts per district."""
        return self._get_bridge("/api/population").json()

    def get_time(self):
        """Current game time: day number, progress, cycle."""
        return self._get_bridge("/api/time").json()

    def get_weather(self):
        """Current weather: cycle, cycle day."""
        return self._get_bridge("/api/weather").json()

    def get_districts(self):
        """All districts with resources and population."""
        return self._get_bridge("/api/districts").json()

    def get_buildings(self):
        """All buildings with id, name, coords, pause/floodgate/priority state."""
        return self._get_bridge("/api/buildings").json()

    # -- write endpoints via Timberbot mod --

    def get_speed(self):
        """Current game speed."""
        return self._get_bridge("/api/speed").json()

    def set_speed(self, speed):
        """Set game speed (0=pause, 1/2/3)."""
        return self._post_bridge("/api/speed", {"speed": speed}).json()

    def pause_building(self, building_id, paused=True):
        """Pause or unpause a building by ID."""
        return self._post_bridge("/api/building/pause", {"id": building_id, "paused": paused}).json()

    def set_floodgate_height(self, building_id, height):
        """Set floodgate height by building ID."""
        return self._post_bridge("/api/floodgate", {"id": building_id, "height": height}).json()

    def set_priority(self, building_id, priority):
        """Set building priority (VeryLow, Normal, VeryHigh)."""
        return self._post_bridge("/api/priority", {"id": building_id, "priority": priority}).json()
