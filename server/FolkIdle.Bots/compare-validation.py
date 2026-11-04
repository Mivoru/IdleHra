"""Phase 3 validation: milestone medians per persona, run against run.

Usage: compare-validation.py RUN_DIR [RUN_DIR ...]   (each holds milestones.csv)
The first run is the reference (paced x20). For every persona and milestone
it prints the median game hours, how many bots reached it, and each other
run's median as a ratio to the reference, plus whether the other run's
median falls inside the reference's bot-to-bot spread (min..max).
"""
import csv
import os
import re
import statistics
import sys

MILESTONES = ["level_5", "level_10", "level_15", "level_20", "level_25", "level_30",
              "level_40", "region_2_open", "boss_r1_defeated", "region_3_open", "first_death"]


def load(run_dir):
    rows = {}
    path = os.path.join(run_dir, "milestones.csv")
    with open(path, newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            persona = re.sub(r"\d+$", "", r["bot"])
            hours = float(r["game_hours"].replace(",", "."))
            per_bot = rows.setdefault(persona, {}).setdefault(r["milestone"], {})
            per_bot.setdefault(r["bot"], hours)  # first time each bot hit it
    return rows


def main():
    runs = sys.argv[1:]
    data = [load(r) for r in runs]
    names = [os.path.basename(os.path.dirname(r.rstrip("/\\"))) or r for r in runs]
    print("# Lockstep validation\n")
    print("Reference: " + names[0] + ". Ratio = other median / reference median; "
          "'in' = inside the reference's min..max across bots.\n")
    personas = sorted(set().union(*[d.keys() for d in data]))
    agree = total = 0
    for persona in personas:
        print(f"## {persona}\n")
        header = "| milestone | " + " | ".join(f"{n} median (n)" for n in names) + " | " + \
                 " | ".join(f"{n} / ref" for n in names[1:]) + " |"
        print(header)
        print("|" + "---|" * (1 + len(names) + len(names) - 1))
        for m in MILESTONES:
            cells, meds = [], []
            for d in data:
                hits = list(d.get(persona, {}).get(m, {}).values())
                med = statistics.median(hits) if hits else None
                meds.append((med, hits))
                cells.append(f"{med:.2f} ({len(hits)})" if med is not None else "- (0)")
            if all(x[0] is None for x in meds):
                continue
            ref_med, ref_hits = meds[0]
            ratios = []
            for med, _ in meds[1:]:
                if med is None or ref_med is None or ref_med == 0:
                    ratios.append("-")
                    continue
                inside = min(ref_hits) <= med <= max(ref_hits)
                total += 1
                agree += inside
                ratios.append(f"{med / ref_med:.2f} {'in' if inside else 'OUT'}")
            print(f"| {m} | " + " | ".join(cells) + " | " + " | ".join(ratios) + " |")
        print()
    print(f"**Summary:** {agree} of {total} comparable medians fall inside the reference's spread.")


if __name__ == "__main__":
    main()
