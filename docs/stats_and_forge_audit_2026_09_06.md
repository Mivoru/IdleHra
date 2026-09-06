# The stats pipeline and the forge — audited 2026-09-06

Asked for after the combat rework: *"I would firstly investigate all the stats
logic, not just what you found, also if the equip stats apply like crit chance
from equip"* — plus two concrete forge reports.

The headline: **the affix → stat → combat pipeline is intact.** Every affix
reaches a stat and every stat reaches the fight. The bugs were elsewhere, and
there were four of them.

---

## 1. Attribute growth was lost on every offline level — FIXED

The only account past level 1 is level 86 and its attributes read
**50 / 50 / 50 / 25** — exactly what a fresh registration gets. A Human at 86
should hold 220 STR, DEX and CON.

The game grows levels in **three** places:

| path | levels | skill point | attributes |
|---|---|---|---|
| `ProgressionEngine.ProcessMonsterDeath` (an ordinary kill) | yes | yes | **yes** |
| `SimulationEngine.ApplyBulkExperience` (warp / catch-up) | yes | yes | **yes** |
| `OfflineSimulationEngine.ApplyCombatXp` (while away) | yes | yes | **NO** |

The offline path raised the level, paid the skill point, and granted no STR,
DEX, CON or LCK at all. **In an idle game most levels are gained exactly that
way**, which is why the live account has none of it.

That file's own comments record the same shape twice before — the XP formula
diverged, then the skill point diverged, each fixed with a note saying the path
"must stay identical to the live tick". The attributes were the third.

It matters more than it did last week. **DEX is `AccuracyRating`**, and accuracy
bought nothing while every canonical monster had `DodgeRating 0`. Monsters evade
now, and `MonsterDefenceCurve` prices their dodge against the accuracy levelling
is supposed to provide — so a character stuck at its starting DEX misses swings
the curve assumes it lands. CON is 15 max HP a point on top.

`AttributeGrowthTests` now covers the growth itself, the unknown-race case that
silently grows nothing, and a source-level guard that no levelling path may
raise a level without asking `RaceAttributeGrowth` for the points.

**Not repaired retroactively.** The 85 levels of growth already missed are a
data question, not a code one — see "Open" below.

---

## 2. Auto-reroll's "stop on stat" pointed at the wrong stat — FIXED

Reported as: *"I can choose which stats to focus in the auto reroll, I think
that doesn't work at all — I chose crit chance but the reroll didn't change the
affix at all even if I lowered it to rare and higher and did like 1k attempts."*

It never rolled once.

The stop-on-stat travels as a **1-based index into `AffixRegistry.Definitions`**,
because `ClientCommandPacket` is fixed-layout and cannot carry a string. The
client's `KNOWN_AFFIX_IDS` is therefore not a display list — it is that ordering
written down a second time. The two had drifted:

| index | client sent | server read |
|---|---|---|
| 3 | armor_pen_flat | gather_speed_pct |
| 4 | melee_dmg_pct | gather_yield_pct |
| 5 | range_dmg_pct | gather_rare_find_pct |
| 6 | magic_dmg_pct | melee_dmg_pct |
| **7** | **crit_chance_pct** | **range_dmg_pct** |
| 8 | crit_dmg_pct | magic_dmg_pct |
| 10 | lifesteal_pct | crit_chance_pct |
| 11 | block_chance_pct | crit_dmg_pct |
| 12 | dodge_chance_pct | lifesteal_pct |

Ten of twelve were wrong, and the client list omitted the three tool affixes
entirely. Choosing **crit chance** sent the index the server reads as
`range_dmg_pct`, which is weapon-only — so on an amulet or an armour piece
`IsConditionReachable` refused the whole run before the first attempt. Gold was
never spent and the affix never moved, exactly as reported.

Fixed by making the client list the server's order verbatim, all fifteen
entries. `serverMirrors.test.ts` now parses `AffixRegistry.cs` and compares the
two **element by element**, because the index is the wire format.

---

## 3. Auto-reroll refused to roll away a result it liked — FIXED

Reported as: *"if I do like 10x reroll and get legendary that I don't want I
have to switch to normal rerolling, then approve that I would delete that
legendary, and then go back to automatic reroll."*

`ExecuteAutoRerollAsync` had an early-out:

```csharp
// Already good enough before spending anything.
if (AutoRerollPlanner.IsSatisfied(stopCondition, currentRarity, currentAffixId))
    return AutoRerollStopReason.ConditionMet;
```

It returned without rolling **and without telling anybody** — no command result,
no toast, nothing moved. A Legendary already satisfies "Rare or better", so
pressing the button again did nothing at all, silently, and the only way
forward was to switch to manual.

Removed. Pressing a button that costs gold, on a screen that has already made
you confirm destroying the affix in that slot, is not ambiguous: the press means
roll. The guard against rerolling away something good belongs in that
confirmation — which is where it is, keyed on the affix about to be destroyed —
not in a silent server-side refusal.

`IsTriviallySatisfied` is a different case and stays: a condition that can never
fail would charge for a guaranteed outcome, and it reports itself.

---

## 4. The audit itself: every stat, written and read

The question was whether equipment stats actually apply. They do — this is the
full trace, and nothing came back dead.

**Every affix maps to a total.** `EquipmentSlotEngine`'s switch covers all
twelve non-tool registry ids plus the five legacy numeric keys. The three
damage-type percentages deliberately sum into one accumulator, because this
combat model has one damage number rather than per-type resistances.

**Every total is consumed by `StatsCalculator`** — all twelve, checked one by
one.

**Every `CombatStats` field has a reader.** Two looked orphaned and are not:
`EquipmentDamagePct` and `EquipmentCritDamagePct` are consumed inside
`ComputeEffectiveMilliAttack` and `ComputeCritMultiplier`, which the live tick
and both projections call.

**The live tick reads the defensive half**: `AccuracyRating` (hit),
`CritChancePct`, `LifestealPct`, `DodgeChancePct`, `CritMitigationPct` and
`BlockStrengthPct` all appear in the combat block.

So crit chance from gear *is* applied. The reason it never felt like it was the
codex damage multiplier at 142.8x, which made gear 0.7% of the player's damage —
fixed separately, see `docs/combat_rework_2026_09_06.md`.

---

## Open

**Player 8's missing attributes.** Roughly 85 levels of growth (≈170 STR/DEX/CON
and 85 LCK) were never granted. The fix stops the loss; it does not backfill it.
Backfilling is a one-off write to a live row and should be a deliberate decision
— the arithmetic is `50 + 2 * (level - 1)` for a Human's first three and
`25 + (level - 1)` for LCK.

**The dropdown offers affixes the slot cannot roll.** All fifteen registry
entries are listed, including the three tool ones, and the server refuses an
illegal combination with a validation result. Filtering the list per slot would
need the slot-legality masks mirrored client-side — a new mirror, deliberately
not added.
