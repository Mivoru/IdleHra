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
| Combat materials | Kill loop: 35% gate, one weighted pick, quantity uniform in `[Min, Max]`, Plenty rounds up, keyed by `GetItemBaseId`. Reads no codex yield, no global drop multiplier, no luck | `kills × codex yield × GlobalDropMultiplier/100` rolls, one unit each, luck-weighted, keyed by `GetMaterialString`. That function names only six legacy slugs, so **every monster material was "unknown" and discarded** | **no** | `CombatLootEngine.RollMaterialsForKills`, the live loop's own gate and pick (`TryPickMaterialDrop`), per kill, at login |
| Diamonds from ordinary kills (task 64) | `OrdinaryKillDiamondChance` (0.01%) per non-boss kill; bosses pay none | none | **no** | `Binomial(kills, 0.0001)`, bosses none (`KillCanPayDiamond`) |
| Codex: kills, levels, region completion, race unlock, first-clear trophy, race-mastery XP | One `KillEvent` per kill to `CodexEngine` | none. A kill made while away never reached the codex | **no** | one `KillEvent` carrying the window (`KillEvent.Kills`, zero means one), `GainedXp` = the live per-kill figure × kills |
| Gathering roll count | Per harvest: `(GlobalDropMultiplier + monolith + Kobold/Moosleute + Golden Harvest + yield trait) × codex yield` roll-percent | `actions × codex yield × yield trait`: no monolith, no race bonus, no event, no global multiplier | **no** | `SimulationEngine.GatheringYieldFor`, the one composition both call |
| Gathering pick and quantity | Weight + flat luck (`LootLuckPct/10`), quantity uniform in `[Min, Max]` when `Max > Min`, keyed by `GetItemBaseId` | Same weights, one unit per roll, keyed by `GetMaterialString`, so **every real gathering item was "unknown" and discarded**. A night of gathering paid mastery XP and no materials | **no** | a multinomial over entries (`DrawLootCountsByEntry`), `SimulationEngine.RollGatherQuantity` per winner, `GetItemBaseId` |
| Gathering speed | `RequiredGatherTicks`, re-read every harvest, so a mastery level gained mid-window speeds the rest of it | A line-for-line copy of the argument list, evaluated once at the logout mastery for the whole window | **no** | the shared `RequiredGatherTicks`, walked one mastery level at a time |
| Gathering mastery XP | `BaseMasteryXpReward` per harvest | same | yes | unchanged (now applied step by step, same total) |
| Gathering storage | Unbounded chest; material ledger `Gathered` | Unbounded; ledger `Gathered` | yes | unchanged |
| Crafting output | The job: one completion per `CraftTicksFor(recipe)`, each through `CraftingEngine.ExecuteCraftingAsync` (Craft bough free roll, material check, Kobold unit, MasterArtisan unit). A refused craft does not stop the job | **None.** A crafting activity (5000+) fell through to the combat projection and **fought monster 1 for the whole window** (the PR #7 defect, alive offline): gold, XP and loot for a fight that never happened | **no** | `CraftingEngine.ExecuteOfflineCraftsAsync`, the same per-craft rules (`CraftIsFree`, `KoboldExtraUnits`, `MasterArtisanExtraUnits`) over the window's completions. It runs after every other slot, because live the crafter spends what its village gathers meanwhile |
| Village: Town Hall gold | `GetTownHallGoldRatePerHour`, 1/36000 of it per tick | the same rate × elapsed | yes | unchanged; checked to the coin |
| Village: Lumberjack / Mine materials | `ProcessPassiveVillageTick`: `0.1/s × level` of **`"wood"`** and `0.05/s × level` of **`"iron_ore"`**, accruing only while that row is below the Warehouse cap | `(level + 1) × 100/h` of the region's **catalogued** log and ore, 10% of it the rare pair (`GetTierMaterials`, `RareYieldPercent`), clamped to the Warehouse cap, overflow on the task-79 ledger | **no** | **not changed. This is a design question** (below) |
| Offline-only: Scholar crown | none | every projection runs on `elapsed × (1 + Scholar%)`, village production included | **no** | **not changed. Design question** (below) |

Streams a kill or a harvest also produces that are **outside this audit's list**
and still differ. They were reported and left alone, because they are
progression or combat rather than loot:

- **Combat XP.** Live multiplies `BaseXpReward` by `LiveKillXpMultiplierPct`
  (the global XP multiplier, mentors, Human mastery, the legacy perk,
  inheritance, the XP bough, the guild buff) plus `ProcessMonsterDeath`'s event
  and race terms. Offline applies only the inheritance bonus.
- **Seasonal / chronicle-pass XP** (`AddSeasonalXp`, per kill and per harvest).
  Offline grants none.
- **Quest progress** (kill quests, craft quests) and **guild-war points**.
  Offline grants none.
- **First-clear and region bookkeeping** (`DefeatedRegionBossMask`, the next
  region's door, boss challenges, personal records). The codex events above now
  let hydration reconcile the door from the codex. A parallel change moves the
  rest into a shared `ApplyKillProgression`.

## Open design questions (not guessed)

1. **Which village production is the game?** Live and offline disagree on the
   material and on the rate. At Lumberjack 5 the live tick pays 1,800 `"wood"`
   an hour and offline pays 600 of the tier log. At Mine 1 it is 180 `"iron_ore"`
   against 200 of copper and malachite. `"wood"` is a gathering slug that
   nothing spends. The offline side is the one the 2026-09-01 "the ores are the
   ones players actually earn" change moved to the catalogued namespace, and
   the live tick was never moved with it. The rule says offline models live,
   but copying the live tick here would move the economy back to a currency the
   village cannot spend. Decide which side is right, then make the other call
   it.
2. **Is Scholar allowed to break parity?** The crown is sold as "everything
   earned while away comes in a quarter faster", so it is offline-only by
   design. It conflicts with the letter of the owner rule.
3. **Should a live kill's materials read the codex yield and the global drop
   multiplier?** No live path does. The tick still has a roll loop that reads
   both, but it only spends `InventorySpaceRemaining`, which the census resets.
   It grants nothing, and the offline copy's comment took it for the real
   thing. Offline now matches live (neither reads them). If the answer is
   "yes", change `CombatLootEngine` and both paths follow.
4. **The diamond calibration.** "About 60 a week at one kill a second" was
   written when offline paid no diamonds. With offline paying the same rate, a
   character that fights around the clock earns the per-second rate for every
   hour of the week, capped by the 12-hour offline window.

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
