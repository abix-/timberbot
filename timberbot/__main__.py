"""CLI for Timberbot API. Run any method directly:

    python -m timberbot buildings
    python -m timberbot set_speed 3
    python -m timberbot place_building LumberjackFlag.IronTeeth 120 130 2
    python -m timberbot mark_trees 100 100 110 110 2
    python -m timberbot demolish_building 12345
"""
import json
import sys

from timberbot.api import Timberbot


def main():
    if len(sys.argv) < 2:
        bot = Timberbot()
        print("usage: python -m timberbot <method> [args...]")
        print()
        print("methods:")
        for name in sorted(dir(bot)):
            if name.startswith("_"):
                continue
            method = getattr(bot, name)
            if callable(method):
                doc = (method.__doc__ or "").split("\n")[0].strip()
                print(f"  {name:30s} {doc}")
        sys.exit(1)

    bot = Timberbot()
    # skip '--' if present (allows negative numbers)
    raw_args = sys.argv[1:]
    raw_args = [a for a in raw_args if a != "--"]
    method_name = raw_args[0]
    args = raw_args[1:]

    if not hasattr(bot, method_name):
        print(f"error: unknown method '{method_name}'", file=sys.stderr)
        sys.exit(1)

    method = getattr(bot, method_name)
    if not callable(method):
        print(json.dumps(method, indent=2))
        sys.exit(0)

    # cast args to int/float/bool/str based on content
    typed_args = []
    for a in args:
        if a.lower() == "true":
            typed_args.append(True)
        elif a.lower() == "false":
            typed_args.append(False)
        else:
            try:
                typed_args.append(int(a))
            except ValueError:
                try:
                    typed_args.append(float(a))
                except ValueError:
                    typed_args.append(a)

    result = method(*typed_args)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
