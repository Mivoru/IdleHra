# Design audit, power measurement and an endless endgame (2026-11-19)

Owner brief (Czech, paraphrased): fix the login popup overflow; then audit the
design of every screen in dark and light mode; find what makes the game less
attractive; verify power and progression speed (damage, every stat); check
breeding and player stats; propose an endgame that never ends, like The Tower,
with reasons to keep improving (monsters, rewards); consider skill-tree and
attribute presets.

Every number below is measured unless it says otherwise. Sources: the
production database (read-only queries over SSH), a local restore of the
2026-11-18 backup with the three endgame accounts signed in so the server's own
stat sheet and hunting projection could be read, and 120 screenshots (30
destinations x 390/1366 px x dark/light) of the dev fixture.

## 0. What shipped from this pass

| PR | What |
|---|---|
| #436 | Login week tiles: "150 diamonds", "5.11M gold", "84,915 g" ran 4-11 px past the tile edge at 360-390 px (measured). The unit now drops under the figure inside a tile. |
| #437 | **The player's blow was an `int` of milli-HP.** Two of three endgame accounts had `BestHit = 2,147,483` = `int.MaxValue / 1000`. Past that, damage did nothing (saturating cast on ARM), the war multipliers could wrap it negative and heal the monster, and `/api/v1/combat/projection` answered **HTTP 500 for Mivoru**. Now `long`. Also fixes a world-boss test that has been red on main since 2026-11-16 (`TreeStrikeFromUtc`). |
| #438 | Four screenshot-audit defects: build cards muddy grey on light (they inherited the `.slot` icon-socket style), the Chronicle bubble covering the coach pill on desktop, unreadable title chips on the World Boss light panel, "Beat the boss before" (now names the boss). |

## 1. Power and progression - what the numbers say

### 1.1 The three endgame accounts (server stat sheet, 2026-11-18 backup)

| | Mivoru | Tomda | (pro)boss |
|---|---|---|---|
| Level / rebirths | 126 / 4 | 107 / 20 | 109 / 20 |
| Attack (flat) | 11,734 | 5,695 | 7,303 |
| Damage % | 45 | 21 | 33 |
| **Codex damage multiplier** | **17.5x** | 10.9x | 11.9x |
| Attack speed | 71% (**at cap**) | 37% | 16.5% |
| Crit chance / crit damage | 64% / 2.67x | 27% / 1.81x | 39% / 1.95x |
| Max HP | 5.32M | 2.33M | 2.64M |
| Gathering speed | +878..1,611% | +803..3,819% | +878..4,492% |
| Malakor (906,542 HP, last boss) | **0.9 s** | 7.0 s | 6.1 s |
| Region-5 regulars | 0.6 s (one swing) | 1.0-2.5 s | 1.4-2.2 s |
| XP/h on Malakor | **901M** | 106M | 126M |

Read:

- **The content ends and the power does not.** Region 5 is the top of every
  combat ladder; Ascension stops at step 10 (Mivoru 49 of 50); the dungeon at
  stage 15. The best account kills the last boss of the game in under a
  second and every regular in one swing. Nothing it can do makes it feel
  stronger, because nothing it fights pushes back.
- **The codex is most of the damage.** `1 + 0.04 * sqrt(codex levels)`, no
  ceiling by design ("a monster ladder that keeps climbing", CodexEngine) -
  but the ladder stopped climbing at region 5. 17.5x on Mivoru means about
  170,000 codex levels (1.7M kills). A curve meant to keep pace with content
  is now the main reason content is trivial.
- **Levels stall by themselves around 135-140.** The XP curve is
  `220 * 1.114^level` (after the opening), while XP income is capped by the
  last region's monsters. Measured income: Mivoru ~28B XP/day. Level 126 ->
  127 costs 14.5B (half a day), level 140 costs 66B (2+ days), level 150 costs
  193B (a week). Levels stop being a goal without anyone deciding that.

### 1.2 Economy

| Week (Mon) | Gold minted | Gold spent |
|---|---|---|
| 2026-10-12 | 3.3B | 2.1B |
| 2026-10-26 | 7.5B | 3.6B |
| 2026-11-09 | 19.6B | 16.3B |
| 2026-11-16 (part) | 20.0B | 8.3B |

Last 14 days, by source: offline combat 20.6B, auto-salvage 8.5B, loot
chests 6.3B, Gold dungeon 4.8B, mail 2.1B. Sinks: Expeditions 13.5B (the
biggest by far, new on 11-13), guild 5.1B, rerolls 2.2B, recruiting 1.4B,
Deep 1.1B. Banked: 4.3-4.6B per endgame account. Income is six times what it
was five weeks ago; every sink except Expeditions is flat.

### 1.3 Breeding

| | chars | max generation | best lowest aptitude | born last 7 days |
|---|---|---|---|---|
| (pro)boss | 104 | 24 | **50 (the ceiling)** | 90 |
| Mivoru | 33 | 13 | 26 | 21 |
| Tomda | 29 | 11 | 26 | 10 |

The lineage overhaul (11-13) works - it was designed for "20 -> 50 in a
median of 24 generations" and (pro)boss did exactly that in **six days**. For
him breeding is now finished: there is nothing left to breed toward.

### 1.4 Presets

Build presets already exist (attributes + War tree ranks + an activity binding
since skill tree v3). On production **2 presets exist, both Tomda's**; the
other two accounts never saved one. Presets are a good answer to "attribute
presets?" - the gap is a reason to switch builds, not the feature. The endless
mode below gives one (a build for depth, a build for farming).

## 2. Design audit (120 screenshots)

Fixed in #438: see section 0. Open, ranked by how often a player meets it:

1. **Three floating widgets on every phone screen.** The Chronicle bubble and
   the chat handle stack in the bottom-left, the coach pill sits bottom-right,
   all above the tab bar - about 130 px of every screen is covered, and they
   sit on top of list rows (Forge, Village, Gathering). Recommendation: move the
   Chronicle level into the header (a small "Lv 26" chip beside the wallet)
   and keep one floating control per corner.
2. **Walls of text.** Rebirth is 4,100 px tall at 390 px, the skill tree
   3,700 px, Progress 4,150 px, World Boss 2,300 px - each opens with a
   paragraph of rules. Recommendation: every screen opens with its ONE action
   and its one number; the rules fold into an "How it works" disclosure (World
   Boss already does this at the bottom - use the same pattern at the top).
3. **Desktop single-panel screens are deliberately narrow** (Expeditions 44rem,
   Mail 46rem - a readable line length), so most of a 1366 px page is the
   background painting. Not a defect; a second column (what the trip brings
   back, the chest odds) would use the space if a screen ever needs it.
4. *Checked and fine:* quiet text (`.dim`) measures 5.1-6.0:1 against the
   panels in both schemes, above the 4.5:1 floor - it only looked faint in
   downscaled contact sheets.
5. **Monster art comes from the server, so a dropped connection showed broken
   image icons** across the Combat list (seen while the server restarted).
   Fixed: MonsterPortrait and ItemIcon fall back to the initials.
6. **A locked screen is an empty page.** The Dungeon before Shadow Lynx is one
   card on a blurred background. Show what is behind the lock (the three gates,
   their rewards) - a locked screen is an advert for the goal.
7. Light mode is otherwise consistent: section colours carry, bar labels keep
   their halo and read on parchment, the skill tree stays dark on purpose.

## 3. What makes the game less attractive (for these three players)

All three log in every day (14/14 days with income). The problem is not a
reason to open the game; it is that **nothing they do changes how strong they
feel**, because every ladder they could climb has a top and they are at it:

| Ladder | Top | Where the best account is |
|---|---|---|
| Regions / bosses | region 5 | done, one swing |
| Ascension | 10 steps x 5 | 49 / 50 |
| Dungeon | stage 15 | 13 |
| Breeding | 50/50/50/50 | reached by (pro)boss in 6 days |
| Level | soft wall ~140 | 126, slowing |
| Boons | 60 ranks | partly capped |
| Gold | none, but nothing to buy | 4.5B banked each |

The Tower works because it has **one endless number** (the wave), **a
currency that every run pays** and **an infinite list of upgrades whose costs
grow faster than their effects**, so there is always a next purchase and a
next record. FolkIdle has every ingredient - strong characters, bred heroes,
builds, gold with nothing to buy - and no endless number.

## 4. Proposal: the Descent into Nav (an endless ladder)

Nav is the Slavic underworld - the world below the World Tree's roots. It fits
the canon and gives the art a new palette (cold blue-green, roots, bone).

### 4.1 The run (one endless number)

- A character takes the **Descent** as a job, like fighting a monster: it can
  run online and offline, in any of the three slots. The slot fights **depth
  after depth**; each depth is one guardian (the 25 monsters' art, recoloured,
  plus an elite modifier).
- A depth is cleared by killing its guardian within **30 seconds of fight
  clock**. Guardian health is `1,000,000 x 1.10^(depth-1)`, attack
  `40,000 x 1.08^(depth-1)` - so damage, speed, crit AND survival all matter,
  and every +10% damage is about one more depth.
- The run ends on a death or a timeout. It banks its reward and (optionally)
  starts again from depth 1 - early depths fall in one swing, so a restart
  costs seconds, exactly like The Tower's early waves.
- Projected from section 1.1 (expectation, before any upgrades): Tomda
  (~130k DPS on Malakor) clears about **depth 15**, (pro)boss about 16,
  Mivoru (about 1.1M DPS: 323k pre-armour x 0.75 armour x 2.07 average crit
  x war tree, every 0.6 s) about **depth 38**. A real spread, and a reason for
  Tomda and (pro)boss to look at their builds.

### 4.2 Souls and the Nav Workshop (always a next purchase)

- Every depth pays **Souls**: `ceil(1 + depth^1.3 / 4)` per depth cleared
  (depth 15: 10, depth 38: 30). A run to depth 38 pays about 540, to depth 15 about 80.
- Souls buy **Workshop upgrades with no top level**. Each costs
  `base x 1.12^level` and adds a flat amount, so cost grows geometrically and
  effect linearly - the exact shape that never runs out:

| Upgrade | Per level | Base cost |
|---|---|---|
| Rootstrength | +3% damage in Nav | 10 |
| Barkskin | +3% health in Nav | 10 |
| Quick roots | +1% attack speed in Nav (cap 50 levels) | 25 |
| Grave luck | +2% crit damage in Nav | 20 |
| Soul harvest | +3% Souls | 30 |
| Long fall | start runs 1 depth deeper (cap = half your record) | 100 |
| Second breath | one revive per run per 10 levels (cap 3) | 500 |

- **Workshop bonuses apply only inside Nav.** The rest of the game's balance
  is untouched - no region, boss or dungeon gets easier - so this does not
  re-open the balance of regions 1-5.

### 4.3 The Volkhv's research (the gold sink the economy needs)

- Timed research paid in **gold**: each line has levels, cost
  `50M x 1.5^level`, duration `2 h x 1.25^level`. One research slot, a second
  for diamonds.
- Lines: "+5% Souls", "+1% all Workshop effects", "-2% Workshop prices",
  "+1 elite chest chance". Level 15 costs 22B and takes 57 h; level 20 costs
  166B. Against 20B minted a week, this is the first sink that scales with
  income - which `GOLD_ECONOMY_2026_11_13.md` asked for.

### 4.4 Circles (infinite tiers)

- Reaching **depth 50** in Circle k opens Circle k+1: guardians x5 health and
  attack, Souls x3, one more elite modifier at a time.
- Each Circle has first-time rewards at depths 10/25/50: a loot chest of the
  top region, a title ("Walker of Nav", "Root-Breaker", "Lord of the Below"),
  an avatar/frame per Circle, a pet at Circle 3 depth 50.

### 4.5 Using what the game already has

- **Builds:** bind a preset to the Descent (one more `BoundActivities` bit).
  A depth build (glass-cannon crit) and a farming build become a real choice.
- **Breeding past 50:** benched ancestors become **spirits of Nav** - the
  three best benched adults add +0.5% Nav damage per aptitude point above 40.
  A 50/50/50/50 line keeps paying, and breeding has a target again.
- **Weekly omen:** one rule per week (guardians regenerate / crit twice / no
  potions) with +25% Souls - the duel's omen system already exists.
- **Leaderboard:** "Deepest in Nav this week" (works with three players).

### 4.6 What it costs to build

One table (`nav_progress`: player, circle, record, souls, workshop levels as a
packed string, research line/level/ends), one engine reusing the live combat
swing against a scaled guardian (the projection and the tick already price a
fight from the payload), REST routes for the Workshop and research, one
screen, exercise checks. Medium-large; about the size of Expeditions.

## 5. Smaller ideas, by value per effort

1. **Paragon after level 120**: XP past the soft wall pays 1 Paragon point per
   N billion XP (N rising), spent on +0.5% stats. Turns dead XP into a number.
2. **Ascension steps 11+**: the cheapest endless ladder - every step after 10
   is health x1.25 and one chest. A one-constant change in
   `BossAscensionRegistry`, but the Descent does the same job better.
3. **Make the Codex visible as a second number** on Home ("Codex x17.5") - it
   is most of every player's damage and no screen says so.

## 5b. Built behind a flag (#440)

The Descent into Nav exists as code, OFF in production (`FOLKIDLE_NAV=on`
opens it; `run-dev.ps1` sets it locally). Calibrated on the local restore of
the 2026-11-18 backup with the server's own numbers, Workshop at zero:

| Account | ChampionPower DPS | Depth reached (5 runs) |
|---|---|---|
| Mivoru | 1.43M | 40, 37, 40, 36, 40 |
| (pro)boss | 159k | 15, 15, 11, 15, 15 |
| Tomda | 139k | 13, 11, 13, 13, 13 |

Souls per run: Mivoru ~600, (pro)boss ~83, Tomda ~64. Thirteen Rootstrength
levels (+39% damage, 300 Souls - one evening for Tomda) took Tomda from 13 to
17. The spread holds after upgrades because Workshop costs grow geometrically.

## 6. Questions for the owner

1. **Build the Descent into Nav?** It adds a currency (Souls) and a gold sink
   (research) - both economy decisions. Recommendation: yes, Nav-only bonuses
   first, so nothing else in the game changes balance.
2. **Numbers:** guardian growth 1.10/depth, 30 s per guardian, Circle at depth
   50, Workshop cost 1.12^level. Recommendation as written; tune after a
   week of real runs.
3. **Spirits of Nav** (section 4.5) as the answer to "breeding is finished"?
4. **Phone layout:** move the Chronicle level into the header (section 2.1)?

### Answers (owner, 2026-11-19)

1. Yes - Nav goes live, but only once it is finished and polished.
2. Keep the numbers; tune after a week of real runs.
3. Not now. The owner suspects breeding only looks fast because (pro)boss
   used an old hole together with Tomda; once one account has a
   50/50/50/50 line, the next one is quick. Measure breeding pace on clean
   accounts before giving breeding a new goal.
4. Yes (#444): a header chip on a phone; a desktop keeps the bubble.

The owner also asked for Nav's own monsters. What was built before launch
(all in #440): ten guardians with one ability each plus Veles every 25th
depth, a depth-by-depth playback, "stopped by" with a hint per ability, the
expected depth per character, a board, four titles, News, a menu dot for
full descents. Calibration with the guardians, on the same restore as
section 4.4: Mivoru 35, (pro)boss 10-11, Tomda 9-16 (see the Modul note on
`NavRules.WardenEvery`).
