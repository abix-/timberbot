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
         lambda: bot.place_building("DeepWaterPump.IronTeeth", 121, 133, 1, orientation=1),
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

    print(f"\n=== {passed} passed, {failed} failed ===\n")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
