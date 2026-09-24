# Task 36: the world boss shield wheel with parries - implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: use superpowers:subagent-driven-development or superpowers:executing-plans to run this plan task by task. Steps use checkbox (`- [ ]`) syntax. The project skills used below are `run-stack`, `verify`, `add-command` (Task 2.4 only), `deploy`, `code-review`, and `security-review` (a required gate in Phase 2).

**Goal:** replace "pick one of five plates" with the owner's shield wheel (free aim) and parries, as a 20-second, one-thumb strike. The client sends only timestamps and choices. The server scores them against its own schedule and deals `A x G x M x P` damage, with `M` in [1.0, 2.0] and the weak plate at 3.0x.

**Spec:** `docs/superpowers/specs/2026-09-24-world-boss-minigame-design.md`. **Every number comes from the spec. If the phone playtest changes one, change the spec first.**
**Brief:** `docs/superpowers/plans/2026-09-23-task-36-world-boss-minigame-design.md`.

**Architecture:**

```
WorldBoss.svelte ── Strike ──> POST /api/v1/worldboss/challenge ──> WorldBossChallengeRegistry (in memory, idempotent)
      │                               (eligibility = task 25's one function)      schedule from ShieldWheelSchedule.Generate(RandomNumberGenerator)
      ├─ ShieldWheel.svelte (rAF, angleAt from time) ── per spear ──> POST /api/v1/worldboss/throw ──> scorer + IsWeakPlateAsync ──> {Plate, Class, WeakHit}
      ├─ finish ──> POST /api/v1/worldboss/strike {Mode: Wheel|Auto} ──> ShieldWheelScorer (pure) ──> WorldBossStrikeQueue
      │                                                               tick: budgeted drain, A x G from payload ──> WorldBossEngine.QueueStrike
      │                                                               ExecuteAttackAsync (Serializable, FOR UPDATE): s, M, P, break, reveal, damage ──> TCS
      └─ Practice ──> /challenge {Practice:true} ... /practice/score (decoy weak plate, no damage)
```

## Global Constraints

- **Prerequisites merged:** task 25 (`fix/world-boss-attack`: the dev window, `WorldBossAttackOutcome`, the guarded `QueueAttack`, codes 38-42, the larder rule gone) and PR #22 (code 37). Rebase on `main` after both. **Re-read every line number below against the merged tree; they move.**
- **CLAUDE.md's load-bearing rules**, the ones this task touches:
  - *Silent rollback*: every return path of the strike completes with a named `WorldBossStrikeResult`.
  - *A guard that starts after `CreateScope`/`BeginTransactionAsync` is not a guard*: keep task 25's shape.
  - *An unbounded drain in a worker loop is a starvation bug*: the strike queue drain takes a budget.
  - *Never hand-write a wire type*: the only wire change is Task 2.4, done with the `add-command` skill. REST DTOs are hand-written by design and pinned by a mirror test.
  - *A field on `StateUpdatePacket` must be loaded at login or declared runtime-only*: Task 2.4 removes one; add nothing.
  - *Every multiplier declares a cap or a curve*: Task 1.1's ledger.
  - *A number a test prints is not a number a test checks*: assert every printed number.
  - *Verify gameplay with `npm run exercise`*, which must prove skill reaches damage (Task 3.3).
  - *A check that spends fixture state passes once and fails forever*: open a fresh window each run, and close it after.
  - *The fixture cannot verify the new-player experience*: a fresh account plays Practice.
  - Svelte traps: runes mode, no `derived` binding, `{@render}`, `{#if}` rather than `<details>`, nothing ticking inside a control, 44 px targets, safe-area insets on the fixed overlay.
- **Merged state to build on (checked 2026-09-24, `main` at `696c559`).** The codes are 37 `GuildWarsLocked` and 38-42 for the world boss. Two things in the coordinator have to be respected:
  - **A spent budget is refused in memory** (`WorldBossTickCoordinator`). The challenge eligibility must reuse that check, not open a transaction to discover it.
  - **The dev route refuses `DOTNET_ENVIRONMENT=Production` whatever `FOLKIDLE_DEV_TOOLS` says** (`NetworkBroadcastSystem.DevToolsEnabled`).
- **No automatic penalty of any kind.** Nothing here calls `RequestShadowBan`, sets `Quarantine_Active`, or sends per-tap WebSocket traffic.
- **Stop the server before `dotnet build`/`dotnet test`,** and run each as its own call. The hook blocks it anyway, and a blocked chained command runs nothing. Docker must be up for `dotnet test`. Only one suite may run on the machine at a time (port 8095).
- **Flag:** `FOLKIDLE_BOSS_MINIGAME` = `off` (default) | `practice` | `wheel`. Production stays `off` until the owner has played practice on the phone. It goes `wheel` only before the Oct 15-22 window, after Phase 2's security review.
- **Each task is one commit; each phase is one PR.** Write commit messages to a file with the Write tool and commit with `git commit -F`. Never use PowerShell `Set-Content`, which adds a BOM. End each message with the `Co-Authored-By` / `Claude-Session` lines.
- **Schedule:** Phase 1 by about Oct 3 (owner playtest), Phase 2 by about Oct 10, Phase 3 by about Oct 13. Flip to `wheel` on Oct 14.

---

## Phase 1 - the smallest playable slice: PRACTICE only, behind the flag (one PR)

Practice touches no attempt, no damage, no shared state and no wire. The owner can play it on the phone before it touches the pool.

### Task 1.1: the rules as pure code, with their own ledger

**Files:**
- Create: `server/FolkIdle.Server/Domain/Combat/WorldBossStrike/WorldBossStrikeRules.cs` (every constant in spec §3; `Classify(angleOffsetInPlate)`; `Score(landings) -> s`; `Multiplier(s) -> M`; `PlateMultiplier(landings, isWeak) -> P`; `BreakTarget(landings, weak, brokenMask) -> plate?`; `RevealByElimination(brokenMask, weak) -> bool`)
- Create: `server/FolkIdle.Server/Domain/Combat/WorldBossStrike/ShieldWheelSchedule.cs` (records `Segment`, `Interrupt`, `ShieldWheelSchedule`; `AngleAt(ms)`; `PlateAt(ms)`; `FrozenAt(ms)`; `Generate(RandomNumberGenerator, bool practice)`)
- Create: `server/FolkIdle.Server/Domain/Combat/WorldBossStrike/ShieldWheelScorer.cs` (`Score(schedule, log, receivedAfterIssueMs) -> ScoredAttempt { Landings[], SpearsLost, Verdict, Suspicion flags }`; never throws)
- Create: `server/FolkIdle.Server.Tests/Fixtures/shield_wheel_cases.json` (hand-built schedules, logs and the expected landing angle, plate, class and verdict)
- Create: `server/FolkIdle.Server.Tests/ShieldWheelScorerTests.cs`
- Create: `server/FolkIdle.Server.Tests/WorldBossStrikeLedgerTests.cs`

- [ ] **Step 1: Write the failing wheel tests** against one hand-built schedule (not generated), read from the fixture:
  - *perfect*: 5 taps at seam centre-crossings minus `FlightMs` give 5 Seam, `s = 1`, `M == 2.0`.
  - *late device*: the same log shifted +30 ms is still 5 Seam (the tolerance).
  - *tolerance cannot change plate*: a tap 2 degrees inside plate 2's border, whose window reaches plate 3's seam, stays plate 2 (Plate class, not a plate-3 Seam).
  - *reversal*: a tap across a direction flip lands where `AngleAt` says, not where constant speed would.
  - *frozen tap*: a non-counter tap inside an interrupt is dropped, not refused, and sets suspicion detail 3.
  - *spray*: 5 evenly spaced taps give `M` in [1.0, 1.6].
  - *no taps* gives `M == 1.0`, `P == 1.0`, no break.
  - *one miss forgiven*: 4 Seam + 1 None gives `M == 2.0`.
- [ ] **Step 2: Write the failing parry tests:**
  - *correct read*: `Tell = Left`, choice `DodgeRight` at +300 ms, counter on plate 4 at +900 ms gives a Seam on plate 4.
  - *each wrong mapping* (the 6 wrong pairs) loses a spear and opens no counter.
  - *guess*: the correct choice at +100 ms (under the 150 ms floor) loses a spear and sets suspicion detail 2.
  - *late*: a choice at +1,100 ms loses a spear.
  - *missing choice* for a reached interrupt loses a spear.
  - *unreached interrupt*: all 5 spears thrown before `TellAtMs` means no spear lost.
  - *counter outside its window* is dropped.
  - *counter without a read* is a shape refusal.
  - *two correct reads + 3 random* gives `M >= 1.6` (spec §3.1).
- [ ] **Step 3: Write the failing shape/cheat tests.** Each must produce `Verdict = Refused` with its detail code, and **never an exception**:
  - 6 taps;
  - non-increasing times;
  - a duplicate `Seq`;
  - a negative time;
  - a time past `MaxPlayMs`;
  - two wheel taps 100 ms apart;
  - an unknown interrupt index;
  - plate 7;
  - `NaN` or `1e300` (parsed at the DTO layer; assert the parser maps them to refusal);
  - `receivedAfterIssueMs < CountdownMs + lastTap - 50`.

  One *precision* case (every tap within 1 ms of its seam crossing) is **accepted** at `M == 2.0` with suspicion detail 1. The test pins that suspicion never lowers a score.
- [ ] **Step 4: Write the failing board tests:**
  - `BreakTarget` picks the first non-weak, unbroken plate in tap order with class Plate or better, and returns null for Glance-only or weak-only attempts.
  - `RevealByElimination` is true exactly when the mask covers the four non-weak plates.
  - `P` is 3.0 for all-weak, 1.0 for none, and the mean in between.
- [ ] **Step 5: Implement** until green. `AngleAt` integrates the segments from `StartAngleDeg`, modulo 360. The class is computed from the offset inside the 72 degree sector (rivet bands `[0, 3)` and `(69, 72]`, seam `|offset - 36| <= 6`). Tolerance samples every 5 ms, restricted to the exact-tap plate.
- [ ] **Step 6: The ledger** (`WorldBossStrikeLedgerTests`). Print, and **assert**, each of:
  - `Cap <= 2.0`;
  - `Floor == 1.0`;
  - `M` is non-decreasing in `s` (0..1 in 0.01 steps);
  - `M(0) == Floor`;
  - `M(SaturationScore) == Cap`;
  - `P <= WeakPlateDamageMultiplier == 3.0`;
  - `M x P <= 6.0`;
  - the Giantslayer cap x 6.0 stays under a stated `WorldBossMaxStrikeFactor` constant (compute Giantslayer's cap from its registry; do not hard-code it);
  - a random-tap simulation (fixed seed, 20,000 attempts) has mean `M` in [1.25, 1.45];
  - a "two reads + random" simulation has mean `M` in [1.65, 1.85].

  These are spec §3.1's numbers, and the test is what makes them numbers the build checks.
- [ ] **Step 7: The generator property test.** Across 10,000 `Generate` outputs, check that:
  - every speed is in [90, 210] or exactly 0 for an interrupt;
  - durations are in [700, 2,200] (non-frozen);
  - the total is >= `MaxPlayMs`;
  - the interrupt count is in {2, 3};
  - the placement rules of spec §3 hold;
  - **each of the 5 plates' seams passes the impact point at least 3 times in non-frozen time**. A plate that never comes round makes the counter the only way to hit it, which is a broken schedule.
- [ ] **Step 8:** Stop the server, then `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~ShieldWheel|FullyQualifiedName~WorldBossStrikeLedger"`. Expected: PASS.
- [ ] **Step 9: Commit** `feat(world-boss): shield wheel and parry rules, pure, scored and ledgered`.

### Task 1.2: the challenge registry and practice over REST

**Files:**
- Create: `server/FolkIdle.Server/Domain/Combat/WorldBossStrike/WorldBossChallengeRegistry.cs` (a `ConcurrentDictionary<long, Challenge>` for real challenges and another for practice; `IssueOrGet`, `RecordThrow` (idempotent by `Seq`), `TryTake`, `ExpireDue(now)`; 128-bit `ChallengeId` from `RandomNumberGenerator`)
- Create: `server/FolkIdle.Server/Domain/Combat/WorldBossStrike/WorldBossStrikeResult.cs` (the enum from spec §5.7, plus a static `AllPlayerFacing` list for the mirror test)
- Create: `server/FolkIdle.Server/Domain/Combat/WorldBossStrike/WorldBossStrikeTelemetry.cs` (the single writer of `EventType = 8`, `Value1 = 32`)
- Create: `server/FolkIdle.Server/Domain/Combat/WorldBossStrike/BossMinigameMode.cs` (`Off | Practice | Wheel`, parsed once from `FOLKIDLE_BOSS_MINIGAME`, unknown -> `Off`)
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs` (routes beside the Delve's, ~`:1065`: `GET|POST /api/v1/worldboss/challenge`, `POST /api/v1/worldboss/throw`, `POST /api/v1/worldboss/practice/score`; `/strike` stays `Disabled` until Phase 2)
- Modify: `server/FolkIdle.Server/Program.cs` (register the registry and the mode)
- Create: `server/FolkIdle.Server.Tests/WorldBossChallengeRegistryTests.cs`
- Create: `server/FolkIdle.Server.Tests/WorldBossStrikeTelemetryTests.cs`

- [ ] **Step 1: Failing tests:**
  - issuing twice returns the same challenge (`Outstanding`);
  - a practice challenge carries a **decoy** weak plate, drawn per challenge, and its throw answers never consult `WorldBossEngine`. Assert with a fake engine that throws if asked;
  - the flag `off` gives `Disabled` on every route;
  - `practice` gives `Disabled` on a real challenge and on `/strike`;
  - `RecordThrow` with a repeated `Seq` returns the stored answer;
  - a `Seq` beyond the spears left (after lost spears) gives `OutOfSpears`;
  - a `TooEarly` throw is not stored;
  - `ExpireDue` removes practice challenges silently. Real-challenge expiry lands in Phase 2.
- [ ] **Step 2: Failing telemetry test:** grep the server source tree (reuse `CronWorkerGuardTests.ServerSourceRoot`'s walk) and fail if `EventType = 8` appears in any file but `WorldBossStrikeTelemetry.cs`.
- [ ] **Step 3: Implement.** Every refusal answers **200 with a `Result`**, following the Delve precedent (`HandleDelveAction`, `NetworkBroadcastSystem.cs:~2779`). Malformed JSON answers 400. A missing token answers 401. Parse numbers defensively: a non-finite or out-of-range `TapMs` becomes a scorer refusal, never an exception.
- [ ] **Step 4:** Run the filter `~WorldBossChallengeRegistry|~WorldBossStrikeTelemetry|~ShieldWheel`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(world-boss): practice challenges over REST behind FOLKIDLE_BOSS_MINIGAME`.

### Task 1.3: the client wheel, practice only

**Files:**
- Create: `client_web/src/lib/game/shieldWheel.ts` (`angleAt`, `plateAt`, `classAt`, `interruptAt`, `correctChoice`; drawing and preview only)
- Create: `client_web/src/lib/ui/ShieldWheel.svelte`
- Modify: `client_web/src/lib/net/rest.ts` (DTOs plus `issueChallenge`, `throwSpear`, `scorePractice`; `strike` arrives in Phase 2)
- Modify: `client_web/src/routes/WorldBoss.svelte` (a **Practice** button shown when mode is not `off`; the plate buttons stay)
- Create: `client_web/tests/shieldWheel.test.ts` (reads `server/FolkIdle.Server.Tests/Fixtures/shield_wheel_cases.json` and agrees with it case by case)
- Create: `client_web/tests/worldBossResults.test.ts` (every `WorldBossStrikeResult` has a sentence; the list is compared to a server-exported JSON, `server/FolkIdle.Server.Tests/Fixtures/world_boss_results.json`, which a server test regenerates-and-compares from the enum)
- Modify: `client_web/scripts/screens.mjs` (one destination: World Boss with the practice overlay open, so the geometry checks see it)

- [ ] **Step 1:** Write the failing `shieldWheel.test.ts` against the fixture (landing angle, plate, class, correct choice, frozen intervals).
- [ ] **Step 2: Build the component** to spec §7:
  - one rAF loop and one `style.transform` write per frame;
  - `pointerdown` timestamps relative to `t0`;
  - the lower-half throw zone locked for `MinReloadMs`;
  - parry and plate buttons rendered with `{#if}` only inside their windows;
  - the overlay `position: fixed` with all four safe-area insets;
  - `visibilitychange` -> hidden submits what was thrown;
  - `navigator.vibrate` guarded;
  - `data-schedule` on the overlay root.
- [ ] **Step 3: The practice card:** per spear plate/class (and "weak" from the decoy), `M`, `P`, "no damage dealt".
- [ ] **Step 4:** `cd client_web; npm test; npm run check:ratchet` (the baseline stays 4). Then **load the page** with the `run-stack` skill and `FOLKIDLE_BOSS_MINIGAME=practice` in the server env (add it to `run-dev.ps1` beside `FOLKIDLE_DEV_TOOLS`), and play three practice runs by hand at 390 px. svelte-check does not catch the Svelte runtime traps.
- [ ] **Step 5:** `npm run check:touch`, `check:overlap`, `check:clipping`, `check:safearea` with the overlay entry, then `check:perf` for 12 s while the wheel spins at 4x throttle. All must stay within their budgets.
- [ ] **Step 6: Commit** `feat(world-boss): the shield wheel with parries, practice mode`.

### Task 1.4: exercise plays practice; ship practice

**Files:**
- Modify: `client_web/scripts/exercise.mjs` (the world boss block, ~`:1043`, as task 25 left it)

- [ ] **Step 1: Practice run, aimed.** Open Practice and read `data-schedule`. For each interrupt, click the correct parry button at `TellAtMs + 250` and then a counter plate. Tap the wheel at computed seam crossings minus `FlightMs`. Assert the card shows `M >= 1.6` and "no damage dealt", and that **the boss HP and attempt pips did not move**.
- [ ] **Step 2: Practice run, wrong reads.** Click the wrong parry each time. Assert the card shows fewer thrown spears (a lost spear is visible).
- [ ] **Step 3: New player.** In the fresh-account context at the end of the script, open World Boss and complete one practice run.
- [ ] **Step 4:** Run the verify order: server tests (filtered), `npm run check:ratchet`, `npm test`, `npm run exercise` (with `FOLKIDLE_BOSS_MINIGAME=practice`), run twice.
- [ ] **Step 5: PR** "task 36 phase 1: practice". Run `code-review`. After the merge, **deploy with `FOLKIDLE_BOSS_MINIGAME=practice`** in `ops/oracle` env (the `deploy` skill; SSH push, not `git pull`) and confirm `smoke:screens` against production. Ask the owner to play practice on the phone, over the OTA bundle.

**PHASE GATE: the owner plays practice on the phone. Record the verdict and any retuned numbers in the spec (§3) before Phase 2. Do not tune in code first.**

---

## Phase 2 - scoring becomes damage (one PR; `security-review` required)

### Task 2.1: the strike queue and the engine

**Files:**
- Modify: `server/FolkIdle.Server/Engine/PlayerSessionRegistry.cs` (`WorldBossStrikeQueue` of `WorldBossStrikeOrder`)
- Modify: `server/FolkIdle.Server/Domain/Combat/WorldBossTickCoordinator.cs` (a budgeted drain; **extract** the existing A x Giantslayer computation into one method used by both opcode 32 and the drain; report the queue depth in the heartbeat beside the other queues)
- Modify: `server/FolkIdle.Server/Engine/WorldBossEngine.cs`:
  - add `QueueStrike(order, attack)` and `IsWeakPlateAsync(plate)` (a read with no lock; never touches the broadcast mirror);
  - `ExecuteAttackAsync` takes the order and computes s/M/P, the break and the elimination reveal inside the transaction, and completes the TCS on **every** path;
  - drop the session check;
  - move the weak seed to `RandomNumberGenerator.GetInt32(PlateCount)`;
  - a weak hit no longer sets `WeakPlateRevealed`.
- Modify: `server/FolkIdle.Server.Tests/WorldBossArmourTests.cs` and `WorldBossClientPathTests.cs` (the reveal rule changed; update the assertions and their comments rather than deleting them)
- Create: `server/FolkIdle.Server.Tests/WorldBossStrikeIntegrationTests.cs` (Testcontainers)

- [ ] **Step 1: Failing integration tests.** Force the weak plate through the snapshot, as `WorldBossArmourTests` does. Then:
  - a wheel strike reduces HP by exactly `ComputeAppliedDamage(hp, A x G x M x P)`, where `A` is the payload's attack;
  - `AttemptCount` increments;
  - `_playerDamageMap` and the Redis hash receive the same applied damage;
  - the break rule breaks exactly the expected plate, and a weak hit does **not** reveal;
  - breaking the fourth non-weak plate **does** reveal;
  - an auto-strike equals today's strike on the same plate;
  - a 4th attempt gives `NoAttemptsLeft` and does not issue;
  - an encounter rollover mid-challenge gives `NotActive` and **no attempt spent**;
  - the dispose path (a failing `IServiceProvider`) gives `Failed` and nothing spent;
  - **no session cap:** three attempts 10 minutes apart all land.
- [ ] **Step 2: Failing expiry tests.** An expired real challenge with 2 answered throws resolves at `M = 1.0` with `P` and the break from those throws, and spends the attempt. With 0 answered throws it gives `A x G x 1.0` and no break. A `Refused` submission resolves the same way and says `Refused`.
- [ ] **Step 3: The secret test.** Drive one whole attempt through the REST handlers and serialise every response. Assert the weak index appears in **none** of them except as `WeakHit: true` on a throw that hit it, or as `RevealedWeakPlate` after the elimination reveal. Do the same for the `StateUpdatePacket` built during the attempt.
- [ ] **Step 4: Result coverage.** A test walks `WorldBossStrikeResult` and asserts each value is produced by at least one test in this file or in Task 1.2's. A result nobody can produce is dead, and a path with no result is a lie.
- [ ] **Step 5: Implement.** The REST `/strike` awaits the TCS for up to 5 s and answers `Queued` on a timeout. **The drain budget** is the queue depth, read once per tick (the `GatheringGrantStarvationTests` shape). Add a test for it in the same style.
- [ ] **Step 6:** Run the filter `~WorldBoss|~ShieldWheel|~CommandGateOrdering`. Expected: PASS.
- [ ] **Step 7: Commit** `feat(world-boss): the shield wheel strike deals server-computed damage`.

### Task 2.2: auto-strike over REST, and opcode 32 under the flag

**Files:**
- Modify: `NetworkBroadcastSystem.cs` (`/strike` with `Mode: "Auto"`)
- Modify: `server/FolkIdle.Server/Network/StateUpdatePacket.cs` (`CommandResultCode.WorldBossUpdateRequired`, the **next free code**; 43 as of 2026-09-24, so check the enum first; use the `add-command` skill for the code and `npm run generate:protocol`)
- Modify: `WorldBossTickCoordinator.cs` (with `wheel`, opcode 32 answers that code and returns; it never terminates)
- Modify: `client_web/src/lib/stores/commandResults.ts` (the sentence: "This version of the app cannot strike the boss any more. Restart the app to update it.")

- [ ] **Step 1: Failing tests:**
  - auto-strike while a challenge is outstanding gives `ChallengeOutstanding` and changes nothing;
  - auto-strike on a weak plate returns `WeakHit: true` to that player and leaves `WeakPlateRevealed = 0`;
  - with `wheel`, opcode 32 through the real client path (task 25's `WorldBossClientPathTests` harness) gives code 43, no attempt row, and **the session alive**;
  - with `off`, opcode 32 lands exactly as before.
- [ ] **Step 2: Implement, run, commit** `feat(world-boss): auto-strike over REST; the legacy opcode says update`.

### Task 2.3: the screen plays for real

**Files:**
- Modify: `client_web/src/routes/WorldBoss.svelte` (Strike -> challenge -> wheel -> finish -> result card; Auto-strike = the plate buttons over `/strike {Mode:"Auto"}`; Practice stays; `ResolvedAtFloor` shown on open; with `Disabled`, today's buttons over opcode 32)
- Modify: `client_web/src/lib/net/rest.ts` (`strike`)
- Modify: `client_web/src/lib/net/commands.ts` (the `attackWorldBoss` header comment: only used when the server says `Disabled`)
- Modify: `client_web/src/lib/ui/wikiData.ts`, `client_web/tests/wiki.test.ts`

- [ ] **Step 1:** Extend `worldBossResults.test.ts`: every `/strike` and `/challenge` result has a sentence.
- [ ] **Step 2:** After a strike, invalidate nothing by hand: HP and pips arrive on the stream. The result card reads the REST answer only, following the "two sources for one truth" rule.
- [ ] **Step 3:** Run `npm test`, `check:ratchet`, the four geometry checks and `check:perf` with the overlay open. Load the page.
- [ ] **Step 4: Commit** `feat(world-boss): strike, auto-strike and results on the World Boss screen`.

### Task 2.4: drop the battle session from the wire (`add-command` skill)

**Files:** `server/FolkIdle.Server/Network/StateUpdatePacket.cs` (remove `WorldBossSessionEndsEpoch`), `NetworkPacketLayoutGuard.cs` (`ExpectedStateUpdateSize` 809 -> 801; verify it against the guard at startup, not by arithmetic alone), every writer and reader of the field (`grep -rn WorldBossSessionEndsEpoch server/`: `TickStatePayload.cs:~742`, `WorldBossTickCoordinator.cs:~33`, `SimulationEngine.cs:~2127`, and the login hydration in `StateCheckpointManager.cs:~680` and `~1020`, which reads `BattleSessionCapSeconds`), `WorldBossAttemptUpdateNotification.SessionEndsEpoch`, `StateUpdatePacketFieldCoverageTests` (if listed), `client_web/src/lib/net/protocol.generated.ts` (regenerated, never edited), `WorldBoss.svelte` (the session countdown), `commands.ts:~367-388` (the session pre-check), `.claude/skills/add-command/SKILL.md` (the stale 359/700 figures become 341/801)

- [ ] **Step 1:** Follow the `add-command` skill's removal steps. Keep `CommandResultCode.WorldBossSessionClosed = 41` with a `// Modul:` note, "reserved; the session was dropped by task 36; never reuse".
- [ ] **Step 2:** `npm run generate:protocol`, then `node client_web/scripts/generate-protocol.mjs --check`. Stop the server and run `dotnet build`, then the full `~WorldBoss|~StateUpdatePacketFieldCoverage|~NetworkPacketLayout` filter.
- [ ] **Step 3: Commit** `refactor(world-boss): the 300 s battle session is gone, from the engine to the wire`.

### Task 2.5: exercise proves skill reaches damage

**Files:** `client_web/scripts/exercise.mjs`

- [ ] **Step 1:** Open a fresh window through task 25's dev route (`POST /api/v1/dev/worldboss/window {open:true, durationSeconds:900}`), so three attempts are available on any calendar day. If it answers 404, **record a failure naming `FOLKIDLE_DEV_TOOLS`**. Do not skip.
- [ ] **Step 2: Blind strike.** Tap the throw zone 5 times evenly and choose no parry. Assert that the pip moved, `aria-valuenow` fell, and `M` on the card is in [1.0, 1.6].
- [ ] **Step 3: Aimed strike.** Read `data-schedule`. Parry correctly at `TellAtMs + 250`, and counter on plate 0. Tap seam crossings minus `FlightMs` (Playwright jitter of 10-30 ms is inside the tolerance). Assert `M_aimed > M_blind + 0.2` and that HP fell by more than in the blind run. **This check proves skill is wired to damage.**
- [ ] **Step 4: Auto-strike** spends the third attempt and shows `M = 1.00`. A 4th press shows the `NoAttemptsLeft` sentence, not a reconnect.
- [ ] **Step 5: Round-trip.** Close the window (`{open:false}`).
- [ ] **Step 6:** Re-seed if needed, run `npm run exercise` twice to green, and commit `test(exercise): the world boss wheel, blind, aimed and auto`.

### Task 2.6: review gates and the PR

- [ ] **Step 1:** Full verification in the `verify` skill's order: `dotnet build`, then the full `dotnet test` (Docker up, run alone), then `npm run check:ratchet`, `npm test`, `npm run build`, then `npm run exercise` twice and the geometry checks.
- [ ] **Step 2:** Open the PR "task 36 phase 2: the wheel deals damage (flag stays practice in prod)". Run **`security-review`** (the client-reported outcomes are an anti-cheat surface; focus on the scorer, the consistency rule, the secret test and the per-throw endpoint) and **`code-review`**. **Fix every security finding before the merge.**

---

## Phase 3 - docs, deploy, flip (small PR plus an ops change)

- [ ] **Step 1: Docs.**
  - `docs/world_boss_design.md`: a new "Task 36: the shield wheel" section; update the "no minigames" line.
  - `docs/TASK_BOARD.md` task 36: a result write-up with the measured playtest numbers.
  - `docs/architecture/CURRENT_IMPLEMENTATION_STATE.md`: the routes, `WorldBossStrikeQueue`, the flag, the removed field, telemetry `EventType 8`.
  - `NEXT_STEPS_BACKLOG.md`: the top section.
  - CLAUDE.md, **only** for a newly confirmed trap. The candidate is "under free aim every landing hits a plate, so class values priced for a chosen target pay out for random tapping".
- [ ] **Step 2: Deploy** with the `deploy` skill, keeping `FOLKIDLE_BOSS_MINIGAME=practice`. Run `smoke:screens` against production. Confirm the OTA bundle names the new version.
- [ ] **Step 3: Flip on Oct 14** (before the Oct 15 window). Set `FOLKIDLE_BOSS_MINIGAME=wheel` in the `ops/oracle` compose env and redeploy. Confirm `GET /api/v1/worldboss/challenge` answers something other than `Disabled` (it answers `NotActive` outside a window).
- [ ] **Step 4: Watch the first live window** (read-only Supabase MCP, SELECT only):
  - `SELECT count(*), sum("TotalInflictedDamage") FROM player_world_boss_attempts;`
  - `SELECT "CurrentHp","MaxHp","BrokenPlateMask","WeakPlateRevealed" FROM "WorldBossSnapshots";`
  - Redis `telemetry:hot_counts` field `<player>:8`.

  An empty attempts table means task 25's class of defect, not balance. Record the results in TASK_BOARD.

## Risks

- **The numbers are the design risk.** The simulation in spec §3.1 is a model. The phone playtest gate exists so the owner can retune before anything touches the shared pool.
- **The per-throw endpoint is a new REST surface:** up to 5 calls per attempt, idempotent by `Seq`. If a reverse proxy rate-limits `/api/v1/*`, check `ops/oracle/caddy/Caddyfile` and the server's own REST limiter, if any, before Phase 2 ships.
- **The in-memory registry across a deploy:** outstanding challenges are forgotten and no attempt is spent (spec §5.6). Deploy outside a live window when possible.
- **The reveal rule change** alters what task 10's tests pin. Update those tests with their comments. Never delete an assertion without replacing it.
- **Task 25 not merged, or merged differently:** stop, and re-derive Tasks 2.1-2.2 from what actually landed. Do not write a second eligibility function.

## What would change this plan

- **The owner answers spec §9 question 1 with "floor at auto-strike":** add `max(played, autoValue(counterPlate))` inside `ExecuteAttackAsync`, plus a ledger assertion.
- **The owner wants difficulty to rise with lost HP (spec §9 question 3):** `Generate` takes a `hpFraction` argument, and the property test gains a phase dimension.
- **Guild Wars (task 38) wants the minigame later:** `ShieldWheelScorer` and `WorldBossStrikeRules.Multiplier` are pure and reusable, and the war resolver takes an optional bounded `M`. Nothing here needs to change for that.
