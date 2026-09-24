# Task 38: Guild Wars, head-to-head - implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: use superpowers:subagent-driven-development or superpowers:executing-plans to run this plan phase by phase. Steps use checkbox (`- [ ]`) syntax. The project skills used below are `add-command` (Phase 1: removing opcodes and wire fields), `run-stack`, `verify`, `deploy`, `code-review`, `security-review` (Phase 5, diamonds), and the `wiring-auditor` agent (a final "done" check).

**Goal:** replace five half-built war systems with one Clash of Clans-style async guild war. It is locked until the population can hold it, pays diamonds only through a capped weekly board, and sinks guild gold into bounded Great Works.

**Spec:** `docs/superpowers/specs/2026-09-24-guild-wars-design.md`. **Audit:** `docs/superpowers/plans/2026-09-23-task-38-guild-wars-audit-and-design.md`.

**Order and urgency:**

- **Phase 1 is urgent** and ships now. It removes the settlement exploit (spec §2), which becomes live the moment `GuildWarUnlock` opens. It also removes the dead code and the four svelte-check errors.
- **Phases 2-6** are complete and ready, but **not urgent**. Start them when the unlock progress (`GET /api/v1/guild/war-unlock`) approaches the floor, or when the owner asks.
- **Task 37 Phase 0 must be merged before Phase 6** (the Great Works gold is recorded where the audit used to move combat gold).

```
opt-in ─> prep (pairing pass: roster lock + snapshots + pairing, idempotent per cycle)
       ─> battle 2 days (POST /api/v1/guildwar/attack: AttackResolver over CombatDamageModel, FOR UPDATE on the roster row)
       ─> BattleEndsAtUtc ─> settlement pass (time-based ONLY; SettledAtUtc under FOR UPDATE; rating, points, mail)
Sunday ─> Works weekly settlement ─> weekly board payout (GuildMMR, floors, cap 20/member, week key, BillingSyncQueue)
GuildWarCycleEngine.StartCron: one 60 s loop; each pass isolated, try before CreateScope, budgeted, depth in heartbeat
```

## Global Constraints

- **CLAUDE.md's load-bearing rules**, the ones this task touches:
  - *Never hand-write a wire type* and *the add-command skill* for every opcode or field removal. Retired opcode numbers stay reserved.
  - *A field on `StateUpdatePacket` must be loaded at login or declared runtime-only*: the new war adds **no** wire fields (REST only), and removes the seven runtime-only entries.
  - *Silent rollback*: every refusal is a `GuildWarResult` with a sentence.
  - *A background worker that can throw is a feature that can vanish*, and *a guard that starts after `CreateScope` is not a guard*, and *an unbounded drain is a starvation bug*: one cron, isolated passes, budgets, `CronWorkerGuardTests` updated.
  - *Two gold paths*: Works donations are DB debits plus `ReloadState`, never `RedisPendingGoldDelta`.
  - *A leaderboard that pays diamonds needs a population floor*: two gates, plus a cap asserted against `DelveRegistry.MaxDiamondsPerWeek`.
  - *Every multiplier declares a cap*: `GuildWarLedgerTests`.
  - *A number a test prints is not a number a test checks.*
  - *Raw SQL must match the table name*: new tables are snake_case with `[Table]`; old ones are PascalCase and quoted.
  - *Grep for a WRITER as well as a reader.*
  - *A check that spends fixture state passes once and fails forever.*
  - *The fixture cannot verify the new-player experience.*
  - The Svelte traps.
- **Diamonds** only through the weekly board, **only** via a DB write plus `BillingSyncQueue` (the `AchievementEngine` pattern). Never a bare `PremiumDiamonds +=` behind a live session.
- **Never trust a client number.** The client sends a war id, a slot, a Work name and an amount of **its own** gold to donate. It never sends damage, stars or prices.
- **Non-additive migrations** (Phase 1's table drops): confirm the production row counts first with a read-only SELECT. Migrations run on the container ENTRYPOINT, so apply them locally with `--migrate`.
- **Stop the server before `dotnet build`/`dotnet test`,** each as its own call. Docker must be up. Run one suite at a time.
- **Each task is one commit; each phase is one PR** that deploys on its own. Write commit messages with the Write tool and `git commit -F`. End them with the `Co-Authored-By` / `Claude-Session` lines.

---

## Phase 1 - URGENT: close the settlement exploit, delete the dead war code (one PR, ships now)

### Task 1.1: the format gate - the old engine can never run, whatever the population

**Files:**
- Create: `server/FolkIdle.Server/Engine/GuildWarsFormat.cs` (`public enum GuildWarsFormatKind { LegacyThreeFront, HeadToHeadV1 }`; `public const GuildWarsFormatKind Current = GuildWarsFormatKind.LegacyThreeFront` until Phase 3 lands, with a `// Modul:` explaining spec §2)
- Modify: `server/FolkIdle.Server/Engine/GuildWarUnlock.cs` (`IsUnlocked`/`Current.Unlocked` are also false unless `GuildWarsFormat.Current == HeadToHeadV1`. **The population progress and the `feature_unlocks` row are unchanged**, so the screen still shows M/50, and the recorded crossing still stands)
- Modify: `server/FolkIdle.Server.Tests/GuildWarUnlockTests.cs`

- [ ] **Step 1: The failing test** `TheLegacyFormatNeverUnlocksWhateverThePopulation`: seed 60 qualifying players in 5 guilds of 4, run the refresh, and assert `IsUnlocked == false`, `QualifyingPlayers == 60`, and that `RunMatchmakingPassAsync` pairs nothing and pays nothing. Stop the server, then run `dotnet test ... --filter "FullyQualifiedName~GuildWarUnlock"`. Expected: FAIL.
- [ ] **Step 2: Implement.** Update the existing test that asserts "pairing does run once crossed": it now needs `HeadToHeadV1`, and that condition cannot be reached until Phase 3. Keep the test, but point it at the new engine in Phase 3. Update its comment to say so.
- [ ] **Step 3: Commit** `fix(guild-war): the legacy war can never unlock, whatever the population`.

### Task 1.2: delete the three-front, cross-shard, turn-based and defence-roster systems (server)

**Files (delete):** `Engine/GuildWarEngine.cs`, `Engine/GuildMatchmakingEngine.cs`, `GlobalTournamentMeshService` (find it with `grep -rln "class GlobalTournamentMeshService" server/`), `Engine/GuildCombatSimulationEngine.cs`, and the `Models/` entities for `GuildWarMatch`, `GuildMatchmakingSnapshot`, `GuildWarActiveMatch`, `GuildWarCombatHistory`, `GuildDefenseRoster`, `GuildWarDefensiveSnapshot`.
**Files (modify):**
- `Program.cs:~453-554`: stop constructing and starting them. **Keep an unlock refresh**: add a minimal `GuildWarUnlockRefresher` loop (60 s, guarded, try before `CreateScope`) that Phase 3 folds into `GuildWarCycleEngine`, and keep the startup warm-up from `696c559`.
- `Engine/GuildWarSnapshotEngine.cs`: move `BuildMemberCombatStatsAsync` into a new `Domain/Social/GuildWarDefenceSnapshotBuilder.cs` (`internal static`) and delete the aggregate loop.
- `Domain/Social/GuildWarTickCoordinator.cs`: opcodes 23/27/49/50 answer `GuildWarsLocked` (37) and do nothing more, until Task 1.3 retires them.
- `SimulationEngine.cs`: the war point sources `~4673-4683`, the opcode 49 handler `~2559-2613`, and the dispatch entries `~771-773`.
- `CraftingTickCoordinator.cs:~58-73`.
- `NetworkBroadcastSystem.cs`: `/api/v1/guild/shard-match`, `~1077`, handler `~3110-3150`.
- `Models/FolkIdleDbContext.cs`: the DbSets.
- `CronWorkerGuardTests.cs`: remove `GuildWarEngine`, `GuildMatchmakingEngine`, `GuildWarSnapshotEngine`; add `GuildWarUnlockRefresher`.
- `HardenedEngineIntegrationTests.cs`: remove the tests of deleted engines (`Test_GuildCombat_*`, `Test_GuildWarScoreboardQueue_*`, `Test_GuildWarSnapshot_*`). **Keep** a snapshot-builder test pointed at the new builder.
- `E2ETestHarness.cs`, `CommandGateOrderingTests.cs`: references.

**Migration (non-additive):** `DropLegacyGuildWarTables` drops `"GuildWarMatches"`, `"GuildMatchmakingSnapshots"`, `"GuildWarActiveMatches"`, `"GuildWarCombatHistory"`, `"GuildDefenseRosters"`, `"GuildWarDefensiveSnapshots"`.

- [ ] **Step 1: Evidence first.** Run a read-only Supabase SELECT of `count(*)` on each of the six tables in production, and paste the numbers into the PR body. The audit found 0,0,0,0,1,1. **If any count is non-zero beyond those two write-only rows, stop and ask the owner.**
- [ ] **Step 2: Grep for writers and readers** of every symbol you delete (`grep -rn "GuildWarMatches\|GuildMatchmakingSnapshots\|GuildWarActiveMatches\|GuildWarCombatHistory\|GuildDefenseRosters\|GuildWarDefensiveSnapshots\|GlobalTournamentMesh\|GuildWarPointEvent\|GuildWarScoreboardQueue" server/`) and remove every hit, or justify it in the PR body.
- [ ] **Step 3: Delete, build, and fix what breaks.** Stop the server, then run `dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj`.
- [ ] **Step 4: A new test**, `GuildWarDeletionTests.NoLegacyWarCodeRemains`: a source grep asserts that none of the deleted class names exist, and that `Program.cs` starts no war cron but `GuildWarUnlockRefresher`.
- [ ] **Step 5:** Generate the migration, then **read its SQL** (drops only, with the quoted PascalCase names). Apply it locally with `--migrate`.
- [ ] **Step 6:** Run the filter `~Guild|~CronWorkerGuard|~CommandGate`. Expected: PASS.
- [ ] **Step 7: Commit** `refactor(guild-war): delete the three-front, cross-shard, turn-based and roster wars`.

### Task 1.3: retire the opcodes and wire fields (`add-command` skill)

**Files:** `Network/ClientCommandPacket.cs` (opcodes 23, 27, 49, 50 become comments of the form `// 23 reserved: ContributeToWarSupply, retired by task 38`, with **no** enum members; the validator entries go), `Network/StateUpdatePacket.cs` (remove the fields in spec §3: `ActiveGuildWarId`, `CombatSimulationMatchId/TurnCounter/DamageDelta`, `GlobalNodeRemainingHp`, `CachedWarMultiplier`, the six scoreboard point fields, and any `VisualActiveMatchMmr`), `NetworkPacketLayoutGuard.cs` (the new `ExpectedStateUpdateSize`, measured by the guard at startup), `StateCheckpointManager.cs` (the hydration of `ActiveGuildWarId`, `~590`, and of the node HP, `~546-556`), `StateUpdatePacketFieldCoverageTests.cs` (remove the seven "Guild Wars, on the roadmap" entries), `client_web/src/lib/net/protocol.generated.ts` (regenerated)

- [ ] **Step 1:** Follow the `add-command` skill's removal steps.
  - `ClientPredictedDamage`: remove it **only** if no reader remains (the world boss validator checks it is 0) **and** the new `ExpectedClientCommandSize` collides with none of the demux sizes the skill lists. Otherwise keep it and write why in a `// Modul:` comment.
  - Check that task 36's removal of `WorldBossSessionEndsEpoch` (if merged first) and this change agree on the final size. Rebase and re-measure; never add up the sizes by hand.
- [ ] **Step 2:** Run `npm run generate:protocol` and `node client_web/scripts/generate-protocol.mjs --check`. Stop the server and run `dotnet build`. Run the filter `~StateUpdatePacketFieldCoverage|~NetworkPacketLayout|~CommandGate`.
- [ ] **Step 3: A test**: a JSON command with `Command: 50` from a stale client is refused as an unknown command, visibly and **without a disconnect**, if the codec path allows it. Otherwise it is dropped and logged, and the test pins whichever behaviour the skill documents.
- [ ] **Step 4: Commit** `refactor(protocol): retire the war opcodes and fields; numbers stay reserved`.

### Task 1.4: the client, as ONE block, and the ratchet goes down

**Files:** `client_web/src/routes/GuildOps.svelte` (delete `~251-306`: `quarantined`, `defend`, `shardMatch`/`matchUuid`/`EMPTY_UUID`, `attackShard`, `matchId`/`turnCounter`/`damageDelta`, `takeTurn`; the imports `registerGuildDefense`, `executeCombatTurn`, `submitShardAttack`, `fetchGuildShardMatch`, and `typicalHit` if nothing else reads it; the visible three-front "Guild war" panel `~428-470`, **keeping** PR #22's locked-progress line), `client_web/src/lib/net/commands.ts` (`~842-920`: the four senders), `client_web/src/lib/net/rest.ts` (`~643-667`: `fetchGuildShardMatch` and its DTO), `client_web/scripts/typecheck-ratchet.mjs` (`BASELINE`), `CLAUDE.md` (the Conventions bullet "Four pre-existing svelte-check errors..."), any client test that referenced the senders (`commandsAudit.test.ts`: the audit list)

- [ ] **Step 1:** Run `npm run check` and record the raw count before (expected 4).
- [ ] **Step 2:** Delete the whole block in one edit. **Do not delete the four symbols alone.** Audit A.2 shows that makes the count go **up**, because each is the only reader of something else.
- [ ] **Step 3:** Run `npm run check` raw again and **read the number** (expected 0). Set `BASELINE` to exactly that number. If it is 0, update the ratchet script's comment, and replace CLAUDE.md's "Four pre-existing `svelte-check` errors..." bullet with one line: the baseline is 0 since task 38 Phase 1 deleted the orphaned war handlers, and a new error is a regression.
- [ ] **Step 4:** Run `npm test` and `npm run check:ratchet`, then **load the Guild screen** (the `run-stack` skill). It must render the locked line and nothing else war-related.
- [ ] **Step 5:** `exercise.mjs`: the existing Guild-lock check from PR #22 still passes. Run `npm run exercise`.
- [ ] **Step 6: Commit** `refactor(guild-ops): delete the orphaned war handlers as one block; svelte-check baseline 4 -> 0`.

### Task 1.5: fix the lost-grant shape in `LeaderboardPayoutEngine` (spec §8)

**Files:** `server/FolkIdle.Server/Engine/LeaderboardPayoutEngine.cs` (take `PlayerSessionRegistry`; after each commit, `BillingSyncQueue.Enqueue(new BillingSyncNotification { PlayerId, ... authoritative balance })`, as `AchievementEngine.cs:~195-215` does), `Program.cs` (the constructor), a new test in `LeaderboardRewardTests.cs` or `LeaderboardPayoutTests.cs`

- [ ] **Step 1: The failing test:** an **online** player (in the session registry, with a payload whose `PremiumCurrency` is stale) is paid for rank 1 with the floor met. Run one checkpoint flush. Assert that the row's `PremiumDiamonds` still includes the payout.
- [ ] **Step 2: Implement, run, commit** `fix(leaderboard): an online player's weekly diamonds survive the next checkpoint`.

### Task 1.6: verify, PR, deploy

- [ ] **Step 1:** Full verification in the `verify` skill's order: `dotnet build`, then the full `dotnet test` (run alone), `npm run check:ratchet` (now at the new baseline), `npm test`, `npm run build`, `npm run exercise`, and the geometry checks for the Guild screen.
- [ ] **Step 2:** Run the `wiring-auditor` agent once on "Guild war: every path from a button to a table". It must report **nothing reachable** except the locked line and the unlock endpoint.
- [ ] **Step 3:** PR "task 38 phase 1: the settlement exploit cannot open; the dead war code is gone". Run `code-review`. Merge and deploy (the `deploy` skill; the non-additive migration runs on ENTRYPOINT, so confirm afterwards with a read-only SELECT that the six tables are gone). Run `smoke:screens` against production.
- [ ] **Step 4: Docs:**
  - `CURRENT_IMPLEMENTATION_STATE.md`: the war section, the dead code list, the dropped tables;
  - `NEXT_STEPS_BACKLOG.md`: the top section;
  - `docs/TASK_BOARD.md` task 38: Phase 1 done, with links to the spec and this plan.

---

## Phase 2 - the smallest playable war, behind a flag: one forced war, attacks, stars (server + dev route)

**Flag:** `FOLKIDLE_GUILD_WARS` = `off` (default) | `dev`. With `dev`, and only where `DevToolsEnabled()` is true (never Production), `POST /api/v1/dev/guildwar/force { "GuildA": 1, "GuildB": 2, "BattleMinutes": 30 }` creates a war immediately (snapshots and all) with a short battle phase, and `{ "clear": true }` deletes it. The cycle, pairing and board wait for Phases 3-5.

### Task 2.1: the pure resolver and its ledger

**Files:**
- Create: `server/FolkIdle.Server/Domain/Social/GuildWarRules.cs` (`AttacksPerMember = 2`, `FightSeconds = 60`, the star thresholds 0.40/0.70/1.00, `WarSkillCap = 1.25`, the war-size buckets, `MinWarSize = 3`, `K = 32`, `WarParticipationPoints`)
- Create: `server/FolkIdle.Server/Domain/Social/AttackResolver.cs` (`Resolve(attackerStats, defenderStats, perks, skillMultiplier = 1.0) -> (destructionMilli, stars)`; the defender becomes a synthetic `MonsterDefinition`; uses `CombatDamageModel.ExpectedSecondsPerKill` both ways; `skillMultiplier` clamped to `[1, WarSkillCap]`)
- Create: `server/FolkIdle.Server/Domain/Social/GuildWarPower.cs` (`Power = sqrt(EHP x DPS)` against a reference monster)
- Create: `server/FolkIdle.Server.Tests/AttackResolverTests.cs`, `server/FolkIdle.Server.Tests/GuildWarLedgerTests.cs`

- [ ] **Step 1: Failing tests:**
  - an identical attacker and defender land on a stated destruction band;
  - a geared level-94 reference (from `ProgressionRateTests`' builder) 3-stars a level-10 alt;
  - a level-10 alt 0-stars the geared reference ("an alt is a weak attacker and a weak defender");
  - destruction is monotonic in attacker damage and in defender HP;
  - `skillMultiplier` 5.0 is clamped to 1.25;
  - the result is deterministic: the same inputs give the same output;
  - `GuildWarLedgerTests` asserts each perk cap (Palisade and Siege at 1.15 or below, attacks at 3 or below, `WarSkillCap` at 1.25 or below) and their product at or below `WarLeverCeiling`, and **fails if any file outside `Domain/Social/GuildWar*`/`AttackResolver.cs` reads a Works perk** (a grep).
- [ ] **Step 2: Implement, run, commit** `feat(guild-war): a deterministic attack resolver over the one damage model`.

### Task 2.2: tables, the forced war, the attack endpoint

**Files:**
- Create: the models and `[Table]` snake_case tables `guild_war_cycles`, `guild_war_optins`, `guild_wars`, `guild_war_roster`, `guild_war_attacks` (spec §6); `GuildMembers.JoinedAtUtc` (nullable); migration `AddGuildWarsV1` (additive). Set `JoinedAtUtc` wherever a member is created (`GuildManagementEngine`), and **grep for every `new GuildMember`** so no writer is missed.
- Create: `server/FolkIdle.Server/Domain/Social/GuildWarEngine.cs`. **Reusing the old name is deliberate after Phase 1's deletion.** If that confuses review, name it `GuildWarService`. It provides `ForceWarAsync` (dev), `SnapshotRosterAsync` (casting `StatsJson` explicitly to jsonb: the 42804 trap), `AttackAsync(playerId, warId, slot)` (Serializable; `FOR UPDATE` on the attacker's roster row and the target row; refuses per spec §4.3; writes the attack and updates the best stars and destruction), and `GetViewAsync(playerId)`.
- Create: `server/FolkIdle.Server/Domain/Social/GuildWarResult.cs`
- Modify: `NetworkBroadcastSystem.cs` (`GET /api/v1/guildwar`, `POST /api/v1/guildwar/attack`, the dev route behind `DevToolsEnabled()` **and** `FOLKIDLE_GUILD_WARS=dev`; every refusal is 200 plus a `Result`, following the Delve precedent)
- Modify: `DevFixtureSeeder.cs` (a second fixture guild with 3 qualifying members, so a dev war has two sides) and `DevFixtureInvariantTests.cs` (assert it)
- Create: `server/FolkIdle.Server.Tests/GuildWarAttackTests.cs` (Testcontainers)

- [ ] **Step 1: Failing tests:**
  - a rostered member's attack writes an attack row, spends one attack, and updates the target's best stars only when it improves them;
  - a third attack gives `NoAttacksLeft`;
  - two concurrent attacks from one member (a double-tap) spend **at most** 2 in total;
  - attacking outside the battle phase gives `NotBattlePhase`;
  - a non-rostered member gets `NotRostered`;
  - a slot outside the enemy roster gives `InvalidTarget`;
  - with the flag off, the answer is `Locked`;
  - the dev route answers 404 under `DOTNET_ENVIRONMENT=Production` even with both flags set;
  - the snapshot is frozen: re-gear the attacker mid-war and the next attack's result is unchanged.
- [ ] **Step 2: Implement.** Apply the migration locally with `--migrate`.
- [ ] **Step 3:** Run the filter `~GuildWar|~AttackResolver|~DevFixture`. **Commit** `feat(guild-war): a forced dev war, rosters, snapshots and attacks`.

**PHASE GATE:** the owner plays a forced dev war on the dev box through a bare REST client or the Phase 4 UI, if it is built first. Tune the thresholds and `FightSeconds` **in the spec first**.

---

## Phase 3 - the cycle: opt-in, pairing, time-based settlement, rating (one PR)

### Task 3.1: the schedule and the cycle engine

**Files:**
- Create: `Domain/Social/GuildWarSchedule.cs` (pure: `PhaseAt(utc)`, `CycleIdFor(utc)`, spec §4.1)
- Create: `Engine/GuildWarCycleEngine.cs` (`StartCron`, a 60 s loop; passes in order: unlock refresh (fold in `GuildWarUnlockRefresher` and delete it), pairing at prep start, settlement of due wars, Works settlement, board payout. **Each pass is in its own try that opens BEFORE `CreateScope`; each pass is budgeted; the heartbeat reports due-and-unsettled wars.**)
- Modify: `GuildWarsFormat.Current = HeadToHeadV1` (**the last commit of this phase, after every test below is green**), `CronWorkerGuardTests` (add `GuildWarCycleEngine`, remove the refresher), `Program.cs`
- Modify: `NetworkBroadcastSystem.cs` (`POST /api/v1/guildwar/optin`, and remove an opt-in by a leader)
- Create: `server/FolkIdle.Server.Tests/GuildWarScheduleTests.cs`, `GuildWarPairingTests.cs`, **`GuildWarSettlementTests.cs`**

- [ ] **Step 1: Failing schedule tests:** every UTC minute of a sample year maps to exactly one phase, the phases tile the week, and the boundaries match spec §4.1.
- [ ] **Step 2: Failing pairing tests:**
  - opt-in eligibility (level, quarantine, tenure through `JoinedAtUtc`);
  - bucket selection with first-come benching;
  - pairing by adjacent Power;
  - a bucket drop for an odd guild out;
  - no rematch within 2 wars when an alternative exists;
  - "no opponent this cycle" is recorded and visible;
  - the pass is **idempotent per cycle** (run it twice: one set of wars);
  - a guild below `MinWarSize` does not enter.
- [ ] **Step 3: Failing settlement tests. THE EXPLOIT (spec §2):**
  - `ANewGuildCannotEndARunningWar`: a war in battle; create two new unmatched guilds, then run **every** pass of the engine repeatedly at `now < BattleEndsAtUtc`. Assert `SettledAtUtc` is null, `GuildMMR` is unchanged for both, and no contribution points or mail were sent. Advance the injected clock past `BattleEndsAtUtc` and run the passes **twice**: assert it settled exactly once, with one rating change and one set of points and mail.
  - `ADisbandedGuildDoesNotSettleItsWarEarly`: delete guild B mid-war. Nothing settles before the end. At the end, A wins, **no rating moves** (anti-throw), and it settles once.
  - `SettlementIsTimeBasedOnly`: a source-level test that the settlement query's only condition on time is `BattleEndsAtUtc <= now` and `SettledAtUtc IS NULL`. Read the method under test, or assert through behaviour with a spy DbContext; prefer behaviour.
  - Result rules: stars, then destruction, then the earlier last star, then a draw. Elo with `K = 32`. A zero-attack loser moves no rating.
  - A restart: a war due while the server was down settles on the first pass after start.
- [ ] **Step 4: Implement** with an injectable clock (`Func<DateTime>`) on the engine, so the tests never depend on today's date.
- [ ] **Step 5:** Run the filter `~GuildWar|~CronWorkerGuard`. Then flip `GuildWarsFormat.Current = HeadToHeadV1`, and re-point Phase 1's `GuildWarUnlockTests` "pairing runs once crossed" test at the new engine. Run the filter again.
- [ ] **Step 6: Commit** in parts: `feat(guild-war): the weekly cycle, opt-in and pairing`, then `feat(guild-war): settlement is time-based and once per war`, then `feat(guild-war): the head-to-head format is the only one that can unlock`.

---

## Phase 4 - the UI (one PR)

**Files:** `client_web/src/routes/GuildOps.svelte` (the War panel by phase, spec §7), `client_web/src/lib/net/rest.ts` (DTOs plus `fetchGuildWar`, `optIn`, `attack`; TanStack query with a 30 s refetch while visible, and invalidate after an attack), `client_web/src/lib/stores/...` (the `GuildWarResult` sentences), `client_web/tests/guildWarResults.test.ts` (every server result has a sentence, compared to a server-exported fixture list), `client_web/scripts/screens.mjs` (a "war in battle" state), `client_web/scripts/exercise.mjs`, `wikiData.ts` + `tests/wiki.test.ts`

- [ ] **Step 1:** Build the panel:
  - a plain **Attack** button per enemy row, 44 px or more with `flex-shrink: 0`;
  - "I'm in" as a button;
  - countdowns outside controls;
  - `{#if}` for role- and phase-dependent controls;
  - no `<select>`;
  - a windowed list if a roster can exceed about 20 rows. It cannot today (bucket maximum 20), so no windowing is needed. Write that in a `// Modul:` comment.
- [ ] **Step 2: The exercise.**
  1. With the stack up and `FOLKIDLE_GUILD_WARS=dev`, call `POST /api/v1/dev/guildwar/force`. A 404 records a failure naming the flags.
  2. As the fixture, open the Guild screen, press Attack on slot 0, and assert that the attacks left went from 2 to 1 and that slot 0's best stars and destruction are shown and match `GET /api/v1/guildwar`.
  3. Attack again; the third press shows the `NoAttacksLeft` sentence.
  4. Clear the war (`{clear:true}`), for the round-trip.
  5. In the fresh-account context, the Guild screen shows the locked or "join a guild" line and nothing broken.
- [ ] **Step 3:** Run `npm test`, `check:ratchet` (the new baseline), `check:touch/overlap/clipping/safearea` with the war state, `npm run exercise` twice, and **load the page**.
- [ ] **Step 4: Commit** `feat(guild-ops): the head-to-head war panel`.

---

## Phase 5 - the weekly board, the only diamond tap (one PR; `security-review`)

**Files:** `server/FolkIdle.Server/Engine/GuildWarBoardRegistry.cs` (tiers, `MinimumRankedGuilds = 4`, `WeeklyDiamondsFor(rank, rankedGuilds)`), `GuildWarCycleEngine.cs` (the board payout pass: polled, per player one transaction with the week key and diamonds, then `BillingSyncQueue`), migration `AddGuildWarPayoutWeekKey` (`PlayerRecords.GuildWarPayoutWeekKey`, `GuildWarPayoutRank`), `NetworkBroadcastSystem.cs` (`GET /api/v1/leaderboard/guildwar`), `Leaderboards.svelte` (a tab), `server/FolkIdle.Server.Tests/GuildWarBoardTests.cs`, `LeaderboardRewardTests.cs`

- [ ] **Step 1: Failing tests:**
  - first place per member is at most `DelveRegistry.MaxDiamondsPerWeek / 3` (asserted against the constant);
  - no rank pays more than first place;
  - a tier pays nothing unless the ranked guild count reaches **both** `MinimumRankedGuilds` and the tier's `MaxRank`;
  - only rostered members with at least one attack that week are paid;
  - a second pass in the same ISO week pays nothing (the week key);
  - an **online** member's diamonds survive the next checkpoint (the Task 1.5 test shape);
  - no per-war code path writes `PremiumDiamonds` (a grep over `GuildWar*`: only the board pass may);
  - a guild that fought no war that week is not ranked.
- [ ] **Step 2: Implement, run, commit** `feat(guild-war): a capped weekly board, the only diamond tap`.
- [ ] **Step 3:** PR. Run **`security-review`** (a diamond tap: payout idempotence, alt-guild farming of Elo, the double payout across boards) and `code-review`.

---

## Phase 6 - Guild Great Works (one PR; after task 37 Phase 0)

**Files:** migration `AddGuildGreatWorks` (the `guild_great_works` table), `server/FolkIdle.Server/Domain/Social/GuildWarGreatWorks.cs` (Works, levels 0-3, perks, `Goal(lastWeek, qualifyingMembers)`, `WorkFloorGold = 250_000` per qualifying member, weekly settlement), `GuildWarCycleEngine.cs` (the Works settlement pass, idempotent through `SettledWeekKey`), `AttackResolver.cs` / `GuildWarRules.cs` (read the perks; the War Banner raises `AttacksPerMember` to 3 at level 3), `GuildContributionEngine.cs` (`ContributeGoldAsync` redirects into a Work: a DB debit on the locked gold row, **no guild tier from gold**, `ReloadState` through the registry), `NetworkBroadcastSystem.cs` (`GET /api/v1/guild/works`, `POST /api/v1/guild/works/donate`, `POST /api/v1/guild/works/fund`, the officer check), `GuildOps.svelte` (the Works panel), `client_web/src/lib/net/rest.ts`, `server/FolkIdle.Server.Tests/GreatWorksTests.cs`, `GoldSinkAffordabilityTests.cs` (add the Works row at the top-income profile)

- [ ] **Step 1: Failing tests:**
  - a donation debits exactly the amount from `CommodityRecords` gold and adds it to `ContributedThisWeek`;
  - not enough gold gives `NotEnoughGold`, and nothing changes;
  - no Works code references `RedisPendingGoldDelta` (a grep);
  - a treasury fund by a plain member gives `NotAnOfficer`;
  - a fund debits `GuildTreasuryGold` under `FOR UPDATE`;
  - weekly settlement raises the level when the goal is met (capped at 3), drops it by 1 when missed (floor 0), and sets next week's goal to `max(floor, 1.1 x contributed)`, and is **idempotent** (run twice);
  - the perks change the resolver's output within their caps;
  - `GuildWarLedgerTests` stays green;
  - **no file outside the war system reads the Works** (the grep from Task 2.1);
  - **`CurrentTier` no longer changes from a gold donation**, and no war number reads `CurrentTier`;
  - `GoldSinkAffordabilityTests` prints the Works goal in minutes of top income for a 3-member and a 20-member guild, and asserts the band the spec's owner decides (default: one week's goal is 1-10 hours of one member's top income per member).
- [ ] **Step 2: Implement.** Apply the migration with `--migrate`. **Commit** `feat(guild): Great Works, a bounded treasury sink for the war`.
- [ ] **Step 3:** Extend the exercise: donate 10k to Palisade, assert the gold fell by exactly 10k and the Work's progress rose by 10k. The round-trip is not possible for destroyed gold, and the fixture's gold is ample. Assert the fixture holds at least 10x the donation in `DevFixtureInvariantTests`, so the check never runs dry.

---

## Phase 7 - docs, deploy, and the unlock watch

- [ ] **Step 1: Docs:**
  - `CURRENT_IMPLEMENTATION_STATE.md`: the war section; the new snake_case tables in §3; the routes; the cron inventory;
  - `NEXT_STEPS_BACKLOG.md`;
  - `docs/TASK_BOARD.md` task 38 marked DONE, with what exercise now proves;
  - the in-game Wiki;
  - CLAUDE.md, only for new confirmed traps. The candidate is "a pairing pass must never settle: settlement is time-based and once per war".
- [ ] **Step 2: Deploy** each phase with the `deploy` skill as it merges. `FOLKIDLE_GUILD_WARS` stays `off` in production. The **population lock** alone opens the real war, because `dev` only enables the forced-war dev route, which is off in Production anyway.
- [ ] **Step 3: Before the floor is crossed**, run the `wiring-auditor` agent on "Guild war: opt-in -> pairing -> attack -> settlement -> board -> diamonds on the header", and a read-only check that `feature_unlocks` has no `guild_wars` row yet. When it crosses, watch the first cycle with SELECT-only queries on `guild_wars`, `guild_war_attacks` and the payout week keys.

## Risks

- **The unlock crosses before Phase 3:** impossible after Task 1.1, because the format gate keeps the war locked. That is the point of doing it first.
- **Balance of `FightSeconds` and the star thresholds:** Phase 2's gate is where the owner tunes them.
- **Elo farming with alt guilds:** mitigated by power pairing, the level-10 roster bar, tenure, the rematch rule and the zero-attack anti-throw. Phase 5's `security-review` is asked to try.
- **The size of the wire removal** collides with task 36's removal of `WorldBossSessionEndsEpoch`. Whichever merges second rebases and re-measures `ExpectedStateUpdateSize`.
