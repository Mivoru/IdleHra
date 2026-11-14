# Skill tree v3: builds that are good at something

Status: **approved 2026-11-14** (see Owner decisions at the end). Build order A-D.

## The problem, measured

- The War tree has 4 columns of 5 rows: 39 nodes, 208 points to fill (about
  188 reachable, because forks exclude their partner).
- Players hold 113-116 points (one per level plus 2 per rebirth up to 20).
  Levels have no cap, so around level 180 a player owns the whole tree.
- Almost every node is a flat percentage of the same damage number. Whatever
  path a player picks, the result is the same damage, slightly re-ordered.
  The keystone build test (`docs/design/SKILL_TREE_HANDOFF.md`) even targets
  "all keystone builds within 15% of each other" - equal power was the goal.
- Living Fortress has no takers on prod; Ruin is the consensus pick.

Owner, 2026-11-14: builds should differ in WHAT they are good at, not be
equal. A crit-heavy character should find Bleed better than flat damage; a
grind build should focus gold, XP and drops; other builds should beat
regional bosses or the world boss; a given situation should want a
different build.

## Principle 1: situations, not one damage number

Each build is judged in five situations, each with its own measured score:

| Situation | What wins it | Score |
|---|---|---|
| **Grind** (farming a region) | kills per hour against weak monsters, gold, XP, loot | gold+XP per hour |
| **Regional boss** (boss wall, first clears) | burst, armour, first-clear relief | time to kill the wall boss, survival |
| **World boss** (one long fight, ranked by damage) | ramp, damage over time, staying alive for the whole window | damage in the fight window |
| **Dungeon** (party, schools, telegraphs) | survival, block, thorns, party role | stage reached |
| **Delve** (depth, attrition) | sustain, lifesteal, food efficiency | floor reached |

The goal changes from "every build within 15%" to:

- **every build is best in at least one situation, and no build is best in
  more than two**, and
- the best build in a situation beats a generalist there by 25-40%.

A test prints this matrix (build x situation) from the existing simulators
(`DungeonSimulator`, the offline per-swing simulator, the world boss
damage model). The table is the balance measurement, like
`ProgressionRateTests`.

## Principle 2: mechanics that care about the fight's shape

Flat damage treats every fight alike. Mechanics give a fight a shape, so the
same build is strong in one situation and weak in another:

| Mechanic | Good in | Weak in | Status |
|---|---|---|---|
| **Bleed** - stacks on hit, ticks over time | long fights: world boss, regional boss | trash dies before it ticks (wasted) | to build |
| **Sunder** - each hit strips armour, stacks | armoured bosses, world boss | trash (dies before stacks matter) | to build (owner approved 2026-11-03) |
| **Momentum** - speed grows with a kill streak | grind | single boss fights (no streak) | to build (approved 2026-11-03) |
| **Execute** - damage below 35% health | trash, finishing bosses | - | exists (Executioner) |
| **Overkill carry** - surplus damage hits the next monster | grind | bosses | new |
| **Thorns** - reflect damage taken | dungeon, tanky fights | grind (monsters die first) | exists (affix, set) |
| **Lifesteal** | Delve, long fights | - | exists |
| **Burn** | sets today | - | exists (set bonus) |
| **Opening blow** (Thunderer) | boss fights | - | exists |

Bleed, Sunder and Momentum must be paid identically on the live tick and in
the offline simulator, with a parity row each, and Bleed measured on the
Ampere box first (owner decision D1/D2, 2026-11-03).

## Principle 3: synergy with what the character already is

The best path should depend on the character's gear and blood, so two
players pick differently:

- **Conversion nodes.** "Crit chance above 40% turns into Bleed damage:
  +2% Bleed damage for each point above 40." A crit-geared character gets a
  lot; a character without crit gets nothing. (Frenzy already converts
  dodge+block into crit; this generalises it.)
- **Aptitude scaling.** Outer nodes scale with one of the four aptitudes
  (Strength, Skill, Endurance, Fortune) of the slot's bloodline: "+0.3%
  Bleed damage per Skill aptitude point". Breeding then chooses the build,
  which ties the endgame loop (breeding, rebirth) to combat.
- **Thresholds.** "While block chance is above 50%, each block adds a
  Thorns stack." Reaching a threshold through gear is a goal in itself.

## Principle 4: a tree you can never fill

- Each column grows from 5 to 9 rows: an **outer ring** of about 9 nodes per
  column, for about 75 nodes and **about 400 points** in all. With about 115
  points today a player covers under 30% (55% today). Even level 200 cannot
  fill it.
- Outer **notables take up to 10 ranks with falling returns** (each rank a
  little less), so spare points always buy something, but opening a new
  direction is worth more.
- The outer ring is organised by **situation lanes**, entered from the
  column they sit next to:

| Lane | Sits between | Holds |
|---|---|---|
| **Hunter** (grind) | Fortune - Might | Momentum, Overkill carry, gold/XP/loot per kill, faster respawn travel |
| **Bossbreaker** (regional boss) | Might - Guard | Sunder, opening blow, first-clear relief, armour |
| **Titan-slayer** (world boss) | Precision - Might | Bleed, ramp over the fight window, world boss damage |
| **Warden** (dungeon/Delve) | Guard - Precision | Thorns stacks, lifesteal, block thresholds, party aura |

  A build is "column + lane": Precision + Titan-slayer is the crit-Bleed
  world boss build; Fortune + Hunter is the grind build; Guard + Warden is
  the dungeon tank.

## Principle 5: one keystone, from twelve

Three keystones per column, only ONE active at a time across all twelve.
Each changes how a fight goes and carries a downside:

| Column | Keystones |
|---|---|
| Might | Ruin (exists) · **Juggernaut**: Sunder stacks twice as high, attack speed -15% · **Berserk**: up to +40% damage as health falls, no lifesteal |
| Precision | Perfect Sequence (exists) · **Haemorrhage**: crits apply Bleed, the next crit detonates it; flat crit damage -30% · **Flurry**: crit chance converts to attack speed |
| Guard | Living Fortress (rework: it has no takers) · **Thornmail**: Thorns x2, own damage -20% · **Vampire**: lifesteal x2, max health -15% |
| Fortune | Golden Fleece (exists) · **Midas**: +50% gold, -20% damage · **Scholar**: +40% XP, -20% damage |

## Principle 6: switching is part of the game

- **Three saved builds (loadouts)**, switched free outside a fight.
- A loadout can be **bound to an activity**: the world boss loadout loads
  when the character registers for the world boss, the dungeon loadout when
  a run starts, the grind loadout otherwise. Choosing the right build for
  the situation becomes the decision, with no daily chore.

## Order of work

| Step | What | Size |
|---|---|---|
| A | One active keystone; three loadouts, bound to activities | small |
| B | Bleed, Sunder, Momentum in the engine (live + offline + parity + Ampere measurement) | medium |
| C | The situation test matrix (build x situation) as a printed table | medium |
| D | Outer ring: four lanes, conversion and aptitude nodes, eight new keystones; free respec for everyone | large |

Each step is its own PR and deploy. Numbers in D are set from C's matrix,
not by hand.

## Owner decisions (2026-11-14)

1. Lanes (Hunter, Bossbreaker, Titan-slayer, Warden) and the twelve keystones
   with downsides: **approved, start with step A.**
2. Aptitude scaling (breeding decides the build): **yes.**
3. Loadouts **bound to activities automatically** (world boss, dungeon,
   grind), still switchable by hand outside a fight.

## Status

- **Step A - done, deployed 2026-11-14.** #407 one keystone (1.85.0; on load
  Tomda was refunded 20 points, Mivoru 10). #408 builds (1.86.0): the existing
  BuildPreset rows are the loadouts, switching is free
  (`SkillTreeEngine.EquipPresetTreeAsync`), and a preset can be bound to the
  world boss or the dungeon (`SkillLoadoutEngine`, laid over a payload COPY).
- **Step B - built 2026-11-14, nodes HIDDEN.** Sunder (id 93), Momentum (94)
  and Lacerate, the bleed (95), are wired in `ResolvePlayerSwing` /
  `RunCombatTick` (the live tick and the per-swing simulator alike), and in
  `HuntingProjection.PlayerSwing.For` - Sunder as the fight's average armour,
  the bleed as the share delivered before the monster dies, Momentum as a full
  streak against anything but a region boss - and so in the dungeon too. The
  fight state is per slot (`CharacterActivityState`) and cleared at every
  spawn; the streak is cleared by a death. Parity at the x4 test scale,
  offline/live kills: sunder r3 175/174, lacerate r3 226/226, lacerate r5
  139/138, momentum r3 1029/998. The bleed costs the simulator's jump +19%
  ticked ticks (68,166 to 81,009 over 12 h on a region-5 farm): measure it on
  the Ampere box before step D makes it buyable. Real-scale values per rank:
  Sunder 2% armour per stack (10 stacks), Momentum 0.5% faster per kill (15
  kills), the bleed 10% of the crit over 3 s. Step C's matrix sets them
  properly.
- Not yet: a combat-feed line for the bleed tick; the nodes' art and their
  place in the tree (step D).
- **Step C - the matrix, 2026-11-14.** `BuildSituationMatrixTests` prints
  five archetypes at the same 88 points on the region-5 reference character
  (real values; grind and boss = an hour of the per-swing simulator, 3 seeds;
  dungeon = highest stage won plus the next boss's share; world boss = today's
  strike and the sustained swing against a boss that never dies):

  | build | grind kills | grind gold | boss kills | dungeon | WB strike today | sustained dps |
  |---|---|---|---|---|---|---|
  | Generalist | 111 | 327,765 | 13.0 | 4.08 | 2,787 | 3,670 |
  | Grind (Fortune + Momentum) | 137 | 408,360 | 12.7 | 4.00 | 2,787 | 3,504 |
  | Bossbreaker (Might + Sunder) | 109 | 296,660 | 14.0 | 4.07 | 2,815 | 3,612 |
  | Titan-slayer (Precision + Bleed) | 146 | 397,670 | 17.0 | 4.11 | 2,787 | 4,899 |
  | Warden (Guard) | 85 | 231,140 | 8.0 | 4.44 | 2,774 | 2,796 |

  Controls (the same build minus its mechanic, 82 points): Momentum is worth
  +26% grind kills and nothing on the boss (situational, as meant); the bleed
  +15-17% everywhere a fight lasts (region-5 fights do); Sunder only +8% on
  the boss - too weak. Findings for step D:
  1. **The world boss ignores the tree.** The strike is attack x pet x potion,
     so every build hits for 2,770-2,815; crit, the bleed, Sunder and every
     keystone count for nothing there. Titan-slayer needs the strike to price
     the sustained swing (owner decision: the weekly payout ranks by damage).
  2. **Precision outclasses Might** even without the bleed (127 vs 109 grind
     kills, 4,176 vs 3,579 sustained): Might's nodes need raising, or
     Precision's lowering, before the ring is placed.
  3. **Sunder needs ~2.5x** (2% to about 5% per stack per rank) to give the
     Bossbreaker its +20-30% on bosses.
  4. The dungeon rewards survival (Warden best at 4.44) - already situational;
     its stage resolution is coarse, read the boss share.
  5. Delve is not in the matrix yet.
- **World boss strike prices the tree (owner, 2026-11-14), from the
  encounter of 2026-11-16.** `WorldBossTickCoordinator.TreeStrike`: the attack
  without the tree times the tree's lift on the sustained swing
  (`PlayerSwing` against the last region's boss with an endless pool). A
  treeless character strikes exactly as before. Matrix: Generalist 2,787 to
  3,106, Grind 2,966, Bossbreaker 3,057, Titan-slayer 4,147, Warden 2,351
  (Living Fortress caps crit, so the lift is below 1). The boss's health is
  set from Monday/Tuesday strikes, so it follows on its own; the date gate
  keeps the current week's boss from going soft.
- **Step D - the outer ring, 2026-11-14.** Rows 5-7 per column (gates 34 /
  44 / 56), each column's lane: Fortune = Hunter (Momentum, Bounty, Treasure
  Sense, Lore of the Hunt; keystones Midas, Sage), Might = Bossbreaker
  (Sunder, Titan's Grip, Overpower, Giant's Bane; Juggernaut, Colossus),
  Precision = Titan-slayer (Lacerate, Killer Instinct, Serrated, Quickening;
  Haemorrhage, Flurry), Guard = Warden (Stoneskin, Deflect, Vitae, Parry;
  Bastion, Vampire). Deviation from the plan: the lanes sit INSIDE their
  column, not between two, so the screen needed no new layout. Keystones are
  trades through the existing effect fields (WarEffects.ApplyKeystoneTrades),
  so every simulator prices them unchanged; the damage and interval reads
  accept negative sums. Giant's Bane (region bosses only) replaced the
  planned Executioner's Edge: without it the Titan-slayer tied the
  Bossbreaker on region bosses. Edge 0.4 to 0.8 and Heavy Hand 0.2 to 0.6%
  a rank; Sunder 2 to 5% a stack per rank. Everyone got a free respec
  (migration SkillTreeOuterRingFreeRespec). The matrix at 115 points (today's
  players) now pins the design: grind gold Hunter+Midas +45% over the
  generalist, grind XP Hunter+Sage +41%, region bosses Bossbreaker+Juggernaut
  +43%, world boss Titan-slayer+Haemorrhage +38%, dungeon Warden+Living
  Fortress +21% - and `BuildSituationMatrixTests` fails if a retune hands a
  situation to another lane. Not done: art for the new nodes (the rune glyph
  stands in), the Delve in the matrix, a bleed line on the combat feed, the
  bleed's cost measured on the Ampere box.
