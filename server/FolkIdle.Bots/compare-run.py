"""Per-persona snapshot of a bot run at chosen game days.

Usage (from the repo root):
    python -I server/FolkIdle.Bots/compare-run.py bot-runs/night2-rebirth-2x2
    python -I server/FolkIdle.Bots/compare-run.py bot-runs/night2-rebirth-2x2 --days 5 10 30 60

Reads the run's timeline.csv and prints, for each persona and day, the median
and range over its bots of: level now, best level so far, best region,
rebirths, best dungeon stage, Ascension steps and gold. This is the comparison
PLAYTEST_BOTS_REPORT.md section 0.6.1 was written from, so later runs are
read the same way.
"""
import argparse
import csv
import os
import statistics
import sys


def persona_of(bot):
    return bot.rstrip("0123456789")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("run_dir")
    ap.add_argument("--days", type=float, nargs="*", default=[5, 10, 20, 30, 45, 60])
    a = ap.parse_args()

    path = os.path.join(a.run_dir, "timeline.csv")
    if not os.path.exists(path):
        sys.exit(f"no timeline.csv in {a.run_dir}")
    rows = list(csv.DictReader(open(path, encoding="utf-8")))
    if not rows:
        sys.exit("timeline.csv is empty")
    end_hours = max(float(r["game_hours"]) for r in rows)
    bots = sorted({r["bot"] for r in rows})
    personas = sorted({persona_of(b) for b in bots})

    def snapshot(bot, hours):
        rs = [r for r in rows if r["bot"] == bot and float(r["game_hours"]) <= hours]
        if not rs:
            return None
        last = rs[-1]
        return {
            "level": int(last["level"]),
            "best_level": max(int(r["level"]) for r in rs),
            "region": max(int(r["region"]) for r in rs),
            "rebirths": int(last["rebirths"]),
            "dungeon": int(last["dungeon_best"]),
            "ascension": int(last["ascension_steps"]),
            "gold_m": int(last["gold"]) / 1e6,
        }

    def cell(values, fmt="{:.0f}"):
        if not values:
            return "-"
        med = statistics.median(values)
        lo, hi = min(values), max(values)
        return fmt.format(med) if lo == hi else f"{fmt.format(med)} ({fmt.format(lo)}-{fmt.format(hi)})"

    print(f"# {a.run_dir}: {len(bots)} bots, {end_hours:.0f} game hours ({end_hours / 24:.1f} days)")
    print("median (min-max) over each persona's bots\n")
    keys = [("level", "{:.0f}"), ("best_level", "{:.0f}"), ("region", "{:.0f}"), ("rebirths", "{:.0f}"),
            ("dungeon", "{:.0f}"), ("ascension", "{:.0f}"), ("gold_m", "{:.1f}")]
    for day in [d for d in a.days if d * 24 <= end_hours + 1] + [round(end_hours / 24, 1)]:
        print(f"## day {day}")
        print("| persona | n | " + " | ".join(k for k, _ in keys) + " |")
        print("|---|---|" + "---|" * len(keys))
        for p in personas:
            snaps = [s for s in (snapshot(b, day * 24) for b in bots if persona_of(b) == p) if s]
            print(f"| {p} | {len(snaps)} | " + " | ".join(cell([s[k] for s in snaps], f) for k, f in keys) + " |")
        print()


if __name__ == "__main__":
    main()
