# Great Works (task 84)

Owner decision, 2026-09-30: the long material sink is **solo monuments** (per
player, not per guild), five stages each, each stage eating 50,000 to 2,000,000
of one region's materials, each stage paying a **small permanent capped bonus
that survives a rebirth**, and each visibly changing the Home map. A rebirth
deletes every material stack, so this is where the stacks go that a player wants
to keep as something permanent.

## 1. What a Great Work is

Five monuments, one per region (`GreatWorksRegistry.Monuments`). Five stages
each, priced the same for every monument:

| Stage | Name | Cost (units) |
|---|---|---|
| 1 | Foundation | 50,000 |
| 2 | Pillars | 150,000 |
| 3 | Walls | 400,000 |
| 4 | Roof | 1,000,000 |
| 5 | Beacon | 2,000,000 |

3,600,000 units a monument, 18,000,000 for all five. For scale: the whole crafting
tree costs about 384,000 units (`GatheringEconomyTests`), so a single Beacon is
five crafting trees. That is the point of a long sink, and it is why the bonuses
below are small.

**What is spent.** The region's own **common log or common ore**, in any mix
toward the same stage. Both come from `VillageManagementEngine.GetTierMaterials`
(ORE CANON: one pair per region, also what the gathering tables and the guild
buffs use), never restated, so a monument cannot name a material no node in its
region pays. Both are catalogued in `items.json`; the rare variants are not
accepted. Backpack first, then the chest, through `TryConsumeUnifiedAsync` - the
same spend every building and recipe uses.

## 2. The bonuses

| Monument | Region | Per stage | At stage 5 |
|---|---|---|---|
| Birchwood Cairn | 1 | +1 % gathering yield | +5 % |
| Willow Hearth | 2 | +15 min offline limit | +75 min |
| Acacia Gate | 3 | +1 % gathering yield | +5 % |
| Frostpine Beacon | 4 | +15 min offline limit | +75 min |
| The Ebon Crown | 5 | +1 % yield and +15 min offline | +5 % and +75 min |

**Ceilings: +15 % gathering yield and +225 min (3 h 45 min) offline limit**, every
monument complete. Both are hard caps by construction (five stages of a fixed
figure) and both are asserted in `PowerCeilingTests`: the yield as a lever in the
yield ledger (1.15x, inside the same "no single lever exceeds the rest" rule),
the offline minutes in `TheGreatWorksOfflineBonusIsACapAndLeavesTheOfflineWindowUnderADay`
(a maxed Vodnik 25 player reaches 21.75 h, under a day and under 2x the base).

**Nothing stores a bonus.** The tick caches the *stages* (`GreatWorksStagesPacked`,
three bits a region) and derives the bonus on every read
(`GreatWorksRegistry.YieldPct` / `OfflineMinutes`). Retuning a per-stage figure
moves everyone at once, and the stored fact (a stage was built) cannot disagree
with what it pays.

**Offline = online (owner rule).** Every reader of each bonus:

- *Gathering yield*: `SimulationEngine.GatheringYieldFor` - the one composition
  the live tick and `OfflineSimulationEngine.CalculateGatheringProjection` both
  call. The bonus is added there as whole roll-percent points beside the trait
  and race terms. Nothing else in the server computes a gathering roll count
  (`BloodlineBonusesTests` already greps that).
- *Offline limit*: `OfflineSimulationEngine.EffectiveOfflineCapSeconds` = the 12 h
  base, Vodnik's 18 h extension, plus the minutes. It is called by the offline
  window itself, by the wire's `OfflineCapSeconds` (which the client shows), and
  by `OfflineCapNotifier`'s email (which loads the stages for its candidates), so
  the number shown, mailed and enforced cannot come apart. The monuments stack on
  top of Vodnik rather than competing with it.

**Writers.** Two things write `GreatWorksStagesPacked`: the login hydration
(`StateCheckpointManager`) and the tick's drain of a committed deposit
(`GreatWorksTickCoordinator.DrainUpdates`). `TheStagesAreWrittenByExactlyTheLoginAndTheDeposit`
fails if a third appears or either goes - a stored-but-never-read bonus is this
project's worst recurring defect, and so is its inverse.

## 3. Persistence and rebirth

`great_works_progress (PlayerId, Region, Stage, Progress, UpdatedAtUtc)`, keyed
`(PlayerId, Region)`, migration `AddGreatWorks`. `Stage` is stages built,
`Progress` what is in the next stage. Rebirth (`SeasonalRotationEngine`'s player
rollover) deletes only the tables it names, and this one is not on the list;
`ABuiltMonumentSurvivesARebirth` runs a real rebirth and checks the row.

## 4. The command

`DepositGreatWork = 82`. `TargetId` the monument (its region, 1-5), `SecondaryId`
0 (log) or 1 (ore), `DepositQuantity` how many, **0 meaning "all I hold, up to
what the stage still needs"**. The handler validates the choices, and the deposit
runs off the tick in `GreatWorksEngine.DepositAsync`: insert-if-absent, lock the
row `FOR UPDATE`, clamp to what the stage needs, spend, raise the progress, and
complete the stage in the same commit. The quantity is only ever clamped down, so
an oversized request is a smaller deposit rather than a refusal or a lost surplus
(stages do not carry over). Every outcome is a command result: 60 deposited, 61 a
stage built (both good news), 62 already complete, 3 not enough material, 44 the
save failed (nothing spent), 8 a nonsense choice. No packet field was added (no
struct change); the panel reads `GET /api/v1/great-works`, and any command result
invalidates every query, so it refetches by itself.

Dev tools only: `POST /api/v1/dev/great-works/restore` sets a monument to a
`(Stage, Progress)` and moves one material's stock by a signed `StockDelta`, so the
exercise step round-trips.

## 5. The client

- **Village** shows the panel (`GreatWorks.svelte`): each monument, its stage
  and per-stage bonus, a progress bar for the next stage, the five stage costs,
  the totals against the ceilings, and Deposit-log / Deposit-ore buttons that say
  in words why they are off.
- **Map** (`Hub.svelte`) draws a landmark for every monument with at least one
  stage built, growing a layer a stage (foundation, pillars, walls, roof, beacon).
  It is `MonumentGlyph.svelte`, an SVG on the theme tokens: there is no painted art
  for monument stages, and a CSS/SVG marker cannot 404. It is a landmark, not a
  control (`pointer-events: none`), so it can never bury a plate.
- The client keeps no copy of the monuments; only *where* each stands on the
  painting is a client fact.

## 6. Not built (open, on purpose)

- **A Hall of Ancestors slot and a frame** were named as example bonuses. A Hall
  slot needs the cap read in the cull and the purchase paths, and a frame needs a
  bound cosmetic and its art (task 87's `Bound` machinery would carry it). Both
  are small additions on top of this table if the owner wants them; neither was
  needed for the sink to work.
- The monument marker positions are estimated from the painting by eye; move them
  in `MONUMENT_SPOTS` if one sits on a landmark.
- The exercise step was written but not run here (one shared local stack).
