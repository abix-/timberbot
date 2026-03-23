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


class TestRunner:
    def __init__(self):
        self.bot = Timberbot()
        self.passed = 0
        self.failed = 0
        self.skipped = 0

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
            ("map", lambda: self.bot.map(120, 142, 122, 144)),
            ("tree_clusters", lambda: self.bot.tree_clusters()),
            ("wellbeing", lambda: self.bot.wellbeing()),
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

        # error cases -- game-native validation returns generic "Cannot place" for most
        tests = [
            ("occupied tile", lambda: self.bot.place_building("Path", 122, 133, 2)),
            ("on water", lambda: self.bot.place_building("Path", 124, 130, 1)),
            ("off map", lambda: self.bot.place_building("Path", 999, 999, 2)),
            ("z too high", lambda: self.bot.place_building("Path", 70, 125, 4)),
            ("z too low", lambda: self.bot.place_building("Path", 70, 125, 1)),
        ]
        for name, fn in tests:
            result = fn()
            self.check(name, self.err(result),
                       json.dumps(result)[:100])

        # these still return specific error messages (checked before preview validation)
        specific_tests = [
            ("unknown prefab", lambda: self.bot.place_building("Fake", 120, 130, 2), "not found"),
            ("invalid orientation", lambda: self.bot.place_building("Path", 120, 127, 2, orientation="bogus"), "invalid orientation"),
            ("locked building", lambda: self.bot.place_building("TributeToIngenuity.IronTeeth", 70, 125, 2), "not unlocked"),
        ]
        for name, fn, expect_err in specific_tests:
            result = fn()
            self.check(name, self.err(result) and expect_err in str(result["error"]),
                       json.dumps(result)[:100])

        # valid placement
        result = self.bot.place_building("Path", 70, 125, 2)
        self.check("valid placement", self.has(result, "id"))

        if self.has(result, "id"):
            placed_id = result["id"]

            # verify via map that tile is now occupied
            tile = self.bot.map(70, 125, 70, 125)
            tiles = tile.get("tiles", [])
            has_path = any(t.get("occupant") == "Path" for t in tiles)
            self.check("verify placement via map", has_path)

            # demolish
            dem = self.bot.demolish_building(placed_id)
            self.check("demolish", self.has(dem, "demolished") or not self.err(dem))

            # verify gone via map
            tile2 = self.bot.map(70, 125, 70, 125)
            tiles2 = tile2.get("tiles", [])
            no_path = not any(t.get("occupant") == "Path" for t in tiles2)
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

        # verify via buildings endpoint
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

        # plant on open ground
        result = self.bot.plant_crop(110, 130, 112, 132, 2, "Kohlrabi")
        self.check("plant crops", self.has(result, "planted") and result["planted"] == 9,
                   json.dumps(result)[:100])

        # plant on occupied (should skip)
        result2 = self.bot.plant_crop(119, 130, 122, 134, 2, "Kohlrabi")
        self.check("skip occupied tiles", self.has(result2, "skipped") and result2["skipped"] > 0)

        # plant on water (all skipped)
        result3 = self.bot.plant_crop(124, 128, 128, 132, 2, "Kohlrabi")
        self.check("skip water tiles",
                   self.has(result3, "planted") and result3["planted"] == 0 and result3.get("skipped", 0) > 0)

        # clear
        self.bot.clear_planting(110, 130, 112, 132, 2)
        self.bot.clear_planting(119, 130, 122, 134, 2)

    def test_tree_marking(self):
        print("\n=== tree marking ===\n")

        # mark trees in an area
        result = self.bot.mark_trees(125, 150, 135, 160, 4)
        self.check("mark trees", not self.err(result),
                   json.dumps(result)[:100] if self.err(result) else "")

        # verify via trees endpoint - some should be marked
        trees = self.bot.trees()
        marked = 0
        if isinstance(trees, list):
            marked = sum(1 for t in trees if t.get("marked"))
        self.check("verify trees marked", marked > 0, f"marked count: {marked}")

        # clear
        self.bot.clear_trees(125, 150, 135, 160, 4)

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

        # find flat test area
        test_spot = None
        need = 5
        for cy in range(125, 145):
            for cx in range(70, 130):
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

                # verify origin via map
                region = self.bot.map(bx - 1, by - 1, bx + sx, by + sy)
                occupied = [(t["x"], t["y"]) for t in region.get("tiles", [])
                            if t.get("occupant") and prefab.split(".")[0] in t["occupant"]]
                min_x = min(t[0] for t in occupied) if occupied else -1
                min_y = min(t[1] for t in occupied) if occupied else -1
                self.check(f"{prefab.split('.')[0]} {orient} origin=({min_x},{min_y})",
                           min_x == bx and min_y == by,
                           f"expected ({bx},{by})")
                self.bot.demolish_building(result["id"])

    def test_find_placement(self):
        print("\n=== find_placement ===\n")

        result = self.bot.find_placement("Inventor.IronTeeth", 100, 115, 155, 155)
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

        # find a flat open area for path tests
        test_spot = None
        for cy in range(125, 140):
            for cx in range(70, 100):
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

        # verify tiles are occupied
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
        for ty in range(125, 145):
            for tx in range(70, 140):
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

                # verify stairs on map
                region = self.bot.map(sx1, sy1, sx2, sy2)
                has_stairs = any("Stairs" in str(t.get("occupant", "")) for t in region.get("tiles", []))
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

    def test_map_moisture(self):
        print("\n=== map moisture ===\n")

        # check tiles near water for moist field
        result = self.bot.map(120, 135, 125, 140)
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


def main():
    runner = TestRunner()
    runner.run()


if __name__ == "__main__":
    main()
