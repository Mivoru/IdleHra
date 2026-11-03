# Lockstep simulation for the playtest bots

Status, 2026-11-03: owner approved the lockstep approach (P0 -> P3, bots stay
on the real WebSocket/REST surface) and asked for implementation. BUILT: step 0
(PR #313) and phase 1a (`SimConductor`, `SimLockstep`, the control socket, the
engine branch, tick-driven `GameClock`) and phase 1b (`SimCronPump`: nine
gameplay crons run on the tick at their production periods) and phase 1c
(game-time guards, event wakes) and phase 1d (settle before each tick,
per-route REST timing). Not built: 2, 3. The bots do
not speak the control socket yet, so a lockstep server today holds still until
a client joins - nothing runs lockstep end to end with real bots.

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
| 2 | Bot on the conductor; personas as attention budgets | a 24-game-hour lockstep run finishes and logs no wall waits |
| 3 | Validation: lockstep vs x20 (same mix and seed), quantum 1 vs 10, x50 vs flat out | `compare-run.py` per persona, overnight |

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

1. (Answered above: every persona sleeps; P4 shelved.)
2. **Population effects** (world boss HP from registration strikes, market)
   reflect 10-20 bots. That is not a speed problem and lockstep does not fix
   it.
