# Workshop commissions (task 83) - the material sink

Owner decision, 2026-09-30 (binding): a commission makes a region piece with a
rarity FLOOR set by the Crafting Workshop's level (T2-T6), always below the
region's usual drop, plus ONE affix the player chooses at Common affix rarity.
It takes 1-8 h of real time and costs tens of thousands of materials. Prices
come from `GatheringEconomyTests`, the floor is checked in `PowerCeilingTests`,
and time to region 5 (~26 days) must not fall below ~20.

## What is true before this

- The Workshop level (`VillageInfrastructures` building 10, levels 0-5) is on the
  wire (`CraftingWorkshopLevel`) and read by nothing that changes the game.
  `CraftingEngine.RollCraftedRarity` takes a workshop level and has no caller.
- `PlayerCraftingSlots` exists since the initial baseline with no writer and no
  reader. Its `CompletionEpoch` is the column the task names.
- Equipment is monster loot; the recipe bench makes tools only, always Normal.

## The rules (`Domain/Economy/WorkshopCommissionRules.cs`)

**Pieces.** Every droppable combat piece authored in regions 1-5 (the 75
canonical pieces, 15 per region). Never a tool. Only pieces of a region the
player has opened (`RegionUnlockGate.CanWearRegionTier`, the same gate as
equipping).

**Floor.** Workshop level 1-5 -> Common, Uncommon, Rare, Ultra Rare, Epic
(T2-T6). Level 0 takes no commissions. Capped per region at
`BossFirstClearRules.RequiredQualityTierFor(region) - 2`.

*Why that cap is "the region's usual drop".* The median SINGLE drop is Normal in
every region (51% of rolls), so every floor would already be above it; that
cannot be the anchor the owner meant. A player wears the best of many drops, and
the game states that best per region: the quality all eight slots of the
region's own gear must reach to beat its boss (4, 8, 8, 10, 11), calibrated by
`BossWallTests`. That test also asserts "two tiers below the requirement loses",
so a floor two below means a wardrobe made entirely of commissions can never
open a region the player could not otherwise open. Only region 1 is capped by
it (4 - 2 = Common); regions 2-5 take the whole Epic.

| Region | Usual (wall) | Best floor |
|---|---|---|
| 1 | Rare (4) | Common (2) |
| 2 | Mythic (8) | Epic (6) |
| 3 | Mythic (8) | Epic (6) |
| 4 | Ancient (10) | Epic (6) |
| 5 | Divine (11) | Epic (6) |

**Result rarity.** A floor, not a fixed tier: `max(floor, RarityTier.RollTier(0))`
- the drop table's own roll at zero luck. The median result is the floor; about
one Epic order in a hundred comes out Legendary or better. No second rarity
table (`RollCraftedRarity` stays unused).

**Affixes.** The item carries `RarityTier.GetAffixCount(tier)` affixes, as every
item does. One is the chosen affix at Common (`"<id>@1"`, magnitude rolled in the
Common band for the piece's region); the rest roll via
`AffixRegistry.TryRollOneAdditional` (legal for the slot, unused stat preferred,
canonical key). Every key is canonical, so every affix is rerollable. The chosen
affix must be legal for the slot (`GetLegalAffixIndices`): weapon-only damage on
a weapon, block on a ring, no tool affixes. It travels as its string id in a JSON
body, never as an index (the auto-reroll index drift).

**Duration.** By floor: Common 1 h, Uncommon 2 h, Rare 4 h, Ultra Rare 6 h,
Epic 8 h. One commission at a time (row `SlotIndex` 0).

## Price derivation

Each commission costs the piece's OWN region's materials - the ore canon pair
and the region's log (`VillageManagementEngine.GetTierMaterials`), all
catalogued items - four lines:

    common log  = wood/h  x hours x 0.9   (rounded up to 500)
    common ore  = ore/h   x hours x 0.9   (rounded up to 500)
    golden log  = wood/h  x hours x 0.1   (rounded up to 50)
    rare ore    = ore/h   x hours x 0.1   (rounded up to 50)

`wood/h` and `ore/h` are what the region's REFERENCE GATHERER harvests an hour,
computed by `GatheringToolEngine.ComputeRequiredTicks` against the node's
authored `BaseTickThreshold` times the codex yield - the function the live tick
and `GatheringEconomyTests` both use. Regions 1, 3 and 5 are that test's own
profiles ("region 1, first axe", "region 3, keeping up", "region 5, geared",
asserted equal); 2 and 4 sit between. The 90/10 split is the harvest's
common/rare share. So a commission eats exactly what one woodcutter and one
miner in its region gather while it is being made.

Measured (`GatheringEconomyTests.Test_WorkshopCommission_IsPricedFromThisSupply`):

| Region | Floor | Hours | Units/h per node | Price (4 lines) | Hours of a MAXED gatherer per common line |
|---|---|---|---|---|---|
| 1 | Common | 1 | 2,908 | 6,600 | 0.14 |
| 2 | Epic | 8 | 4,500 | 72,200 | 1.50 |
| 3 | Epic | 8 | 6,750 | 108,800 | 2.27 |
| 4 | Epic | 8 | 7,875 | 126,600 | 2.64 |
| 5 | Epic | 8 | 9,000 | 144,400 | 3.01 |

Region 4 Epic is 57,000 frostpine + 57,000 silver + 6,300 of each rare. The
owner's account holds 1.8 M frostpine and 1.7 M silver, so the Workshop can eat
that stockpile at up to three orders a day - a real sink - while a region-2
player pays about sixteen hours of one gatherer's work for an Epic.

## Why time to region 5 cannot move

Regions open only by beating their boss, and a full set of commissioned pieces
at the best floor - with Common affixes on everything, the pessimistic reading -
LOSES every region's first clear (`PowerCeilingTests
.TheWorkshopCommissionFloorStaysBelowEveryRegionsUsualDrop`, measured with
`BossGearBenchmark`). A commission can fill a slot sooner; it cannot open a wall
sooner, so the ~26-day figure is untouched.

## Lifecycle and persistence

- `PlayerCraftingSlots` row per player (`SlotIndex` 0): `ActiveRecipeId` = the
  item-definition id, `CompletionEpoch`, and three new columns
  (`ChosenAffixId`, `FloorTier`, `StartedEpoch`) - migration
  `AddWorkshopCommissions`, additive. `IsReady` is left unused.
- **Place** (`POST /api/v1/workshop/commission {ItemId, AffixId}`): validate the
  piece, the affix, the Workshop level (a matured upgrade counts), the region
  gate, quarantine and "one at a time"; charge all four lines through
  `InventoryAndStashSystem.TryConsumeUnifiedAsync` (backpack then stash, recorded
  as Spent in the material ledger); write the row. One transaction.
- **Collect** (`POST /api/v1/workshop/collect`): refuses `NotReady` before
  `CompletionEpoch`; otherwise rolls the tier and affixes, inserts the
  `EquipmentInstance`, counts it in the drop record as a Craft, deletes the row.
  One transaction, under the row's `FOR UPDATE`, so a double tap collects once.
- **Offline.** Readiness is wall-clock `CompletionEpoch <= now`, asked at read
  time. No worker, no tick and no offline catch-up is involved, so a commission
  placed before logging out is simply ready at the next login.
- **Rebirth / season wipe** deletes the row (`SeasonalRotationEngine
  .ResetPlayersAsync`), or it would be the one piece of gear to survive the wipe.
  Account purge deletes it too.
- **No cancel.** A refund would have to un-record the material ledger's Spent
  line; the owner did not ask for it and an order ends within 8 h anyway.
- REST, not opcodes: nothing here is read by the tick, no gold moves, and
  `StateUpdatePacket` is untouched. Every refusal answers 200 with the view and a
  `Result` name, never a silent rollback.
- Dev: `POST /api/v1/dev/workshop/finish {Refund}` (404 without
  `FOLKIDLE_DEV_TOOLS=1`) makes the order ready now and optionally gives the price
  back, so `exercise.mjs` round-trips the fixture.

## Client

`client_web/src/lib/ui/WorkshopCommissions.svelte`, at the top of the Crafting
screen: region chips (opened regions), the floor, duration and every price line
with what the player holds, piece chips, affix chips, Commission; while an order
runs, its progress bar, a countdown on the server's clock, and Collect. Buttons,
never a `<select>` (Android WebView), and nothing that ticks sits inside a
control.
