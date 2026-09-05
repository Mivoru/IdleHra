# Gathering is 26x faster than its own slowest node — 2026-09-06

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
