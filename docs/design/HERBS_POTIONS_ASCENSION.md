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
