# Audit remediation: eight items, one plan

> **For agentic workers:** each item below is its own branch and PR, cut from
> `main`. Steps use checkbox (`- [ ]`) syntax. Every "Done when" is a check
> somebody can run, not a description. Read CLAUDE.md's load-bearing rules
> before starting any item; the ones each item leans on are named in it.

**Source:** the architecture/performance audit of 2026-09-26 (session report,
not committed). Every file:line below was read on 2026-09-26 against
`docs/wheel-live` at `0e3c3de`. Re-grep before editing; lines drift.

**Order, and why it is not the audit's order:**

| Order | Item | Why here | Size |
|---|---|---|---|
| 1 | **4. Funnel telemetry** | Independent of everything else, and every day it is not live is a cohort nobody can measure afterwards | 1-1.5 d |
| 2 | **1. Concurrent HTTP router** | Unauthenticated DoS today; blocks every login behind the slowest request | 2-3 d |
| 3 | **3. Per-session outbox** | Small, self-contained, fixes silent event loss | 1.5-2 d |
| 4 | **6. Cap split-brain gold** | Tiny, and it edits `FlushState`, which item 2 then restructures - do it first to avoid a conflict | 0.5 d |
| 5 | **2. Checkpoints off the tick thread** | The riskiest change here (it touches both gold paths). It needs 6 to be merged first | 3-4 d |
| 6 | **5. Unique commodity rows** | Needs a production query before the migration can be written | 1.5-2 d |
| 7 | **7. Haptics + local notifications** | Client only, plus one wire field | 1.5 d |
| 8 | **8. State-frame size** | **Measure first; may be cancelled. If not: compression via Kestrel, and deltas only if that is not enough** | 0.5 d measure; +2-3 d Kestrel; +2-3 d deltas |

**Decisions (settled 2026-09-27).** The owner set D1 and delegated D2-D5; the reasoning is recorded so nobody has to re-derive it:
- **D1 (item 6): a flat 1,000 gold, once per incident.** It does not depend on `epochDelta`.
- **D2 (item 4 follow-up): the first-kill decision is reopened only on evidence, by a rule fixed now.**
  - The trigger has three parts, all of which must hold:
    - at least 30 new registrations are in the cohort;
    - step 1 to step 2 (first kill) is the **largest** drop in the funnel;
    - that drop is at least 25%.
  - If the trigger fires, the change is limited to **new characters**: the first 3 kills against a tutorial-weight regular, kept inside the existing region 1 roster. The region-1 curve pinned in `HardenedEngineIntegrationTests` stays as it is. Take it to the owner as a proposal with the numbers attached, not as a fix.
  - If the trigger does not fire, the 2026-09-23 decision stands, and the funnel says where to look instead.
  - Why a rule instead of a judgement later: 61 accounts is a small population, and deciding the threshold after seeing the data is how a preference gets dressed up as evidence.
- **D3 (item 7): haptics are ON by default, with a toggle in Settings.**
  - They are light and throttled to one per 80 ms.
  - A phone's own system setting still silences them.
  - Players rarely discover a feature that is off by default.
- **D4 (item 8): measure first, then use the cheaper remedy first.** If 8a crosses the threshold, the first remedy is **WebSocket compression (permessage-deflate)**, not hand-written deltas.
  - Every browser and WebView negotiates it natively, so the client needs no change.
  - It needs no new packet type and no capability negotiation.
  - JSON with about 230 repeated PascalCase keys typically compresses by 80% or more.
  - Build deltas (8b) only if compressed frames still exceed the threshold.
- **D5: no Kestrel migration now, with one exception.** `HttpListener` cannot negotiate compression, so **Kestrel becomes the remedy if D4 triggers.**
  - Item 1's router split does most of the migration's groundwork anyway.
  - Kestrel is also reopened if concurrent players pass about 200, or if slow-body abuse gets past Caddy's `request_buffers`.
  - Otherwise, migrating the 102 routes carries risk and buys nothing at this population.

---

## Item 4 - Funnel telemetry (do this first)

**Problem.** Of 61 accounts, 1 is at level 10 or above (2026-09-25). Nothing
records where the other 60 stopped. `AccountAnalyticsLogs` exists
(`TelemetryStreamingEngine.cs:228`), but it is hash-typed, not unique per step,
and mixed with anti-cheat events. It is the wrong shape for a first-occurrence
funnel, so do not reuse it. `PlayerRecord` has **no creation timestamp at all**,
so the "registered" row is also the only record of when an account was created.

**Design.** One table records the first time each player reaches each step:

```
player_funnel_events (PlayerId bigint, Step smallint, At timestamptz,
                      PRIMARY KEY (PlayerId, Step))
```

Writes use `INSERT ... ON CONFLICT DO NOTHING`, so recording a step twice is
free and correct. The producer calls are `ConcurrentQueue` enqueues, which are
safe on the tick thread. A cron worker drains the queue in batches.

| Step | Name | Hook (one writer each) |
|---|---|---|
| 1 | registered | `AuthenticationEngine.cs` - both `new PlayerRecord` sites (~578, ~828) |
| 2 | first_kill | the kill path in `ProgressionEngine` (live), plus offline `ApplyCombatXp` |
| 3 | first_equip | `EquipmentSlotEngine` equip commit |
| 4 | first_craft | `CraftingEngine` completion |
| 5 | onboarding_done | wherever `OnboardingSeenIds` receives the final id |
| 6 | region1_boss | `BossFirstClearRules` / first-clear trophy grant |
| 7 | level_5 | **in `FlushState`**, from `state.CurrentLevel` |
| 8 | level_10 | same |
| 9 | level_20 | same |
| 10 | joined_guild | `GuildManagementEngine` join commit |
| 11 | returned_d1 | login: now - registered.At is between 24 h and 48 h |
| 12 | returned_d7 | login: now - registered.At is between 7 d and 8 d |

Levels are hooked in `FlushState` on purpose. CLAUDE.md: *three paths grow a
level*. The checkpoint is the one place all three pass through, so one hook
covers kills, warp and offline progress. A level reached within five minutes of
logout still lands, because Logout flushes too.

### Steps
- [ ] Model `PlayerFunnelEvent` with `[Table("player_funnel_events")]`, a composite key in `FolkIdleDbContext`, and a migration. **Add the table to `CURRENT_IMPLEMENTATION_STATE.md` §3** (the snake_case table list; CLAUDE.md "Raw SQL must match the table name").
- [ ] `Engine/FunnelRecorder.cs`:
  - a static `ConcurrentQueue<(long, FunnelStep, DateTime)>`;
  - `Record(playerId, step)`;
  - a per-session `HashSet` guard, so a hot path enqueues once and not every kill.
- [ ] Add `FunnelRecorder.StartCron/StopCron`. Follow each rule from CLAUDE.md:
  - read the queue depth once per cycle and take it as a **budget** ("unbounded drain");
  - one multi-row `INSERT ... ON CONFLICT DO NOTHING` per batch;
  - put the try/catch **before** `CreateScope`, because a guard that starts afterwards guards nothing;
  - report the depth in the heartbeat.
- [ ] Add it to the `CronWorkerGuardTests` inventory. The test fails until you do, which is intended.
- [ ] Add the 12 hooks. For each one, grep that there is exactly one writer.
- [ ] Tests (`FunnelRecorderTests`, Testcontainers):
  - a fresh registration plus one kill gives rows 1 and 2;
  - a repeated step does not duplicate;
  - levels reached through **warp** and through **offline** progress both record (this pins the three-paths rule);
  - start and stop the worker in `try/finally` (CLAUDE.md "static queues").
- [ ] Add `docs/ops/funnel.sql`. It must exclude `exercise%` throwaways and the dev fixture:

```sql
SELECT f."Step", count(*) AS players
FROM player_funnel_events f
JOIN "PlayerRecords" p ON p."Id" = f."PlayerId"
WHERE p."Username" NOT LIKE 'exercise%' AND p."Username" <> 'dev'
  AND f."PlayerId" IN (SELECT "PlayerId" FROM player_funnel_events WHERE "Step" = 1)
GROUP BY 1 ORDER BY 1;
```

**Done when:**
- `exercise.mjs` passes, and its new-player context produces steps 1-2 in the local database;
- after deploy, the SQL runs over SSH (pipe it on stdin, as the backlog notes) and shows rows for new registrations.

**Limit:** accounts from before the deploy have no step 1 and are outside the
cohort. That is accepted, because nothing can reconstruct their registration
time.

**Follow-up (D2, not in this PR).** Once the cohort holds at least 30 new
registrations, run `funnel.sql` and apply the D2 trigger at the top of this
plan. If it fires, write the proposal (the first 3 kills against a
tutorial-weight regular, new characters only) and take it to the owner with the
numbers, citing the 2026-09-23 decision (TASK_BOARD ~3272). If it does not fire,
record the funnel in the backlog and leave monster stats alone.

---

## Item 1 - A concurrent HTTP router

**Problem.** `ListenLoopAsync` (`Network/NetworkBroadcastSystem.cs:663`) awaits
79 handlers inside the accept loop, and the loop reads request bodies with
`ReadToEndAsync` in 37 places.
- One slow request, such as `/api/v1/player/inventory` at 3.2 MB (`:1315`), stalls every login and WebSocket upgrade.
- A client that sends its body one byte at a time freezes the server **without authenticating**. `/api/v1/assets/handshake` reads the body inline, and Caddy streams bodies to the app unbuffered.

**The trap in the fix.** Handling one request at a time currently prevents
races **between** HTTP requests: a double-tapped chest sale, two bulk salvages,
a Delve action racing itself. Making the loop concurrent without a per-player
lock would turn a latency bug into a duplication bug.

**Design.**
1. **Split the loop.** It keeps only `GetContextAsync` and `_ = Task.Run(() => RouteAsync(ctx))`. `RouteAsync` is today's `if` chain, moved unchanged. It gets an outer try/catch that always closes the response (500 on a throw) and a 30 s `CancellationTokenSource` per request.
2. **Lock each account's mutating requests.**
   - Use a **striped** lock: `SemaphoreSlim[1024]`, indexed by `hash(accountId) & 1023`. Memory stays bounded and nothing needs cleaning up.
   - The router takes the account id from the Bearer JWT using `AuthenticationEngine.ValidateJwt`, which is CPU-only with no database call. The handler still does the full nonce check.
   - Lock only non-GET, non-OPTIONS requests that carry a valid token. Wait up to 10 s, then answer 429 with a JSON reason. Per CLAUDE.md, a player has to see *something* when a request is refused.
   - Two accounts can share a stripe. That serialises them against each other, which is harmless.
3. **Bound body reads.** Add `ReadBodyAsync(request, maxBytes, ct)`: a 64 KB cap, then 413, and the per-request cancellation token. Replace all 37 `ReadToEndAsync` calls. Add a test that greps for any remaining `ReadToEndAsync` in the file, the same kind of mechanical guard as `runesMode.test.ts`.
4. **Buffer at the edge.** In the Caddyfile's `@api` handle, add `request_body { max_size 1MB }` and `reverse_proxy app:8080 { request_buffers 1MB }`. Caddy then reads the whole body before the app sees any of it, so a slow body occupies Caddy, which is built to take that, rather than the app. **Do not add this to the WebSocket handle.** Only `caddy` publishes ports (`docker-compose.yml:140`), so the edge is the only way in.

### Steps
- [ ] **Write the failing test first** (`HttpRouterConcurrencyTests`). Start the listener on a free port. Open a raw `TcpClient` and send a POST with `Content-Length: 100` and no body. While that is pending, `GET /healthz` must answer within 1 s. **This fails on `main` today**; record that it does in the PR.
- [ ] Split the loop (1). Keep the `/health*` and `503 while cold-booting` checks first in `RouteAsync`.
- [ ] Add the striped lock (2). Test: two concurrent `POST` chest sales of the same stack for one account. Exactly one sells, and the other answers with a refusal, not a 500.
- [ ] Add `ReadBodyAsync` (3) and its guard test.
- [ ] Caddy change (4). Dry-run it as `ops/oracle/README.md` describes, since `caddy validate` is not enough (see the Caddyfile's own `handle` comment).
- [ ] Update CLAUDE.md with a new load-bearing rule: *mutating REST handlers run concurrently; they are serialised per account by the router's striped lock; a new handler that mutates on GET must say so*.

**Done when:**
- the new tests pass, and the full suite passes;
- `exercise.mjs` passes;
- `smoke:screens` passes against production after the deploy;
- a manual `curl` that trickles a body at the production host does not delay a parallel `curl /healthz`.

**Follow-up (D5, decided).** Do not migrate to Kestrel here. It becomes the
remedy only if item 8a triggers, because WebSocket compression needs it, or if
concurrent players pass about 200, or if slow-body abuse gets past Caddy. Write
`RouteAsync` so it does not depend on `HttpListenerContext` more than it has
to: take the request fields it needs as parameters where that is cheap. That
keeps a later move small.

---

## Item 3 - A per-session outbox

**Problem.** `WebSocketSession.SendAsync` (`NetworkBroadcastSystem.cs:126`)
takes its lock with `WaitAsync(0)` and **drops** the frame when the lock is
busy. That is right for absolute snapshots, and the doc comment argues exactly
that. It is wrong for events: loot (`:423`), combat events (`:475`) and chat,
including private messages (`:541`, `:575`, `:10276`), are discarded whenever a
state frame is in flight. On top of that, each dispatch loop has a single
consumer that awaits each send in turn, so one peer that has stopped reading
holds its loop for up to `SendTimeout` (20 s) and delays every other player.

**Design.** Each session gets one writer task, and nothing else touches its socket.

```
WebSocketSession
  _latestSnapshot : byte[]?        // Interlocked.Exchange; latest wins
  _events         : Channel<byte[]> bounded 512, FullMode = DropOldest
  _signal         : SemaphoreSlim(0)
  WriterLoopAsync : wait signal -> drain events in order -> send snapshot if any
  EnqueueEvent(bytes) / OfferSnapshot(bytes) : never await, never block
```

- Events go out **before** the snapshot in the same wake-up. The combat feed comment requires this: a blow must not arrive visibly after the health change it explains.
- A dropped event increments `outbox_events_dropped_total` in `/metrics`. A drop that nobody counts would be silent again.
- The 20 s send timeout and `IsWedged` eviction stay. They move into the writer loop.
- `CloseAsync` goes through the writer as a sentinel, so a close never races a send.
- Binary sessions (the retired Unity client) keep working. `SendStateFrameAsync` copies into a rented buffer and offers that, instead of sending from the shared `DiagnosticSendBuffer`.

### Steps
- [ ] Write a `FakeWebSocket : WebSocket` for tests whose `SendAsync` waits on a gate.
- [ ] Tests (`SessionOutboxTests`, no database):
  - (a) events enqueued while a send is blocked arrive in order after the gate opens;
  - (b) three snapshots offered during a blocked send deliver only the last;
  - (c) a blocked session A does not delay session B's event; the dispatch loop returns within 5 ms;
  - (d) the 513th event drops the oldest and increments the counter;
  - (e) a send timeout sets `IsWedged`.
- [ ] Implement the outbox. Replace the 14 `.SendAsync(` call sites with `EnqueueEvent` or `OfferSnapshot`, and remove `_sendLock` usage outside the writer.
- [ ] The three dispatch loops (loot, combat, chat) now only enqueue. Remove their `await`s.
- [ ] Add the counter and queue depth to `/metrics`.

**Done when:**
- the tests pass;
- `exercise.mjs` passes. Its loot and combat-log checks read these feeds;
- two browsers in world chat both see every message during a combat session. Check this by hand, because no current test covers chat delivery under load.

---

## Item 6 - Cap the split-brain gold mail (before item 2)

**Problem.** `StateCheckpointManager.cs:265` mails `epochDelta * 500` gold when
the database epoch is ahead of memory. The amount has no ceiling, and nothing
makes it pay once per incident. Minting currency on an error path is an exploit
shape: whoever can make two sessions race the Redis session lock is paid for
it. **The trigger is plausible, not demonstrated.**

### Steps
- [ ] Before changing anything, count past payouts over SSH with `SELECT count(*), sum("GoldAttachment") FROM "MailboxInstances" WHERE "BaseItemId" = 'GOLD_COMPENSATION';`. Record the result in the PR. If the count is non-zero, it is evidence the path fires.
- [ ] Create table `split_brain_incidents (PlayerId, DbEpoch, At, PRIMARY KEY (PlayerId, DbEpoch))`, with `[Table]` and an entry in §3.
- [ ] In the compensation task, `INSERT ... ON CONFLICT DO NOTHING`, and mail only when it inserted one row. Mail a flat `SplitBrainCompensationGold = 1000` (D1, owner decision 2026-09-27), independent of `epochDelta`.
- [ ] Log one line per incident with player, both epochs and the Redis lock holder, so the cause can be found. At present there is only a telemetry event with codes that collide with other events (a memory note records that collision).
- [ ] Test: the same stale state flushed twice produces one mail. A second incident at a new database epoch produces a second mail.

**Done when:** the test passes and the full suite passes.

---

## Item 2 - Checkpoints off the tick thread

**Problem.** `FlushStateAndAdvance` (`StateCheckpointManager.cs:172`) runs a
Serializable, `FOR UPDATE`, retrying transaction **synchronously on the one 10 Hz
thread that simulates every player**. It is called from 10 sites:
`TrackState` (`SimulationEngine.cs:1819`), ReloadState (`:1636`), Logout
(`:1677`), Forge (`ForgeTickCoordinator.cs:71,105`), Market
(`MarketTickCoordinator.cs:99,137`), Guild (`GuildTickCoordinator.cs:60`) and
GuildWar (`GuildWarTickCoordinator.cs:53`). The call at `SimulationEngine.cs:1393`
runs on the login path, off the tick thread, and can stay synchronous.

**Read before touching anything:**
- CLAUDE.md, "Two gold paths" and "A Redis frame is not a checkpoint".
- `RedisSessionCache.TryStoreFrame` (`:79-83`): **with Redis up it moves `RedisPendingGoldDelta` into the Redis buffer and zeroes it on the payload**. `RedisWriteBehindEngine` banks it from there. The flush sees a non-zero delta only when Redis did not take the frame.
- `FlushState`'s epoch rule: it refuses when `db.Epoch > state.Epoch`, and on commit writes `db.Epoch = state.Epoch + 1`, after which the caller does `state.Epoch++`. Invariant after a commit: **the database epoch equals the payload epoch.**

**Design.**

```
tick thread                                   CheckpointWriter (4 partitions, playerId % 4)
-----------                                   ------------------------------------------
RequestFlush(ref p, reason, then?)            per-partition Channel<FlushJob>, FIFO per player
  TryStoreFrame(ref p)          (unchanged)   job.Snapshot -> FlushState(snapshot)
  job.Snapshot = p              (struct copy) on commit: await job.Then?.Invoke()
  job.Snapshot.Epoch = p.Epoch + p.FlushesInFlight
  job.GoldDelta = p.RedisPendingGoldDelta     post FlushAck{playerId, committed,
  p.RedisPendingGoldDelta = 0   (moved)            committedDbEpoch, goldDelta, reason}
  p.FlushesInFlight++
                                              
drain FlushAckQueue (every tick)
  p.FlushesInFlight--
  committed: p.Epoch = max(p.Epoch, committedDbEpoch); IsDirty=false; TicksSinceLastFlush=0
  failed:    p.RedisPendingGoldDelta += goldDelta; IsDirty = true   (never lose coins)
             if reason is a command (market/forge/...): IsSuspended=false + a result code
```

Why the epoch arithmetic is safe. A snapshot's epoch is the payload epoch plus
the number of flushes already in flight. The database epoch can never exceed
the payload epoch plus the number of those that commit, so `db.Epoch > snapshot.Epoch`
cannot happen for this player's own queued flushes. That rules out a false
split-brain, and with it a false compensation mail. A failed flush only leaves a
gap in the epoch sequence. Epochs only need to increase, and the command gate
has a drift tolerance (`ClientCommandValidator.cs:51`).

A command flush uses `then`. Market and Forge today do three things in order:
"suspend, flush synchronously, dispatch engine work that reads the flushed
rows". `then` preserves that order. The work runs **on the writer, after the
commit**, and never after a failed flush. The failure path un-suspends the
player **with a result code**, per CLAUDE.md "Silent rollback".

Logout sends a final job with retries (3 attempts, backoff). After those it
writes a dead-letter line holding the player id and the gold delta. Today a
failed Logout flush loses that data silently.

Shutdown: `FlushAllGracefully` completes the channels and awaits the partitions.

### Steps (three PRs)
- [ ] **2a - Build the writer, change no callers.**
  - Add `Domain/Shared/CheckpointWriter.cs`, `FlushJob`, `FlushAck`, `TickStatePayload.FlushesInFlight` (runtime-only, never on the wire), and `PlayerSessionRegistry.FlushAckQueue`.
  - Register the writer in `CronWorkerGuardTests` if it uses `StartCron`. Guard each job individually (CLAUDE.md "background worker").
  - Tests:
    - (a) flushes in flight at E and E+1 both commit, the payload ends at E+2, and no split-brain;
    - (b) the first of two flushes fails and the second commits: no split-brain, and the gold delta is restored exactly once;
    - (c) with Redis down, gold earned during an in-flight flush is banked **exactly once**. Use `CombatGoldParityTests` as the pattern;
    - (d) a failed `then` flush never runs the continuation and produces a result code;
    - (e) shutdown drains the queue.
- [ ] **2b - Move the callers, one per commit.** Order: `TrackState`, Guild/GuildWar, Forge, Market, ReloadState, Logout. After each commit run the full suite and `exercise.mjs`, whose market, forge and reroll checks cover these paths.
  - `ReloadState` becomes `RequestFlush(ref p, Reload, then: LoadPlayerState -> StateReloadQueue)`.
- [ ] **2b, extra.** Delete `|| state.InventorySpaceRemaining <= 0` from `TrackState` (`StateCheckpointManager.cs:71`). The counter no longer means anything (`InventoryCensusTickCoordinator.cs:100-110`), but `SimulationEngine.cs:4777` still decrements it, which can trigger a flush every tick.
- [ ] **2c - Stagger the boundaries.** At `AddActivePlayer`, set `TicksSinceLastFlush = PlayerId % CheckpointBoundaryTicks` rather than 0, so a reconnect wave does not all hit the boundary on one tick. Test: 100 players added on one tick reach the boundary on more than 50 distinct ticks.
- [ ] **2d - Fixed-timestep pacing (a separate PR, because it changes game speed).**
  - Replace `Thread.Sleep(TickIntervalMs - elapsed)` (end of the tick loop) with an accumulator that runs up to 5 catch-up ticks, then drops the rest and counts `ticks_dropped_total`.
  - Before merging, compare `ProgressionRateTests` output against `main`. The tests should not change, because they are headless. What changes is live progress per wall-clock minute, which is currently slower whenever a tick overruns.
- [ ] Add a histogram of tick duration percentiles to `/metrics`, if the existing 10/25/50/100/250 ms buckets do not already give p99. Record the before and after numbers in the PR.

**Done when:**
- no `FlushStateAndAdvance` call remains on the tick thread. A guard test greps `Domain/**` and `SimulationEngine.cs` for it; only the login path may call it;
- tests (a) to (e) pass, as does the full suite, including `CombatGoldParityTests` and `StateUpdatePacketFieldCoverageTests`;
- `exercise.mjs` passes;
- after deploy, the tick p99 in `/metrics` is under 25 ms with the live population.

---

## Item 5 - Unique `(PlayerId, ItemId)` on `CommodityRecords`

**Problem.** The index is not unique (model snapshot), and about 30 sites check
for a row and then insert one, in these files: `AuthenticationEngine`,
`CombatLootEngine`, `LarderEngine`, `LeaderboardCronEngine`,
`MailboxAndBankEngine`, `MarketOrderBookEngine`, `OfflineSimulationEngine`,
`PendingGrantOutbox`, `RedisWriteBehindEngine`, `VillageChestEngine`,
`CraftingEngine`, `DelveEngine`, `InventoryAndStashSystem`,
`MarketEscrowEngine`, `MarketTickCoordinator` (for example the settlement rescue
at `:58-80`, which has no lock at all), `DailyLoginRewardEngine` and
`StateCheckpointManager`. Any of these running under Read Committed can create a
second `gold` row.

### Steps
- [ ] **Query production first**, over SSH with the SQL on stdin, and put the result in the PR:
  ```sql
  SELECT "PlayerId","ItemId",count(*),sum("Quantity")
  FROM "CommodityRecords" GROUP BY 1,2 HAVING count(*)>1;
  ```
  If it returns rows, list which players and items. The migration merges them either way.
- [ ] Add `Engine/CommodityLedger.cs` with `AddAsync(db, playerId, itemId, delta)`:
  ```sql
  INSERT INTO "CommodityRecords" ("PlayerId","ItemId","Quantity") VALUES (@p,@i,@d)
  ON CONFLICT ("PlayerId","ItemId")
  DO UPDATE SET "Quantity" = "CommodityRecords"."Quantity" + EXCLUDED."Quantity"
  RETURNING "Quantity";
  ```
  It needs a unique index to target, so it lands in the same PR as the migration.
- [ ] Migration, raw SQL in `Up`, so it runs in the same transaction:
  1. merge duplicates, keeping the lowest `Id` with `Quantity = sum`, and delete the rest;
  2. `DROP INDEX` the non-unique one, then `CREATE UNIQUE INDEX`.

  This is **not additive**, so say so in the PR. The entrypoint runs it on deploy. Locally, apply it with `--migrate` before `run-dev.ps1`.
- [ ] Convert every **increment** site to `CommodityLedger.AddAsync`. Leave **decrement-with-check** sites (a `FOR UPDATE` row, then `Quantity -= cost`) alone; they are correct.
  - `DbSeeder` and `DevFixtureSeeder` may keep `new CommodityRecord`, but must not create a duplicate. Run `DevFixtureInvariantTests`.
- [ ] Guard test: `new CommodityRecord` and `CommodityRecords.Add(` may appear only in `CommodityLedger.cs` and the two seeders.
- [ ] Test: 20 parallel `AddAsync` calls for a new item on one player produce one row with the correct sum.

**Done when:**
- the guard test passes, and the full suite passes;
- `exercise.mjs` passes. It covers chest sale, crafting, market and the Delve, which all move commodities;
- after deploy, the production query returns 0 rows.

---

## Item 7 - Haptics and local notifications

**Problem.** Neither `@capacitor/haptics` nor `@capacitor/local-notifications`
is installed (`client_web/package.json`). Push exists, but the Firebase side is
incomplete (see the memory notes on mobile). A local notification needs no
server.

**Read first:** CLAUDE.md, "A Capacitor plugin read off the global still has to
be INSTALLED", and "cap sync on Windows writes a Package.swift Swift cannot
parse". Use `npm run sync`, which normalises the paths.

### 7a - Haptics
- [ ] `npm i @capacitor/haptics`, then `npm run sync`. Add a row to `REQUIRED_PLUGINS` in `tests/nativeProjects.test.ts`.
- [ ] Add `src/lib/net/haptics.ts`. It reads `Capacitor.Plugins.Haptics`, does nothing on the web, and is guarded like `push.ts`. It exports `tap('light'|'medium'|'heavy'|'success')` and a persisted `hapticsEnabled` store, following the pattern of `muted` in `lib/ui/audio.ts`.
- [ ] Hook it up to the feeds, not to snapshot inference (CLAUDE.md "combat EVENTS"):

  | Event | Haptic |
  |---|---|
  | crit | light, from the combat event feed |
  | kill | medium, from the same feed |
  | loot of tier 10 or above | success, from the loot feed |
  | shield-wheel parry window opens | light, in `ShieldWheel.svelte` |
  | boss plate breaks | heavy, in `ShieldWheel.svelte` |

  Throttle to one every 80 ms, because a fast fight fires many events.
- [ ] Add a Settings toggle next to Mute, with i18n keys in all 6 languages (`i18nKeys.test.ts` enforces this). **It defaults to ON** (D3).
- [ ] Unit test: haptics does nothing on the web, respects the toggle, and throttles.

### 7b - Local notifications
- [ ] **Wire field (use the `add-command` skill).**
  - Add `OfflineCapSeconds` (int) to `StateUpdatePacket`: the *effective* cap, including the Vodník extension (`RaceMasteryResolver.GetVodnikExtendedOfflineSeconds`). The client does not mirror the 12 h rule. A second copy of that rule would be the two-sources-of-truth bug class.
  - Hydrate it at login, or declare it runtime-only with a reason; `StateUpdatePacketFieldCoverageTests` forces the choice. Update the size guard (801 -> 805).
  - Run `npm run generate:protocol`.
- [ ] `npm i @capacitor/local-notifications`, `npm run sync`, and add a `REQUIRED_PLUGINS` row.
  - Android 13+: **declare** `POST_NOTIFICATIONS` in the manifest. The memory note records that an undeclared runtime request is refused silently and permanently.
  - Request permission from a Settings button, not at launch.
- [ ] Add `src/lib/net/localNotify.ts`, hooked into `lifecycle.ts`:
  - on background (native only, as `lifecycle.ts` already decides), schedule "Your folk are resting - they stop earning in 1 h" at `now + OfflineCapSeconds - 3600`;
  - on resume, cancel it.
  - Use one fixed notification id, so the schedule is idempotent.
- [ ] Larder run-out notification: **not in this PR.** The client cannot compute the drain honestly, because it depends on monster damage against healing. If wanted, the server would put a `ProjectedLarderSeconds` on the wire, computed with the same `OfflineSimulationEngine` food model. File it on the board.
- [ ] Tests: `localNotify.test.ts` covers the schedule arithmetic, cancel on resume, no action on the web, and no action without permission.

**Done when:**
- `nativeProjects.test.ts` passes, as do `npm test` and `npm run check:ratchet` (no new errors);
- on a real Android device, a crit vibrates, and backgrounding the app schedules the notification. **This needs the owner's phone**, because nothing in CI can check a vibration.

---

## Item 8 - Delta state frames (measure first, and possibly cancel)

**Problem, not yet measured.** JSON state frames (`PacketJsonCodec`, about 230
PascalCase fields) are serialized **on the tick thread** (`SendToPlayer`,
`NetworkBroadcastSystem.cs:9895`) into a new `byte[]` each time, with no
WebSocket compression. The send rate is already limited to 1 Hz plus a
keepalive, by a byte-compare dirty check (`SimulationEngine.cs:3147`).

### 8a - Measure (half a day, always do this)
- [ ] Add `state_frame_bytes_total`, `state_frames_total` and `state_frame_serialize_us` to `/metrics`, the last timed around `SerializeToUtf8`.
- [ ] Add a test that prints the JSON size of a realistic mid-game packet, using the dev fixture's payload, **and asserts an upper bound**. CLAUDE.md: a number a test prints is not a number a test checks.
- [ ] Record for one week in production: bytes per player per minute, and serialize microseconds per tick at p99.

**Go/no-go (D4, decided).** The item goes ahead if **either** of these holds:
- more than about 150 KB per player per minute on mobile. That is roughly 200 MB a month for a player idling one hour a day;
- serialization above 10% of the tick budget at the current population.

Otherwise close the item with the numbers written down.

- **The bandwidth trigger fires:** do **8c first** (compression), re-measure, and build 8b only if compressed frames still exceed the threshold.
- **Only the CPU trigger fires:** skip 8c. Compression costs CPU rather than saving it. Instead move serialization into the outbox writer (item 3), off the tick thread. Re-measure before considering 8b.

### 8c - WebSocket compression via Kestrel (the first remedy, D4/D5)
- [ ] Move the listener to Kestrel. `RouteAsync` from item 1 moves across and keeps its routing. The WebSocket upgrade uses `AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = true })`.
  - Keep Caddy in front, unchanged. It passes the `Sec-WebSocket-Extensions` header through.
- [ ] Set `MinRequestBodyDataRate` too. This retires the in-app half of item 1's slow-body defence, but leave Caddy's `request_buffers` in place.
- [ ] Verify on a real session in browser devtools: the socket's response headers show `permessage-deflate`, and the frame bytes in `/metrics` drop. Note that `/metrics` counts bytes before compression. Add `state_frame_wire_bytes` if Kestrel exposes the compressed size; if not, measure once with a devtools capture and record it.
- [ ] Run the full suite, `exercise.mjs`, and `smoke:screens` against production.
- [ ] Check the Android APK over a mobile connection. Reconnect after backgrounding must still work (`lifecycle.ts`).

### 8b - Deltas (only if 8c was not enough, or only the CPU trigger fired)
- [ ] Negotiate the capability. The handshake's `mode` property (`PacketJsonCodec.ModePropertyName`) gains `"json-delta"`.
  - Installed APKs whose OTA update has not yet been applied keep getting full snapshots.
  - Never send a delta to a client that did not ask for it. The alternative is a blank screen on a phone nobody can debug.
- [ ] Server: `_lastBroadcastByPlayer` already holds the previous packet.
  - Emit `{type:"StateDelta", seq, base, f:{Field: value, ...}}` containing only the fields that changed. Derive the field list by reflection from the same plan `PacketJsonCodec` builds. **Never write a field list by hand** (CLAUDE.md "Never hand-write a wire type").
  - Send a full `StateUpdate` on the keepalive, on reconnect, and on any `seq` gap the client reports.
  - Serialize in the outbox writer from item 3, not on the tick thread. That dependency is why this item comes after 3.
- [ ] Client (`connection.ts:456`): add `case StateDelta`, which merges into the last full packet and calls the same `onStateUpdate`.
  - On a `seq` gap, discard the delta and request a full snapshot.
  - `generate-protocol.mjs` must emit the `StateDelta` type from the dump (the rule: do not hand-write it).
- [ ] Tests:
  - server: applying a delta to the base equals the full packet, for 1,000 random mutations;
  - client (`connectionMessage.test.ts`): merge, gap recovery, and a client without delta support never receiving one.
- [ ] Also fix `visualState.set` on every animation frame (`game.ts:110`): only `set` when a field moved by more than 0.5, and stop the loop when nothing is interpolating. It belongs here because it is also frame-rate work. `check:perf` is the measure.

**Done when:**
- in production, measured bytes per player per minute drop by at least 60% for delta clients;
- `exercise.mjs` passes;
- `smoke:screens` passes against production;
- the Combat bar still animates (look at it on the phone).

---

## Housekeeping (every item)
- [ ] Use the `verify` skill before claiming an item is done. Gameplay proof is `npm run exercise`, not the smoke tests.
- [ ] Stop the server before `dotnet build` (the hook enforces this).
- [ ] Update `NEXT_STEPS_BACKLOG.md`'s handoff block, and `TASK_BOARD.md` if the items are filed there as tasks.
- [ ] Deploy with the `deploy` skill. Items 4, 5 and 6 carry migrations, which run on the container entrypoint.

## Also found in the audit, deliberately left out of the eight
- **`Character.svelte:370` uses `observedMaxPlayerHp`** while `PlayerMaxHp` is on the wire, and `Combat.svelte:157` uses it. This is a one-line fix plus deleting the estimate (`game.ts:66`). Do it as a standalone small PR at any time.
- `MailboxInstances` has no index on `PlayerId`.
- Offline catch-up rolls up to 200,000 loot rolls one at a time (`OfflineSimulationEngine.cs:94-103, 954`). A binomial draw per table entry would do the same work in far fewer steps.
- Lock ordering in market matching (`MarketOrderBookEngine.cs:412-474`). It only matters at a higher population.
- Only 1 of the 26 screens needs to load at startup, but all of them do (`App.svelte:3-31`, one 644 KB chunk).
