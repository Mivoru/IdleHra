# "It looks like no items are dropping" — 2026-09-05

A player report, investigated end to end. **Nothing in the drop pipeline was
broken.** One thing in the *display* was, and it is the reason the report was a
reasonable thing to make.

Read this before touching `RarityTier.RollTier`, `EquipmentDropChance` or
`EquipmentDropTable` in response to a similar report — every one of them was
measured here and is doing what it is authored to do.

## The report, in order

1. "it looks like no items are dropping"
2. "it's strange that nothing better than Rare has dropped from the Ice Bat and
   I have 23,804 kills"
3. "now not even the tier 3 monsters drop good things, so something broke in an
   update"

## What was measured

| Question | How | Answer |
|---|---|---|
| Does the rarity roll match its table? | `RarityRollDistributionTests`, **2,000,000** samples | Every tier 1-8 within **0.98-1.03x** of its authored share. Mean tier **1.918**. |
| Is something capping the ladder? | The clamp in `TryRollEquipment` uses `CraftingEngine.RarityTierCount`, a *crafting* constant, on a *loot* roll | Both are 14. Pinned by a test now, because if they ever disagreed the only symptom would be this exact report. |
| Does the Ice Bat drop region-4 gear? | `EquipmentDropTableTests` prints every canonical monster's table | Yes. No monster drops another region's gear; no monster has an empty table. |
| Did the drop chance fall in an update? | `git log -p` on `CombatLootEngine.EquipmentDropChance` | 0.020 -> 0.050 -> **0.150**, and never downwards. 0.150 since 2026-08-06. |
| Is auto-salvage eating the good drops? | The account's own setting | Off, by the player's choice. |
| Is loot luck wired? | `LootLuckRaisesTheTopWithoutRemovingTheBottom` | Yes, it raises tier 5+ and removes nothing. |
| What is the player ACTUALLY getting? | Their last 91 drops, live DB | Mean tier **2.26** against a theoretical **1.918** — slightly *better* than authored. |

## Why the chest looked wrong, and why that was not evidence

The high-tier ratio in the older regions (region 3: 121 pieces above tier 7 out
of 506) is the fingerprint of **the chest sweep and of selling**, not of better
drops. The player identified this themselves and was right: they sold the low
rarities out of the regions they had left behind, which deflates the
denominator. A chest is `drops − sweep − salvage − sales + fusions`, none of
which are recorded with a timestamp, so **a chest census cannot answer a
question about drop rates**. Only the roll can, which is why the test above
asks the roll.

## The odds nobody had ever been told

Gear drops on **15%** of kills. Legendary-or-better is **0.85%** of drops.

- Legendary+ : about **1 kill in 780**
- Mythic+ : about **1 kill in 13,000**

24,000 Ice Bat kills is therefore a couple of dozen Legendaries — all of which
the sweep spares, and most of which had already been fused or sold. A player
counting kills against a number nobody showed them will conclude the game is
broken, and be reasonable about it. `SessionLoot` states the odds now.

`rarityOdds()` and `RARITY_DROP_SHARE` had been written, exported and imported
by **nothing** — found by a scan for dead client exports on the same day.

## The real defect: the panel threw the drops away

One shared 100-entry ring buffer held materials and equipment together. With two
characters gathering, a material lands every few seconds and the whole buffer
turned over in **about four minutes**, evicting every piece of equipment older
than that whatever its rarity. The player's own diagnosis was exactly right:
"with 2 characters on gathering the equipment probably gets overwritten straight
away".

Fixed by splitting it:

- `lootLogEquipment` and `lootLogMaterials` are separate stores, so material
  volume cannot reach the gear.
- `SessionLoot` renders two sections, each independently scrollable, so it
  cannot crowd it out visually either.
- Within each, sorted by **rarity descending**, not by time — a chronological
  feed of an idle session is a wall of Normal-tier scrap with the one Legendary
  buried four hundred lines up.
- Each equipment row names its tier. The same base item at three qualities
  renders as three rows and without the name they read as duplicates, on a panel
  whose whole purpose is saying how good a drop was.
- Neither store was ever cleared on sign-out or on switching characters. They
  are now.
- Gathering passes `showEquipment={false}` — a node drops no gear, so that
  screen was showing a permanently empty list under kill odds.

Loot was briefly narrated in the fight log; it was moved out on request ("under
the monster only the course of the fight, and on the right a loot drops window
split into materials and equipment"), and the eviction above is why that is the
right call and not only a layout preference. `exercise.mjs` asserts both halves:
the fight log carries **no** loot lines, and the loot panel fills, is split, and
sits to the right of the log.

## If this is reported again

Run `RarityRollDistributionTests` and `EquipmentDropTableTests` — both print
their tables. If they are green, the drops are happening and the question is
where they went: the sweep, auto-salvage, the forge, or a panel that dropped
them on the floor.
