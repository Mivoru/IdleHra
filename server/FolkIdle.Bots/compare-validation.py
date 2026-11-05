"""Lockstep validation: does a lockstep run play the same game as the paced reference?

Usage: compare-validation.py REF_BOTS_DIR OTHER_BOTS_DIR [OTHER_BOTS_DIR ...]

Each directory is a bot run's --out folder (milestones.csv + run.json). The
first is the reference (paced x20). Exit code 0 = valid comparison, 2 = the
runs cannot be compared (the report says why), 1 = usage or file error.

Night 2026-11-04 showed what goes wrong without these checks: the reference
had 5 of 20 bots die on a 429 at login and 8 more stuck at level 1-5, and the
lockstep runs started inside the sleepers' 00:30-06:30 core, four game hours
"late". The old report compared all of that as if it were progression and
called 78 of 88 medians "OUT". So, in order:

1. VALIDITY. Every run must have started every bot it expected, lost none,
   and played the same mix, seed, length, sleep rule and game start time
   (within START_TOLERANCE_HOURS). Otherwise the comparison is INVALID.
2. START. Each bot's milestones count from ITS OWN first state frame (the
   login that really went through), not from the run's start. The start
   offsets are reported separately.
3. BROKEN BOTS. A bot that ends far below its persona's median level is
   reported as broken and kept out of the progression comparison - its
   problem is a bot or harness defect, not the game's pacing.
4. PROGRESSION. Per persona and milestone: median hours since login, n, the
   ratio to the reference and whether it lies inside the reference's
   bot-to-bot spread.
"""
import csv
import json
import os
import re
import statistics
import sys
from datetime import datetime

MILESTONES = ["level_5", "level_10", "level_15", "level_20", "level_25", "level_30",
              "level_40", "level_50", "level_60", "region_2_open", "boss_r1_defeated",
              "region_3_open", "boss_r2_defeated", "region_4_open", "first_death"]
# Night 2026-11-05: a paced bot decides once per 0.4-1.2 wall seconds, which
# is 8-24 game seconds at x20, so it reacts to an emptied larder later than a
# lockstep bot, and x20 bots died within minutes of their first fight (median
# 0.06 h) where lockstep ones died hours later. These milestones measure the
# bot's reaction time, not the sim: shown, but left out of the in/OUT count.
REACTION_MILESTONES = {"first_death"}
START_TOLERANCE_HOURS = 0.5
BROKEN_FRACTION = 0.5  # final level below half the persona median (all runs pooled) = broken
BROKEN_MIN_MEDIAN = 10  # a run too short to reach level 10 cannot tell slow from stuck
SAME = ["Mix", "Seed", "Hours", "NoSleep"]


def persona_of(bot):
    return re.sub(r"\d+$", "", bot)


def hours(text):
    return float(text.replace(",", "."))


def load(run_dir):
    rows = []
    with open(os.path.join(run_dir, "milestones.csv"), newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            rows.append(r)
    info_path = os.path.join(run_dir, "run.json")
    info = None
    if os.path.exists(info_path):
        with open(info_path, encoding="utf-8-sig") as f:
            info = json.load(f)
    start, final_level, hits = {}, {}, {}
    for r in rows:
        bot, h = r["bot"], hours(r["game_hours"])
        final_level[bot] = max(final_level.get(bot, 0), int(r["level"]))
        if r["milestone"] == "start":
            start.setdefault(bot, h)
    for r in rows:
        bot = r["bot"]
        if bot not in start or r["milestone"] in ("start", "end"):
            continue
        hits.setdefault(bot, {}).setdefault(r["milestone"], hours(r["game_hours"]) - start[bot])
    return {"info": info, "start": start, "final": final_level, "hits": hits}


def name_of(run_dir):
    d = run_dir.rstrip("/\\")
    return os.path.basename(os.path.dirname(d)) if os.path.basename(d) == "bots" else os.path.basename(d)


def parse_utc(text):
    return datetime.fromisoformat(text.replace("Z", "+00:00"))


def validity(runs, names):
    problems = []
    for run, name in zip(runs, names):
        info = run["info"]
        if info is None:
            problems.append(f"{name}: no run.json (a bot build from before 2026-11-04 cannot be validated)")
            continue
        if info["BotsStarted"] != info["BotsExpected"]:
            problems.append(f"{name}: {info['BotsStarted']} of {info['BotsExpected']} bots logged in")
        if info["BotsStopped"]:
            problems.append(f"{name}: {info['BotsStopped']} bot(s) stopped during the run")
        if info["GameHoursRun"] < info["Hours"] * 0.98:
            problems.append(f"{name}: ran {info['GameHoursRun']:.1f} of {info['Hours']} game hours")
    infos = [r["info"] for r in runs]
    if all(infos):
        ref = infos[0]
        for info, name in zip(infos[1:], names[1:]):
            for key in SAME:
                if info.get(key) != ref.get(key):
                    problems.append(f"{name}: {key} = {info.get(key)!r}, reference {ref.get(key)!r}")
            delta = abs((parse_utc(info["GameStartUtc"]) - parse_utc(ref["GameStartUtc"])).total_seconds()) / 3600
            if delta > START_TOLERANCE_HOURS:
                problems.append(f"{name}: game start {info['GameStartUtc']} is {delta:.1f} game h from the reference's {ref['GameStartUtc']}")
    return problems


def pooled_medians(runs):
    """The persona's median final level over ALL runs: a run where most bots of
    a persona are stuck (val-x20: four of five eagerkits at level 1-3) must not
    make "stuck" the yardstick."""
    by_persona = {}
    for run in runs:
        for bot, level in run["final"].items():
            by_persona.setdefault(persona_of(bot), []).append(level)
    return {p: statistics.median(v) for p, v in by_persona.items()}


def broken_bots(run, medians):
    return {bot: (level, medians[persona_of(bot)]) for bot, level in run["final"].items()
            if medians[persona_of(bot)] >= BROKEN_MIN_MEDIAN and level < BROKEN_FRACTION * medians[persona_of(bot)]}


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    dirs = sys.argv[1:]
    runs = [load(d) for d in dirs]
    names = [name_of(d) for d in dirs]

    print("# Lockstep validation\n")
    print(f"Reference: **{names[0]}**. Hours count from each bot's own first login.\n")

    print("## 1. Validity\n")
    print("| run | mode | speed | cron scale | game start (UTC) | bots started | stopped | game h |")
    print("|---|---|---|---|---|---|---|---|")
    for run, name in zip(runs, names):
        i = run["info"] or {}
        print(f"| {name} | {i.get('Mode', '?')} | {i.get('Speed', '?')} | {i.get('CronScale', '?')} | {i.get('GameStartUtc', '?')} "
              f"| {i.get('BotsStarted', len(run['start']))}/{i.get('BotsExpected', '?')} | {i.get('BotsStopped', '?')} | {i.get('GameHoursRun', 0):.1f} |")
    problems = validity(runs, names)
    print()
    if problems:
        print("**INVALID - these runs did not play the same game:**\n")
        for p in problems:
            print(f"- {p}")
        print()
    else:
        print("Valid: same mix, seed, length, sleep rule and start time; every bot logged in and none stopped.\n")

    print("## 2. Start offsets (game hours from run start to each bot's first login)\n")
    print("| run | min | median | max |")
    print("|---|---|---|---|")
    for run, name in zip(runs, names):
        s = sorted(run["start"].values())
        if s:
            print(f"| {name} | {s[0]:.2f} | {statistics.median(s):.2f} | {s[-1]:.2f} |")
    print("\nA difference here is a start-time difference, not a progression difference; section 4 has it removed.\n")

    print("## 3. Broken bots (final level below half the persona median over all runs)\n")
    excluded = []
    any_broken = False
    medians = pooled_medians(runs)
    for run, name in zip(runs, names):
        broken = broken_bots(run, medians)
        excluded.append(set(broken))
        for bot, (level, median) in sorted(broken.items()):
            any_broken = True
            print(f"- {name}: **{bot}** ended at level {level} (persona median {median:g})")
    if not any_broken:
        print("None.")
    print("\nBroken bots are left out of section 4: their numbers describe a harness or bot defect.\n")
    if any_broken:
        problems.append("broken bots present (section 3)")

    print("## 4. Progression (median game hours since login; healthy bots only)\n")
    personas = sorted({persona_of(b) for run in runs for b in run["start"]})
    agree = total = 0
    for persona in personas:
        print(f"### {persona}\n")
        print("| milestone | " + " | ".join(f"{n} (n)" for n in names) + " | " + " | ".join(f"{n} / ref" for n in names[1:]) + " |")
        print("|" + "---|" * (2 * len(names)))
        for m in MILESTONES:
            meds = []
            for run, skip in zip(runs, excluded):
                vals = [h[m] for bot, h in run["hits"].items() if persona_of(bot) == persona and bot not in skip and m in h]
                meds.append((statistics.median(vals) if vals else None, vals))
            if all(x[0] is None for x in meds):
                continue
            cells = [f"{med:.2f} ({len(v)})" if med is not None else "- (0)" for med, v in meds]
            ref_med, ref_vals = meds[0]
            ratios = []
            for med, _ in meds[1:]:
                if med is None or not ref_med:
                    ratios.append("-")
                    continue
                inside = min(ref_vals) <= med <= max(ref_vals)
                if m in REACTION_MILESTONES:
                    ratios.append(f"{med / ref_med:.2f} (reaction, not counted)")
                    continue
                total += 1
                agree += inside
                ratios.append(f"{med / ref_med:.2f} {'in' if inside else 'OUT'}")
            print(f"| {m} | " + " | ".join(cells) + " | " + " | ".join(ratios) + " |")
        print()

    verdict = "INVALID" if problems else "VALID"
    print(f"**Verdict: {verdict}.** {agree} of {total} comparable medians fall inside the reference's spread.")
    return 2 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
