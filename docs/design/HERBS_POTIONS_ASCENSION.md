# Herbs and potions, rare materials, and the Ascension reset - a proposal

Written 2026-10-30 from the owner's questions in one conversation. Each part
says what is true today (measured or read from the code), proposes a design,
and ends with the decisions it needs. Nothing here is built.

---

## 1. Herbalism and potions

### 1.1 What is true today

- The code knows four gathering professions (wood, mining, fishing,
  herbalism) and two recipe chains that would use herbs (Cooking and Alchemy,
  `RecipeDefinition.ProfessionType` 4 and 5). That is the original design.
- **The game has no herbalism node.** `gathering_nodes.json` holds 5 wood,
  5 mining and 5 fishing nodes; herbalism has 0. Every player's herbalism
  mastery is 0 (measured 2026-10-30).
- The Hearth node **Green Thumb** (+herbalism speed) was buyable and paid
  nothing; it is pending since PR #245 (production had 0 buyers). The
  fish-and-herb pairs keep paying through fishing.
- There are no monster materials: `items.json` has 180 items, and the only
  material entries are three woodcutting ones. Monster drops are equipment,
  gold and chests.

### 1.2 The proposal

**Herbs, one pair per region**, gathered like fish (a normal herb and a rare
one, the same rare-find roll the other professions use):

| Region | Normal herb | Rare herb |
|---|---|---|
| 1 Sunlit Plains | Yarrow | Golden Chamomile |
| 2 Mire | Marsh Mallow | Will-o'-wisp Moss |
| 3 Canyon | Thyme | Dragon's Blood Sage |
| 4 Fjord | Sea Kale | Frost Lichen |
| 5 Astral | Mugwort | Fern Flower (the Kupala night flower) |

**Potions are buffs, not heals** (the larder already heals). Brewed in the
Workshop from herbs (the rare herb for the stronger tier), one active per
kind, a timer that runs online and offline alike, so the idle loop and the
watched fight agree (the parity rule every War mechanic is held to):

| Potion | Effect | Typical length |
|---|---|---|
| Elixir of the Hunt | +XP from kills | 1 h |
| Draught of Fortune | +loot luck | 1 h |
| Goldmoss Tonic | +gold from kills | 1 h |
| Ironbark Brew | +armour | 1 h |
| Swiftroot Tea | +attack speed | 30 min |
| Gatherer's Draught | +gathering speed | 2 h |
| Kupala Night | +rare find (gathering) | 2 h |
| Boss-bane Philtre | +damage against region and world bosses | one boss fight |

Each potion has a normal and a strong version (from the rare herb). Numbers
are sized against what already pays the same stat (the War nodes, the pets,
the new ring affixes) and measured before shipping, the way the affixes were.

**Where it touches the rest of the game:**
- **Larder:** a "Potions" shelf beside the food, and an auto-drink rule
  ("keep Elixir of the Hunt up") like auto-eat.
- **Crafting/Workshop:** an Alchemy tab with the recipes.
- **Hearth:** Green Thumb comes back, plus the fish-and-herb nodes pay herbs
  too. A new Hearth node could lengthen potions.
- **Loot chests:** herbs of the chest's region as a material line.
- **Quest line / achievements:** first herb, first potion.

### 1.2a Build plan (2026-10-30, after H1-H3 were approved)

**What already exists and is reused:**
- Herbalism is a profession in the tick (`HerbalismMasteryXp/Level`,
  `AdvanceMastery`, the realtime and offline gathering paths handle type 3),
  and `ContentRegistry` already has herb loot-table slots (401-412) - only
  `gathering_nodes.json` has no herbalism node, so nothing reaches them.
- A dormant potion engine (`ConsumableEngine`): payload slots
  `ActiveOffensivePotionId` / `ActiveDefensivePotionId` with millisecond
  countdowns, checkpoint persistence as an absolute expiry, the offline
  projection and the wire already carry them. It was left inert on
  2026-10-07 because no content fed it. A new consumable needs only an
  items.json entry with the BaseId marker.
- The images: 10 herbs and 8 potions are generated (tools/assets/staging,
  prompts in ASSET_PROMPTS.md 9.2-9.3).

**Steps (one PR each, each with exercise coverage):**
1. **Herbs gathered:** 5 herbalism nodes in `gathering_nodes.json` (one per
   region) and 10 herb items (normal + rare per region) in `items.json`,
   wired to the existing loot slots. Green Thumb comes off pending. Herb
   icons into the sprite pipeline.
2. **Potions brewed:** 8 potion items (normal + strong = 16 entries) and
   Alchemy recipes (`RecipeDefinition.ProfessionType` 5) from herbs, the
   strong version from the rare herb or rare fish (R1's edge for the rare
   catch). Crafting screen tab.
3. **Potions drunk:** the effects through ConsumableEngine's slots (one
   offensive, one defensive, plus a gathering slot if needed), each effect
   read where its stat already is (XP, loot luck, gold, armour, attack speed,
   gathering speed, rare find, boss damage) - online and offline alike (H2),
   with a parity row each. Magnitudes sized against the War nodes and pets,
   and **the wall scan run with each combat potion active**: a potion must
   not lower a wall the way the rare-fish heal would have.
4. **Larder and UI:** a Potions shelf beside the food, an auto-drink rule
   ("keep X up"), the active potion and its timer in the header.
5. **Sources and sinks:** herbs as a material line in loot chests; potions in
   the quest line and achievements.

**Step 1 built (2026-10-30, branch `claude/herbs-gathered`):**
- Nodes 4001-4005 (profession 3) with the fishing nodes' thresholds and XP
  (30/45/60/80/100 ticks, 15-140 XP); items 442-451 (common + rare per
  region, values 5/20/80/320/1280 like the fish); loot rows 107-116 at 90/10.
  Rare herbs join `IsRareMaterial` (R1). Herbs are not food - the larder's
  raw-fish set reads the fishing band only (pinned in GatheringMasteryRoutingTests).
- **Correction to the plan: herbs have no tool (owner, 2026-10-30).** The plan
  assumed nothing about tools, and the code's tool switch fell through to the
  ROD for profession 3, so a fisher's rod would have sped up a herbalist who
  never wore it. Herbalism now reads tool tier 0 everywhere; the speed is the
  mastery (40 x sqrt(level) %), plus Green Thumb and the generic extras.
- Measured harvest time by hand (no tool, no extras), region 1 / region 5:
  mastery 1: 2.1 s / 7.1 s; mastery 10: 1.3 s / 4.4 s; mastery 25: 1.0 s /
  3.3 s; mastery 50: 0.7 s / 2.6 s. Region 5 at mastery 25 is ~1,090 harvests
  an hour (~980 Mugwort, ~110 Fern Flower before yield bonuses). Step 2 sizes
  the potion recipes against these numbers: a potion lasts 1-2 h, so one
  hour of picking must buy several hours of a potion, not one.
- Green Thumb is buyable again. A side fix on the Gathering screen: the
  monolith line showed the MINING monolith on every non-wood card, though the
  server pays a monolith only to wood and ore.

**Steps 2-3 plan (2026-10-31, owner set the price):**
- **Price (owner):** about ten minutes of picking buys an hour of effect. A
  normal potion costs 150 common herbs of its region; a strong one 100 common
  + 15 rare (about twenty minutes). Both last 1 h (gathering ones 2 h).
- **Which region brews what** - an early potion from early herbs, so each
  region's pair has a use and a new player can brew the first one:
  r1 Yarrow: Elixir of the Hunt; r2 Marsh Mallow: Ironbark Brew and
  Gatherer's Draught; r3 Thyme: Swiftroot Tea and Goldmoss Tonic; r4 Sea Kale:
  Draught of Fortune; r5 Mugwort: Kupala Night and Boss-bane Philtre.
- **Three slots, one potion each:** *rewards* (Hunt, Fortune, Goldmoss) in
  the existing offensive slot, *combat* (Ironbark, Swiftroot, Boss-bane) in the
  defensive slot, and a new *gathering* slot (Gatherer's, Kupala) - one new
  payload/wire pair, through the add-command skill.
- **Two traps found while reading (fix in step 3):**
  1. `StatsCalculator` gives EVERY offensive-slot potion flat damage and
     armour penetration by tier, and every defensive one HP, armour and
     dodge. Those formulas belonged to the deleted legacy potions; a new
     potion must pay only its own effect, so the generic block goes and a
     per-kind table replaces it.
  2. The offline projection gets the potion wrong both ways. The load turns
     the stored absolute expiry into "remaining from NOW" before the
     projection runs, and the projection reads whatever is left in the slot -
     so a potion still running at sign-in counts for the WHOLE absence, and
     one that ran out while away counts for none of it. H2 needs the payload
     to carry each slot's absolute expiry, and the projection to split the
     absence into at most four stretches at the expiry times (three slots),
     each projected with exactly the potions that were running then. A
     parity row per potion pins it.
  3. `ConsumeConsumableAsset` sets the buff and takes NOTHING from the
     inventory: the consumable branch in SimulationEngine returns before the
     async half that would remove a row (and that half reads
     EquipmentInstances, where a stack never lives). With real potions that
     is an unlimited free buff. Potions do not use it.
- **The shape: a potion shelf, built like the larder.** Each of the three
  slots gets a shelf on the payload - item id, count, auto-drink on/off -
  stocked from the chest by a command (as the larder is), so drinking is a
  synchronous decrement on the tick and the checkpoint persists it, with no
  async race. Auto-drink refills a slot from its shelf the tick its potion
  runs out - online, and offline analytically: with N on the shelf and D per
  potion, the slot is covered for min(absence, remaining + N x D), which
  the stretches above use. One new command per action (stock, drink, toggle)
  through the add-command skill; new payload/PlayerRecord fields with an
  additive migration.
- **Magnitudes** are measured, not picked: each against what already pays the
  same stat (War nodes, pets, affixes), and the wall scan run with every
  combat potion active before it ships.

### 1.3 Decisions

- **H1.** Build herbalism + potions as described? Recommendation: yes, as its
  own phase after the combat affixes (3.6), because potions are stat buffs and
  must be measured against the same benchmark.
- **H2.** Potions online and offline alike? Recommendation: yes (parity).
- **H3.** Monster materials back (for potions or crafting)? Recommendation:
  no for now - herbs cover the need without a second drop economy.

---

## 2. Rare materials give the same stats as normal ones

### 2.1 What is true today

Rare materials (golden birch log, the rare ores, rare fish) are used in tool
recipes such as the Golden Birch Axe, but a tool's power comes from its
region and quality alone, so the Golden Birch Axe has the same stats as the
Birch Axe. Rare fish heal the same share as normal fish of their region.

### 2.2 The proposal

- **Tools from rare materials** craft one quality step higher and carry one
  extra tool affix roll (gathering speed, yield or rare find).
- **Rare fish** heal half again as much and count double for the "fish
  gathered vs eaten" balance.
- **Rare herbs** brew the strong potion (section 1).
- **Workshop commissions** may take rare materials as an optional extra for a
  higher rarity floor.

### 2.3 Decision

- **R1.** Give rare-material crafts a real edge as above? Recommendation: yes,
  measured against the gathering economy (task 131). **Approved 2026-10-30.**

### 2.4 Built, and what waits (2026-10-30)

- **Built:** a recipe that takes a rare material (`VillageManagementEngine
  .IsRareMaterial`: the golden logs, rare ores and rare catch) crafts one
  quality step higher with one more affix. Golden Birch Axe vs Birch Axe:
  Common with 2 affixes vs Normal with 1.
- **Rare fish x1.5 heal WAITS for a measurement.** The wall benchmark eats a
  region's common fish (`FoodRegistry.FirstRawFishOfTier`), so the walls
  would not see the change, but players would: they catch 5-15x what they
  eat, so the rare catch alone covers most bites, and x1.5 would act as a
  near-global +50% sustain - and the larder is this game's difficulty
  ceiling.
- **Measured the same day and DROPPED:** the wall scan with the benchmark's
  bite healing x1.2 broke the walls of regions 1, 2, 3 and 5 and let region
  5's wall gear win tier 6; x1.5 broke all five (region 2's window moved from
  1.861-1.884 to 2.782-2.819). No heal bonus is small enough to matter and
  safe at once. **The rare catch gets its edge elsewhere:** it becomes the
  rare ingredient of the strong potions in the herbs phase (section 1), and
  is worth more at the chest sale. Heal stays the same as the common fish.

---

## 3. Ascension: chests now, and a reset and redesign

### 3.1 Built the same day (PR on `claude/chest-polish`)

- Every new Ascension step pays a loot chest of a random tier: Bronze 50%,
  Silver 30%, Gold 15%, Ancient 5%.
- First-clear and Ascension chests hold only their own region's loot; Deep
  chests use the player's highest region.

### 3.2 The reset and redesign (proposal)

The owner asked to reset Ascension and its rewards and design them again.

**What a reset touches:** `boss_ascension_progress` (each player's highest
step per boss), the Ascension titles (`player_titles`) and frames
(`cosmetic_items`, source Ascension). **It is a data change on live
players** - progress they earned would go away - so it needs:
1. a backup (taken before every deploy anyway);
2. a clear in-game message ("Ascension was redesigned; your old steps were
   converted to ...");
3. a fair conversion, so nobody loses what they paid for.

**Recommendation for the conversion:** keep every title and frame already
earned (they are cosmetic and a player's history), reset only the step
counter, and pay each player the new design's chests for the steps they had
cleared, once, by mail. Then the new ladder starts from step 1 for everyone,
and nobody is behind for having played early.

**A new design to discuss** (each step = one modifier on the boss):
- 10 steps per boss, stepped in a clear curve (health, attack, a time limit,
  then one mechanic per band: enrage, shield phases, adds).
- Rewards: a chest every step (as built), a title at 5 and 10, a frame at 10,
  and a small permanent account bonus at 10 per boss (for example +1% damage
  against that region's monsters), so the ladder pays progress, not only
  cosmetics.

### 3.2a A concrete A2 (drafted 2026-10-30, after A1 was approved)

**What exists:** 10 steps per boss; health and attack grow geometrically to a
per-region ratio at step 10 (`BossAscensionRegistry`), and the even steps add
a time limit (200% -> 120% of Swift). Rewards: a title per step, a Laurel
frame and a Crown frame at step 10, and since today a loot chest per step.

**Why reset only WITH a new ladder:** resetting the counters on the same
ladder would only let everyone re-climb known steps for chests - a gift, not
a redesign. So A1 ships together with this:

| Steps | What the boss gains | Reward per step |
|---|---|---|
| 1-3 | health and attack only (today's curve) | chest (50/30/15/5) |
| 4-6 | + **Enrage**: below 30% health its attack rises by a third | chest, +5% Silver-or-better |
| 7-9 | + **Ward**: every 25% of health it shields itself for 3 s (no damage) | chest, +10% Gold-or-better |
| 10 | + both, and the tightest clock | Gold-or-better chest guaranteed, the Crown, and **+2% damage against that region's monsters** (account-wide) |

- The two mechanics are each one rule in `RunCombatTick` with a projection
  term and a parity row - the same discipline as the War mechanics.
- The step-10 bonus is +2% per region, at most +10% for all five, against
  monsters of that region only; sized under one War node (Edge) so it never
  outweighs the tree.
- **Conversion (A1, approved):** keep every title and frame; reset the step
  counters to 0; mail each player one chest per step they had cleared, at the
  new odds, so early players are paid for what they did and nobody re-climbs
  for a double reward. In-game message: "Ascension has been rebuilt with new
  mechanics. Your titles and frames stay; your old steps were paid out as
  chests in your mail."
- Measured before shipping: each step's win line with the region's best gear
  (BossChallengeCalibrationTests' Malakor ladder), so step 10 stays beatable.

### 3.3 Decisions

- **A1.** Reset with the conversion above? Recommendation: yes, with the
  backup and the message. **Approved 2026-10-30.** Ships together with A2.
- **A2.** The new ladder's shape and the step-10 account bonus? Needs the
  owner's call; it changes power, so it is measured before shipping.
