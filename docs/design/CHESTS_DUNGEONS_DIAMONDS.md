# Loot chests, dungeons, diamonds, inheritance and achievements - a proposal

Written 2026-10-30, after the owner's answers about the Deep. This is a
proposal, not a plan someone can build from yet. Each section ends with the
decisions the owner has to take, and each decision comes with a
recommendation. Nothing here is committed until the owner says so.

It covers five things the owner raised in one conversation:

1. **Loot chests**: chests that pay gold, diamonds, materials and equipment,
   not only cosmetics.
2. **Dungeons**: daily ticket-gated runs (XP, gold, equipment), with tickets
   that can also drop from chests.
3. **Diamonds**: higher earning taps, and more things worth spending them on.
4. **Inheritance**: a rework, because today it is too expensive to matter.
5. **Achievements**: one progression with the quest line and the tutorial,
   fair rewards, more and better achievements, and a proper announcement.

What was decided and built the same day is recorded in section 0, so the rest
can refer to it.

---

## Decisions taken (owner, 2026-10-30)

| Topic | Decision |
|---|---|
| Loot chests (L1-L3) | Earned only, never sold for diamonds. First sources: the Deep's new weekly floors from 10, and regional boss first clears. Chests stack and open in batches. |
| Dungeons (D1-D5) | A simulated run (Option A): server-simulated waves and a boss, played back from the combat event feed. 2 free tickets a day per kind, cap 4, more from chests. Equipment dungeon first, after Hearth and the affix rework. Not offline. |
| Achievements (A1-A4) | One registry; the quest line becomes its first chapter; reward bands 5/15/40/100 diamonds with chests as an alternative from Silver; about 150 diamonds per achievement at most, today's 1,000/1,500 tiers split into more goals; banner with sound, a big card for Legendary, an Achievements screen. After loot chests. |
| Diamonds and inheritance (M1-M3, I1-I2) | One balanced change: taps (Delve 60 -> 80), inheritance Option B (10 levels of +4%, milestones at 5 and 10), and the achievements tap, measured with one model. New sinks: dungeon tickets, loadout slots, breeding helpers. Diamonds never buy gold or materials. |

The sections below keep the options and the reasoning behind each choice.

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

### 1.4a Build plan (2026-10-30, after L1-L3 were decided)

1. **Rules** - `LootChestRules` (built): tiers, contents as time, published
   odds, the refused-diamond-pays-gold rule. First-cut numbers, re-measured
   after a week of real openings.
2. **Storage** - a `LootChests` table (Id, PlayerId, Tier, Region, Source,
   GrantedAtUtc), one row per chest, so opening consumes a row id in the
   same transaction that writes its contents (idempotent by construction).
   A `ChestDiamondsThisWeek` counter with the Delve's week stamp: chests get
   their own weekly diamond ceiling (proposal: 15) rather than sharing the
   Delve's 60 or the Deep's 20.
3. **Opening** - `LootChestEngine.OpenAsync(ids)` on the REST stripe lock,
   like the Delve: gold through `CommodityLedger` + `GoldLedger` (a new
   `GoldIncomeSource.LootChest`), materials through `CommodityLedger`,
   equipment through the existing equipment insert with auto-salvage OFF
   (section 1.4), diamonds under the ceiling. Gold per minute = the
   player's measured income (`gold_income_daily`, last 7 days / minutes
   played), with a region floor so a new account's chest is not empty.
4. **Sources** - the Deep's new weekly-deepest floors from 10 (Bronze at
   10-12, Silver 13-16, Gold 17+) and regional boss first clears (Silver,
   Gold for region 5). Both already have a grant point.
5. **Client** - a chest shelf (stacked counts) and the opening on
   `ChestStage` (draft PR #239), open one or x5; the reveal card lists
   what came out.
6. **Verify** - `exercise.mjs` grants a chest through a dev route, opens it,
   checks the wallet and stash moved, and restores what it spent.

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

### 2.6a Difficulty, stages, unlocks and loot (added 2026-10-30)

The owner's brief: difficulty that makes sense, a top stage much harder than
Malakor (who is no longer a problem), and clear rules for when a stage
unlocks and how difficulty and loot scale.

**What production says (2026-10-30, read-only):** Mivoru cleared Malakor A10
on 2026-10-02 (before the ladder was re-tuned to x85 attack at A10 on
10-11) and finds it easy now; Tomda has every boss of regions 1-4 at A10 and
Malakor at A1. The last measured headroom (x32 attack for the owner's
character, x49 for a maxed profile) is from 2026-10-11, before the War tree,
the stats fixes and the pets, so it is out of date. **Every number below is
relative to the reference ladder; the absolute values come from re-running
`AscensionCalibrationHarness` on a production copy (task 112) first.**

#### Fifteen stages per dungeon, in three bands

| Band | Stages | Strength | Unlock |
|---|---|---|---|
| **Regions** | 1-5 | Stage N is region N's boss strength (the monster ladder), slightly above it (x1.1) | Stage N needs region N's boss beaten, and stage N-1 of the same dungeon cleared |
| **Ascension** | 6-10 | Malakor's own ladder: stage 6 = A2, 7 = A4, 8 = A6, 9 = A8, 10 = A10 | Stage 6 needs Malakor beaten once; each later stage, the one before it |
| **Beyond** | 11-15 | Past anything in the game: stage 11 = A10 x1.5, then x1.4 a stage, so stage 15 is about **5.8x Malakor A10's attack** (health rises as attack^0.68, the Ascension split) | Stage 11 needs stage 10 cleared; each later stage, the one before it |

The dungeons open at the **region 2 boss** (about level 25), the point where
the regular loop has a ticket's worth of reasons to leave it. A new player
sees the lobby locked with that sentence on it, not an empty screen.

**The target for the top**, set against the measured headroom once task 112
re-runs: **stage 10 is what a maxed endgame profile clears with effort**
(the A10 role today), and **stage 15 needs about three times a maxed
profile**, so it is a goal for several rebirths and the inheritance rework
(section 4), not for one strong season. If the measurement shows today's
characters already near A10's power, the Beyond band's base moves up, not the
ratio: the shape (1.5x then 1.4x a stage) stays.

#### What makes a dungeon hard, and why it is not just health

The larder is the game's real difficulty ceiling: auto-eat heals a share of
the bar, so a strong character's headroom grows faster than any monster
table (the Malakor calibration note, and the "larder is the ceiling" lesson).
A dungeon that let the larder run freely would be beaten by food, not by
power. So:

- **Provisions:** a run takes at most **5 meals** from the larder (auto-eat
  as usual, then nothing). Healing becomes a resource across the six fights,
  and a boss's damage per hit matters again.
- **The fights chain:** health carries from wave to wave to the boss; there
  is no rest between waves.
- **No time limit.** A run ends in a kill or a death, so the simulation is
  exact and the result is easy to read ("defeated by the boss at 40% of its
  health").
- **Waves** are five fights at about 40% of the boss's strength each, drawn
  from the dungeon's own monsters (section 7 of the visuals doc), so the
  waves wear the champion down before the boss.

#### Loot by stage

| | Stages 1-5 | 6-10 | 11-15 |
|---|---|---|---|
| **XP / Gold dungeon** | 20 + 8 x stage minutes of the champion's own income (28-60 min) | 68-100 min | 108-140 min |
| **Equipment dungeon** | gear from region = stage, rarity floor Rare; a Common loot chest | region 5 gear, floor Epic; a Rare chest (stages 6-8) or an Epic chest (9-10) | region 5 gear, floor Legendary, one rarity step up on a share of rolls; an Epic chest (11-13) or a Legendary chest (14-15) |
| **First clear** (once per stage) | 5 diamonds | 10 diamonds | 20 diamonds and a title at 15 ("Deathless", "Dragonbane", "Witch-bane") |

**The ticket is the cost**, so the reward per ticket must beat what the same
time in the ordinary loop pays at the stage a player can clear; the "income
minutes" scale guarantees that, because it is the player's own income.
Two tickets a day per kind at stage 15 is about 4.7 hours of extra income a
day from the gold dungeon; **measure it against the weekly gold sinks before
shipping**, and lower the per-stage minutes if it outruns them.

**A lost run** pays a quarter of the stage's reward (no first-clear bonus),
so trying a stage too early costs a ticket's value, not nothing and not
everything.

**Owner's data point (2026-10-30):** the owner's strongest character beats
Malakor **A6** today (about x49 attack on today's ladder). That is where the
strongest live player stands NOW, not a ceiling: that character is not
Transcendent in every slot, not at the level cap, and far from maxed
inheritance and rebirths. So it says stage 10 (A10, x85) is about 1.7x above
today's best and stage 15 about ten times above, but the TARGETS are set
against the theoretical maximum: **the maxed profile** the harness builds
(Transcendent region-5 gear in every slot, the level cap, inheritance maxed,
every rewarded rebirth, the best pets and trees). Stage 10 is what that
profile clears with effort; stage 15 needs about three times its power, so it
stays hard even for a player who has finished everything else. The maxed
profile's power is re-measured whenever something new makes characters
stronger (Hearth, the affix rework, the inheritance rework), and the Beyond
band moves with it. The owner approved the bands, the provisions cap and the
stage-15 target as recommended.

#### Boss abilities that grow with the stage (owner idea, 2026-10-30)

A plain boss is a bigger health bar. Each dungeon boss instead has **four
abilities**: one from stage 1, and one more at stages 4, 8 and 11. An
ability's numbers also grow within its band. Each one is chosen so that a
**build** answers it (crit, speed, armour, sustain), so the three dungeons ask
different things of a character, and each one is an **event** the fight feed
reports, so the playback can show it.

**Koschei the Deathless (XP dungeon) - "his death is not here"**

| From stage | Ability | What it does | Grows to |
|---|---|---|---|
| 1 | **Deathless** | The first killing blow does not kill him: he rises at 15% health | 40% at stage 10; at stage 13+ he rises twice |
| 4 | **Bone Ward** | Every 20 s a bone shield absorbs 5% of his max health | 12%, every 14 s |
| 8 | **Soul Drain** | Heals for 10% of the damage he deals | 30% |
| 11 | **The Needle** | His weakness: crits deal +25% to him, but non-crit hits deal -20% | +60% / -35% (crit builds shine, others struggle) |

**Zmey Gorynych (Gold dungeon) - three heads, three tempers**

| From stage | Ability | What it does | Grows to |
|---|---|---|---|
| 1 | **Three Heads** | His health is three bars, one per head. Each head lost cuts his damage by a third, but the heads left swing 15% faster | 30% faster |
| 4 | **Fire Breath** | Every 6th swing is a breath that burns: 3% of max health a second for 3 s, armour ignored | 6% a second, every 5th swing |
| 8 | **Regrowth** | A severed head grows back after 20 s unless the next head falls first | 10 s (speed builds shine) |
| 11 | **Gold Hide** | Takes 20% less from every hit | 40% (armour penetration and big hits shine) |

**Baba Yaga (Equipment dungeon) - she does not fight fair**

| From stage | Ability | What it does | Grows to |
|---|---|---|---|
| 1 | **Mortar Flight** | Dodges 10% of attacks | 25% (accuracy shines) |
| 4 | **Hex** | Every 15 s, the champion's next 3 hits deal 40% less | next 5 hits, every 10 s |
| 8 | **Bone Fence** | At 50% health she raises a skull-lantern with 15% of her health that must fall before she can be hit again | 30%, at 66% and 33% |
| 11 | **Broom Sweep** | Every 30 s sweeps the larder away: no meal can be eaten for 6 s | 10 s, every 20 s (burst healing and armour shine) |

**How it is built.** The run is simulated swing by swing on the server, so
each ability is a rule inside that loop with an exact, testable effect (the
same discipline as the War keystones). Each fires an event
(`BossAbility: id, value`) into the run's feed. The lobby shows the boss's
abilities **at the chosen stage**, with their numbers, from the server, so a
player can see why stage 9 beat them and what to change.

**How it looks.** Each ability has an icon on the boss's bar, a name banner
when it fires, its own effect and its own sound, described in the visuals
document (`LOOT_DUNGEON_VISUALS.md` section 8): Koschei's green soul flame
returning to his body, Zmey's head falling and the stump smoking, Baba Yaga's
mortar blurring aside, a hex sigil over the champion's weapon.

#### What is tested
- Each boss ability has a test that its rule does exactly what its line says
  at its first and last stage, and that it fires its feed event.
- The simulation of a run is the live combat model on the server (the same
  helpers the tick uses), with the provisions cap, and parity-tested like the
  offline combat path.
- A test pins each band's strength against `BossAscensionRegistry`
  (stage 10 = Malakor A10, stage 6 = A2), so a re-tune of Malakor moves the
  dungeons with it rather than leaving them behind.
- The calibration harness reports, for the owner's character and the maxed
  profile, the highest stage each clears; the target is checked there.

### 2.7 Built: the equipment dungeon (2026-10-31, autonomous run)

> **Superseded by the rework in 2.8.** **Hidden from players since client 1.49.1 (owner, 2026-10-31: "far from how
> I want it to look").** `NetworkBroadcastSystem.Dungeon.cs` answers every
> account except the admin ones with a closed lobby (no stages) and refuses a
> run with `Outcome: "Locked"`; `Game.svelte` shows the menu entry only to an
> admin. Code, tables, tickets and first-clear masks stay. Remove both gates
> (`DungeonClosedReason`, `MENU_HIDDEN_PLAYERS`) when the rework lands, and put
> `Dungeon` back in `scripts/screens.mjs`.

The first slice of the phase - Baba Yaga's equipment dungeon (D4) - with the
decisions D1-D6 as approved. What is in it, and the calls made along the way:

- **The fight** (`DungeonSimulator`): the live character (a snapshot of the
  live payload, main slot) through five waves at 40% of the boss and then the
  boss, health carried over, **at most five meals**. It reuses the live
  helpers (LiveCombatStats, EffectiveMaxMilliHpFor, CombatDamageModel's
  swing, the benchmark's monster swing, FoodRegistry's heal, the auto-eat
  cooldown) and replaces rolls by their expected values, as the wall
  benchmark does - so a run is deterministic, honest to play back and
  testable. *Own decision:* expected values instead of seeded rolls. A run
  that can flip on a die is more exciting, but a deterministic answer is what
  the lobby can explain ("she had 40% left") and what a test can pin; rolls
  can be layered on later without changing the rules.
- **Stages:** 1-5 = region N's boss (as farmed) x1.1, 6-10 = Malakor A2-A10,
  11-15 = A10 x1.5 then x1.4 a stage (stage 15 = 5.76x stage 10's attack,
  pinned). The absolute top still waits for the task-112 re-measure.
- **Baba Yaga's four abilities**, each a rule in the loop and an event in
  the feed, growing linearly to stage 15: Mortar Flight (dodge 10% -> 25%),
  Hex from 4 (every 15 s -> 10 s, next 3 -> 5 hits deal 40% less), Bone Fence
  from 8 (a lantern of 15% -> 30% of her health at half health), Broom Sweep
  from 11 (every 30 s -> 20 s, no meals for 6 s -> 10 s). *Own decision:* the
  Bone Fence rises once (at 50%) at every stage - the second and third
  lanterns at the top are left for the tuning pass.
- **Tickets** (`DungeonEngine`): 2 a day, at most 4, topped up on read
  (no job). A new row's first read gives the cap.
- **Unlock:** the lobby opens with the region 2 boss; stage N needs its
  boss (region N, Malakor from 6) and stage N-1 cleared.
- **Rewards:** a won stage pays a piece of region min(stage, 5) with a
  rarity floor of Rare / Epic / Legendary by band, and a loot chest (Bronze
  1-5, Silver 6-8, Gold 9-13, Ancient 14-15), plus 5 / 10 / 20 diamonds on
  the first clear. *Own decision for "a lost run pays a quarter":* a Bronze
  chest one time in four, which is about a quarter of a win's chest and
  needs no second reward table.
- **REST**, not the wire: `GET /api/v1/dungeon`, `POST /api/v1/dungeon/run`
  (one transaction: ticket, fight, reward); meals eaten leave the live
  larder as a negative delta the tick applies. Dev route
  `/api/v1/dev/dungeon/tickets` for exercise.
- **Client:** a Dungeon screen under Play - stage grid by band, the boss's
  abilities with their numbers at the chosen stage (from the server), the
  run played back line by line, the result card.

**Measured on a production copy (2026-10-31, task 112 re-run, player 8,
level 112, after the DiamondRework migration):**
- Ascension headroom on Malakor: the real character x45 (clears A5, not A6),
  the maxed profile x70 (clears A7, not A8). So A10 (x85) is above even the
  maxed profile now - the old x32/x49 figures were before the War tree.
- The first dungeon fight model (the reference-gear swing the wall benchmark
  uses) dealt about a twentieth of a real character's live damage - it has no
  skill tree and no codex. It now uses the offline projection's swing
  (`HuntingProjection.PlayerSwing`, parity-tested against the live tick).
- With the waves and the meal cap a run is far harder than the Ascension
  fight alone: at Malakor's raw ladder the maxed profile cleared only stage 6.
  The harness searched the band scale: the maxed profile wins stage 10 up to
  x0.256 using all five meals. **Shipped: stage 10 = Malakor A10 x0.24**
  (attack; health x0.24^0.68), stages 6-9 stepping geometrically from stage 5,
  Beyond as designed (x1.5, then x1.4 a stage).
- Result: the owner's character clears **stage 9**; the maxed profile clears
  **stage 10 with every meal spent** and loses stage 11 with Baba Yaga at 69%;
  stages 12-15 are out of its reach. That is the target the owner approved.

**Not built yet:** the XP (Koschei) and Gold (Zmey) dungeons, tickets from
chests, first-clear titles at stage 15, the run's art and sounds from the
visuals doc, and the calibration-harness report of the highest stage the
owner's character and the maxed profile clear (needs task 112).

### 2.8 The rework (owner, 2026-10-31) - plan and what is built

The owner hid 2.7 ("far from how I want it to look") and gave the direction.
Decisions, all approved the same day:

- **Three gates side by side** - XP (Koschei), Gold (Zmey), Equipment (Baba
  Yaga) - each with a stage field under it; stage N+1 opens when N is beaten.
- **The fight is shown, not written.** The run plays in a scene like the
  Combat screen's, not as a log.
- **Not wholly passive (slice 3).** During a fight the enemy telegraphs an
  attack or a sequence of attacks and the player swipes up, down, left or
  right to dodge (arrow keys or WASD on a PC). A miss costs a run-long
  penalty (less damage, health or armour); a clean dodge offers **one of
  three bonuses**, Vampire Survivors style (damage, crit, crit damage, health,
  armour...). **Bonuses and penalties last for the run only** and stack.
  Sequences grow with the stage: longer, with a shorter window; about one per
  wave and two or three in the boss fight.
- **A run becomes a live session on the server (slice 3).** The server
  simulates to the next telegraph, sends it, waits for the swipes, judges
  them against the right directions and the window, and goes on. The client
  never reports "I dodged" - the server knows the answer. Bonuses are capped,
  because a bot can swipe too.
- **Sweep for cleared stages (slice 3):** a stage already beaten can be paid
  out for a ticket without playing it, so the minigame is for new stages and
  not a daily chore.
- **The roster grows to three characters (slice 4):** a second champion
  after stage 6 of a dungeon, a third after stage 11, so an endgame player
  gears three characters. The Beyond band is then calibrated for three.

**Order, each slice admin-only until the owner has seen it:**
1. The three gates, the stage field, the XP and Gold dungeons. **Built.**
2. The run played back as a scene. **Built.**
3. Dodges and bonuses, the server-side run session, sweep. **Built.**
4. Roster slots 2 and 3. **Built (uncalibrated).**
5. The remaining art and sound (fight backgrounds, wave monsters, ability
   effects and sounds from LOOT_DUNGEON_VISUALS.md section 8).

**What slices 1-2 built:**
- `DungeonBoss` (`Domain/Combat/DungeonBosses.cs`): one class per boss with
  its four abilities as hooks the loop calls (hit factor, timed abilities,
  shield, survive-at-zero, swing speed and size, burns, can-eat). Koschei:
  Deathless (rises at 15% -> 40% by stage 10, twice from 13), Bone Ward,
  Soul Drain, The Needle (crits +25 -> 60%, other hits -20 -> -35%). Zmey:
  Three Heads (each head lost cuts his damage by a third, the rest swing
  15 -> 30% faster), Fire Breath (every 6th, from 10 every 5th swing; 3 -> 6%
  of max health a second for 3 s, armour ignored), Regrowth (a head grows
  back after 20 -> 10 s unless the next falls first), Gold Hide (20 -> 40%
  less from every hit). Baba Yaga as in 2.7.
- `DungeonProgress` table: one row per player per dungeon (tickets, ticket
  day, best stage, first-clear mask). Migration `AddDungeonProgress` copies
  the four old PlayerRecords columns into Kind 0 rows; the columns stay,
  unread, to be dropped later. **Tickets are per gate**: 2 a day each, cap 4.
- Rewards: XP and Gold pay **20 + 8 x stage minutes** (28 -> 140) of the
  champion's own income - the best hunting XP an hour over the monsters of
  open regions they survive (`BestHuntingXpPerHour`, the projection the
  Combat advisor uses), or the account's earned gold an hour
  (`GoldRewardScale`). A lost run pays a quarter. First clears pay 5 / 10 /
  20 diamonds **in each dungeon** (525 a player, once). XP reaches the live
  payload through `DungeonXpGrantQueue` and the warp path's level-up loop;
  gold is a ledger write (`GoldIncomeSource.Dungeon`, never in the earned
  base) and a ReloadState.
- Every run event carries both bars in percent, and the result names the
  five wave monsters (the ordinary monsters of the stage's region), so the
  scene animates from server numbers and never infers health.
- Client: `routes/Dungeon.svelte` (three gates with painted art, ticket pips,
  a stage stepper per gate, the boss's abilities at the chosen stage) and
  `lib/ui/DungeonRun.svelte` (gate-opening light clip, wave pips and the
  boss skull, the wave monster or the boss portrait, both bars, ability and
  meal banners, the boss intro card, 1x / 2x / Skip, the verdict and the
  reward). Gate and boss art: `tools/assets/gemini.json`
  (`dungeon_gate_*`, `dungeon_boss_*`), in `client_web/public/dungeon/`.
- Still admin-only in production. The server also opens it where its dev
  tools are on and a dev build shows the menu entry, so `exercise.mjs` runs
  the whole flow on the dev fixture; `/api/v1/dev/dungeon/unlock` counts the
  region bosses as beaten in memory only, for the fixture.

**What slices 3-4 built (2026-10-31, after the owner's second look):**
- The owner changed one number: a clean dodge offers **two** bonuses, as
  cards with their line, not three.
- `DungeonRunner` (`Domain/Combat/DungeonRunner.cs`) is the simulator's loop
  with its state kept, so a run can stop and go on. `DungeonSimulator.Run`
  is the same runner without pauses (calibration, sweep, tests), so a live
  run plays the numbers the calibration measured.
- Telegraphs: one as each wave opens, one as the boss fight opens, and one
  more each below two thirds and one third of the boss's health (when he
  lives that long). One swipe on stages 1-4, one more every four stages, at
  most four (five for the boss); 1.4 s a swipe on stage 1 down to 0.77 s on
  stage 15, plus 0.6 s to read (`DungeonPerks`).
- The server holds the run (`DungeonLiveRuns`, in memory, one per player):
  `POST /dungeon/run` takes the ticket and answers the first segment;
  `POST /dungeon/act` with `telegraph` shows the swipes and starts the
  window on the server, `answer` judges them (right order, inside the window
  plus 1.2 s for the network, not faster than 90 ms a swipe), `pick` takes a
  bonus. The client never says "I dodged".
- A miss lands twice an ordinary hit on every champion and a random penalty
  (-10% damage, +10% damage taken, -8% speed, -10% max health). Bonuses:
  damage, crit chance, crit damage, max health, damage taken, leech, speed,
  Second Wind - each capped (the caps are the anti-bot).
- A run left alone is played out with every telegraph missed and paid when
  the player starts another run or opens the lobby 10 minutes later. A server
  restart mid-run loses that run and its ticket.
- Sweep: a beaten stage pays as won for a ticket, no fight
  (`RunAsync(sweep: true)`); no first clear, by definition.
- The party: a second champion after stage 6 of THAT dungeon, a third after
  stage 11, from roster slots that hold a character. Each swings with their
  own gear; each enemy swing lands on the next champion through their
  armour; the health is one shared pool, so the bosses' rules work unchanged.
  **Not calibrated:** a party of three is far stronger than one champion and
  the stages do not yet scale for it.

**Not measured yet:** Koschei's and Zmey's stages against the maxed profile.
`AscensionCalibrationHarness` now reports all three dungeons; the band scale
was calibrated on Baba Yaga alone, and the other two bosses' abilities make
their stages easier or harder for a given build. Run it on the production
copy before the dungeons open to players, and record the numbers here.

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
- **D6. Stages and the top (2.6a):** 15 stages in three bands, stage 10 =
  Malakor A10, stage 15 about 5.8x its attack and about three times a maxed
  profile, a 5-meal provisions cap per run. Recommendation: yes, with the
  absolute values set after task 112 re-measures the headroom.
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

## 5. Achievements, the quest line and the tutorial as one progression

### 5.1 What exists today

Three systems answer "what should I do, and what do I get for it", and they
were built at different times:

| System | Where | What it pays |
|---|---|---|
| **Onboarding cues** ("Got it" cards, objectives) | `stores/tutorial.ts`, seen-set on the player row | nothing |
| **Quest line** (the tutorial chain) | `QuestLineRegistry`, `QuestLineEngine` | gold and materials, scaled to the region |
| **Lifetime achievements** | `AchievementMilestones` | diamonds, and Logistics also gathering speed |
| Titles | `TitleRegistry` | a cosmetic title (Deep floors, Ascension, events) |

**The lifetime achievements are four, with 13 tiers in all:**

| Achievement | Tiers | Diamonds per tier |
|---|---|---|
| Monster Slayer | 1 (10,000 kills) | 100 |
| Treasury (gold spent) | 4 | 10, 50, 250, **1,000** |
| Forging (fusions, then fused rarity) | 4 | 15, 75, 200, **1,500** |
| Logistics (harvests) | 4 | 10, 50, 200, 800 |

That is the owner's complaint in numbers: one milestone pays 1,500 diamonds
(25 weeks of the Delve), while reaching a new region, beating a boss, a first
rebirth, a bred child or a maxed village pay no achievement at all. Achievements
cover four counters out of the dozens of things the game has, and nothing tells
the player they exist until a counter crosses a tier.

### 5.2 The proposal: one progression with three faces

**One registry of achievements**, server-owned, each with:
- a **category** (Combat, Bosses, Gathering, Crafting, Village, Family,
  Delve and Deep, Social, Collection, Events);
- a **counter** the server already keeps, or a one-off event (a first clear,
  a first rebirth);
- **tiers** where a counter has a natural ladder;
- a **reward** from one table (section 5.3);
- an optional **title** or cosmetic for the hardest ones.

**The quest line becomes the first chapter of it.** Each quest step is an
achievement in a "Getting started" category, done in order. It keeps its
current gold-and-material reward, and the tutorial's objective cards point at
the next unearned achievement instead of a separate list. One source of truth
for "what next", as this codebase's rules ask.

**Onboarding cues stay as explanations**, not goals. A cue explains a screen
the first time; an achievement is the goal that sends the player there.

### 5.3 One reward table

Rewards should follow how hard a goal is, measured in hours of play, not the
size of the number in the counter. Proposed bands:

| Band | Typical goal | Reward |
|---|---|---|
| Starter | first craft, first gather, first Delve run | gold and materials (the quest line's table) |
| Bronze | about 1-3 hours | 5 diamonds or a Bronze loot chest |
| Silver | about a day | 15 diamonds or a Silver chest |
| Gold | about a week | 40 diamonds or a Gold chest |
| Legendary | about a month or more | 100 diamonds, a title, and a cosmetic |

**No single achievement pays more than about 100-150 diamonds.** The 1,000
and 1,500 tiers today would be split into more goals across the ladder, so
the total is similar but the player is paid along the way. Players who
already crossed a tier keep what they were paid.

**A lifetime total** for all achievements, measured once the list exists, so
the achievements tap is known against the weekly taps in section 3.

### 5.4 More, and more interesting

A first list to grow from (each needs a counter the server already has, or a
new one-line counter):
- **Bosses**: each regional boss's first clear; each Ascension step;
  Chernobog's milestones; beating a boss with no food eaten.
- **Regions**: reaching each region; clearing every monster of a region.
- **Family**: first marriage, first child, a child with an aptitude at 30,
  40, 50; five generations in one bloodline.
- **Rebirth**: first rebirth, rebirth 5, 10, 20.
- **Village**: each building at level 5 and 10; all buildings maxed.
- **Delve and Deep**: a full eight-floor clear; Deep floors 10, 15, 20, 30.
- **Collection**: monster pets found; cosmetics owned; every item of a set.
- **Gathering and crafting**: each profession's mastery milestones; a
  Legendary tool.
- **Hidden achievements** for odd things (a kill at 1 HP, 100 fails in a
  row at the Delve), shown as "???" until earned.

### 5.5 The announcement

**Correction (2026-10-30):** most of this already exists. `AchievementToast.svelte`
shows a card with a seal, the tier and the reward, plays `achievement_unlock.wav`,
and opens the Book of Deeds (Progress, Goals tab) on a tap. What is still missing
is the bigger Legendary card and the offline queue. The original text:

A real moment when an achievement is earned:
- A **banner** slides in at the top with the achievement's icon, name, tier
  and reward, plus a short **sound** (a new clip in the audio registry).
- **Legendary** tiers get a bigger card in the middle of the screen with a
  light burst, like the rare-drop card, and a longer sound.
- Several at once **queue**, never stack on top of each other.
- The server sends the event (a new packet, or a field on an existing
  notification), so it fires once, also for something earned offline (shown
  after the offline summary).
- Settings can mute the sound like every other clip.

### 5.6 The screen

One **Achievements** screen (or a tab of Progress):
- categories down the side, a progress bar per category;
- each achievement with its tiers, its progress (`12,430 / 100,000`) and its
  reward visible **before** it is earned, so it is a goal and not a surprise;
- "closest to done" at the top, which is also what the tutorial's objective
  card points at.

### 5.7 Decisions for the owner

- **A1. Merge the quest line into achievements as their first chapter?**
  Recommendation: yes. It removes one of the three overlapping systems.
- **A2. The reward bands and the 150-diamond ceiling per achievement?**
  Recommendation: yes, and the old 1,000 and 1,500 tiers split across new
  goals. Nobody loses what they were already paid.
- **A3. Diamonds or chests as the default reward?** Recommendation: a mix -
  diamonds for Bronze to Gold, chests from Silver up as an alternative, so
  achievements feed the loot-chest system once it exists.
- **A4. Order:** after loot chests (so chests can be rewards) and together
  with the diamond rework (section 3), because achievements are a diamond tap
  and have to be measured with the others.

---

### 5.8 Built (2026-10-31, autonomous run - decisions recorded here)

**PR 1 - one list.** The Book of Deeds opens with **Getting started**, which
is the quest line itself (same steps, same claim, same query as the Home
panel; the Home panel stays as the pointer to the next step), and **Closest
to done**, the unfinished goals nearest their target across the open
chapters and the lifetime tiers, ranked on the server (`DeedCloseness`).
Goals without a real fraction (one-step deeds, Master Smith's rarity tiers,
whose progress counts fusions) are left out rather than shown wrong.

**PR 2 - one reward table and one diamond change.**
- Lifetime tiers pay **5 / 15 / 40 / 100** only. Treasury became seven tiers
  (100k, 1M, 5M, 25M, 100M, 500M, 2.5B), Master Smith six (50, 200, 500
  fusions; rarity 10, 12, 14), Logistics keeps its four thresholds and its
  speed bonus with the new diamonds. Every old threshold is still a
  threshold, so the `DiamondRework` migration moves a stored tier to its new
  index (`AchievementMilestones.MigratedTier`, tested against the ladders):
  nothing already paid is paid again or taken back.
- **Alchemist** (new, id 5): 1 / 10 / 50 / 250 potions drunk, counted on the
  tick and offline - the potion achievements moved here from the potions PR.
- **Delve 60 -> 80** a week (M1). The world boss milestones and the
  ordinary-kill diamond chance were sized "like the Delve" and stay at 60
  (`DelveRegistry.TapYardstickDiamondsPerWeek`): the owner raised the Delve,
  not every tap.
- **Inheritance Option B** (I1): ten levels of +4% at 100 x 1.25^level.
  Owned levels convert to ceil(old / 2) - never less bonus (old 11 = 22%
  becomes 6 = 24%), no refund needed. **The milestone effects at levels 5
  and 10 are NOT built yet** - twelve new mechanics, each needing parity;
  they are their own PR.
- **The one model** (`DiamondReworkTests.TheEconomyModel_Report`): 100 free
  diamonds a week (Delve 80 + Deep 20). First level 100 (1 week); one stat
  to level 5: 820 (8.2 weeks); one stat to 10: 3,323 (33 weeks); all six:
  19,938 (3.8 years, was ~23 years at 118,000); every lifetime tier once:
  950 diamonds (9.5 weeks of taps, was ~4,260 in four milestones).

## 6. Suggested order

1. The Deep reward (done, section 0). Watch the Deep's spend for two weeks.
2. Loot chests, first sources: the Deep and boss first clears (section 1). M.
3. Diamond taps, the inheritance rework and the achievements rework as one
   balanced change (sections 3-5). They are all diamond taps or sinks, so
   they are measured with one model. L, in two or three PRs: the registry and
   the screen, the rewards and inheritance, the announcement.
4. Dungeons, the equipment dungeon first (section 2). XL, after Hearth and the
   affix rework in `ROADMAP_2026_10_30.md`.

Item 2 is small enough to fit between the roadmap's phases. Items 3 and 4 are
phases of their own.
