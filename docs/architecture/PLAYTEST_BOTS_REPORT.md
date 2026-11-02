# Playtest bots: results, speed limits, and what the bot gets wrong

Written 2026-11-01. The tool is `server/FolkIdle.Bots` (how to run it is in its
README). This file records what the runs measured, what the game showed, how
far the speed can go, and where the bot itself is wrong or missing.

Production has 3 real players, so balance cannot be measured on players. The
bots are the only progression data the game has. Read every number below as
"a player who does what the screens recommend", not "the best possible player".

## 0. Update 2026-11-02: the bot plays every system, x100 validated

Sections 1-5 below describe the first bot (2026-11-01). Since then:

**The bot plays the whole game** (see `server/FolkIdle.Bots/README.md`):
village, tools, herbs and potions, forge, Workshop, Great Works, loot
chests, mail, achievements, world boss (Auto strike), the three dungeons
(telegraphs answered at human speed), the Delve, boss Ascension, characters
2 and 3, breeding, inheritance and rebirth. Every bot mistake in 4.1 is
fixed. Attributes, skill nodes and gear are now chosen with
`POST /api/v1/dev/projection/whatif` (dev only), the server's own
HuntingProjection on a copy of the payload with the change applied.

**x100 agrees with x20.** Same bot build, optimal persona, median game hours:

| Milestone | x20 (3 bots, run E) | x100 (3 bots, run D) | old bot, x20 (run C) |
|---|---|---|---|
| level 10 | 1.95 | 2.26 | 2.03 |
| level 15 | 3.89 | 4.06 | 4.68 |
| level 20 | 6.65 | 6.72 | 8.03 |
| level 25 | 10.05 | 9.87 | 11.65 |
| region-1 boss / region 2 open | 13.10 | 12.78 | 15.36 |
| level 30 | 14.39 | 14.41 | 15.50 |
| level at 24 h | 37, 38, 41 | 39, 39, 40 | 32-36 |

Both runs: 0 ticks dropped (x20: 864,083 ticks; x100: 2,592,648 ticks with
7 bots). The early levels run slightly behind at x100 (level 2 at 0.53 h
against 0.26 h), because the bot makes fewer decisions per GAME minute; from
level 15 on the two agree within the spread between bots. x200 then held
x201 effective with tick p95 0.17 ms (overnight run, section 0.3).

### 0.1 What the full bot shows (run D, x100, 72 game hours)

- **The full game is much faster than fighting alone.** Region 2 at 12.8 h
  (old bot 15.4 h), region 3 at 25.2 h, level 50 at 40.1 h, level 60 at
  66.6 h (2 of 3 bots). The old bot never left region 1 in 26 h.
- **Every system was used.** Per optimal bot in 72 h: 9-10 Ascension steps,
  6 dungeon wins and 19-24 sweeps, 20-42 fusions, 120-129 building upgrades
  sent, 6-7 tools, 70-96 potions, 7-9 Workshop pieces, 8-9 Delve runs for
  14-20 diamonds, 1-4 children bred, 2 inheritance levels. No rebirth yet:
  no bot stalled for 6 game hours, and none reached level 100.
- **Casual (3 x 20 min/day) reached level 36-39 but never region 2 in 72 h.**
  The region boss is fought only while online, and at x100 a 20-minute
  check-in is 12 wall seconds - a few decisions. Treat this as a likely x100
  artefact until a casual run at x20 confirms or refutes it.
- **One casual login paid +340,000 gold and +0 XP** for 7.7 offline hours
  at level 34-38 (both casual bots, the same login); the next login paid
  XP normally. Not investigated yet.
- **Idle stayed at level 3-4** in this run because it logged out on a fight
  that holds only with food, ran dry and died offline. Fixed after the run:
  a bot about to log out picks a fight that holds without food, and fishes
  for the time away first.

### 0.2 A server defect the bots found (owner decision)

A Postgres serialization conflict (`40001: could not serialize access due
to concurrent update`, or EF's "transient failure") inside a command's
write makes `SafeDispatchAsync` **force-disconnect the player** - seen for
`Character.ChangeActivity`, `Potions.Stock` and the village upgrade. The bot
then reconnected at once, and the old session's teardown removed the new
one (plus `RunRedisLockRenewalAsync` kicks), leaving an open socket with no
session. Seen 4 times in 7 bots x 72 h at x100; checkpoints are counted in
ticks, so x100 writes 100x as often and makes the race far more likely, but
nothing about it is specific to the speed. In production it would show as a
player dropped to the reconnect screen right after a command. Possible
fixes: retry the write on 40001 (the usual EF answer), or refuse the one
command instead of kicking the session. Not changed - production code.

### 0.3 Speed limits now

`TickPacer.MaxTickSpeed` is 200 (dev only). x100 and x200 hold with a few
bots. x1000 does not fit: a tick would get 0.1 ms, the bot's 0.4-1.2 s
command spacing (anti-cheat and throttler) would be 7-20 game minutes a
decision, and the database write rate would grow 10x again. That speed
needs a lockstep mode where the server waits for the bots, not a faster
clock.

## 1. Runs

All runs used the old starter kit of **10 fish**. Since client 1.52.1 a new
account gets 40.

| Run | Speed | Bots | Game hours | Server build |
|---|---|---|---|---|
| A | x10 | 2 optimal | 3 | #277 |
| B | x10 | 3 optimal | 24 | #278 (no scouting, wall-clock timers) |
| C | x20 | 1 optimal, 1 casual, 1 idle | 26 | #284 (GameClock, real logouts, scouting) |

### Optimal: milestones in game hours

| Milestone | B (x10, 3 bots) | C (x20) |
|---|---|---|
| level 2 | 0.23-0.28 | 0.38 |
| level 5 | 0.47-0.54 | 0.64 |
| level 10 | 1.82-1.88 | 2.03 |
| level 15 | 4.53-4.67 | 4.68 |
| level 20 | 7.71-7.99 | 8.03 |
| level 25 | 11.37-12.69 | 11.65 |
| region-1 boss / region 2 open | 6.77 / 9.50 / 14.26 | 15.36 |
| level 30 | 16.16-20.14 | 15.50 |
| level at 24 h / 26 h | 32-36 | 36 |

**x20 agrees with x10.** Every milestone falls inside run B's spread or next to
it. So the speed does not change the result, and x20 can be trusted.

### Personas (run C, with real logouts)

| | casual (8 h check-ins, 20 min) | idle (24 h check-ins, 20 min) | optimal |
|---|---|---|---|
| level 10 | 16.02 h | - | 2.03 h |
| level 20 | 24.02 h | - | 8.03 h |
| level at 26 h | **26** | **8** | 36 |
| offline per login | 7.67 h (under the 12 h cap) | 12.00 h (capped) | - |

What each login paid the casual bot: +3,173 XP (level 1 to 5), +65,180 XP and
78k gold (7 to 17), +76,464 XP and 234k gold (18 to 26).

## 2. What the game showed

1. **The starter kit closed the entrance (fixed).** Ten fish won 1-2 Field Mice
   (93-186 XP). The new account then got 0 kills without food until it
   discovered fishing on its own. The owner raised the kit to 40, which wins
   6-7 fights and reaches level 2 (#279, live as 1.0.1540).
2. **The region-2 catch-22 (open, owner decision).** A location's fishing opens
   only on the first kill THERE (`ApplyKillProgression`). Region 2's fights
   need region 2's fish. On region-1 fish every region-2 monster projected
   "you would die" (first monster: a 3.9 s kill and about 874 fish/h). No bot
   in run B ever fought in region 2, even at level 30. In run C the scouting
   bot killed Thorny Vine 7 times and opened region-2 fishing, but went back
   to region 1, because it fishes only when the larder is empty (bot gap 4.1).
   Possible fix in the game: open a region's gathering when the PREVIOUS
   region's boss falls. Measure it first with bot gap 4.1 closed.
3. **Offline pays a casual player almost as well as playing.** One hour a day
   online (three 20-minute check-ins) reached level 26 in 26 h. Playing
   without pause reached 36. Offline parity (#120) works.
4. **An idle player who leaves at level 2 barely moves.** The 12 h offline cap
   halves a once-a-day player's day, and offline at level 2 is worth little
   (+2,648 XP for 12 h). Level 8 after 26 h, against 26 for casual. Whether a
   once-a-day player should be served better (a longer cap early on, or a
   first-day boost) is an owner decision.
5. **Region 1 is long.** Level 30 at 15-20 game hours, and the region-1 boss
   fell between 6.8 h and 15.4 h depending on drops. The spread comes from
   gear luck, since the bot does not forge (gap 4.3).

## 3. How fast it can run

**Measured:**
- x10, 3 bots, 2.4 h: 845,728 ticks, exactly 100/s, 218 dropped (0.03%).
  Tick p50 0.17 ms, p99 0.41 ms.
- x20, 3 bots, 81 min: 938,848 ticks, 10 dropped. Tick p99 0.067 ms.

**x100: not run (the owner asked not to). Estimate:**
- **CPU is not the limit.** At x100 a tick gets 1 ms and costs about
  0.07-0.4 ms with a few bots. That is 7-40% of one core. A larger roster
  scales the cost linearly.
- **Windows sleeps at least about 15.6 ms.** At x100 one sleep owes about 16
  ticks. The old cap of 5 would drop most of them. The pacer's cap now scales
  with speed (`MaxCatchUpTicks x speed`, #284), so ticks run in bursts of about
  16 and none are dropped. That is fine for the simulation.
- **What gets coarse:** the snapshot (1 per wall second = 100 game seconds)
  and the bot's decisions (every 1.2-3 wall s = 2-5 game minutes). A death, an
  empty larder or a drop is seen minutes of game time late. The server keeps
  fighting meanwhile, so the cost is mostly lost minutes after a halt.
- **What does not speed up, and still matters:** the projection cache (1 wall
  minute = 100 game minutes stale), command rate limits (wall, about 1/s per
  bot), and the dungeon's swipe windows (wall).
- **Unmeasured:** checkpoint flushes are counted in ticks, so DB writes would
  be 5x the x20 rate. That is fine for a few bots, unknown for many.

**Recommendation:** stay at x20 (validated). If the owner wants faster, go to
x50 first: raise `MaxTickSpeed`, then repeat run B at x50 and accept it only
if the milestones match. Try x100 only after x50 passes. 24 game hours take
72 min at x20, 29 min at x50, 14 min at x100.

## 4. What the bot gets wrong, and how to improve it

### 4.1 Mistakes (it does something badly)

1. **It fishes only when the larder is empty AND no fight holds.** After
   scouting a new location it keeps eating region-1 fish and never fetches the
   better ones, so it never fights in region 2 (run C). **Fix:** when
   `HighestLocationReached` rises, or when the projection says the next
   region's first monster holds only with better food, go fishing at the new
   node even with food left.
2. **Skill points go to the cheapest open node, lowest row first.** This is not
   a build. It ignores what the column does and the keystones. **Fix:** score
   each candidate with `/skilltree/preview` (the After stat sheet: damage,
   health, sustain) and buy the best gain per point. Or follow one of the
   harness's predicted best builds.
3. **Attributes: requirements first, then 2:1 Might/Vigour.** It never weighs
   Finesse or Fortune against what they actually do. **Fix:** like 2, compare
   the projection's XP/h or survival before and after one point.
4. **Gear is compared on base ATK+DEF times rarity only,** exactly like the
   loot row. Affixes are ignored, so a Legendary with useless affixes beats a
   rare with lifesteal. **Fix:** compare the projection (XP/h, survival)
   before and after wearing a piece. The server already computes it.
5. **The boss fight waits for "holds without food or with food".** The boss
   is judged with the fish the bot carries, and those are always region-1
   fish. Better fish would probably open the boss earlier. Unmeasured; same
   root as 1.
6. **Refused activities are remembered forever** (`_refusedMonsters`). A node
   refused because the location was not reached yet stays banned after it
   opens. **Fix:** clear the set when `HighestLocationReached` or the region
   rises.
7. **The fishing target is a fixed 200.** **Fix:** fish for N game hours of
   the CURRENT fight's food use (the projection's `FoodPerHour`).
8. **Log artifact:** "HP 100/0" before the first full snapshot (MaxHp 0).
   Cosmetic.

### 4.2 Things the bot does not do yet, roughly by expected effect

1. **Forge**: fuse spares, reroll affixes. This is the game's main lever for
   gear past what drops, and it explains the 6.8-15.4 h boss spread.
2. **Potions**: herbalism, Alchemy, the potion shelf. Up to +15% stats per the
   herbs design.
3. **Village**: Town Hall and production buildings (materials while away).
4. **Tool upgrades** (axe, pickaxe, rod): fishing speed is the bot's food
   supply.
5. **Dungeons**: the daily tickets now refill at game speed (GameClock), so
   the dungeon can finally be measured. It needs the swipe answers, which the
   server judges against wall-time windows.
6. **World boss** strikes (daily) and **the Delve** (gold to diamonds), which
   feed **inheritance**.
7. **Breeding, rebirth, the bloodline**: the long game. It needs a persona
   that plays for weeks of game time.
8. **More characters** (slots 2-3 working other jobs), **guild, market**.
9. **Chest management** beyond auto-selling Common.
10. **Quest steps** whose actions the bot never takes (forge, village, market),
    so it claims only the few it happens to finish.

### 4.3 Measurement improvements

- **Correct game time for dropped ticks.** Read `folkidle_ticks_dropped_total`
  from `/metrics` at the end of a run and report it next to the milestones.
- **A record of the bot's choices over time.** Which monster it fought per
  game hour, food per hour, XP per hour. This would show region 1's long tail
  directly instead of through milestones.
- **Seeds.** Drops are random, so the boss time varies 2x between bots. Run 5+
  bots per persona and report medians (summary.md already does).

## 5. Next steps, in recommended order

Items 1-3 of the first list (fish at the new river, decide by the projection,
forge/potions/dungeons) are done - see section 0. Now:

1. **Owner:** the serialization-conflict kick (section 0.2).
2. Read the overnight x200 run (63 game days, 3 optimal, 2 casual, 2 idle):
   rebirth, breeding over generations, regions 4-5.
3. A casual run at x20, to tell the casual-never-reaches-region-2 result
   (0.1) from an x100 artefact; and the +0 XP offline login.
4. **Owner decisions still waiting:** the region-2 fishing unlock (2.2) is
   now moot for a bot that fetches the new fish (region 2 at 12.8 h, region 3
   at 25.2 h), and the idle player's day (2.4).
