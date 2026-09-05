# "It looks like no items are dropping" — 2026-09-05

A player report, investigated end to end. **Nothing in the drop pipeline was
broken.** One thing in the *display* was, and it is the reason the report was a
reasonable thing to make.

Read this before touching `RarityTier.RollTier`, `EquipmentDropChance` or
`EquipmentDropTable` in response to a similar report — every one of them was
measured here and is doing what it is authored to do.

## The report, in order

1. "it looks like no items are dropping"
2. "it's strange that nothing better than Rare has dropped from the Ice Bat and
   I have 23,804 kills"
3. "now not even the tier 3 monsters drop good things, so something broke in an
   update"

## What was measured

| Question | How | Answer |
|---|---|---|
| Does the rarity roll match its table? | `RarityRollDistributionTests`, **2,000,000** samples | Every tier 1-8 within **0.98-1.03x** of its authored share. Mean tier **1.918**. |
| Is something capping the ladder? | The clamp in `TryRollEquipment` uses `CraftingEngine.RarityTierCount`, a *crafting* constant, on a *loot* roll | Both are 14. Pinned by a test now, because if they ever disagreed the only symptom would be this exact report. |
| Does the Ice Bat drop region-4 gear? | `EquipmentDropTableTests` prints every canonical monster's table | Yes. No monster drops another region's gear; no monster has an empty table. |
| Did the drop chance fall in an update? | `git log -p` on `CombatLootEngine.EquipmentDropChance` | 0.020 -> 0.050 -> **0.150**, and never downwards. 0.150 since 2026-08-06. |
| Is auto-salvage eating the good drops? | The account's own setting | Off, by the player's choice. |
| Is loot luck wired? | `LootLuckRaisesTheTopWithoutRemovingTheBottom` | Yes, it raises tier 5+ and removes nothing. |
| What is the player ACTUALLY getting? | Their last 91 drops, live DB | Mean tier **2.26** against a theoretical **1.918** — slightly *better* than authored. |

## Why the chest looked wrong, and why that was not evidence

The high-tier ratio in the older regions (region 3: 121 pieces above tier 7 out
of 506) is the fingerprint of **the chest sweep and of selling**, not of better
drops. The player identified this themselves and was right: they sold the low
rarities out of the regions they had left behind, which deflates the
denominator. A chest is `drops − sweep − salvage − sales + fusions`, none of
which are recorded with a timestamp, so **a chest census cannot answer a
question about drop rates**. Only the roll can, which is why the test above
asks the roll.

## The odds nobody had ever been told

Gear drops on **15%** of kills. Legendary-or-better is **0.85%** of drops.

- Legendary+ : about **1 kill in 780**
- Mythic+ : about **1 kill in 13,000**

24,000 Ice Bat kills is therefore a couple of dozen Legendaries — all of which
the sweep spares, and most of which had already been fused or sold. A player
counting kills against a number nobody showed them will conclude the game is
broken, and be reasonable about it. `SessionLoot` states the odds now.

`rarityOdds()` and `RARITY_DROP_SHARE` had been written, exported and imported
by **nothing** — found by a scan for dead client exports on the same day.

## The real defect: the panel threw the drops away

One shared 100-entry ring buffer held materials and equipment together. With two
characters gathering, a material lands every few seconds and the whole buffer
turned over in **about four minutes**, evicting every piece of equipment older
than that whatever its rarity. The player's own diagnosis was exactly right:
"with 2 characters on gathering the equipment probably gets overwritten straight
away".

Fixed by splitting it:

- `lootLogEquipment` and `lootLogMaterials` are separate stores, so material
  volume cannot reach the gear.
- `SessionLoot` renders two sections, each independently scrollable, so it
  cannot crowd it out visually either.
- Within each, sorted by **rarity descending**, not by time — a chronological
  feed of an idle session is a wall of Normal-tier scrap with the one Legendary
  buried four hundred lines up.
- Each equipment row names its tier. The same base item at three qualities
  renders as three rows and without the name they read as duplicates, on a panel
  whose whole purpose is saying how good a drop was.
- Neither store was ever cleared on sign-out or on switching characters. They
  are now.
- Gathering passes `showEquipment={false}` — a node drops no gear, so that
  screen was showing a permanently empty list under kill odds.

Loot was briefly narrated in the fight log; it was moved out on request ("under
the monster only the course of the fight, and on the right a loot drops window
split into materials and equipment"), and the eviction above is why that is the
right call and not only a layout preference. `exercise.mjs` asserts both halves:
the fight log carries **no** loot lines, and the loot panel fills, is split, and
sits to the right of the log.

## If this is reported again

Run `RarityRollDistributionTests` and `EquipmentDropTableTests` — both print
their tables. If they are green, the drops are happening and the question is
where they went: the sweep, auto-salvage, the forge, or a panel that dropped
them on the floor.

---

# THE SECOND REPORT, same day: "nothing drops, check it yourself"

The panel fix above was real and necessary, and it was **not** what the player
was hitting. Reported again after it shipped, with an instruction to verify
rather than reason: nothing had dropped since.

## What the live server actually showed

Measured on the production database while the account played:

| Signal | Over ~6 minutes | Reading |
|---|---|---|
| Ice Bat kills (codex) | 25,094 -> 25,635 | combat is running |
| Gold | +93,554 | kills are paying |
| Gathering materials | +26,498 | that grant path is alive |
| Combat materials (`mat_*`) | **0** | combat loot grants nothing |
| Equipment rows | **0** | ditto |
| `EquipmentInstances_Id_seq` | **unmoved** | no insert was even ATTEMPTED |

The sequence is the load-bearing one: a rolled-back insert still burns a
sequence value, so an untouched sequence means nothing reached the table at all.
And the account has 25,635 Ice Bat kills and has never once received
`mat_frozen_wing`, that monster's own material.

## What it was not

Every component was tested in isolation and every one was correct:

- `RollTier` matches its authored table across 2,000,000 samples.
- Every canonical monster grants gear AND materials against a real Postgres -
  Ice Bat gave 37 pieces and 80 materials in 200 kills (`LootWorkerResilienceTests`).
- Ice Bat's tables are right: `mat_frozen_wing`, and eight region-4 pieces.
- `EquipmentDropChance` is 0.150 and has only ever risen.
- Auto-salvage is 0 in the player's row.
- The deployed binary was verified to be the current commit, by finding a
  newly-added string inside the running container's DLL.

## What it was

`CombatLootEngine.ExecuteAsync` had **no exception handling of any kind**, and
`ProcessMonsterLootDropAsync` acquires its scope and opens its SERIALIZABLE
transaction **outside** its own try. So the two calls that take a database
connection could throw straight through the drain loop, out of the `Task.Run`
in `StartCron`, and end the worker. Nothing restarted it. Nothing logged it.
The queue simply filled for the rest of the process's life.

The throw is in the boot log:

```
ColdRecoveryCoordinator: Failed to reconstruct session for player 8:
  XX000: (EMAXCONNSESSION) max clients reached in session mode
  - max clients are limited to pool_size: 15
```

Production talks to Supabase's **session** pooler, which refuses the sixteenth
client. Npgsql's own default pool is **100**, so nothing on the client side ever
waits - it opens what it likes and the server throws, at whichever operation
happens to ask. One of those landed on the loot worker.

Everything else kept working because everything else is on another path: the
tick pays gold in memory, the codex has its own queue and its own worker, and
gathering grants were still being applied. From the player's seat the game was
running perfectly and simply stopped giving loot.

**Why it never reproduced locally:** there is no pooler on a dev box and never a
sixteenth client. The exact same code, content and tests are green there.

## The fix

1. **The worker cannot die.** Every dequeued request is isolated in its own
   try/catch, the cycle is guarded, and if the loop ever does exit it says so
   loudly instead of vanishing. The cost of a failure is one kill's loot.
2. **The pool is bounded below the server's limit** -
   `ConnectionStringDefaults.WithBoundedPool`, default 12, override with
   `FOLKIDLE_DB_MAX_POOL`. The same load now waits in Npgsql's queue instead of
   being refused. A slow acquire is recoverable; a throw was not.
3. **The loot path reports itself** - one line a minute when kills are rolling:

   ```
   Loot: 412 requests / 412 kills rolled -> 61 equipment, 144 materials, 0 auto-salvaged, 0 failed
   ```

   Requests at zero while a player is killing means the tick's half; requests
   flowing with no rows out means the worker's half. Neither was distinguishable
   from outside before, which is why this took a day.

## The wider lesson, and what is still open

Five other cron workers drain a queue with **no catch in their loop at all**:
`LeaderboardCronEngine`, `GuildMatchmakingEngine`, `LiveOpsTickEngine`,
`PushNotificationTriggerEngine`, `GuildWarEngine`. Each can die exactly the same
way, silently, and each would present as one feature quietly ceasing to exist
while the game runs on. They are the same defect, unfired.

## Verified live

After the fix, on the production box:

```
Loot worker started.
Loot: 3 requests / 559 kills rolled -> 83 equipment, 1 materials, 0 auto-salvaged, 0 failed
```

83 pieces from 559 kills is 14.8% against an authored 15%. Three requests for
559 kills is the offline catch-up, which carries a whole window in one request
and skips materials because its own bulk projection already granted them - hence
the single material, which is correct rather than suspicious.

On the database, over the same period: the account went from 935 owned items to
1,059, the equipment sequence moved for the first time in hours, and
`mat_frozen_wing` **exists for the first time ever** at 25,000+ Ice Bat kills.
Cold recovery reconstructed all 20 sessions with **zero** `EMAXCONNSESSION`
failures, against five (player 8 among them) on the boot before the pool bound.

### A postscript worth reading: the telemetry lied first

The throughput line shipped an hour before the rest and printed **nothing** for
fifteen minutes while 124 rows were landing - so the one signal that
distinguishes "the tick never enqueued" from "the worker wrote nothing" was
asserting the first while the second was provably false. It stamped its rate
limiter's clock *before* checking whether it had anything to say, so the empty
call three seconds after start-up consumed the window and the limiter kept
re-arming against nothing.

It was caught only because the database disagreed with it. Telemetry that lies
by silence is worse than none: silence is exactly what the failure it exists to
detect looks like. `LootThroughputReportTests` pins it, and `StartCron` now
announces itself so "loot is not being granted" and "the worker never started"
never again need a debugger on a production container.

## Also found: a second server has been running for three weeks

`docker ps` on the box shows an orphaned stack from 2026-08-10 -
`folk-idle-server`, `folk-idle-pgbouncer`, `folk-idle-db` - from the ROOT
`docker-compose.yml` rather than `ops/oracle/`. It points at its own local
`folkidle_prod` database, so it has never touched live player data, but it has
been spinning on a 2 vCPU box for three weeks logging a Redis timeout every
second (583 million completed thread-pool items). It is competing with the live
game for CPU and should be removed.

---

# STILL OPEN, 2026-09-06: live kills pay no loot

The worker-death fix above is real and holds. **A second, different fault is
still live** and this section is the handoff for it. Read it before touching
anything in the loot path.

## The symptom now

Loot arrives **only through the offline catch-up**, at the moment a client
connects or reconnects — 490 pieces on one login, 36 on the next, 8 from a
38-kill batch. Between those, a player who fights continuously receives
nothing, while their codex kill count, gold and gathering all advance normally.

That is also why the player reported it twice as "nothing drops": after logging
in, the loot panel is empty (it is session-scoped and offline drops predate it),
and everything that lands afterwards lands only at the next reconnect.

## What the instrumentation says

`CombatLootEngine` now counts BOTH ends of the queue and beats once a minute:

```
Loot: tick saw 1 kills, enqueued 1, worker drained 2 requests / 38 kills
      -> 8 equipment, 0 materials, 0 auto-salvaged, 0 failed, 0 still queued
```

`tick saw` is incremented immediately after `CodexEngine.KillEventQueue.Enqueue`
(SimulationEngine ~6258), `enqueued` immediately after
`CombatLootEngine.DropRequestQueue.Enqueue` (~6318). **They always agree.** The
two sit at the same brace depth in one straight-line block with no `return`,
`break`, `continue` or `goto` between them (verified by a brace-depth scan, not
by eye).

And yet, over a measured 204 seconds: **the database gained 127 codex kills
while `tick saw` reported 0.** Over another 226 seconds: 163 codex kills, and
the log printed nothing at all.

## What has been ruled out, with the evidence

| Hypothesis | How it was tested | Result |
|---|---|---|
| A second server writing to the same database | Stopped the app container for 90 s | Kills **stopped** (+6, a flush in flight). This process is the writer. |
| The retired Render deployment | `curl folkidle.onrender.com` | 404 from Render's edge; service gone. |
| The orphaned `folk-idle-server` stack on the box | Inspected its env and started its DB | Points at a LOCAL `folkidle_prod` with **zero** player rows. Now stopped. |
| A dev server on the developer's machine | `Get-Process`, command line | Only Roslyn's compiler server. |
| Stale deployment | Box HEAD == local HEAD; searched the running container's DLL for the new symbols | `NoteKillEnqueued`, `tick enqueued`, `Loot worker STOPPED` all present. |
| The worker died again | Grepped for its own STOPPED line; watched gathering | Alive: gathering grants keep landing, drained from the same loop. |
| An exception between the two enqueues | Grepped for `Tick processing failed` across the whole log | Zero. A throw there would log, suspend the player and disconnect them. |
| Codex counts being multiplied | Read `CodexEngine.ExecuteAsync` end to end | `entry.KillCount += group.Count()` — one event, one kill. No other writer of `KillCount` exists outside migrations. |

So: the codex enqueue fires ~40 times a minute (the database proves it, and
stopping the process stops it), the counter three lines below it reports zero,
and the counter demonstrably works because it printed `tick saw 1` once.

**Those three facts cannot all be true.** One of the measurements is lying and
the next session's first job is to find out which.

## Where to go next, in order

1. **Log inside `CodexEngine.ExecuteAsync` how many events it dequeues per
   cycle.** That splits the remaining space cleanly: events dequeued means the
   enqueue ran (and the counter beside it is somehow not running); zero events
   dequeued while `KillCount` still climbs means the rows come from somewhere
   nobody has found yet.
2. **Check the multi-slot path.** `ProcessAllSlotSubTicks` is called twice per
   tick (SimulationEngine 4436, and 4459 for the `AccumulatedTimeBankMs`
   acceleration). Both go through the same block, but the second has not been
   observed directly.
3. **Reproduce locally with a fighting character** and watch the same heartbeat.
   Local drops work (`npm run exercise` sees them), so a local run that shows
   `tick saw N, enqueued N` with N large is the control that proves the
   instrumentation, and a local run that reproduces the gap is the whole answer.

## What is already committed and working

- The worker cannot die on one bad request; the pool is bounded below the
  database's client limit; both fixes verified live.
- The loot path beats once a minute with counts from both ends of its queue.
- An expired JWT signs the player out instead of retrying forever
  (`interpretClose`); the 24-hour token with no refresh is itself worth a
  product decision.
