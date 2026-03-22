"""Regression tests for placement and crop validation.

Requires a running game with the Iron Teeth day-5 save (pump at 121,133,
path column at x=120, berry bushes west, water east, DC at 124,143).

Usage:
    python timberbot/script/test_validation.py
"""
import json
import sys

from timberbot import Timberbot


def check(name, result, expect_error=None, expect_id=False,
          expect_planted=None, expect_skipped=None):
    ok = True
    if expect_error:
        if "error" not in result:
            ok = False
        elif expect_error not in str(result["error"]):
            ok = False
    if expect_id and "id" not in result:
        ok = False
    if expect_planted is not None and result.get("planted") != expect_planted:
        ok = False
    if expect_skipped is not None:
        skipped = result.get("skipped", -1)
        if callable(expect_skipped):
            if not expect_skipped(skipped):
                ok = False
        elif skipped != expect_skipped:
            ok = False

    status = "PASS" if ok else "FAIL"
    print(f"  {status}  {name}")
    if not ok:
        print(f"         got: {json.dumps(result)}")
    return ok


def main():
    bot = Timberbot()
    if not bot.ping():
        print("error: game not reachable")
        sys.exit(1)

    passed = 0
    failed = 0

    print("\n=== building placement ===\n")

    tests = [
        ("occupied tile (pump body)",
         lambda: bot.place_building("Path", 122, 133, 1),
         {"expect_error": "occupied"}),

        ("occupied tile (berry bush)",
         lambda: bot.place_building("Path", 118, 133, 2),
         {"expect_error": "occupied"}),

        ("non-water building on water",
         lambda: bot.place_building("Path", 124, 130, 1),
         {"expect_error": "water"}),

        ("path on existing path",
         lambda: bot.place_building("Path", 120, 135, 2),
         {"expect_error": "occupied"}),

        ("off map",
         lambda: bot.place_building("Path", 999, 999, 2),
         {"expect_error": "no terrain"}),

        ("unknown prefab",
         lambda: bot.place_building("Fake", 120, 130, 2),
         {"expect_error": "not found"}),

        ("multi-tile overlap (barrack on pump entrance)",
         lambda: bot.place_building("Barrack.IronTeeth", 121, 132, 2),
         {"expect_error": "occupied"}),

        ("duplicate pump",
         lambda: bot.place_building("DeepWaterPump.IronTeeth", 121, 133, 1, orientation="west"),
         {"expect_error": "occupied"}),
    ]

    for name, fn, kwargs in tests:
        result = fn()
        if check(name, result, **kwargs):
            passed += 1
        else:
            failed += 1

    # valid placement + cleanup
    result = bot.place_building("Path", 119, 127, 2)
    if check("valid placement on empty ground", result, expect_id=True):
        passed += 1
        bot.demolish_building(result["id"])
    else:
        failed += 1

    print("\n=== crop planting ===\n")

    crop_tests = [
        ("crop over buildings+paths (should skip occupied)",
         lambda: bot.plant_crop(119, 130, 122, 134, 2, "Kohlrabi"),
         {"expect_skipped": lambda s: s > 0}),

        ("crop on open ground (all planted)",
         lambda: bot.plant_crop(110, 130, 112, 132, 2, "Kohlrabi"),
         {"expect_planted": 9, "expect_skipped": 0}),

        ("crop over DC+paths (should skip many)",
         lambda: bot.plant_crop(123, 142, 126, 145, 2, "Kohlrabi"),
         {"expect_skipped": lambda s: s > 0}),

        ("crop on water (all skipped)",
         lambda: bot.plant_crop(124, 128, 128, 132, 2, "Kohlrabi"),
         {"expect_planted": 0, "expect_skipped": lambda s: s > 0}),
    ]

    for name, fn, kwargs in crop_tests:
        result = fn()
        if check(name, result, **kwargs):
            passed += 1
        else:
            failed += 1

    # cleanup crop marks
    bot.clear_planting(119, 130, 122, 134, 2)
    bot.clear_planting(110, 130, 112, 132, 2)
    bot.clear_planting(123, 142, 126, 145, 2)

    print("\n=== orientation (origin correction) ===\n")

    # test area: east of DC at y=145 z=2, flat open ground
    # place all 4 orientations for a 2x2 (FarmHouse) and 3x2 (Barrack)
    for prefab, sx, sy in [("FarmHouse.IronTeeth", 2, 2),
                               ("Barrack.IronTeeth", 3, 2),
                               ("Rowhouse.IronTeeth", 1, 2),
                               ("IndustrialLumberMill.IronTeeth", 2, 3),
                               ("WoodWorkshop.IronTeeth", 2, 4),
                               ("WeatherStation.IronTeeth", 3, 1)]:
        for orient in ["south", "west", "north", "east"]:
            bx, by = 130, 145
            result = bot.place_building(prefab, bx, by, 2, orientation=orient)
            if "id" not in result:
                if check(f"{prefab} {orient} placement", result, expect_id=True):
                    passed += 1
                else:
                    failed += 1
                continue

            # check footprint bottom-left matches user coords
            tiles = bot.map(bx - 1, by - 1, bx + sx, by + sy)
            occupied = [(t["x"], t["y"]) for t in tiles.get("tiles", [])
                        if t.get("occupant") and prefab.split(".")[0] in t["occupant"]]
            min_x = min(t[0] for t in occupied) if occupied else -1
            min_y = min(t[1] for t in occupied) if occupied else -1
            origin_ok = min_x == bx and min_y == by
            if check(f"{prefab.split('.')[0]} {orient} origin=({min_x},{min_y})",
                     result, expect_id=True):
                if origin_ok:
                    passed += 1
                else:
                    failed += 1
                    print(f"         expected bottom-left ({bx},{by}), got ({min_x},{min_y})")
            else:
                failed += 1

            bot.demolish_building(result["id"])

    # named orientation validation
    result = bot.place_building("Path", 130, 145, 2, orientation="1")
    if check("numeric orientation rejected", result, expect_error="invalid orientation"):
        passed += 1
    else:
        failed += 1

    result = bot.place_building("Path", 130, 145, 2, orientation="bogus")
    if check("invalid orientation name rejected", result, expect_error="invalid orientation"):
        passed += 1
    else:
        failed += 1

    print(f"\n=== {passed} passed, {failed} failed ===\n")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
