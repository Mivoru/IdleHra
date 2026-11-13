# Game review - where FolkIdle loses players (2026-11-13)

Written during the lineage/rebirth overhaul, at the owner's request: "think
about the game as a whole, ask what is wrong, what other games have and ours
does not, where the game loses, then plan and build". Every claim below is
measured on production unless it says otherwise.

## 1. What the numbers say

Production, 2026-11-13 (16 accounts):

| Group | Accounts | Notes |
|---|---|---|
| Endgame (level 89-106) | 3 | Tomda (20 rebirths) last logged in 7 days ago; (pro)boss (20 rebirths) on a 6-day streak; the owner |
| Named accounts that stopped at level 1-4 | 4 | Tob1k and tobiaschodera (likely one person), rajus, an unnamed-looking one |
| Nameless level-1 accounts | 8 | mostly throwaway guests: `smoke:screens` signs in as a guest on production after every deploy (account 229 logged in during the 2026-11-13 deploy) |

Two findings matter more than anything else in this review:

1. **The first session loses real people too.** Correction made the same
   night: most nameless level-1 accounts are smoke-test guests, so "11 of 16"
   overstated it. Of the 7 named accounts, 3 people stopped at level 1-4 and
   never came back. A small sample, but the shape is the same: the loss is in
   the first minutes, not over days. Funnel telemetry (question 4) would
   settle it - and smoke guests should be excluded from it.
2. **The endgame ran dry.** Both 20-rebirth players had hit every cap the game
   had: rebirth (20), the bloodline (stuck at ~21 under a ceiling of 50), the
   reward road (10). One stopped logging in. The lineage overhaul
   (`LINEAGE_REBIRTH_OVERHAUL.md`) is the answer to this one.

## 2. Where the game loses, ranked

### A. The first five minutes (biggest loss)

What a new player meets: a login, then a game with 26 screens, a menu tree,
three trees of skills, a village, breeding words (aptitude, gene pool, elder,
the cull) and an onboarding coach. Comparable idle games (Melvor Idle, Idle
Slayer, Legends of Idleon, NGU Idle) open with **one button that visibly does
something**, and unlock every other screen only when it becomes useful.

Already in place (checked after the first draft of this review): screens
lock until their requirement (`client_web/src/lib/ui/unlocks.ts`, bound in
`stores/tutorial.ts`), the home screen shows the nearest unlock and a
"Closest goal" card (`DeedCloseness`), and onboarding has a quest line. So the
loss is not explained by a missing feature from the usual list.

What a new account actually meets (registered locally 2026-11-13, phone
width, screenshots every few seconds): the Home screen with the character
**idle**, "Give a job", "Closest goal: reach level 10", and a guided card
"Load your fish into the larder ... Go to Auto-Eat" covering everything else.
Nothing moves until the player has stocked a larder - an idle game's first
impression is a chore, not a fight. The guided first minute
(`stores/guided.ts`) is two steps, StockTheLarder then EquipADrop.

Recommendations:
- **Start the first fight for them**: seed the starter fish straight into the
  larder at registration (the reason the larder step exists is the
  2026-09-02 "died to the first monster" defect, which a pre-stocked larder
  solves just as well) and put the founder on the first monster, so the very
  first screen shows a fight, damage numbers and a level-up within seconds.
  The guided steps then start at "equip the drop you just got". Needs the
  owner's yes - it rewrites onboarding the owner designed, and exercise's
  new-player checks follow the current flow.
- **Measure before redesigning**: funnel telemetry for the first ten steps
  (signed in, first kill, first level, first equip, first boss...), with the
  smoke-test guests excluded, then fix the step where people stop.
- **Watch one new player play**, or register a fresh account and screen-record
  the first ten minutes - the fixture cannot show this (CLAUDE.md).

### B. Too many parallel systems, too little "what next"

Combat, gathering x4, crafting, Forge, potions, larder, village, Inn, breeding,
Hall, rebirth, dungeon, Delve, world boss, Samhain, pets, cosmetics, chests,
market, guild, deeds, War tree, Hearth tree, Inheritance, Boons. Each has its
own screen and vocabulary. Players of comparable games praise a single "next
goal" line more than any one system.

Recommendations:
- The "Closest goal" card already exists; check that it points at the
  breeding and rebirth goals for endgame accounts now that chapter VI exists.
- **Merge screens where the decision is one decision**: Hall + Breeding +
  Rebirth are one loop ("my bloodline") and could be one screen with tabs.

### C. Endgame (addressed today, more possible)

Built in this overhaul: unlimited rebirth, shards by level, the Ancestral
Boons, the chapter "The Long Blood", titles at 20/30/50, a bloodline climb that
actually reaches 50.

Further ideas, in order of value per effort:
- **Rebirth Trials** - built the same night (see the overhaul doc, section 7).
- **Personal records**: fastest life to level 50 and to 100, shown on the
  Rebirth screen and as a leaderboard. Costs one timestamp column; makes every
  rebirth a race against yourself.
- **Weekly world-boss and Delve ladders** exist; a weekly "bloodline" ladder
  (best child bred this week) would give breeding a social side.

### D. Social layer for a tiny population

Guild Wars are hidden, the market needs sellers, chat is quiet. With 3 active
players, features that need many players read as broken.

Recommendation: prefer **asynchronous** social features that work with few
players - leaderboards, profile showcases (best ancestor, titles), gifts -
over ones that need a crowd. Keep Guild Wars hidden until there are enough
guilds (already the owner's rule: 4+ guilds).

### E. Clarity of numbers

Breeding was the worst offender (three genetic systems, one of them dead).
Fixed today: the dead locus rows are gone from the preview, the rules are one
sentence each, the Best matches list does the comparison. The same pass is
worth doing for the stat sheet (`STATS_REFERENCE.md` exists) - show each
bonus with its source.

## 3. Questions for the owner

1. **New players** (A): screens already unlock progressively. Do you know
   anyone who quit early and would say why? A ten-minute screen recording of a
   new player would tell us more than any guess here.
2. **Rebirth Trials** (C): built the same night (client 1.61.0) with four
   trials - Want, the Slow Hand, Frailty, Iron. Which others would you like?
3. **Merge Hall + Breeding + Rebirth into one "Bloodline" screen** (B)?
4. **The new-player funnel** (A): may I add funnel telemetry for the first ten
   steps and send you a weekly summary?

## 4. What I am doing without waiting

- The lineage overhaul (built, tested, see its design doc).
- Rebirth Trials (built, 1.61.0).
- Personal life records (fastest life to level 50/100) are next if time
  remains.
