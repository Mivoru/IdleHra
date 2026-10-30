# Loot chests, dungeons, diamonds and inheritance - a proposal

Written 2026-10-30, after the owner's answers about the Deep. This is a
proposal, not a plan someone can build from yet. Each section ends with the
decisions the owner has to take, and each decision comes with a
recommendation. Nothing here is committed until the owner says so.

It covers four things the owner raised in one conversation:

1. **Loot chests**: chests that pay gold, diamonds, materials and equipment,
   not only cosmetics.
2. **Dungeons**: daily ticket-gated runs (XP, gold, equipment), with tickets
   that can also drop from chests.
3. **Diamonds**: higher earning taps, and more things worth spending them on.
4. **Inheritance**: a rework, because today it is too expensive to matter.

What was decided and built the same day is recorded in section 0, so the rest
can refer to it.

---

## 0. Already decided (2026-10-30)

**The Deep, built in the `claude/deep-reward` PR:**
- The toll curve went from x1.35 to x1.25 a floor. Floor 20 now costs 11% of
  wealth, not 20%, and floor 23 costs 22%, not 51%.
- The Deep has its own weekly diamond ceiling of 20. Floors 10-19 each pay 2
  diamonds the first time they are the week's deepest. The Delve keeps its own
  60.

**Why:** production showed the Deep's spend fall from 41% of the gold minted
(week of 2026-10-12) to 0.8% in the two weeks after. Its only diamond reward
shared the Delve's ceiling, which three Delve clears fill, so depth paid a title
once and nothing after.

**Owner direction for later:** the diamond taps may go higher, diamonds need
more uses, and inheritance should be reworked. Sections 3 and 4.

---

## 1. Loot chests

### 1.1 What exists today

- **Cosmetic chests** (`CosmeticGrantEngine`, `ChestOpening.svelte`) have a
  rarity, an opening animation and published odds. They pay cosmetics only.
- **Equipment drops** come from kills (`CombatLootEngine`, per-monster drop
  tables, rarity from `RarityTier.ResolveDropTier`).
- **Materials** come from gathering and from kills.
- The client already has a chest stage, a reveal and a rare-drop card.

So a loot chest is mostly content and a grant path, not new presentation.

### 1.2 The proposal

A **loot chest** is an item the player holds and opens. It has a tier
(Bronze, Silver, Gold, plus a rare Ancient). Opening it rolls a table on the
server. The table is scaled to the player's **highest unlocked region**, so a
chest is worth the same share of progress at level 20 as at level 100.

Proposed contents (weights per tier are numbers to measure, not final):

| Slot | Bronze | Silver | Gold | Ancient |
|---|---|---|---|---|
| Materials of the current region tier | always | always | always | always |
| Materials of the highest tier the player has reached | - | sometimes | often | always |
| Gold (a fixed number of minutes of the player's income) | small | medium | large | large |
| Equipment of the current region, rolled rarity | rare | uncommon | common | always, high rarity floor |
| Diamonds | - | very rare, 1-3 | rare, 2-5 | 5-10 |
| Dungeon ticket (section 2) | - | rare | uncommon | common |

**Gold is priced in minutes of the player's own income**, not in a flat
number. A flat number is either worthless at the top or a fortune at the
bottom, which is the same trap the Delve's gate fell into.

**Diamonds count inside a weekly ceiling** like every other free tap (section
3). A chest roll that would pass the ceiling pays gold instead, the same rule
the Delve uses.

### 1.3 Where chests come from

| Source | Tier | Why |
|---|---|---|
| The Deep, each new weekly-deepest floor from 10 | Bronze at 10, rising with depth | Gives depth a reward that is not diamonds or power. This is the first use. |
| Regional boss first clears and Ascension steps | Silver / Gold | Milestones that today pay a trophy or a title. |
| Dungeon completion (section 2) | by dungeon and difficulty | The dungeon's main payout. |
| World boss milestones | Gold | Replaces part of the diamond payout, which is under the ceiling anyway. |
| Daily login streak, day 7 | Silver | |
| A very rare kill drop (like the monster pets) | Bronze | A small surprise in the idle loop. |

### 1.4 Rules that keep it safe

- **The server rolls, opens and grants.** The client only shows the result.
  One grant path with one ledger entry: gold goes to `gold_income_daily`
  (a new source), diamonds to the weekly ceiling, items through the existing
  grant outbox.
- **Opening is idempotent**: a chest id is consumed in the same transaction that
  writes its contents, so a double tap or a replay grants once.
- **Odds are published**, as the cosmetic chests' odds already are. Several
  store rules require that.
- **Auto-salvage does not touch chest equipment.** A player who opens a chest
  sees what was in it.

### 1.5 Decisions for the owner

- **L1. Which sources ship first?** Recommendation: the Deep and regional boss
  first clears. Both exist, both are short of rewards, and that is enough to
  measure how chests feel before dungeons depend on them.
- **L2. Can loot chests be bought for diamonds?** Recommendation: no, not at
  first. A purchasable chest that pays diamonds and gear is a loot box bought
  with real money, with legal and store-review consequences in several
  countries. Keep chests earned. Revisit after the store submission.
- **L3. Do chests stack in an inventory, or open on the spot?**
  Recommendation: stack. Opening a few at once is the satisfying part, and
  the "open x5" flow already exists for cosmetic chests.

---

## 2. Dungeons

### 2.1 The idea

A dungeon is a short, separate run with its own entry, its own fights and a
payout at the end. Entry costs a **ticket**. A player gets a fixed number of
tickets a day and can find more in chests. Three kinds to start:

- **XP dungeon**: pays experience.
- **Gold dungeon**: pays gold.
- **Equipment dungeon**: pays equipment and a loot chest.

### 2.2 How a dungeon fight works

This is the decision that shapes everything else, so it comes first.

**Option A - a simulated run (recommended).** The player chooses a dungeon,
a difficulty and a character, and pays a ticket. The server runs the fight
with the existing combat simulation over a fixed sequence of rooms (for
example five waves and a boss). The client plays it back from the combat event
feed (`ResponseCombatEventPacket`), the way the combat screen does today. It
takes about one to three minutes. It can be lost.
- It reuses the combat model, the event feed and the combat screen.
- It cannot be cheated: the server decides every blow.
- It fits an idle game: a dungeon is a decision (which, how hard, who) and a
  result, not a reflex test.

**Option B - an interactive run.** The player makes choices between rooms
(rest, push, pick a buff), like the Delve's doors.
- More engaging, but a second minigame to design, balance and verify.
- Could be added later on top of A, as a modifier between rooms.

**Option C - an offline timer.** Send a character for N minutes and collect.
- Simplest, but it is the idle loop again with a different label.

### 2.3 Tickets

- **N free tickets a day per kind** (start with 2), reset at 00:00 UTC.
- Unused free tickets **do not** accumulate past a small cap (for example 4),
  so a player who misses a day is not punished and one who misses a week is
  not rewarded.
- **Extra tickets** come from loot chests and could later be bought with
  diamonds (section 3, a diamond sink that sells convenience, not power).

### 2.4 Difficulty and payout

- Each dungeon has difficulty steps tied to the region ladder: step N fights
  monsters scaled like region N's top, with a boss.
- **Payout scales with the step and with the player's own income**, so a
  higher step is worth more than farming the region it matches, which is why
  a player would spend a ticket.
- **A lost run** refunds nothing but pays a consolation (a Bronze chest, or a
  share of the payout), so a failed attempt is not pure loss.

### 2.5 The pieces it needs

| Piece | Size | Notes |
|---|---|---|
| Content: dungeon definitions (rooms, monsters, scaling, payout) | M | JSON in `server/GameData`, validated by the content hook. |
| Server: `DungeonEngine` (enter, simulate, settle), tickets, a run table | L | Reuses `SimulationEngine` combat helpers, the event feed and the grant outbox. One migration. |
| Wire: commands (enter, claim), a dungeon view, the event feed reused | M | Use the `add-command` skill; generated protocol. |
| Client: a Dungeons screen, the run playback, the result card | L | Reuses the combat scene and the chest reveal. |
| Art: a background per dungeon kind, ticket and chest icons | owner | |
| Verification: `exercise` steps that enter, finish and claim, and restore the tickets they spend | M | |

Total: an **XL phase**, about the size of phase 2b. It depends on loot chests
(the equipment dungeon pays one) and should come after them.

### 2.6 Decisions for the owner

- **D1. Fight model: A (simulated), B (interactive) or C (timer)?**
  Recommendation: A first, with B's between-room choices as a later
  extension.
- **D2. Can dungeons be run offline?** Recommendation: no. A dungeon is
  something you sit down for. The idle loop already covers offline time.
- **D3. Free tickets a day?** Recommendation: 2 per kind, a cap of 4, and
  more from chests.
- **D4. Which kind first?** Recommendation: the equipment dungeon. It pays
  what the game is shortest of at the top (better gear without RNG grinding)
  and it uses loot chests.
- **D5. Where does it sit in the roadmap?** Recommendation: after Hearth
  (phase 4) and the affix rework (phase 3), because the equipment dungeon pays
  affixed gear and its value depends on that rework. Loot chests can come
  earlier, right after the measurement batch.

---

## 3. Diamonds

### 3.1 The taps today (free diamonds a week, at most)

| Tap | Per week | Rule |
|---|---|---|
| The Delve | 60 | `DelveRegistry.MaxDiamondsPerWeek` |
| The Deep | 20 | New 2026-10-30, `MaxDeepDiamondsPerWeek` |
| Mastery leaderboard, first place | 60 | `LeaderboardTierRegistry` |
| World boss milestones and kill | event | Chernobog |
| Daily login, day 7 | small | |
| Achievements | one-off | `AchievementMilestones` |

The spec for Guild Wars pins a total (`EconomyDecisions.MaxWeeklyDiamondsAllTaps`
= 140). It is not in code yet because Guild Wars is parked, but the Deep's
new tap changes that sum, and the spec should be updated when Guild Wars comes
back.

### 3.2 The sinks today

| Sink | Price |
|---|---|
| Inheritance levels | 40 x 1.28^level each (section 4) |
| Hall of Ancestors slots | doubling from the first slot |
| Chronicle premium pass | 950 |
| Affix reroll (Diamond path) and rarity upgrade | `AffixRegistry` |

**Production on 2026-10-30:** the three players at level 95+ hold 107, 147
and 370 diamonds. Nobody is hoarding thousands, so the taps are not too high
for the sinks that exist. The problem is the other way round: diamonds come
in slowly and the main sink (inheritance) is so steep that a purchase is a
month away.

### 3.3 Proposals

**Raising the taps** (the owner is open to it):
- The Delve 60 -> 80, and the Deep's 20 stays. That is about +25% free
  diamonds for an active player.
- Recommendation: raise them **together with** the inheritance rework below,
  not before. A higher tap into today's sinks only shortens the top account's
  wait for its next +2% (about 600 diamonds) from about ten weeks to seven. A tap and a sink should be tuned as a pair, and
  measured with the same model.

**New sinks** (convenience and choice, not raw power):
- **Extra dungeon tickets** (section 2), capped per day.
- **Gold respec of the skill tree for diamonds** as an alternative to the gold
  price: fast, never cheaper than the gold path in value.
- **Loadout slots**: save and swap gear and skill-tree builds per character.
- **Breeding helpers**: guarantee one aptitude from the better parent, or
  reroll a newborn's flaw once.
- **Cosmetic market fee waivers or extra market listings.**
- **Offline cap extension**: a few more hours of offline catch-up, bought per
  week. It is power-adjacent, so it needs a cap.

### 3.4 Decisions for the owner

- **M1. Raise the taps now, or with the inheritance rework?** Recommendation:
  with the rework, as one balanced change.
- **M2. Which new sinks?** Recommendation: dungeon tickets (when dungeons
  ship), loadout slots and breeding helpers. All three sell convenience or
  choice, none sells a bigger number.
- **M3. Do diamond purchases ever buy gold or materials directly?**
  Recommendation: no. That turns real money into progress one-to-one and
  makes the free taps feel pointless.

---

## 4. Inheritance

### 4.1 What it is today

Six stats (damage, max HP, XP gain, gold gain, gathering yield, loot luck), 20
levels each, +2% a level, so +40% at most per stat. A level costs
`40 x 1.28^level` diamonds (`InheritanceRegistry`). It survives rebirth.

**The cost, worked out:**
- Level 0 -> 1 costs 40. Level 10 -> 11 costs about 470. Level 19 -> 20 costs
  about 4,350.
- One stat from 0 to 20 costs about 19,800 diamonds. All six cost about
  118,000.
- At 80 free diamonds a week, one maxed stat is about 5 years.

**Production on 2026-10-30:** the most invested account has damage 11, XP 9,
loot luck 11. Its next damage level costs about 600 diamonds, 7-8 weeks of
free diamonds, for +2%.

So inheritance is the game's main diamond sink and it does not work as a
progression: the first levels are cheap, the rest are out of reach, and a +2%
step is too small to feel.

### 4.2 Options

**Option A - flatten the curve.** Keep the six stats, change the price to
`40 x 1.12^level` (one stat 0 -> 20 about 2,900; all six about 17,500), and
keep +2% a level.
- Smallest change, no migration, existing levels stay.
- Still six flat percentages.

**Option B - fewer, bigger steps with milestones.** 10 levels a stat, +4% a
level, and at levels 5 and 10 a **milestone effect** (damage 10: crits pierce
some armour; gold 10: one extra Delve run a week; gathering 10: rare finds
doubled). Price `100 x 1.25^level`.
- Each purchase is felt, and the milestones give a reason to finish a stat.
- Existing levels convert (two old levels to one new level, rounded up, plus
  a diamond refund for the remainder). Needs a migration and a clear notice.

**Option C - an inheritance tree.** Turn it into a small tree like the War
skill tree, bought with diamonds, surviving rebirth.
- The most interesting, and it reuses the tree UI and its harness.
- The biggest piece of work, and it overlaps the skill tree's role. Risk of
  two trees that feel the same.

### 4.3 Decisions for the owner

- **I1. A, B or C?** Recommendation: **B**. It fixes both problems (out of
  reach, too small to feel) and gives diamonds a goal with milestones. A
  alone is the cheap fix if B is too much for now. C is not worth its
  overlap with the skill tree.
- **I2. When?** Recommendation: as one change with the diamond taps (M1),
  measured with one model of free diamonds a week against the price of each
  milestone. Before that model exists, no numbers here are final.

---

## 5. Suggested order

1. The Deep reward (done, section 0). Watch the Deep's spend for two weeks.
2. Loot chests, first sources: the Deep and boss first clears (section 1). M.
3. Diamond taps and the inheritance rework as one balanced change (sections
   3-4). M-L.
4. Dungeons, the equipment dungeon first (section 2). XL, after Hearth and the
   affix rework in `ROADMAP_2026_10_30.md`.

Items 2 and 3 are small enough to fit between the roadmap's phases. Item 4 is
a phase of its own.
