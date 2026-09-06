# The attributes got a system — 2026-09-06

Attributes became a player's choice earlier the same day, and making them
visible is exactly what exposed how little they did.

## What they were

| | effects | verdict |
|---|---|---|
| STR | +2 melee damage, +1 armour penetration | penetration was **dead** |
| DEX | +2 ranged damage, +0.05% speed, +0.1% crit, +1 accuracy | ranged damage was **dead** |
| CON | +15 health, +1 armour, regen, block | all four worked |
| LCK | +0.05% forge, +0.1% loot | two, both minor |

**Two of the eleven effects did nothing.** `CombatDamageModel.Mitigate` — the
only place armour is ever applied — never took a penetration term, so Might's
second effect and every `armor_pen_flat` affix ever rolled was worth zero. The
live account carries **1,122 penetration** on its weapon for no effect at all.
And nothing in combat reads `FlatRangedDamage`; the model has one damage number.

That left DEX as a strictly better STR — the same +2 damage plus accuracy, crit
and attack speed — and LCK as a dump stat. There was no choice to make.

## What they are

Four identities that beat each other at something:

| | | per point |
|---|---|---|
| **Might** | hits hard, and through armour | +2 attack power, +1 penetration |
| **Finesse** | hits often, and precisely | +1 accuracy, crit chance, attack speed |
| **Vigour** | survives being hit | +15 health, +1 armour, block |
| **Fortune** | takes more from the world | loot luck, forge success |

Finesse grants **no damage at all** now — that was the dead ranged number — which
is what finally separates it from Might instead of making it a superset.

### Armour penetration works, and is bounded by shape

It raises the halving constant rather than subtracting from armour:
`raw × (K + pen) / (K + pen + armour)`. That matters because the two are on
wildly different scales — monster armour runs 1 to 62 while a single affix rolls
above a thousand, so subtracting would let one roll erase every monster's armour
in the game and turn a stat into a switch.

Against Malakor's 62 armour at region 5 (K = 150): **70.8%** of a hit gets
through with none, **91.3%** with 500, **98.8%** with 5,000. Smoothly
diminishing, and it can never do better than ignoring armour — a natural ceiling
of about 1.4x.

### The percentages curve; the flat effects do not

A level pays 7 points and nothing spends them for you, so a long-played
character holds hundreds in one attribute. At the old flat rates that is +59%
crit chance and +29% attack speed from Finesse alone — the linear-and-uncapped
shape `PowerCeilingTests` refuses.

| points | 25 | 50 | 100 | 300 | 600 |
|---|---|---|---|---|---|
| crit, old | 2.5% | 5.0% | 10.0% | 30.0% | 60.0% |
| **crit, new** | **7.5%** | **10.6%** | **15.0%** | **26.0%** | **36.7%** |

The first points are worth *more* than they were. Health, attack power, armour
and accuracy stay linear, because they race content that grows geometrically — a
curve there would make them stop mattering rather than stop running away.

### Milestone tracks

Five rungs per attribute at the same thresholds — **25 / 60 / 120 / 200 / 300** —
so the four tracks are comparable at a glance.

| | 25 | 60 | 120 | 200 | 300 |
|---|---|---|---|---|---|
| **Might** | Heavy Hands +5% attack | Sunder +40 pen | Executioner +8% attack | Titan's Grip +80 pen | Worldbreaker +12% attack |
| **Finesse** | Quick Step +3% speed | Keen Eye +25 accuracy | Deadly Precision +15% crit dmg | Flurry +4% speed | Perfect Form +25% crit dmg |
| **Vigour** | Hardy +5% health | Thick Skin +10% armour | Second Wind +2/s regen | Ironhide +8% health | Unbreakable +25% crit mitigation |
| **Fortune** | Scavenger +8% loot | Prospector +5% gathering | Lucky Strike +2% crit | Golden Touch +8% gold | Fortune's Favour +8% forge |

Fortune's track is the only one that reaches outside a fight, which is what
stops it being the dump stat it has always been.

**Every rung lands on a `CombatStats` field that already has a reader.** That is
a deliberate constraint, not a coincidence: this codebase's most expensive
recurring defect is a stat that is computed and never consumed, and a milestone
table inventing five new mechanics would have been twenty fresh chances at it.

Applied *after* gear, so a +5% health rung acts on what the character actually
has rather than on the bare attribute contribution.

## Respec, and why it is free

`CommandType.RespecAttributes` returns every placed point to the pool.

Free, and that is a decision rather than an oversight. Every other purchase in
the game charges through a database transaction off the tick. Gold spent **on**
the tick would need a new path — decrementing `CurrentGold` and
`RedisPendingGoldDelta` together — and "two gold paths, and mixing them pays the
player twice" is a rule this codebase learned the hard way; inventing a third
for a respec button is not worth it. Doing it off the tick instead would mean an
engine writing the four attribute columns while a live session holds its own
copy, which is the exact split-brain the checkpoint's own comment warns about.

So the cost is the placing, not the paying.

## The window

`AttributePanel.svelte`, on the Character screen. Per card: the name and what it
is for, the live derived stats, a pip row showing progress along the milestone
track, an expandable track, and what one more point would buy — because the
curved effects pay less the more you have, and that is invisible unless it is
shown. Where the pool can reach the next rung, a **→ Prospector** button spends
exactly enough to get there.

## Two things this pass got wrong first, both worth recording

**A test that could not fail.** `EveryMilestoneMovesAStatSomethingReads` compared
threshold−1 against threshold and passed while the milestone code was *not wired
in at all* — because every per-point effect also moves when a point is added, so
its disjunction was satisfied by the linear terms alone. It now measures the
STEP against the SLOPE: the change across a threshold must exceed the change one
point earlier, which only a discrete rung can produce.

**A helper named `derived`.** A local binding of that name shadows the `$derived`
rune, so the compiler read `$derived(...)` in the same file as a store
auto-subscription and the whole Character screen threw `store_invalid_shape` at
runtime. `svelte-check` does not catch it — it is legal TypeScript — and only
loading the page did.

## Where the ledger stands

`PowerCeilingTests` picked up both new levers:

```
attribute milestones: damage   1.25x
armour penetration             1.27x
TOTAL 2,755x   monster ladder 750x   headroom 3.7x
```

Still inside the 0.5–10x band.


---

# Addendum: Fortune's second effect — 2026-09-06

Reported: *"forge success is a shit stat — we have 100% chance to upgrade an
item from 3 rarities to 1 higher, is that like a chance to get one more bonus
rarity?"*

No, and it was worse than that. **Fusion is guaranteed** — three of a rarity make
one of the next, with no roll — so `ForgeSuccessPct` was never a success chance.
It had been quietly repurposed into a **gold discount on the fusion fee**, capped
at 25%, under a name promising something else. A discount on the abundant
currency (the live account holds 85 million gold), invisible at the moment it
lands, is a weak second effect for the attribute whose whole identity is what
the world gives back.

**One correction to the premise, though: loot luck is not drop chance.**
`RollTier(lootLuckPct)` multiplies the weight of every rarity above Normal, so
it is *already* a rarity stat. Drop chance is a flat 15% and nothing moves it.
The panel said "loot luck", which let everyone assume the opposite; it now says
**"rarer drops"**.

## What Fortune grants now

| | |
|---|---|
| **rarer drops** | reweights the rarity roll (unchanged, honestly labelled) |
| **rarity elevation** | a chance the dropped piece comes out **one tier above** what it rolled |

They are genuinely different mechanics rather than one stat twice: luck reshapes
the roll silently, elevation bumps the result **visibly**. A player sees an
elevated drop happen.

`0.35 per root point` — 1.75% at 25 points, 8.6% at 600. Rare enough that it
stays an event; if it ever read like a coin flip, every drop would simply be a
tier better and the rarity ladder would have moved rather than gained a bonus.
`AttributeSystemTests` asserts that band.

The machinery already existed: `bonusRarityTiers` is what Golden Fleece uses for
its every-hundredth-kill bump. Elevation rolls *after* it and shares the clamp,
so the two stack without either escaping the fourteen real tiers.

Forge success survives as **Fortune's capstone milestone only** — "Fortune's
Favour, 8% off fusion fees" — where a discrete perk is an honest shape for a fee
discount, and where the panel now says what it actually does.
