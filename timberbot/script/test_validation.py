"""Comprehensive endpoint tests with debug verification.

Tests every API endpoint, then uses the debug endpoint to verify
the game state actually changed. Requires a running game with
the Iron Teeth day-5 save.

Usage:
    python timberbot/script/test_validation.py
"""
import json
import sys
import time

from timberbot import Timberbot


REFRESH_WAIT = 1.5  # seconds to wait after POST for cache refresh (settings.json refreshIntervalSeconds + margin)


class TestRunner:
    def __init__(self):
        self.bot = Timberbot()
        self.passed = 0
        self.failed = 0
        self.skipped = 0

    def wait_for_refresh(self):
        """Wait for the double-buffered cache to refresh after a write."""
        time.sleep(REFRESH_WAIT)

    def write_and_wait(self, fn):
        """Execute a write operation and wait for cache refresh."""
        result = fn()
        self.wait_for_refresh()
        return result

    def check(self, name, ok, detail=""):
        if ok:
            self.passed += 1
            print(f"  PASS  {name}")
        else:
            self.failed += 1
            print(f"  FAIL  {name}")
            if detail:
                print(f"         {detail}")

    def skip(self, name, reason=""):
        self.skipped += 1
        print(f"  SKIP  {name}" + (f" ({reason})" if reason else ""))

    def has(self, result, key):
        """check result dict has key"""
        return isinstance(result, dict) and key in result

    def err(self, result):
        """check result is an error"""
        return isinstance(result, dict) and "error" in result

    def debug_get(self, path):
        """get a value from game internals via debug endpoint"""
        return self.bot.debug(target="get", path=path)

    def debug_call(self, path, method, **kwargs):
        """call a method on a game object via debug endpoint"""
        args = {"target": "call", "path": path, "method": method}
        args.update(kwargs)
        return self.bot.debug(**args)

    def find_spot(self, prefab="Path", x1=100, y1=100, x2=160, y2=160):
        """find a valid placement spot for prefab, return {x,y,z,orientation} or None"""
        result = self.bot.find_placement(prefab, x1, y1, x2, y2)
        placements = result.get("placements", []) if isinstance(result, dict) else []
        return placements[0] if placements else None

    def find_building(self, name):
        """find first building matching name, return id"""
        buildings = self.bot._get("/api/buildings")
        if isinstance(buildings, list):
            for b in buildings:
                if name.lower() in str(b.get("name", "")).lower():
                    return b.get("id")
        return None

    def run(self):
        if not self.bot.ping():
            print("error: game not reachable")
            sys.exit(1)

        self.test_read_endpoints()
        self.test_speed()
        self.test_placement_and_demolish()
        self.test_priority()
        self.test_workers()
        self.test_pause()
        self.test_crops()
        self.test_tree_marking()
        self.test_stockpile()
        self.test_orientation()
        self.test_find_placement()
        self.test_path_routing()
        self.test_overridable_placement()
        self.test_summary_projection()
        self.test_map_moisture()
        self.test_unlock()
        self.test_floodgate()
        self.test_haul_priority()
        self.test_recipe()
        self.test_farmhouse_action()
        self.test_plantable_priority()
        self.test_stockpile_capacity()
        self.test_workhours()
        self.test_distribution()
        self.test_beaver_needs()
        self.test_building_range()
        self.test_find_planting()
        self.test_prefab_costs()
        self.test_building_inventory()
        self.test_building_recipes()
        self.test_clutch()
        self.test_bot_data()
        self.test_bot_buildings()
        self.test_bot_in_summary()
        self.test_bot_toon_format()
        self.test_beaver_detail()
        self.test_building_detail()
        self.test_beaver_position()
        self.test_beaver_district()
        self.test_map_stacking()
        self.test_carried_goods()
        self.test_bot_durability()
        self.test_power_networks()
        self.test_performance()

        summary = f"\n=== {self.passed} passed, {self.failed} failed"
        if self.skipped:
            summary += f", {self.skipped} skipped"
        summary += " ===\n"
        print(summary)
        sys.exit(1 if self.failed else 0)

    def test_read_endpoints(self):
        print("\n=== read endpoints ===\n")

        # each read endpoint should return data without error
        reads = [
            ("ping", lambda: self.bot.ping()),
            ("summary", lambda: self.bot.summary()),
            ("alerts", lambda: self.bot.alerts()),
            ("buildings", lambda: self.bot.buildings()),
            ("trees", lambda: self.bot.trees()),
            ("gatherables", lambda: self.bot.gatherables()),
            ("beavers", lambda: self.bot.beavers()),
            ("resources", lambda: self.bot.resources()),
            ("population", lambda: self.bot.population()),
            ("weather", lambda: self.bot.weather()),
            ("time", lambda: self.bot.time()),
            ("districts", lambda: self.bot.districts()),
            ("distribution", lambda: self.bot.distribution()),
            ("science", lambda: self.bot.science()),
            ("notifications", lambda: self.bot.notifications()),
            ("workhours", lambda: self.bot.workhours()),
            ("speed", lambda: self.bot.speed()),
            ("prefabs", lambda: self.bot.prefabs()),
            ("map", lambda: self.bot.map(110, 130, 115, 135)),
            ("tree_clusters", lambda: self.bot.tree_clusters()),
            ("wellbeing", lambda: self.bot.wellbeing()),
            ("scan", lambda: self.bot.scan(120, 140, 5)),
        ]
        for name, fn in reads:
            result = fn()
            self.check(f"GET {name}", not self.err(result),
                       json.dumps(result)[:100] if self.err(result) else "")

    def test_speed(self):
        print("\n=== speed ===\n")

        # save original
        orig = self.bot.speed()
        orig_speed = orig.get("speed", 1) if isinstance(orig, dict) else 1

        # set to 0 (pause)
        result = self.bot.set_speed(0)
        self.check("set speed 0", not self.err(result))

        # verify via debug
        verify = self.debug_get("_speedManager.CurrentSpeed")
        self.check("verify speed=0 via debug",
                   str(verify.get("value", "")) == "0",
                   f"got: {verify.get('value')}")

        # restore
        self.bot.set_speed(orig_speed)

    def test_placement_and_demolish(self):
        print("\n=== placement + demolish ===\n")

        # find a valid spot dynamically for placement tests
        spot = self.find_spot("Path")
        if not spot:
            self.skip("placement tests", "no valid Path spot found")
            return
        sx, sy, sz = spot["x"], spot["y"], spot["z"]

        # error cases
        tests = [
            ("off map", lambda: self.bot.place_building("Path", 999, 999, 2)),
            ("z too high", lambda: self.bot.place_building("Path", sx, sy, sz + 10)),
            ("z too low", lambda: self.bot.place_building("Path", sx, sy, max(0, sz - 5))),
        ]
        for name, fn in tests:
            result = fn()
            self.check(name, self.err(result),
                       json.dumps(result)[:100])

        # specific error messages
        specific_tests = [
            ("unknown prefab", lambda: self.bot.place_building("Fake", sx, sy, sz), "not found"),
            ("invalid orientation", lambda: self.bot.place_building("Path", sx, sy, sz, orientation="bogus"), "invalid orientation"),
            ("locked building", lambda: self.bot.place_building("TributeToIngenuity.IronTeeth", sx, sy, sz), "not unlocked"),
        ]
        for name, fn, expect_err in specific_tests:
            result = fn()
            self.check(name, self.err(result) and expect_err in str(result["error"]),
                       json.dumps(result)[:100])

        # valid placement using find_spot coords
        result = self.bot.place_building("Path", sx, sy, sz, spot.get("orientation", "south"))
        self.check("valid placement", self.has(result, "id"))

        if self.has(result, "id"):
            placed_id = result["id"]

            # verify via map
            tile = self.bot.map(sx, sy, sx, sy)
            tiles = tile.get("tiles", [])
            has_path = any(t.get("occupant") == "Path" or any("Path" in o.get("name", "") for o in t.get("occupants", [])) for t in tiles)
            self.check("verify placement via map", has_path)

            # demolish
            dem = self.bot.demolish_building(placed_id)
            self.check("demolish", self.has(dem, "demolished") or not self.err(dem))

            # verify gone
            tile2 = self.bot.map(sx, sy, sx, sy)
            tiles2 = tile2.get("tiles", [])
            no_path = not any(t.get("occupant") == "Path" or any("Path" in o.get("name", "") for o in t.get("occupants", [])) for t in tiles2)
            self.check("verify demolish via map", no_path)

        # multi-tile z mismatch: find a spot where terrain changes within a footprint
        found_mismatch = False
        for tx in range(80, 140):
            region = self.bot.map(tx, 135, tx + 2, 136)
            tiles = region.get("tiles", [])
            if len(tiles) < 6:
                continue
            heights = set(t.get("terrain", 0) for t in tiles)
            occupied = any(t.get("occupant") for t in tiles)
            if len(heights) > 1 and not occupied and all(h >= 2 for h in heights):
                z = min(heights)
                result = self.bot.place_building("Barrack.IronTeeth", tx, 135, z)
                self.check("multi-tile z mismatch rejected",
                           self.err(result),
                           json.dumps(result)[:100])
                found_mismatch = True
                break
        if not found_mismatch:
            self.skip("multi-tile z mismatch", "no height transition found")

    def test_priority(self):
        print("\n=== priority ===\n")

        # find the DC (always exists)
        dc_id = None
        buildings = self.bot.buildings()
        if isinstance(buildings, list):
            for b in buildings:
                if "DistrictCenter" in str(b.get("name", "")):
                    dc_id = b.get("id")
                    break
        if not dc_id:
            self.skip("priority tests", "no DC found")
            return

        # set workplace priority
        result = self.bot.set_priority(dc_id, "VeryHigh", type="workplace")
        self.check("set priority VeryHigh",
                   self.has(result, "workplacePriority") and result["workplacePriority"] == "VeryHigh",
                   json.dumps(result)[:100])

        # restore
        self.bot.set_priority(dc_id, "Normal", type="workplace")

    def test_workers(self):
        print("\n=== workers ===\n")

        # find a workplace building
        dc_id = None
        buildings = self.bot.buildings()
        if isinstance(buildings, list):
            for b in buildings:
                if "DistrictCenter" in str(b.get("name", "")):
                    dc_id = b.get("id")
                    break
        if not dc_id:
            self.skip("worker tests", "no DC found")
            return

        # set workers to 1
        result = self.bot.set_workers(dc_id, 1)
        self.check("set workers 1",
                   self.has(result, "desiredWorkers") and result["desiredWorkers"] == 1,
                   json.dumps(result)[:100])

        # restore to 3
        self.bot.set_workers(dc_id, 3)

    def test_pause(self):
        print("\n=== pause/unpause ===\n")

        dc_id = self.find_building("DistrictCenter")
        if not dc_id:
            self.skip("pause tests", "no DC found")
            return

        # pause
        result = self.bot.pause_building(dc_id)
        self.check("pause building", not self.err(result))

        # verify via buildings endpoint (wait for cache refresh)
        self.wait_for_refresh()
        buildings = self.bot.buildings()
        paused = False
        if isinstance(buildings, list):
            for b in buildings:
                if b.get("id") == dc_id:
                    paused = b.get("paused", False)
                    break
        self.check("verify paused", paused)

        # unpause
        self.bot.unpause_building(dc_id)

        # verify unpaused
        self.wait_for_refresh()
        buildings2 = self.bot.buildings()
        unpaused = True
        if isinstance(buildings2, list):
            for b in buildings2:
                if b.get("id") == dc_id:
                    unpaused = not b.get("paused", True)
                    break
        self.check("verify unpaused", unpaused)

    def test_crops(self):
        print("\n=== crops ===\n")

        # find a farmhouse to get valid planting area
        fh = self.find_building("FarmHouse")
        if not fh:
            self.skip("crops", "no FarmHouse found")
            return
        fb = self.bot.buildings(detail=f"id:{fh}")
        if not fb or not isinstance(fb, list) or not fb:
            self.skip("crops", "cannot get farmhouse details")
            return
        b = fb[0]
        bx, by, bz = b.get("x", 0), b.get("y", 0), b.get("z", 0)

        # plant near farmhouse
        result = self.bot.plant_crop(bx - 3, by - 3, bx + 3, by + 3, bz, "Kohlrabi")
        self.check("plant crops", self.has(result, "planted"),
                   json.dumps(result)[:100])

        # clear
        self.bot.clear_planting(bx - 3, by - 3, bx + 3, by + 3, bz)

    def test_tree_marking(self):
        print("\n=== tree marking ===\n")

        # find actual tree coords dynamically
        trees = self.bot.trees()
        alive_trees = [t for t in trees if t.get("alive")] if isinstance(trees, list) else []
        if not alive_trees:
            self.skip("tree marking", "no alive trees")
            return
        t = alive_trees[0]
        tx, ty, tz = t["x"], t["y"], t["z"]

        # mark trees in area around the found tree
        result = self.bot.mark_trees(tx - 3, ty - 3, tx + 3, ty + 3, tz)
        self.check("mark trees", not self.err(result),
                   json.dumps(result)[:100] if self.err(result) else "")

        # verify via trees endpoint - some should be marked
        self.wait_for_refresh()
        trees = self.bot.trees()
        marked = 0
        if isinstance(trees, list):
            marked = sum(1 for t in trees if t.get("marked"))
        self.check("verify trees marked", marked > 0, f"marked count: {marked}")

        # clear
        self.bot.clear_trees(tx - 3, ty - 3, tx + 3, ty + 3, tz)

    def test_stockpile(self):
        print("\n=== stockpile ===\n")

        # find a tank
        tank_id = self.find_building("SmallTank") or self.find_building("Tank")
        if not tank_id:
            self.skip("stockpile tests", "no tank found")
            return

        # set good
        result = self.bot.set_good(tank_id, "Water")
        self.check("set stockpile good", not self.err(result),
                   json.dumps(result)[:100] if self.err(result) else "")

    def test_orientation(self):
        print("\n=== orientation ===\n")

        # find flat test area dynamically
        test_spot = None
        need = 5
        for cy in range(100, 170):
            for cx in range(70, 170):
                region = self.bot.map(cx, cy, cx + need - 1, cy + need - 1)
                tiles = region.get("tiles", [])
                if len(tiles) < need * need:
                    continue
                heights = set(t.get("terrain", 0) for t in tiles)
                if len(heights) != 1:
                    continue
                tz = heights.pop()
                if tz < 2:
                    continue
                occupants = [t for t in tiles if t.get("occupant") or t.get("water", 0) > 0]
                if occupants:
                    continue
                test = self.bot.place_building("Path", cx, cy, tz, orientation="south")
                if "id" in test:
                    self.bot.demolish_building(test["id"])
                    test_spot = (cx, cy, tz)
                    break
            if test_spot:
                break

        if not test_spot:
            self.skip("orientation tests", "no flat 5x5 area")
            return

        bx, by, bz = test_spot
        print(f"  using ({bx},{by},z={bz})\n")

        for prefab, sx, sy in [("FarmHouse.IronTeeth", 2, 2),
                                ("Barrack.IronTeeth", 3, 2),
                                ("IndustrialLumberMill.IronTeeth", 2, 3)]:
            for orient in ["south", "west", "north", "east"]:
                result = self.bot.place_building(prefab, bx, by, bz, orientation=orient)
                if "id" not in result:
                    if "not unlocked" in str(result.get("error", "")):
                        self.skip(f"{prefab.split('.')[0]} {orient}", "not unlocked")
                        continue
                    self.check(f"{prefab.split('.')[0]} {orient}", False, json.dumps(result)[:100])
                    continue

                # wait for cache then verify origin via map
                self.wait_for_refresh()
                region = self.bot.map(bx - 1, by - 1, bx + sx, by + sy)
                pname = prefab.split(".")[0]
                occupied = []
                for t in region.get("tiles", []):
                    occ = t.get("occupant", "")
                    occs = t.get("occupants", [])
                    if (occ and pname in occ) or any(pname in o.get("name", "") for o in occs):
                        occupied.append((t["x"], t["y"]))
                min_x = min(t[0] for t in occupied) if occupied else -1
                min_y = min(t[1] for t in occupied) if occupied else -1
                self.check(f"{prefab.split('.')[0]} {orient} origin=({min_x},{min_y})",
                           min_x == bx and min_y == by,
                           f"expected ({bx},{by})")
                self.bot.demolish_building(result["id"])

    def test_find_placement(self):
        print("\n=== find_placement ===\n")

        result = self.bot.find_placement("Inventor.IronTeeth", 100, 100, 170, 170)
        self.check("returns results",
                   self.has(result, "placements") and len(result.get("placements", [])) > 0)

        placements = result.get("placements", [])
        if not placements:
            return

        # check result fields
        p0 = placements[0]
        for field in ["x", "y", "z", "orientation", "pathAccess", "reachable", "nearPower"]:
            self.check(f"result has {field}", field in p0, f"keys: {list(p0.keys())}")

        # reachable spots
        reachable = [p for p in placements if p.get("reachable")]
        unreachable = [p for p in placements if not p.get("reachable")]
        self.check("has reachable placements", len(reachable) > 0,
                   f"got {len(reachable)} reachable of {len(placements)}")

        # verify reachable spot is actually placeable and connected
        if reachable:
            p = reachable[0]
            self.check("reachable has pathAccess", p.get("pathAccess") == True)

            placed = self.bot.place_building("Inventor.IronTeeth",
                p["x"], p["y"], p["z"], orientation=p["orientation"])
            if self.has(placed, "id"):
                alerts = self.bot.alerts()
                disconnected = False
                if isinstance(alerts, list):
                    disconnected = any(
                        a.get("id") == placed["id"] and "not connected" in str(a.get("status", ""))
                        for a in alerts)
                self.check("reachable spot is connected", not disconnected)
                self.bot.demolish_building(placed["id"])
            else:
                self.check("place at reachable spot", False, json.dumps(placed)[:100])

        # verify unreachable spot is actually disconnected (if we have one with pathAccess)
        unreachable_with_path = [p for p in unreachable if p.get("pathAccess")]
        if unreachable_with_path:
            p = unreachable_with_path[0]
            placed = self.bot.place_building("Inventor.IronTeeth",
                p["x"], p["y"], p["z"], orientation=p["orientation"])
            if self.has(placed, "id"):
                alerts = self.bot.alerts()
                disconnected = any(
                    a.get("id") == placed["id"] and "not connected" in str(a.get("status", ""))
                    for a in alerts) if isinstance(alerts, list) else False
                self.check("unreachable spot IS disconnected", disconnected,
                           f"expected disconnect alert for ({p['x']},{p['y']})")
                self.bot.demolish_building(placed["id"])
            else:
                # placement rejected = also proves unreachable
                self.check("unreachable spot rejected by game", True)
        else:
            self.skip("unreachable verification", "no unreachable+pathAccess spots")

        # verify sort order: reachable before unreachable
        if reachable and unreachable:
            first_unreachable_idx = next((i for i, p in enumerate(placements) if not p.get("reachable")), len(placements))
            last_reachable_idx = max(i for i, p in enumerate(placements) if p.get("reachable"))
            self.check("reachable sorted before unreachable", last_reachable_idx < first_unreachable_idx)

        # nearPower check: find a spot near power and verify
        powered = [p for p in placements if p.get("nearPower")]
        if powered:
            self.check("nearPower spots exist", True)
        else:
            self.skip("nearPower verification", "no powered spots in results")

        # bounds check: no results should have coords outside map
        map_info = self.bot.map()
        if isinstance(map_info, dict) and "mapSize" in map_info:
            mx = map_info["mapSize"]["x"]
            my = map_info["mapSize"]["y"]
            oob = [p for p in placements if p["x"] < 0 or p["x"] >= mx or p["y"] < 0 or p["y"] >= my]
            self.check("no out-of-bounds results", len(oob) == 0,
                       f"found {len(oob)} OOB placements")

        # unknown prefab
        bad = self.bot.find_placement("FakeBuilding", 100, 100, 110, 110)
        self.check("find_placement unknown prefab", self.err(bad))

    def test_path_routing(self):
        print("\n=== path routing ===\n")

        # find a flat open area for path tests dynamically
        test_spot = None
        for cy in range(100, 170):
            for cx in range(70, 170):
                region = self.bot.map(cx, cy, cx + 4, cy)
                tiles = region.get("tiles", [])
                if len(tiles) < 5:
                    continue
                heights = set(t.get("terrain", 0) for t in tiles)
                if len(heights) != 1:
                    continue
                tz = heights.pop()
                if tz < 2:
                    continue
                occupants = [t for t in tiles if t.get("occupant") or t.get("water", 0) > 0]
                if occupants:
                    continue
                test_spot = (cx, cy, tz)
                break
            if test_spot:
                break

        if not test_spot:
            self.skip("path routing tests", "no flat open area")
            return

        sx, sy, sz = test_spot

        # flat path
        result = self.bot.place_path(sx, sy, sx + 3, sy)
        self.check("flat path placement",
                   self.has(result, "placed") and result["placed"] > 0,
                   json.dumps(result)[:100])

        # verify tiles are occupied (wait for cache refresh)
        self.wait_for_refresh()
        region = self.bot.map(sx, sy, sx + 3, sy)
        paths = [t for t in region.get("tiles", []) if t.get("occupant") == "Path"]
        self.check("verify paths on map", len(paths) >= 3, f"found {len(paths)} paths")

        # cleanup: demolish placed paths
        for t in paths:
            buildings = self.bot._get("/api/buildings")
            if isinstance(buildings, list):
                for b in buildings:
                    if b.get("x") == t["x"] and b.get("y") == t["y"] and "Path" in str(b.get("name", "")):
                        self.bot.demolish_building(b["id"])
                        break

        # non-straight path rejected
        result2 = self.bot.place_path(100, 100, 105, 105)
        self.check("diagonal path rejected",
                   self.err(result2) and "straight" in str(result2.get("error", "")),
                   json.dumps(result2)[:100])

        # path across z-level change (should auto-place stairs)
        # find two adjacent unoccupied tiles with z-height difference of 1
        stair_spot = None
        for ty in range(100, 170):
            for tx in range(70, 170):
                region = self.bot.map(tx, ty, tx + 1, ty)
                tiles = region.get("tiles", [])
                if len(tiles) < 2:
                    continue
                t0, t1 = tiles[0], tiles[1]
                if t0.get("occupant") or t1.get("occupant"):
                    continue
                if t0.get("water", 0) > 0 or t1.get("water", 0) > 0:
                    continue
                h0, h1 = t0.get("terrain", 0), t1.get("terrain", 0)
                if abs(h0 - h1) == 1 and h0 >= 2 and h1 >= 2:
                    stair_spot = (tx, ty, tx + 1, ty)
                    break
            if stair_spot:
                break

        if stair_spot:
            sx1, sy1, sx2, sy2 = stair_spot
            result3 = self.bot.place_path(sx1, sy1, sx2, sy2)
            if self.has(result3, "errors") and result3["errors"] and "not unlocked" in str(result3["errors"]):
                self.skip("path with z-change", "stairs not unlocked")
            else:
                self.check("path with z-change places stairs",
                           self.has(result3, "stairs") and result3.get("stairs", 0) > 0,
                           json.dumps(result3)[:100])

                # verify stairs on map (wait for cache refresh)
                self.wait_for_refresh()
                region = self.bot.map(sx1, sy1, sx2, sy2)
                has_stairs = any("Stairs" in str(t.get("occupant", "")) or any("Stairs" in o.get("name", "") for o in t.get("occupants", [])) for t in region.get("tiles", []))
                self.check("verify stairs on map", has_stairs)

            # cleanup
            buildings = self.bot.buildings()
            if isinstance(buildings, list):
                for b in buildings:
                    bx, by = b.get("x", -1), b.get("y", -1)
                    if (bx == sx1 and by == sy1) or (bx == sx2 and by == sy2):
                        if "Path" in str(b.get("name", "")) or "Stairs" in str(b.get("name", "")):
                            self.bot.demolish_building(b["id"])
        else:
            self.skip("path with z-change", "no adjacent z-diff=1 tiles found")

    def test_overridable_placement(self):
        print("\n=== overridable placement ===\n")

        # find a non-overridable tree (dead standing or alive) -- should block placement
        # find an overridable entity (empty cut stump) -- should allow placement
        # use debug to check BlockObject.Overridable on each

        trees = self.bot.trees(limit=500)
        if not isinstance(trees, list) or len(trees) == 0:
            self.skip("overridable placement", "no trees found")
            return

        blocking_tree = None
        overridable_tree = None

        def is_overridable(entity_id):
            """check BlockObject.Overridable via debug endpoint"""
            self.bot.debug(target="call", method="FindEntity", arg0=str(entity_id))
            # BlockObject is at index 20 for natural resources (Pine, Birch, etc.)
            r = self.bot.debug(target="get", path="$.AllComponents.[20].Overridable")
            val = r.get("value")
            if val in ("True", "False"):
                return val == "True"
            # fallback: scan nearby indices
            for idx in range(18, 25):
                self.bot.debug(target="call", method="FindEntity", arg0=str(entity_id))
                r = self.bot.debug(target="get", path=f"$.AllComponents.[{idx}]")
                if "BlockObject" in r.get("type", "") and "Spec" not in r.get("type", ""):
                    self.bot.debug(target="call", method="FindEntity", arg0=str(entity_id))
                    ov = self.bot.debug(target="get", path=f"$.AllComponents.[{idx}].Overridable")
                    return ov.get("value") == "True"
            return False

        # living trees are always non-overridable
        # dead trees might be overridable (empty cut stumps)
        for t in trees:
            if not t.get("id"):
                continue
            if t.get("alive") and blocking_tree is None:
                blocking_tree = t
            elif not t.get("alive") and overridable_tree is None:
                if is_overridable(t["id"]):
                    overridable_tree = t

            if blocking_tree and overridable_tree:
                break

        # test: non-overridable tree blocks placement
        if blocking_tree:
            bx, by, bz = blocking_tree["x"], blocking_tree["y"], blocking_tree["z"]
            result = self.bot.place_building("Path", bx, by, bz)
            self.check("non-overridable tree blocks",
                       self.err(result) and "occupied" in str(result.get("error", "")),
                       json.dumps(result)[:100])
        else:
            self.skip("non-overridable tree blocks", "no blocking tree found")

        # test: overridable stump allows placement
        if overridable_tree:
            ox, oy, oz = overridable_tree["x"], overridable_tree["y"], overridable_tree["z"]
            result = self.bot.place_building("Path", ox, oy, oz)
            placed = self.has(result, "id")
            self.check("overridable stump allows placement", placed,
                       json.dumps(result)[:100])
            # clean up
            if placed:
                self.bot.demolish_building(result["id"])
        else:
            self.skip("overridable stump allows placement", "no overridable stump found")

    def test_summary_projection(self):
        print("\n=== summary projection ===\n")

        result = self.bot.summary()
        if isinstance(result, dict):
            self.check("foodDays present", "foodDays" in result)
            self.check("waterDays present", "waterDays" in result)
            if "foodDays" in result:
                fd = result["foodDays"]
                self.check("foodDays > 0", isinstance(fd, (int, float)) and fd > 0, f"got: {fd}")
            if "waterDays" in result:
                wd = result["waterDays"]
                self.check("waterDays > 0", isinstance(wd, (int, float)) and wd > 0, f"got: {wd}")
            self.check("logDays present", "logDays" in result,
                       f"keys: {[k for k in result if 'Days' in k]}")
            self.check("plankDays present", "plankDays" in result)
            self.check("gearDays present", "gearDays" in result)

    def test_map_moisture(self):
        print("\n=== map moisture ===\n")

        # check tiles near a water pump for moist field
        pump = self.find_building("DeepWaterPump")
        if not pump:
            self.skip("map moisture", "no water pump found")
            return
        pb = self.bot.buildings(detail=f"id:{pump}")
        if not pb or not isinstance(pb, list) or not pb:
            self.skip("map moisture", "cannot get pump details")
            return
        px, py = pb[0].get("x", 120), pb[0].get("y", 130)
        result = self.bot.map(px - 3, py - 3, px + 3, py + 3)
        tiles = result.get("tiles", [])
        moist_count = sum(1 for t in tiles if t.get("moist"))
        self.check("moist tiles near water", moist_count > 0, f"got {moist_count} moist tiles")

    def test_unlock(self):
        print("\n=== unlock ===\n")

        # check science points
        sci = self.bot.science()
        points = sci.get("points", 0) if isinstance(sci, dict) else 0
        if points < 50:
            self.skip("unlock test", f"only {points} science")
            return

        # find a cheap unlockable
        unlockables = sci.get("unlockables", [])
        target = None
        for u in unlockables:
            if not u.get("unlocked") and u.get("cost", 999) <= points:
                target = u
                break
        if not target:
            self.skip("unlock test", "nothing affordable")
            return

        before = points
        result = self.bot.unlock_building(target["name"])
        self.check(f"unlock {target['name']}",
                   self.has(result, "unlocked") and result["unlocked"] == True,
                   json.dumps(result)[:100])

        if self.has(result, "remaining"):
            expected = before - target["cost"]
            self.check("science deducted",
                       result["remaining"] == expected,
                       f"expected {expected}, got {result['remaining']}")

        # try unlocking same building again -- should say already unlocked, no point change
        points_after = result.get("remaining", 0)
        result2 = self.bot.unlock_building(target["name"])
        self.check("already unlocked returns note",
                   self.has(result2, "note") and "already" in str(result2.get("note", "")),
                   json.dumps(result2)[:100])
        self.check("no points deducted on re-unlock",
                   result2.get("remaining") == points_after,
                   f"expected {points_after}, got {result2.get('remaining')}")


    def test_floodgate(self):
        print("\n=== floodgate ===\n")

        fid = self.find_building("Floodgate")
        if not fid:
            self.skip("floodgate", "no floodgate found")
            return

        result = self.bot.set_floodgate(fid, 1.5)
        self.check("set floodgate height",
                   self.has(result, "height"),
                   json.dumps(result)[:100])

    def test_haul_priority(self):
        print("\n=== haul priority ===\n")

        # find a breeding pod or stockpile
        bid = self.find_building("BreedingPod") or self.find_building("SmallTank")
        if not bid:
            self.skip("haul priority", "no suitable building found")
            return

        result = self.bot.set_haul_priority(bid, True)
        self.check("set haul priority",
                   self.has(result, "haulPrioritized") and result["haulPrioritized"] == True,
                   json.dumps(result)[:100])

        # reset
        self.bot.set_haul_priority(bid, False)

    def test_recipe(self):
        print("\n=== recipe ===\n")

        mid = self.find_building("IndustrialLumberMill")
        if not mid:
            self.skip("recipe", "no lumber mill found")
            return

        # set recipe -- use invalid name first to see what's available
        result = self.bot.set_recipe(mid, "InvalidRecipe")
        self.check("invalid recipe returns error",
                   self.err(result),
                   json.dumps(result)[:100])

    def test_farmhouse_action(self):
        print("\n=== farmhouse action ===\n")

        fid = self.find_building("FarmHouse")
        if not fid:
            self.skip("farmhouse action", "no farmhouse found")
            return

        result = self.bot.set_farmhouse_action(fid, "planting")
        self.check("set farmhouse planting",
                   self.has(result, "action") and result["action"] == "planting",
                   json.dumps(result)[:100])

        # reset
        self.bot.set_farmhouse_action(fid, "harvesting")

    def test_plantable_priority(self):
        print("\n=== plantable priority ===\n")

        fid = self.find_building("Forester")
        if not fid:
            self.skip("plantable priority", "no forester found")
            return

        result = self.bot.set_plantable_priority(fid, "Pine")
        self.check("set plantable priority",
                   self.has(result, "prioritized") and result["prioritized"] == "Pine",
                   json.dumps(result)[:100])

        # clear
        self.bot.set_plantable_priority(fid, "none")

    def test_stockpile_capacity(self):
        print("\n=== stockpile capacity ===\n")

        sid = self.find_building("SmallTank") or self.find_building("MediumTank")
        if not sid:
            self.skip("stockpile capacity", "no tank found")
            return

        # get current capacity via debug
        self.bot.debug(target="call", method="FindEntity", arg0=str(sid))
        orig = self.bot.debug(target="get", path="$~Inventories")

        result = self.bot.set_capacity(sid, 50)
        self.check("set stockpile capacity",
                   self.has(result, "capacity"),
                   json.dumps(result)[:100])

    def test_workhours(self):
        print("\n=== workhours ===\n")

        # get current
        orig = self.bot.workhours()
        orig_hours = orig.get("endHours", 18) if isinstance(orig, dict) else 18

        result = self.bot.set_workhours(20)
        self.check("set workhours",
                   not self.err(result),
                   json.dumps(result)[:100])

        # verify via debug
        verify = self.debug_get("_workingHoursManager.EndHours")
        self.check("verify workhours via debug",
                   str(verify.get("value", "")) == "20",
                   f"got: {verify.get('value')}")

        # restore
        self.bot.set_workhours(orig_hours)

    def test_distribution(self):
        print("\n=== distribution ===\n")

        # get current distribution
        dist = self.bot.distribution()
        if not isinstance(dist, list) or len(dist) == 0:
            self.skip("distribution", "no distribution data")
            return

        # find a district with a good
        district = None
        good = None
        for d in dist:
            goods = d.get("goods", [])
            if goods and d.get("district"):
                district = d["district"]
                good = goods[0].get("good")
                break
        if not district or not good:
            self.skip("distribution", "no district/good pair found")
            return

        result = self.bot.set_distribution(district, good, "ImportDisabled", 0)
        self.check("set distribution",
                   not self.err(result),
                   json.dumps(result)[:100])


    def test_beaver_needs(self):
        print("\n=== beaver needs ===\n")

        # get beavers in JSON mode for full need data
        result = self.bot.beavers(detail="full")
        if not isinstance(result, list) or len(result) == 0:
            self.skip("beaver needs", "no beavers found")
            return

        beaver = result[0]
        self.check("beaver has needs list",
                   "needs" in beaver and isinstance(beaver["needs"], list),
                   f"keys: {list(beaver.keys())}")

        if isinstance(beaver.get("needs"), list) and len(beaver["needs"]) > 0:
            need = beaver["needs"][0]
            self.check("need has id",
                       "id" in need,
                       json.dumps(need)[:100])
            self.check("need has wellbeing",
                       "wellbeing" in need,
                       json.dumps(need)[:100])
            self.check("need has favorable",
                       "favorable" in need,
                       json.dumps(need)[:100])

        # TOON format should have unmet field
        toon = self.bot.beavers(limit=1)
        if isinstance(toon, list) and len(toon) > 0:
            self.check("toon has unmet field",
                       "unmet" in toon[0],
                       json.dumps(toon[0])[:100])

    def test_building_range(self):
        print("\n=== building range ===\n")

        # find a farmhouse
        fid = self.find_building("FarmHouse")
        if not fid:
            self.skip("building range", "no farmhouse found")
            return

        result = self.bot.building_range(fid)
        self.check("range returns tiles",
                   self.has(result, "tiles") and result["tiles"] > 0,
                   json.dumps(result)[:100])
        self.check("range has moist count",
                   "moist" in result,
                   json.dumps(result)[:100])
        self.check("range has bounds",
                   self.has(result, "bounds"),
                   json.dumps(result)[:100])

        # also test on a building without range
        dc_id = self.find_building("DistrictCenter")
        if dc_id:
            dc_result = self.bot.building_range(dc_id)
            # DC may or may not have range -- just check no crash
            self.check("range on DC no crash", not self.err(dc_result) or "no work range" in str(dc_result.get("error", "")),
                       json.dumps(dc_result)[:100])

    def test_find_planting(self):
        print("\n=== find planting ===\n")

        # area mode
        result = self.bot.find_planting("Kohlrabi", x1=68, y1=128, x2=72, y2=132, z=2)
        self.check("find_planting area returns spots",
                   self.has(result, "spots") and len(result.get("spots", [])) > 0,
                   json.dumps(result)[:100])

        if result.get("spots"):
            spot = result["spots"][0]
            self.check("spot has moist field",
                       "moist" in spot,
                       json.dumps(spot)[:60])
            self.check("spot has planted field",
                       "planted" in spot,
                       json.dumps(spot)[:60])

        # building mode
        fid = self.find_building("FarmHouse")
        if fid:
            result2 = self.bot.find_planting("Kohlrabi", building_id=fid)
            self.check("find_planting by building",
                       self.has(result2, "spots"),
                       json.dumps(result2)[:100])
        else:
            self.skip("find_planting by building", "no farmhouse found")


    def test_prefab_costs(self):
        print("\n=== prefab costs ===\n")

        prefabs = self.bot.prefabs()
        if not isinstance(prefabs, list) or len(prefabs) == 0:
            self.skip("prefab costs", "no prefabs")
            return

        # find a building with costs
        with_cost = None
        for p in prefabs:
            if p.get("cost") and len(p.get("cost", [])) > 0:
                with_cost = p
                break

        if with_cost:
            self.check("prefab has cost array",
                       isinstance(with_cost["cost"], list),
                       json.dumps(with_cost)[:100])
            cost0 = with_cost["cost"][0]
            self.check("cost has good field",
                       "good" in cost0,
                       json.dumps(cost0)[:60])
            self.check("cost has amount field",
                       "amount" in cost0,
                       json.dumps(cost0)[:60])
        else:
            self.skip("prefab cost fields", "no prefab with costs found")

        # find a building with science cost
        with_science = None
        for p in prefabs:
            if p.get("scienceCost") and p["scienceCost"] > 0:
                with_science = p
                break

        if with_science:
            self.check("prefab has scienceCost",
                       with_science["scienceCost"] > 0,
                       json.dumps(with_science)[:100])
            self.check("prefab has unlocked field",
                       "unlocked" in with_science,
                       json.dumps(with_science)[:100])
        else:
            self.skip("prefab science fields", "no prefab with science cost")

    def test_building_inventory(self):
        print("\n=== building inventory ===\n")

        # find a tank or warehouse with stock/capacity
        buildings = self.bot.buildings(detail="full")
        if not isinstance(buildings, list):
            self.skip("building inventory", "no buildings")
            return

        with_capacity = None
        for b in buildings:
            if b.get("capacity") and b["capacity"] > 0:
                with_capacity = b
                break

        if with_capacity:
            self.check("building has stock",
                       "stock" in with_capacity,
                       f"{with_capacity.get('name')}: stock={with_capacity.get('stock')}")
            self.check("building has capacity",
                       with_capacity["capacity"] > 0,
                       f"{with_capacity.get('name')}: capacity={with_capacity.get('capacity')}")

            # verify via debug
            bid = with_capacity["id"]
            self.bot.debug(target="call", method="FindEntity", arg0=str(bid))
            # find Inventories component and check TotalAmountInStock
            r = self.bot.debug(target="get", path="$~Inventories")
            self.check("debug confirms Inventories component",
                       r.get("value") != "null" or "error" not in r,
                       json.dumps(r)[:100])
        else:
            self.skip("building inventory", "no building with capacity found")

    def test_building_recipes(self):
        print("\n=== building recipes ===\n")

        buildings = self.bot.buildings(detail="full")
        if not isinstance(buildings, list):
            self.skip("building recipes", "no buildings")
            return

        with_recipes = None
        for b in buildings:
            if b.get("recipes") and len(b.get("recipes", [])) > 0:
                with_recipes = b
                break

        if with_recipes:
            self.check("building has recipes array",
                       isinstance(with_recipes["recipes"], list) and len(with_recipes["recipes"]) > 0,
                       json.dumps(with_recipes["recipes"])[:100])
            self.check("building has currentRecipe",
                       "currentRecipe" in with_recipes,
                       f"currentRecipe={with_recipes.get('currentRecipe')}")
        else:
            self.skip("building recipes", "no manufactory found")

        # find a breeding pod
        with_breeding = None
        for b in buildings:
            if "BreedingPod" in str(b.get("name", "")):
                with_breeding = b
                break

        if with_breeding:
            self.check("breeding pod has needsNutrients",
                       "needsNutrients" in with_breeding,
                       json.dumps(with_breeding)[:100])
        else:
            self.skip("breeding pod status", "no breeding pod found")

    def test_clutch(self):
        print("\n=== clutch ===\n")

        # find a building with clutch
        buildings = self.bot.buildings(detail="full")
        if not isinstance(buildings, list):
            self.skip("clutch", "no buildings")
            return

        clutch_id = None
        for b in buildings:
            if b.get("isClutch"):
                clutch_id = b["id"]
                break

        if not clutch_id:
            self.skip("clutch", "no building with clutch found")
            return

        # disengage
        result = self.bot.set_clutch(clutch_id, False)
        self.check("disengage clutch",
                   self.has(result, "engaged") and result["engaged"] == False,
                   json.dumps(result)[:100])

        # verify via debug
        self.bot.debug(target="call", method="FindEntity", arg0=str(clutch_id))
        r = self.bot.debug(target="get", path="$~Clutch.IsEngaged")
        self.check("verify disengaged via debug",
                   r.get("value") == "False",
                   f"got: {r.get('value')}")

        # re-engage
        result2 = self.bot.set_clutch(clutch_id, True)
        self.check("re-engage clutch",
                   self.has(result2, "engaged") and result2["engaged"] == True,
                   json.dumps(result2)[:100])


    def test_beaver_detail(self):
        """Test detail mode shows all needs with group categories."""
        # basic mode (default detail)
        basic = self.bot.beavers()
        beavers_only = [b for b in basic if not b.get("isBot")]
        if not beavers_only:
            self.skip("beaver_detail", "no beavers")
            return
        beaver = beavers_only[0]
        basic_needs = len(beaver.get("needs", []))

        # full detail mode
        full = self.bot.beavers(detail="full")
        beaver_full = [b for b in full if b["id"] == beaver["id"]][0]
        full_needs = len(beaver_full.get("needs", []))
        self.check("full has more needs than basic",
                   full_needs >= basic_needs,
                   f"full={full_needs} basic={basic_needs}")

        # check group field on full detail needs
        groups_seen = set()
        for n in beaver_full.get("needs", []):
            has_group = "group" in n
            self.check(f"need {n['id']} has group", has_group,
                       f"keys: {list(n.keys())}")
            if has_group:
                groups_seen.add(n["group"])
        self.check("multiple need groups found", len(groups_seen) > 1,
                   f"groups: {groups_seen}")

        # single beaver by id
        single = self.bot.beavers(detail=f"id:{beaver['id']}")
        self.check("single beaver returns 1 result", len(single) == 1,
                   f"got {len(single)}")
        self.check("single has all needs",
                   len(single[0].get("needs", [])) == full_needs,
                   f"single={len(single[0].get('needs', []))} full={full_needs}")

        # basic mode should NOT have group field
        for n in beaver.get("needs", []):
            self.check(f"basic need {n['id']} no group", "group" not in n)

    def test_bot_data(self):
        """Test bot-specific fields in beavers endpoint."""
        beavers = self.bot._get("/api/beavers", params={"detail": "full"})
        bots = [b for b in beavers if b.get("isBot")]
        if not bots:
            self.skip("bot_data", "no bots in colony")
            return
        bot = bots[0]
        self.check("bot has isBot=true", bot["isBot"] == True)
        self.check("bot has needs", "needs" in bot and len(bot["needs"]) > 0)

        # bot should have Energy, ControlTower, and Grease needs (always, even if inactive)
        need_ids = {n["id"] for n in bot.get("needs", [])}
        self.check("bot has Energy need", "Energy" in need_ids, f"got: {need_ids}")
        self.check("bot has ControlTower need", "ControlTower" in need_ids, f"got: {need_ids}")
        self.check("bot has Grease need", "Grease" in need_ids, f"got: {need_ids}")

        # each need should have points between 0 and 1
        for need in bot.get("needs", []):
            pts = need.get("points", -1)
            self.check(f"bot need {need['id']} points in range",
                       0 <= pts <= 1, f"points={pts}")

    def test_bot_buildings(self):
        """Test bot production buildings have correct detail fields."""
        buildings = self.bot.buildings()

        # BotPartFactory
        factories = [b for b in buildings if b.get("name") == "BotPartFactory"]
        if factories:
            fid = factories[0]["id"]
            detail = self.bot.buildings(detail=f"id:{fid}")
            if detail:
                d = detail[0]
                self.check("factory has recipes", "recipes" in d)
                self.check("factory has productionProgress", "productionProgress" in d)
                self.check("factory has inventory", "inventory" in d)
                self.check("factory has powered field", "powered" in d,
                           f"keys={list(d.keys())[:10]}")
        else:
            self.skip("bot_factory", "no BotPartFactory")

        # BotAssembler
        assemblers = [b for b in buildings if b.get("name") == "BotAssembler"]
        if assemblers:
            aid = assemblers[0]["id"]
            detail = self.bot.buildings(detail=f"id:{aid}")
            if detail:
                d = detail[0]
                self.check("assembler has recipes", "recipes" in d)
                self.check("assembler recipe is Bot.IronTeeth",
                           d.get("currentRecipe") == "Bot.IronTeeth",
                           f"recipe={d.get('currentRecipe')}")
                self.check("assembler has productionProgress",
                           "productionProgress" in d)
        else:
            self.skip("bot_assembler", "no BotAssembler")

        # ChargingStation
        chargers = [b for b in buildings if b.get("name") == "ChargingStation"]
        if chargers:
            cid = chargers[0]["id"]
            detail = self.bot.buildings(detail=f"id:{cid}")
            if detail:
                self.check("charger has powered field", "powered" in detail[0])
        else:
            self.skip("charging_station", "no ChargingStation")

    def test_bot_in_summary(self):
        """Verify bots appear in summary and population counts."""
        summary = self.bot.summary()
        self.check("summary has bots field", "bots" in summary,
                   f"keys: {list(summary.keys())[:20]}")

        pop = self.bot.population()
        if pop:
            self.check("population has bots", "bots" in pop[0],
                       f"keys: {list(pop[0].keys())}")

    def test_bot_toon_format(self):
        """Verify bot shows correctly in TOON beavers output."""
        beavers = self.bot.beavers()
        bots = [b for b in beavers if b.get("isBot")]
        if bots:
            bot = bots[0]
            self.check("toon bot has isBot", "isBot" in bot)
            self.check("toon bot isBot=True", bot["isBot"] == True)
            self.check("toon bot has name", "name" in bot and "Bot" in str(bot["name"]))
        else:
            self.skip("bot_toon", "no bots in colony")


    def test_beaver_position(self):
        """Test beaver/bot x,y,z grid position."""
        print("\n=== beaver position ===\n")
        beavers = self.bot.beavers(detail="full")
        if not beavers:
            self.skip("beaver_position", "no beavers")
            return
        b = beavers[0]
        self.check("beaver has x", "x" in b, f"keys: {list(b.keys())[:10]}")
        self.check("beaver has y", "y" in b)
        self.check("beaver has z", "z" in b)
        if "x" in b:
            self.check("x is int", isinstance(b["x"], int), f"type={type(b['x'])}")
            self.check("x in range", 0 <= b["x"] <= 256, f"x={b['x']}")
            self.check("y in range", 0 <= b["y"] <= 256, f"y={b['y']}")
            self.check("z >= 0", b["z"] >= 0, f"z={b['z']}")

        # bot should also have position
        bots = [x for x in beavers if x.get("isBot")]
        if bots:
            bot = bots[0]
            self.check("bot has x", "x" in bot)
            self.check("bot has y", "y" in bot)
        else:
            self.skip("bot_position", "no bots in colony")

    def test_beaver_district(self):
        """Test district field on beavers."""
        print("\n=== beaver district ===\n")
        beavers = self.bot.beavers(detail="full")
        if not beavers:
            self.skip("beaver_district", "no beavers")
            return
        # at least one beaver should have a district
        with_district = [b for b in beavers if "district" in b]
        self.check("some beavers have district",
                   len(with_district) > 0,
                   f"{len(with_district)}/{len(beavers)} have district")
        if with_district:
            self.check("district is string",
                       isinstance(with_district[0]["district"], str))
            self.check("district is not empty",
                       len(with_district[0]["district"]) > 0)

    def test_map_stacking(self):
        """Test map shows multiple occupants at different z-levels."""
        print("\n=== map stacking ===\n")
        # find an area with stairs/platforms by looking for Platform buildings
        platform = self.find_building("Platform") or self.find_building("Stairs")
        if not platform:
            self.skip("map stacking", "no platform/stairs found")
            return
        pb = self.bot.buildings(detail=f"id:{platform}")
        if not pb or not isinstance(pb, list) or not pb:
            self.skip("map stacking", "cannot get platform details")
            return
        px, py = pb[0].get("x", 139), pb[0].get("y", 147)
        result = self.bot.map(px - 1, py - 1, px + 1, py + 1)
        tiles = result.get("tiles", [])
        self.check("map returns tiles", len(tiles) > 0)

        # check for stacked tiles (occupants array)
        stacked = [t for t in tiles if "occupants" in t]
        single = [t for t in tiles if "occupant" in t and "occupants" not in t]

        if stacked:
            t = stacked[0]
            self.check("stacked tile has occupants array",
                       isinstance(t["occupants"], list))
            self.check("stacked has multiple occupants",
                       len(t["occupants"]) >= 2,
                       f"count={len(t['occupants'])}")
            occ = t["occupants"][0]
            self.check("stacked occupant has name", "name" in occ)
            self.check("stacked occupant has z", "z" in occ)
        else:
            self.skip("map_stacked", "no stacked tiles in test area")

        if single:
            self.check("single occupant is string",
                       isinstance(single[0]["occupant"], str))

        # backward compat: single occupant tiles still use "occupant" string
        for t in tiles:
            has_both = "occupant" in t and "occupants" in t
            self.check(f"tile ({t['x']},{t['y']}) no overlap",
                       not has_both,
                       "has both occupant and occupants")

    def test_carried_goods(self):
        """Test carried goods fields on beavers."""
        print("\n=== carried goods ===\n")
        beavers = self.bot.beavers(detail="full")
        carriers = [b for b in beavers if "carrying" in b]
        if carriers:
            c = carriers[0]
            self.check("carrying is string", isinstance(c["carrying"], str))
            self.check("carryAmount is int", isinstance(c["carryAmount"], int))
            self.check("carryAmount > 0", c["carryAmount"] > 0)
        else:
            self.skip("carried_goods", "no beaver currently carrying")

        # detail:full should have liftingCapacity
        full = self.bot.beavers(detail="full")
        beaver = [b for b in full if not b.get("isBot")][0]
        self.check("detail has liftingCapacity", "liftingCapacity" in beaver,
                   f"keys: {[k for k in beaver.keys() if 'lift' in k.lower() or 'carry' in k.lower()]}")
        if "liftingCapacity" in beaver:
            self.check("liftingCapacity > 0", beaver["liftingCapacity"] > 0)

    def test_bot_durability(self):
        """Test deterioration field on bots."""
        print("\n=== bot durability ===\n")
        beavers = self.bot.beavers(detail="full")
        bots = [b for b in beavers if b.get("isBot")]
        if not bots:
            self.skip("bot_durability", "no bots in colony")
            return
        bot = bots[0]
        self.check("bot has deterioration", "deterioration" in bot,
                   f"keys: {list(bot.keys())}")
        if "deterioration" in bot:
            self.check("deterioration is number",
                       isinstance(bot["deterioration"], (int, float)))
            self.check("deterioration in range 0-1",
                       0 <= bot["deterioration"] <= 1,
                       f"deterioration={bot['deterioration']}")

    def test_power_networks(self):
        """Test power network endpoint."""
        print("\n=== power networks ===\n")
        networks = self.bot.power()
        self.check("power returns list", isinstance(networks, list))
        self.check("has networks", len(networks) > 0, f"count={len(networks)}")

        if networks:
            net = networks[0]
            self.check("network has id", "id" in net)
            self.check("network has supply", "supply" in net)
            self.check("network has demand", "demand" in net)
            self.check("network has buildings", "buildings" in net)
            self.check("buildings is list", isinstance(net["buildings"], list))

            if net["buildings"]:
                b = net["buildings"][0]
                self.check("building has name", "name" in b)
                self.check("building has id", "id" in b)
                self.check("building has isGenerator", "isGenerator" in b)
                self.check("building has nominalOutput", "nominalOutput" in b)
                self.check("building has nominalInput", "nominalInput" in b)

            # find a network with a generator
            gen_nets = [n for n in networks
                        if any(b.get("isGenerator") for b in n.get("buildings", []))]
            if gen_nets:
                self.check("generator network has demand field",
                           "demand" in gen_nets[0])
            else:
                self.skip("power_generator", "no networks with generators")

    def test_building_detail(self):
        print("\n=== building detail ===\n")

        # basic (default) should return compact rows
        basic = self.bot.buildings()
        self.check("basic returns list", isinstance(basic, list) and len(basic) > 0)
        if basic:
            first = basic[0]
            self.check("basic has id", "id" in first)
            self.check("basic has name", "name" in first)
            # basic should NOT have inventory or effectRadius
            self.check("basic omits inventory", "inventory" not in first)

        # full detail
        full = self.bot.buildings(detail="full")
        self.check("full returns list", isinstance(full, list) and len(full) > 0)
        if full:
            # find a manufactory to check productionProgress
            manufactory = None
            for b in full:
                if "productionProgress" in b:
                    manufactory = b
                    break
            if manufactory:
                self.check("full has productionProgress", "productionProgress" in manufactory)
                self.check("full has readyToProduce", "readyToProduce" in manufactory)
            else:
                self.skip("manufactory fields", "no active manufactory")

            # find a monument to check effectRadius
            monument = None
            for b in full:
                if "effectRadius" in b:
                    monument = b
                    break
            if monument:
                self.check("full has effectRadius", monument["effectRadius"] > 0)
            else:
                self.skip("effectRadius", "no ranged effect building")

        # single building by id
        if basic:
            bid = basic[0]["id"]
            single = self.bot.buildings(detail=f"id:{bid}")
            self.check("single returns list", isinstance(single, list))
            self.check("single has 1 result", len(single) == 1)
            if single:
                self.check("single has full fields", "finished" in single[0])

    def test_performance(self):
        print("\n=== performance ===\n")

        # time ALL endpoints, 100 iterations each, track reliability
        endpoints = [
            ("ping", lambda: self.bot.ping()),
            ("summary", lambda: self.bot.summary()),
            ("buildings", lambda: self.bot.buildings()),
            ("buildings full", lambda: self.bot.buildings(detail="full")),
            ("trees", lambda: self.bot.trees()),
            ("gatherables", lambda: self.bot.gatherables()),
            ("beavers", lambda: self.bot.beavers()),
            ("alerts", lambda: self.bot.alerts()),
            ("resources", lambda: self.bot.resources()),
            ("weather", lambda: self.bot.weather()),
            ("time", lambda: self.bot.time()),
            ("districts", lambda: self.bot.districts()),
            ("distribution", lambda: self.bot.distribution()),
            ("science", lambda: self.bot.science()),
            ("notifications", lambda: self.bot.notifications()),
            ("workhours", lambda: self.bot.workhours()),
            ("speed", lambda: self.bot.speed()),
            ("prefabs", lambda: self.bot.prefabs()),
            ("wellbeing", lambda: self.bot.wellbeing()),
            ("tree_clusters", lambda: self.bot.tree_clusters()),
        ]

        iterations = 100

        print(f"  timing {len(endpoints)} endpoints x {iterations} iterations\n")
        print(f"  {'endpoint':<25} {'avg ms':>8} {'min ms':>8} {'max ms':>8} {'items':>6} {'ok':>4}")
        print(f"  {'-'*25} {'-'*8} {'-'*8} {'-'*8} {'-'*6} {'-'*4}")

        total_ok = 0
        total_bad = 0
        for name, fn in endpoints:
            times = []
            ok = 0
            result = None
            for _ in range(iterations):
                t0 = time.perf_counter()
                try:
                    result = fn()
                    ok += 1
                except Exception:
                    result = None
                t1 = time.perf_counter()
                times.append((t1 - t0) * 1000)

            total_ok += ok
            total_bad += iterations - ok
            avg = sum(times) / len(times)
            mn = min(times)
            mx = max(times)
            count = len(result) if isinstance(result, list) else 1 if result else 0

            print(f"  {name:<25} {avg:>8.1f} {mn:>8.1f} {mx:>8.1f} {count:>6} {ok:>4}")

        print()
        self.check(f"all {total_ok + total_bad} responses valid",
                   total_bad == 0,
                   f"{total_bad} bad responses out of {total_ok + total_bad}")

        # cache consistency: call same endpoint twice, verify same results
        print("\n  cache consistency: verifying repeated calls return same data...")
        b1 = self.bot.buildings()
        b2 = self.bot.buildings()
        self.check("buildings cache consistent",
                   isinstance(b1, list) and isinstance(b2, list) and len(b1) == len(b2),
                   f"first={len(b1) if isinstance(b1,list) else '?'}, second={len(b2) if isinstance(b2,list) else '?'}")

        t1 = self.bot.trees()
        t2 = self.bot.trees()
        self.check("trees cache consistent",
                   isinstance(t1, list) and isinstance(t2, list) and len(t1) == len(t2),
                   f"first={len(t1) if isinstance(t1,list) else '?'}, second={len(t2) if isinstance(t2,list) else '?'}")

        bv1 = self.bot.beavers()
        bv2 = self.bot.beavers()
        self.check("beavers cache consistent",
                   isinstance(bv1, list) and isinstance(bv2, list) and len(bv1) == len(bv2),
                   f"first={len(bv1) if isinstance(bv1,list) else '?'}, second={len(bv2) if isinstance(bv2,list) else '?'}")

        # cache invalidation: place and demolish a building, verify index updates
        print("\n  cache invalidation: place + demolish to verify index tracks changes...")
        before_count = len(self.bot.buildings())
        # find a valid spot dynamically
        spots = self.bot.find_placement("Path", 120, 120, 130, 130)
        placements = spots.get("placements", []) if isinstance(spots, dict) else []
        placed = None
        if placements:
            s = placements[0]
            placed = self.bot.place_building("Path", s["x"], s["y"], s["z"], s.get("orientation", "south"))
        if placed and not self.err(placed):
            self.wait_for_refresh()
            after_count = len(self.bot.buildings())
            self.check("index grew after place", after_count == before_count + 1,
                       f"before={before_count}, after={after_count}")

            # demolish it
            placed_id = placed.get("id")
            if placed_id:
                self.bot.demolish_building(placed_id)
                self.wait_for_refresh()
                final_count = len(self.bot.buildings())
                self.check("index shrank after demolish", final_count == before_count,
                           f"before={before_count}, final={final_count}")
            else:
                self.skip("demolish check", "no id in place result")
        else:
            self.skip("cache invalidation", f"place failed: {placed}" if placed else "no valid spot")

        # serialization A/B test: dict vs anon vs sb for trees
        print("\n  serialization A/B test (trees)...\n")
        methods = ["dict", "anon", "sb"]
        iters = 5
        print(f"  {'method':<10} {'avg ms':>8} {'min ms':>8} {'max ms':>8} {'items':>6} {'bytes':>8}")
        print(f"  {'-'*10} {'-'*8} {'-'*8} {'-'*8} {'-'*6} {'-'*8}")
        for m in methods:
            times = []
            result = None
            raw = None
            for _ in range(iters):
                t0 = time.perf_counter()
                r = self.bot.s.get(f"{self.bot.url}/api/trees",
                                   params={"format": self.bot._format, "serial": m}, timeout=10)
                t1 = time.perf_counter()
                times.append((t1 - t0) * 1000)
                raw = r.content
                result = r.json()
            avg = sum(times) / len(times)
            mn = min(times)
            mx = max(times)
            count = len(result) if isinstance(result, list) else 1
            print(f"  {m:<10} {avg:>8.1f} {mn:>8.1f} {mx:>8.1f} {count:>6} {len(raw):>8}")

        # burst test: simulate bot turn (multiple calls in quick succession)
        print("\n  burst test: simulating bot turn (7 calls)...")
        t0 = time.perf_counter()
        self.bot.summary()
        self.bot.buildings()
        self.bot.beavers()
        self.bot.trees()
        self.bot.alerts()
        self.bot.resources()
        self.bot.weather()
        t1 = time.perf_counter()
        burst_ms = (t1 - t0) * 1000
        print(f"  burst total: {burst_ms:.0f}ms ({burst_ms/7:.0f}ms avg per call)")
        self.check("burst < 3s total", burst_ms < 3000,
                   f"burst took {burst_ms:.0f}ms")


def main():
    runner = TestRunner()
    runner.run()


if __name__ == "__main__":
    main()
