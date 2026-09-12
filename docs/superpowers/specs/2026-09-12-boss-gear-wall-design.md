# Boss gear wall — design

Date: 2026-09-12
Status: IMPLEMENTED 2026-09-12. Two things changed during calibration - see "What the measurement changed" at the end.

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
  | region 2 | 2 | >= 7 | any |
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


## What the measurement changed

Two parts of the design above did not survive contact with the projection, and
both changes are recorded here rather than quietly applied.

### Region 2 asks for quality 7, not 6

Affix COUNT steps at quality 4, 7, 10 and 13 (`RarityTier.GetAffixCount`), and
tiers 4 and 6 both carry two - so "quality 6 wins, quality 4 loses" was a 4%
tuning window between two mechanically identical sets. That is a coin flip
dressed as a requirement, and it would have flaked the first time anything
touched armour, food or the HP curve. Seven is the first tier carrying a third
affix. Every other row of the ladder already crossed a step.

### The wall is calibrated per region AT THE TOP OF ITS OWN LEVEL BAND

The spec said "the reference character is level 100 because the wall is about
gear". Measured, that is wrong in both directions:

- At level 100 a character's health bar is ~611,000 and **gear contributes under
  1% of it** (624,491 in region-4 T14 against 630,648 in region-5 T11). Survival
  at the cap is a function of the level curve, not of gear.
- Auto-eat restores 12% of max HP per food tier every 2.5 s, so a level-100
  character with region-5 food sustains about **147,000 HP/s** against a boss
  dealing 34,000/s. Nothing could kill it. This is the trap already written into
  the tick: *"nothing could kill a player who owned fish, so a boss was a check
  on inventory rather than on equipment, and every attempt to make gear the gate
  failed against it."*
- And calibrating the EARLY bosses at level 100 would have been worse than
  useless: the region-1 boss needs about 4,000x its authored attack to threaten a
  level-100 character, and would then one-shot every real new player who meets it
  at level 20.

So the reference level is the top of each region's own band - twenty levels a
region, the design's own pacing and the same mapping `ProgressionRateTests` uses
(`region = (level - 1) / 20 + 1`). Region 5's band top is level 100, which is
where Malakor is actually fought and why the wall has to hold at the cap. The
live account that went after Malakor in region-4 gear was level 88: inside region
5's band, a region behind on gear - exactly the case the wall now refuses.

### What makes the wall work, since health does not

**Mitigation.** Incoming DPS across the three region-5 candidates is 33,991 /
21,695 / 15,940 - a 2.1x spread driven by armour and block, which DO come from
the gear's region and rarity. The attack multipliers are set so sustain covers
the required set and not the ones below it, and the windows were found by
bisection rather than chosen:

| boss | lethal for two-below | lethal for region-behind | lethal for REQUIRED | picked |
|---|---|---|---|---|
| region 1 | 3.2 | 3.2 | 4.3 | 3.7 |
| region 2 | 2.5 | 1.9 | 2.7 | 2.6 |
| region 3 | 5.4 | 3.9 | 5.9 | 5.7 |
| region 4 | 10.0 | 6.4 | 13.0 | 11.4 |
| region 5 | 18.3 | 11.6 | 25.0 | 21.4 |

Health multipliers (3 / 4 / 6 / 9 / 14) set the LENGTH of the fight rather than
its outcome: 194 s, 346 s, 592 s, 815 s and 1,190 s for the required set. A
three-to-twenty-minute boss fight is an event; it is not a chore.

### One more thing this required

`SimulationEngine`'s incoming-damage path saturated `rawDamage` at `int.MaxValue`.
That was a correct overflow fix and an invisible CEILING on the wall - 2.147e9
milli-damage is about twelve times Malakor's authored attack, and region 5 needs
twenty-one. The path is `long` now, with the subtraction onto the `int` PlayerHp
clamped. Without this the table above would have been silently truncated and the
wall would have looked tuned while being capped.

### Measured outcome

| boss | required set | two tiers below | a full set one region behind |
|---|---|---|---|
| Alpha Wolf | wins in 194 s | dies in 40 s | - |
| Shadow Lynx | wins in 346 s | dies in 66 s | dies in 2 s at every tier 1-14 |
| Magma Wyrm | wins in 592 s | dies in 32 s | dies in 2 s at every tier 1-14 |
| Frost Titan | wins in 815 s | dies in 6 s | dies in 2 s at every tier 1-14 |
| Malakor | wins in 1,190 s | dies in 16 s | dies in 2 s at every tier 1-14 |

The bottom-right column is the defect this work existed to fix: a full set of
region-4 gear, at ANY quality tier including Transcendent, now dies to Malakor in
about two seconds.
