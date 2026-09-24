# Guild Wars: head-to-head async wars, Great Works, and the deletion of the old half-systems (task 38), design

Date: 2026-09-24
Status: SPEC. The owner decided this on 2026-09-24. The build is **not urgent**: it waits for the population lock to open. **One part is urgent regardless, §2.** Plan: `docs/superpowers/plans/2026-09-24-task-38-guild-wars.md`.
Audit and option menu: `docs/superpowers/plans/2026-09-23-task-38-guild-wars-audit-and-design.md`.

**Builds on PR #22 (merged, `main` at `696c559`).** That PR made these the one gate for every war path:

- `GuildWarUnlock`: 50 players at `MinimumRankedLevel` AND 4 guilds with 3 such members each;
- `QualifyingPopulation`;
- the `feature_unlocks` table (one-way);
- `CommandResultCode.GuildWarsLocked = 37`;
- `GET /api/v1/guild/war-unlock`.

This spec keeps all of that, and does not redo it.

---

## 1. Decisions (owner, final, 2026-09-24)

| # | Question | Answer |
|---|---|---|
| 1 | Format | **Head-to-head async, Clash of Clans style.** A prep day (roster lock plus defence snapshots via `GuildWarSnapshotEngine`'s stat builder), then 2 battle days. Each rostered member gets **2 attacks** on enemy defence snapshots. Stars come from damage thresholds. Matchmaking is by snapshot power |
| 2 | When | Only after the population lock unlocks (PR #22). No ghost opponents |
| 3 | Diamonds | **Only from a capped, ranked weekly board.** It respects `DelveRegistry.MaxDiamondsPerWeek` and the leaderboard cap tests. **No per-war diamond payout** |
| 4 | Gold sink | **Guild Great Works**, the treasury sink. The goal scales with the guild's own contributions. Perks are bounded **inside the war system**. There is no feed into the PvE power ledger. The uncapped, linear gold donation -> defender HP path is fixed and replaced |
| 5 | Old code | **DELETE** the cross-shard war, the turn-based duel, and the defence roster, plus the four orphaned `GuildOps.svelte` handlers **as one block**. The `check:ratchet` baseline goes down to match |
| 6 | Interaction | A **plain Attack button**. Task 36's minigame may be reused later, as a bounded multiplier |
| 7 | Urgency | Not urgent, but the plan must be complete |
| 8 | Third diamond tap | Accepted: a **separate** war board, at most 20 per member per week, so 140 a week across all taps (60 + 60 + 20), pinned by a test (follow-up answer) |
| 9 | Guild Raid | **Deleted** with the other old war code (follow-up answer) |
| 10 | Cadence | **One war a week**: prep Monday, battle Tuesday and Wednesday, results Thursday (follow-up answer) |

## 2. The settlement exploit, and the phase that must ship BEFORE the lock can open

`GuildWarEngine.RunMatchmakingPassAsync` (`Engine/GuildWarEngine.cs:~418-560` on `main`):

- counts guilds with no active match;
- when **two or more** are unmatched (`hasUnmatchedGuilds`), it runs the whole pass;
- the pass **resolves every active match and pays each winner** (`ResolveCombatPhaseAsync` + `DistributeVictoryTokensAsync`, 100 diamonds a member) **regardless of the match's age**.

After the unlock, a leader who creates one extra guild ends every running war on the server within a minute and triggers the payouts. Then they disband and repeat. PR #22 only gates this pass behind the unlock. **It is the live exploit the moment the floor is crossed.**

Therefore:

- **Phase 1 of the plan (the deletions) must merge and deploy BEFORE `GuildWarUnlock` can report unlocked in production.** Today that is far away (1 qualifying player of 50), but the gate is automatic, with no deploy. Phase 1 therefore also adds a **second, explicit condition** to the gate: `GuildWarUnlock.IsUnlocked` stays false while `GuildWarsFormat.Current != HeadToHeadV1`, which is a code constant that Phase 1 sets. The old three-front engine cannot run against any population, even if the new engine is late.
- **The new engine does not inherit the shape.** It is enforced in code and by a test:
  - **Settlement is time-based.** A war resolves only when `now >= BattleEndsAtUtc`, and no other event (pairing, a new guild, a disbanded guild, a restart) can end one early.
  - **Settlement is idempotent**, once per war. `SettledAtUtc` is set in the same transaction as the result, rating and rewards, under `FOR UPDATE` on the war row. A second pass finds it set and does nothing.
  - **Pairing never touches running wars.** The pairing pass and the settlement pass are separate methods with separate queries.
  - Test `GuildWarSettlementTests.ANewGuildCannotEndARunningWar`: create a running war; create two new unmatched guilds; run every pass the engine has, repeatedly, at `now < BattleEndsAtUtc`; assert that the war is unresolved, nothing is paid, and the rating is unchanged. Then advance the clock past the end, run the pass twice, and assert it is resolved once, with rating moved once and rewards once.
  - Test `ADisbandedGuildDoesNotSettleItsWarEarly`: the opponent's guild is deleted mid-war (the last member leaves, per `GuildManagementEngine:~377`). The war still resolves at `BattleEndsAtUtc`, and the remaining guild wins by forfeit rules (§4.6), exactly once.

## 3. What is deleted

Nothing here is kept "for later". Opcode numbers stay **reserved**, never reused.

| Deleted | Where | Note |
|---|---|---|
| Three-front war: pairing, resolution, payout, point queue | `Engine/GuildWarEngine.cs` (4 loops), `GuildWarPointEvent` sources in `SimulationEngine.cs:~4673`, `CraftingTickCoordinator.cs:~58`, opcode 23 `ContributeToWarSupply`, `GuildWarMatches` | replaced by §4. **Before deleting, move the `GuildWarUnlock` refresh** (today driven by this engine's matchmaking loop) into the new engine's loop, keeping the startup warm-up from `696c559` |
| Cross-shard "node HP" war | `Engine/GuildMatchmakingEngine.cs`, `GlobalTournamentMeshService`, `GuildMatchmakingSnapshots`, opcode 50 `SubmitShardAttack`, `/api/v1/guild/shard-match` | client-authoritative damage. The uncapped `CalculateScaledDefenderHp` (gold -> tier -> defender HP) dies with it |
| Turn-based duel | `Engine/GuildCombatSimulationEngine.cs`, `GuildWarActiveMatches`, `GuildWarCombatHistory`, opcode 27 `ExecuteCombatTurn` | no match writer ever existed |
| Defence roster | opcode 49 `RegisterGuildDefense`, `GuildDefenseRosters`, the handler at `SimulationEngine.cs:~2559` | write-only |
| Aggregate snapshot cron + table | the `GuildWarSnapshotEngine` 15-min loop, `GuildWarDefensiveSnapshots` | its only readers are the two engines above. **Keep** `BuildMemberCombatStatsAsync` (made `internal static` in a new home, `GuildWarDefenceSnapshotBuilder`) |
| Wire fields | `StateUpdatePacket`: `ActiveGuildWarId`, `CombatSimulationMatchId/TurnCounter/DamageDelta`, `GlobalNodeRemainingHp`, `CachedWarMultiplier`, the six `Guild*/Enemy*Points` scoreboard fields, `VisualActiveMatchMmr` if present; `ClientCommandPacket.ClientPredictedDamage` only if nothing else reads it (the world boss validator checks it is 0 today; keep the field if removing it changes `ExpectedClientCommandSize` into a demux collision, per the `add-command` skill) | via the `add-command` skill, `npm run generate:protocol`, `NetworkPacketLayoutGuard`, and `StateUpdatePacketFieldCoverageTests` (the seven "Guild Wars, on the roadmap" entries go) |
| Client | `GuildOps.svelte:~251-306` (the `quarantined`, `defend`, `shardMatch`/`matchUuid`/`EMPTY_UUID`, `attackShard`, `matchId`/`turnCounter`/`damageDelta`, `takeTurn` blocks) and their imports (`registerGuildDefense`, `submitShardAttack`, `executeCombatTurn`, `fetchGuildShardMatch`, and `typicalHit` if left unread); the senders in `commands.ts:~842-920` and `rest.ts:~643-667`; the visible three-front "Guild war" panel | **One commit** for the whole block, with `BASELINE` in `client_web/scripts/typecheck-ratchet.mjs` lowered to the count `npm run check` actually reports (expected 0), plus CLAUDE.md's "Four pre-existing svelte-check errors" convention rewritten. Deleting the four alone would **raise** the count (audit A.2) |
| Tables | `GuildWarMatches`, `GuildMatchmakingSnapshots`, `GuildWarActiveMatches`, `GuildWarCombatHistory`, `GuildDefenseRosters`, `GuildWarDefensiveSnapshots` | a **non-additive** migration. Confirm the row counts in production first (read-only SELECT). The audit found 0/0/0/0/1/1, and the two single rows are the one guild's write-only snapshot and roster |

| **Guild Raid** (owner decision, follow-up answer) | `Engine/GuildRaidEngine.cs` (a live cron), `GuildRaidStates`, opcode 53 `LaunchGuildRaid`, its handler in `GuildWarTickCoordinator.cs:~139-148`, the `StateUpdatePacket` fields `GuildRaidTier`/`GuildRaidBossCurrentHp`/`GuildRaidBossMaxHp` (`StateUpdatePacket.cs:~660-662`), the raid half of the hidden **Raid + Logistics** panel (`GuildOps.svelte:~526-540`), and `launchGuildRaid` in `GuildOps.svelte:~22,100-105` and `commands.ts` | 0 rows ever. Its only reward was guild `ContributionPoints`. **The logistics half of that panel is not war code and is not deleted here.** If removing the raid half leaves logistics controls that nothing else reaches, record that in the PR as a finding for the owner rather than deleting them |

## 4. The war

### 4.1 The cycle

- **One war a week** (owner decision, follow-up answer), UTC, keyed to `DelveEngine.CurrentWeekKey`'s ISO week:
  - **opt-in**: Thu 00:00 to Mon 00:00 (it opens as the previous war's results are posted);
  - **prep**: Mon 00:00-24:00 (roster lock, snapshots, pairing, scouting);
  - **battle**: Tue 00:00 to Thu 00:00 (Tuesday and Wednesday);
  - **results**: Thursday 00:00. Settlement, the Great Works weekly settlement and the weekly board payout all run in that order in the first cron pass at or after Thu 00:00. Results are shown all of Thursday, and the next opt-in is open.
- **One constant table**, `GuildWarSchedule`, with a pure `PhaseAt(DateTime utc) -> (cycleId, phase, phaseEndsAt)`. Everything derives from it, and no stored schedule can drift from it.
- **Opt-in:** each member presses **"I'm in"** during the opt-in window (Thursday to Monday 00:00). Leaders can remove a member's opt-in, but cannot add anyone.
- **Eligibility to opt in**:
  - `CurrentLevel >= LeaderboardTierRegistry.MinimumRankedLevel` (10);
  - not quarantined;
  - **tenure**: a member of this guild since before the cycle's opt-in window opened. This needs `GuildMembers.JoinedAtUtc`, a new column; existing rows are null and treated as long-standing.

### 4.2 Prep: roster lock, snapshots, pairing

At prep start, one settlement-style pass runs. It is idempotent per cycle, through `guild_war_cycles.PairedAtUtc`.

1. **Roster.** A guild with at least `MinWarSize = 3` opted-in members enters. **War-size buckets are {3, 5, 10, 15, 20}.** The roster is the first opted-in members (by opt-in time) up to the largest bucket not above the count. First-come is used because it needs no leader UI, cannot be gamed by benching strong members after seeing an opponent, and is explainable in one sentence. The rest are benched for this war and told so.
2. **Snapshot.** For each rostered member, `GuildWarDefenceSnapshotBuilder.BuildAsync(player)` (the existing `BuildMemberCombatStatsAsync` path through `StatsCalculator`) is frozen into `guild_war_roster.StatsJson`, with a scalar `Power`. **The same snapshot is the member's attack profile for this war**, so gear swaps mid-war change nothing and attacks need no live payload.
   `Power = sqrt(EffectiveHp x ExpectedDps)`, both computed by the existing `CombatDamageModel` against a reference monster.
3. **Pairing.** Guilds are grouped by bucket and sorted by **total roster Power** (tie-break: `GuildMMR`). Each is paired with the adjacent one:
   - never the same opponent as in either of the guild's last 2 wars, if any alternative exists;
   - a guild left unpaired in its bucket drops one bucket (trimming its roster to that size, last opt-ins benched) and tries again;
   - still unpaired: **"no opponent this week"**, visible on the Guild screen, and nothing else happens.

   Guilds never choose opponents.
4. **Rosters are visible** to both sides during prep: names, snapshot HP, armour and Power. That is the scouting layer.

### 4.3 Battle: attacks

- Each rostered member has **2 attacks** (`AttacksPerMember`, +1 with the War Banner work, §5). One attack targets one enemy roster slot.
- **Resolution is server-side and deterministic**, through the **one** damage model (`CombatDamageModel`; do not add another):
  - the defender's snapshot becomes a synthetic `MonsterDefinition` (HP, armour, dodge rating, attack, interval) at the defender's highest region tier (for the armour constants);
  - `attackerSeconds = ExpectedSecondsPerKill(attackerStats, defenderAsMonster, ...)`;
  - the defender's time to kill the attacker is computed the same way, mirrored: `defenderSeconds`;
  - `destruction = clamp(min(FightSeconds, defenderSeconds) / attackerSeconds, 0, 1)`, with `FightSeconds = 60`.
  - **Stars:** 1 at >= 40%, 2 at >= 70%, 3 at 100%.
- **A target's stars are the best any attack achieved against it** (as in CoC). The war score is the sum over enemy slots of the best stars, then the sum of best destruction.
- **Optional bounded skill input (later, task 36 reuse):** `AttackResolver.Resolve(attacker, defender, skillMultiplier = 1.0)`, where `skillMultiplier` is clamped to `[1.0, WarSkillCap]` and applied to attacker damage. v1 always passes 1.0. The cap is declared now (1.25) and ledgered (§6), so the later hookup cannot be an uncapped lever.
- **The attack is REST:** `POST /api/v1/guildwar/attack { "WarId": 812, "TargetSlot": 4 }` answers `{ Result, Destruction, Stars, NewBestForTarget, AttacksLeft, View }`. The client sends a war id and a slot, **never a number the server adopts**. It is DB-backed and off the tick (`FOR UPDATE` on the attacker's roster row, so a double-tap cannot spend three attacks).
- **Refusals** (`GuildWarResult`), each 200 with a sentence: `Locked`, `NotInWar`, `NotRostered`, `NotBattlePhase`, `NoAttacksLeft`, `InvalidTarget`, `WarOver`, `Failed`.

### 4.4 Settlement (time-based, idempotent; see §2)

At `BattleEndsAtUtc`, the war row is locked, `SettledAtUtc` is checked, and then:

- **Result:** more stars wins; ties go to more destruction, then to the earlier time of the last star-gaining attack; otherwise a draw.
- **Rating:** Elo on `GuildRecords.GuildMMR` (the existing column, finally written), `K = 32`, expected score from the rating difference. **Anti-throw:** a war in which the losing side made **zero** attacks moves **no** rating for either side.
- **Rewards per war: no diamonds.**
  - guild contribution points to each member who attacked at least once: `WarParticipationPoints` per attack used, into the existing `GuildMembers.ContributionPoints` / `WeeklyContributionPoints`;
  - guild experience for the winning guild through the existing `ApplyGuildExperienceAsync`;
  - nothing convertible to diamonds, and no gold.
- **Notice:** every rostered member gets a mailbox message with the result. Use the existing mail path; a notice nobody hears is the audit's A.3(1) defect.

### 4.5 The weekly board (the only diamond tap)

- **Metric:** `GuildMMR` right after Thursday's settlement, among guilds that **fought that week's war**.
- **Payout** (`GuildWarBoardRegistry`):

  | Rank | Diamonds per member |
  |---|---|
  | 1 | 20 |
  | 2-3 | 12 |
  | 4-10 | 6 |

  - It pays each member who was **rostered and made at least one attack** in that week's war. A benched or idle member gets nothing.
  - **Floors, the two-gate pattern of `LeaderboardTierRegistry`:** a tier pays only if the ranked guild count is at least `MinimumRankedGuilds = 4` **and** at least the tier's `MaxRank`. "Top 10 of 6" does not pay rank 10.
- **Caps, asserted in tests** (the `LeaderboardRewardTests` style, against the constants, not trusting comments):
  - first place per member is **exactly** `GuildWarBoardRegistry.MaxWeeklyDiamondsPerMember = 20`, which is at most `DelveRegistry.MaxDiamondsPerWeek / 3`;
  - **the three weekly taps are pinned together** (owner decision: a separate tap, a weekly maximum of 140). `DelveRegistry.MaxDiamondsPerWeek` (60) + mastery-board first place (`LeaderboardTierRegistry.WeeklyDiamondsFor(1, large)`, 60) + war-board first place (20) == `EconomyDecisions.MaxWeeklyDiamondsAllTaps = 140`. Any change to one tap fails that test until the decision constant is changed on purpose;
  - no rank pays more than first place;
  - a player receives the war-board payout at most once per ISO week (`PlayerRecords.GuildWarPayoutWeekKey`, written in the same transaction as the diamonds).
- **The grant path:** a DB write per player, one transaction each, week key and diamonds together. Then `BillingSyncQueue` (the `AchievementEngine` pattern) so an **online** member's payload learns the new balance. **Never a bare `PremiumDiamonds +=` behind a live session**, which the checkpoint's absolute write (`StateCheckpointManager.cs:~377`) would overwrite. See §8 for the same defect found in `LeaderboardPayoutEngine`.
- **Settlement:** polled (the `LeaderboardPayoutEngine` shape: "pay whoever has not been paid for this week"), inside the new engine's cron. No separate loop.

### 4.6 Edge cases

- **The opponent guild is deleted mid-war:** the war still settles at `BattleEndsAtUtc`. The remaining guild wins with its stars as scored; no rating moves (anti-throw: the deleted side cannot attack).
- **A member leaves the guild mid-war:** their roster row stays (their defence can still be attacked), and they cannot attack for that war.
- **The server is down at `BattleEndsAtUtc`:** the first pass after restart settles every war that is due and unsettled.

## 5. Guild Great Works (the treasury gold sink)

- **What it is:** three Works per guild, each with **levels 0-3**:

  | Work | Perk per level | Hard cap |
  |---|---|---|
  | **Palisade** | defender effective HP in wars +5% | +15% |
  | **Siege Engines** | attacker damage in wars +5% | +15% |
  | **War Banner** | at level 3 only: +1 attack per rostered member | 3 attacks |

- **The weekly goal:** each week (ISO week), each Work has a goal.
  - `goal = max(WorkFloorGold, 1.1 x lastWeekContributed)`, where `WorkFloorGold = 250,000 x the guild's qualifying member count`. So it follows the guild's own income.
  - **Met by the week's end:** the level rises by 1, capped at 3.
  - **Missed:** it falls by 1. The level is upkeep, so the sink recurs.
  - The **Thursday results pass** in the war engine's cron applies this, right after the war settles, idempotent through `guild_great_works.SettledWeekKey`. A Works week is the war week (Thu 00:00 to Thu 00:00).
- **Funding:**
  - (a) members donate their **own** gold: `POST /api/v1/guild/works/donate { Work, Amount }`. A DB debit on the member's locked `CommodityRecords` gold row, then `ReloadState`. **Never `RedisPendingGoldDelta`.**
  - (b) the leader or an officer moves **treasury** gold: `POST /api/v1/guild/works/fund { Work, Amount }`. A debit on `GuildRecords.GuildTreasuryGold`, `FOR UPDATE`.

  Donated or funded gold is destroyed: it is a sink, not a transfer. Over-goal donation is allowed and counts toward next week's goal base (`lastWeekContributed`).
- **Replacing the old path:**
  - the existing gold donation (opcode path `GuildTickCoordinator` -> `ContributeGoldAsync`) is **redirected** to the Works (the member picks one; the default is Palisade);
  - it stops raising `CurrentTier` through gold. Guild tier keeps its equipment and material sources;
  - the uncapped linear chain gold -> tier -> `CalculateScaledDefenderHp` is gone with `GuildMatchmakingEngine`.
  - A test pins that no war number reads `CurrentTier`.
- **Bounded, and inside the war only:**
  - the perks are read **only** by `AttackResolver` and the attack-count rule;
  - a grep test fails if any file outside `Domain/Social/GuildWar*` reads the Works table or its perk values. That keeps the PvE ledger and the gold -> gold loop out (the guild Gold buff is a gold multiplier).
  - `PowerCeilingTests` is not touched, but a new `GuildWarLedgerTests` asserts each perk's hard cap, and that the product of Palisade, Siege, `WarSkillCap` and the Banner stays at or under a stated `WarLeverCeiling` (1.15 x 1.15 x 1.25 x 1.5 attacks).
- **The audit:** combat gold is a named constant after task 37 Phase 0, so recording Works gold in `GuildMaterialSinkLedgers` (the audit's "consumed") can no longer move combat gold. **Task 37 Phase 0 must merge before this phase.**

## 6. Data model (snake_case `[Table]` for every new table; add them to `CURRENT_IMPLEMENTATION_STATE.md` §3)

| Table | Key columns |
|---|---|
| `guild_war_cycles` | `CycleId` (the ISO week, e.g. `2026W41`), `OptInClosesAtUtc`, `PrepEndsAtUtc`, `BattleEndsAtUtc`, `PairedAtUtc?` |
| `guild_war_optins` | `(CycleId, PlayerId)` PK, `GuildId`, `OptedInAtUtc` |
| `guild_wars` | `WarId`, `CycleId`, `GuildAId`, `GuildBId`, `WarSize`, `BattleEndsAtUtc`, `StarsA/B`, `DestructionA/B` (milli), `LastStarAtA/B`, `Outcome`, `RatingDeltaA/B`, `SettledAtUtc?` |
| `guild_war_roster` | `(WarId, PlayerId)` PK, `GuildId`, `Slot`, `StatsJson` (jsonb, cast explicitly: the 42804 trap recorded in `GuildWarSnapshotEngine`), `Power`, `AttacksUsed`, `BestStarsAgainst`, `BestDestructionAgainst` |
| `guild_war_attacks` | `AttackId`, `WarId`, `AttackerId`, `TargetSlot`, `DestructionMilli`, `Stars`, `AtUtc` |
| `guild_great_works` | `(GuildId, Work)` PK, `Level`, `WeekKey`, `Goal`, `ContributedThisWeek`, `ContributedLastWeek`, `SettledWeekKey` |
| `GuildMembers.JoinedAtUtc` | new nullable column (tenure) |
| `PlayerRecords.GuildWarPayoutWeekKey`, `GuildWarPayoutRank` | the idempotent weekly payout |

**Wire:** **no `StateUpdatePacket` fields.** The war is REST:

- `GET /api/v1/guildwar` (cycle, phase, time left, my opt-in, roster, opponent roster, my attacks, the score);
- `POST /api/v1/guildwar/optin`;
- `POST /api/v1/guildwar/attack`;
- `GET /api/v1/guild/works`;
- `POST /api/v1/guild/works/donate|fund`;
- `GET /api/v1/leaderboard/guildwar`.

The Guild screen polls `GET /api/v1/guildwar` (TanStack query, a 30 s refetch while visible), so "a war started while I was online" cannot recur. It was a login-only hydration defect (`ActiveGuildWarId`), and REST has no login hydration to forget. The old opcodes 23/27/49/50 answer `GuildWarsLocked` until they are removed, and then they stay **reserved**.

**Cron:** one new loop, `GuildWarCycleEngine.StartCron`:

- it runs every 60 s and does, in order: the unlock refresh, the pairing at prep start, due settlements, the Works weekly settlement, and the weekly board payout;
- **every pass is isolated in its own try that opens before `CreateScope`**;
- each pass takes a **budget** (for example 50 wars per pass);
- it reports the depth of due-and-unsettled wars in the heartbeat.

`CronWorkerGuardTests.KnownCronEngines`:

- **add** `GuildWarCycleEngine`;
- **remove** `GuildWarEngine`, `GuildMatchmakingEngine`, `GuildWarSnapshotEngine` and `GuildRaidEngine`.

## 7. Client

- **Guild screen, "War" panel, by phase:**
  - locked: PR #22's progress line;
  - opt-in: an "I'm in" toggle (a button, not a checkbox);
  - prep: both rosters and a countdown **outside any control**;
  - battle: the enemy roster as rows with the best stars so far, and a plain **Attack** button per row (44 px or more, `flex-shrink: 0`), plus "attacks left: N" and the score;
  - result.
- **Great Works panel:** three bars (goal, progress, level) and Donate buttons with fixed amounts (10k / 100k / 1M / 10M) and a custom amount field. **No `<select>`.** The officer's "fund from treasury" is shown with `{#if}` by role.
- **Leaderboards:** a "Guild War" tab.
- **Svelte rules:** runes mode, no `derived` binding, `{#if}` rather than `<details>`, snippets with `{@render}`.
- **Geometry and exercise:** a `screens.mjs` state for a war in battle. `exercise.mjs`, with a dev route that forces a war between two fixture guilds (§8 of the plan): opt in, attack, assert the stars and attacks-left moved, and round-trip (the dev route also clears the war).

## 8. Side findings (not the owner's to answer; fix in the plan)

### 8.1 Standalone defect: the weekly leaderboard's diamonds can be overwritten for an online player (may be pulled forward as its own fix)

**Files:**

| File:line | What |
|---|---|
| `server/FolkIdle.Server/Engine/LeaderboardPayoutEngine.cs:177` | `player.PremiumDiamonds += diamonds;` committed with the week key (`:178`). The class holds no `PlayerSessionRegistry`, and sends no `BillingSyncQueue` notice and no `ReloadState` |
| `server/FolkIdle.Server/Domain/Shared/StateCheckpointManager.cs:377` (and `:1550`) | `player.PremiumDiamonds = state.PremiumCurrency;`, an **absolute** write from the live payload on every checkpoint |
| `server/FolkIdle.Server/Engine/AchievementEngine.cs:~195-215` | the same bug, documented and fixed (2026-08-02) by enqueuing a `BillingSyncNotification` with the authoritative balance, so the tick thread updates `PremiumCurrency` |

**What happens:**

- For a player who is **online** when the payout pass runs, the payload's `PremiumCurrency` still holds the old balance.
- The next checkpoint writes that stale value back over the row, and the week's diamonds vanish.
- The week key was written, so the payout is never retried.
- Offline players are unaffected, which is why it would be hard to notice.

It has not fired in production only because the mastery board has never cleared `MinimumRankedPopulation` (20).

**Fix:** after each per-player commit, enqueue `BillingSyncQueue` with the new authoritative balance, exactly as `AchievementEngine` does. The constructor gains the registry (`Program.cs`, where the engine is constructed).

**Test:** pay rank 1 to a player who is in the session registry with a stale payload, run one checkpoint flush, and assert the row still includes the payout.

It is task 38 Phase 1 Task 1.5 in the plan, but it touches nothing war-related and can ship on its own. The war board (§4.5) must use the fixed pattern either way.
- **`GuildContributionEngine.ContributeGoldAsync`** debits gold with no `ReloadState`, so the header shows the old balance until a relog. Fixed where the Works take over the donation.

## 9. Owner answers (2026-09-24, final)

1. **The war board is a separate diamond tap**, capped at 20 per member per week. The weekly maximum across all taps is 140 (60 + 60 + 20), pinned by a test (§4.5).
2. **Guild Raid is deleted** with the other old war code (§3).
3. **One war a week:** prep Monday, battle Tuesday and Wednesday, results Thursday (§4.1).
