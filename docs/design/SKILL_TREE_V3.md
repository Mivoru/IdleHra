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
