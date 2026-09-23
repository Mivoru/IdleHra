# Task 25: World boss, no attack has ever landed

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Project skills used below: `run-stack`, `verify`, `add-command` (only if a wire field or command is added), `deploy`. The `wiring-auditor` agent is used once, in Task 1.

**Goal:** A player who presses "Strike plate N" during an open world boss window either lands the blow (an attempt row in `player_world_boss_attempts`, `WorldBossSnapshots.CurrentHp` goes down, the pip is spent) or **is told on screen why not**. This must hold for the dev fixture and for a freshly registered account, on a window forced open locally. `exercise.mjs` must prove it every run, on any calendar day.

**Architecture:** One click travels through seven layers, and any one of them can drop it without a word:

```
WorldBoss.svelte button (disabled=…)            client_web/src/routes/WorldBoss.svelte:289-295
  -> attack() -> attackWorldBoss() refusals     WorldBoss.svelte:139-150, lib/net/commands.ts:359-405
  -> connection.send (JSON, stamps epoch)       lib/net/connection.ts:510-520
  -> NetworkBroadcastSystem JSON path           Network/NetworkBroadcastSystem.cs:9207-9211 -> ValidateAndEnqueue :7835
  -> tick: CommandGate.Evaluate                 Domain/Combat/SimulationEngine.cs:1402 -> Domain/Shared/CommandGate.cs
  -> dispatch table [AttackWorldBoss]           SimulationEngine.cs:811, :1418
  -> WorldBossTickCoordinator.HandleAttackWorldBoss   Domain/Combat/WorldBossTickCoordinator.cs:38-96
       -> ClientCommandValidator.ValidateWorldBossAttackRequest (false => TerminateSessionForSecurity)
  -> WorldBossEngine.QueueAttack (bare Task.Run)      Engine/WorldBossEngine.cs:141-144
  -> WorldBossEngine.ExecuteAttackAsync (Serializable tx) Engine/WorldBossEngine.cs:486-612
  -> WorldBossAttemptUpdateQueue -> WorldBossTickCoordinator.DrainNotifications -> StateUpdatePacket
```

The window is opened and closed by `LiveOpsTickEngine.EvaluateWorldBossEventWindowAsync` (`Engine/LiveOpsTickEngine.cs:84-132`), once every 60 s (`ScaleIntervalTicks = 600` x `TickIntervalMs = 100`).

**Spec:** `docs/TASK_BOARD.md`, "## 25. World boss: no attack has ever landed" (line ~3364), its section intro (line ~3327), and task 10's "PHASE G RESULT" / "PHASE H RESULT" (lines ~1032-1150) for the plate design. Design doc: `docs/world_boss_design.md`.

## Global Constraints

- **CLAUDE.md's load-bearing rules apply.** The ones this task touches directly:
  - *Silent rollback is this server's favourite way to lie.* Every early `return` and `RollbackAsync` in the attack path must end with the player being told something. This is an acceptance criterion, not polish.
  - *A background worker that can throw is a feature that can vanish.* `QueueAttack` is a bare `Task.Run`, and the statements before the `try` in `ExecuteAttackAsync` can throw into nothing.
  - *Verify gameplay with `npm run exercise`.* A smoke test would pass a dead Strike button.
  - *A check that spends fixture state passes once and fails forever.* The exercise check must restore what it spends.
  - *The fixture cannot verify the new-player experience.* Check with a fresh account too.
  - *Never hand-write a wire type.* If you add a `CommandResultCode` or any packet field, use the `add-command` skill and `npm run generate:protocol`.
  - *Raw SQL must match the table name.* `"WorldBossSnapshots"` is PascalCase and quoted. `player_world_boss_attempts` is snake_case (`[Table]` on `Models/PlayerWorldBossAttempt.cs:10`).
- **Stop the server before `dotnet build` / `dotnet test`.** The `guard_stale_build.py` hook blocks it anyway, and a blocked chained command runs nothing (see memory note "Hook-blocked commands run NOTHING"). Run build and test as separate calls, not chained after an edit.
- **Only one copy of the server suite can run on this machine at a time.** `SustainedLoadTests` and the live-socket tests bind port 8095. Check that no other agent is running `dotnet test` before you start one.
- **Production is read-only for investigation.** Only SELECTs through the Supabase MCP, and only reads on the Oracle box (`docker compose logs`, `redis-cli HGETALL`). Never poke the live snapshot row to open a window.
- **Do not change balance** (`BattleSessionCapSeconds = 300`, `MaxAttemptsPerEncounter = 3`, plate multipliers, `BaseHp`) unless the owner decides to. Two decision points are marked **OWNER DECISION** below.
- **Each task is its own commit.** Commit messages are written with the Write tool to a file, never with PowerShell `Set-Content`, which adds a BOM. End every commit with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

---

## Findings from reading the code (2026-09-23)

### What production says (read-only SQL, 2026-09-23)

| Fact | Value | Source |
|---|---|---|
| Snapshot row | `MaxHp = CurrentHp = 50,000,000`, `TotalDamageContributed = 0`, `EventState = 2`, `EventEndEpoch = 1790121599` (2026-09-22 23:59:59 UTC), `BrokenPlateMask = 0`, `WeakPlateIndex = 2`, `WeakPlateRevealed = 0`, `LastActiveTimestamp = 1790093523` (2026-09-22 16:12 UTC) | `SELECT * FROM "WorldBossSnapshots"` |
| Attempts | table **empty** | `player_world_boss_attempts` |
| Player 8 "Mivoru" larder | `LarderSlot1Count 8116, Slot2 9999, Slot3 9999` (full) | `"PlayerRecords"` |
| Player 8 time bank | `AccumulatedTimeBankSeconds = 555,597`. Not quarantined. | `"PlayerRecords"` |
| Player 8 activity | `LastLoginTimestamp` 2026-09-16 18:28 UTC (inside the window), `LastLogoutTimestamp` 2026-09-23 19:12 UTC | `"PlayerRecords"` |
| Penalties | `account_penalties` empty | |
| Schema | Both tables match the model: composite PK `(PlayerId, BossInstanceId)`, no FKs, column types as in the model | `information_schema` |

`EventEndEpoch` equals the Sep 22 window end, so `ActivateEventWindowAsync` did run for Sep 15-22. `WeakPlateIndex = 2` was freshly seeded, and nobody broke a plate.

### The deduction that narrows everything: the three named silent rollbacks CANNOT explain this

The task card and CLAUDE.md name three silent rollbacks in `ExecuteAttackAsync`: the attempt cap (`WorldBossEngine.cs:533`), the 300 s battle session (`:543`), and the empty larder (`:549`, fed by `WorldBossTickCoordinator.cs:57`). **For a player's first strike of an encounter, none of the three can fire:**

- Cap and session both read the attempt row. With no row, `attempt` is new, with `AttemptCount = 0` and `SessionStartEpoch = now` (`:520-531`), so `:533` and `:543` are false.
- The larder rule reads `payload.Food1_Count/2/3`, hydrated from `LarderSlot*Count` (`StateCheckpointManager.cs:980`), and player 8's larder is full.

A table that is empty for the whole window means **not one first strike committed**. So the cause is upstream of those three checks, or in the parts of `ExecuteAttackAsync` that are not them. Ranked:

### H1: The strike never reached an Active window (timing, and a screen that does not make it plain). Likely. Confirm with the owner first.

- The report came from the **2026-09-23** phone playtest (TASK_BOARD line ~3329). On the 23rd the boss is `Concluded` (`EventState = 2`) by design. The button is disabled by `eventState !== BossEventState.Active` (`WorldBoss.svelte:291`), and the screen says "This encounter is over. Returns on the 1st of next month." (`:185-189`). The same is true on the 8th-14th.
- Player 8 was online inside the window (login stamp Sep 16), so a real attempt was *possible*. But "I can never attack" said on the 23rd fits a disabled button exactly.
- **On screen:** a grey "Strike plate N" button, "Concluded" pill, "Returns on the 1st of next month". No click reaches the server, so nothing is logged anywhere.
- **Evidence to collect:** ask the owner which dates he tried, and whether the button was grey or pressable. That one question decides whether H2-H4 are live defects or latent ones.
- **Also a real defect even if H1 holds:** `exercise.mjs` only strikes `if (active)` (`client_web/scripts/exercise.mjs:1088`), which is true on 15 days a month (1st-7th, 15th-22nd). So on the other 13-16 days nothing in CI or on a dev box has pressed Strike. The "112/112 including four world boss checks" of 2026-09-05 ran inside a window by luck of the calendar. **This is why "no attack landed" could not have been caught.**

### H2: The command is killed at the gate or the validator, which is a disconnect, not a message. Medium.

Every refusal before the engine is `TerminateSessionForSecurity` (`SimulationEngine.cs:624-633`): `RemoveActivePlayer` + `PurgeTokensForPlayer` + `ForceDisconnect`. On the phone this looks like a reconnect blip (the client reconnects, `connection.ts:63-71`, or silently re-authenticates with the refresh token). Then the screen is back, unchanged, and nothing was logged in any table. From the player's side that is "I pressed it and nothing happened".

The terminate paths a real opcode-32 JSON command can hit:

1. `CommandGate.Evaluate` -> `ValidateEpochSynchronization` (`ClientCommandValidator.cs:49-64`). This kills every command type, so it would have shown up elsewhere. Low.
2. `CommandGate.Evaluate` -> `ValidateCommand` (`ClientCommandValidator.cs:212-253`). **Opcode 32 is in its rate-limited list**, so it terminates if *any* listed command (1, 2, 8, 24-30, 32, 33, 47-52) arrived under 100 ms before it (`:219`), or if `SpeedMultiplier > 1 && AccumulatedTimeBankMs < 100` (`:237`). A double-tap on a phone is exactly two opcode-32s inside 100 ms, and **the second one disconnects the player**. Note that the first one would still have written a row, so this alone does not explain an empty table. It is a real hostile-to-thumbs defect anyway. Low-medium as root cause, but fix it.
3. `ValidateWorldBossAttackRequest` (`ClientCommandValidator.cs:1026-1080`) returns false, and the caller terminates, on: `!eventActive` (`:1033`), `ClientPredictedDamage != 0` (`:1047`), plate `>= 5` (`:1055`), boss id `!= 1` (`:1061`), `bossIsDead` (`:1067`), or any of 20 unrelated fields non-zero (`:1073`). The current web client sends only `Command`, `TargetedBossId: 1`, `TargetedPlateIndex`, and `LogicEpochCounter` (`commands.ts:398-402`, `connection.ts:513-519`). The JSON codec leaves absent fields zero (`PacketJsonCodec.cs:572-601`). So a *current* client passes every one of them. A **stale client** would not: anything built before `928deca` (2026-09-05) posts `ClientPredictedDamage` and is disconnected on every strike. The phone runs an OTA bundle (`capacitor.config.json`, `/api/v1/app/bundle`). TASK_BOARD #28 already suspects the phone of running the APK's own 2026-09-12 bundle rather than the OTA one. The 09-12 bundle is post-plates, so this is only live if the device holds something older still. Low, but check the bundle version on the phone.
4. `eventActive` / `bossIsDead` are read from the in-memory mirror (`ctx.WorldBossEngine.IsEventActive`), which is refreshed at most once a minute (`EnsureSnapshotAsync` -> `RefreshLocalSnapshot`). A click in the last minute of a window, or the minute after a kill, is a **disconnect, not a refusal**. The client's own pre-check (`commands.ts:376`) reads the same mirror via the packet, so this only bites in that one-minute race. Low as root cause, but it is the wrong response to an honest client and gets fixed below.

**On screen:** a brief "reconnecting"/"Server terminated the session" state and no change to the boss. **Evidence:** Redis on the box keeps the last telemetry event per player: `HGETALL telemetry:last_event:8`. `event_type 3, value1 32` is a validator refusal and `value2` says which (1 = damage, 2 = boss id, 3 = dead, 4 = inactive or extra fields, 5 = plate). `event_type 3, value1 32, value2 1|2` from `ValidateCommand` is rate or speed. `event_type 5` is epoch drift. `telemetry:hot_counts` field `8:3` counts them. These are overwritten by later events, so read them early.

### H3: `ExecuteAttackAsync` throws, and the throw is invisible. Medium-low as root cause, certain as a defect.

- `QueueAttack` is `_ = Task.Run(async () => await ExecuteAttackAsync(...))` (`WorldBossEngine.cs:141-144`). `CreateScope`, `GetRequiredService` and **`BeginTransactionAsync(Serializable)` at `:498-500` sit outside the `try`**. A connection-acquire failure (the bounded pool under load, see CLAUDE.md on `EMAXCONNSESSION` and `ConnectionStringDefaults.WithBoundedPool`) becomes an unobserved task exception: no log, no row, no message.
- Inside the `try`, any exception (for example a Postgres `40001` serialization failure against `ScaleActiveBossAsync`, which updates the same row under `FOR UPDATE` whenever the online count changes, `:146-218`) is rolled back and **only `Console.WriteLine`d** (`:607-611`). The player sees nothing.
- **On screen:** exactly nothing. The button stays live, the pip stays unspent. **Evidence:** `docker compose logs app | grep -i "world boss"` on the box, looking for `World boss attack failed for player 8`, `World boss scale update failed`, or `LiveOps ticker failed`.

### H4: The button is not reachable on a 390 px phone. Low-medium; cheap to rule out.

The Strike button is the last element of the panel (`WorldBoss.svelte:289-295`). If a fixed bottom bar or the gesture inset covers it, a tap lands on the bar. `check:overlap` and `check:safearea` exist for this. Confirm that `worldboss` is in `client_web/scripts/screens.mjs` and that those checks scroll to the button rather than measuring the first viewport only (the `check:touch` history in CLAUDE.md shows why that matters). **On screen:** the tap does something else, or nothing.

### Silent paths that exist regardless of which hypothesis wins

Every one of these must say why on screen after this task:

| Where | Condition | Today |
|---|---|---|
| `WorldBossEngine.cs:488-491` | `playerId <= 0`, wrong boss, `serverComputedDamage == 0` | silent return |
| `:493-496` | plate `>= PlateCount` | silent return (validator already terminates first) |
| `:498-500` | scope / connection / BeginTransaction throws | **unobserved Task exception** |
| `:508-512` | snapshot missing, `CurrentHp <= 0`, `EventState != 1` | silent rollback |
| `:533-537` | attempt cap | silent rollback (client pre-checks) |
| `:543-547` | 300 s session | silent rollback (client pre-checks) |
| `:549-553` | larder empty | silent rollback (client pre-checks) |
| `:607-611` | any exception | console only |
| `WorldBossTickCoordinator.cs:43-52` | validator false, including the honest races (`!eventActive`, `bossIsDead`) | **disconnect** |
| `CommandGate` -> `ValidateCommand` | two taps within 100 ms | **disconnect** |

### Tests: why they are green

`WorldBossArmourTests` (9 tests) and `HardenedEngineIntegrationTests.Test_WorldBoss_*` (`:576`, `:632`) all call `engine.ExecuteAttackAsync(...)` directly. **None goes through `PacketJsonCodec` -> `CommandGate` -> dispatch table -> `WorldBossTickCoordinator` -> `ValidateWorldBossAttackRequest` -> `QueueAttack`.** That is the whole path a phone uses, and it has no test. The harness to drive it already exists: `E2ETestHarness.BuildEngineGraph` (`server/FolkIdle.Server.Tests/E2ETestHarness.cs:87`) and the live-socket pattern at `HardenedEngineIntegrationTests.cs:4424-4450` (port 8095, `MintTestJwt`, `BuildAuthHandshakeBuffer`).

### How a window can be forced open locally: today, it cannot

- No admin or dev endpoint touches the boss. `HandleAdminEndpoints` (`NetworkBroadcastSystem.cs:9512+`) has status, profanity, announce, penalties, ban, unban and mail only. `IsAdmin` (`:9505`) is username `Mivoru` or one e-mail, so the dev fixture (username `dev`, `Models/DevFixtureSeeder.cs:33`) is **not** an admin by that check, despite the comment at `exercise.mjs:2709`. Verify before relying on it.
- **A SQL poke does not work.** Setting `EventState = 1` in the local DB is picked up by `EnsureSnapshotAsync` on the next LiveOps tick, and in the **same** tick `!shouldBeActive && IsEventActive` calls `FinalizeEventAsFailedAsync` (`LiveOpsTickEngine.cs:117-120`). Outside the 1st-7th and 15th-22nd the window lives for less than a minute and is never seen by a client.
- Calling `ActivateEventWindowAsync` directly has the same fate a minute later.

So Task 2 builds a dev-only override that LiveOps respects.

---

## How to approach this (for the implementing agent)

1. **Ask before you build (5 minutes, saves hours).** Ask the owner, in Czech: *which days did you try the world boss, and was the Strike button grey or could you press it? Did the app flash "reconnecting" after pressing?* A grey button on the 23rd means H1, and the rest is hardening plus the exercise gap. A pressable button that did nothing means H2 or H3, and the production evidence below decides which.
2. **Collect production evidence before it is overwritten** (read-only, on the Oracle box; see the `deploy` skill for the SSH details):
   ```bash
   docker compose logs app --since 2026-09-15 2>&1 | grep -iE "world boss|LiveOps ticker failed" | tail -50
   docker compose exec redis redis-cli HGETALL telemetry:last_event:8
   docker compose exec redis redis-cli HGET telemetry:hot_counts 8:3
   docker compose exec redis redis-cli HGET telemetry:hot_counts 8:5
   ```
   Also note which OTA bundle version the phone reports (Settings, or `CapacitorUpdater.current()`), for H2.3. Write the findings into the TASK_BOARD entry, whatever they are.
3. **Run the `wiring-auditor` agent once** on "AttackWorldBoss: button -> attempt row -> HP on the wire". Give it this plan's architecture diagram and ask for the first missing link. It is a second pair of eyes on H2-H4, and it can see links this plan missed. Do not let it edit.
4. **Reproduce locally before fixing anything.** Start the stack with the `run-stack` skill. Land Task 2 (the forced window) first, because nothing else can be reproduced without it. Then, as the dev fixture in a browser at 390 px width, open World Boss and strike. Then do the same as a freshly registered account. Watch the server console for `World boss attack failed`, and look at the DB:
   ```sql
   SELECT * FROM "WorldBossSnapshots";
   SELECT * FROM player_world_boss_attempts;
   ```
5. **TDD: the failing test reproduces what a real client does, not what the engine API allows.** Task 3 writes it first, through `PacketJsonCodec` with the exact JSON the browser sends, the gate, the dispatch table and the coordinator, and asserts a row, HP moved, and the session still alive. If it passes on unmodified code, that is itself a finding: the server path works, which points at H1/H4 (client) or at production-only H3. Keep the test either way, because this path had no test at all. Then write the refusal tests (Task 4) red, then make them green.
6. **"A refusal says why on screen" is an acceptance criterion.** Every row of the silent-paths table above ends in a `CommandResultCode` the client renders through `lib/stores/commandResults.ts`. An honest client is never disconnected for a state race.
7. **Verify with the `verify` skill,** in its order: server tests, then `npm run check:ratchet`, then client unit tests, then `npm run exercise`, then the geometry checks for the World Boss screen.
8. **Deploy with the `deploy` skill** (the owner gave standing approval on 2026-09-23 to deploy after each merged fix). Then confirm on production what can be confirmed without a window (Task 8).

---

## Tasks

### Task 1: Evidence and hypothesis decision (no code)

**Files:**
- Modify: `docs/TASK_BOARD.md` (task 25: add an "Investigation 2026-09-2x" subsection)

- [ ] **Step 1:** Ask the owner the question in "How to approach this" §1. Record the answer verbatim.
- [ ] **Step 2:** Collect the Oracle box evidence in §2. Record the relevant log lines and the telemetry hash contents.
- [ ] **Step 3:** Re-run the production SELECTs (read-only) to check that nothing moved:
  ```sql
  SELECT "EventState","CurrentHp","MaxHp","TotalDamageContributed", to_timestamp("LastActiveTimestamp") FROM "WorldBossSnapshots";
  SELECT count(*) FROM player_world_boss_attempts;
  ```
  Expected: unchanged (`2`, 50M, 50M, 0; count 0) until Oct 1.
- [ ] **Step 4:** Dispatch the `wiring-auditor` agent (§3). Paste its first-missing-link verdict into the TASK_BOARD entry.
- [ ] **Step 5:** Mark H1-H4 as confirmed, refuted, or open in the TASK_BOARD entry. **Tasks 2-7 are needed whichever hypothesis wins.** Only the "root cause" sentence in the final report depends on this.
- [ ] **Step 6: Commit** `docs(task-25): production evidence for the world boss report`.

### Task 2: A dev-only way to open and close a window that LiveOps respects

**Files:**
- Modify: `server/FolkIdle.Server/Engine/WorldBossEngine.cs` (a manual-window override)
- Modify: `server/FolkIdle.Server/Engine/LiveOpsTickEngine.cs:84-132` (`shouldBeActive` respects it)
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs` (a dev route near `HandleAdminEndpoints`, ~`:9512`)
- Modify: `run-dev.ps1` (set `FOLKIDLE_DEV_TOOLS=1` for the server process, beside `FOLKIDLE_DB_CONN` at lines 25 and 32)
- Modify: `client_web/src/lib/net/rest.ts` (typed helpers used by `exercise.mjs` only)
- Test: `server/FolkIdle.Server.Tests/WorldBossWindowOverrideTests.cs` (new)

**Design:**
- `WorldBossEngine` gets `long _manualWindowEndEpoch` (0 = none), `public bool IsManualWindowOpen(long now)`, `public Task OpenManualWindowAsync(long durationSeconds)` (sets the override, then calls `ActivateEventWindowAsync(now + duration)`, which already resets HP, plates, the weak seed and **deletes every attempt row**), and `public Task CloseManualWindowAsync()` (clears the override, then `FinalizeEventAsFailedAsync()`).
- `LiveOpsTickEngine.EvaluateWorldBossEventWindowAsync`: `bool shouldBeActive = inWindowA || inWindowB || _worldBossEngine.IsManualWindowOpen(nowEpoch);`, and the calendar `windowEnd` is used only when the manual window is not open. Add a `// Modul:` comment explaining that without this, `FinalizeEventAsFailedAsync` kills a poked window within one 60 s tick, which is why no SQL poke can open it.
- Route: `POST /api/v1/dev/worldboss/window` with body `{ "open": true, "durationSeconds": 900 }` or `{ "open": false }`. It is authenticated with `TryResolveAuthenticatedPlayerAsync` and **answers 404 unless `FOLKIDLE_DEV_TOOLS == "1"`**, so it fails closed in production, like the other dev gates (`FOLKIDLE_ALLOW_DEV_SEED`, `Program.cs:77-83`). Clamp `durationSeconds` to 60-86,400. Do not route it under `/api/v1/admin/`, because the fixture is not `IsAdmin`.
- Is the route in Caddy's proxied prefixes? Check `ops/oracle/caddy/Caddyfile`. It does not matter for production (it 404s there), but the local Vite proxy must forward `/api/v1/dev/`. Check `client_web/vite.config.ts`.

- [ ] **Step 1: Write the failing tests** in `WorldBossWindowOverrideTests` (Testcontainers collection, same fixture shape as `WorldBossArmourTests`):
  - `AManualWindowSurvivesALiveOpsTickOutsideTheCalendar`: open a manual window, run one `EvaluateWorldBossEventWindowAsync` (make it `internal` and add `InternalsVisibleTo` if needed, or add an injectable clock `Func<DateTimeOffset>` to `LiveOpsTickEngine`, which is preferable so the test does not depend on today's date). Assert `EventState == 1` afterwards.
  - `ClosingTheManualWindowConcludesIt`: `EventState == 2`, and the next tick does not reopen it on an out-of-calendar day.
  - `OpeningClearsAttempts`: a pre-existing attempt row is gone.
- [ ] **Step 2:** Stop any running server. Then, as its own call: `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter FullyQualifiedName~WorldBossWindowOverrideTests`. Expected: FAIL (the members do not exist).
- [ ] **Step 3:** Implement. Then, as a separate call: `dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj`.
- [ ] **Step 4:** Re-run the filter. Expected: PASS.
- [ ] **Step 5: Manual repro** with the `run-stack` skill. Sign in as the fixture, then:
  ```bash
  curl -s -X POST http://localhost:<server-port>/api/v1/dev/worldboss/window -H "Authorization: Bearer <jwt>" -H "Content-Type: application/json" -d '{"open":true,"durationSeconds":900}'
  ```
  Expected: within one broadcast the World Boss screen shows **Active** with a countdown, and it stays Active past the next LiveOps tick (wait more than 60 s).
- [ ] **Step 6: Now reproduce the report.** As the fixture, at 390 px, strike once. Record whether a row appears in `player_world_boss_attempts`. Repeat with a freshly registered account (see Task 7 for its larder). **Write down what you saw before changing anything else.** This is the local answer to H2-H4.
- [ ] **Step 7: Commit** `feat(worldboss): a dev-only window override that LiveOps respects`.

### Task 3: The failing test, the path a real client takes

**Files:**
- Test: `server/FolkIdle.Server.Tests/WorldBossClientPathTests.cs` (new)

- [ ] **Step 1: Write the test.** Build the graph with `E2ETestHarness.BuildEngineGraph(...)`. Log a seeded player in so it is in `_activePlayers`, with a stocked larder (as the fixture has) and its `LogicEpochCounter` known. Open a window with `OpenManualWindowAsync(900)`. Then decode **the exact JSON string the browser sends**:
  ```csharp
  string json = $"{{\"type\":\"ClientCommand\",\"LogicEpochCounter\":{epoch},\"Command\":32,\"TargetedBossId\":1,\"TargetedPlateIndex\":3}}";
  Assert.True(PacketJsonCodec.TryDeserialize(Encoding.UTF8.GetBytes(json), out ClientCommandPacket cmd, out var err), err);
  networkSystem.CommandQueue.Enqueue(new PlayerCommand { PlayerId = playerId, Packet = cmd });
  ```
  Poll for up to 10 s, then assert:
  1. a `player_world_boss_attempts` row with `AttemptCount == 1`;
  2. `WorldBossSnapshots.CurrentHp < MaxHp`;
  3. the player is **still** in the active set (it was not terminated);
  4. the next `StateUpdatePacket` for that player has `WorldBossAttemptCount == 1` and `WorldBossSessionEndsEpoch > 0`.

  Check that the `"type"` discriminator key matches what `PacketJsonCodec.TypePropertyName` actually is (`PacketJsonCodec.cs:72` gives `TypeClientCommand = "ClientCommand"`). Take it from the constant rather than retyping it.
- [ ] **Step 2: A second test** sends **two** strikes 50 ms apart, which is a phone double-tap. Assert that the session survives and that the second one yields a visible refusal or a second attempt, **never a termination**. This is expected to FAIL today via `ValidateCommand`'s 100 ms rule (`ClientCommandValidator.cs:219`).
- [ ] **Step 3:** Run both (server stopped; as its own call): `dotnet test ... --filter FullyQualifiedName~WorldBossClientPathTests`. Record the result. **If test 1 passes on unmodified code**, write that into the TASK_BOARD entry. The server path works end to end, so the production cause is H1, H4 or a production-only H3, and the Task 1 evidence decides which.
- [ ] **Step 4: Commit** the tests (red where red) `test(worldboss): the path a real client takes, codec to attempt row`. If your workflow forbids committing red tests, fold this commit into Task 4's.

### Task 4: Every refusal says why, and an honest client is never disconnected

**Files:**
- Modify: `server/FolkIdle.Server/Network/StateUpdatePacket.cs:81+` (`CommandResultCode`: add codes from 37; the last used is `BreedingFailed = 36`)
- Modify: `server/FolkIdle.Server/Engine/WorldBossEngine.cs:141-144, 486-612`
- Modify: `server/FolkIdle.Server/Domain/Combat/WorldBossTickCoordinator.cs:38-96`
- Modify: `server/FolkIdle.Server/Engine/ClientCommandValidator.cs:212-253` (opcode 32 and the 100 ms rule) and `:1026-1080`
- Modify: `client_web/src/lib/stores/commandResults.ts` (messages for the new codes)
- Regenerate: `client_web/src/lib/net/protocol.generated.ts` (use the `add-command` skill's steps: `npm run generate:protocol`)
- Test: extend `WorldBossClientPathTests`, `WorldBossArmourTests`, `CommandGateOrderingTests`; `client_web/tests/commandResults.test.ts`

**Codes (proposed; use the `add-command` skill to place them):** `WorldBossNotActive = 37`, `WorldBossAlreadyDefeated = 38`, `WorldBossNoAttemptsLeft = 39`, `WorldBossSessionClosed = 40`, `WorldBossLarderEmpty = 41`, `WorldBossStrikeFailed = 42` ("could not be recorded, nothing was spent, try again").

- [ ] **Step 1: Write the failing tests first.** For each silent path in the findings table, one test goes through the Task 3 path (JSON -> queue) and asserts both **the specific code in the player's command-result ring** and **no termination**: window closed; boss dead; 4th strike; strike after the session is closed (backdate `SessionStartEpoch` by 301 s in the DB); empty larder; a forced exception (for example drop the attempts table's permissions in a scoped test DB, or inject a failing `IServiceProvider`; pick whichever is least invasive). Keep the existing protocol-violation tests terminating: `ClientPredictedDamage != 0`, plate >= 5, boss id != 1, extra fields.
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - `ExecuteAttackAsync` returns a `WorldBossAttackOutcome` enum (or the code) instead of `void`. Each early return and rollback maps to a code. `QueueAttack` becomes a guarded dispatch: `Task.Run(async () => { try { var o = await ExecuteAttackAsync(...); if (o != Success) _playerRegistry.EnqueueCommandResult(playerId, (byte)code); } catch (Exception ex) { Console.WriteLine(...); _playerRegistry.EnqueueCommandResult(playerId, (byte)CommandResultCode.WorldBossStrikeFailed); } })`. Move `CreateScope`/`BeginTransactionAsync` inside the `try`. Add a `// Modul:` comment citing CLAUDE.md's "A guard that starts AFTER `CreateScope`/`BeginTransactionAsync` is not a guard". The engine already holds `_playerRegistry` (`WorldBossEngine.cs:58`).
  - On success, keep the existing `WorldBossAttemptUpdateQueue` notification (`:599-604`). Optionally also enqueue `Success` so the client can say "Strike landed: N damage". That needs damage on the wire, and it is **not** in scope unless trivial.
  - `ValidateWorldBossAttackRequest`: split *state* from *protocol*. `!eventActive` and `bossIsDead` stop being `return false` (which terminates) and become a soft refusal that the coordinator reports with code 37/38 and then returns. Protocol violations still terminate. Keep the telemetry writes. `CommandGateOrderingTests` must stay green. If a pinned order changes, update the test **and** its comment.
  - `ValidateCommand`'s 100 ms rule: a second opcode 32 within 100 ms must not terminate. Least-risk fix: exempt opcode 32 from the list (its own caps, 3 per encounter and a Serializable row lock, already bound it), with a `// Modul:` note on the double-tap. **Do not** loosen the rule for the other opcodes in this task.
  - Client: add the six messages to `COMMAND_RESULT_MESSAGES` in `commandResults.ts`, in the same player-facing voice as codes 26-36.
- [ ] **Step 4:** `npm run generate:protocol` if the enum is dumped into the protocol. Then `node client_web/scripts/generate-protocol.mjs --check` from `client_web`, or whatever `npm run build` calls. Expected: clean.
- [ ] **Step 5:** Server tests (server stopped; separate calls): the full world boss filter (`~WorldBoss`), then `~CommandGateOrderingTests`, then `~StateUpdatePacketFieldCoverageTests` (a new wire field forces a hydration decision; command results are runtime-only by design, so check that they are listed in `RuntimeOnlyByDesign`). Expected: PASS.
- [ ] **Step 6:** `cd client_web; npm test` (or the repo's unit-test script) and `npm run check:ratchet`. Expected: PASS, with the four known `GuildOps.svelte` errors only.
- [ ] **Step 7: Commit** `fix(worldboss): every refusal says why, and a state race is not a disconnect`.

### Task 5: The screen tells the truth before and after a strike

**Files:**
- Modify: `client_web/src/routes/WorldBoss.svelte`

- [ ] **Step 1:** After `attack()` sends, disable the button until the next packet shows `WorldBossAttemptCount` changed **or** a command result arrived, with a timeout of about 5 s. This is the client half of the double-tap fix, and it stops a second tap being sent at all. Use a `$state` flag. Do not name any local binding `derived` (CLAUDE.md).
- [ ] **Step 2:** When the button is disabled because the event is not Active, say so **next to the button**, not only at the top ("The boss is not here today. Next encounter: the 1st."). This is the H1 fix: a grey button with its reason far above it on a phone reads as "broken".
- [ ] **Step 3:** Run `npm run check:overlap`, `npm run check:touch` and `npm run check:safearea` with the stack up, and read the `worldboss` rows specifically. Confirm that `worldboss` is in `client_web/scripts/screens.mjs`. If the Strike button is ever covered at 390 px (H4), fix the layout (bottom padding for the fixed bar, using `var(--safe-area-inset-bottom, env(safe-area-inset-bottom, 0px))`) and record it.
- [ ] **Step 4:** `npm run check:ratchet`. Expected: the baseline 4.
- [ ] **Step 5: Commit** `fix(worldboss): the strike button says why it is grey, and cannot double-send`.

### Task 6: `exercise.mjs` strikes every run, whatever the date

**Files:**
- Modify: `client_web/scripts/exercise.mjs:1043-1135` (the world boss block)
- Modify: `client_web/src/lib/net/rest.ts` (if the Task 2 helpers live there)

- [ ] **Step 1: Open the window itself.** At the top of the block, `POST /api/v1/dev/worldboss/window {open:true, durationSeconds:900}` with the fixture's token (the script already holds an authed session, so reuse its fetch helper). Opening **deletes every attempt row and resets HP** (`ActivateEventWindowAsync`, `WorldBossEngine.cs:397-416`), so the fixture always starts with 3 attempts and a fresh session. Wait for the screen to show `Active`, with a bounded wait of about 70 s max, because the mirror refresh can take one LiveOps tick. If the route returns 404 (a target without `FOLKIDLE_DEV_TOOLS`), record a **failure** that names the missing variable. Do not skip silently, because a skipped check is decoration.
- [ ] **Step 2: Strike and assert the world changed.** Read the HP from the progressbar's `aria-valuenow` (`WorldBoss.svelte:172`) before and after. Assert:
  - `'striking a plate spends an attempt'`: pips +1 (existing);
  - **new** `'the strike moved the boss HP'`: `after < before`;
  - **new** `'the strike is recorded on the server'`: poll `aria-valuenow`, or add a tiny dev GET that returns the fixture's attempt row, and assert `AttemptCount >= 1`.
- [ ] **Step 3: Assert a refusal is visible.** Close the window (`{open:false}`), wait for `Concluded`, and assert that the reason text next to the button (Task 5 Step 2) is present. Optionally, while still open, spend all 3 attempts and assert that the 4th is refused on screen with the Task 4 message, not a reconnect. Check `connection` status did not go to `reconnecting` (read the connection indicator the script already inspects elsewhere, or `page.on('websocket')` close events).
- [ ] **Step 4: Round-trip.** End the block with `{open:false}`. Opening the window already refunds attempts next run, so the fixture is left exactly as the calendar would have left it (Concluded, no active override). Nothing else is spent: a strike eats no food and no gold.
- [ ] **Step 5: Fresh account.** In the existing fresh-account browser context at the end of the script (CLAUDE.md, "The fixture cannot verify the new-player experience"), open the window, visit World Boss, and assert **one** of: the strike lands (HP moved), or the screen names the reason (for example the larder). Never "nothing happened". See Task 7 for the larder question.
- [ ] **Step 6:** Run it (the stack must be up; re-seed first if the fixture was spent, `--seed-dev` is idempotent):
  ```powershell
  cd client_web; npm run exercise
  ```
  Expected: all world boss checks PASS **on 2026-09-2x, a dormant calendar day**. Run it **twice in a row** to prove the round-trip. The second run must also be green.
- [ ] **Step 7: Commit** `test(exercise): open a world boss window, strike it, assert its HP moved`.

### Task 7: Fresh-account verification, and the larder rule

**OWNER DECISION.** A brand-new account's larder is empty: `LarderSlot*Count` is only written by the checkpoint, `LarderEngine` and the fixture seeder, never at registration. So by `WorldBossTickCoordinator.cs:57` a new player **cannot** strike the world boss until they have cooked or bought food and stocked it. The strike itself consumes no food. The rule dates from a "Modul 06/15" brief ("Auto-Eat food depletion also closes a player's World Boss battle session"). Ask the owner: keep it (and the fresh account is told "stock your larder" on screen, which Task 4 guarantees), or drop it for world boss strikes. Do not decide it yourself.

- [ ] **Step 1:** Register a fresh account through the UI on the local stack. Open a window (Task 2). Visit World Boss at 390 px. Record what it says and whether a strike lands.
- [ ] **Step 2:** If the owner keeps the rule, stock food through the normal game path (Larder screen), strike, and confirm a row and an HP drop. If the owner drops it, remove the `autoEatFoodDepleted` parameter end to end (`WorldBossTickCoordinator.cs:57`, `QueueAttack`, `ExecuteAttackAsync:549-553`, `commands.ts:385-387`, `WorldBoss.svelte:36-38, 212-222, 291`) together with its tests, and update `commands.ts`'s header comment (`:333-357`).
- [ ] **Step 3: Commit** only if code changed: `feat(worldboss): <owner's decision on the larder rule>`.

### Task 8: Full verification, deploy, docs

**Files:**
- Modify: `docs/TASK_BOARD.md` (task 25: status DONE, root cause, evidence, what the checks now prove)
- Modify: `CLAUDE.md` (only if a new trap was confirmed, see below)
- Modify: `docs/architecture/NEXT_STEPS_BACKLOG.md` (top section: task 25 closed; world boss attempts now visible)
- Modify: `client_web/src/lib/net/commands.ts:333-357` (header comment: the three silent rollbacks are no longer silent)

- [ ] **Step 1: Full suites**, using the `verify` skill order. Server stopped, one suite at a time, each its own call:
  ```powershell
  dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj
  dotnet test  server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj
  cd client_web; npm run check:ratchet; npm test; npm run build
  ```
  Then with the stack up: `npm run exercise` (twice), `npm run check:overlap`, `npm run check:touch`, `npm run check:safearea`, `npm run check:clipping`. Expected: green, and svelte-check at the 4 known errors.
- [ ] **Step 2: PR, merge, deploy** with the `deploy` skill (SSH push; `git pull` does not work on the box). `FOLKIDLE_DEV_TOOLS` must **not** be set in `ops/oracle` env files. Check `ops/oracle/.env` and `docker-compose.yml`.
- [ ] **Step 3: Production checks that do not need a window:**
  - `curl -s -o /dev/null -w "%{http_code}" -X POST https://folkidle.duckdns.org/api/v1/dev/worldboss/window` returns **404** (it fails closed).
  - `npm run smoke:screens` with `FOLKIDLE_E2E_BASE=https://folkidle.duckdns.org` (the only production-safe script) shows World Boss rendering, with the new "not here today" line.
  - Confirm that the OTA bundle endpoint names the new version (`POST /api/v1/app/bundle`), so the phone will pick up Task 5.
- [ ] **Step 4: Schedule the real proof.** The next live window opens **2026-10-01 00:00 UTC**. Add a TASK_BOARD note: on Oct 1-2 run `SELECT count(*) FROM player_world_boss_attempts` and the snapshot SELECT (read-only), and ask the owner to strike once from the phone. Task 25 is **verified in production only when that row exists.**
- [ ] **Step 5: Docs.** TASK_BOARD entry: the root cause as confirmed in Task 1 (or "not reproducible server-side; H1 confirmed by owner"), the list of silent paths closed, and the exercise gap. **CLAUDE.md:** add a short paragraph only for traps that are new and confirmed. Candidates:
  - *"A calendar-gated feature needs a way to open it, or it is untested most of the month."* `exercise.mjs` skipped the world boss strike on 16 of 31 days, and a SQL poke is finalised by LiveOps within one tick.
  - *"The shared 100 ms command rate limit terminates a double-tap"*, if it was not fixed generally.
  - Update the existing "Silent rollback is this server's favourite way to lie" paragraph to say the three `ExecuteAttackAsync` rollbacks now report codes 39-41.
- [ ] **Step 6: Commit** `docs(task-25): world boss attack fixed, evidence and what now guards it`.

---

## Risks

- **The fix may not be the cause.** If Task 3's client-path test passes on unmodified code, the server was never the problem and the owner hit H1 or H4. The hardening in Tasks 4-6 is still correct and required by the task card's "done when", but the TASK_BOARD entry must say plainly that the production cause was the calendar or the layout, not a server defect. Do not claim a server fix fixed it.
- **Softening the validator** (Task 4) changes what `CommandGateOrderingTests` pins. Change only the two state conditions (inactive, dead) and the opcode-32 rate rule, never a protocol check, and keep the telemetry writes so anti-cheat evidence is not lost.
- **The dev window route is a production attack surface if the env gate is wrong.** It must 404 unless `FOLKIDLE_DEV_TOOLS == "1"`, and Task 8 Step 3 checks that on the live URL.
- **`ActivateEventWindowAsync` deletes every attempt row server-wide.** On a dev box that is the point. Make sure nothing in production can call `OpenManualWindowAsync`.
- **Port 8095 / Docker contention with other agents.** Run the server suite alone.
- **The exercise wait for `Active`** can take one LiveOps tick (up to 60 s). If that is too slow, have the route also call `EnsureSnapshotAsync`, so the in-memory mirror, and therefore the next broadcast, flips immediately. `ActivateEventWindowAsync` already calls `RefreshLocalSnapshot`, so it should be immediate. Measure it.

## What would change this plan

- **The owner says the button was grey and it was the 23rd (or the 8th-14th):** H1 confirmed. Tasks 2, 5 and 6 are the substance. Task 4 is still required by the "a refusal says why" criterion, but the TASK_BOARD root cause is "UX plus an untested calendar gate", not a server defect.
- **Box logs show `World boss attack failed for player 8: ...`:** H3 confirmed, and the exception text decides the fix. `40001` means wrapping the transaction in `RetryingDbContextOptions` + `CreateExecutionStrategy().ExecuteAsync` like `CraftingEngine.cs:168`. A pool timeout means the pool bound or the queueing. Add a regression test for that exact exception.
- **Redis telemetry shows `value1 32`, or `event_type 5`, for player 8:** H2 confirmed, and `value2` names the check. If it is `ClientPredictedDamage` (value2 1), the phone runs a pre-2026-09-05 bundle: fix the OTA path (TASK_BOARD #28 shares this) before anything else.
- **The `wiring-auditor` finds a missing link this plan did not:** follow it first, and add it to the findings table.
- **The owner wants world boss testing in production** (not only dev): the override would need an admin gate (`IsAdmin`) plus an explicit production switch. That is a separate decision, not part of this task.
- **Task 36 (world boss reflex minigame) is approved before this lands:** do not merge the two. This task fixes the pipe, and 36 changes what flows through it. Keep the codes and the exercise check, because 36 will need both.
