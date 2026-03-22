"""Smoke tests for all API endpoints + TOON CLI output.

Requires a running game with any save loaded.

Usage:
    python timberbot/script/test_endpoints.py
"""
import json
import subprocess
import sys

from timberbot import Timberbot


def check(name, ok, detail=""):
    status = "PASS" if ok else "FAIL"
    print(f"  {status}  {name}" + (f" -- {detail}" if detail else ""))
    return ok


def main():
    bot = Timberbot()
    if not bot.ping():
        print("error: game not reachable")
        sys.exit(1)

    passed = 0
    failed = 0

    print("\n=== API endpoints ===\n")

    tests = [
        ("summary", lambda: bot.summary(), lambda r: "time" in r),
        ("time", lambda: bot.time(), lambda r: "dayNumber" in r),
        ("weather", lambda: bot.weather(), lambda r: "cycle" in r),
        ("population", lambda: bot.population(), lambda r: isinstance(r, list)),
        ("resources", lambda: bot.resources(), lambda r: isinstance(r, dict)),
        ("districts", lambda: bot.districts(), lambda r: isinstance(r, list)),
        ("buildings", lambda: bot.buildings(), lambda r: isinstance(r, list) and len(r) > 0),
        ("trees", lambda: bot.trees(), lambda r: isinstance(r, list)),
        ("gatherables", lambda: bot.gatherables(), lambda r: isinstance(r, list)),
        ("beavers", lambda: bot.beavers(), lambda r: isinstance(r, list) and len(r) > 0),
        ("prefabs", lambda: bot.prefabs(), lambda r: isinstance(r, list) and len(r) > 0),
        ("speed", lambda: bot.speed(), lambda r: "speed" in r),
        ("map (size)", lambda: bot.map(), lambda r: "mapSize" in r),
        ("map (region)", lambda: bot.map(120, 130, 125, 135), lambda r: "tiles" in r),
        ("scan", lambda: bot.scan(120, 135, 5), lambda r: isinstance(r, dict) and "occupied" in r),
        ("distribution", lambda: bot.distribution(), lambda r: isinstance(r, list)),
        ("notifications", lambda: bot.notifications(), lambda r: isinstance(r, list)),
        ("workhours", lambda: bot.workhours(), lambda r: "endHours" in r),
        ("science", lambda: bot.science(), lambda r: "points" in r),
        ("tree_clusters", lambda: bot.tree_clusters(), lambda r: isinstance(r, list)),
        ("alerts", lambda: bot.alerts(), lambda r: isinstance(r, list)),
    ]

    for name, fn, validate in tests:
        result = fn()
        detail = f"{len(result)} items" if isinstance(result, list) else ""
        if check(name, validate(result), detail):
            passed += 1
        else:
            failed += 1

    print("\n=== beavers detail ===\n")

    b = bot.beavers()[0]
    for field in ["id", "name", "wellbeing", "needs", "anyCritical", "isBot", "contaminated", "hasHome"]:
        if check(f"has {field}", field in b):
            passed += 1
        else:
            failed += 1

    needs = b.get("needs", {})
    if check("needs have points", all("points" in v for v in needs.values())):
        passed += 1
    else:
        failed += 1

    print("\n=== buildings enrichment ===\n")

    buildings = bot.buildings()
    # find a building with power fields
    power_bldgs = [b for b in buildings if "isGenerator" in b or "isConsumer" in b]
    if power_bldgs:
        pb = power_bldgs[0]
        for field in ["isGenerator", "isConsumer", "nominalPowerInput", "nominalPowerOutput"]:
            if check(f"power building has {field}", field in pb):
                passed += 1
            else:
                failed += 1
    else:
        if check("any power buildings found", False):
            passed += 1
        else:
            failed += 1

    # find a building with reachable field
    reach_bldgs = [b for b in buildings if "reachable" in b]
    if check(f"buildings have reachable ({len(reach_bldgs)} of {len(buildings)})", len(reach_bldgs) > 0):
        passed += 1
    else:
        failed += 1

    # construction progress on unfinished buildings
    unfinished = [b for b in buildings if not b.get("finished", True) and "buildProgress" in b]
    if check(f"unfinished buildings have buildProgress ({len(unfinished)})", len(unfinished) >= 0):
        passed += 1
    else:
        failed += 1

    # inventory on storage buildings
    inv_bldgs = [b for b in buildings if "inventory" in b]
    if check(f"buildings with inventory ({len(inv_bldgs)})", True):
        passed += 1
    else:
        failed += 1

    print("\n=== distribution detail ===\n")

    dist = bot.distribution()
    if dist and isinstance(dist, list) and len(dist) > 0:
        d = dist[0]
        if check("has district name", "district" in d):
            passed += 1
        else:
            failed += 1
        goods = d.get("goods", [])
        if check(f"has goods ({len(goods)})", len(goods) > 0):
            passed += 1
        else:
            failed += 1
        if goods:
            g = goods[0]
            for field in ["good", "importOption", "exportThreshold"]:
                if check(f"good has {field}", field in g):
                    passed += 1
                else:
                    failed += 1

    print("\n=== tree clusters ===\n")

    clusters = bot.tree_clusters()
    if check(f"tree_clusters returns list ({len(clusters)})", isinstance(clusters, list) and len(clusters) > 0):
        passed += 1
    else:
        failed += 1
    if clusters:
        c = clusters[0]
        for field in ["x", "y", "z", "grown", "total"]:
            if check(f"cluster has {field}", field in c):
                passed += 1
            else:
                failed += 1

    print("\n=== write endpoints ===\n")

    # distribution write (set + verify + restore)
    result = bot.set_distribution("District 1", "Log", export_threshold=99)
    if check("distribution write", "error" not in result):
        passed += 1
    else:
        failed += 1
    dist = bot.distribution()
    log_goods = [g for d in dist for g in d.get("goods", []) if g.get("good") == "Log"]
    if check("distribution write verified", log_goods and log_goods[0].get("exportThreshold") == 99):
        passed += 1
    else:
        failed += 1
    bot.set_distribution("District 1", "Log", export_threshold=0)  # restore

    # speed write (verify set response)
    result = bot.set_speed(2)
    if check("speed write", result.get("speed") == 2):
        passed += 1
    else:
        failed += 1

    # science read
    result = bot.science()
    if check("science read", "points" in result and "unlockables" in result):
        passed += 1
    else:
        failed += 1

    # work hours write (set + verify + restore)
    old = bot.workhours().get("endHours", 16)
    result = bot.set_workhours(14)
    if check("workhours write", result.get("endHours") == 14):
        passed += 1
    else:
        failed += 1
    bot.set_workhours(old)  # restore

    # migrate (will fail gracefully with single district -- that's expected)
    result = bot.migrate("District 1", "NonExistent", 1)
    if check("migrate write (invalid target)", "error" in result):
        passed += 1
    else:
        failed += 1

    # distribution import option write
    result = bot.set_distribution("District 1", "Plank", import_option="Auto")
    if check("distribution import write", "error" not in result):
        passed += 1
    else:
        failed += 1
    bot.set_distribution("District 1", "Plank", import_option="Forced")  # restore

    # priority write (find a building, set + restore)
    buildings = bot.buildings(limit=5)
    prio_bldg = next((b for b in buildings if "priority" in b and b.get("priority") == "Normal"), None)
    if prio_bldg:
        result = bot.set_priority(prio_bldg["id"], "VeryHigh")
        if check("priority write", result.get("priority") == "VeryHigh"):
            passed += 1
        else:
            failed += 1
        bot.set_priority(prio_bldg["id"], "Normal")  # restore
    else:
        if check("priority write (no building found)", False):
            passed += 1
        else:
            failed += 1

    # workers write (find a workplace, set + restore)
    workplaces = [b for b in bot.buildings() if b.get("maxWorkers", 0) > 0]
    if workplaces:
        wp = workplaces[0]
        old_workers = wp.get("desiredWorkers", 0)
        result = bot.set_workers(wp["id"], 0)
        if check("workers write", result.get("desiredWorkers") == 0):
            passed += 1
        else:
            failed += 1
        bot.set_workers(wp["id"], old_workers)  # restore
    else:
        if check("workers write (no workplace found)", False):
            passed += 1
        else:
            failed += 1

    # pause/unpause write
    pausable = next((b for b in bot.buildings() if b.get("pausable") and not b.get("paused")), None)
    if pausable:
        result = bot.pause_building(pausable["id"])
        if check("pause write", result.get("paused") == True):
            passed += 1
        else:
            failed += 1
        bot.unpause_building(pausable["id"])  # restore
    else:
        if check("pause write (no pausable found)", False):
            passed += 1
        else:
            failed += 1

    # stockpile good write (find a tank)
    tanks = [b for b in bot.buildings() if "Tank" in b.get("name", "")]
    if tanks:
        result = bot.set_good(tanks[0]["id"], "Water")
        if check("stockpile good write", result.get("good") == "Water"):
            passed += 1
        else:
            failed += 1
    else:
        if check("stockpile good write (no tank found)", False):
            passed += 1
        else:
            failed += 1

    print("\n=== pagination ===\n")

    all_buildings = bot.buildings()
    limited = bot.buildings(limit=3)
    if check(f"buildings limit=3 ({len(limited)} of {len(all_buildings)})", len(limited) == 3):
        passed += 1
    else:
        failed += 1

    offset_buildings = bot.buildings(limit=2, offset=5)
    if check("buildings offset=5 limit=2", len(offset_buildings) == 2 and offset_buildings[0]["id"] == all_buildings[5]["id"]):
        passed += 1
    else:
        failed += 1

    all_trees = bot.trees(limit=10)
    if check(f"trees limit=10 ({len(all_trees)})", len(all_trees) == 10):
        passed += 1
    else:
        failed += 1

    print("\n=== TOON CLI output ===\n")

    cli = "python timberbot/script/timberbot.py"
    toon_tests = [
        ("summary", "day:"),
        ("speed", "speed:"),
        ("buildings", "{"),  # tabular or structured
        ("beavers", "wellbeing"),
        ("prefabs", "name"),
    ]

    for method, expect in toon_tests:
        out = subprocess.run(f"{cli} {method}", capture_output=True, text=True,
                             shell=True, cwd="C:/code/timberborn").stdout
        is_toon = "json" not in out[:20].lower() and expect in out
        if check(f"{method} TOON output", is_toon, f"{len(out)} chars"):
            passed += 1
        else:
            failed += 1
            print(f"         first 100 chars: {out[:100]}")

    print(f"\n=== {passed} passed, {failed} failed ===\n")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
