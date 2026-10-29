# FolkIdle stats reference

Written 2026-10-29 from the server source (`server/FolkIdle.Server`), not
from older docs. Every number below is a constant or a formula in the code, and
each section names the file that owns it. If this document and the code
disagree, the code wins. Re-check the file before you tune anything.

Section 16 lists the defects found while writing this. Four of them were stats
that a screen promises and the simulation never applied. Items 1 and 2 were
fixed on 2026-10-29 (PR "stats: pay inheritance gathering yield and Fortitude
armour"); section 16 marks each item's status.

---

## 0. The pipeline in one page

Every character's combat stats come from one function,
`StatsCalculator.Calculate` (`Engine/StatsCalculator.cs`). The live tick, the
offline catch-up, the hunting advisor and the guild-war snapshot all call it.
After that, `SimulationEngine` applies the account-wide multipliers.

**Order inside `StatsCalculator.Calculate`:**

1. Epic-mutated lineage: STR/DEX/CON/LCK × 1.05.
2. Attributes become base stats (section 2).
3. Completed areas: +1% loot luck each (up to 10).
4. Race mastery bonuses (Vila crit, Draugr lifesteal).
5. Potions (offensive, defensive).
6. Innate race passives (multiply what exists so far).
7. Equipped gear: item base power × quality, plus affixes, plus pets (section 3-6).
8. Attribute milestones, including the percentage ones (they act on gear).
9. Pet armour %.
10. Heritable traits (crit, speed, dodge, lifesteal, elevation).
11. Set bonuses (flat attack, armour %, fire %, 5-piece flags).
12. Age penalty: × 0.95 (Senior) or × 0.90 (Elder) on melee damage, max HP and
    attack speed.

**Attack per swing** (`StatsCalculator.ComputeEffectiveMilliAttack`, then
`SimulationEngine.EffectiveMilliAttackFor`):

```
base     = 15 + 15 × 5% × level              (Warrior lineage, see 1.3)
flat     = base + FlatMeleeDamage            (STR×2 + gear attack + potion, ×age)
× (1 + EquipmentDamagePct)                   (weapon dmg affixes + Might milestones + pet Damage)
× (1 + inheritance Damage + guild Damage buff + Renown)    (one additive group)
× (1 + Legacy "combat speed" rank %)          (a damage multiplier despite the name; no client UI sells it)
× (1 + Strength aptitude %)
× (1 + trait attack %)
```

Then each swing: × crit multiplier on a crit, monster armour mitigation (armour
penetration helps), × codex damage multiplier, × set fire %, + burn.

**Max HP** (`SimulationEngine.EffectiveMaxMilliHpFor`):

```
base     = 100 × 1.092^(level-1)
+ CombatStats.MaxHp                          (CON×15 + gear HP + potion, race ×, milestones ×, age ×)
× (1 + inheritance Max HP)
× (1 + pet MaxHp)
× (1 + Fortitude bough)
× (1 + Endurance aptitude %)
× (1 + trait HP %)
```

---

## 1. Levels

Owner: `Engine/ProgressionEngine.cs`, `Engine/RaceAttributeGrowth.cs`.

### 1.1 XP curve

`XP to next level = ceil(220 × 1.114^L × e^(4.4 × (1 − e^(−L/6))))`

| Level | XP to next |
|---|---|
| 1 | 482 |
| 5 | 4,543 |
| 10 | 22,975 |
| 20 | 132,695 |
| 50 | 3,954,747 |
| 100 | 874,656,192 |

The opening term makes early levels fast. The 1.114 growth makes each region
cost much more than the one before.

### 1.2 What one level pays

| Reward | Amount | Notes |
|---|---|---|
| Attribute points | 7 | × 1.05 for an Epic-mutated lineage. The player places them (section 2). |
| Skill points | 1 | Section 8. |
| Base HP | × 1.092 | `100 × 1.092^(L−1)`: L10 = 221, L20 = 532, L50 = 7,463, L100 = 608,193. |
| Base attack | +0.75 | 5% of the base 15, for the Warrior lineage (1.3). |

Combat XP multipliers stack additively in `LiveKillXpMultiplierPct`: mentors (+5%
each below level 50), Human mastery (+12% / +15%), Legacy XP perk (+1%/rank),
inheritance XP (+2%/level), Insight root (+0.4%/level), guild Exp buff (+2% per
tier), pets XP, rebirth re-level bonus (+50% below the peak level). A mentorship
termination penalty is −20%.

### 1.3 Lineage (Warrior / Tank)

`ProgressionEngine.Lineages` defines Warrior (+5% base damage per level) and
Tank (+8% base HP per level). Registration sets every account to Warrior
(`AuthenticationEngine`), and no command changes it. So Tank exists only in the
table.

---

## 2. Attributes

Owners: `Engine/AttributeRegistry.cs`, `Engine/StatsCalculator.cs`,
`Domain/Progression/AttributeTickCoordinator.cs`,
`Domain/Combat/EquipmentAttributeGate.cs`.

### 2.1 Basics

- Four attributes: **Might** (STR), **Finesse** (DEX), **Vigour** (CON),
  **Fortune** (LCK).
- Everything starts at **0**. Each level pays 7 unspent points.
- Attributes are **account-wide**, on `PlayerRecord`. All characters share them.
- **Spend:** any amount into one attribute. The server refuses an amount above
  the unspent balance.
- **Respec:** free and unlimited. All placed points return to the pool.
- **Rebirth** resets all four to 0 and the pool to 0.

### 2.2 What one point gives

Flat effects are linear. Percentage effects use a square root:
`value = k × √points`.

| Attribute | Flat (per point) | Curved (k × √points) |
|---|---|---|
| Might | +2 melee damage, +1 armour penetration | — |
| Finesse | +1 accuracy | crit chance k=1.5, attack speed k=0.8 |
| Vigour | +15 max HP, +1 armour, +0.1 HP/s regen | block k=0.6 |
| Fortune | — | loot luck k=1.2, rarity elevation k=0.35 |

Curved values at common point totals:

| Points | Crit % | Attack speed % | Block % | Loot luck % | Elevation % |
|---|---|---|---|---|---|
| 25 | 7.5 | 4.0 | 3.0 | 6.0 | 1.75 |
| 60 | 11.6 | 6.2 | 4.6 | 9.3 | 2.71 |
| 100 | 15.0 | 8.0 | 6.0 | 12.0 | 3.50 |
| 200 | 21.2 | 11.3 | 8.5 | 17.0 | 4.95 |
| 300 | 26.0 | 13.9 | 10.4 | 20.8 | 6.06 |
| 600 | 36.7 | 19.6 | 14.7 | 29.4 | 8.57 |

### 2.3 Milestones

There are five rungs per attribute, at 25 / 60 / 120 / 200 / 300 points. The
percentage rungs apply **after** gear, so they multiply what the character
actually wears.

| Threshold | Might | Finesse | Vigour | Fortune |
|---|---|---|---|---|
| 25 | Heavy Hands: +5% attack power | Quick Step: +3% attack speed | Hardy: +5% max HP | Scavenger: +8% loot luck |
| 60 | Sunder: +40 armour pen | Keen Eye: +25 accuracy | Thick Skin: +10% armour | Prospector: +5% gathering yield (wood and ore) |
| 120 | Executioner: +8% attack power | Deadly Precision: +15% crit damage | Second Wind: +2 HP/s regen | Lucky Strike: +2% crit chance |
| 200 | Titan's Grip: +80 armour pen | Flurry: +4% attack speed | Ironhide: +8% max HP | Golden Touch: +8% gold |
| 300 | Worldbreaker: +12% attack power | Perfect Form: +25% crit damage | Unbreakable: +25% crit mitigation | Fortune's Favour: 8% off fusion fees |

Sum of the tracks: Might +25% attack power and +120 armour pen. Finesse +7%
speed, +25 accuracy and +40% crit damage.

### 2.4 Gear requirements

Equipping region-2+ gear needs a minimum in one attribute. The server checks it
**only when you equip**. Gear already worn is never removed.

`minimum = 20 × (regionTier − 1)`: region 2 = 20, region 3 = 40, region 4 = 60,
region 5 = 80.

| Slot | Attribute |
|---|---|
| Weapon | Might |
| Helmet, Chest, Leggings | Vigour |
| Gloves, Boots | Finesse |
| Amulet, Ring | Fortune |

A full region-5 kit therefore needs 80 in each of the four attributes, or 320
points. That is about level 47.

---

## 3. Equipment

Owners: `Domain/Combat/EquipmentSlotEngine.cs`, `Engine/CombatLootEngine.cs`
(`RarityTier`), `Engine/EquipmentDropTable.cs`,
`Domain/Combat/RegionUnlockGate.cs`, `server/GameData/items.json`.

### 3.1 Slots

There are **eleven** slots. Slots 0-7 are combat slots: Weapon, Helmet, Chest,
Gloves, Leggings, Boots, Amulet, Ring. Slots 8-10 are tools: Axe, Pickaxe, Rod.
Gear and pets belong to a **character** (slot 1 is the main character). One
physical item cannot be worn by two characters.

### 3.2 Base power per region

Item base power **triples each region**. Weapons have only attack. Armour,
amulets and rings have only defence.

| Region | Weapon attack | Any armour/jewellery defence | Base gold value |
|---|---|---|---|
| 1 | 12 | 8 | 50 |
| 2 | 36 | 24 | 200 |
| 3 | 108 | 72 | 800 |
| 4 | 324 | 216 | 3,200 |
| 5 | 972 | 648 | 12,800 |

Every location authors 15 pieces: three weapons (melee / ranged / magic, all
with the same numbers), two armour families × five pieces, one amulet and one
ring. Tools have no combat stats; their affixes carry the gathering effects.

The catalogue has items up to region tier 10 (`MaxAuthoredRegionTier`), but
`items.json` currently authors only tiers 1-5. Tiers 6-10 would drop only from
the final boss.

### 3.3 Item rarity (quality tier)

There are 14 tiers. Quality multiplies the item's base power:
`multiplier = 2.12^((tier−1)/13)`. So the whole ladder is worth ×2.12, less
than one region step (×3). A Transcendent from one region down still loses to
a Normal from two regions up.

| # | Tier | Power × | Affixes | Drop share at 0 luck |
|---|---|---|---|---|
| 1 | Normal | 1.000 | 1 | 50.85% |
| 2 | Common | 1.060 | 1 | 25.42% |
| 3 | Uncommon | 1.123 | 1 | 12.71% |
| 4 | Rare | 1.189 | 2 | 6.36% |
| 5 | Ultra Rare | 1.260 | 2 | 2.54% |
| 6 | Epic | 1.335 | 2 | 1.27% |
| 7 | Legendary | 1.415 | 3 | 0.51% |
| 8 | Mythic | 1.499 | 3 | 0.25% |
| 9 | Relic | 1.588 | 3 | 0.051% |
| 10 | Ancient | 1.682 | 4 | 0.025% |
| 11 | Divine | 1.782 | 4 | 0.0051% |
| 12 | Demonic | 1.889 | 4 | 0.0025% |
| 13 | Godly | 2.001 | 5 | 0.0005% |
| 14 | Transcendent | 2.120 | 5 | 0.0001% |

### 3.4 How a drop's rarity is decided

`RarityTier.ResolveDropTier`:

1. **Weighted roll.** Normal has a fixed weight of 100. Tiers 2-14 have weights
   50 / 25 / 12.5 / 5 / 2.5 / 1 / 0.5 / 0.1 / 0.05 / 0.01 / 0.005 / 0.001 /
   0.0001, each × `(1 + lootLuck% / 100)`. Luck therefore only shrinks Normal's
   share. It does not reshape the tiers above Normal.
2. **Golden Fleece** (Fortune crown): every 100th kill, +2 tiers.
3. **Rarity elevation:** a chance of +1 tier, capped at 14.

**Loot luck sources** (`LootLuckBreakdown`): the Fortune curve, the Scavenger
milestone, completed areas (+1% each), pet Drop Chance, inheritance Loot luck
(+2%/level), the Fortune root (+1%/level), the guild DropRate buff (+2%/tier) and
the Fortune aptitude.

**Elevation sources:** the Fortune curve, the Fae Touched trait (+4) and the
Rarity bough (+1%/level, maximum 8%).

### 3.5 Who drops what

Each location's 15 pieces are dealt across its 5 monsters slot by slot, so
**every monster drops every slot**. Armour is ordered by family, so each
monster carries 3 pieces of one set and 2 of the other, alternating between
monsters. Tools are crafted and never dropped.

### 3.6 Wearing gates

- **Region gate:** you can wear gear from a region only after you beat the
  previous region's boss. Rarity does not matter. Buying from the market
  checks the same gate.
- **Attribute gate:** section 2.4.
- The old level gate (`EquipmentLevelGate`, `(region−1)×10 + quality×2`) was
  deleted on 2026-10-29; the region gate replaced it long before.

### 3.7 Crafting and Workshop

A crafted item is always **Normal** with 1 affix (`CraftingEngine`). Workshop
commissions produce higher tiers (`WorkshopCommissionRules`), and the affix
count follows the same table.

---

## 4. Affixes

Owner: `Engine/AffixRegistry.cs`, folded in by `EquipmentSlotEngine.AddAffixTotals`.

### 4.1 The 15 affixes and where they may roll

| Affix | Law | Weapon | Helmet | Chest | Leggings | Boots | Gloves | Amulet | Ring | Tool |
|---|---|---|---|---|---|---|---|---|---|---|
| flat_hp | FlatHp | | ✓ | ✓ | ✓ | ✓ | | ✓ | | |
| flat_armor | FlatStat | | ✓ | ✓ | ✓ | ✓ | | ✓ | | |
| armor_pen_flat | FlatStat | ✓ | | | | | ✓ | | | |
| melee_dmg_pct | % | ✓ | | | | | | | | |
| range_dmg_pct | % | ✓ | | | | | | | | |
| magic_dmg_pct | % | ✓ | | | | | | | | |
| attack_speed_pct | % | ✓ | | | | ✓ | ✓ | | | |
| crit_chance_pct | % | ✓ | ✓ | | | | | | | |
| crit_dmg_pct | % | ✓ | | | | | | | | |
| lifesteal_pct | % | ✓ | | | | | | | | |
| dodge_chance_pct | % | | ✓ | | ✓ | ✓ | | | | |
| block_chance_pct | % | | | | | | | | ✓ | |
| gather_speed_pct | % | | | | | | | | | ✓ |
| gather_yield_pct | % | | | | | | | | | ✓ |
| gather_rare_find_pct | % | | | | | | | | | ✓ |

Legal affixes per slot: Weapon 8, Helmet 4, Boots 4, Leggings 3, Tool 3,
Chest 2, Gloves 2, Amulet 2, **Ring 1**.

The three damage % affixes all add into one `EquipmentDamagePct`, because combat
has one damage number. Melee, ranged and magic are therefore the same stat.

### 4.2 How many, and what happens when there are more rolls than types

The count comes from the item tier (3.3): 1 / 2 / 3 / 4 / 5. Each roll picks an
**unused** legal type first. Once all types are used, it picks any legal type
again and **stacks** it as a second copy (`crit_chance_pct#2`). So a
Transcendent ring has five block rolls, and a 5-affix chest has HP and armour
two or three times each.

### 4.3 Affix rarity (separate from item rarity)

Each affix has its own rarity, 1-5: Common, Uncommon, Rare, Epic, Legendary.
Base weights are 52 / 28 / 15 / 4 / 1 %. A better item rolls more times and
keeps the best result: `attempts = 1 + 3 × (itemTier − 1) / 13`, with the
fraction as a chance of one more attempt.

| Item tier | Attempts | Common | Uncommon | Rare | Epic | Legendary |
|---|---|---|---|---|---|---|
| 1 Normal | 1 | 52% | 28% | 15% | 4% | 1% |
| 7 Legendary | 2-3 | ~27% | ~37% | ~26% | ~8% | ~2% |
| 10 Ancient | 3-4 | ~14% | ~37% | ~35% | ~11% | ~3% |
| 14 Transcendent | 4 | 7.3% | 33.6% | 40.5% | 14.6% | 3.9% |

### 4.4 Magnitude

Each roll lands in **±20%** around a centre value. The centre depends on the law:

- **FlatHp:** `15 × 2.2^(region−1) × 1.6^(affixRarity−1)`
- **FlatStat** (armour, armour pen): `6 × 3^(region−1) × 1.6^(affixRarity−1)`
- **Percentage:** `base + growth × (affixRarity − 1)`. **The region does not
  enter this formula.**

FlatHp centre:

| Region | Common | Uncommon | Rare | Epic | Legendary |
|---|---|---|---|---|---|
| 1 | 15 | 24 | 38 | 61 | 98 |
| 2 | 33 | 52 | 84 | 135 | 216 |
| 3 | 72 | 116 | 185 | 297 | 475 |
| 4 | 159 | 255 | 408 | 654 | 1,046 |
| 5 | 351 | 562 | 899 | 1,439 | 2,302 |

FlatStat centre (flat_armor, armor_pen_flat):

| Region | Common | Uncommon | Rare | Epic | Legendary |
|---|---|---|---|---|---|
| 1 | 6 | 9 | 15 | 24 | 39 |
| 2 | 18 | 28 | 46 | 73 | 117 |
| 3 | 54 | 86 | 138 | 221 | 353 |
| 4 | 162 | 259 | 414 | 663 | 1,061 |
| 5 | 486 | 777 | 1,244 | 1,990 | 3,185 |

Percentage centre (identical in every region):

| Affix | Common | Uncommon | Rare | Epic | Legendary |
|---|---|---|---|---|---|
| melee/range/magic dmg | 2.0% | 3.5% | 5.0% | 6.5% | 8.0% |
| attack speed | 1.0% | 1.5% | 2.0% | 2.5% | 3.0% |
| crit chance | 0.5 | 1.0 | 1.5 | 2.0 | 2.5 |
| crit damage | 5.0% | 7.5% | 10.0% | 12.5% | 15.0% |
| lifesteal | 0.5% | 0.8% | 1.1% | 1.4% | 1.7% |
| dodge | 0.5 | 0.9 | 1.3 | 1.7 | 2.1 |
| block | 1.0% | 1.8% | 2.6% | 3.4% | 4.2% |
| gather speed | 1.5% | 2.5% | 3.5% | 4.5% | 5.5% |
| gather yield | 2.5% | 4.3% | 6.1% | 7.9% | 9.7% |
| gather rare find | 3.0% | 5.2% | 7.4% | 9.6% | 11.8% |

### 4.5 Changing affixes

- **Gold reroll:** 1,000 / 2,000 / 4,000 / 5,000 / 10,000 by region. The streak
  growth is 1.0, so the price stays flat. Capped at 100M. **The flat price is
  deliberate** (commit `143502e1`): the old ×-per-attempt curve priced 20
  attempts at 13.5M against ~564k earned over a whole level 1-100 run, so the
  ~100-attempt Legendary chase was impossible. Do not bring a growth factor
  back without re-running `ProgressionRateTests`; the 100M cap is a guard only.
- **Diamond reroll:** `5 × 1.35^(tier−1)`.
- **Affix rarity upgrade (diamonds):** Common→Uncommon 5, →Rare 17, →Epic 57,
  →Legendary 196.
- **Lock** an affix to protect it from rerolls. **Auto-reroll** can stop on a
  chosen affix.
- **Fusion:** three items of one tier make one of the next tier. It always
  succeeds. Fusion-added affix keys cannot be rerolled and carry no stats.

---

## 5. Set bonuses

Owners: `Engine/ArmourSetRegistry.cs`, `Domain/Combat/SetBonusEngine.cs`.

### 5.1 The sets

Each region has two armour families, 10 sets in total. A family is the first
word of the piece id. Only the five **armour** slots belong to a set. Weapons,
amulets and rings belong to none.

| Region | Offensive set (odd id) | Defensive set (even id) |
|---|---|---|
| 1 | Linen (1) | Steel (2) |
| 2 | Hunter (3) | Sentry (4) |
| 3 | Magus (5) | Obsidian (6) |
| 4 | Brawler (7) | Monolith (8) |
| 5 | Doom (9) | Dread / Dreadnought (10) |

### 5.2 Tiers and quality scaling

The tier comes from the number of pieces of one set you wear: **2 pieces = tier 1,
3-4 pieces = tier 2, 5 pieces = tier 3**.

The bonus is multiplied by a **quality scale**:
`average quality tier of those pieces / 4`, clamped to 0.4-2.0. A set of Rare
(tier 4) pieces gives ×1.0. All-Normal gives ×0.4. Ancient (tier 8 or higher)
reaches the ×2.0 cap.

| Tier | Offensive set | Defensive set |
|---|---|---|
| 1 (2 pieces) | +8% damage × scale | +10% armour × scale |
| 2 (3-4 pieces) | +15% damage × scale | +18% armour × scale |
| 3 (5 pieces) | +26% damage × scale, **plus Burn** | +32% armour × scale, **plus Thorns and Damage cap** |

- **"Fire" damage** multiplies net damage after armour, on every connecting swing.
- **Burn:** an extra 25% of the swing's net damage.
- **Thorns:** reflects 20% of each hit you take back at the monster.
- **Damage cap:** no single monster hit can exceed 20% of your max HP.

You can wear two sets at once. For example, 3 + 2 pieces gives tier 2 of one set
and tier 1 of the other, and the bonuses add.

---

## 6. Pets

Owners: `Domain/Progression/PetRegistry.cs`, `PetEngine.cs`.

- Each **character** has 1 pet slot. A second slot opens at **5 rebirths**.
- A pet's bonuses go into the same totals as gear. Damage adds to the weapon
  damage-% group. Crit damage raises the crit multiplier. Drop chance is loot
  luck. Max HP and armour multiply the whole pool.
- A duplicate pet pays **50 diamonds** instead.

| Pet | Source | Bonuses |
|---|---|---|
| Ghostie | Samhain shop | +5% combat XP |
| Pixie | Samhain shop | +5% gold from kills |
| Skeleton Dog | Samhain shop | +5% gathering speed |
| Black Cat | Samhain shop | +5% loot luck |
| Wolf Pup | Samhain shop | +5% crit damage |
| Witch | Samhain rare drop, 1 in 20,000 per event currency earned | +8% world boss damage, +8% loot luck |
| Mini Vampire | Samhain boss | +10% damage, +10% world boss damage |
| Hugin | Monster pet | +8% combat XP |
| Nisse | Monster pet | +8% gold |
| Cú Sídhe | Monster pet | +8% damage |
| Cait Sídhe | Monster pet | +8% crit damage |
| Ignis Fatuus | Monster pet | +8% loot luck |
| Kikimora | Monster pet | +8% gathering speed |
| Bäckahäst | Monster pet | +8% world boss damage |
| Llamhigyn y Dŵr | Monster pet | +8% health |
| Ogham Monolith | Monster pet | +8% armour |
| Dagda's Cauldron | Monster pet | +8% gathering yield |
| Phoenix | 10th rebirth | +8% damage, +8% health, +8% combat XP |
| Black Serpent | World boss | +12% damage, +12% crit damage |

Monster pets drop at **1 in 100,000 per kill**, any monster, as a uniform pick
from the ten.

---

## 7. Combat formulas

Owners: `Engine/CombatDamageModel.cs`, `Domain/Combat/SimulationEngine.cs`.

**Your swing**

- **Attack interval:** `1500 ms × (1 − min(attackSpeed%, 60%))`, minimum 200 ms.
  Relentless then cuts it by another 1% per level.
- **Hit chance:** `(100 + accuracy) / (100 + monster dodge)`, clamped 5-95%.
- **Crit chance:** crit chance + Precision root, clamped 0-100.
- **Crit multiplier:** `1.5 + crit damage%`. Crit damage comes from crit_dmg
  affixes, Finesse milestones and pets. The Cruelty root and Guile bough also
  raise it.
- **Monster armour:** `damage × K / (K + armour)`, with `K = 30 × region +
  your armour pen`. The minimum is 1 damage.
- Then × codex damage multiplier, × set fire %, + burn, + lifesteal.
- **Lifesteal** heals `net damage × lifesteal%`, capped at **1% of max HP per hit**.

**The monster's swing**

- **Hit chance:** `100 / (100 + your dodge) − 5%`, clamped 5-95% (since
  2026-10-29; before that there was no −5%, so the 95% clamp swallowed the first
  5.26 points of dodge).
- **Crit chance:** 5% + 0.5% per region. **Crit multiplier:** 1.5 minus crit
  mitigation%, minimum 1.0.
- **Your armour:** `K = 40 × 3^(region−1)`. Region 1 is 40, region 5 is 3,240.
- **Block:** then removes `block%` of what is left, capped at 75%.
- The minimum hit is 1 HP. The Dreadnought cap limits a hit to 20% of max HP.
  Thorns reflects 20%.

---

## 8. Skill tree

Owners: `Engine/SkillTreeRegistry.cs`, `Engine/SkillTreeEngine.cs`.

### 8.1 Shape

The tree has five limbs. Each limb has one **root**, a fork of two **boughs**
(you may take **only one**) and one **crown**.

| Ring | Max level | Cost | Unlock |
|---|---|---|---|
| Root | 10 | 1 point per level for levels 1-5, 2 points for 6-10 (15 total) | — |
| Bough | 8 | 2 points per level (16 total) | Root at level 5. The other bough must be 0. |
| Crown | 1 | 12 points | The chosen bough at level 5 |

A full limb costs 15 + 16 + 12 = **43 points**. All five limbs cost **215**.

### 8.2 The 20 nodes

| Limb | Root (per level → max) | Bough A | Bough B | Crown |
|---|---|---|---|---|
| **Fortune** | Loot rarity: +1% loot luck → +10% | Plenty: +1.5% material stack size → +12% | Rarity: +1% rarity elevation → +8% | **Golden Fleece:** every 100th kill drops an item +2 tiers |
| **Giantslayer** | World boss damage: +2% → +20% | First Blood: −4% of the first-clear boss HP penalty per level | Trophy Hunter: +2.5% boss gold → +20%, and bosses always drop a material | **Thunderer:** a free opening blow at 500% weapon damage against a boss |
| **Precision** | Crit chance: +0.4 → +4 | Guile: +3% crit damage → +24% | Relentless: −1% attack interval → −8% | **Double Strike:** a 25% chance a crit lands twice |
| **Cruelty** | Crit damage: +3% → +30% | Bloodthirst: +0.5% damage dealt as healing → +4% | Fortitude: +2% max HP and +2% armour → +16% each | **Last Stand:** once an hour, a killing blow leaves you at 1 HP |
| **Insight** | XP: +0.4% → +4% | Craft: −2.5% craft time → −20% | Harvest: +2% gathering speed → +16% | **Scholar:** +25% offline rate |

### 8.3 Where points come from

| Source | Points |
|---|---|
| Each level | 1 |
| Each Book of Deeds Seal (5 seals) | 2, kept forever and re-granted after a rebirth |
| Each rebirth, up to 10 | 2 |

At level 100 with all seals and 10 rebirths, that is about 99 + 10 + 20 =
**129 points**, enough for three complete limbs. The tree is designed so you
cannot buy all of it.

**Respec:** all or nothing. One free respec per run, which a rebirth restores.
Extra respecs are paid grants, and unused grants survive a rebirth. A rebirth
wipes the whole tree.

---

## 9. Codex

Owner: `Engine/CodexEngine.cs`.

- Each monster has a codex level = **kills of that monster / 10**, uncapped.
- The **level sum** across all monsters drives two account-wide multipliers:

| | Formula | Cap |
|---|---|---|
| **Damage** | `1 + 0.04 × √levelSum` | none (diminishing) |
| **Yield** (gathering and material drop roll count) | `1 + 0.005 × levelSum` | **2.0×**, reached at 200 levels (~2,000 kills) |

| Level sum | 10 | 100 | 500 | 2,000 | 14,178 |
|---|---|---|---|---|---|
| Damage × | 1.13 | 1.40 | 1.89 | 2.79 | 5.76 |

- The codex damage multiplier applies after monster armour, on every connecting
  swing.
- The codex is also where a **region boss's first kill** unlocks a playable race
  and marks a region complete. Each completed area gives +1% loot luck.
- **Race mastery** also levels in the codex worker. XP to the next level is
  `500 × L × 1.32^L` (section 13.2).
- The rebirth reset does not touch codex entries.

---

## 10. Rebirth

Owners: `Engine/RebirthRules.cs`, `Engine/SeasonalRotationEngine.ResetPlayersAsync`.

You can rebirth at **any level**. You must type `REBIRTH` to confirm. The
account limit is 20 rebirths. Only the first 10 pay anything besides the
bloodline ceiling.

### 10.1 What a rebirth takes

- Level → 1, XP → 0. Attributes → 0, unspent attribute points → 0.
- Skill tree wiped. Skill points reset to `2 × seals + 2 × min(rebirths, 10)`.
- All gear deleted, except pieces you pay to keep: **5M (Normal) up to 100M
  (Transcendent)** gold, geometric in tier (5, 6, 8, 10, 13, 16, 20, 25, 32, 40,
  50, 63, 79, 100 M).
- Gold → 0, all materials deleted. Crafting slots, Workshop commissions in
  progress and unescrowed market listings are deleted.
- Potions are cleared. Every character is unequipped, set idle and reset to a
  fresh adult.
- Village newcomers are wiped. The arrival clock and recruitment price reset.

### 10.2 What survives

Village buildings, race mastery, inheritance levels (diamond purchases), the
lineage roster (culled to the Hall of Ancestors cap, 10 base / 14 bought / +1
from a Great Work), seals, codex, pets, legacy perks and paid respec grants.

### 10.3 What it pays

| Reward | Rule |
|---|---|
| **Renown damage** | Only for a rebirth taken at **level 50 or higher**. `floor(15 × (1 − 0.8^n))`, n ≤ 10. Values for n = 1-10: **3, 5, 7, 8, 10, 11, 11, 12, 12, 13%**. It never reaches 15. |
| Skill points | +2 per rebirth, up to 10 rebirths (+20) |
| Re-level XP | +50% combat XP on every level below your best-ever level |
| Second pet slot | at 5 rebirths |
| Avatars | Jarilo (3), Vesna (5), Étaín (7), Morana (10) |
| Phoenix pet and title | at the 10th rebirth |
| Bloodline ceiling | `20 + 30 × rebirths / 20`: 0 → 20, 5 → 27, 10 → 35, 15 → 42, 20 → 50 |
| Free skill respec | restored |

Renown adds to the same group as inheritance Damage and the guild Damage buff.

---

## 11. Breeding, aptitudes and traits

Owners: `Engine/BreedingAptitudes.cs`, `Engine/BloodlineBonuses.cs`,
`Engine/TraitRegistry.cs`, `Engine/TraitTotals.cs`, `Domain/Progression/AgePhaseCurve.cs`.

### 11.1 The four aptitudes

Each lineage member has four aptitudes, 0-50. A new character starts at 4 in each.

| Aptitude | Effect |
|---|---|
| Strength | × attack (last multiplier group) |
| Skill | + gathering speed % |
| Endurance | × max HP |
| Fortune | + loot luck % |

A point's value falls off in three bands: 1.5% each for points 1-20, 0.7% for
21-35 and 0.3% for 36-50.

| Points | 4 | 10 | 20 | 25 | 35 | 50 |
|---|---|---|---|---|---|---|
| Bonus | 6% | 15% | 30% | 33.5% | 40.5% | 45% |

### 11.2 How a child is made

1. **Inheritance per aptitude:** `P(father's value) = father / (father + mother)`.
   Crossing a strong fighter with a strong gatherer tends to give a child good
   at both.
2. **Selection** (Breeding Grounds): at level 4 / 7 / 10 you may pick 1 / 2 / 3
   aptitudes. A picked aptitude takes the better parent's value outright. You
   can never pick all four.
3. **Mutation:** +1 at `25% + Grounds level`, −1 at 10%. An **inbred** pair
   swaps the two chances, so building the Grounds makes inbreeding worse.
4. **Epic mutation:** 5% chance (1% if inbred). It gives +1 to every aptitude,
   and the lineage permanently gets ×1.05 on all four attributes and ×1.05
   attribute points per level.
5. **Ceiling:** a child cannot exceed the account's bloodline ceiling (section
   10), unless a parent is already above it.

**Villagers** arrive with `2 + random(0 .. Inn level × 1.5)` per aptitude, never
above 20. So 0-20 is the village phase, and 20-50 needs mutation over
generations plus rebirths to raise the ceiling.

### 11.3 Heritable traits

A character has at most 3 traits. Totals per stat are capped between −15 and +20.
A related pair has a 60% chance of a visible flaw.

| Trait | Rarity | Effect |
|---|---|---|
| Stout Heart | Common | +5% max HP |
| Keen Edge | Common | +4% attack |
| Quick Hands | Common | +5% gathering speed |
| Green Thumb | Common | +5% gathering yield |
| Iron Blood | Rare | +8% max HP |
| Hawk Eye | Rare | +3 crit chance |
| Swift Blood | Rare | +6% attack speed |
| Nimble | Rare | +4 dodge |
| Blood of Kings | Legendary | +10% attack |
| Wolf's Hunger | Legendary | +3% lifesteal |
| Fae Touched | Legendary | +4% rarity elevation |
| Thin Blood | Flaw | −6% max HP |
| Faint Heart | Flaw | −5% attack |
| Clumsy Hands | Flaw | −6% gathering speed |

### 11.4 Age

A character ages only while it works, online and offline. The thresholds are in
hours of work.

| Phase | Hours | Combat |
|---|---|---|
| Child | 0-1 | — |
| Adult | 1-40 | ×1.00 |
| Senior | 40-80 | ×0.95 melee damage, HP, attack speed |
| Elder | 80+ | ×0.90 |

The Inn's maturation bonus shortens childhood.

---

## 12. Inheritance (diamond store)

Owner: `Engine/InheritanceRegistry.cs`.

There are six stats. Each gives **+2% per level, up to level 20 (+40%)**. The
cost is `floor(40 × 1.28^level)` diamonds: 40, 51, 65 … 4,355 for the last level.
A full stat costs **19,758 diamonds**. Inheritance survives rebirth.

| Stat | Where it applies |
|---|---|
| Damage | attack (Renown / guild group) |
| Max health | the whole HP pool |
| Experience | XP multiplier |
| Gold | kill gold |
| Loot luck | loot luck |
| Gathering yield | +2 points of harvest rolls per level, added to the yield pool before the codex multiplier (`SimulationEngine.GatheringYieldFor`, live and offline) |

---

## 13. Other sources

### 13.1 Race innate passives

The **slot 1** character's race is active.

| Race | Innate effect |
|---|---|
| Human | +5% gold |
| Vila | +10 dodge, −30% base armour (also +20% "ranged damage", which nothing reads) |
| Draugr | +25% base max HP, +15% base armour, −15 attack speed (clamped: never slower than the 1500 ms base) |
| Kobold | +30% ore duplication |
| Moosleute | +20% woodcutting yield |
| Vodnik | +15% HP regen, +10% crit mitigation |

All race numbers live in `Engine/RaceBonusRegistry.cs`; `StatsCalculator` and
`RaceMasteryResolver` read them, and `Describe()` builds the player-facing text
from the same constants (`GET /api/v1/races`, shown in the Wiki and the
Progression race-mastery panel).

### 13.2 Race mastery milestones

Vila and Draugr mastery pay only while a character of that race is active.
The other four are read off the account's mastery level and pay every
character.

| Race | Level 10 | Level 25 | Level 50 |
|---|---|---|---|
| Human | +12% XP | +5 vault slots | +15% XP |
| Vila | +15 attack speed | — | +8 crit chance |
| Draugr | 10% lifesteal | — | 12% lifesteal |
| Kobold | +20% ore duplication | — | — |
| Moosleute | +16% double harvest | — | — |
| Vodnik | — | offline cap 18 h | — |

### 13.3 Potions

- **Offensive**, item region tier T: +10T melee damage, +5T armour pen.
- **Defensive**, item region tier T: +100T HP, +5T armour, +T dodge (was
  +0.01T until 2026-10-29).
- **Food buff:** regenerates `maxHP / FoodRegenDivisor` per tick in combat.

### 13.4 Guild and legacy

- **Guild buffs:** Damage, Exp, Gold and DropRate each give **+2% per tier**
  while active.
- **Legacy perks** (legacy points): XP, Gold and Combat speed give **+1% per
  rank**, up to 50. Rank r costs `20 + 8r`. "Combat speed" multiplies damage.
  It does not change the swing interval. No screen in the web client buys
  legacy perks (`purchaseLegacyUnlock` in `commands.ts` has no caller), so the
  misleading name is not shown to anyone today.

### 13.5 Mentorship

Below level 50, each mentor adds +5% XP.

---

## 14. Stats on the Character screen

Every stat number on the Character screen comes from the server's own
`ComputeCharacterCombatStatsWithSetsAsync` per roster character, plus
`CharacterStatSheet`. The client never recomputes them.

---

## 15. Quick "where does X come from" index

| Stat | Sources |
|---|---|
| Attack | base + level, STR×2, gear attack × quality, offensive potion, weapon dmg% affixes, Might milestones, pet Damage, inheritance, guild, Renown, legacy, Strength aptitude, traits, sets, codex, age |
| Max HP | level curve, CON×15, flat_hp affixes, defensive potion, Draugr, Vigour milestones, inheritance, pets, Fortitude, Endurance aptitude, traits, age |
| Armour | CON, gear defence × quality, flat_armor affixes, defensive potion, race, Thick Skin, pet Armour, defensive sets, Fortitude |
| Crit chance | DEX curve, crit affixes (weapon, helmet), Lucky Strike, Vila mastery, Hawk Eye, Precision root |
| Crit damage | crit_dmg affix, Finesse milestones, pets, Cruelty root, Guile |
| Attack speed | DEX curve, attack speed affixes, Finesse milestones, Swift Blood, race, Relentless, age |
| Dodge | dodge affixes, Vila, Nimble, defensive potion |
| Block | CON curve, ring block affixes |
| Lifesteal | weapon affix, Wolf's Hunger, Draugr mastery, Bloodthirst |
| Loot luck | 3.4 |
| Rarity elevation | 3.4 |
| XP | 1.2 |
| Gold | Human, Golden Touch, pets, inheritance, guild, legacy, Trophy Hunter (bosses) |
| Gathering | tool affixes, Skill aptitude, traits, pets, Prospector, Kobold/Moosleute, Harvest, Plenty, codex yield |

---

## 16. Defects found while writing this

Each item was checked by grepping for readers of the field. Status at the
end of 2026-10-29:

| # | Status | Where |
|---|---|---|
| 1, 2 | FIXED, live | #197 |
| 3 | FIXED, live | #199 |
| 4 | attack speed FIXED, live (#199); dodge + potion in PR #200; Fairy's ranged damage open | TASK_BOARD 124 |
| 5 | FIXED | #207 |
| 6 | open, design | TASK_BOARD 125 |
| 7 | kept on purpose | - |
| 8 | open, design | TASK_BOARD 125 |
| 9 | FIXED | #206 |
| 10 | yield and rare find FIXED, live (#202); speed open | issue #203, TASK_BOARD 121 |
| 11 | labels FIXED, live | #197 |
| 12 | open, balance | TASK_BOARD 127 |
| 13 | open, design | TASK_BOARD 125 |

`StatAuditTests` (task 126, #208) now fails the suite when a stat has no
consumer or a percentage affix pays a different number than its tooltip, so
a new item of this kind is caught in CI rather than by reading code.

1. **Inheritance "Gathering yield" does nothing.** `Inherit_GatheringYield` is
   bought with diamonds, persisted and broadcast. No gathering or yield
   calculation reads it. The other five inheritance stats each have a reader.
   Players can spend up to ~19,758 diamonds on it for no effect. **FIXED
   2026-10-29:** read in `GatheringYieldFor`, so live harvests, the offline
   projection and the stat sheet all pay it (`StatsReferenceFixTests`). In
   production two players had bought one level each (40 diamonds apiece).
2. **Fortitude (Cruelty bough) gives HP only.** The registry comment and the card
   say "+2%/lvl max health and armour". `SimulationEngine.EffectiveMaxMilliHpFor`
   is the only reader, and it applies health only. **FIXED 2026-10-29:**
   `SkillTreeRegistry.ApplyFortitudeArmour`, applied in
   `SimulationEngine.LiveCombatStats` (tick, offline, stat sheet, broadcast)
   and to the roster's per-character armour.
3. **Draugr mastery lifesteal is probably 100× too small.**
   `GetDraugrLifestealBonusPct` returns 10 / 12 (percent). `StatsCalculator`
   divides by 100 before adding it to `LifestealPct`, which is already in
   percent. The result is 0.10% / 0.12% instead of 10% / 12%. The 1%-of-max-HP
   per-hit lifesteal cap limits the effect either way. **FIXED 2026-10-29**
   (owner chose 10 / 12 from the `RaceUnitMeasurementTests` table: the cap
   almost never binds).
4. **Unit leftovers in race effects.** Vila mastery "+0.15 attack speed"
   ("nullify armour agility penalty") and Draugr "−0.15 attack speed" add 0.15
   *percentage points*, so they have almost no effect. The defensive potion's
   dodge is `tier × 0.01` points, at most 0.05 (**FIXED 2026-10-29**: one point
   a tier, and the hit curve no longer ignores the first 5.26 points). Vila's +20% `FlatRangedDamage`
   applies to a stat combat never reads (only the guild-war aggregate sums it).
   The ±0.15 lines date from when `CombatDamageModel` read attack speed as a
   FRACTION (see its "read as a fraction" comment), so 15% was the author's
   intent. **Attack speed FIXED 2026-10-29** to ±15 (owner's choice). The
   potion dodge and Vila's ranged damage remain open; the dodge question is
   bigger than the potion, because `MonsterHitChance` clamps at 95% and so
   ignores the first 5.26 points of ANY dodge.
5. **Dead set-bonus fields.** `SetBonusResult.FlatAttackPowerBonus` and
   `CooldownReductionActive` are never assigned, so they are always 0 / false.
   `StatsCalculator` still adds the flat attack bonus. **FIXED 2026-10-29**
   (task 122): both removed. The flat +10 was replaced on purpose by scaled
   fire damage in 56894e46 ("quality, not collection"); cooldown reduction
   served active skills, which are gone. Neither was persisted or on the wire.
6. **Tank lineage is unreachable.** Every account is Warrior, and no command
   changes `SelectedLineageId`.
7. **Percentage affixes do not scale with region.** A region-1 Legendary crit
   affix equals a region-5 one. Only flat HP and armour scale with region. This
   may be intended, but it is not documented anywhere else.
8. **The ring has one legal affix (block).** A high-tier ring stacks 4-5 block
   rolls (up to ~20% block at Legendary rarity, within the 75% block cap).
9. **Stale comments.** The `CritMitigationPct` comment in `StatsCalculator` says
   "not yet consumed". `MonsterCritMultiplier` now consumes it.
   `EquipmentLevelGate` is dead code. **FIXED 2026-10-29** (task 123): the
   comment tells the truth and the class is deleted.
10. **Tool gathering-yield and rare-find affixes do nothing.** Found
    2026-10-29. `ToolLoadoutResolver` sums `gather_yield_pct` and
    `gather_rare_find_pct` into `ToolGatherYieldPct` / `ToolRareFindPct`, which
    are stored, swapped per slot and broadcast, and read by no simulation code.
    Only `gather_speed_pct` reaches `RequiredGatherTicks`. **FIXED
    2026-10-29:** yield adds `ToolGatherYieldPct / 10` points to the yield pool,
    rare find adds `ToolRareFindPct / 10` to the luck that sets
    `LuckWeightBonus` (the rare-favouring weight), live and offline.
    **Still open, owner's call:** `gather_speed_pct` is added to the speed bonus
    as WHOLE percent (`GatheringToolEngine.TotalSpeedBonusPct`), while the
    tooltip and every other affix read the magnitude as tenths - a Legendary
    roll shows +5.5% and pays +55% (issue #203). Yield and rare find are per
    tool since 2026-10-29 (axe woodcutting, pickaxe mining, rod fishing);
    speed is still one sum across all three tools, which goes with #203.
11. **The weapon damage affixes are one stat with three names.**
    `melee_dmg_pct`, `range_dmg_pct` and `magic_dmg_pct` all add to the same
    damage bonus. The client now labels all three "Damage" (2026-10-29); the
    ids stay, because their order crosses the wire.
12. **The Clumsy Hands flaw does nothing.** Found by the stat audit on
    2026-10-29. Its -6% gathering speed is dropped by two `> 0` guards in
    `GatheringToolEngine` (`TotalSpeedBonusPct` and `ComputeRequiredTicks`).
    The other flaws apply. TASK_BOARD 127.
13. **Legacy perks can be neither bought nor earned.** No client screen calls
    `purchaseLegacyUnlock`, and legacy shards are credited only by
    `GuildWarEngine` - Guild Wars are parked. Production, 2026-10-29: 0 of 26
    accounts hold a perk; one account holds shards (37,628). TASK_BOARD 125.
