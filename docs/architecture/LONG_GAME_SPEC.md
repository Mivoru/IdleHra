# The Long Game: Skill Tree, Book of Deeds, Breeding

Agreed design, 2026-08-08. Three systems that exist to answer one measured
problem, stated first because every number below is chosen against it.

## The problem these three solve

An earlier draft of this document opened by claiming the content was exhausted
in 13 hours of play, and built its whole motivation on that. **That number was
wrong** - it was carried over from a note written on 2026-08-05, before the
rebalance that tripled monster HP and attack, rebuilt the ladder and added the
5x first-clear boss. Recording the correction here because the real numbers
point the opposite way.

`SeasonalRotationEngine.EraDurationSeconds` is **90 days** = 2,160 hours.
`ProgressionRateTests` measures the current content, bare (the model ignores
affixes, STR growth and set bonuses on purpose, and live play runs roughly 3x
its floor):

| Region | s/kill | kills | hours bare | ~3x geared |
|---|---|---|---|---|
| 1 | 62 | 180 | 3.1 | ~1 |
| 2 | 75 | 1,201 | 25 | ~8 |
| 3 | 79 | 9,604 | 212 | ~71 |
| 4 | 77 | 76,748 | 1,648 | ~549 |
| 5 | 69 | 614,287 | 11,802 | ~3,934 |

One hour of play: level 11, 1,529 gold, 53 kills.

So the season is not empty - **region 5 does not fit inside it**. Even at the
geared rate it is 164 days against a 90-day season, and region 4 eats most of
what is left. That is a real balance problem, it is the opposite of the one
assumed above, and it is tracked separately from this document.

What this means for the three systems below: they are **not** here to fill dead
time, because there is none. They are here because the game's only axes today
are level and gear, both of which the rollover wipes - so a season leaves
nothing behind but diamonds. The skill tree gives a season a shape, the Book of
Deeds gives it a checklist that teaches the game, and breeding gives the *next*
season a reason to exist. Judge a change to any of them on that, not on filling
a calendar.

---

# 1. Skill tree - Yggdrasil

## Why the current tree is not a choice

Five branches, twenty levels, `GetUpgradeCost(level) = level/5 + 1`. Filling
everything costs 250 points; a season pays about 100.

That looks like a choice and is not. Every branch is a pure bonus, and the cost
curve is nearly flat (1 to 4). So the optimal play is "pour everything into the
strongest branch, then the next one" - it is an **ordering**, not an identity.
Two players end the season looking the same, one of them just got there first.

Drawing it as a tree does not fix that. The tree needs places where **a door
closes**.

## Structure: three rings

Trunk, five limbs (the existing five branch ids, unchanged), each limb three
deep.

### Ring 1 - Roots

The existing linear node, but capped at **10** levels instead of 20. Cost 1
point per level. No choice here, deliberately: this is the "you want some of
each" layer and it should be cheap.

### Ring 2 - Boughs

Each limb forks into **two** twigs and **only one may be levelled**. Max 8
levels, 2 points per level. Requires Root >= 5.

This is where the choice lives. Taking one twig **locks the other for the
season**.

### Ring 3 - Crown

One keystone per limb. Single level, **12 points**, requires Bough >= 5. Not a
percentage - a qualitative effect that changes how the character plays.

### Budget

| | Cost |
|---|---|
| Full limb (Root 10 + Bough 8 + Crown) | 10 + 16 + 12 = **38** |
| All five limbs | **190** |
| Points per season | ~100 (+2 per Seal, see part 2) |

So a season buys **two full limbs and part of a third**. All five crowns are out
of reach. That gap is the design.

## The five limbs

Existing branch ids keep their meaning; the ring-1 node is what they already do.

### Fortune (`BranchLootRarity`) - +% loot rarity

- **2A Plenty** - +% material drop quantity. Feeds crafting.
- **2B Rarity** - +% chance a dropped item rolls one rarity higher.
- **Crown - Golden Fleece** - every 100th kill guarantees an item at +2 rarity
  tiers. Gives the player a counter to watch, which is the point.

### Giantslayer (`BranchWorldBossDamage`) - +% boss damage

- **2A First Blood** - reduces the first-clear boss penalty by 4% per level
  (`BossFirstClearRules.FirstClearHpMultiplier` 5x drops to ~3.4x at level 8).
  A direct answer to the hardest wall in the game.
- **2B Trophy Hunter** - bosses drop +% gold and +1 guaranteed material.
- **Crown - Thunderer** - boss fights open with a free hit at 500% weapon damage.

### Precision (`BranchCritChance`) - +crit chance

- **2A Guile** - +crit damage.
- **2B Relentless** - -% attack interval.
- **Crown - Double Strike** - a crit has a 25% chance to land twice.

### Cruelty (`BranchCritDamage`) - +% damage

- **2A Bloodthirst** - lifesteal as a % of damage dealt. Saves food, which is a
  real economy in this game rather than a stat.
- **2B Fortitude** - +% max HP and +armour.
- **Crown - Last Stand** - one death per hour is survived at 1 HP.

### Insight (`BranchXpGain`) - +% XP

- **2A Craft** - -% crafting time, +% chance to keep materials on a craft.
- **2B Harvest** - +% gathering speed, +% double-gather chance.
- **Crown - Scholar** - offline progress runs at +25%.

Note what this adds that the tree has never had: **gathering and crafting**. Half
the game is the gathering loop and today it has no decision in the tree at all.

## Respec

**One free respec per season. Every further respec is a real-money purchase
(~2 EUR), not diamonds.**

Deliberately not diamonds: diamonds are the inheritance currency and pricing a
respec in them would make respeccing compete with permanent progression, which
turns a convenience into a tax on the long game.

Paid respecs are **per season**, never permanent. A permanent unlimited respec
would delete ring 2's exclusivity, which is the only real choice in the tree.

### Build it in two halves

1. **Now:** `FreeRespecUsed` and `PaidRespecGrants` counters on the account, and
   the gate in the tree. This works immediately - the first respec goes through,
   the second says "spent".
2. **Later:** the purchase flow increments `PaidRespecGrants` and touches nothing
   else.

Practical note: purchases exist for mobile via Capacitor (App Store / Play take
15-30%). **There is no payment provider wired on web** - a 2 EUR web purchase
means Stripe: checkout, webhook, receipt validation, refunds, EU VAT/OSS. Not
hard, but it is its own piece of work and the gate must not wait on it.

## Drawing it

Trunk, five limbs, each forking at ~60% of its length. The untaken twig is drawn
as a thin bud; once the other is taken it **greys out and shows a lock**. The
crown bud glows when it becomes reachable.

The tree's *shape* must not change as it fills - only thickness and colour. A
player has to see what they are choosing between before they choose.

## Implementation note (corrected)

An earlier draft of this plan called for refactoring the tree onto a packed
array of node levels first, on the assumption that each new node was expensive.
**That refactor is not needed**, and the reason is worth recording:

- `player_skill_tree` is already **one row per (player, branch)**, so a new node
  is a registry entry, not a migration.
- `client_web/scripts/generate-protocol.mjs` derives the client's view from the
  server DLL via `--dump-protocol`. There is no hand-written mirror to update.

So a node costs: one byte on `StateUpdatePacket` (plus
`NetworkPacketLayoutGuard.ExpectedStateUpdateSize`), one byte on
`TickStatePayload` (server-side only, free), a registry entry, and the effect
that consumes it. The effect is the only real work, and it is irreducible.

15 new nodes is ~15 bytes on a 747-byte packet. The historical "700-byte
ceiling" in the guard's comments has already been passed (711, 746, 747) and is
documentation, not a limit.

---

# 2. Book of Deeds (achievement tree)

## What exists today

Four families in `AchievementMilestones`, tiers I-IV, paying `PremiumDiamonds`;
only Logistics also pays a stat (`LogisticsGatheringSpeedBonusPct`, +1/2/4/8%).

Three problems:

1. It is a **list**, not a tree. Nothing leads to anything.
2. It **teaches nothing**. A new player is told none of the game's order.
3. The thresholds are **unreachable**. Treasury tier IV is 2.5 billion gold;
   `ProgressionRateTests` measures region 4 as yielding 53M gold over 1,648
   hours, so tier IV is roughly two full seasons of uninterrupted region-4
   farming spent on nothing else.

## Structure: chapters and Seals

Five chapters of ~6 deeds. A chapter opens when the previous one completes.
Completing a chapter awards a **Seal**, and Seals are permanent across seasons.

### I - The Village Road (open from the first login)

Kill 10 monsters - equip a weapon - cook and eat a meal - gather 100 wood -
craft one item - fill the Auto-Eat larder.

Reward: Seal + a set of Common tools.

**This chapter is the interactive tutorial.** Onboarding expressed as content
with rewards, instead of popups that get clicked away, and it teaches the order
of operations rather than the location of buttons. It is the highest-value item
in this entire document per hour of work.

### II - Smiths

50 fusions - produce a rarity-8 item - 20 affix rerolls - activate a 2-piece set
bonus - Forge to level 5 - craft from 3 different materials.

Reward: Seal + a guaranteed Rare tool.

### III - Hunters

Kill each of region 1's five monsters 100x - first-clear a boss - reach level 40
- 5,000 kills - survive a fight below 10% HP - complete one region's codex.

Reward: Seal.

### IV - Stewards

Village building levels - gathering totals - cooking totals - warehouse capacity
- 1M harvests.

Reward: Seal + permanent +% gathering.

### V - The Ledger of Legends

Finish a season in the top 50 - activate a 3-piece and a 5-piece set bonus -
reach level 100 - first-clear region 5's boss - raise a child - own an
epic-mutation child.

Reward: Seal + an inheritance discount.

## What Seals buy

**Each Seal grants +2 permanent skill points, every season, forever.**

This is the load-bearing decision of the whole document, because it couples the
two systems: the tree gains a **second source of points**, earned by *exploring*
the game rather than by levelling. Five Seals is +10 points against a base of
~100 - felt, but not decisive.

## Two non-negotiables

1. **Every deed shows a live x/y counter.** Today `GetNextTierTarget` returns 0
   for most ids and the client renders "0 / MAX". A deed without a number does
   not exist to the player.
2. **Thresholds recalibrated against measured pacing.** Treasury's top tier
   belongs near 50M, not 2.5G.

Keep tiers (I-IV) for counters - kills, gold, harvests. Use binary for firsts -
first boss, first set bonus. Mixing the two reads better than forcing either.

## The unlock toast

Steam-style, because the moment of earning is most of the reward.

- **Look:** slides in bottom-right (bottom-centre on mobile). Parchment card,
  brass frame, deed icon left, name + tier, reward beneath. A **light sweep**
  crosses the card once (~600ms) and the border glows. Holds 4s, then drifts out.
- **Queue:** threshold crossings arrive in clusters. Cards stack ~400ms apart,
  max 3 visible, the rest wait. They must never overlap.
- **Sound:** one new clip, `achievement_unlock`, in the existing
  `Resources/Audio` registry. Respects master/SFX volume and mute.
- **Reduced motion:** no sweep, fade only.
- **Backgrounded tab:** no sound, and do not replay a backlog on return.

**No new wire field is needed.** The client already receives the achievement
flag bitmask; it diffs against the previous value and fires a toast per newly
set bit.

---

# 3. Breeding

## What exists today

`BreedingEngine` requires both parents at `AgePhase >= 1` and `Level >= 50`,
splices a packed `GeneticVector`, rolls a 5% epic mutation, and the child is born
at `AgePhase 0`.

Two things are missing: the child **cannot be felt** (the genome barely does
anything), and there is **no decision** anywhere in it.

## Aptitudes

Four values per lineage member: **Strength** (combat), **Skill**
(gathering/crafting), **Endurance** (HP/armour), **Fortune** (luck).

### Inheritance: one parent per aptitude, weighted by that parent's value

For each aptitude independently:

```
P(from father) = fatherValue / (fatherValue + motherValue)
```

Father at Strength 12 against mother at 4 means a 75% chance the child inherits
the 12.

### The emergent property this creates - and it is the best part of the design

Father `(12, 4, 4, 4)` (a fighter) crossed with mother `(4, 12, 4, 4)` (a
gatherer):

- Strength: 75% from the father -> likely **12**
- Skill: 75% from the mother -> likely **12**
- The rest stay at 4

The child is `(12, 12, 4, 4)`. **Crossing two specialists produces someone good
at both.** So the strategy discovers itself, and it is real husbandry: you do not
want two similar parents, you want two different ones.

This is why the design uses weighted parental inheritance instead of a
player-picked "focus" - the player's choice happens when *selecting the pair*,
which is a more interesting decision than a button labelled "combat".

### Mutation, because inheritance alone dead-ends

A child always receives one parent's exact value, so it can never exceed the best
value already in the lineage. Without mutation the bloodline freezes after two
generations, permanently.

After inheritance, per aptitude: **25% chance +1, 10% chance -1.** Net drift
+0.15 per aptitude per generation.

### Inbreeding is degraded, not forbidden

Up to 14 lineage members carry across a rollover, so nothing structural stops a
player from crossing their own children forever and never touching the village
again. That would make the entire gene pool pointless.

If a pair **shares a parent or a grandparent**: mutation inverts to **10% +1 /
25% -1**, and epic mutation drops from 5% to **1%**.

So it is possible when convenient but no strategy can be built on it, and fresh
village blood stays valuable permanently. Requires a parent-id pair stored per
lineage member and a two-level ancestry check.

### Value per point: diminishing

| Points | Per point | Cumulative |
|---|---|---|
| 1-20 | 1.5% | +30% |
| 21-35 | 0.7% | +40.5% |
| 36-50 | 0.3% | **+45%** |

**Absolute cap 50.**

Flat 1.5% to a cap of 50 would be +75% in one domain - a veteran roughly twice a
newcomer's strength in everything, on a shared seasonal leaderboard. That would
make the board a function of account age rather than of how the season was
played, which is the exact failure seasons exist to prevent.

Diminishing returns keep the veteran advantage **visible but not decisive**: +45%
at the absolute cap against +30% for a settled player at 20.

### Epic mutation

5%, unchanged. Now worth chasing: **+1 to all four aptitudes**, a gold name and a
crown on the portrait.

## Two-phase climb

This is the shape the whole system is tuned around.

**Villager aptitudes roll `2 + random(0 .. Inn level)`, capped at 20.**

- **0 -> 20: village-driven.** Build the Inn, better people arrive, marry good
  blood in. Achievable within a few seasons and it feels fast.
- **20 -> 50: lineage-driven.** The village cannot take you here. Only mutation
  and selection across generations, over roughly 8-12 seasons - which at 90 days
  a season is **two to three years**. An absolute cap should be an asymptote
  almost nobody reaches.

## Pairing

The standard pair is **hero x villager**. The hero must be level 50; the villager
only has to exist and be adult.

Requiring level 50 of both parents would mean levelling two characters to 50 for
one child - double the grind for a single roll of the dice.

## Cooldowns

- **Conception** is instant but puts **both parents on a 24h cooldown**. Since the
  hero is one parent nearly every time, this is a natural cap of **one child per
  day** and no separate global limit is needed.
- **Gestation is 8 hours of real time**, and it ticks offline. Chosen to match a
  night's sleep: conceive in the evening, meet the child at the morning login.
  It pairs with the existing offline summary card.

The hero's level-50 gate means breeding starts late in a run, so a season yields
a handful of generations rather than dozens.

## The child starts at level 1 - and this is why it costs nothing

A level-1 child looks like a punishment. It is not, because of when it happens:

> You breed at the **end** of a season, when the hero is maxed. The child is born
> at level 1 and is the character you **begin the next season with** - where
> everyone starts at level 1 anyway.

The penalty lands exactly where everything resets. And it produces the loop the
game has never had:

1. Start the season with your best child - level 1, but carrying the bloodline
2. Play, level, gear up
3. Late season, breed: pick pairs from the lineage and the village
4. Season ends: gear and levels wiped, **the bloodline persists**
5. The next season starts with better blood

That is the reason to come back for season two, and it is what fills the
eighty-three days.

---

# 4. What crosses a rollover

**Carries:** lineage members (**born children only**), their race, aptitudes,
generation number, epic-mutation flag. Diamonds and inheritance. Seals.

**Does not carry:** village building levels, stored materials, **recruited
villagers**, gold, gear, character levels.

## Why building levels do not carry

The village is that season's own progression curve. If it carried, a third-season
player would skip the entire mid-game and a new player could never close the gap.
That is precisely the death a seasonal reset exists to prevent.

A "Foundations" discount (a permanent village-cost reduction based on the best
level ever reached) **was considered and rejected**: every permanent discount is
a head start that cannot be caught.

Which means rebuilding the village each season needs its own reason - and it has
one. **The Inn sets the quality of the gene pool.** You do not rebuild the
village because you must; you rebuild it because *this* season's village decides
what blood you can marry into *this* season's bloodline. The goal is different
each season because the lineage is different.

## Why only born children

A recruit is seasonal labour; family is permanent. More usefully: it means
carrying a bloodline forward requires having actually *bred* during the season,
which is the behaviour worth rewarding.

## The Hall of Ancestors

The roster, and the screen it lives on: every born child with race, four
aptitudes, generation number, epic flag, and parents. Three jobs:

- **During the season** - pick which member you are playing as.
- **At the rollover** - when full, choose who carries and who is let go. This is
  what gives the last week of a season weight.
- **Always** - the pedigree view, the family tree across generations.

**10 slots base, +1 per inheritance purchase, hard cap 14.**

---

# 5. How villagers arrive

## Passive - the Inn attracts

Base interval **48h**, shortened by the Inn: `48h - (Inn level x 2h)`, floor 24h.
Over 90 days that is roughly **45 arrivals**.

The Inn therefore drives **both** how many people come and how good they are.
One building, one legible lever.

## Population cap, not a stretching timer

An escalating interval (each arrival taking a day longer than the last) was
considered and rejected: it is invisible and reads as a punishment for playing.

Instead the village holds **6 + Inn level** residents, max 16. When it is full,
nobody arrives until a slot is freed.

Better because it is visible at a glance ("Village 11/14"), it gives capacity
buildings a purpose, and it creates a decision: a villager shows up at
`4/3/9/2` - keep them, or turn them away and wait for a better roll?

Across a season that is ~45 rolls of the dice to hunt a 20 with. Enough to be
possible, not enough to be free.

## Active - recruitment

Gold plus cooked food (a feast) attracts someone immediately. Cost escalates
within the season at `base x 1.6^n` so it cannot be spammed. Doubles as the gold
sink the economy needs.

## Season start

**Two villagers**, rolled at Inn level 1 - so aptitudes of 2-3. Enough that the
first two days are not dead, bad enough to want better.

---

# 6. Build order

1. **Book of Deeds chapter I + the toast.** It is the onboarding fix, and it is
   the largest impact for the least work in this document.
2. **Ring 2 of the skill tree** (10 nodes) **+ the respec gate.**
3. **Breeding aptitudes**, inheritance, mutation, inbreeding degradation.
4. **Villager arrival**, Inn-scaled quality, population cap, Hall of Ancestors.
5. **Crowns** (5 keystones). The most work - each is its own mechanic.
6. **Chapters II-V and Seals -> skill points.**

# 7. Open, deliberately not decided here

**Region 5 does not fit in a season.** See the table at the top: ~3,934 geared
hours against a 2,160-hour season. Region 4 is ~549 and already consumes most of
what a player has. Either the season lengthens, or regions 4-5 come down, or the
gear curve has to outrun the monster curve far more steeply than 3x. This is the
biggest open balance question in the game and it needs its own pass with its own
measurements - deciding it inside a design document about three other systems
would be guessing.
