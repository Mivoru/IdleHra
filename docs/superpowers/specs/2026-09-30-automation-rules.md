# Orders: automation rules as a reward (task 85)

Owner decision, 2026-09-30 (binding): up to three rules per character,
unlocked at level 20, 40 and 60, one slot each. The rules act on the server,
on the event they answer, and **the offline catch-up applies the same rules**
("three paths grow a level" is the lesson: every path that meets the event has
to be told).

## The three rules

| Type | Name | Event it answers | What it does | Parameter |
|---|---|---|---|---|
| 1 | **Fish when the larder runs dry** | Auto-eat wants a bite and all three larder slots are empty (the moment `ActivityHaltReason.OutOfFood` is raised) | The character stops fighting and fishes the chosen spot | A fishing node (`3001`-`3005`) |
| 2 | **Step down after a death** | A death no Death Ward caught (`SimulationEngine.ApplyCombatDeath`) | The character respawns on the next monster **down the ladder** instead of going idle | none |
| 3 | **Fuse stacks up to tier N** | Equipment drops landing in the chest (`CombatLootEngine`) | Every stack a drop landed in is fused, from tier 1 up to N, by the stack fusion's own rules | N, 2-14 |

A rule that cannot act falls back to exactly what happened before it existed:
the character keeps fighting without healing (rule 1) or goes idle with the
death card (rule 2). Rule 1 cannot act when the spot is not reached yet
(`HighestLocationReached`) or another of the player's characters already
works it; rule 2 cannot act at the bottom of the ladder or when the easier
monster is taken by another slot. The occupancy check is the deploy command's
own rule (`CharacterSlotEngine.IsActivityOccupiedByAnotherSlot`) read off the
parked registers, so it needs no database on the tick.

**"One monster easier"** is the canonical ladder (ids 91-115,
`MonsterLadderTests`): the previous regular. A regional boss steps to its own
region's strongest regular; the first regular of a region steps to the last
regular of the region before, never to a boss. Monster 91 has nothing below it.
Legacy ids (1-90) are not on the ladder and do not step.

**Rule 3 fuses only the stacks a drop just landed in.** A request whose kills
dropped kept pieces (not auto-salvaged) names their base items; after the loot
worker's cycle each of those stacks is fused from tier 1 up to N with
`ForgeSplicingEngine.FuseStackInTransactionAsync`, the same planner
(`PlanStack`), fee, Forge-level ceiling and eligibility filter (not locked,
not worn) the Forge's "fuse stack" button uses. There is no second fusion
path. It spends gold from `CommodityRecords["gold"]` as the button does, so it
stops where gold stops; the live session's balance follows through
`ChestSaleGoldQueue` (display only - the row is already debited, see the gold
rule in `server/CLAUDE.md`).

## Slots and levels

- Slot 1 opens at level 20, slot 2 at 40, slot 3 at 60 (`AutomationRules.UnlockLevels`).
- The level is the account level (`PlayerRecords.CurrentLevel`); there is no
  per-character level in this game (`CharacterRecord` says why).
- A slot above the current level is **kept but inert**: a rebirth drops the
  level to 1 and the rules come back as the level does. The gate is read when
  the rule would act, not only when it is set.
- A rule type may appear once per character.

## Storage and the wire

- `characters."AutomationRules"` (bigint, migration `AddAutomationRules`):
  three 16-bit slots, each `type` in the low 4 bits and the parameter in the
  high 12 (a fishing node is stored as its offset in the fishing band).
- It rides the tick as `TickStatePayload.AutomationRules` for the active
  register and `CharacterActivityState.AutomationRules` for the parked slots,
  and is swapped with the rest of the character (`SwapRegisterWith`), so
  slots 2 and 3 obey their own rules, live and offline.
- **No packet field and no opcode.** It is a setting, so it goes the way the
  auto-salvage floor goes: `GET/POST /api/v1/automation-rules` writes the row,
  then `AutomationRulesQueue` hands it to the tick (as `ChestSettingsQueue`
  does). A reload re-reads the row.
- Two new halt reasons tell the player a rule acted: `6 AutomationFishing`
  and `7 AutomationSteppedDown`. They are notes, not stops: the tick does not
  clear them while the activity runs (as with `OutOfFood`), a redeploy does,
  and the client does not buzz or dot the tab for them.

## Offline parity

Every path that meets the two events calls the same function:

| Event | Live | Offline |
|---|---|---|
| Death | `RunCombatTick` -> `ApplyCombatDeath` -> `AutomationRules.TryStepDownAfterDeath` | `ProjectCombat` -> `ApplyCombatDeath` (same) |
| Larder dry | auto-eat raises `OutOfFood`; the next `RunCombatTick` calls `AutomationRules.TryGoFishing` | `HuntingProjection.Advance` stops at the starving tick (`StopWhenStarved`), `ProjectCombat` raises `OutOfFood` and calls `TryGoFishing` |
| Drops | `CombatLootDropRequest.Build` carries `AutoFuseToTier` | the offline requests are built by the same `Build` and rolled by the same worker |

The offline slot loop is now a sequence of **legs**: fight until the window
ends, the character dies, or the larder runs dry. If a rule moved the
character, the rest of the window is spent on the new activity - another
fight (a death stepped down) or the fishing projection (the larder ran dry).
`AutomationRuleParityTests` runs the real `RunCombatTick` beside the offline
projection for a rule-triggering scenario and holds kills, the activity the
character ends on and the halt reason to the live result.

## Not done, on purpose

- The larder being restocked does not send a fisher back to the fight; the
  player redeploys. A rule answers one event, it does not plan.
- (Fixed 2026-09-30.) Slot 2 and 3's activity change used to be live-only,
  as a death's already was: the checkpoint wrote only slot 1's activity. It
  now writes all three (`PersistFieldedActivitiesAsync`).
