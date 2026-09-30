# Offline parity: loot, drops, gathering, crafting, production

**Owner rule, 2026-09-30:** offline and online give the *same results per hour*,
drops and loot included. `OfflineSimulationEngine` is an expected-value model
of the live paths, and every difference between them is a bug. The owner
accepted that this changes offline rewards.

This page is the audit of every reward stream a character produces, the live
producer beside the offline one, and what was changed. It covers loot, drops,
gathering, crafting and village production. Combat math (kill time, damage,
healing, food, survival) is a separate audit: here the offline **kill count is
taken as given**, and only what each kill pays is checked.

`OfflineLootParityTests` is the guard. Each test runs the real live path and
the offline model for the same activity, then asserts each stream's rate within
a band derived from its sampling error. The math is stated beside each
assertion.

## The audit

| Stream | Live rule (producer) | Offline rule, before | Same? | Now |
|---|---|---|---|---|
| Equipment drops: rate, rarity, Golden Fleece | Per kill, `CombatLootDropRequest.Build` then the `CombatLootEngine` kill loop: 15% (`EquipmentDropChance`), tier by `LootLuckPct` / `RarityElevationPct`, +2 tiers on each 100th kill with the crown | One request carrying the window's kills, from the same `Build`, split plain vs fleece kills, rolled by the same kill loop | yes | unchanged; the test checks the request odds match field by field, pieces per kill, and mean tier |
| Boss guarantee | Kill loop: +1 piece per regional-boss kill | Same loop (`DropSource.OfflineBossGuarantee`) | yes | unchanged; checked (1.15 pieces a kill) |
| Cosmetic chests (task 54) | Kill loop: `CosmeticRegistry.RollMonsterChest` once per kill | Same loop | yes | unchanged (too rare to measure a rate; same code) |
| Combat materials | Kill loop: 35% gate, one weighted pick, quantity uniform in `[Min, Max]`, Plenty rounds up, keyed by `GetItemBaseId`. Reads no codex yield, no global drop multiplier, no luck (confirmed as the rule, decision 3) | `kills × codex yield × GlobalDropMultiplier/100` rolls, one unit each, luck-weighted, keyed by `GetMaterialString`. That function names only six legacy slugs, so **every monster material was "unknown" and discarded** | **no** | `CombatLootEngine.RollMaterialsForKills`, the live loop's own gate and pick (`TryPickMaterialDrop`), per kill, at login |
| Diamonds from ordinary kills (task 64) | `OrdinaryKillDiamondChance` (0.01%) per non-boss kill; bosses pay none | none | **no** | `Binomial(kills, 0.0001)`, bosses none (`KillCanPayDiamond`); no weekly cap (decision 4) |
| Codex: kills, levels, region completion, race unlock, first-clear trophy, race-mastery XP | One `KillEvent` per kill to `CodexEngine` | none. A kill made while away never reached the codex | **no** | one `KillEvent` carrying the window (`KillEvent.Kills`, zero means one), `GainedXp` = the live per-kill figure × kills |
| Gathering roll count | Per harvest: `(GlobalDropMultiplier + monolith + Kobold/Moosleute + Golden Harvest + yield trait) × codex yield` roll-percent | `actions × codex yield × yield trait`: no monolith, no race bonus, no event, no global multiplier | **no** | `SimulationEngine.GatheringYieldFor`, the one composition both call |
| Gathering pick and quantity | Weight + flat luck (`LootLuckPct/10`), quantity uniform in `[Min, Max]` when `Max > Min`, keyed by `GetItemBaseId` | Same weights, one unit per roll, keyed by `GetMaterialString`, so **every real gathering item was "unknown" and discarded**. A night of gathering paid mastery XP and no materials | **no** | a multinomial over entries (`DrawLootCountsByEntry`), `SimulationEngine.RollGatherQuantity` per winner, `GetItemBaseId` |
| Gathering speed | `RequiredGatherTicks`, re-read every harvest, so a mastery level gained mid-window speeds the rest of it | A line-for-line copy of the argument list, evaluated once at the logout mastery for the whole window | **no** | the shared `RequiredGatherTicks`, walked one mastery level at a time |
| Gathering mastery XP | `BaseMasteryXpReward` per harvest | same | yes | unchanged (now applied step by step, same total) |
| Gathering storage | Unbounded chest; material ledger `Gathered` | Unbounded; ledger `Gathered` | yes | unchanged |
| Crafting output | The job: one completion per `CraftTicksFor(recipe)`, each through `CraftingEngine.ExecuteCraftingAsync` (Craft bough free roll, material check, Kobold unit, MasterArtisan unit). A refused craft does not stop the job | **None.** A crafting activity (5000+) fell through to the combat projection and **fought monster 1 for the whole window** (the PR #7 defect, alive offline): gold, XP and loot for a fight that never happened | **no** | `CraftingEngine.ExecuteOfflineCraftsAsync`, the same per-craft rules (`CraftIsFree`, `KoboldExtraUnits`, `MasterArtisanExtraUnits`) over the window's completions. It runs after every other slot, because live the crafter spends what its village gathers meanwhile |
| Village: Town Hall gold | `GetTownHallGoldRatePerHour`, 1/36000 of it per tick | the same rate × elapsed | yes | both call `VillageManagementEngine.AccrueProduction`; checked to the coin |
| Village: Lumberjack / Mine materials | `ProcessPassiveVillageTick`: `0.1/s × level` of **`"wood"`** and `0.05/s × level` of **`"iron_ore"`**, accruing only while that row is below the Warehouse cap | `(level + 1) × 100/h` of the region's **catalogued** log and ore, 10% rare, clamped to one Warehouse for the whole window, then capped per material, overflow on the task-79 ledger | **no** | **The offline rule is the game (decision 1), and the live tick now calls it.** `ProductionRatePerHour`, `AccrueProduction`, `RareShareOf` and `GrantProductionAsync` are shared. The live tick batches a minute at a time onto `SimulationEngine.VillageProductionQueue`, plus the remainder at session end. `VillageTickCoordinator.DrainProductionGrants` writes each batch under the Warehouse cap with the ledger. The offline one-Warehouse window ceiling is gone: it was a second cap live never had. Checked for equality, per material, with and without Scholar and with a Warehouse that caps |
| Scholar crown | none | every projection ran on `elapsed × (1 + Scholar%)`, village included, and ate that much more food | **no** | **A reward rate on both paths (decision 2)** (`ScholarRate`): each kill, harvest and craft completion pays for one more with a 25% chance (offline draws `Binomial(n, 0.25)` for its window), and village production and Town Hall gold accrue at 1250‰. Time, food and aging use honest seconds. It is capped (a crown has one level) and listed in `PowerCeilingTests`' yield ledger |

Streams a kill or a harvest also produces that are **outside this audit's list**
and still differ. They were reported and left alone, because they are
progression or combat rather than loot:

- **Combat XP.** Live multiplies `BaseXpReward` by `LiveKillXpMultiplierPct`
  (the global XP multiplier, mentors, Human mastery, the legacy perk,
  inheritance, the XP bough, the guild buff) plus `ProcessMonsterDeath`'s event
  and race terms. Offline applies only the inheritance bonus.
- **Seasonal / chronicle-pass XP** (`AddSeasonalXp`, per kill and per harvest).
  Offline grants none. Live pays it once per fight or harvest, not per
  Scholar-paid one.
- **Quest progress** (kill quests, craft quests) and **guild-war points**.
  Offline grants none.
- **First-clear and region bookkeeping** (`DefeatedRegionBossMask`, the next
  region's door, boss challenges, personal records). The codex events above now
  let hydration reconcile the door from the codex. A parallel change moves the
  rest into a shared `ApplyKillProgression`.

## Owner decisions, 2026-09-30 (implemented)

1. **Village production follows the offline rule**, and the live tick calls
   it (row above). Nothing produces `"wood"` any more. `"iron_ore"` is
   still produced, but only because it is also region 2's catalogued common
   ore: the legacy constant `VillageManagementEngine.IronOreCommodityId` and
   the real item share one key. Stock was not deleted. What still **reads**
   the legacy rows:
   - `StateCheckpointManager` hydrates `CachedWoodStock` / `CachedStoneStock`
     / `CachedIronOreStock` from `"wood"` / `"stone"` / `"iron_ore"`, and the
     tick copies them onto the wire (`StateUpdatePacket`). The web client
     reads none of the three, so they now show a frozen stock.
   - `DeedProgressSource` reads `"wood"` for a deed's progress (`WoodStock`).
     That deed can no longer advance from production.
   - `RedisWriteBehindEngine` still drains the `wood` / `stone` / `iron_ore`
     Redis buffers into those rows, so whatever a buffer held before the
     deploy lands. `TickStatePayload.PendingWoodDelta` / `PendingIronDelta`
     now have no writer, and a comment on them says so.
   - `DevFixtureSeeder` seeds 50,000 of each. `LootTableEngine` names
     `"wood"` in a legacy table.
2. **Scholar applies online too**, as a rate on the same streams, never by
   speeding the tick (row above). It is bounded at 1.25x.
3. **A live kill's materials read neither the codex yield nor the drop
   multiplier.** The dead roll loop in the kill block, which read both and
   only decremented `InventorySpaceRemaining`, was deleted. A comment in
   its place says why.
4. **Ordinary-kill diamonds pay the same rate offline, with no weekly cap.**
   The calibration comment next to `OrdinaryKillDiamondChance` states the
   per-kill rate and no longer quotes "~60 a week".

**Boss first-clear bookkeeping offline** (the boss mask, the next region's
door) was not changed here. The parallel combat branch is moving it into a
shared `ApplyKillProgression` that the offline projection calls. That
branch had not been pushed to `origin` when this was checked, so this change
does not duplicate it. Until it lands, the door opens at the next login from
the codex events.

## Automation rules (task 85)

The three "Orders" act on both paths through the same functions
(`Domain.Combat.AutomationRules`): a death steps down inside
`ApplyCombatDeath`, a dry larder sends the character fishing through
`TryGoFishing` (live at the next combat tick, offline at the starving tick,
where `HuntingProjection.FightState.StopWhenStarved` ends the fight), and the
fusion tier rides `CombatLootDropRequest.Build`. Offline, a combat slot's
window is a sequence of legs (`OfflineSimulationEngine.ProjectCombatLegs`).
`AutomationRuleParityTests` holds the legs against the live tick. See
`docs/superpowers/specs/2026-09-30-automation-rules.md`.

## Approximations that remain (documented, second-order)

- `GlobalDropMultiplier`, `GlobalXpMultiplier` and `ActiveGlobalEventId`
  (Golden Harvest, MasterArtisan) are read **at login**. Live reads them at
  each kill or harvest, so a window that straddles an event's start or end is
  paid entirely at the login-time value.
- Crafting runs after the other slots. That equals live whenever the materials
  are a total over the window. It can overpay only if the crafter would have
  been refused early, before gatherers restocked it. A refused live job keeps
  counting and crafts again once materials arrive, so the totals agree.
- Runaway guards: 200,000 kills per slot per window and 200,000 gathering
  actions or rolls. The 12-hour cap keeps any real window below both.
