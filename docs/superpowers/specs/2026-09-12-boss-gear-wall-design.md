# Boss gear wall — design

Date: 2026-09-12
Status: approved, not yet implemented

## The defect

A player in a full set of **RegionTier 4** gear was winning the fight against
**Malakor**, the region-5 final boss, on 2026-09-12 — the same day they cleared
the region-4 boss. Measured from the live database, that set was:

| slot | base id | RegionTier | QualityTier | affixes |
|---|---|---|---|---|
| weapon | eq_frost_crystal_staff | 4 | 8 | 3, all `@5` |
| helmet | eq_brawler_pelt | 4 | 9 | 3, all `@5` |
| chest | eq_monolith_body | 4 | 12 | 4, all `@5` |
| leggings | eq_monolith_guards | 4 | 8 | 3, all `@5` |
| boots | eq_monolith_footguards | 4 | 10 | 4, all `@5` |
| gloves | eq_monolith_bracers | 4 | 12 | 4, all `@5` |
| amulet | eq_glacial_pendant | 4 | 12 | 4, all `@5` |
| ring | eq_frost_band | 4 | 9 | 3, all `@5` |

Every affix on every piece is Legendary affix rarity. Malakor's authored stats
are 348,670 HP and 118,400 attack on a 2,000 ms interval; the uncleared boss
carries the flat `FirstClearHpMultiplier = 5` and
`FirstClearAttackMultiplier = 2`, so 1,743,350 HP and 236,800 a blow. That was
not enough.

The fight is a genuine race and therefore tunable to a loss: on death the tick
sets `ActiveActivityId = 0` and zeroes `CurrentMonsterHp`, so a boss a character
cannot out-damage resets to full and the character is halted with
`ActivityHaltReason.Died`. "Unwinnable" is expressible as a number.

## Decisions taken

- **Mechanism: tuning only.** No new gate, no refusal to engage. A boss that is
  too big is the wall.
- **First clear only.** The multipliers apply while a boss is uncleared, exactly
  as `BossFirstClearRules` already works. A cleared boss reverts to its authored
  stats and stays farmable.
- **The required-gear ladder** (all 8 combat slots, the boss's OWN region tier):

  | boss | gear RegionTier | QualityTier | affixes |
  |---|---|---|---|
  | region 1 | 1 | >= 4 | any |
  | region 2 | 2 | >= 6 | any |
  | region 3 | 3 | >= 8 | Rare+ |
  | region 4 | 4 | >= 10 | Epic+ |
  | region 5 (Malakor) | 5 | >= 11 | all Legendary |

  Region 1 starts at tier 4 deliberately. A fresh account must beat the region-1
  boss to reach region 2, the Forge's level ceiling starts at 2
  (`2 + TownHallLevel * 2`), and tier 8 is a ~1-in-400 drop roll — a higher bar
  there reproduces the shipped defect where a new player could not get past the
  first monster.
- **Transcendent trophy.** The first clear of each region boss grants one
  QualityTier-14 equipment piece of that boss's own region, random slot. Five per
  account, ever. Not granted retroactively for clears that already happened.
- **Out of scope:** regular monsters, already-cleared bosses, the World Boss, the
  Delve, and the supply side (how a tier-11 set is obtained). The grind is
  measured and printed by the tests rather than changed here.

## What the quality tier can and cannot do

Two measurements constrain the design and are recorded so the next reader does
not re-derive them:

- `CombatLootEngine.RarityTier.PowerMultiplier` spans tiers 1 to 14 at **2.12x
  total**, about 7.4% a tier. Affix COUNT steps at tiers 4, 7, 10 and 13
  (`GetAffixCount`). So tiers 10, 11 and 12 all carry 4 affixes and sit within
  ~5% base power of each other.
- Fusion cannot exceed the Forge ceiling of 12, so tiers 13 and 14 only ever
  arrive as a drop roll of weight 0.001 and 0.0001 against a total of 196.7.

Consequence: the ladder's top two rows cannot be separated by quality tier
alone. The separation between the region-4 boss and Malakor comes from the gear's
**RegionTier** (region-4 against region-5 base stats), **affix rarity**, and
level. Quality tier is the coarse band. If the calibration finds tier 10 and
tier 11 indistinguishable, the test reports the real separating number; it is not
tuned to a fiction.

This is also what makes the Transcendent trophy meaningful: tier 14 is 5 affixes
and the top of the power curve, and the trophy is the game's only reliable source
of it.

## Design

### 1. Per-region multipliers in `BossFirstClearRules`

`FirstClearHpMultiplier` and `FirstClearAttackMultiplier` become per-region
tables behind `HpMultiplierFor(int region)` and `AttackMultiplierFor(int region)`.
`MaxHpFor` and `AttackPowerFor` keep their signatures, so the four fight-start
sites in `SimulationEngine` are unchanged.

First Blood keeps its existing shape — it reduces the penalty above 1x and can
never take the boss below its authored stats.

### 2. `Domain/Combat/BossGearBenchmark.cs` (new, pure, no DB, no async)

Given `(level, gearRegionTier, qualityTier, affixRarity, foodTier)` it builds a
reference 8-slot loadout and resolves the race:

- player sustained DPS against boss HP, versus
- boss DPS after armour, block and dodge, net of **one auto-eat per
  `AutoEatCooldownTicks` (25 ticks = 2.5 s)** — healing is a share of max HP, so
  sustain is a real term — against the player's health bar from
  `ProgressionEngine.BaseMilliHpForLevel`.

It calls the **live** `CombatDamageModel`, `RarityTier.PowerMultiplier`,
`RarityTier.GetAffixCount` and the `AffixRegistry` magnitude bounds. No second
copy of the combat math: two derivations of one truth is this codebase's
dominant bug class, and a benchmark that disagrees with the tick is worse than
no benchmark.

It assumes **First Blood at its maximum level**, so the wall holds for a fully
invested character rather than only an un-invested one.

### 3. `BossWallTests` (new)

Prints, for each region boss, a win/lose matrix across QualityTier 1-14 x gear
RegionTier {R-1, R, R+1}, and **asserts**:

1. the break-even QualityTier equals the ladder above (4 / 6 / 8 / 10 / 11);
2. a full set one region behind the boss always loses, at every quality tier;
3. the required set wins — the wall is a fight, not a brick;
4. the multipliers never make a cleared boss harder than its authored stats.

The per-region multipliers are whatever makes those assertions pass. A number
this test prints is a number it checks.

### 4. The third path

`OfflineSimulationEngine.cs:513` applies the wall by reading
`FirstClearHpMultiplier` and `FirstClearAttackMultiplier` directly, which also
means it ignores First Blood relief. It becomes a call to `MaxHpFor` /
`AttackPowerFor`. Three paths resolve a boss fight — the tick, the offline
catch-up, and the snapshot the client is sent (`SimulationEngine:3616`) — and a
wall present in some of them is a health bar that changes when a player
reconnects.

`BossFirstClearTests` asserts `* 5` literally and is updated with the table.

### 5. The Transcendent trophy

On the first kill of a region boss, grant one equipment instance:

- `RegionTier` = the boss's region, slot chosen from that region's drop table,
  restricted to the **combat slots 0-7**. There are eleven equipment slots and
  8 Axe / 9 Pickaxe / 10 Rod are tools; a Transcendent pickaxe is not a trophy
  for killing a boss, and every list in this repo that forgot the distinction in
  either direction has been a bug.
- `QualityTier` = 14, so 5 affixes per `GetAffixCount`. Affix **rarity** is
  rolled by the normal weighted table rather than forced to Legendary: the
  trophy is the best possible item frame, not a finished item, and the reroll
  system is how a player finishes it.
- granted **once ever per region boss**.

Idempotency is guarded on the durable first-kill transition — the
`monster_codex_entries` row for that boss going from absent/0 to 1, inside the
transaction that writes it — **not** on `DefeatedRegionBossMask`, which is a
payload cache that a stale session can re-present. Delivery goes through
`CombatLootEngine`'s existing drain, which isolates each dequeued item in its own
try/catch; a worker that throws here would take equipment drops down for every
player on the server.

## Verification

- `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj`
  (needs Docker) — `BossWallTests`, `BossFirstClearTests`, plus the existing
  measured suites that touch this ladder: `MonsterLadderTests`,
  `ProgressionRateTests`, `PowerCeilingTests`, `CombatIdentityTests`.
- The multipliers are a new lever, so `PowerCeilingTests`' ledger gains the boss
  side rather than silently ignoring it.
- A live check that the snapshot, the tick and the offline path agree on an
  uncleared boss's maximum health.

## Risks

- The tuning targets a reference build. A real character differs by lineage
  (Warrior +5% damage a level, Tank +8% HP), attributes, set bonuses, skill
  tree and potions. The matrix is therefore a band, not a knife edge, and the
  assertions are written as bands.
- Raising Malakor's wall walls the one account that is positioned to fight it
  until it holds a region-5 set. That is the intent.
- A tier-14 trophy from the region-1 boss is a strong early item. The benchmark
  includes one trophy piece in the reference build for the next boss up, so the
  ladder is calibrated with it rather than around it.
