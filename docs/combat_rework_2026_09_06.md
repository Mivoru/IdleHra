# Combat had no identity — 2026-09-06

Reported: *"I think it's bad that every monster is one-shot and then the boss
instakills me... I now deal 73,500 damage to all monsters but I thought it
should scale and my damage should be lower to the higher difficulty monsters in
that location."*

All three observations were correct, and each had a separate, measurable cause.
Two of them were content defects in systems that were already fully built.

## 1. Why the damage was flat at 73,500

`CombatDamageModel.Mitigate` is `raw * K / (K + armour)`. Monster armour was
authored as `10 * regionTier`; `MonsterArmourHalvingConstant` is
`30 * regionTier`. **The tier is on both sides, so it cancels.**

| region | armour | K | mitigation |
|---|---|---|---|
| 1 Field Mouse | 10 | 30 | 25.0% |
| 3 Sandstone Golem | 30 | 90 | 25.0% |
| 5 Malakor | 50 | 150 | 25.0% |

Every monster in the game, all 115, mitigated **exactly 25%**. The stat was
authored, validated at start-up and read on every swing, and was mathematically
incapable of distinguishing any monster from any other.

**`DodgeRating` was 0 on all 25 canonical monsters.** The 68 legacy monsters
(ids 1-90) that the canon replaced *did* carry varied values; the canon that
shipped over them did not. `HitChance` was therefore pinned at its 0.95 ceiling
for every player forever, and `AccuracyRating` — which is DEX — bought nothing
at all.

So within a region the four regulars differed from each other, and from their
boss, **only** in HP and attack power.

## 2. Why everything died in one swing

The codex damage multiplier: `1 + 0.01 * levelSum`, linear and uncapped, where a
codex level is ten kills of one monster. On the reporting account that is 14,178
levels and **142.8x**. A raw swing of ~690 landed as 73,500.

Gear, affixes, set bonuses and the entire fourteen-tier rarity ladder — rebuilt
the day before to be worth a full region step — were together contributing
**0.7%** of the player's damage. Nothing underneath that multiplier could be
felt, which is also why adding armour and dodge without fixing it would have
been invisible.

## 3. Why the boss instakilled

`ProgressionRateTests` had been **printing this table for months without
asserting on it** — the strongest *regular* of a region as a share of the geared
health bar, per second:

| region | geared pool | strongest regular hits | share/second |
|---|---|---|---|
| 1 | 175 | 33 | 9.5% |
| 2 | 960 | 326 | 17.0% |
| 3 | 2,225 | 1,477 | 33.2% |
| 4 | 5,170 | 6,655 | 64.4% |
| 5 | 14,010 | **29,134** | **104.0%** |

Player health was **linear** in level (`base * (1 + pct * level / 100)`, and for
a Warrior lineage `pct` is 0, so flat) with a base that was the constant
`100_000L` at both sites that compute a pool. Monster attack is **geometric**:
40 → 500 → 2,100 → 8,725 → 36,400 for the strongest regular, about 4.2x a
region.

Linear against geometric crosses once and never comes back. By region 5 an
ordinary regular two-shot a fully geared character and Malakor's 118,400 was a
single blow larger than the whole bar — not a difficulty, a wall.

## What changed

**The codex damage multiplier diminishes instead of running away.** A curve, not
a ceiling — chosen deliberately over the hard cap used for the yield multiplier,
because this one feeds a ladder that keeps climbing rather than an economy with
fixed sinks.

| codex levels | 10 | 100 | 500 | 2,000 | 14,178 | 50,000 |
|---|---|---|---|---|---|---|
| old | 1.10x | 2.00x | 6.00x | 21.00x | **142.78x** | 501.00x |
| new | 1.13x | 1.40x | 1.89x | 2.79x | **5.76x** | 9.94x |

The first levels are worth *more* than they were (4% against 1%). It never stops
paying.

**Monsters have a defensive identity, derived rather than authored.**
`MonsterDefenceCurve` computes armour and dodge from a monster's rank inside its
region, and `ContentRegistry.Initialize` overwrites the canonical values with it
— one source of truth, so a flat table cannot creep back through a content edit.

| region 5 | armour | dodge | hit | through armour | effective |
|---|---|---|---|---|---|
| Grave Ghoul | 5 | 174 | 0.95 | 0.968 | 0.918 |
| Fortress Gargoyle | 16 | 180 | 0.93 | 0.904 | 0.839 |
| Dark Necromancer | 23 | 192 | 0.89 | 0.867 | 0.772 |
| Death Knight | 30 | 210 | 0.84 | 0.833 | 0.699 |
| **Malakor** | **62** | 183 | 0.92 | **0.708** | 0.650 |

The deepest regular of a location now takes **76%** of the swing the first one
takes, and with its larger health pool the fight is 3.2x as long: seconds to
kill on arrival gear run **52 / 82 / 117 / 169** across region 1, and
**77 / 96 / 120 / 152** across region 5.

Two design notes worth keeping:

- **The spread runs downward from the old flat 25%.** That is forced, not
  chosen. `Test_Content_EveryMonsterDiesInsideTheAttentionSpan` measures the
  strongest regular against *arrival* gear and caps it at 180 seconds; the first
  attempt, which made the deepest regular tankier, put region 1's fourth monster
  at 246s. So the easy monsters got easier instead.
- **The boss is hard to HURT, not hard to HIT.** Giving a boss both, on top of
  the 5x health it already carries, measured at eleven times a regular fight.
  Armour alone keeps it near 5.3x.

**The health bar grows on the same shape as the thing hitting it.**
`ProgressionEngine.BaseMilliHpForLevel` compounds at **9.2% a level**, which
is about 5.8x every twenty levels — a region. Tuned against the table below
rather than picked: 6.5% and 8% were both measured first and both left region 5
above a third of the bar per second.

| level | 1 | 21 | 41 | 61 | 81 | 101 |
|---|---|---|---|---|---|---|
| old base | 100 | 100 | 100 | 100 | 100 | 100 |
| new base | 100 | 581 | 3,379 | 19,649 | 114,238 | 664,146 |

Level 1 is untouched, so nothing about the opening hour moves. And a Warrior,
whose `HpScalePerLevelPct` is 0, has a health curve at all for the first time.

The survivability table is now flat, and **asserted**:

| region | 1 | 2 | 3 | 4 | 5 |
|---|---|---|---|---|---|
| before | 9.5% | 17.0% | 33.2% | 64.4% | **104.0%** |
| after | 9.5% | 11.3% | 13.4% | 13.5% | **11.4%** |

## Two fixture bugs the change exposed

Both were latent for months and invisible only because dodge did nothing.

`ProgressionRateTests.HowLongARegionTakes` and
`HardenedEngineIntegrationTests.SecondsToKill` both set a character's LEVEL
without giving it the ATTRIBUTES of that level, so STR/DEX/CON/LCK were zero —
a level-1 character holding region-5 gear. The health-pool block in the same
file has documented that exact trap since it was written ("a finding about the
fixture"), and neither of its two neighbours had it fixed.

It did not matter while `AccuracyRating` was inert. The moment monsters started
evading, the unlevelled model missed most of its swings and reported region 5 at
**246 s/kill against a real 60**. Both now level their character.

## Still open

**The lineage HP disparity.** `HpScalePerLevelPct` is 8 for a Tank and **0** for
a Warrior, applied as a percentage of the base — so with a geometric base a Tank
now has roughly 7.5x a Warrior's pool. That is a real choice rather than the
difference between playable and not, which it used to be, but it is a wider gap
than a lineage should probably buy. Not touched here; it is lineage design, not
a combat defect.
