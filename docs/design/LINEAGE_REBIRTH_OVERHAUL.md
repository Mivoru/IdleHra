# Lineage and Rebirth overhaul (2026-11-13)

Owner brief (2026-11-13, autonomous until Friday 15:00): breeding and rebirth
are both unfinished, unclear and buggy. 50/50/50/50 is "almost impossible",
rebirth stops at 20, keeping every race means dodging inbreeding with too few
partners, and rebirth should pay more without becoming an exploit. Endgame
players (Tomda, (pro)boss: 20 rebirths, level ~90) have nothing left to do.

The owner left every decision to me. Each one below has its reason and the
measurement behind it.

## 1. What is actually true today (measured on production, 2026-11-13)

| Finding | Evidence |
|---|---|
| The two 20-rebirth accounts have a ceiling of 50 and their best aptitude is 21-23. Most members sit at 15-20. | `character_lineage_registry`, Tomda and (pro)boss |
| Past the village (20), an aptitude climbs about +0.2-0.33 a generation. That is 77-119 generations from 25 to 50 for ONE selected stat. The fourth stat can never be selected. | `BreedingClimbTests`, `BreedingAptitudes.Mutate` |
| A pairing needs the same race. Villagers arrive spread evenly over all unlocked races: (pro)boss has 1-4 newcomers per race, Tomda 1-4. One strong line cannot carry into another race, so every race is its own breeding program with its own inbreeding problem. | `village_newcomers` grouped by race; `BreedingGateRules.RaceMismatch` |
| A child grows up only while it works in one of the three slots (owner rule 2026-10-21, for adults' aging). So every newborn takes a working slot away for an hour before it can breed. | `SimulationEngine.ProcessAgeSlot` |
| Legacy Shards are paid at every rebirth (40k-55k banked by the top three accounts) and **the web client has no way to spend them**. The server's three perks (XP, gold, combat speed; 50 ranks each, 20+8r shards) are applied live and offline (shared helpers). `LegacyPerks = 0` on every account. | `PlayerLegacyLedgers`, `LegacyPerkResolver`, grep of client_web |
| Rebirth is capped at 20, and everything except the bloodline ceiling stops at 10. Both 20-rebirth accounts can never rebirth again. | `RebirthRules.MaxRebirths`, `RewardedRebirths` |
| The genome's Speed/Crit/Yield loci are bred and stored but read by nothing. | `CharacterLineageRegistry` comment, grep |

## 2. Breeding: what changes

### 2.1 Any two races may pair; the player picks the child's race

Removing the same-race gate is the one change that makes "all races" possible.
One strong bloodline can now marry into any race, and the child is born as
whichever of the two parents' races the player chooses. Racial passives and
mastery stay per race, so a race is still an identity, not a stat.

- Unlocking a race still matters: you need a member of it (the boss pair, or a
  villager, who only arrive in unlocked races).
- Every villager is now marriageable for every line, so the village pool is
  effectively six times larger and inbreeding is easy to avoid.
- Default child race: the hero's race (villager pairing) or the father's race
  (roster pairing), so an old client that sends no choice behaves sensibly.

Rejected alternative: hybrids with mixed passives. It needs new art and a new
passive system, and makes races blur.

### 2.2 A selected aptitude always grows (until the ceiling)

The climb past the village was a dice game with a 10% chance of going
backwards on the exact stat the player chose to improve. New rule, in one
sentence each:

- **Selected aptitude:** the child takes the better parent's value, then
  **+1 guaranteed, +2 while it is 10 or more below the ceiling**, plus the
  usual luck roll (+1 at 25% + Grounds level). Never goes down.
- **Other aptitudes:** inherited from one parent as today (the stronger parent
  is likelier), then +1 / -1 luck; while 10 or more below the ceiling, a
  further 50% chance of +1.
- **Related parents:** no guaranteed growth and the luck roll is reversed (as
  today), plus the flaw risk. One clear sentence in the preview.
- **The bloodline ceiling (set by rebirths) is unchanged**: 20 with no
  rebirths, +1.5 per rebirth, 50 at rebirth 20.

Selection gets cheaper: 1 aptitude at Grounds 1, 2 at Grounds 4, 3 at
Grounds 7 (was 4/7/10). Still never four, so the partner's numbers still
matter for the fourth stat.

Measured (Python model of the rule, 300 runs, Grounds 12, ceiling 50, start 20):

| Partner | Selected | Generations to 50/50/50/50 (median / p90) |
|---|---|---|
| unrelated line member | 3 | 16 / 17 |
| unrelated line member | 2 | 19 / 21 |
| fresh villager each time | 3 | 25 / 29 |
| fresh villager each time | 1 | 75 / 91 |

The old rule needed 77-119 generations for ONE stat from 25 to 50. Now a
20-rebirth account fills all four in roughly 16-29 generations (a few days of
casual breeding), and a new account fills its ceiling of 20 in about six. The
ceiling, not the dice, decides how far a bloodline can go, which makes every
rebirth a visible breeding gain.

### 2.3 Children grow up on their own

A newborn becomes an adult one hour after birth, online or offline, whether
or not it works. Adults still age only on their own work (the owner's
2026-10-21 rule is about adults wearing out, and stays).

### 2.4 Clarity

- The preview drops the genome locus rows (they do nothing) and shows, per
  aptitude: the band, whether it is selected, and the guaranteed growth.
- Refusal "race mismatch" disappears; the race picker appears instead when
  the two parents differ.
- Wiki and Breeding Grounds text rewritten to the new rules.

## 3. Rebirth: what changes

### 3.1 No cap

Rebirth is unlimited. The bloodline ceiling still tops out at 20 rebirths and
the power rewards (skill points, Renown damage) still stop at 10, so power
stays bounded for newcomers. What keeps paying forever is Legacy Shards.

### 3.2 Legacy Shards become the rebirth's real reward: the Ancestral Boons

The shards already exist, are already paid, and the three perks are already
wired live. What was missing: a screen, an offline path, and sane numbers.

- **Retuned so the banked shards are not a jackpot.** 50 ranks of +1% each,
  with a cost of 20+8r, would let the top accounts buy +50% XP, +50% gold
  and +50% combat speed tonight with what they already hold.
  New costs are geometric and ranks are capped lower (see section 6 for the
  numbers and the measurement).
- Already applied offline as well as live (the kill, gold and damage helpers
  are shared), so nothing to change there.
- **A Boons panel** on the Rebirth screen: balance, each boon's rank, effect
  and next price.

### 3.3 Shards paid by how far the life went

The payout today is `12.5 log10(gold) + 0.05 level^2 + 1.5 x inventory score`.
The inventory term rewards hoarding gear right before a rebirth. The new
payout keys on the level the life reached and the bosses it beat, which a
player cannot inflate by hoarding. Numbers in section 6.

### 3.4 Milestones past 20

Every 5 rebirths past 20 pays a cosmetic step (badge tier shown beside the
name), never power. A count that keeps climbing is the visible "I am a
veteran" signal endgame players asked for.

## 4. Endgame: Bloodline deeds

Book of Deeds gains lineage entries: an aptitude at 30, 40, 50; a perfect
50/50/50/50; a 40+ member of every race; ten generations; a legendary trait.
They give the 20-rebirth players a concrete list for the new climb.

## 5. Self-review: what could go wrong

- **Mixed race makes the boss race pair worthless?** No: it is still the only
  first member of that race, and villagers only arrive in unlocked races.
- **Guaranteed growth makes breeding "solved"?** It makes the ceiling the
  limit, which is the owner's 2026-10-14 intent ("50 should take about twenty
  rebirths"). Breeding still has traits, epics, the fourth stat and race
  porting to chase.
- **Existing lines above the ceiling** stay grandfathered (unchanged rule).
- **Old clients** send no race choice: the default race keeps them working.
- **Boons too strong with 55k banked shards?** The retune is designed against
  exactly that balance; see section 6.
- **Uncapped rebirth exploit (rebirth at level 1)?** The level-50 floor stays.
  Shards scale with level, so a level-50 rebirth pays much less than a
  level-90 one.
- **Offline growth of children vs the slot payload:** a fielded child's age
  lives in the session payload. The promotion must update the payload as well
  as the row, or the session keeps treating it as a child.

## 6. Numbers (as built)

| Lever | Value | Where |
|---|---|---|
| Sure growth of a selected aptitude | +1, +2 while 10+ below the limit | `BreedingAptitudes.SureGrowth` |
| Catch-up of an unselected aptitude | 50% chance of +1 while 10+ below | `BreedingAptitudes.CatchUpPercent` |
| Selections | 1 / 2 / 3 at Grounds 1 / 4 / 7 | `SelectableCount` |
| 20 -> 50/50/50/50 (fresh villager each time, Grounds 12) | median 24, p90 28 generations | `BreedingClimbTests` |
| Childhood | 1 hour from birth, working or not | `ChildGrowth` |
| Shards per rebirth | level^2 / 2 (50: 1,250; 90: 4,050; 100: 5,000) | `RebirthRules.ShardsForLevel` |
| Boons | XP +25%, gold +25%, damage +10%; 200x1.12^r, 200x1.12^r, 500x1.3^r | `LegacyPerkResolver` |
| All boons maxed | ~74,600 shards (18 level-90 rebirths) | `AncestralBoonsTests` |
| Title milestones | 20 Keeper of the Blood, 30 The Undying, 50 Wheel of Ages | `RebirthRules.TitleMilestones` |
| Deeds chapter VI | aptitude 30, 40, 50x4, six races at 30, gen 10, 20 rebirths; a Seal | `DeedRegistry` |

Also built: **Best matches** - the five best eligible partners for a hero,
ranked by the child they would make, with the aptitudes to tick
(`BreedingMatchmaker`, `GET /api/v1/breeding/suggest`).

### What existing players get on deploy

- Every child already on a roster (born before `BornAtEpoch` existed) reads as
  grown at the next read and becomes an adult.
- Lines already above the ceiling keep their values (grandfathered).
- The 20-rebirth accounts can rebirth again; their next rebirth also grants
  Keeper of the Blood.
- Banked shards (40k-55k) can be spent at once, on the new prices.
