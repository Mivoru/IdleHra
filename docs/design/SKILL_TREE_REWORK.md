# Skill tree and affix rework - design

Status: **proposal, awaiting owner decisions** (section 9). No code yet.
Written 2026-10-29 against production 1.0.1310 / client 1.37.0. It merges
two outside proposals (Gemini: four hybrid combat paths, cross-domain
conversions; ChatGPT: five disciplines, draft mode, Recast/Refine, affix
families) with what the code and production actually allow.

Read `docs/architecture/STATS_REFERENCE.md` sections 0, 4, 7 and 8 first.
Every number below is a **starting value for the balance harness**
(section 8), not a balance decision.

---

## 1. What is wrong with the current tree

- **It is a list, not a tree.** Five limbs of root, fork and crown, drawn as
  rows with an Assign button each. The only real decision is the bough fork.
- **Combat and economy compete for one pool.** Fortune and Insight cost the
  same points as Precision and Cruelty. Region bosses gate every region, and
  respec is scarce (one free per run). So a player who invests in economy
  stalls at a wall and cannot cheaply undo it. Both outside proposals saw
  this. Neither solves it (section 2).
- **Almost every node is a flat percentage** that attributes or affixes
  already give. Only the five crowns change how a fight plays.
- **The client mirrors the tree by hand** (`SKILL_TREE_NODES` in
  `client_web/src/lib/net/commands.ts`). Twenty node levels ride every
  `StateUpdatePacket` as twenty named byte fields. Any new node means a
  packet change, a protocol regeneration and a second hand-edited copy of
  the rules.

Production, 2026-10-29: **4 accounts have spent any points**. Twelve nodes
are in use and one crown (Double Strike) is owned. Moving to a new tree costs
almost nothing in player trust: refund everything.

## 2. The central decision: two halves, two currencies

The "everyone goes combat" problem is not a numbers problem. While economy
and combat share one currency, a wall that blocks progress always wins the
argument. Gemini's fix converts economy stats into combat stats (mining
yield into armour pen, fishing rare find into boss damage). That fails here
for two reasons:

1. **Work exclusivity.** One character fights and others gather
   (`project_work_exclusivity`). The fighter does not wear the pickaxe, so a
   "pickaxe stat helps combat" node either reads another character's gear or
   forces the fighter to carry a tool for its combat stats. Both are wrong.
2. **Scale.** Tool affixes run in whole tens of percent (gather speed 55% at
   Legendary). Any conversion ratio that makes them matter in combat makes
   the tool the strongest combat item in the game.

ChatGPT's fix (measure each archetype on its own metric) is right in
principle. It fails on the wall plus scarce respec: a farmer build still
cannot pass the next region.

**Proposal: split the tree into two halves with separate currencies.**

| Half | Currency | Earned from | Contains |
|---|---|---|---|
| **War** (combat) | skill points, unchanged | account level, seals, rebirths (~129 max) | four combat columns: Might, Precision, Guard, Fortune |
| **Hearth** (economy) | craft points, new | profession mastery levels (woodcutting, mining, fishing, herbalism) | four columns: Wood & Stone, Water & Herb, Workshop, Kin |

Combat choices then compete only with combat choices, and economy choices
compete only with economy choices. A player who gathers a lot earns more
Hearth points. They never pay for that with boss damage. Both halves run
side by side in this game, because different characters do the work, so
the tree should run side by side too.

Fortune stays in **War** on purpose. Loot comes from kills. "Push the next
wall" against "farm this region richer" is a real trade-off inside combat,
and Fortune gives farming builds a long-term combat return (Codex Scholar,
section 4.4).

Two open points (section 9): the craft-point rate, and whether Hearth
survives a rebirth.

## 3. Shape and rules (both halves)

### 3.1 Grid

Each half is a grid of **four columns by five tiers** of square nodes. A
**bridge** node sits between each pair of neighbouring columns (three per
half). Four columns of 64-72 px squares fit a 390 px phone without
horizontal scroll, so the whole half shows at once. A toggle switches
between War and Hearth.

```
          Fortune      Might      Precision     Guard
T1 (0)    [a][b]       [a][b]     [a][b]        [a][b]
T2 (4)     [N]          [N]        [N]           [N]
T3 (10)  [A|B]  <br>  [A|B] <br>  [A|B]  <br>  [A|B]      br = bridge
T4 (16)  [m][N]       [m][N]     [m][N]        [m][N]
T5 (26)   [KEY]        [KEY]      [KEY]         [KEY]
```

### 3.2 Node kinds

| Kind | Ranks | Cost per rank | Notes |
|---|---|---|---|
| Minor | 5 | 1 | a plain stat |
| Notable | 3 | 2 | a mechanic, or a strong stat |
| Fork | 5 each | 1 | two nodes, only one may hold ranks (as today's boughs) |
| Bridge | 3 | 2 | needs 10 points in **both** neighbouring columns; its points count toward both columns' gates |
| Keystone | 1 | 5 | changes a rule, usually at a price |

### 3.3 Gates

A tier opens when the column holds enough points: **0 / 4 / 10 / 16 / 26**
for T1-T5. A column has 32 points of nodes below the keystone, so the gate
of 26 lets a player skip about one node. A full column costs **37**.

Budget check (War, ~129 points at the end):

| Moment | Points | What fits |
|---|---|---|
| Level 25 | ~27 | the first column to T4, or two columns to T3 |
| Level ~30 | ~31 | **first keystone** (today's first crown is at 27 points) |
| Level 50 + seals | ~55 | one full column + half of a second |
| Level 100, 10 rebirths | ~129 | three full columns + 18 more, or two full columns + all bridges |

Four full columns cost 148 plus 18 for bridges. **Nobody owns the whole War
half.** That gap is the design, the same as today's tree comment says. We
do not need ChatGPT's "two active keystones" rule. The budget already caps
keystones at three, which needs no extra UI.

### 3.4 Respec

Keep the free full respec per run. **Add a gold respec** (refund
everything, price grows with points spent, a gold sink). The draft mode
(section 6) ends the "misclick costs a point" problem. The respec only has
to cover "I changed my mind". Paid respec grants are a store item, so this
is an owner decision (section 9).

## 4. The War half

### 4.1 The rule every combat mechanic must pass

The live tick rolls each swing. Offline catch-up, the hunting advisor and
warp use **one expected-value model** (`CombatDamageModel`,
`HuntingProjection`). A mechanic is allowed only if:

1. it resolves **inside one swing or one monster swing** (no damage over
   time, no timed debuff on the monster),
2. it has a **closed-form expectation** that HuntingProjection can apply,
   and
3. it gets a **live-against-expectation parity test**, like the one that
   pins today's damage models together.

Counters ("every 8th hit") pass easily: the expectation is exactly 1/N.
Counters persist across fights, so live and offline agree exactly. A
counter also gives a visible rhythm on the combat feed, and that is fun.

Deferred for this reason: **Bleed** (damage over time), **Sunder** and
**Momentum** (timed state on the monster), and "+1% damage per missing 1%
HP" (it depends on the HP path, which auto-eat drives).

**This rule describes today's engine, not a law.** The expected-value model
exists because offline catch-up once used three models that disagreed. It
can be replaced by an **event-driven per-swing simulator**: jump from swing
to swing (yours and the monster's), roll with a seeded RNG, and run the same
resolution code the live tick runs. Twelve offline hours at a 1.5 s swing
are about 29,000 player swings plus the monster's. That costs milliseconds,
not seconds, and this game has 26 accounts. It would:

- make offline and live **one model**, not two models held together by a
  parity test, which removes a whole defect class;
- allow every stateful mechanic above (Bleed, Momentum, Sunder, low-HP
  bonuses), because the simulator carries the state;
- leave the hunting advisor on expectations, or on a short Monte Carlo run
  of the same simulator.

The price is a rewrite of the combat part of `OfflineSimulationEngine` and
`HuntingProjection`. That path has had real defects before (offline parity,
#120), so it needs its own phase and its own before/after measurement
(section 11, phase 2b). Decision D6.

### 4.2 Columns

Numbers are per rank, then (at max). Wire flags for the combat feed follow
`FlagDoubleStrike`.

**Might** - hits hard, breaks armour, kills bosses.

| Tier | Node | Effect |
|---|---|---|
| T1 | Edge | +1.5% damage (7.5%) |
| T1 | Breach | monster armour −4% (−20%) |
| T2 | **Crushing Blow** | 3 / 6 / 10% chance a hit ignores monster armour |
| T3 | Executioner \| Bossbreaker | +4% damage against monsters below 35% HP (20%) \| +4% damage against region and world bosses (20%; replaces the Giantslayer root) |
| T4 | Heavy Hand | +1.5% damage (7.5%) |
| T4 | **Thunderer** | opening blow at 150 / 300 / 500% weapon damage against a boss (today's crown) |
| T5 | **KEY Ruin** | every 8th connecting hit deals ×2.5 after armour. *Price:* attack speed from all sources counts at half. |

**Precision** - crits, tempo, repeated hits.

| Tier | Node | Effect |
|---|---|---|
| T1 | Keen Eye | +0.5 crit chance (2.5) |
| T1 | Cruel Edge | +4% crit damage (20%) |
| T2 | **Relentless** | −3% attack interval (−9%) |
| T3 | Echo \| Frenzy | a crit has 8% chance to repeat at 50% damage (40%), and an echo never echoes \| after a crit, the next swing interval is −6% (−30%) |
| T4 | Steady Hand | +5 accuracy (25) |
| T4 | **Double Strike** | 8 / 16 / 25% chance a crit lands twice (today's crown) |
| T5 | **KEY Perfect Sequence** | every 6th swing is a guaranteed crit with +50% crit damage. *Price:* crit chance from all other sources is capped at 50%. |

**Guard** - survives, and turns defence into damage.

| Tier | Node | Effect |
|---|---|---|
| T1 | Vitality | +2.5% max HP (12.5%) |
| T1 | Ironhide | +2.5% armour (12.5%) |
| T2 | **Bloodthirst** | +1.3 / 2.6 / 4% lifesteal (today's bough) |
| T3 | Evasion \| Shield Wall | +1.5 dodge (7.5) \| +2 block (10) |
| T4 | **Riposte** | a dodged or blocked monster swing answers at 25 / 50 / 75% weapon damage (an instant counter, never a proc chain) |
| T4 | Last Stand (1 rank, 3 points) | once an hour, a killing blow leaves you at 1 HP (today's crown) |
| T5 | **KEY Living Fortress** | crit chance converts 1:1 to block, and the block cap rises from 75% to 85%. *Price:* you never crit. |

**Fortune** - farms richer, and grows stronger slowly.

| Tier | Node | Effect |
|---|---|---|
| T1 | Scavenger | +1.5% loot luck (7.5%) |
| T1 | Golden Touch | +3% gold from kills (15%) |
| T2 | **Trophy Hunter** | +7 / 14 / 20% boss gold, and bosses always drop a material |
| T3 | Plenty \| Rarity | +2.5% material stack (12.5%) \| +1.5% rarity elevation (7.5%) |
| T4 | Quick Study | +1% XP (5%) |
| T4 | **Codex Scholar** | kills count 15 / 30 / 50% extra toward codex levels |
| T5 | **KEY Golden Fleece** | every 100th kill drops an item two tiers up (today's crown) |

**Codex Scholar is Fortune's answer to the wall.** It is not a stat
conversion. The codex damage multiplier is `1 + 0.04 × √levelSum`,
uncapped. A farmer reaches a higher codex sooner, so the farmer's damage
grows *over days*, not at once. The codex yield cap (2.0× at 200 levels)
is reached sooner too. The harness has to measure how much sooner.

### 4.3 Bridges (War)

| Between | Bridge | Effect |
|---|---|---|
| Fortune - Might | Know Your Prey | +3 / 6 / 10% damage against monsters whose codex level is 10 or more |
| Might - Precision | Piercing Crit | a crit ignores another 15 / 30 / 50% of monster armour |
| Precision - Guard | Counterstroke | after you dodge or block, your next swing has +20 / 35 / 50 crit chance |

### 4.4 Archetypes this allows

- **Duelist** (Might + Precision + Piercing Crit): the fastest wall
  breaker.
- **Warden** (Guard + Might): slow, survives the larder ceiling, and
  Riposte deals damage.
- **Fencer** (Precision + Guard + Counterstroke): dodge, then crit.
- **Bounty Hunter** (Fortune + Might + Know Your Prey): farms well and
  still clears region bosses.
- **Living Fortress**: no crits at all. A build that throws away the
  game's main damage stat is the clearest sign that the tree offers real
  choices.

## 5. The Hearth half (sketch, phase 4)

Earned from profession mastery, so nothing here costs a combat point.
Shape and rules as in section 3. The numbers wait for the gathering
economy to settle: #211 made loaded characters about 1.5× faster, and the
effect is still being watched.

| Column | Direction | Keystone candidate |
|---|---|---|
| Wood & Stone | woodcutting and mining speed and yield, per tool | **Deep Vein**: every 20th harvest is a rare-table roll |
| Water & Herb | fishing and herbalism, rare find | **Moonlit Catch**: rare find doubles while the character is offline |
| Workshop | craft time, commissions | **Masterwork**: a crafted item offers three legal affixes and you keep one |
| Kin | XP, offline rate (today's Scholar), breeding aptitudes | **Bloodline Memory**: a newborn inherits +10% of each parent aptitude |

Today's Insight limb (XP, Craft, Harvest, Scholar) moves here. That frees
War points: the War half's total power must still match today's best
combat build (section 8).

## 6. UI

The current panel (`client_web/src/lib/ui/SkillsPanel.svelte`) is replaced.

- **Square nodes** in the grid: icon, rank badge (`2/5`), and a state shown
  by border *and* icon. The states are locked (grey, with a padlock),
  available (amber), in draft (dashed yellow), owned (solid), maxed (rune
  glow) and fork-closed (struck through). Colour is never the only signal.
- **Lines** between tiers light up when a gate opens.
- **Tap a node** to select it. A detail sheet slides up from the bottom
  (the desktop shows a side panel). It shows the name, kind, current
  effect, next-rank effect, cost, and *why it is locked* if it is. Its
  buttons are **+1, −1 (draft ranks only), Max** and **Close**. The tree
  itself has no buttons.
- **Long-press for 350 ms** (or hover on desktop) to show the same text as
  a tooltip. The tooltip is only a shortcut: tapping always opens the full
  detail.
- **Draft mode.** Every change is a draft until confirmed. A footer shows
  `Confirm (7 points)` and `Discard`, and a **stat preview** lists the
  changes to damage, HP, armour, crit, loot luck and so on.
- **The preview comes from the server** (a preview endpoint that runs
  `CharacterStatSheet` against the draft). A client-side stat formula would
  be a second source of truth, and drift between two copies of one truth is
  this codebase's dominant defect class.

## 7. Wire and engine

- **Node definitions are data on the server** (an id, half, column, tier,
  kind, ranks, cost, fork partner, effect kind and per-rank values, and the
  text). They are served by REST next to `/gamedata`. The client renders
  what it receives and keeps **no** copy of the rules. `BlockedReason`
  already exists server-side and becomes the "why locked" text.
- **Player ranks leave `StateUpdatePacket`.** Twenty named bytes per
  snapshot become a REST read plus a small push when the ranks change. This
  goes through the `add-command` skill and `generate:protocol`.
- **Commit is atomic.** One request sends the whole target state. The
  server checks points, gates, forks and bridges, and applies all of it or
  nothing, at a tick boundary like today's `SkillTreeTickCoordinator`.
- **Effects resolve once per change** into one `SkillEffects` struct (the
  sum of each effect kind). The tick reads that struct instead of twenty
  `Skill_*` payload fields.
- **Persistence:** `player_skill_tree` (PlayerId, BranchId, Level) is kept.
  The new nodes take a fresh id range (100+). The migration refunds and
  deletes the old rows, and grants a free respec. Today 4 accounts hold
  rows.
- **`StatAuditTests`** gains a rule: every effect kind must have a
  consumer. This is the "read side wired, write side not" trap from the
  root `CLAUDE.md`, applied to the tree.

## 8. Balance method

The owner wants balance backed by measurement. Nothing in sections 4 and
10 ships on these numbers.

1. **Baseline first.** A `TreeBuildHarnessTests` scenario (in the style of
   `WallWindowScanTests` and `ProgressionRateTests`) records today's best
   build at 30 / 60 / 100 / 129 points: time to kill each region boss,
   survival against the band-top monster, and gold, loot and XP per hour.
   **Measured 2026-10-29** (`TreeBuildHarnessTests`, greedy search on
   expected damage per second, Human at each band top, Rare gear and Rare
   affixes, the region's third ordinary monster):

   | Region (level) | Budget | Points the best build can use | DPS from the tree |
   |---|---|---|---|
   | 1 (20) | 19 | 19 | +8.6% |
   | 2 (40) | 39-41 | 38-40 | +18.5-19.1% |
   | 3 (60) | 59-63 | 58 | +26.0% |
   | 4 (80) | 79-85 | 58 | +28.6% |
   | 5 (100) | 99-129 | 58 | +30.1% |

   The best build is Precision 10, Cruelty 10, Relentless 8 and Double
   Strike. **Today's tree runs out of combat power at 58 points.** Past
   that, every point buys economy or nothing, because Guile sits on the
   far side of Relentless's fork. The War half has to keep the ceiling near
   +30% while spreading it over the whole budget, so a level-100 player's
   last 70 points still buy a choice.

   **War half, stat nodes only (slice 2.2, 2026-10-29).** The mechanics are
   still pending, so this is only part of the half:

   | Region | Today's best | War, stat nodes only |
   |---|---|---|
   | 1 | +8.6% | **+21.4%** (19 points) |
   | 2 | +18.5% | +23.0% (21 points) |
   | 3-5 | +26.0-30.1% | +23.7-24.8% (21 points) |

   Two findings for slice 2.4:
   - Row 0 is too strong. Edge, Keen Eye and Cruel Edge at 1 point a rank
     buy two and a half times today's region-1 bonus, which would soften
     the region-1 wall. The fix is lower per-rank values on the first row,
     or a 1/1/2/2/2 cost per rank.
   - The greedy run stops at 21 points. Every column's row-1 gate passes
     through a pending mechanic node, so nothing past row 0 can be bought
     yet. This is expected until slice 2.3.

   **Slice 2.3a (2026-10-29): Crushing Blow and First Strike.** Measured
   first, armour ignore was dead weight. Monster armour lets 94-99% of a
   Rare-geared hit through, because penetration affixes cancel it, so
   Breach plus an armour-ignoring Crushing Blow were worth 0.4-1.6% DPS.
   The owner chose the redesign:
   - Crushing Blow: a 3 / 6 / 10% chance that a hit lands for double.
   - First Strike, in Breach's place: +25% per rank on the first hit
     against a monster at full health.

   The parity rows, one hour live against the offline projection, agree
   within 1-2 kills:

   | Row | Kills with the mechanic | Kills without |
   |---|---|---|
   | Crushing r3 | +8% | 178 |
   | First Strike r3 | +8% | 177 |
   | Both r5 | +15% | 97 |

   With Crushing Blow buyable, the War half reaches **+27.7% at region 1 and
   +37.3% at the end on 27 points**, already above today's +30.1%. Slice
   2.4 has to bring it down.

   The harness's DPS metric cannot see First Strike, because First Strike
   shortens a kill without raising the mean swing. Slice 2.4 adds a
   seconds-per-kill objective, measured from the projection's kill model.

   **Slices 2.3b-2.3d (2026-10-29).** Every War mechanic except the Fortune
   nodes and the bridges is wired. Each one has a parity row, an hour of the
   real tick against the offline projection, and agrees within 0-2 kills.
   Kills per hour with the mechanic, against 177 without it (region 3,
   geared):

   | Mechanic | Kills | Note |
   |---|---|---|
   | Crushing Blow 3 | 194 | +10% |
   | First Strike 5 | 192 | +8% |
   | Executioner 5 | 187 | +6% |
   | Double Strike 3 | 189 | +7% |
   | Echo 5 | 187 | +6% |
   | Frenzy 5 | 189 | +7%; a conversion of crit chance into interval, not a timer |
   | Riposte 3 + Shield Wall 5 | 198 | +12%; fires on a dodge or on the block share of a hit |
   | Ruin | 185 | +5%; the attack-speed halving eats most of it |
   | Perfect Sequence | 202 | +14% |
   | Bossbreaker 5, region 2 boss farm | 212 | +19%, **softens the walls directly** |
   | Living Fortress, region 4 | 113 vs 131 | **-14% kills for -2% fish** |
   | Bloodthirst 3, region 4 | 550 vs 554 fish | the larder is mostly rations |

   Findings for slice 2.4:
   - Living Fortress and Bloodthirst buy almost nothing, because sustain
     goes to rations, not healing. Either they need bigger numbers, or
     Guard needs a different payoff, such as fewer rations.
   - Ruin's price swallows its gain. A smaller price fixes it, for example
     attack speed counting at 75% instead of 50%.
   - Bossbreaker has to be weighed against the walls.

   The walls do not include the tree. `BossGearBenchmark` assumes only First
   Blood at its maximum, so the War half must either keep a node that
   softens the first-clear HP penalty, or `BossFirstClearRules.MaxHpFor`
   loses that relief, and the walls are re-solved together with the change.

2. **Targets.** The best War build must stay within ±5% of the baseline
   time to kill at every band. Otherwise the boss walls move, and region 2's
   window is only 1.855-1.882 wide. Each column's keystone build should be
   within 15% of the others on its own metric. Fortune keeps at least 70% of
   the Duelist's damage, so it can still clear walls slowly.
3. **Re-solve the walls** with `WallWindowScanTests` after the numbers
   settle, as #200 did.
4. **Proc ceilings.** No recursion: an echo, a double strike or a riposte
   never triggers another proc. Crushing Blow ignores armour; it never takes
   a share of the monster's HP (a share of HP would bypass every wall).
   Lifesteal keeps its 1%-of-max-HP-per-hit cap.

## 9. Decisions for the owner

| # | Question | Recommendation |
|---|---|---|
| D1 | Two halves with two currencies (War / Hearth)? | **Yes.** It is the only option here that removes the combat-versus-economy competition. |
| D2 | Craft-point rate and rebirth behaviour | Measure the mastery spread first. Hearth survives a rebirth if mastery does. |
| D3 | Gold respec beside the free one and the paid grants | **Yes**, priced by points spent. It is a gold sink. |
| D4 | Ring affixes (today block only, TASK_BOARD 125) | **Reopen**, see section 10.3. |
| D5 | Order of work | Phase 1 first: the new UI on today's 20 nodes, with no balance change. |
| D6 | Replace offline expected-value combat with a per-swing simulator (4.1) | **Yes, as phase 2b.** Ship the War half on mechanics that pass today's rule, then the simulator, then Bleed/Momentum-style mechanics. |

## 10. Affix rework

### 10.1 Keep the ids

Affix ids and their order cross the wire as an index (auto-reroll's "stop
on stat", guarded by `serverMirrors.test.ts`). **New affixes are appended
at the end of `AffixRegistry.Definitions`. Nothing is renamed or merged.**

### 10.2 Damage family

`melee_dmg_pct`, `range_dmg_pct` and `magic_dmg_pct` are one stat. **New
rolls use `melee_dmg_pct` only.** The other two leave the roll pools, and
existing items keep them. The weapon pool drops from 8 to 6 types, so the
"pick an unused type first" rule (STATS_REFERENCE 4.2) stops spending
three of five rolls on damage.

### 10.3 New affixes (first wave)

Each one has a consumer before it ships and passes the rule in 4.1.

| Id | Effect | Slots | Common → Legendary centre |
|---|---|---|---|
| `crushing_blow_pct` | chance a hit ignores armour (adds to Crushing Blow) | Weapon, Gloves | 0.5 → 2.5 |
| `double_hit_pct` | chance a swing repeats in full, no recursion | Weapon, Gloves, Ring | 0.5 → 2.5 |
| `boss_dmg_pct` | damage against bosses | Weapon, Amulet, Ring | 2 → 8 |
| `loot_luck_pct` | loot luck | Helmet, Amulet, Ring | 1 → 5 |
| `gold_find_pct` | gold from kills | Amulet, Ring | 2 → 8 |
| `thorns_pct` | reflects a share of each monster hit (the set's 20% becomes this stat) | Chest, Helmet | 2 → 6 |

Rejected:

- `hp_regen_flat`: the larder is the game's difficulty ceiling
  (`feedback_larder_is_the_difficulty_ceiling`), and regeneration would
  route around it.
- `skill_aptitude_pct`: it has no clear meaning.
- "Relic collector" (all stats per Legendary item): +20% to everything
  across eight slots.

### 10.4 Pools after the change

| Slot | Before | After |
|---|---|---|
| Weapon | 8 (3 are duplicates) | dmg, attack speed, crit chance, crit damage, lifesteal, armour pen, crushing, double hit, boss = 9 |
| Ring | 1 | block, crit chance, double hit, boss, loot luck, gold find = 6 |
| Amulet | 2 | flat HP, flat armour, crit damage, boss, loot luck, gold find = 6 |
| Chest | 2 | flat HP, flat armour, thorns = 3 |
| Gloves | 2 | armour pen, attack speed, crit chance, crushing, double hit = 5 |
| Helmet | 4 | + loot luck, thorns = 6 |

### 10.5 Recast and Refine

- **Recast** is today's reroll: it changes the type and value of unlocked
  affixes. The flat gold price is kept on purpose (STATS_REFERENCE 4.5).
- **Refine** is new: it keeps the type and rarity, and rerolls only the
  value within ±20%. It costs a fraction of a Recast. It gives a small,
  steady gold sink for an item the player already likes.

### 10.6 Later, not in the first wave

- **Affix families** (Power, Precision, Guard, Fortune, Craft), with one
  tree node per column that raises its family's affix values by up to +10%
  (relative, and the UI says so). It is a good link between tree and gear.
  It adds a multiplier per family in `EquipmentSlotEngine.AddAffixTotals`,
  so it waits until the base rework is measured.
- **Signature affixes** (an affix slot that holds a mechanic instead of a
  number). The tree supplies mechanics first; the gear amplifies them.

## 11. Phases

| Phase | Content | Proof |
|---|---|---|
| 1 | Data-driven node definitions, REST, atomic commit, draft UI, server stat preview, **today's 20 nodes ported 1:1** | `exercise` buys and refunds a node; no balance test moves |
| 2 | Baseline harness, then the War half, mechanics with parity tests, migration and refund | harness targets (section 8), walls re-solved |
| 2b | Event-driven per-swing offline simulator sharing the live resolution code; then stateful mechanics (Bleed, Momentum) | offline against live payout on the same seed, before and after; `ProgressionRateTests` unchanged |
| 3 | Affixes: damage family, new ids appended, pools, Refine | `StatAuditTests`, `serverMirrors.test.ts`, reroll `exercise` |
| 4 | Hearth half, craft points, Masterwork | gathering economy measured before and after |
| 5 | Affix families, signature affixes (optional) | - |

Phase 1 shipped as #216 (client 1.38.0). Phase 2 is cut into slices that
each ship on their own, behind the old tree, until 2.5 switches it:

| Slice | Content | Proof |
|---|---|---|
| 2.0 | The baseline harness (section 8) | the table in section 8 |
| 2.1 | A generic node table: id, half, column, tier, kind, ranks, cost, fork, bridge, effect kind and per-rank values. `PlanReason` generalised to tier gates and bridges. Ranks live in one array on the payload instead of one field per node, and leave `StateUpdatePacket` for a push on change. | today's 20 nodes expressed in the table, all phase 1 tests and `exercise` unchanged |
| 2.2 | The War half's stat nodes as effect kinds read by `LiveCombatStats` and `HuntingProjection` | the harness measures the War half next to today's tree |
| 2.3 | The mechanics, one per PR: Crushing Blow, Echo, Frenzy, Riposte, Ruin, Perfect Sequence, Living Fortress, Codex Scholar, the bridges. Each comes with its live roll, its expectation, a parity test, a combat-feed flag, its callout and its sound. | parity test per mechanic; the harness after each |
| 2.4 | The numbers tuned until the targets in section 8 hold | harness table within ±5% per band |
| 2.5 | The switch: a migration refunds every old node and grants a free respec; the client draws the War grid | `exercise` on a fresh account and the fixture |

Phase 1 is worth doing even if the rest changes. It removes the client
mirror and the per-snapshot node bytes, and it gives the owner the new
controls on numbers already known to be balanced.
