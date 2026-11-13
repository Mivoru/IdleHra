# Endgame content and the long loop (2026-11-13)

Owner brief: "after breeding and rebirth, look at what new content endgame
players need so they do not get bored and have goals; take ideas from popular
games of this kind; adjust the progress / game loop."

Players this is for: Tomda (level 89, 20 rebirths), (pro)boss (92, 20) and
Mivoru (107, 4). Player count is not a concern.

## 1. What the three accounts actually do (production, read-only)

| | Tomda | (pro)boss | Mivoru |
|---|---|---|---|
| Dungeon best stage per gate | 9 / 9 / 9 | 5 / 8 / never | 13 / 13 / 13 |
| Delve deepest floor | 10 | 0 (never) | 23 |
| Boss Ascension steps (regions 1-5) | 0 / 0 / 0 / 0 / 2 | 0 / - / - / - / 1 | 10 / 10 / 10 / 10 / 9 |
| Book of Deeds Seals | 3 of 6 | 2 of 6 | 5 of 6 |
| Titles | 47 | 15 | 57 |
| Days with income, last 14 | every day | every day | every day |

Read:
- **All three log in every day.** The game is a daily habit; what is missing
  is a reason to make choices in it, not a reason to open it.
- **Boss Ascension was not re-climbed after its 10-30 reset by two of
  three.** First read as "cosmetics-only content is skipped" - wrong: since
  10-30 every new step pays a loot chest and step 10 +2% region damage. But
  the Ascension panel, the Wiki and the quest line still said "cosmetics
  only" (corrected 2026-11-13, with a Your road row). Re-climbing regions 1-4
  the account already beat is also plain repetition; watch whether the fixed
  copy moves it.
- (pro)boss has not entered the Delve or the third Dungeon gate at all - the
  "Your road" card (#342) now points there.

## 2. What the game already has for the endgame

Regions 1-5 and their bosses; Boss Ascension (10 steps per boss, titles); the
Dungeon (3 gates x 15 stages, tickets 2/day); the Delve and the Deep (weekly);
the world boss (weekly payout); Book of Deeds (6 chapters, Seals = skill
points); rebirth (unlimited), Trials, Ancestral Boons, life records; breeding
to 50/50/50/50 with aptitude milestones; Hall of Ancestors; inheritance;
Forge/affixes/sets; Great Works; codex; pets; cosmetics; leaderboards
(standing, Deepest, Fastest lives, Best blood).

So the problem is not a lack of systems. It is (a) some systems pay nothing a
progression player values, (b) nothing ties the systems into one weekly plan,
and (c) bred characters beyond the three slots have nothing to do.

## 3. What comparable games do

| Pattern | Seen in | What it gives |
|---|---|---|
| Rotating bounties / tasks with a token shop | OSRS Slayer, Idleon dailies, Melvor | A small set of fresh targets every day/week across all content, and a currency that buys something lasting. Directs players into content they skip. |
| Expeditions for the bench | Idle Champions adventures, AFK Arena bounties, Idleon | Characters not in the active party go on timed missions and bring loot back - a reason to own many heroes. |
| Endless scaling tier with a ladder | Diablo Greater Rifts, PoE maps, Mythic+ | A push that never runs out; the number itself is the goal. |
| Weekly modifier | Mythic+ affixes, Diablo seasons | The same content plays differently each week; a bonus for doing it under the rule. |
| Post-cap points | Diablo Paragon, PoE atlas points | Every level after the cap still pays a tiny permanent point. |
| A second prestige layer | Realm Grinder, NGU, Antimatter | A longer reset that multiplies the first. |

## 4. Proposals, ranked by value for these three players per effort

### A. Expeditions for the Hall (recommended first)
Benched grown ancestors go on timed expeditions (4 h / 8 h / 12 h) to a region
the account has cleared. Success and haul scale with the party's aptitudes
(Strength for hunts, Skill for gathering runs, Fortune for treasure). Returns:
materials of that region, a chance at a loot chest, Legacy Shards in small
amounts. Two or three expedition slots, more from the Breeding Grounds level.
- Why: the Hall holds hundreds of bred adults with nothing to do. This makes
  every good child useful twice (as an heir and as an expeditioner), gives a
  check-in goal, and makes the aptitude milestones matter off the main slot.
- Risks: a new material faucet (gold inflation is already open). Cap it as
  "minutes of the region's reference gathering", the Workshop/chest unit.
- Effort: M-L. One table, one engine, one screen tab, exercise checks.

### B. Bounty board (weekly)
Five bounties a week drawn from all content: "clear Dungeon stage N", "reach
Delve floor N", "climb one Ascension step", "breed a child with lowest
aptitude N", "deal X to the world boss". Each pays Ancestral Marks; Marks buy
lasting things - a Hall slot, an expedition slot, a Vault slot, a Trial
re-take, cosmetics.
- Why: one weekly plan across every system, and it points Tomda and (pro)boss
  at the Ascension and Delve they skip - with a progress reward.
- Effort: M. Goals reuse DeedProgressSource-style readers.

### C. Ascension: say what it pays (done 2026-11-13)
Approved as "Ascension pays Legacy Shards". On reading the engine it already
pays a chest per new step and +2% region damage at step 10; only the copy was
wrong. Fixed the copy in the panel, Wiki and quest line and added an
Ascension row to Your road instead of adding a second reward.

### D. Endless Dungeon tier
Past stage 15 of a gate, "Depth N" scales on (the simulator already scales by
stage), with a weekly "Deepest gate" ladder and a small chest per new depth.
- Effort: M.

### E. Weekly Dungeon/Delve modifier
One rule per week ("monsters crit twice as often", "no potions") with +25% rewards.
- Effort: S-M.

### F. Paragon points after level 100 / a second prestige layer
Strong long-term levers, but they touch the whole power curve.
- Effort: L; needs balance measurement first. Not now.

## 5. The loop after these changes

| Cadence | Goal |
|---|---|
| Every session | Expeditions back, send the next; Dungeon tickets |
| Daily | Bounties progress; world boss strike |
| Weekly | Bounty board refresh; Delve/Deep; Best blood; world boss payout |
| Monthly | Bloodline toward 50/50/50/50 (milestones on the way); Trials; Ascension steps |
| Every life | Rebirth with a Trial, Boons, life record |

## 6. Recommendation

C is done (copy, not a new reward). Build **A** next (the content that ties
breeding to everyday play), then **B**. D and E after that; F not before
measuring. A and B add currencies/faucets, so their reward sizes are set in
the units the game already uses (minutes of reference gathering, shards) and
need the owner's yes.
