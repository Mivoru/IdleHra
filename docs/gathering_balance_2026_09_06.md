# Gathering is 26x faster than its own slowest node — 2026-09-06

> **APPLIED, later the same day.** The measurement below stands, and it was
> INCOMPLETE: it measured speed and never looked at the roll count beside it,
> which was a further **71.9x**. What was changed, and what it now pays against
> what the game charges, is at the bottom under "WHAT WAS APPLIED". The
> "not been decided" section is what was decided.

Reported: "farmení materiálů je moc OP, mám přes 2 mil frostpine log, jenom kvůli
tomu že mám t3 axe — tohle se musí upravit a správně propočítat".

The holding is real: **2,296,754 frostpine logs** on one account. The cause is
not the axe.

## The formula

`GatheringToolEngine.ComputeRequiredTicks`:

```
ticks = baseTickThreshold * 100 / (100 + totalSpeedBonusPct)
totalSpeedBonusPct = tool + mastery*10 + villageProduction*5 + toolAffixes + skills + aptitude
floor: MinRequiredTicks = 2   (0.2 s)
```

Node bases are 30/45/60/80/100 ticks — 3 to 10 seconds at zero bonus.

## Measured, on the slowest node in the game (base 100 = 10.0 s)

| mastery | tool tier | total bonus | ticks | seconds | per hour |
|---:|---:|---:|---:|---:|---:|
| 0 | none | 50% | 66 | 6.6 | 545 |
| 0 | 5 | 398% | 20 | 2.0 | 1,800 |
| 50 | 5 | 898% | 10 | 1.0 | 3,600 |
| 100 | 5 | 1398% | 6 | 0.6 | 6,000 |
| **127** | **5** | **1668%** | **5** | **0.5** | **7,200** |
| 127 | 7 | 2037% | 4 | 0.4 | 9,000 |
| 150 | 10 | 3462% | 2 | 0.2 | **18,000** (floor) |

The reporting account sits on row five: **0.5 s per harvest, 7,200 an hour per
character, 14,400 with two gathering, 345,600 a day.** 2.3 million logs is 159
hours of that — which matches the account's age. Nothing is bugged; the curve
is simply this steep.

## Where the speed actually comes from

At the reported state (mastery 127, tool tier 5, village 10):

| lever | contribution | share |
|---|---:|---:|
| **mastery, 127 x 10%** | **+1270%** | **76%** |
| tool tier 5 | +348% | 21% |
| village production 10 x 5% | +50% | 3% |

**The axe is 21% of it. Mastery is 76%.** The player's own diagnosis blamed the
tool, and the tool is the one lever that was deliberately tuned (a geometric
1.35x a tier, documented at length in `GatheringToolEngine`). Mastery was never
tuned against it: `MasterySpeedPctPerLevel = 10`, linear, **uncapped**, and
mastery levels have no ceiling.

Two consequences:

1. **Mastery drowns the tool.** Past about level 35 the mastery term exceeds
   even a tier-10 tool, so the forge upgrade the tool curve exists to motivate
   becomes a rounding error. That is exactly the flatness the tool rework was
   written to end, reintroduced from the other side.
2. **Everything converges on the 0.2 s floor.** `MinRequiredTicks = 2` caps
   every node at 5 harvests a second, so at high mastery the five node tiers
   collapse into one: a region-1 twig and a region-5 frostpine both pay 18,000
   an hour. The node ladder stops meaning anything.

## What has NOT been decided

Numbers below are candidates, not a chosen design — the balance rules in
CLAUDE.md say the curve is measured by tests that print their tables, and
nothing here has been applied.

- **Diminishing mastery.** Something like `100 * sqrt(level)` percent instead of
  `10 * level`: level 25 keeps roughly what it has (+500% vs +250%), level 127
  drops from +1270% to +1127%... still too flat. A logarithmic or capped form
  (say, mastery contributes at most +400%) restores the tool as the dominant
  lever while keeping mastery worth levelling.
- **A relative floor instead of an absolute one.** `MinRequiredTicks = 2`
  should probably become "never faster than 25% of the node's base", so a
  10-second node floors at 2.5 s and the node ladder survives.
- **Check the sinks before cutting the source.** Nobody has measured what
  logs and ore are actually FOR at endgame - crafting costs, village upgrades,
  guild donations. If the sinks are in the hundreds while supply is in the
  hundreds of thousands, the supply is not the only half that is wrong, and
  cutting it alone would just make an unused surplus smaller.

## How to work on this

`GatheringShareTests` and `ProgressionRateTests` print their tables; add the
table above as a test that fails when the ratio between the levers moves, so
this cannot silently drift again. Do not retune the tool curve without reading
the `GatheringToolEngine` header — the reasoning for every tier is there, and it
is sound; the defect is the term beside it.

---

# WHAT WAS APPLIED, 2026-09-06

## The lever the measurement above missed

Speed is only half of a harvest. The other half is how many times the node's
loot table is rolled, and each roll grants one unit:

```csharp
int multiplier = (int)((localDropMultiplier + additionalYieldBonus) * payload.CachedCodexYieldMultiplier);
int rollsToExecute = multiplier / 100;   // plus a fractional chance
```

`CachedCodexYieldMultiplier` is `1 + 0.005 * (sum of every codex level)`, and a
codex level is ten kills of one monster. **Nothing bounded it.** On the
reporting account the sum is **14,178 levels**, so:

| lever | factor |
|---|---:|
| **codex yield multiplier** | **71.9x** |
| mastery speed, 127 x 10% | 14.7x |
| tool tier (measured, tuned, paid for in materials) | 4.5x |

Real supply at that state was not 7,200 units an hour. It was **~862,800 an
hour, per character** - the speed table above multiplied by seventy-two.

## What the game actually charges

| sink | units of material |
|---|---:|
| the most expensive single village upgrade | 507 |
| one village building from level 0 to 12 (logs + ore) | 5,780 |
| the most expensive recipe in the game (Voidbark axe) | 69,862 |
| **every recipe in the crafting tree, once** | **383,553** |

So one character was earning the **entire material sink of the game, twice
over, every hour**, and holding a hundred times the village's lifetime cost in
a single log type. The supply was not slightly out; it was three orders of
magnitude out.

## The three changes

**1. The codex yield multiplier is capped at 2.0x** (`CodexEngine
.MaxYieldMultiplier`). It reaches the ceiling at 200 codex levels - about two
thousand kills, a region's worth of play - and doubling a harvest is still a
real reward. Kill counts, codex levels and the codex **damage** multiplier are
untouched.

**2. Mastery speed is `40 * sqrt(level)` percent**, where it was `10 * level`
linear and uncapped.

| mastery | 1 | 10 | 25 | 50 | 100 | 127 | 400 |
|---|---:|---:|---:|---:|---:|---:|---:|
| old | 10% | 100% | 250% | 500% | 1000% | 1270% | 4000% |
| **new** | **40%** | **126%** | **200%** | **283%** | **400%** | **450%** | **800%** |

The first levels are worth *more* than they were, and past level 25 the tool is
the bigger lever again - which is the whole point. A curve that bounds the top
by punishing the bottom would be a different, worse game.

**3. The tool curve is untouched.** It was the one lever that had been designed,
its reasoning is in `GatheringToolEngine`'s header, and it costs materials -
which is exactly what a dominant lever should do.

**The relative tick floor was considered and rejected.** "Never faster than 25%
of the node's base" needs 300% of total bonus to bind, and a tier-5 axe alone is
348%, so every tool from tier 5 up would have been worth the same on every node.
Even at 10% it binds at 900% and flattens tiers 8, 9 and 10 - re-creating from a
third direction the exact flatness the tool rework exists to end. The absolute
`MinRequiredTicks = 2` stays. The consequence is that a fully-maxed gatherer
does floor a region-1 node, and that is harmless: region-1 logs buy region-1
recipes and nothing else.

## What it pays now

`GatheringEconomyTests` prints this table and fails if it drifts:

| profile | s/harvest | units/h | common/h |
|---|---:|---:|---:|
| new player, region 1, bare hands | 3.0 | 1,200 | 1,080 |
| region 1, first axe | 1.3 | 2,908 | 2,617 |
| region 3, keeping up | 0.8 | 6,750 | 6,075 |
| region 5, geared | 0.8 | 9,000 | 8,100 |
| region 5, everything maxed | 0.3 | 24,000 | 21,600 |

At the maxed rate: the most expensive village upgrade is **1.4 minutes**, the
most expensive recipe in the game **3.2 hours**, and the entire crafting tree
**17.8 hours** for one character. That is a **36x** cut at the top and no cut at
all for a new player.

## Still open, and deliberately not taken

**The codex DAMAGE multiplier is `1 + 0.01 * levelSum` and is also uncapped.**
On the same account that is **142x**, which is why a region-5 monster dies in
about a second there and why the codex kill count climbs at one a second. It is
the same defect in the same formula, one line above the one that was capped.

It was left alone on purpose: capping it would divide a live character's combat
output by a hundred without being asked, and that is a product decision, not a
bug fix. If it is taken, the shape is the same one line in
`CodexEngine.CalculateActiveMultipliersAsync`, and `MonsterLadderTests` /
`ProgressionRateTests` are what should be read first - the ladder was tuned
against a player who did *not* have a 142x multiplier, so capping it is a
re-pacing of the whole game rather than a nerf to one number.

**Existing stockpiles are untouched.** Nobody's 2.4 million logs were taken
away; only the rate at which the next ones arrive changed.
