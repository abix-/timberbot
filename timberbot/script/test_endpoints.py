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
        ("tree_clusters", lambda: bot.tree_clusters(), lambda r: isinstance(r, list)),
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
    for field in ["id", "name", "wellbeing", "needs", "anyCritical"]:
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
        for field in ["isGenerator", "isConsumer"]:
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

    # speed write (set + verify + restore)
    old_speed = bot.speed().get("speed", 0)
    bot.set_speed(1)
    result = bot.speed()
    if check("speed write", result.get("speed") == 1):
        passed += 1
    else:
        failed += 1
    bot.set_speed(old_speed)  # restore

    # science read
    result = bot.science()
    if check("science read", "points" in result and "unlockables" in result):
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
