# Breeding round 2: heritable traits - design

Status: approved in conversation with the owner, 2026-09-13, section by section.
Round 1 (pickers, audit, Celtic names) shipped as b181837. Round 3 (translation
into EN/ES/FR/DE/PL/CS) comes after this and is out of scope here, except that
every player-facing string introduced below carries a stable key for it.

## 1. Why

A bloodline has two inheritance systems today and only one of them is a game.

- **Aptitudes** (Strength, Skill, Endurance, Fortune) are chosen, weighted,
  previewed and consumed. They stay exactly as they are.
- **Genes** (Speed, Crit, Yield) are invisible dominant/recessive bytes. They
  pay +0.05% attack speed or crit per point and +4% gathering yield per point,
  no screen names them, a village partner carries zeroes so the recommended
  strategy dilutes them, and their "mutation" XORs the low five bits - it can
  turn 31 into 0. Its rate also shrinks every generation.
- **The epic mutation** is +1 to four numbers and a star.
- **Inbreeding** costs a lifetime -25% attribute growth
  (`RaceAttributeGrowth`), which no screen, preview or document mentions - and
  round 1 made cousins count as related.

This round replaces genes and the hidden penalty with **named, visible,
heritable traits**.

## 2. Decisions (owner-approved)

| Question | Decision |
|---|---|
| Direction | Named traits, replacing the three genes. Aptitudes remain the main axis. |
| Negative traits | Yes. Inbreeding produces a visible **flaw** instead of the hidden -25% growth. |
| Where new traits come from | Newcomers (a better Inn rolls rarer ones) and mutation at birth (the Grounds raises its chance); the epic mutation guarantees a good one. |
| How many | At most **3** per character, no duplicates. |
| Strength | Noticeable, not decisive: Common +3-5%, Rare +6-8%, Legendary +10-12% or a distinct effect, flaws -5 to -8%. Positive total per stat capped at +20, negative floored at -15. |
| Architecture | Traits defined in code (`TraitRegistry`), one `TraitMask` column per character and per newcomer, definitions served over REST. |

## 3. The catalogue

Bit positions are **permanent**. A mask stores positions, so renumbering a trait
silently swaps every character's traits; a test pins them (see section 9).
Flaws start at bit 16 to leave room for positive traits.

| Bit | Key | Name | Rarity | Effect |
|---|---|---|---|---|
| 0 | `stout_heart` | Stout Heart | Common | +5% max HP |
| 1 | `keen_edge` | Keen Edge | Common | +4% attack damage |
| 2 | `quick_hands` | Quick Hands | Common | +5% gathering speed |
| 3 | `green_thumb` | Green Thumb | Common | +5% gathering yield |
| 4 | `iron_blood` | Iron Blood | Rare | +8% max HP |
| 5 | `hawk_eye` | Hawk Eye | Rare | +3 crit chance points |
| 6 | `swift_blood` | Swift Blood | Rare | +6% attack speed |
| 7 | `nimble` | Nimble | Rare | +4 dodge points |
| 8 | `blood_of_kings` | Blood of Kings | Legendary | +10% attack damage |
| 9 | `wolfs_hunger` | Wolf's Hunger | Legendary | +3% lifesteal |
| 10 | `fae_touched` | Fae Touched | Legendary | +4 points rarity elevation on drops |
| 16 | `thin_blood` | Thin Blood | Flaw | -6% max HP |
| 17 | `faint_heart` | Faint Heart | Flaw | -5% attack damage |
| 18 | `clumsy_hands` | Clumsy Hands | Flaw | -6% gathering speed |

Every effect acts on a value the game demonstrably reads (verified 2026-09-13):
max HP, attack, `AttackSpeedPct` (`CombatDamageModel.AttackIntervalMs`, capped),
`CritChancePct`, `DodgeChancePct`, `LifestealPct`, `RarityElevationPct`
(`CombatLootEngine`), gathering speed and gathering yield - online and offline.

Player-facing text uses the keys `trait.<key>.name` and
`trait.<key>.description`; round 3 translates them.

## 4. Inheritance and mutation

Applied in this order when a child is born. All randomness comes from the
engine's single `Random`, like the aptitudes.

1. **Inherit.** Each parent trait passes with **50%**; a trait both parents
   carry passes with **90%**. A villager parent's traits count like any
   parent's.
2. **Related pair** (the round-1 `BreedingRelatedness` check): **60%** chance
   of one random flaw the child does not already have. Inverted aptitude drift
   and the 1% epic chance stay as they are - both are already shown.
3. **Mutation:** chance **4% + 1% per Breeding Grounds level** (14% at 10) of
   one new random non-flaw trait, rarity weights Common 70 / Rare 25 /
   Legendary 5.
4. **Epic mutation** (5%, 1% if related): keeps +1 to all four aptitudes and
   additionally **guarantees one new Rare (80) or Legendary (20)** trait.
5. **Cap at 3.** Flaws are never dropped by the cap - the only way out of a
   flaw is breeding it out (a clean partner halves its odds each generation).
   Past flaws, the highest rarity wins (Legendary > Rare > Common); ties are a
   coin flip. A new trait from steps 3-4 only enters a full set if it outranks
   the lowest-rarity positive trait, which it then replaces.

**Newcomers** arrive with a trait at **20% + 3% per Inn level** (capped at
50%). Of those, **15%** are flaws. Otherwise the rarity weights are Common 75 /
Rare 25 below Inn level 6, and Common 60 / Rare 32 / Legendary 8 from Inn 6.
A newcomer carries at most one trait.

**Existing characters** start with no traits; nothing is backfilled. Characters
flagged `IsInbred` simply lose the hidden -25% growth.

## 5. Data and persistence

- `CharacterLineageRegistry.TraitMask` and `VillageNewcomer.TraitMask`: `long`,
  default 0. One additive migration (`AddBreedingTraits`); no existing row is
  rewritten, so no backup is required - take one anyway before deploying, as
  every breeding release has.
- `TickStatePayload.TraitMask`: hydrated in `StateCheckpointManager` beside the
  slot-1 aptitudes, and everywhere the slot-1 lineage is reloaded.
- `StateUpdatePacket` does **not** change. The client reads traits over REST.
- Genes: the genome keeps its bits (race lives in the same `long`) and splicing
  keeps running, but nothing consumes Speed/Crit/Yield any more.
  `TickStatePayload.LocusSpeed/LocusCrit/LocusYield` and their hydration are
  removed; `RaceAttributeGrowth` drops the loci term and the inbred multiplier
  and keeps the epic +5%.

## 6. Code structure

- `Engine/TraitRegistry.cs` - pure, static: the catalogue above, lookup by bit,
  `IsFlaw`, rarity.
- `Engine/BreedingTraits.cs` - pure, static: section 4 (`Inherit`,
  `RollNewcomerTrait`, `PreviewOdds`), taking an explicit `Random`, so every rule
  is a millisecond test.
- `Engine/TraitTotals.cs` - `TraitTotals.From(long mask)`: sums effects and
  applies the +20 / -15 caps.
- `Engine/BloodlineBonuses.cs` - **one function per stat that today has two
  hand-written copies**, called by both `SimulationEngine` and
  `OfflineSimulationEngine`:
  - `ApplyAttack(milliAttack, strengthAptitude, traits)`
  - `ApplyMaxHp(milliHp, enduranceAptitude, traits)`
  - `GatherSpeedBonusPct(skillAptitude, traits)`
  - `GatherYieldFactor(traits)`

  **This fixes a live defect found while designing:** offline combat never
  applies the Strength aptitude. `SimulationEngine` adds it after
  `ComputeEffectiveMilliAttack` (line ~5088); `OfflineSimulationEngine` calls
  `ComputeEffectiveMilliAttack` and stops, so a bred line kills more slowly
  away than online - the "three paths grow a level" shape from CLAUDE.md.
- `StatsCalculator.Calculate` replaces its `locusSpeed`/`locusCrit` parameters
  with `in TraitTotals`, which carries crit, attack speed, dodge, lifesteal and
  rarity elevation to every caller (live, offline, loot, guild war, benchmark).
- `BreedingEngine` calls `BreedingTraits.Inherit` for both pairings and stores
  the mask; `VillageArrivalEngine.Roll` calls `RollNewcomerTrait`.

## 7. REST

- `GET /api/v1/breeding/traits` - the catalogue: `Id` (bit), `Key`, `Name`,
  `Description`, `Rarity`, `Effect`, `Value`. The client never keeps its own copy.
- Roster, Hall and newcomers gain `TraitMask`.
- Both previews gain `TraitOdds` (`TraitId`, `ChancePct`, `Source`: father /
  mother / both / villager), `MutationChancePct`, `FlawChancePct`, and drop the
  Speed/Crit/Yield loci.

## 8. Interface (mobile and web)

- **Trait badges**, coloured by rarity (grey Common, blue Rare, gold Legendary,
  red Flaw), under the name on picker cards, Hall rows and newcomer rows. They
  wrap below the aptitudes on a phone rather than overflowing.
- **Tap or hover** a badge for its effect. On a phone this opens a small inline
  explanation - `title` does nothing on touch.
- **Child preview**: "Traits the child can inherit" with each trait's chance
  and source, the mutation chance from the Grounds, and for a related pair a red
  "60% chance of a flaw" before the Breed button. The "And its genes" section is
  removed.
- **Wiki**: a Traits page listing every trait, its rarity and effect, and how
  flaws arise and leave.
- Geometry: `check:touch`, `check:overlap`, `check:clipping` on Breeding,
  Ancestors and Village at 390px.

## 9. Tests

- **Pure (xUnit):** 50/90% inheritance (sampled within tolerance), the cap,
  flaws surviving the cap, Grounds-scaled mutation, epic guaranteeing Rare+,
  related-pair flaw, newcomer rolls by Inn (no Legendary below 6), and a
  **pinned bit table** that fails if any trait moves.
- **Effects:** `TraitTotals` caps; online and offline produce the same attack,
  HP, gathering speed and yield for the same payload - which fails today on
  offline Strength; traits added as levers in both `PowerCeilingTests` ledgers.
- **Integration (Testcontainers):** a birth stores the child's mask, a newcomer
  arrives with one, the previews return odds, and a character with a trait gets
  different stats in the payload.
- **Client (vitest):** badge and odds formatting; `serverMirrors` asserts no
  client-side copy of the catalogue.
- **End to end:** the full suite, `npm run exercise` (the preview lists traits;
  a born child shows its traits in the Hall), and `smoke:screens`.

## 10. Out of scope

- Translation (round 3) - keys only.
- Choosing a trait with the Grounds' selection mask.
- Rebalancing aptitudes, the monster ladder or the epic chance.
- Any change to `StateUpdatePacket`.
