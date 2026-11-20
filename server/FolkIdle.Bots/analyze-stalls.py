"""Where did the bots get stuck? Reads a bot run directory and writes stalls.md.

    python server/FolkIdle.Bots/analyze-stalls.py bot-runs/<run> [--hours 6]

A STALL is a stretch of at least --hours game hours (default 6) in which none
of the progress measures moved: level, highest region, highest location,
best dungeon stage, Ascension steps, Forge level, Town Hall level or rebirths.
It is reported with what the bot was doing (activity id, food, deaths gained,
gold change) and the decisions it logged in that window (events.log), grouped
by kind, so the question "is this the game or the bot?" can be answered from
the file without reading the whole log.

Modul: GAME HOURS FROM timeline.csv, which MilestoneLog writes once an hour
while a bot is online. Offline time (casual, idle, the night's sleep) has no
rows, so a gap between two rows is not a stall: it is time the server's
offline catch-up decided. A stretch is only counted across ONLINE rows, and
its length is the sum of the gaps of at most 1.5 game hours.

Also writes per-persona curves: the median level at every 12 game hours.
"""
from __future__ import annotations

import csv
import re
import statistics
import sys
from collections import Counter, defaultdict
from pathlib import Path

COLUMNS = ["bot", "hours", "level", "xp", "region", "location", "gold", "diamonds", "activity",
           "food", "deaths", "forge", "townhall", "rebirths", "dungeon", "ascension"]
PROGRESS = ["level", "region", "location", "dungeon", "ascension", "forge", "townhall", "rebirths"]
EVENT = re.compile(r"^\s*([0-9.]+)h\s+(\S+)\s+(.*)$")


def persona_of(bot: str) -> str:
    return re.sub(r"\d+$", "", bot)


def load_timeline(run: Path) -> dict[str, list[dict]]:
    rows: dict[str, list[dict]] = defaultdict(list)
    with open(run / "timeline.csv", newline="", encoding="utf-8") as f:
        for raw in csv.reader(f):
            if not raw or raw[0] == "bot":
                continue
            r = dict(zip(COLUMNS, raw))
            for k in COLUMNS[1:]:
                r[k] = float(r[k]) if k == "hours" else int(float(r[k]))
            rows[r["bot"]].append(r)
    for bot in rows:
        rows[bot].sort(key=lambda r: r["hours"])
    return rows


def load_events(run: Path) -> dict[str, list[tuple[float, str]]]:
    events: dict[str, list[tuple[float, str]]] = defaultdict(list)
    path = run / "events.log"
    if not path.exists():
        return events
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = EVENT.match(line)
            if m:
                events[m.group(2)].append((float(m.group(1)), m.group(3)))
    return events


def kind_of(text: str) -> str:
    """The first words of a decision, digits stripped, so repeats group together."""
    words = re.sub(r"[0-9][0-9.,]*", "#", text).split()
    return " ".join(words[:4])


def stalls(rows: list[dict], min_hours: float) -> list[tuple[dict, dict, float]]:
    found = []
    start = rows[0] if rows else None
    online = 0.0
    for prev, cur in zip(rows, rows[1:]):
        gap = cur["hours"] - prev["hours"]
        moved = any(cur[k] != start[k] for k in PROGRESS)
        if moved or gap > 1.5:
            if online >= min_hours:
                found.append((start, prev, online))
            start, online = cur, 0.0
            continue
        online += gap
    if rows and online >= min_hours:
        found.append((start, rows[-1], online))
    return found


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    run = Path(sys.argv[1])
    min_hours = float(sys.argv[sys.argv.index("--hours") + 1]) if "--hours" in sys.argv else 6.0
    timeline = load_timeline(run)
    events = load_events(run)

    out = [f"# Stalls in {run.name}", "",
           f"A stall: at least {min_hours:g} ONLINE game hours in which none of {', '.join(PROGRESS)} moved.", ""]

    total = Counter()
    for bot in sorted(timeline):
        found = stalls(timeline[bot], min_hours)
        total[persona_of(bot)] += len(found)
        if not found:
            continue
        out.append(f"## {bot}")
        out.append("")
        for a, b, hours in found:
            gold = b["gold"] - a["gold"]
            out.append(f"- **{a['hours']:.1f}h - {b['hours']:.1f}h** ({hours:.1f} online h): level {a['level']}, "
                       f"region {a['region']}, location {a['location']}, dungeon {a['dungeon']}, ascension {a['ascension']}; "
                       f"activity {a['activity']}->{b['activity']}, deaths +{b['deaths'] - a['deaths']}, gold {gold:+,}, food {b['food']}")
            window = [t for h, t in events.get(bot, []) if a["hours"] <= h <= b["hours"]]
            kinds = Counter(kind_of(t) for t in window).most_common(6)
            if kinds:
                out.append("  - decisions: " + "; ".join(f"{k} x{n}" for k, n in kinds))
        out.append("")

    out.insert(4, "Stalls per persona: " + ", ".join(f"{p} {n}" for p, n in sorted(total.items())) + "")
    out.insert(5, "")

    # Curves: median level per persona every 12 game hours.
    by_persona: dict[str, list[list[dict]]] = defaultdict(list)
    for bot, rows in timeline.items():
        by_persona[persona_of(bot)].append(rows)
    end = max((r[-1]["hours"] for r in timeline.values() if r), default=0)
    marks = list(range(12, int(end) + 1, 12))
    if marks:
        out.append("## Median level by game hour")
        out.append("")
        out.append("| persona | " + " | ".join(f"{m}h" for m in marks) + " |")
        out.append("|---|" + "---:|" * len(marks))
        for persona, bots in sorted(by_persona.items()):
            cells = []
            for m in marks:
                levels = [max((r["level"] for r in rows if r["hours"] <= m), default=0) for rows in bots]
                cells.append(f"{statistics.median(levels):g}" if levels else "-")
            out.append(f"| {persona} | " + " | ".join(cells) + " |")
        out.append("")

    (run / "stalls.md").write_text("\n".join(out), encoding="utf-8")
    print(f"wrote {run / 'stalls.md'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
