# Nav, second pass: spirits, banes, the week, outward research (2026-11-19)

The owner asked (2026-11-19, the same day Nav went live): fix every weakness
of the first Nav, add the spirits after all, and think through whether Nav
should reach the rest of the game (combat XP, gold, drops) once a player is
deep enough - measured, not guessed.

## What was weak in the first Nav

1. **Souls had one use.** Everything bought in the Workshop, so after a few
   weeks a record was only a number.
2. **No decisions.** Three research lines that all said "more Workshop", and
   seven upgrades that were "buy them all again".
3. **The hint pointed at nothing.** A run named the guardian that stopped it
   and said what beats it, then offered nothing to buy for it.
4. **Breeding had no goal past 50/50/50/50.**
5. **The board was all-time and paid nothing.** With three players it only
   ever moves for one of them.
6. **Nothing reached outside Nav.** Deliberate in the first build (so the
   regions' balance was untouched), but it also meant going deeper did not
   help the rest of the game at all.

## What was built

| Weakness | Answer | Where |
|---|---|---|
| 1, 2 | **Frames of Nav** for Souls (4, bound, each behind a place in Nav); **Spirit bond** (+3% to spirits a level) | `NavRules.Frames`, `NavEngine.BuyFrameAsync` |
| 2, 3 | **Banes**: one per guardian, -4% to its rule a level, 15 levels; opens when that guardian first stops you; the "why it stopped" box sells it | `NavRules.Workshop` (Guardian), `NavProgress.GuardiansMet` |
| 2 | **Six new research lines**: Deep well (+1 waiting descent, 6), Swift return (-1 min a descent, 8), Ancestors' call (+1 spirit, 3), and three outward lines (below) | `NavRules.Research` |
| 4 | **Spirits of Nav**: the best 3 (6 with research) adults outside the slots; each aptitude point above 20 adds Strength damage, Endurance health, Skill speed, Fortune Souls | `NavRules.SpiritBonus`, `NavEngine.LoadBenchAsync` |
| 5 | **Deepest this week**, Monday to Monday; top three paid an Ancient/Gold/Silver chest (if 2+ descended), first win a title | `NavEngine.SettleWeekAsync`, `Week*` columns |
| variety | **Weekly omen**: one guardian restless - rule x1.5, its depths x2 Souls | `NavRules.RestlessGuardian` |
| 6 | **Outward research**, gated by Circles: Veles' bounty (+1% combat gold), Memory of the dead (+1% combat XP), Mokosh's thread (+0.5% loot luck) | `NavBlessingsCache` |

## The outward bonuses: measured

On the 2026-11-19 backup, read through the server's own stat sheet:

| Player | Level | Combat XP | Combat gold | Loot luck | Gold/h on Malakor |
|---|---|---|---|---|---|
| Mivoru | 127 | +101% | +13% | +25% | 95M |
| Tomda | 108 | +97% | +128% | +61.5% | 25M |
| (pro)boss | 110 | +99% | +49% | +37% | 19M |

- **XP** is a sum: one Memory level adds 1 point to ~+100%, so 0.5% more XP.
  Levels stall at 135-140; the line buys a few levels, not regions.
- **Gold** multipliers multiply (`CombatGoldReward.FactorsFor`), so Veles'
  bounty is a full 1% a level: 160M a week for Mivoru at 95M/h, against a
  level that costs `100M x 1.5^level`. It pays back only while shallow, and
  the gold it makes flows back into the same research.
- **Luck** reweights rarity, not the drop count, so it cannot flood the chest.
- Tops: 10 levels per Circle from the one that opens the line, 25 (luck 20)
  at most. Gold and XP open with Circle 2, luck with Circle 3: the bonus
  follows how deep you went, not only your gold.
- The tick reads them from `NavBlessingsCache` (by player id, like the guild
  buffs), loaded at start-up and updated on every research write; the
  `FOLKIDLE_NAV` flag turns them off with Nav.

## Fourteen simulated days (first pass of the numbers)

`scratchpad/navsim.mjs` against the local restore: the real simulator, the
real bench, real gold; 3 check-ins a day of a full charge bar (39 descents),
Souls spent on the cheapest of damage / health / crit / the last stopper's
bane, research in a fixed order on a 24-hour clock.

| Day | Mivoru (C:depth) | Tomda | (pro)boss |
|---|---|---|---|
| 0 (no upgrades) | 1:37 | 1:13 | 1:19 |
| 1 | 1:49 | 1:21 | 1:24 |
| 2 | 2:42 | 1:24 | 1:33 |
| 6 | 3:29 | 1:34 | 1:44 |
| 14 | 3:34 | 1:38 | 1:49 |

- Effective depth (`depth + 16.9 x (circle - 1)`, a Circle being x5):
  Mivoru 37 -> 68, Tomda 13 -> 38. Fast first days, then logarithmic: costs
  grow 12% a level, effects are flat.
- **Banes were too cheap** (40 Souls, 20 levels to 80%): Mivoru had them all by
  day seven and the ten guardians became one. Now 150 Souls, 15 levels, 60%.
- Research spent 7.9B of Mivoru's gold in two weeks (the restore earns
  nothing, so it ran dry): the sink works.
- **Second run (7 days, new bane numbers):** Mivoru still opens Circle 2 on
  day 1-2 - the early climb is the cheap first levels of Rootstrength /
  Barkskin / Grave luck, not the banes - and holds eight banes at 60% by day
  seven, so every rule keeps 40%. Workshop growth (1.12) is left as the owner
  set it on 2026-11-19 ("tune after a week of real runs"); if the first week
  is too fast, the lever is the base costs (x5 is ~14 levels fewer each).
  (Research in this run is starved: the restore had spent its gold in the
  first run.)
- (pro)boss climbs faster than Tomda (49 vs 38 by day 14) on three 200-point
  spirits (+36% damage and health, +36% Souls) - breeding now shows in Nav.
- The gap stays: ~30 effective depths (~x17 power) between Mivoru and the
  others, from their combat power, not from Nav.

## Every depth pays (owner, later the same day)

The owner asked for a reward for every depth: gold and a loot chest, Ancient
chests deeper, and diamonds and dungeon tickets on round milestones.

| First clear of a depth (per Circle) | Pays |
|---|---|
| any | 10 min of the player's gold income x Circle, a Bronze chest (the owner's pick over Silver) |
| every 5th | a Gold chest instead |
| every 10th | an Ancient chest, 10 diamonds x Circle, 1 ticket for each dungeon gate |
| 50 (opens the next Circle) | 50 more diamonds |

- Paid on the record (`BestDepths`), never per run: 39 descents a day would
  farm a per-run reward. `NavRules.DepthRewardFor`, `NavEngine.PayDepthsAsync`.
- It replaces the first build's 10/25/50 milestone chests. Depths reached
  before it are back-paid once (`NavProgress.DepthRewardsVersion`, migration
  `NavDepthRewards`), on the next GET or descent, less the chests those
  milestones already paid (`MilestoneMask`).
- Gold and diamonds move after the Nav transaction commits (the commodity and
  player rows are the checkpoint's too); the route then sends ReloadState and
  BillingSync. Gold income is ledgered as `GoldIncomeSource.Nav`.

**Measured** on the 2026-11-19 backup (`notable_item_events`, the 7 days to
the dump): the three players took 750 (Mivoru), ~1,300 (Tomda) and ~1,200
((pro)boss) Legendary-or-better pieces a week, most of them boss guarantees
won away. The 14-day simulation's fastest climb (Mivoru, ~97 new depths)
becomes ~78 Bronze, ~10 Gold and ~10 Ancient chests - under 5% of the gear
they get anyway. Diamonds: ~210 in those two weeks, about one ordinary week
(login 120 + Delve 80 + chests 15), then 10-30 a week as the climb slows.
Tickets: ~30 in two weeks against 84 from the daily top-up, and the bonus cap
(8 a gate) holds. Ancient chests' own diamonds stay under the chest cap (15 a
week).

## The descent as a scene (owner, the same evening)

"Graphics, animations, transitions, sounds - a really good game experience."
`client_web/src/lib/ui/NavDescentStage.svelte` (on `Modal`) replaces the
in-page replay card: the Circle's painted shaft (`Backgrounds/nav_stage_c1-3`,
Gemini, cycling past Circle 3) scrolls as the depths fall, a rail fills towards
the record, each guardian arrives, is slashed and sinks with its Souls floating
up, Veles glows gold, the stopper shakes the scene red, a ribbon marks the
record, and the run ends on its haul drawn as chest art, gold, diamonds and
tickets. The server sends the haul as data (`NavLoot`, `RecordBefore` on the
run view). Six ElevenLabs sounds (`nav_fall`, `nav_stopped`, `nav_veles`,
`nav_reward`, `nav_buy`, `nav_research`), loudness matched at -21 LUFS (fall
-28 measured after limiting, buy -24: they repeat). Reduced motion opens on the
result.

## Not done

- Weekly omen variants beyond "one guardian restless" (crit twice, no
  potions...) - the duel's omen system could carry them later.
- Tune after a week of real runs (owner, unchanged).
