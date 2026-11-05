# Lockstep simulation for the playtest bots

## Read this first - state on 2026-11-05 (results of two validation nights below)

**Phases 0-2 are BUILT and MERGED to main; none of it is DEPLOYED. Phase 3 (validation) runs the night of 2026-11-04; P5 is a spec only.**
PRs: #313 (Release overnight runs, precise tick sum), #314 (lockstep, phases
1a-2), #315 (`SafeRollbackAsync`, a production fix), #316 (loot coalescing,
production). #314-#316 touch production code paths: every rollback, the
anti-cheat/throttle guards (`GameClock.MonotonicMs`, identical off lockstep),
the cron `RunCycleAsync` extraction, the `[flood]` log line, the loot worker.
The owner decides the deploy (planned 2026-11-04).

How to run it: server with `FOLKIDLE_DEV_TOOLS=1 FOLKIDLE_SIM_LOCKSTEP=1`
(optional `FOLKIDLE_SIM_CRON_SCALE=4`, `FOLKIDLE_SIM_QUANTUM`, default 1),
bots with `--lockstep`. A whole night of it:
`server/FolkIdle.Bots/night-lockstep-validation.ps1`. Inside Docker:
`server/FolkIdle.Bots/docker/run-lockstep.sh` (slower today - see below).

Measured speed: 4 bots x500-x560; 20 bots (2x2 personas) x160 at
`CRON_SCALE=4`, quantum 1, 0 kicks, 0 lost sockets, all reach L10.

Where the wall time goes (measured, not guessed):
- **The database round trip, not fsync.** Windows -> Postgres in Docker is
  0.43 ms per query (`SELECT 1`); a Serializable BEGIN+INSERT+COMMIT 0.73 ms
  with `synchronous_commit` on and 0.77 ms with it off - so turning fsync off
  buys nothing (the sim databases already have it off). A loot transaction
  makes 15-20 EF round trips, about 9 ms. Inside the Docker network the round
  trip is 0.12 ms and the crons run 3x faster (4 bots, 10 game h: crons 47 s
  on Windows, 16 s in Docker).
- **But a run inside Docker was not faster** (4 bots: 1.1 vs 1.2 wall min;
  20 bots: x98 vs x160). About 50 ms per wake is unaccounted for in Docker
  (38 s over 765 wakes). Prime suspect, NOT yet confirmed: Nagle + delayed
  ACK on Linux TCP - small request/response frames (wake -> yield, commands)
  stall ~40 ms each, and the managed Linux `HttpListener` does not set
  `NoDelay`. Windows loopback does not show it. Next step: set `NoDelay` on
  the control socket and the game WebSockets and re-measure; if it holds,
  Docker should beat Windows because its crons are 3x cheaper.
- Loot coalescing (#316) cut loot transactions 26% at 20 early-game bots
  (4,860 requests -> 3,578); a low-level player kills a given monster once
  or twice per 12-second cycle, so the win grows with kill rate (late game,
  offline bursts), not visible yet in speed (x157 vs x165, noise).

Night runs (Windows Task Scheduler, from the frozen worktree
`../IdleHra-night3` at main 4bb7c070, Release):
- **2026-11-04 00:00** "FolkIdle bots night 2026-11-04 lockstep": phase 3 -
  paced x20 72 game h (reference), lockstep scale 1 and scale 4 72 h, same
  2x2 mix, seed 1; `compare-validation.py` writes `validation.md`; then a
  240-game-hour lockstep deep run. Output `bot-runs/night-20261104/`.
- **2026-11-05 00:00** "FolkIdle bots night 2026-11-05 rebirth lockstep": the
  rebirth 2x2 (PLAYTEST_BOTS_REPORT 0.6.2) under lockstep, 720 game h, seed 2.
- The old tasks "night1 casual x20" and "night2 rebirth 2x2" (frozen
  pre-#313 worktree, Debug, x200) are DISABLED.

**Result of 2026-11-04: the validation was INVALID, not failed.** The
lockstep runs were healthy (20/20 bots, no kicks, ~L60 at 72 h), but the
paced reference was broken: 5 of 20 bots ended on `429 Too Many Requests` at
login (AuthThrottle, 15 auth requests a minute per address), and 8 more stood
at level 1-5 for 71 game hours (one fight refused, the other four monsters
"left until the next level" after two deaths each, and no fight left to reach
it). The lockstep runs also started inside the sleepers' 00:30-06:30 core, so
their bots began ~4 game hours later than the reference's. Measured from each
bot's own login, scale 1 and scale 4 agree closely (eagerkit level 50: 47.7 vs
48.3 h), which says nothing yet about parity with the paced path. Fixes: bot
recovery and login retry (PR "bot-recovery"), a validation that checks run
conditions, aligns the start time and measures from login (PR
"validation-fix"). The 240-game-hour deep run of the same night went to
region 4 and two rebirths; it is not a parity proof.

**Result of 2026-11-05: VALID, and level pacing agrees; two bot defects
explain the large gaps.** From `../IdleHra-night4` (main at #319, Release),
`bot-runs/night-20261105/`: x20 216 wall min, lockstep scale 1 154 min (x28),
scale 4 108 min (x40). 20/20 bots started and none stopped in all three runs;
no broken bots; the loot worker reported 0 failed batches in every run. 84 of
110 medians fell inside the reference's spread (81 of 102 once `first_death`
is left out, see below). Levels 10-50 agree within about 10% for every
persona (optimal level 50: 40.5 h x20, 39.6 h scale 1; eagerkit 47.7 / 47.7 /
47.8 h). The gaps, and what they are:
- **Region-1 boss, x20 23.7 h vs lockstep 5.3-6.1 h (optimal, eagerkit): a
  bot defect, in both modes.** The bot went fishing only at a larder of
  exactly 0, and auto-eat leaves a few fish, so after its first trip it
  fought region 1's no-food fights on a larder of 1-8 until region 2 opened
  (x20 optimal1: larder 4 at 8 h, 2 at 12 h, 1 at 16 h). The first trip's
  size decided the run: its target read the food rate of the ACTIVE fight,
  which is often 0 just after the fight stopped, so it fell to the 150
  floor (x20 optimal1 396 fish) or got three hours of food (lockstep 2,316).
  Lockstep's optimal5 hit the same trap (larder 8 at 12 h, region 2 at
  ~16 h). Fixed in the bot: the target falls back to the best fight that
  holds with food, and the bot tops the larder up when it holds less than
  half an hour of a food fight that beats every no-food fight.
- **Refusals, x20 394 vs lockstep 56 "refused (halt 0, result 0)"; 153 vs
  17 trips ended as refused: a bot defect, paced only.** The confirmation
  window was 8 game seconds = 0.4 wall seconds at x20, shorter than the
  round trip plus the next snapshot. Paced, it now also waits 2 wall
  seconds and 2 state frames (10 s at most); lockstep is unchanged.
- **`first_death`, x20 0.06-0.47 h vs lockstep 1.2-27 h: reaction time, not
  the sim.** A paced bot decides every 0.4-1.2 wall seconds = 8-24 game
  seconds at x20, so it is slower to leave a fight whose food ran out. A
  real x1 player is closer to lockstep here. `compare-validation.py` now
  shows it as "reaction, not counted".
- **Village upgrades: 191 serialization failures in scale 1 vs 17 in x20**
  ("could not serialize access due to concurrent update / read/write
  dependencies"). Lockstep sends many players' commands in the same instant
  as village production, so it amplifies a real server race (the upgrade
  and live production both write the village rows). Production server code:
  noted for the owner, not changed here.
- Scale 4 against scale 1: the same within noise except optimal region 3
  (33.7 vs 28.1 h) and level 50 (47.2 vs 39.6 h); one night is not enough to
  call it.

Next, in order: re-run the validation with the two bot fixes (2026-11-05,
daytime window approved by the owner until 19:00); the village race (owner
decision, production code); NoDelay in Docker; then P5 below.

### P5 - strategy cohorts and an endgame scoreboard (owner spec, not built)

Today's optimal bot is greedy: it scores attributes, nodes and gear by the
immediate combat what-if, so loot luck (0 DPS now, rarity and affixes later)
never wins, and "strongest" is not one goal. P5 runs 20 bots as five cohorts
of four, the SAME attention budget and sleep, different decision weights:

| Cohort | Decision rule |
|---|---|
| GreedyCombat | today's bot, pure immediate combat what-if (baseline) |
| LuckSnowball | high loot-luck weight early/mid game, pivots to combat once rare gear with affixes is in hand |
| BalancedLuck | a fixed synthetic value: +1% loot luck counts as +X% combat score |
| BossSlayer | offense/crit/speed weighted 1.5x defence; max world boss strike and Malakor kills an hour |
| AscensionDelver | strict death threshold (refuses >2% death risk), EHP/sustain; top dungeon and Malakor Ascension |

Endgame scoreboard in `MilestoneLog`/`compare-run.py`, at game hours 72, 168
and 336, per cohort: world boss total and best single strike, Malakor kills,
top Malakor Ascension step, top dungeon tier, a gear rarity/affix index.

## History

Status, 2026-11-03: owner approved the lockstep approach (P0 -> P3, bots stay
on the real WebSocket/REST surface) and asked for implementation. BUILT: step 0
(PR #313) and phase 1a (`SimConductor`, `SimLockstep`, the control socket, the
engine branch, tick-driven `GameClock`) and phase 1b (`SimCronPump`: nine
gameplay crons run on the tick at their production periods) and phase 1c
(game-time guards, event wakes) and phase 1d (settle before each tick,
per-route REST timing) and phase 2 (bots on the control socket, every
persona sleeps). Not built: 3 (validation against x20, overnight).

Phase 2 first runs (Release, 2 optimal + 1 casual + 1 idle, seed 1, 30 game
hours, local Docker Postgres): **x279 effective - 30 game hours in 6.4 wall
minutes**, 3,177 wake frames, 0.8 M ticks run, the rest jumped at night.
Optimal L10 at 2.5 game h (x20 baseline about 2 h), region 2 at 12-22 h,
L30 at 15-24 h; casual L20 at 24 h. Three defects found and fixed on the
way: REST calls that wait on the tick (payload snapshots, world boss
strikes) timed out as NoSession while the tick was parked - the parked tick
now serves them; settling every dispatch before every tick (x33) - now only
world-changing ones per tick, everything before a wake or a jump; and
overdue chore timers turning into one wake per game second (95,000 wake
frames -> 3,177).

Where the wall time goes now (10 game hours, 72 s): the gameplay crons
~41 s (codex 4.5 ms a run, achievements 7 ms, loot 2.6 ms, pending grants
1.2 ms - real database work at production cadence), REST ~20 s, ticks
0.5 s. `synchronous_commit=off` gained ~5%. The next step up is an owner
decision: run the crons at a multiple of their production period under
lockstep (a fidelity trade-off), or accept ~x300. (Owner chose the scale:
`FOLKIDLE_SIM_CRON_SCALE`, default 1.)

Phase 1a proof: `SimConductorTests` (27, mutation-checked: dropping the
reaction or the online check fails 7), and a live Release server with
`FOLKIDLE_SIM_LOCKSTEP=1` and one control client: 0 ticks before the client
joined, then wakes at ticks 1, 36,000 and 864,000 - 24 game hours in 102 wall
ms, with 1 tick actually run, because nobody was online.

## Goal

Run the playtest bots as fast as the CPU allows, with results that do NOT
depend on the speed. The reference is the x20 run, not x200: at x200 a
casual bot's 20-game-minute check-in is 6 wall seconds, which is why idle9
sat at level 4 for 61 days and casual bots never left region 1
(PLAYTEST_BOTS_REPORT.md 0.6.1). A faster wall clock makes that worse, so
the answer is to stop reading the wall clock, not to run it faster.

Acceptance:
1. A lockstep run and an x20 run of the same mix and seed agree on the
   per-persona milestone medians (level 10/20/30, region opens, gold, deaths,
   dungeon best) within the spread between bots (`compare-run.py`).
2. A lockstep run held to x50 and one run flat out agree the same way. If
   they do not, something still reads the wall clock.
3. Production is untouched: everything is behind `FOLKIDLE_SIM_LOCKSTEP=1`,
   honoured only with dev tools on and outside `Production`, like
   `FOLKIDLE_TICK_SPEED`.

## What P0 measured (2026-11-03)

Three minutes each at x200, fresh DB, early game (game hours 1-13), quantiles
averaged over 90 samples of the 600-tick window:

| Build, bots | p50 tick | p95 | p99 | ceiling at p50 | at p95 |
|---|---|---|---|---|---|
| Debug, 10 | 0.174 ms | 0.442 | 0.965 | x574 | x226 |
| Release, 10 | 0.091 ms | 0.293 | 0.751 | x1094 | x341 |
| Release, 20 | 0.187 ms | 0.473 | 2.095 | x535 | x211 |

- Tick cost grows linearly with ONLINE players, about 13-15 us per player
  per tick. At x200 the tick thread is only ~5% busy; x1000 with 20 online
  players would need ~5 us each.
- The 18,871 dropped ticks of the 20-bot run all fell in the first wall
  minute (20 sign-ups and logins at once; checkpoint queue 47). The measured
  window dropped none.
- 40001 serialisation conflicts appeared (village upgrade, skill tree
  commit) - commands racing checkpoints at x200.
- The server process used more than two cores (Debug, 10 bots) while the
  tick thread used ~5% of one. The rest is outside the tick: REST (the bots
  made 1,191 attribute decisions through the what-if projection in four
  minutes), checkpoints, broadcast, GC. In lockstep that work sits on the
  critical path, so it must be measured per route before P2 (task in P1).
- Before step 0 the exported tick-duration sum truncated every tick to whole
  milliseconds, so it read about a tenth of the truth.
- Every overnight run so far used the Debug build (about 2x slower).

## The design

### 1. The conductor owns time

`SimConductor` replaces `TickPacer` when lockstep is on. The engine runs tick
T only after every bot due at or before T has yielded. Game time is
`anchor + tick x 100 ms`; `GameClock` reads the engine's tick count instead
of a stopwatch, so a restart cannot re-anchor it and write a "future"
logout.

Between wakes the engine ticks with no sleeping at all. That is the speed:
whatever the CPU gives.

### 2. One control socket per bot process

A bot is logged OUT between check-ins, so its wake cannot travel on its game
session. The bot process opens one dev-only control WebSocket
(`/api/v1/dev/sim`) that carries every bot it runs:

- server -> bots: `Wake { tick, bots: [{ bot, reasons }] }` - everyone due in
  this quantum, in one frame.
- bots -> server: `Yield { tick, bots: [{ bot, nextWakeTick, wakeOn,
  reactionMinTicks, reactionMaxTicks }] }` - one frame back.

A bot's pass runs between the two frames: its REST chores and at most one
WebSocket command, exactly as today. Time is frozen while it thinks, so REST
stays as it is; only the wake/yield pair is new protocol. All bots woken in
the same frame think in parallel.

### 3. When a bot wakes

`wake = min(nextWakeTick, eventTick + reaction)`, rounded up to the wake
quantum (default 10 ticks = 1 game second).

- `nextWakeTick` is the bot's own plan: its next chore timer, its next
  check-in, or "a reaction from now" when it has a command queued.
- `wakeOn` is a mask of events that cut the sleep short: death, activity
  halted (empty larder, full inventory, activity stopped), level up, the
  daily reset. A dungeon telegraph needs no event - the bot reads it from the
  REST reply and yields for its own reaction; the world boss has no live
  window (one strike per game day), so the daily reset covers it.
- The reaction is sampled BY THE CONDUCTOR from the bot's own range. A bot
  woken at `eventTick` would otherwise call REST at that very tick with zero
  human delay. Woken at `eventTick + reaction`, it sees the state at the
  moment it reacts (including dying in between), and needs no queue of
  delayed commands.
- The quantum is a parameter. Acceptance test: quantum 1 and quantum 10
  give the same results.

Before a bot wakes, the server pushes that player's snapshot (the broadcast
is per wall second today and would never arrive in time). The wake frame
carries the tick, and the bot waits for a snapshot with that tick or later
before it acts.

### 4. Nobody online: jump the clock

With zero game sessions and nobody due, the conductor advances the tick
count straight to the next wake. A logged-out player is not ticked anyway;
the login's offline catch-up (`FOLKIDLE_OFFLINE_SIM=1`, 20-50 ms per 12 h)
pays for the time away. This is the real offline path, so it adds no second
copy of game logic. It only helps when every bot is offline, so it speeds up
casual/idle mixes, not the always-online personas (open question 1).

### 5. Crons run on game time

Neither review caught this, and it already distorts x200 runs. About 19
background workers loop on WALL delays: `CodexEngine` 5 s, `CombatLootEngine`
3 s, `PendingGrantDrainEngine` 5 s, `AchievementEngine` 15 s,
`CosmeticGrantEngine` 1 s, `LiveOpsTickEngine` 100 ms, leaderboards
5-10 min. At x200 a region-boss kill reaches the codex (which the boss mask
and region opens derive from) up to 17 game minutes late and loot up to 10
minutes late; at x1000, 83 and 50. In lockstep, the cron's wall delay means
nothing at all.

In lockstep the conductor PUMPS them: each gameplay cron exposes its one
iteration (`RunOnceAsync`), and the conductor awaits it every N ticks, where
N is its production period (codex every 50 ticks, loot every 30, ...). That
reproduces production's lag exactly. The cron's own loop does not start in
lockstep. Workers that do not touch gameplay (telemetry, funnel, stat
sampler, push) keep their wall loops.

### 6. Wall-clock guards switch to game time

In lockstep these read game time, still enforcing their rule:
`ClientInputThrottler` and `ValidateCommand`'s 100 ms gap (both
`Environment.TickCount64`), the macro detector's command timing, the
anti-cheat challenge budget, and the dungeon telegraph window
(`DungeonLiveRuns.IssuedUtc`, `TouchedUtc`). A live dungeon run spans several
wakes: the bot swipes at `telegraph tick + reaction`, so a missed window is a
real miss. Rate limits on REST stay wall (they would only throttle bot
thinking, not game time); bots are exempted on dev if they bite.

### 7. Checkpoints: keep the cadence, add back-pressure and a logout ack

- The cadence stays 3,000 ticks (5 game minutes), staggered. A checkpoint
  carries game state (village production deltas, gold high water, AgeTicks,
  pending gold), and "a field the checkpoint did not save" is how children
  never grew up. Stretching it to 30,000 is an experiment, not a default: it
  is allowed only if a lockstep run with 3,000 and one with 30,000 agree.
- Back-pressure: the conductor does not start a tick while
  `CheckpointWriter.QueueDepth` is above a bound (16). At x1000 with 20
  online players the steady rate is ~67 checkpoints per wall second, which
  Postgres handles; the bound only catches bursts like P0's login wave.
- Logout ack: already there. A login awaits `WaitForPendingFlushesAsync`
  for that player before it reads the row (task 43), so the offline catch-up
  always reads the state the session left.
- Found while building 1d: a login's database load runs on the thread pool
  (`SafeDispatchAsync`). Paced, it costs a few ms of a 100 ms tick; in
  lockstep the player would enter the world at whatever tick the pool
  finished on. The engine now waits for every in-flight dispatch before the
  next tick (`LockstepSettle`).
- The throwaway DB runs with `synchronous_commit=off`.

### 8. What the bot changes (P2)

- `Now` comes from the wake's tick, not from wall time x speed.
- The loop becomes: wait for wake -> pass (REST chores + one command) ->
  yield. `Task.Delay` between passes goes away.
- The persona becomes an explicit attention budget in game time: check-in
  schedule, reaction range, miss rate, how often it looks at each chore.
- Login waits for the first snapshot by tick, not for 2 wall seconds.
- `SessionLooksDead`'s 10 wall seconds without frames is replaced by "no
  snapshot for this wake".

## Phases

| Phase | Scope | Proof |
|---|---|---|
| 0 | Release build for overnight runs; precise tick sum | PR #313 |
| 1a (BUILT) | `SimConductor`, tick-driven `GameClock`, control socket, wake/yield, quantum, clock jump at zero sessions | `SimConductorTests`; live control-socket smoke |
| 1b (BUILT) | Cron pump on the tick for the gameplay crons: liveops (600 ticks, run at start), codex 50, loot 30, pending grants 50, achievements 150, cosmetic chests 10, season era 3,000, leaderboards 3,000, leaderboard payout 6,000. The tick waits for each. | `SimCronPumpTests`; live smoke: 9 registered, no failures |
| 1c (BUILT) | `GameClock.MonotonicMs` (game ms under lockstep, `Environment.TickCount64` otherwise) for the 100 ms command rule, the throttler, the token bucket, the challenge window and the macro detector; `GameClock.ReactionUtcNow` for the dungeon telegraph window. Event wakes from ONE place: `LockstepEventProbe` diffs each player's payload around `ProcessTick` (level up, death, new halt incl. full inventory, activity stopped); the engine wakes `DailyReset` subscribers when the game day turns. Telemetry timestamps stay wall. | `LockstepEventProbeTests` (mutation-checked); guard/anti-cheat/dungeon suites 154/154 |
| 1d (BUILT) | `LockstepSettle` before every tick: all `SafeDispatchAsync` work the tick handed off (logins above all) has finished, and the checkpoint queue is at most 16 (10 s wall timeout, logged). Logout ack needed nothing new: a login already fences on that player's queued checkpoints (`WaitForPendingFlushesAsync`, task 43). `folkidle_dev_route_seconds_total{route}` on /metrics, dev tools only. | targeted suites 116/116; exercised end to end by P2 |
| 2 (BUILT) | `SimDriver` + `LockstepBotTime` (every wait is a yield), `RunLockstepAsync`, `--lockstep`, `--no-sleep`; every persona sleeps 00:30-06:30 plus up to an hour either side; the parked tick serves REST orders; per-tick settle only for world-changing dispatches; `folkidle_sim_cron_seconds_total` | `BotPersonaSleepTests` (8); 30-game-hour runs above |
| 3 | Validation: lockstep vs x20 (same mix and seed), quantum 1 vs 10, x50 vs flat out | `compare-run.py` per persona, overnight |

## 20-bot smoke (2026-11-03, late)

`-Mix optimal:5,patientbare:5,eagerkit:5,patient:5`, 10 game hours,
`FOLKIDLE_SIM_CRON_SCALE=4` (owner option B), quantum 1: x130, 4.6 wall
minutes; 19-20 of 20 bots reach L10 (median 2.3 h). Wall time: loot cron
48 s (gathering grants scale with players), codex 12 s, achievements 10 s,
REST most of the rest; ticks 4 s.

Found and fixed on the way:
- **A production defect:** a Serializable commit that loses (40001) leaves
  the transaction complete, and `catch { await transaction.RollbackAsync();
  throw; }` then threw "This NpgsqlTransaction has completed", REPLACING the
  40001 - no retry, no refusal, and SafeDispatchAsync kicked the player (13
  kicks in one run). Fixed for every rollback in the server by PR #315
  (`SafeRollbackAsync`, a source guard keeps bare `RollbackAsync` out): 0
  kicks, 16 polite refusals.
- The default wake quantum is 1 tick: a 10-tick quantum rounded every
  reaction to one boundary, so 20 bots' commands hit one tick together.
- Before a wake the checkpoint writer must be idle (it was "at most 16").

The 7 silent socket closes were the flood abort: lockstep broadcast in the
tick before ANY bot's wake - with 20 bots nearly every tick - and every
broadcast issued a fresh anti-cheat challenge, so each bot answered up to
ten a game second against a token bucket that refills five. Under lockstep a
challenge is now issued at most once a game second, as production's one
broadcast a second does; the flood abort now logs a line (it never did).

Quantum 10 vs 1, same 20-bot run after the fix (CRON_SCALE 4, 10 game h):

| quantum | wall | speed | wake frames | 40001 refusals | sockets lost |
|---|---|---|---|---|---|
| 10 | 4.2 min | x142 | 2,828 | 37 | 0 |
| 1 | 3.6 min | x165 | 4,513 | 16 | 0 |

Quantum 1 is both faster and cleaner: a 10-tick boundary makes twenty bots
write at once, and the conflicts cost more than the extra round trips.
Default stays 1.

The loot cron's cost is one Serializable transaction PER KILL (about 8,000
drop requests a minute of log at 20 bots); gathering is already one
transaction per player per cycle. Coalescing a cycle's requests per player
and monster into one request with `Kills = N` (the field the offline
catch-up already uses) would cut it by orders of magnitude, but it changes
the production loot path's failure isolation (one failure loses N kills, not
one), so it is a proposal for the owner, not part of this PR.

## Owner decisions (2026-11-03, second review)

- **Every persona sleeps.** optimal, patient, patientbare and eagerkit go
  offline 6-8 game hours a night too, with a shared core where ALL personas
  are offline (00:30-06:30 game time) so the conductor jumps every night;
  evening logouts and morning logins are staggered by a few minutes so no
  login storm lands on one tick. 24/7 stays only as a persona option.
- **P4 is shelved.** P0 dropped no tick outside the login burst; nightly
  jumps plus event-driven wakes by day should reach the target without it.
- **Daytime bots sleep long.** An online persona with a long activity sets a
  distant `nextWakeTick` and relies on `wakeOn` (level up, inventory full,
  death, activity stopped, dungeon telegraph) instead of checking every few
  seconds.
- **Crons on the tick, awaited** - built as phase 1b.

## Open questions for the owner

1. (Answered: every persona sleeps; P4 shelved; cron scale built.)
2. **Population effects** (world boss HP from registration strikes, market)
   reflect 10-20 bots. That is not a speed problem and lockstep does not fix
   it.
