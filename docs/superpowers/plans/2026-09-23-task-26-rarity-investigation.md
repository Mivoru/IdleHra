# Task 26: no Ancient+ drop in ~5 days (investigation, and a drop record)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Answer "Ancient+ stopped dropping" from measurements, not guesses. Pin every loot-luck term in a test that prints each one and asserts it. Fix only the defects the investigation confirmed. Take any change to the drop weights to the owner as an explicit decision. Add a durable **drop record** (tier, source, time) so the next "this feels sus" can be answered with a query, instead of being pieced together from row ids.

**Architecture:** One number decides rarity. `RarityTier.RollTier(lootLuckPct)` (`server/FolkIdle.Server/Engine/CombatLootEngine.cs:126`) multiplies the weights of tiers 2-14 by `1 + L/100` and keeps Normal at 100. `TryRollEquipment` (`CombatLootEngine.cs:1152`) then applies Golden Fleece (+2 tiers on every 100th kill, if the crown is taken) and Fortune's rarity elevation (+1 tier with chance `RarityElevationPct`). `L` is summed once, in `CombatLootDropRequest.Build` (`CombatLootEngine.cs:321`). The live tick (`SimulationEngine.cs:4712`) and offline catch-up (`OfflineSimulationEngine.cs:759/765`) both call it with a `CombatStats` from the same `StatsCalculator.Calculate` signature. Equipment rows (`EquipmentInstances`) have **no timestamp and no origin column**. `EcoTelemetryLedgers` holds only gold/diamond totals (`Models/EcoTelemetryLedger.cs`), so nothing in the database records a drop today.

**Tech stack:** C# / .NET 8 server, EF Core migrations on Postgres (Supabase in prod), xUnit + Testcontainers, Svelte 5 client (only for the optional admin view).

**Spec:** `docs/TASK_BOARD.md` "## 26. Rarity: no Ancient+ drop in ~5 days (investigate before changing anything)" (~line 3395). Constrained by task 9 (~lines 609-1030): PHASE F "a full rarity ladder is worth one region step" (`RarityTier.TopTierPowerMultiplier = 2.12`, `ItemRarityPowerTests` band 2.85-3.15), and "hold the player's power curve where it is" (the monster HP buff per region was matched to the **measured best-of-N equipped tier**, which the drop table feeds directly).

## Global Constraints

- **Measure first, change second.** Task 1 changes no behaviour. Task 2 changes only confirmed defects. Task 3 is a decision gate: **no drop weight, luck coefficient, elevation rate or pity rule changes without the owner's explicit answer.** Talk to the owner in Czech (standing project preference).
- **The luck sum exists once.** `CombatLootDropRequest.Build` carries a comment about the offline copy that drifted. Any breakdown, admin view or test must read the terms from the same function Build uses. Never restate the sum in a second place.
- **A number a test PRINTS is not a number a test CHECKS.** Every printed luck term and expected rate needs an assertion beside it.
- **Migrations run on the container ENTRYPOINT, not on app start.** After adding one, apply it locally by hand (`$env:FOLKIDLE_DB_CONN=...; dotnet run --project server/FolkIdle.Server/FolkIdle.Server.csproj --migrate`). If you skip that, the next local sign-in hangs.
- **New snake_case tables carry `[Table("...")]`**, and any raw SQL must use that name. Add the table to `docs/architecture/CURRENT_IMPLEMENTATION_STATE.md` §3.
- **No new background loop without `CronWorkerGuardTests` knowing about it.** The try/catch must enclose `CreateScope`/`BeginTransactionAsync`. Any drain must be budgeted (read the depth once per cycle), never `while (TryDequeue)` around an `await`.
- **Stop the running server before `dotnet build`/`dotnet test`.** The hook blocks it anyway, and a blocked chained command runs NOTHING. Keep edits and builds in separate calls. `dotnet test` needs Docker up.
- **Do not touch the monster ladder, the rarity power curve or `GetAffixCount`** in this plan.
- One commit per task, each independently revertible.

---

## What the investigation already found (2026-09-23, read-only)

Sources: the code at `219d2bf`, `git log`, and SELECT-only queries against production (Supabase).

### Player 8 ("Mivoru") inputs, from production

| input | value | source |
|---|---|---|
| level | 94 | `PlayerRecords` |
| STR/DEX/CON/**LCK** | 200 / 200 / 120 / **300** | `PlayerRecords.Base*` |
| `AutoSalvageBelowTier` | 0 | `PlayerRecords` |
| `ForgeFusionsCompleted` | **0** | `PlayerRecords` (counter exists since `16df59c`, 2026-08-09) |
| skill tree | root 0 (Fortune) **10**, bough 6 (Rarity) **8**, crown 15 (Golden Fleece) **1**, root 2 10, bough 9 2 | `player_skill_tree` (`BranchId`,`Level`) |
| inheritance | stat 5 (`StatLootLuck`) **4**, stat 0 4, stat 2 1 | `player_inheritance_stats` |
| guild 1 buffs | `DropRate` tier 4 **expired 2026-09-09 15:09 UTC**; `Damage` tier 4 expired 2026-09-20 | `GuildActiveBuffs` |
| lineage aptitude | `AptitudeFortune` 4 on 11 characters, 6 on one; all `TraitMask` 0, no epic mutation | `character_lineage_registry` |
| affix loot luck | 0 (no item carries the legacy `"4"` key, the only affix mapped to `LootLuckTenthsPct`) | `EquipmentInstances.AffixPayload` |
| codex kills | ~176,500 total; region 5 (ids 111-114) ~13,000 | `monster_codex_entries` |

### The computed luck breakdown (live formula, by hand)

| term | formula | value |
|---|---|---|
| LCK curve | `AttributeRegistry.DiminishedPercent(1.2, 300)` = 1.2·√300 | **20.78** |
| Scavenger milestone (LCK ≥ 25) | `AttributeRegistry.Milestones` | **8** |
| area completion | `CompletedAreaFlags`, +1 per region | **0** (see H5) |
| equipped affixes | `LootLuckTenthsPct / 10` | **0** |
| inheritance loot luck | `InheritanceRegistry.GetBonusPct(4)` = 4·2 | **8** |
| Fortune root (`BranchLootRarity`) | 10 lvl × 1.0% | **10** |
| guild `DropRate` | tier × 2 | **0** now (was **8** until 2026-09-09) |
| Rarity bough (`BoughRarity`) | 8 lvl × 1.0%, **added to luck** | **8** (see H4) |
| Fortune aptitude | `BreedingAptitudes.BonusPercentFor(4)` = 4·1.5 | **6** (9 if the aptitude-6 character is `characters[0]`) |
| **LootLuckPct** | | **≈ 60.8** (68.8 before 2026-09-09) |
| RarityElevationPct | 0.35·√300 | **6.06%** |
| Golden Fleece | +2 tiers, 1 kill in 100 | on |

### Expected rates from that breakdown (tiers after fleece and elevation)

| LootLuckPct | Legendary+ per drop | Ancient+ per drop | drops per Ancient+ | Ancient+ / Legendary+ | P(0 Ancient+ in 976 drops) |
|---|---|---|---|---|---|
| 0 (nothing at all) | 0.847% | 0.0336% | 2,975 | 3.97% | 0.72 |
| **60.8 (player 8 now)** | **1.195%** | **0.0495%** | **2,018** | **4.15%** | **0.62** |
| 68.8 (with the guild buff) | 1.218% | 0.0505% | 1,981 | 4.15% | 0.61 |
| 100 | 1.295% | 0.0537% | 1,863 | 4.15% | 0.59 |
| ∞ (theoretical ceiling) | 1.724% | 0.0684% | 1,462 | 3.97% | 0.51 |

**Structural fact that drives the whole answer:** luck multiplies all thirteen non-Normal weights *equally*. It can only shrink Normal's share, so no amount of luck more than about doubles Legendary+ or Ancient+. The **ratio Ancient+ : Legendary+ does not depend on luck** (~4%). That ratio is the one comparison the biased survivor set allows, because auto-salvage and the sweep never remove Legendary+ (`VillageChestEngine.MaxSweepableQualityTier = 6`).

### Observed, from production

- After the last Ancient+ (row id 64019): **976 rows for player 8, 11 Legendary+ (1.13%), 0 Ancient+.** Expected: 11.7 Legendary+ and 0.48 Ancient+. Seeing none happens 62% of the time.
- Ids 42270-65015: 1,246 rows, 21 of them other players'. Player 8 holds **252 Legendary+ and 13 Ancient+ (5.2%)** against the expected 4.15%.
- All survivors: 630 Legendary+, 29 Ancient+ = **4.6%**. Tier 13 (Godly): 1, against 0.4 expected. Tier 12 (Demonic): 4, against ~1.9 expected.
- Region-5 Ancient+ ids: 42270, 42499, 43890, 43965, 46705, 48797, 53181, 53187, 60074, 60103, 60245, 61870, 64019. The gaps run 30 to ~7,000 ids around a mean of ~1,800. The current gap of ~1,000 is **ordinary**.

### Hypotheses and verdicts

| # | hypothesis | evidence | verdict |
|---|---|---|---|
| H1 | The old Godly/Demonic pieces were **forged**, not dropped | `ForgeFusionsCompleted = 0` (counter live since 2026-08-09). Fusion appends an affix keyed `{type}_{4 hex}` with no `@rarity` suffix (`ForgeSplicingEngine.cs:355`). **0 of 1,604 rows** carry such a key, and all 1,604 carry drop-style `id@rarity` keys. Fusion upgrades **in place** (`targetItem.QualityTier = currentTier + 1`), so a fused item would keep its old low id, but none shows the forge key. | **Ruled out.** They were drops. |
| H2 | Player 8's real `LootLuckPct` is far below what is expected | The formula gives ≈60.8. The "1.1% matches luck near zero" premise was wrong: 1.1% Legendary+ fits **any** luck between 0 (0.85%) and ∞ (1.72%) at n=800, since the standard deviation is ~3 drops. Every term's source row exists and is hydrated (`StateCheckpointManager.cs:1030,1116-1123,1182`). One term did fall: the guild `DropRate` buff **expired 2026-09-09**, costing 8 points, which moves Ancient+ from 0.0505% to 0.0495% (−2%). | **Ruled out** as the cause. The breakdown still has to be *pinned by a test* (Task 1), because this is a hand computation. |
| H2b | A luck term changed around the 2026-09-18 deploy of `84fe16c` | `84fe16c` is a docs-only commit. `git log --since=2026-09-10` on `CombatLootEngine.cs`, `StatsCalculator.cs`, `AttributeRegistry.cs`, `OfflineSimulationEngine.cs`, `Trait*.cs`, `SkillTreeRegistry.cs` and `InheritanceRegistry.cs` shows only the retry outbox (`a4993cb`), test worker cleanup (`1ef5318`), traits replacing the Speed/Crit genes (`3f25d13`, which *adds* trait elevation) and the aging curve. No term was removed or rescaled. `-S LootLuck` last touched on 2026-09-06 (`aeb4d43`: LCK went from `0.1·lck` = 30 to `1.2·√lck + 8` = 28.8, which is flat for this account). | **Ruled out.** |
| H3 | Offline catch-up fills fewer luck terms than live | Both call `CombatLootDropRequest.Build` with a `CombatStats` from the identical 16-argument `StatsCalculator.Calculate` (`OfflineSimulationEngine.cs:613`, `SimulationEngine.cs:3768`). Offline passes `BonusRarityTiers` for fleece procs and live passes it per kill. | **Ruled out** (already fixed in `75e4d74`, 2026-09-03). |
| H4 | *(found on the way)* The **Rarity bough is sold as one thing and does another** | `SkillTreeRegistry.cs:196,232` and `client_web/src/lib/net/commands.ts:1115` describe it as "A drop has a chance to roll one rarity higher than it should", 1%/level. `Build` adds it to **LootLuckPct** instead ("the same currency as the root, so it simply adds"). 8 levels = +8 luck (Ancient+ +~1.5% relative) instead of +8% elevation (Ancient+ +~11% relative: 0.0495% becomes 0.0550%). | **Confirmed defect** (description and implementation disagree). The fix is small but changes drop odds, so it goes through the Task 3 gate with a recommended default. |
| H5 | *(found on the way)* **Area-completion luck cannot be earned** | `StateCheckpointManager.cs:714-722` sets a region's bit only if **every** monster with that `GetMonsterRegionTier` has 1,000 codex kills. `monsters.json` still holds the 90 legacy monsters (ids 1-90), which nobody can fight, and they count toward regions 1-10. So no region can ever complete, and the +1/region `StatsCalculator.cs:178-186` term is **0 for every player**. The 1,000-kill rule would also require ~1,000 kills of each regional boss (player 8: 2 / 8 / 33 / 2 / 0). | **Confirmed defect** (a dead lever: computed and read, but its input is unreachable). Worth at most +5 luck, which is about +3% relative Ancient+. The fix (canonical monsters only) goes through the gate because it grants luck. The boss-kill threshold is a design question for the owner. |
| H6 | The owner's *perceived* drought comes from volume, not odds | Ancient+ arrives about once per 2,000 drops. At ~195 drops/day (976 rows in ~5 days) the **mean wait is ~10 days**. Region-4 farming (monsters 106-110: ~53,000 kills) was faster per kill than region 5 (~13,000 kills), so the owner probably saw more drops per day earlier. Rows have no timestamps, so drops/day over time **cannot be measured today**. | **Still open.** This is the most likely explanation, and the drop record (Task 4) exists to prove or refute it. |

**Bottom line:** the odds did not change. Everything observed fits the authored table at L ≈ 61. Two unrelated defects in the luck inputs are real (H4, H5), and neither explains the report. Whether Ancient+ *should* be this rare is a design question (Task 3).

---

## How to approach this (for the implementing agent)

1. **Do Task 1 first and alone.** It pins today's behaviour. If its assertions disagree with the table above, stop: the hand computation was wrong, so re-open H2 before anything else.
2. **Task 2 fixes nothing that changes odds.** It only extracts the pure pieces so they can be tested and shown. H4 and H5 wait for the gate.
3. **Task 3 is a conversation, not code.** Bring the table above plus the options list to the owner, in Czech. Record the answer in `docs/TASK_BOARD.md` task 26 before implementing any option.
4. **Task 4 (the drop record) ships whatever the owner decides.** The task board says "Add a drop record either way".
5. Verification, deploy, docs last (Tasks 5-6).

---

### Task 1: Pin every luck term and the expected rates for a real level-94 region-5 build

**Files:**
- Modify: `server/FolkIdle.Server/Engine/CombatLootEngine.cs` (add `LootLuckBreakdown` and make `Build` sum it)
- Modify: `server/FolkIdle.Server.Tests/RarityRollDistributionTests.cs` (extend; it already restates the weights on purpose and carries the "ask the roll itself" rationale)
- Check first: `server/FolkIdle.Server.Tests/HardenedEngineIntegrationTests.cs` `Test_CombatLootDropRequest_LuckSumCarriesEveryRaritySource` and `SkillNodeEffectTests.LootLuckAndMaterialQuantityAreNotSpliced`. Extend those rather than duplicating them if they already cover a term.

**Interfaces:**
```csharp
public readonly struct LootLuckBreakdown
{
    public float Stats { get; init; }            // combatStats.LootLuckPct (LCK curve + milestones + area + affixes)
    public float Inheritance { get; init; }
    public float FortuneRoot { get; init; }
    public float GuildDropRate { get; init; }
    public float RarityBough { get; init; }
    public float FortuneAptitude { get; init; }
    public float Total => Stats + Inheritance + FortuneRoot + GuildDropRate + RarityBough + FortuneAptitude;
    public static LootLuckBreakdown From(in TickStatePayload payload, in CombatStats combatStats);
}
```
`CombatLootDropRequest.Build` sets `LootLuckPct = LootLuckBreakdown.From(payload, combatStats).Total`, so the sum still exists exactly once. Keep the existing `// Modul:` comments and move them onto `From`. Read the whole initialiser before editing. CLAUDE.md records a blind multi-line edit that re-parented three terms.

- [ ] **Step 1:** Read `CombatLootEngine.cs:301-357` in full, then add `LootLuckBreakdown` directly above `CombatLootDropRequest`. Refactor `Build` to use it. Do not change any coefficient.
- [ ] **Step 2:** In `RarityRollDistributionTests`, add `ALevel94Region5Build_EveryLuckTermIsWhatTheFormulaSays`:
  - Build a `TickStatePayload` with LCK 300, STR/DEX 200, CON 120, `Inherit_LootLuck = 4`, `Skill_LootRarity = 10`, `Skill_Rarity = 8`, `Skill_GoldenFleece = 1`, `Aptitude_Fortune = 4`, `GuildId = 0` (the cache returns 0), `CompletedAreaFlags = 0`, and an empty `CachedAffixTotals`.
  - Compute `StatsCalculator.Calculate(...)` with the same 16 arguments the live tick passes (`SimulationEngine.cs:3768`).
  - **Print** each term and **assert** each one to 0.01: Stats 28.78 (20.78 curve + 8 Scavenger), Inheritance 8, FortuneRoot 10, Guild 0, RarityBough 8, Aptitude 6, Total 60.78, and `RarityElevationPct` 6.06.
  - Assert `Build(...).LootLuckPct == breakdown.Total` so the test and the drop path cannot drift apart.
- [ ] **Step 3:** Add `ExpectedRatesAtThatBuild_MatchTheInvestigationTable`. Use the file's own restated `Weights`, not RollTier's private array. Compute analytically the Legendary+ and Ancient+ shares after fleece (1%, +2) and elevation (6.06%, +1). **Assert** Legendary+ in [1.15%, 1.24%], Ancient+ in [0.047%, 0.052%], and Ancient+/Legendary+ in [3.9%, 4.3%]. Also assert the **luck ceiling**: at L = 1e6, Legendary+ < 1.75%. That documents that luck can never more than about double the top, and the test fails loudly if someone changes the roll's shape without meaning to.
- [ ] **Step 4:** Add a Monte-Carlo cross-check through the real chain once Task 2 has extracted it (2,000,000 rolls at L = 60.78, elevation 6.06%, fleece 1%). Assert Legendary+ within 3% relative of the analytic value, and Ancient+ within an order-of-magnitude band, following the existing file's convention for rare tiers.
- [ ] **Step 5:** Stop any running server, then `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~RarityRollDistributionTests|FullyQualifiedName~SkillNodeEffectTests|FullyQualifiedName~HardenedEngineIntegrationTests.Test_CombatLootDropRequest"`. Expected: all green, and the printed table matches the investigation table above.
- [ ] **Step 6: Commit.** `test(loot): pin every loot-luck term and the expected rarity rates for a level-94 region-5 build`

### Task 2: Extract the tier resolution so it is testable (no behaviour change)

**Files:**
- Modify: `server/FolkIdle.Server/Engine/CombatLootEngine.cs`

**Interfaces:**
```csharp
// RarityTier
public static (int Rolled, int Final) ResolveDropTier(float lootLuckPct, int bonusRarityTiers, float rarityElevationPct);
```
`TryRollEquipment` calls this in place of the three inline steps (`RollTier`, fleece clamp, elevation clamp). Returning `Rolled` as well as `Final` is what lets the drop record (Task 4) say whether a piece was *rolled* Ancient or *lifted* into it.

- [ ] **Step 1:** Move the three steps from `CombatLootEngine.cs:1165-1192` into `ResolveDropTier`, keeping order (roll, then fleece, then elevation) and the clamp to `CraftingEngine.RarityTierCount`. Keep the `// Modul:` comments with the code they explain.
- [ ] **Step 2:** Wire Task 1 Step 4's Monte-Carlo test to `ResolveDropTier`.
- [ ] **Step 3:** Run the same filtered test command plus `--filter "FullyQualifiedName~Loot"`. Expected: green, and printed distributions unchanged from Task 1.
- [ ] **Step 4: Commit.** `refactor(loot): one pure function resolves a drop's final tier`

### Task 3: DECIDE WITH THE OWNER (gate, no code until answered)

Present the investigation table and these options in Czech. Record the answer under task 26 in `docs/TASK_BOARD.md`. **Every option except A moves drop odds, and so moves the median equipped tier.** Task 9's per-region monster HP buff (1.00/1.36/1.47/1.58/1.70) was matched to that median, so any accepted option must re-run `ItemRarityPowerTests.HowMuchStrongerThePlayerGets_WhichIsExactlyTheMonsterBuffOwed` and `WhatTheMonsterBuffActuallyRestored_KillTimeAndXpRate` and show the before/after table. If XP/sec at any region moves outside ±2%, that is a **separate** monster-HP change with its own table, never folded in.

What does **not** move: the one-region-step rule (`PowerMultiplier` per tier is untouched, the ladder stays 3.00x : 3.00x) and `PowerCeilingTests`' maximum (it already assumes tier 14 on every slot). Only the *rate* of reaching it changes. Treat any new pity/luck constant as a multiplier that needs a cap or a curve (CLAUDE.md rule): add it to the `PowerCeilingTests` ledger if it scales, or state its hard cap.

| option | what it changes | effect on player 8's Ancient+ | balance consequence |
|---|---|---|---|
| **A. No weight change** (recommended default) | nothing but the record and an in-game odds line (Wiki) | 0.0495% (~1 per 2,000 drops, ~10 days at today's volume) | none |
| **B. Fix H4 as advertised:** Rarity bough becomes elevation (+1%/lvl to `RarityElevationPct`), no longer luck | `Build` drops the term; `StatsCalculator` or the request adds it to elevation | 0.0550% (+11%) | small; median equipped tier moves by far less than 0.1 |
| **B'. Fix H4 by rewording** the bough to "+1% loot luck per level" | text only (`SkillTreeRegistry` blurb and `commands.ts:1115`, which must stay in sync) | unchanged | none, but the node stays weak |
| **C. Fix H5:** area completion counts canonical monsters (91-115) only, with the 1,000-kill threshold kept or lowered for bosses (owner's call) | `StateCheckpointManager.cs:714-722`; also check whether anything else reads `CompletedAreaFlags` | at most +5 luck: ~+3% relative | negligible |
| **D. Tilt luck toward the top:** multiply tier *t*'s weight by `f^((t-1)/13·k)` instead of flat `f` | `RollTier` shape | e.g. k=2 roughly doubles Ancient+ at L=61 (**measure, do not quote**) | changes the median equipped tier; requires the task 9 re-measurement and possibly a monster-HP follow-up |
| **E. Bad-luck protection:** guaranteed Ancient+ after N drops without one (per player, persisted) | a counter on `PlayerRecords`, loaded at login and checkpointed (it is **not** a Redis frame field; see CLAUDE.md), carried on the request | caps the drought (e.g. N = 4,000 is 2x the mean); mean moves only slightly | new wire/persistence field; `StateUpdatePacketFieldCoverageTests` applies only if it goes on the wire |
| **F. Raise the explicit weights of tiers 10-14** | `_explicitWeights` | proportional | same as D; re-run `RarityRollDistributionTests.TheRollMatchesItsAuthoredTable` with the new weights restated |

- [ ] **Step 1:** Send the owner the table and options. Wait.
- [ ] **Step 2:** Write the decision into `docs/TASK_BOARD.md` task 26 ("Decided with the owner, <date>: ..."), with the numbers.
- [ ] **Step 3:** If B, C, D, E or F was chosen, implement it as its **own commit**. Update Task 1's asserted values in the same commit (the bands encode today's behaviour and are meant to fail when it changes), and paste the before/after `ItemRarityPowerTests` tables into the commit body. Commit per option, e.g. `fix(skills): the Rarity bough elevates drops, as its card has always said`.

### Task 4: The drop record

**Why two tables.** The problem was a biased denominator: survivors minus salvage minus sales. Recording every drop would add ~195 rows/day for one player, which is the same growth that took `EquipmentInstances` to 17,836 rows. So: **counts for everything, rows only for what is rare.**

**Files:**
- Create: `server/FolkIdle.Server/Models/LootTierDailyCount.cs`, `server/FolkIdle.Server/Models/NotableItemEvent.cs`
- Modify: `server/FolkIdle.Server/Models/FolkIdleDbContext.cs` (DbSets; composite key/index)
- Create: migration `AddDropRecord` (via `dotnet ef migrations add AddDropRecord --project server/FolkIdle.Server/FolkIdle.Server.csproj`)
- Create: `server/FolkIdle.Server/Engine/DropRecord.cs` (the one writer helper, used by every source)
- Modify writers: `CombatLootEngine.cs` (live + offline + boss), `Domain/Economy/ForgeSplicingEngine.cs`, `Domain/Economy/CraftingEngine.cs`, `Engine/CodexEngine.cs` (first-clear trophy), `Engine/WorldBossEngine.cs`, `Domain/Combat/SimulationEngine.cs:2805` (chronicle pass), `Engine/PendingGrantOutbox.cs` (retry path)
- Modify: `docs/architecture/CURRENT_IMPLEMENTATION_STATE.md` §3

**Schema:**
```csharp
[Table("loot_tier_daily_counts")]            // PK (PlayerId, Day, Source, RegionTier, QualityTier)
public class LootTierDailyCount {
    public long PlayerId; public DateOnly Day;   // UTC day
    public short Source;                         // DropSource enum
    public short RegionTier; public short QualityTier;
    public int Count;                            // drops that landed at this final tier
    public int SalvagedCount;                    // of those, auto-salvaged on the way in
}

[Table("notable_item_events")]               // PK Id; index (PlayerId, CreatedAtUtc)
public class NotableItemEvent {
    public long Id; public long PlayerId;
    public long? EquipmentInstanceId;            // null only if written before the row had an id
    public string BaseItemId; public short Source;
    public short RolledTier; public short FinalTier;   // FinalTier > RolledTier = fleece/elevation/forge
    public float LootLuckPct;                    // the L the roll used; 0 for non-roll sources
    public DateTime CreatedAtUtc;
}
public enum DropSource : short { LiveKill = 1, BossGuarantee = 2, Offline = 3, Forge = 4, Craft = 5, FirstClearTrophy = 6, WorldBoss = 7, ChroniclePass = 8, OutboxRetry = 9 }
```
Notable = `FinalTier >= RarityTier.Legendary` (7), about 1.2% of drops, so ~2-3 rows per day for the heaviest player. Forge and trophy events are always notable (they are rare and they are the origin question H1 had to reconstruct). Market and mail **move** items; they do not create them, so they are not recorded.

**Writers (no new worker):**
- `CombatLootEngine.ProcessMonsterLootDropAsync` already runs inside one SERIALIZABLE transaction on its own single-threaded worker, which is guarded and has a budgeted drain. Accumulate a per-request `Dictionary<(source, region, tier), (count, salvaged)>` in `TryRollEquipment` (using `ResolveDropTier`'s result) and upsert it **once per request** before `SaveChangesAsync`, with `INSERT ... ON CONFLICT (...) DO UPDATE SET "Count" = loot_tier_daily_counts."Count" + EXCLUDED."Count"`. Use the snake_case table name in raw SQL. One statement per request, not per kill: offline passes `kills = N` and this must cost O(tiers), not O(N). Add notable rows to the change tracker. Both commit or roll back with the drop.
- Distinguish **Offline** by a new request field `DropSource Source`. The default `0` means LiveKill (document it like `Kills`, where "zero means one"; map 0 to LiveKill in one place). Offline `Build` calls pass `Offline`, and the boss guarantee roll records `BossGuarantee`.
- The **retry outbox**: if the drop transaction throws, the counts roll back with it. `PendingGrantOutbox` must then record the grant (source `OutboxRetry`, plus the original source if `EquipmentGrantPayload` carries it) when it replays. Otherwise the record under-counts exactly the drops the retry saved.
- `ForgeSplicingEngine`: a notable row (`RolledTier = currentTier`, `FinalTier = currentTier + 1`, `EquipmentInstanceId = targetItem.Id`) in the fusion transaction, beside `ForgeFusionsCompleted++`.
- Craft / trophy / world boss / chronicle: a count through the same helper, and a notable row when the tier is Legendary+ (the trophy always qualifies).

**Retention:** `loot_tier_daily_counts` is bounded by (days × sources × regions × tiers actually hit). That is roughly tens of rows per player-day, fine to keep for a season. `notable_item_events` grows ~1% of drops. **Recommendation:** no pruning loop now, and a note in the backlog to revisit at 1M rows. If the owner wants pruning, add a `StartCron` loop that deletes rows older than 90 days in batches of 5,000 per cycle (try/catch enclosing `CreateScope`, added to `CronWorkerGuardTests`' inventory, depth logged in the heartbeat).

- [ ] **Step 1:** Models, DbSets and migration. Read the generated migration: it must be additive only (two `CreateTable`, no `AlterColumn` on existing tables).
- [ ] **Step 2:** Apply it locally by hand: `$env:FOLKIDLE_DB_CONN='Host=localhost;Database=folkidle_dev;Username=postgres;Password=postgres'; dotnet run --project server/FolkIdle.Server/FolkIdle.Server.csproj --migrate`.
- [ ] **Step 3:** `DropRecord` helper plus the loot-engine writer. Test (Testcontainers, alongside the existing loot worker tests, remembering `1ef5318`'s "stop every loot worker a test starts"):
  - an offline request with `Kills = 5000` writes counts summing to the pieces rolled, with ≤ 14×regions rows and no per-kill statements;
  - a forced transaction failure leaves **no** counts, and the outbox replay then records `OutboxRetry`;
  - auto-salvaged drops land in `SalvagedCount` and write no notable row;
  - a fleece or elevation lift shows `FinalTier > RolledTier`.
- [ ] **Step 4:** Forge, craft, trophy, world boss, chronicle, outbox writers, each with a one-assert test that the source appears. **Grep for a writer as well as a reader:** after this step, `rg "EquipmentInstances.Add|new EquipmentInstance" server/FolkIdle.Server` must show every creation site either calling `DropRecord` or listed in a comment in `DropRecord.cs` as deliberately excluded (market, mail, dev fixture, starter grant).
- [ ] **Step 5: Commit** (can be split per writer group): `feat(loot): a drop record - daily tier counts for every source, a row for every Legendary+`

### Task 4b (optional, ask the owner): admin view

- `GET /api/v1/admin/loot-stats?username=<name>&days=14`, beside the other admin routes in `Network/NetworkBroadcastSystem.cs` (~line 9534) and gated by the same admin check. It returns per-day, per-tier counts from `loot_tier_daily_counts`, the notable events, **and the player's current `LootLuckBreakdown` with the analytic expected shares** (from the Task 1 function, not a copy), so "observed vs expected" is one screen.
- REST JSON, not a packet, so there is no `generate:protocol`. If a client admin panel is added, add it to `client_web/scripts/screens.mjs` only if it is a new destination.
- [ ] Commit: `feat(admin): loot stats - what dropped, against what the odds say`

### Task 5: Verification

Follow the `verify` skill in order.
- [ ] `dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj` (server stopped).
- [ ] Docker up, then the full `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj`. Must be green, including `RarityRollDistributionTests`, `ItemRarityPowerTests`, `PowerCeilingTests`, `CronWorkerGuardTests`, `StateUpdatePacketFieldCoverageTests`, `MonsterLadderTests` and `ProgressionRateTests`. Report the count you actually saw; do not quote a number from a document.
- [ ] `.\run-dev.ps1`, then `cd client_web; npm run exercise`. The loot path changed, so this is the real proof: kills still produce loot and the chest still grows. Re-seed with `--seed-dev` first if villager/breeding steps report a spent pool. Close any browser signed in as the dev fixture first, or the Fight button check fails spuriously.
- [ ] Locally: `SELECT * FROM loot_tier_daily_counts ORDER BY "Day" DESC LIMIT 20;` shows rows after a few minutes of fixture combat, and a forge fusion in the UI creates a `notable_item_events` row with `"Source" = 4`.
- [ ] If Task 4b shipped, `npm run check:ratchet` and `npm run smoke:screens`.

### Task 6: Deploy and docs

- [ ] Deploy with the `deploy` skill (SSH push; `git pull` does not work on the box). The migration applies on the container entrypoint.
- [ ] Production check, SELECT only, the next day: `SELECT "Source","QualityTier",sum("Count") FROM loot_tier_daily_counts WHERE "PlayerId"=8 GROUP BY 1,2 ORDER BY 1,2;`. Legendary+ share should sit ~1.2% of the total (or whatever Task 3 decided), and drops/day is now measurable, which closes **H6**.
- [ ] `docs/TASK_BOARD.md` task 26: an "Investigated 2026-09-23" block (the verdict table, condensed), the owner's decision, and "DONE" once shipped.
- [ ] `docs/architecture/CURRENT_IMPLEMENTATION_STATE.md` §3: both new snake_case tables. `docs/architecture/NEXT_STEPS_BACKLOG.md`: a standing-trap line: "luck multiplies tiers 2-14 equally; the Ancient+ : Legendary+ ratio is luck-invariant (~4%) and is the one test a survivor-biased chest allows."
- [ ] Commit: `docs: task 26 investigated - the odds did not change; drop record live`

---

## Risks

- **The hand computation is wrong** (e.g. `characters[0]` is the aptitude-6 character, or a term is hydrated differently from what was read). Task 1 exists to catch this. The range 60.8-63.8 does not change any verdict, because the Ancient+ rate barely moves across it.
- **The owner reads "the odds are fine" as dismissive.** The data says the drought is ordinary, but ~10 days between Ancient+ may still *feel* bad. That is a legitimate design complaint, and options D/E/F answer it. Present it that way.
- **Changing the drop shape re-opens task 9's balance.** Any option other than A/B'/C must carry the `ItemRarityPowerTests` before/after tables, and a monster-HP change must be separate.
- **Upsert contention.** The loot worker is single-threaded, so one upsert per request under SERIALIZABLE should not conflict. If the forge or crafting writer shares a row key (same player/day/tier/source), serialization failures are possible. Use distinct `Source` values (already the case) and keep forge on notable rows plus its own count row key.
- **Hot-path cost.** One extra statement per loot request. The loot engine once starved on an unbounded drain, so watch the heartbeat's queue depths after deploy. If depth grows, coalesce counts per player across the cycle's budget instead of per request.
- **Under-counting through the retry outbox** if Task 4 Step 3's replay write is skipped. The test there is the guard.
- **Migration on production is additive only.** Confirm by reading the generated file. A non-additive migration here would be a mistake, not a requirement.
- **Legacy monsters in `monsters.json`** are why H5 exists. Fixing it by filtering to canonical ids is safe. Deleting legacy monsters is not (item ids are positional, and legacy codex rows exist), so do not.
