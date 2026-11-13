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
| Reached level 4 | 1 | |
| Never left level 1 | 11 | 8 of them guests with no name; most logged in once |

Two findings matter more than anything else in this review:

1. **The first session loses almost everyone.** 11 of 16 accounts never
   reached level 2. An idle game normally loses players over days; this one
   loses them in minutes. Whatever the first screen asks, most people do not
   do it.
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

Recommendations:
- **Progressive disclosure**: hide screens until their unlock (village at
  first boss, breeding at Breeding Grounds, rebirth at level 40, guild at 10,
  market at 15...). The menu grows as the player does - a reward in itself.
- **A first-minute script**: the account lands in Combat with the first
  monster already being fought, a level-up within 30 seconds, a loot drop
  within a minute, and the first "go equip it" prompt.
- **Measure it**: a funnel event at each of the first ten steps (exists in
  part as telemetry), then fix the step where people stop.

### B. Too many parallel systems, too little "what next"

Combat, gathering x4, crafting, Forge, potions, larder, village, Inn, breeding,
Hall, rebirth, dungeon, Delve, world boss, Samhain, pets, cosmetics, chests,
market, guild, deeds, War tree, Hearth tree, Inheritance, Boons. Each has its
own screen and vocabulary. Players of comparable games praise a single "next
goal" line more than any one system.

Recommendations:
- **One "Next step" card** on the home screen, chosen by the server from the
  deeds, the quest line and the unlocks (the data exists: `DeedCloseness`
  already ranks the nearest deeds).
- **Merge screens where the decision is one decision**: Hall + Breeding +
  Rebirth are one loop ("my bloodline") and could be one screen with tabs.

### C. Endgame (addressed today, more possible)

Built in this overhaul: unlimited rebirth, shards by level, the Ancestral
Boons, the chapter "The Long Blood", titles at 20/30/50, a bloodline climb that
actually reaches 50.

Further ideas, in order of value per effort:
- **Rebirth Trials** (Antimatter Dimensions / NGU style "challenges"): an
  optional modifier for one life - no Vault, half gold, monsters +50% HP, one
  character only - that pays a permanent, bounded reward and a title when the
  life reaches level 50. Re-uses rebirth; needs one multiplier hook per
  modifier in the shared kill/stat helpers (live and offline already share
  them).
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

1. **Progressive disclosure** (A): may screens be hidden until unlocked? It
   changes the navigation every existing player knows. My recommendation:
   yes, for new accounts only (accounts past level 20 keep everything).
2. **Rebirth Trials** (C): build them next? Which modifiers do you like?
   My pick for a first set: Iron Life (no Vault, no kept gear), Lean Life
   (gold income halved), Lone Life (one working slot).
3. **Merge Hall + Breeding + Rebirth into one "Bloodline" screen** (B)?
4. **The new-player funnel** (A): may I add funnel telemetry for the first ten
   steps and send you a weekly summary?

## 4. What I am doing without waiting

- The lineage overhaul (built, tested, see its design doc).
- Personal life records and the "Next step" card are small and safe; they are
  next on my list if time remains.
