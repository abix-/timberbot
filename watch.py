"""Timberborn live dashboard -- polls Timberbot and prints colorful status."""
import sys
import time
import requests

URL = "http://localhost:8085"
POLL = 3  # seconds

# ANSI colors
RST = "\033[0m"
BOLD = "\033[1m"
DIM = "\033[2m"
RED = "\033[31m"
GRN = "\033[32m"
YEL = "\033[33m"
BLU = "\033[34m"
MAG = "\033[35m"
CYN = "\033[36m"
WHT = "\033[37m"
BRED = "\033[91m"
BGRN = "\033[92m"
BYEL = "\033[93m"
BBLU = "\033[94m"
BMAG = "\033[95m"
BCYN = "\033[96m"


def bar(pct, width=20):
    filled = int(pct * width)
    empty = width - filled
    return f"{BGRN}{'#' * filled}{DIM}{'.' * empty}{RST}"


def fetch(path):
    try:
        r = requests.get(f"{URL}{path}", timeout=3)
        return r.json()
    except Exception:
        return None


def render(data):
    if not data:
        print(f"  {RED}-- game not reachable --{RST}")
        return

    t = data.get("time", {})
    w = data.get("weather", {})
    districts = data.get("districts", [])

    day = t.get("dayNumber", 0)
    progress = t.get("dayProgress", 0)
    cycle = w.get("cycle", 0)
    cday = w.get("cycleDay", 0)
    hazardous = w.get("isHazardous", False)
    temperate_len = w.get("temperateWeatherDuration", 0)
    hazard_len = w.get("hazardousWeatherDuration", 0)
    days_left = temperate_len - cday + 1 if not hazardous else 0

    # header
    season_color = BRED if hazardous else BGRN
    season_label = "DROUGHT" if hazardous else "temperate"
    print(f"  {BOLD}{BCYN}day {day}{RST} {bar(progress)} {DIM}{progress:.0%}{RST}")
    print(f"  {season_color}{season_label}{RST} {DIM}cycle {cycle} day {cday}/{temperate_len}+{hazard_len}{RST}", end="")
    if not hazardous:
        if days_left <= 3:
            print(f"  {BRED}{BOLD}{days_left}d to drought!{RST}")
        else:
            print(f"  {DIM}{days_left}d to drought{RST}")
    else:
        remaining = temperate_len + hazard_len - cday + 1
        print(f"  {BRED}{remaining}d remaining{RST}")
    print()

    for d in districts:
        name = d.get("name", "?")
        pop = d.get("population", {})
        adults = pop.get("adults", 0)
        children = pop.get("children", 0)
        bots = pop.get("bots", 0)
        resources = d.get("resources", {})

        total = adults + children + bots
        print(f"  {BOLD}{BYEL}{name}{RST}  {BCYN}{total}{RST} pop {DIM}({adults}a {children}c{f' {bots}b' if bots else ''}){RST}")

        if resources:
            items = sorted(resources.items(), key=lambda x: -(x[1]["all"] if isinstance(x[1], dict) else x[1]))
            for good, val in items:
                if isinstance(val, dict):
                    avail = val.get("available", 0)
                    total_stock = val.get("all", 0)
                else:
                    avail = val
                    total_stock = val

                # color by type
                if "water" in good.lower():
                    color = BBLU
                elif "berr" in good.lower() or "bread" in good.lower() or "carrot" in good.lower():
                    color = BGRN
                elif "log" in good.lower() or "plank" in good.lower() or "wood" in good.lower():
                    color = BYEL
                elif "metal" in good.lower() or "gear" in good.lower() or "scrap" in good.lower():
                    color = BMAG
                else:
                    color = WHT

                carried = total_stock - avail
                carried_str = f" {DIM}(+{carried} in transit){RST}" if carried > 0 else ""
                print(f"    {color}{good:22s}{RST} {BOLD}{avail:>5}{RST}{carried_str}")
        else:
            print(f"    {DIM}(no resources){RST}")
        print()


def main():
    print(f"\n  {BOLD}{BMAG}=== Timberborn Live ==={RST}\n")

    # check connection
    ping = fetch("/api/ping")
    if not ping:
        print(f"  {RED}cannot reach Timberbot on port 8085{RST}")
        print(f"  {DIM}start Timberborn with the mod loaded{RST}\n")
        sys.exit(1)

    print(f"  {BGRN}connected{RST}  {DIM}polling every {POLL}s -- ctrl+c to stop{RST}\n")

    try:
        while True:
            data = fetch("/api/summary")
            # clear screen
            print("\033[2J\033[H", end="")
            print(f"\n  {BOLD}{BMAG}=== Timberborn Live ==={RST}\n")
            render(data)
            time.sleep(POLL)
    except KeyboardInterrupt:
        print(f"\n  {DIM}bye!{RST}\n")


if __name__ == "__main__":
    main()
