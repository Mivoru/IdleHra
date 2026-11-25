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

### Run C (Village 2 + Shrine)

2026-11-21/22, main at 3b97994f (prod 1.0.2038 / client 1.116.0): Village 2
(Town Hall and Workshop to 10, other buildings to 20, Forge to 14, endgame
levels 20 M gold doubling), the Shrine (Town Hall 10, 1 B x 1.5^level),
4 Demonic -> Godly and 5 Godly -> Transcendent fusion, quiet reroll (#474),
multi-ticket sweep server side (#475; the bot still sent one ticket per sweep
in this run). 168 game hours in 123 wall minutes, all 10 bots ran to the end.
Run folder: `bot-runs/measure-c-20261121-2302` in the measurement worktree.

| Milestone | optimal | casual | idle |
|---|---:|---:|---:|
| Level 10 | 1.9 | 16 | 48 |
| Region 1 boss | 8.8 | 24 | 72 |
| Level 30 | 14 | 32 | 72 |
| Region 2 boss (dungeons) | 27 | 56 | 132 |
| Level 50 | 48 | 60 | 144 |
| Level 60 | 74 | 104 | - |
| Region 3 boss | 114 (2 of 4) | 160 (1 of 4) | - |
| First rebirth | 138 (4 of 4) | - | - |
| Level at the end of the week | 37-63 (reborn once) | 68-72 | 51, 52 |

| Persona | 12h | 24h | 48h | 72h | 96h | 120h | 156h |
|---|---:|---:|---:|---:|---:|---:|---:|
| optimal | 26.5 | 37 | 49.5 | 57.5 | 63 | 65.5 | 68 |
| casual | 6 | 26.5 | 45 | 54 | 59 | 63.5 | 68.5 |
| idle | 1 | 5.5 | 18.5 | 31 | 41.5 | 47.5 | 50.5 |

The curve is run B's within a level or two at every column (casual a little
faster on day 1-2, optimal a level or two lower on day 5-7 because it
rebirthed six hours earlier). Village 2 did not move the pace in week 1, as the answers below
explain.

**Village 2 pace.** Town Hall at the end of the week: optimal 6, 6, 6, 5;
casual 3, 3, 3, 3; idle 1, 1. Other buildings: optimal 7-9 (Forge 7-9, one
Workshop 6), casual 1-3, idle 0-2. Three of four optimal bots bought Town
Hall 6 (the first endgame level, 20 M gold + 1,000 acacia and sulfur) at
95.5-99.5 h; one also bought Workshop 6 (20 M) at 143.6 h. **No bot ever
sent Town Hall 7** (40 M + 1,500 materials). Gold is the binding cost for
optimal: it ends the week with 0.7-10 M because fusion takes 10-34 M of it and
the rebirth wipes the rest. Casual is bound by its check-ins, not by gold:
it holds 37-68 M at the end and only reaches Town Hall 3 at 144 h (18 upgrades
a week). Nothing maxed on day 1; nothing past level 9 was reached by anyone.
By the measure's own rule ("a level never reached in a week is fine") the
endgame prices are not too cheap.

**Shrine.** Not reached. It needs Town Hall 10, which is 40 + 80 + 160 +
320 = 600 M gold past Town Hall 6, against a week's total income of 50-65 M
per optimal bot. For a bot-paced account it is month-plus content. For
production it is a different story: the top account held ~15 B (Open
findings), so it can buy Town Hall 7-10 and several Shrine levels the day it
looks. The bots cannot answer whether the Shrine is a real sink for the
existing rich accounts; production's `gold_spend_daily` (category 3) after a
week of 1.0.2038 can.

**Gold flow (week totals, million gold).**

| Player id (persona) | income | of it combat | spent | reroll | fusion | village |
|---|---:|---:|---:|---:|---:|---:|
| 1 (optimal1) | 64.6 | 54.5 | 50.5 | 0.1 | 10.3 | 40.1 |
| 4 (optimal4, no Town Hall 6) | 52.0 | 44.3 | 35.1 | 0.7 | 34.3 | 0.1 |
| 10 (optimal) | 49.7 | 42.0 | 45.2 | 0.2 | 24.8 | 20.1 |
| 11 (optimal) | 63.3 | 51.7 | 44.5 | 1.4 | 23.0 | 20.1 |
| 6-9 (casual) | 50.5-75.7 | 44.8-67.2 | 4.4-16.1 | 0.8-2.7 | 2.8-15.0 | 0.0 |
| 2, 5 (idle) | 7.3, 9.0 | 6.5, 7.9 | 0.1, 0.8 | 0-0.8 | 0 | 0 |

End balances (timeline.csv): optimal 0.7-10.4 M, casual 37-68 M, idle 6.0
and 9.4 M. Optimal's income minus spend is larger than its end balance
because the rebirth wipes gold and charges the keep price, which the spend
ledger does not record. (Player ids are the measure DB's; 10 and 11 are
optimal2 and optimal3 in some order.) Against runs A and B: optimal end gold fell from
0.7-70 M (A) and 0.7-18 M (B) to 0.7-10 M, now with the village as the second
sink after fusion. Casual is unchanged at 37-68 M (B: 24-59 M) and never
spends on the village past its Town Hall ceiling.

**Top fusions.** No fusion to Godly or Transcendent, as expected (no fusion
to Demonic either). Godly items exist anyway: casual bots wear tier-13 drops
(casual8 from 32 h), and hold six to ten Demonic-or-better pieces each.

**Region 3-4 wall.** `analyze-stalls.py` finds 11 stalls (run B: 6, which
the text above rounds to 5), all optimal, all 6-9 online hours at level
61-70 in region 3-4, same shape as B. The village and the Shrine did not
touch it: Town Hall 6 has no combat effect, and the Shrine was not reached.
C's optimal bots sat at Town Hall 6 from ~100 h with nothing to buy next,
and the stall rule counts a Town Hall level as progress, so some of the extra
stalls are the village going quiet rather than combat getting harder. In
every stall the bot fought monster 95 (Alpha Wolf, the region-1
boss) at level 61-70: the bot's `ChooseMonster` takes the most XP/h among the
fights the server projects as holding, so for these characters the region-1
boss out-earns every region-3 monster they survive. That is either the
projection or the region-3 XP curve; it was the same in B. Tuning stays the
owner's call.

**Regressions.** None. Refusals per bot: optimal 9/7/9/7 (B 7/9/11/9),
casual 1 each, idle 0. No village refusal codes (MaxTierReached,
TownHallCeilingReached) appear. `server.log` has 8 lines at error level, all
"transient failure" database retries under lockstep load (6 village
production rollbacks re-queued, 2 aborted stack fusions); no unhandled
exception.

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

### Run D (half XP two regions down)

`measure-d-20261122-1114`, 168 game hours, the same ten bots as run C, on
the branch that adds `LowRegionXp` (a monster two or more regions below the
highest open one pays half its base character XP) and the bot fix that
sweeps every dungeon ticket (#477).

| | run C | run D |
|---|---:|---:|
| optimal stalls (6+ online h, nothing moves) | 11, all on the region-1 boss (95) | 5, on 100, 103, 104 |
| region 3 boss beaten in the week (optimal) | 2 of 4, at 106 h and 121 h | 3 of 4, at 96 h, 97 h and 110 h |
| casual region 4 open in the week | 1 of 4, at 160 h | 3 of 4, at 128-152 h |
| region 2 open (optimal, median) | 8.8 h | 8.3 h |
| region 3 open (optimal, median) | 26.6 h | 26.2 h |
| optimal median level at 12 / 24 / 48 / 72 / 96 h | 26.5 / 37 / 49.5 / 57.5 / 63 | 27 / 32 / 46.5 / 55.5 / 60.5 |
| casual median level at 24 / 72 / 156 h | 26.5 / 54 / 68.5 | 27.5 / 51.5 / 67.5 |

What it says:

- **The farm-down trap is gone.** No bot stands on the region-1 boss any
  more; the remaining stalls are on the region-2 boss once (Shadow Lynx,
  one region down, still full XP) and on region 3-4 monsters, which is the
  intended Renown wall.
- **Optimal levels a little slower, but progresses faster.** It reaches the
  same level two to three hours later in the middle game because the
  region-1 boss no longer pays double, yet it beats the region 3 boss ten
  hours sooner and one more bot gets there, because it now fights where the
  boss drops are.
- **The first day is unchanged where it counts.** Nothing is halved before
  region 3 opens; the region-open times and casual levels match run C within
  bot noise. The optimal day-1 median (37 vs 32) moved because one run-C bot
  had already reached region 3 at the 24 h sample; the ranges overlap
  (29-39 vs 30-40).
- **Optimal Ascension steps are lower** (median 0-3 vs 10 from day 3).
  Most attempts are region 1 step 1, the easiest step, "failed" with the
  boss dying in 2-8 s and no time limit. Not the XP cut:
  `BossAscensionTests.AStepClearsWhenTheBossPaysLowRegionXp` clears a step
  on a boss paying half XP. Ascension in run C mostly cleared for bots that
  already farmed the region-1 boss, so the suspect is the bot (its 30-second
  hold on the character after starting a step with no limit); its command
  result is not logged, so the next run should log it before this is tuned.

### Runs E, F and G (Ascension: the bot, then the server)

`measure-e-20261124-0036` and `measure-f-20261124-0332`, 168 game hours,
the same ten bots. E is the bot fix #482: it holds an attempt until it ends
and logs why it ended. F adds the server fix #483: a mid-session reload keeps
the armed attempt. G (`measure-g-20261124-0523`) adds the bot's logout guard
and a failure line that says how the attempt ended.

| | run D | run E (#482) | run F (+ #483) | run G (+ logout guard) |
|---|---:|---:|---:|---:|
| Ascension steps cleared (all bots) | 18 | 67 | 144 | 111 logged, 118 in the server log |
| optimal steps at the end | 1 / 1 / 5 / 3 | 10 / 10 / 18 / 20 | 10 / 20 / 13 / 19 | 18 / 10 / 27 / 20 |
| casual steps at the end | 0 / 5 / 3 / 0 | 2 / 1 / 4 / 2 | 19 / 10 / 20 / 20 | 8 / 7 / 8 / 8 |
| "failed" with no reason logged | 54 | 63 | 0 (15 logout, 3 deaths) | 12 (6 false, 2 deaths, 4 not cleared) |
| optimal stalls | 5 | 3 | 4 | 5 |
| region 4 open, optimal / casual median | 103 h / 136 h | 120 h / 152 h | 112 h / 132 h | 98 h / 136 h |

What it says:

- **The server dropped the attempt.** Every engine that writes the database
  out of band (fusion, reroll, the village, crafting, the market) ends with
  `ReloadState`. `StateReloadMerge` carried the fight across it but not the
  armed step, so the boss kept fighting at plain strength and the kill
  cleared nothing. Run E2 (100 h, a diagnostic) logged every unexplained
  failure as "armed never seen, still on the boss, no death, no halt", always
  right after a fuse, an equip or an upgrade. Real players hit the same thing
  when they start a step and then open the Forge. #483 carries the attempt,
  pinned to its character and never through a rebirth.
- **What is left is the bot.** 15 of F's 18 failures were casual bots that
  started a step in the last minutes of a check-in and logged out on it (a
  logout disarms by design). The bot now starts a step only when the hold
  fits before it leaves.
- **Run G: the ladder lags the kill.** The armed step leaves the wire the
  tick the boss dies, but `/boss-ascension` reads the database, which hears
  of the clear later. The server log has a clear for six of G's twelve
  "failures". The bot now reads the ladder once more before it calls a step
  failed. No run has tested that yet: run H (70 h) started the old bot build,
  because `measure.ps1` does not build.
- **Run G and H: another character's job ended the attempt.** The armed
  fields are account-wide, and `DisarmAscension` ran for any slot's activity
  change, so sending the second character to woodcutting dropped slot 1's
  step. Three of the five deathless uncleared attempts in G and H had a
  roster job between start and failure; the other two, casual7 region 1
  step 8 (ended on a gathering node) and optimal1 region 2 step 9, are not
  explained.
- **Casual bots cleared fewer steps in G (7-8) than in F (10-20).** The
  logout guard may be the cause, but that was not measured. The counter
  `ascension_skipped_logout` will show it in the next run.
- **The `roster job ... was refused` line is not a refusal.** The bot prints
  it whenever the command was sent.
- **Ascension is not hard for a geared character.** With the attempts
  counted honestly, casual bots finish region 1's ten steps within the week.
  Production agrees: Mivoru holds 49 of 50 steps
  (`AUDIT_AND_ENDGAME_2026_11_19.md`). Tuning it is the owner's call.
- **Milestones after a rebirth were wrong in every earlier run.** The log
  zeroed the level at the rebirth, so every `r1_level_N` mark fired at once
  on the next frame, which still carried the old level. Ignore the `r1_`
  rows of runs A-E. From F on, a mark is logged only when a frame sees it.

### Runs I and J (2026-11-25: the Ascension fix, then the reload fix)

`measure-i-20261125-0006` is main plus #485 (another character's job no longer
ends the attempt) and the bot's second ladder read. `measure-j-20261125-0257`
is main plus #485 and #486 (a reload keeps the death and victory edges, the
village's pending output and the fight counters). 168 game hours, the same ten
bots and seed; one run each, so small differences are noise.

| | run G | run I (#485) | run J (#485 + #486) |
|---|---:|---:|---:|
| Ascension steps cleared, bot log / server log | 111 / 118 | 118 / 118 | 122 / 123 |
| failed attempts | 12 | 1 (a death) | 6 (5 deaths, 1 fish order) |
| optimal steps at the end | 18 / 10 / 27 / 20 | 20 / 20 / 20 / 20 | 20 / 20 / 20 / 20 |
| casual steps at the end | 8 / 7 / 8 / 8 | 8 / 8 / 8 / 8 | 8 / 8 / 8 / 8 |
| "died to monster 0" mid-session / at login | not split (23) | 12 / 7 | 0 / 11 |
| deaths with a real killer, before / after the rebirth | 29 / 25 | 26 / 11 | 22 / 24 |
| optimal stalls | 5 | 7 | 9 |
| region 4 open, optimal / casual median | 98 h / 136 h | 97 h / 124 h | 88 h / 136 h |

- **The unexplained failures are gone.** Run J's one deathless failure is
  casual7, whose "fish when the larder runs dry" order took the character off
  the boss with an empty larder. That is the order doing its job.
- **Casual bots stop at step 8 because of the bot.** Each skipped 12-16 starts
  (`ascension_skipped_logout`): the hold does not fit in a short check-in.
- **#486 removes the phantom deaths in play.** The ones left at login are the
  bot log's own: a new session starts at death tick 0 with no killer. #486
  also fixes the log.
- **#486's extra village output is not visible in `material_flow_daily`.**
  Gathering moves hundreds of thousands of logs and ores a week between runs,
  which hides it. The unit tests are the evidence for that part.
- **Open: the hunting projection looks too optimistic after a rebirth.** A
  freshly reborn optimal bot walks into region 4 around level 41-46 with a
  full larder and dies within minutes, over and over (run J optimal1: 20
  deaths in 5 hours to Desert Crab and Ashen Basilisk; run G optimal4 did the
  same). These deaths are in G too, so neither fix caused them. Measured
  the same day (`HuntingSurvivalHarness`: the ten run J characters, attributes
  at 100/80/60/45%, every monster the projection says holds with food, 20
  seeded live hours each):
  - 665 fights; 23 killed at least one seed, 19 killed half or more. All 23 are
    fights that need food. No "holds without food" verdict (420) was wrong.
  - The marker is one monster crit against the auto-eat line. Every bot eats at
    the default 30%. Of the 23 fights whose crit takes 30% of the bar or more,
    22 were deadly; of the other 642, one (3 of 20 seeds). A bar just above
    the line is not fed, and the next crit empties it. The projection swings
    for the mean, so it never sees that.
  - Eating at 50% instead saves 20 of those 23; at 70%, 21. The projection then
    also accepts riskier fights, and 14 (50%) and 13 (70%) fights are deadly,
    none with a crit under 40% of the bar.
  - **Done (owner, 2026-11-25):** the advisor refuses a food fight when one
    monster crit takes the auto-eat share (at most 50%) of the bar. The rule had
    no false alarm in any of the three sets (one of its fights killed only
    from a bar that started at 40%); at 50% it catches 11 of the 14 deadly.
  - **Held: the default auto-eat share at 50%.** Approved, then measured: the
    boss walls are calibrated for a character eating at 30%
    (`BossGearBenchmark`). At 50%, eight wall tests fail: regions 2, 4 and 5
    lose their margin or fall to gear two tiers under the requirement, and
    the seasonal tier 6 falls to region 5's wall gear. A player can set 50%
    by hand today, so the walls are softer than the tests say. Either the
    walls are retuned first or the softer walls are accepted.

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
  dry" is the fix the game already has, but it opened at level 20.
  **Done:** #470 opens the first Order from level 1 (live since 2026-11-21);
  the bots set it from the first check-in, idle bots die 0 times in the first
  three days in runs C and D, casual bots 0 in run D.

- **Late gold has no sink.** 11-70 M gold per optimal bot after a week; prod's
  top account 15 B. A balance decision for the owner, not tuned here.
  Run C (Village 2 + Shrine): optimal now spends it (Town Hall 6 at 20 M,
  end balance 0.7-10 M) and never reaches Town Hall 7 or the Shrine in a
  week; whether the Shrine drains the rich production accounts is a
  production measurement, not a bot one.
- **Optimal bots farm the region-1 boss at level 61-70** (runs B and C): the
  most XP/h among fights the server projects as holding. Investigated
  2026-11-22: the projection is faithful (HuntingProjection prices the live
  tick, parity tests), and `monsters.json` is loaded as written. The cause is
  the content: **XP per monster health point falls with the region**, and
  gold per HP falls with it.

  | Region | XP / HP | gold / HP | armour |
  |---|---:|---:|---:|
  | 1 (91-95) | 0.200 | 0.049 | 10 |
  | 2 (96-100) | 0.147 | 0.037 | 20 |
  | 3 (101-105) | 0.136 | 0.034 | 30 |
  | 4 (106-110) | 0.127 | 0.032 | 40 |
  | 5 (111-115) | 0.118 | 0.029 | 50 |

  Every monster in a region has the same ratio, the boss included. So a
  character that needs several swings per kill earns most per hour on the
  lowest region it can farm quickly, and the region-1 boss (most XP per
  kill at region 1's ratio, lowest armour, boss-only damage bonuses on top)
  beats every region-3 monster. A character that one-shots everything is
  paid per kill instead, so it farms the biggest monster: production's three
  main characters all stand on Malakor (115). The trap is the middle game,
  where a character neither one-shots region 3 nor beats its boss, which is
  exactly where the bots stall. Fix options (owner decision): make XP and
  gold per HP rise with the region, or at least stay flat; this inflates
  XP for the Malakor farmers unless their per-kill XP is held.
  **Done 2026-11-22 (option A):** a monster two or more regions below the
  highest open region pays half its base XP (`LowRegionXp`); run D cut the
  optimal stalls from 11 to 5 and nobody farms 95 any more.
- **Shrine in production (2026-11-22, one day after 1.0.2038):** village
  gold spent 120 M in total (Town Hall levels), no Shrine level bought. The
  richest account holds 17.9 B at Town Hall 7. Re-read after a week.
- **Region 3 boss is the week-1 wall.** Optimal bots spent 6-8 online hours
  each at level 61-70 in region 3 with nothing moving except gold (the stall
  report). Run B confirms it without the Town Hall loop: 5 stalls of 6-8
  online hours, all at level 65-74 in region 3-4, and only 2 of 4 optimal
  bots beat the region 3 boss in the week.
- **Bots do not play situational builds** beyond binding one per activity, and
  never respec for gold. Real players don't either (task 130 data).
