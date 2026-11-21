# The player path: the best order to play, and how long it takes

What a player who plays well does, in what order, and how many game hours
each step takes as measured by the bots. Use it to tune: when a number here
moves after a change, the change moved the game.

- Numbers per stat: `docs/architecture/STATS_TABLES.md` (generated, cannot drift).
- Formulas and the reasons behind them: `docs/architecture/STATS_REFERENCE.md`.
- How the bots play: `server/FolkIdle.Bots/README.md`. Personas: **optimal**
  (plays most of the day, makes the best choice each time), **casual** (several
  short check-ins a day), **idle** (a few check-ins, mostly offline).

## How the hours were measured

Lockstep run (`FOLKIDLE_SIM_LOCKSTEP=1`): the server ticks exactly as live but
only as fast as the bots answer, so a game hour costs real ticks, offline time
runs through the per-swing offline simulator, and the result does not depend on
the machine. Production flags (`ops/oracle/.env` on 2026-11-20): duel world
boss, Nav on, Delve deep on, offline sim on. Mix optimal 4, casual 4, idle 2,
168 game hours (one week), seed 1. Fresh database, so no market, no guild and
a world boss nobody else hits.

Run A: 2026-11-20, main at dd7873ef (before the bot fixes #466 and #468).
Run B: 2026-11-21, with #466 (no logout mid-trip) and #468 (no capped
upgrades, no occupied nodes). Re-run with:

```powershell
# from a worktree with a Release build of server and bots, Docker up
server/FolkIdle.Bots/measure.ps1 -Repo <worktree> -Mix "optimal:4,casual:4,idle:2" -Hours 168 -Seed 1 -Tag <tag>
python server/FolkIdle.Bots/analyze-stalls.py <run folder>
```

`measure.ps1` creates the database `folkidle_measure_<tag>`, migrates it,
starts the Release server with the production flags and lockstep, runs the
bots, and stops the server. A week takes about two real hours.

## Measured milestones (median game hours, run A)

| Milestone | optimal | casual | idle |
|---|---:|---:|---:|
| Level 5 (Forge, Delve) | 1.1 | 8 | 24 |
| Level 10 (Market, world boss) | 2.3 | 16 | 48 |
| Region 1 boss, region 2 open | 6.2 | 28 | 84 |
| Level 30 | 13 | 48 | 120 |
| Region 2 boss, region 3 open (dungeons) | 28 | 64 | - |
| Level 50 (Renown) | 48 | 76 | - |
| Level 60 | 72 | 100 | - |
| Region 3 boss, region 4 open | 104 (3 of 4) | 152 (2 of 4) | - |
| First rebirth | 120 | - | - |
| Back to level 50 after the rebirth | 13 more | - | - |
| Region 4 boss (Malakor's region), Nav | not reached | not reached | not reached |
| Level at the end of the week | 55-67 (reborn once) | 68-72 | 36, 48 |

Median level by game hour:

| Persona | 12h | 24h | 48h | 72h | 96h | 120h | 156h |
|---|---:|---:|---:|---:|---:|---:|---:|
| optimal | 27 | 35.5 | 49.5 | 56.5 | 61 | 64.5 | 66.5 |
| casual | 5.5 | 18.5 | 33 | 48.5 | 59.5 | 64.5 | 69.5 |
| idle | 1 | 6 | 12.5 | 17.5 | 22.5 | 33 | 42 |

### Run B (with the bot fixes)

| Milestone | optimal | casual | idle |
|---|---:|---:|---:|
| Level 10 | 2.1 | 16 | 48 |
| Region 1 boss | 7.7 | 32 | 96 |
| Level 30 | 13 | 40 | 108 |
| Region 2 boss (dungeons) | 28 | 84 | - |
| Level 50 | 44 | 72 | 144 (1 of 2) |
| Level 60 | 72 | 104 | - |
| Region 3 boss | 112 (2 of 4) | 152 (2 of 4) | - |
| First rebirth | 144 (4 of 4) | - | - |
| Level at the end of the week | 33-64 (reborn once) | 68-71 | 46, 50 |

| Persona | 12h | 24h | 48h | 72h | 96h | 120h | 156h |
|---|---:|---:|---:|---:|---:|---:|---:|
| optimal | 27 | 36.5 | 50 | 59 | 64.5 | 68.5 | 72 |
| casual | 5 | 18 | 39.5 | 51 | 58 | 63.5 | 69 |
| idle | 1 | 5.5 | 10.5 | 20 | 29.5 | 40 | 48 |

A against B: the bot fixes removed the wasted commands (optimal refusals
104/9/30/38 in A, 7/9/11/9 in B; the 519 refused Town Hall 6 upgrades are
gone) and the curve barely moved, so the hours above are the game's pace and
not bot noise. Optimal now rebirths later (day 6, at level 70-74) because it
keeps climbing past 65 instead of looping on the Town Hall. The first-night
deaths are unchanged (every casual and idle bot at its first one or two
logins), as expected: #470 is not in this run. End gold: optimal 0.7-18 M
(spent on rerolls), casual 24-59 M.

What the shape says:

- **The first day is fast and the curve flattens at Renown.** Optimal reaches
  level 50 in two days, then needs another three for the next 15 levels. That
  is the intended wall (rebirth exists to climb it), and the bots rebirth at
  about day 5.
- **Casual catches optimal by day 5.** Offline time pays close to online time
  with the offline simulator, so a player who checks in 20 times a week loses
  little to one who plays all day. Optimal's edge is the first two days and
  the rebirth.
- **Nobody reaches region 5 or Nav in the first week.** Nav opens after
  Malakor; in practice it is week-two content.
- **Gold piles up from day 4.** Optimal bots held 11-70 million gold at the
  end with nothing worth buying; production agrees (the top account holds
  about 15 billion after spending 52 billion). Late gold has no sink that
  moves power. See "Open findings".

## The best order

These are the rules the optimal bot follows, written for a person. Gates are
in `STATS_TABLES.md`; the quest line (`QuestLineRegistry`, 17 steps) teaches
them in this order.

### Hour 0-2: region 1, level 1-10

1. Fight the best monster you beat without dying; move up as soon as the next
   one dies faster than it hurts you. Food (fish) is your health: the larder is
   the real difficulty ceiling, so fish before a push, not after a death.
   Before logging off, stock the larder for the hours away: the Combat row's
   "needs food (~N/h)" times the hours. Below level 20 a dry larder offline
   ends the night in a death (see Open findings).
2. Turn on auto-sell for commons (quest step 2) and wear every upgrade.
3. Claim the daily missions and the Chronicle (step 3); they are free XP,
   diamonds and chests.
4. Level 5: the Forge opens (fuse duplicates up a tier) and the Delve (one
   free run a day). Do the free run every day; it pays diamonds.
5. Level 10: Market and world boss. Strike the world boss every day; the duel
   pays by damage band.

### Hour 2-30: region 1 boss, region 2

6. Build the village in this order: Town Hall and Forge first (the Town Hall
   is the ceiling of every other building, and opens character slots 2 and 3
   at levels 3 and 5), then Lumberjack, Mine and Workshop, then Warehouse,
   Inn and Breeding Grounds from level 40.
7. Put characters 2 and 3 on woodcutting and mining at the best node you have
   reached. They gather the village and tool materials while the main
   character fights. One character per node.
8. Craft the tool of your region (axe, pickaxe, rod) as soon as you have the
   wood and ore; brew the defensive potion of your tier and keep the shelf
   full.
9. Reroll and fuse only gear you will wear for a region or more. Rerolling a
   region-1 item is gold thrown away by region 2.
10. Inheritance (diamonds) carries over every rebirth: buy it whenever the
    diamonds allow, cheapest level first.

### Hour 30-120: region 3, dungeons, Renown

11. Region 2 boss opens the dungeons: two tickets a day, sweep the stages you
    have cleared, push one new stage when the party wins.
12. Breed the main character with the best villager of the other sex for
    Strength and Endurance. Children grow only on their own work.
13. Skill tree: spend in War, pick one keystone (players and bots both settle
    on Ruin), and bind a build per activity (step 14) so gathering and fighting
    each wear their own.
14. At level 50 every level pays Renown. Rebirth when the next level takes
    longer than re-climbing to it: the bots do it at about level 65, day 5,
    and are back at 50 thirteen hours later with +XP below the peak.

### Week 2: region 4-5, Ascension, Nav

15. Ascension steps, region 4 and 5 bosses, then Nav (opens after Malakor):
    descend from the slot with the best expected depth and spend Souls on the
    cheapest Workshop upgrade.

## Where to tune

| To change | Lever | Where |
|---|---|---|
| First-day pace | XP curve, region-1 monster HP | `ProgressionEngine.GetRequiredXpForLevel`, `server/GameData/monsters.json` |
| The Renown wall | `RenownLevel`, rebirth rewards | `RebirthRules` |
| Casual vs optimal gap | offline cap (12 h), offline sim | `OfflineProgress*`, `FOLKIDLE_OFFLINE_SIM` |
| Region walls | boss HP/attack | `STATS_TABLES.md` §2, `monsters.json` |
| Late gold | sinks: reroll, fusion, village, Delve deep | `GOLD_ECONOMY_2026_11_13.md` |

After a change: run the measurement, compare this file's tables, and update
them in the same PR.

## Open findings (2026-11-21)

- **The first nights starve (levels 1-19).** A real character gets seven
  attribute points a level; at level 7 it has about 40 in total and 14
  armour, and eats ~240 fish an hour even against the Field Mouse. Every
  casual and idle bot logged out with a few hundred fish and died offline on
  each of its first nights. Reproduced on a bot character loaded from the run
  database: 330 fish lasted 1.4 of 8 hours (level 7 -> 10); with enough food
  the same night is level 7 -> 16. The Orders rule "fish when the larder runs
  dry" is the fix the game already has, but it opens at level 20. Proposal:
  #470 (first Order from level 1), owner decision.

- **Late gold has no sink.** 11-70 M gold per optimal bot after a week; prod's
  top account 15 B. A balance decision for the owner, not tuned here.
- **Region 3 boss is the week-1 wall.** Optimal bots spent 6-8 online hours
  each at level 61-70 in region 3 with nothing moving except gold (the stall
  report). It is also where the Town Hall loop (fixed in #468) cost the most,
  so re-read run B before tuning it.
- **Bots do not play situational builds** beyond binding one per activity, and
  never respec for gold. Real players don't either (task 130 data).
