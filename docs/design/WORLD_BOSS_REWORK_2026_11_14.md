# World boss rework: replacing the shield wheel (2026-11-14)

Owner, 2026-11-14: "the wheel is nothing much" - replace the shield wheel
fight and make the world boss fight more engaging, with graphics and effects.
Process the owner asked for: several proposals, then four independent model
evaluators (Fable, Sonnet, Haiku, Opus) pick once; build what they agree on.

## 1. What exists today (measured from the code)

- **Cadence.** One encounter a week (Mon-Sun UTC), **one strike per player per
  day**. Mon/Tue strikes register the player and size the boss's HP (Chernobog
  spec 2026-10-13); HP is revealed Wednesday.
- **The strike today is the shield wheel** (task 36): a 3 s countdown, then a
  24 s minigame. Five armour plates form a spinning ring that changes speed and
  reverses; the player throws 5 spears by tapping; a narrow centre band of each
  plate is a Seam (scores 1), everything else scores 0. 2-3 times the boss
  winds up a blow (left / right / overhead) and the player must press the
  matching dodge/block button within 2 s; a correct read gives one guaranteed
  Seam on a plate of their choice, a wrong read loses a spear.
- **Damage** = max(1,000, the character's attack including the skill tree) x
  skill multiplier M (1.0 auto-strike .. 2.0 perfect) x plate multiplier (the
  hidden weak plate pays 3x). The first hit on a plate in an attempt can break
  it for everyone, so the crowd narrows down the weak plate over the week.
- **Auto-strike** (no minigame) pays the 1.0 floor; **practice** is free.
- **Server owns everything**: the client gets a server-issued random schedule,
  sends only timestamps and choices, and the server scores them. Nothing the
  client computes is trusted.
- **Rewards**: weekly damage milestones (gold scaled to income, 60 diamonds a
  week, avatars, the Black Serpent pet), top-3 titles, kill reward. Enrage at
  25% HP switches the portrait to the Black Serpent form.
- **Builds**: the skill tree has a "Titan-slayer" lane that is best against
  the world boss; pets and potions also add.

**Players and platform.** Three real players, all endgame; idle/incremental
RPG; web and Android (portrait phone, 390 px wide, touch); a strike happens
once a day, so it can be a small event, but a player who plays daily for a
year does it 365 times. Slavic mythology setting (Chernobog, Svarog, the Black
Serpent). Reduced-motion and colour-blind safe play are required.

**What is wrong with the wheel (owner + measurement).**
- It is abstract: a spinning ring of plates does not read as fighting a god.
- Skill is mostly precise timing on a narrow band; the parry is the only
  decision, and it is a reflex test.
- Your character, build, gear and pet do not appear in the fight at all - the
  strike is the same minigame for a fresh account and a level-90 one.
- The boss never visibly reacts; there is little spectacle.

## 2. How other games run world/raid bosses

| Game | Shape | What makes it work |
|---|---|---|
| Tap Titans 2 (clan raid) | Tap frenzy on body parts; parts have separate HP and armour; clan coordinates on parts | Physical feedback, part targeting is a group decision |
| AFK Arena / Idle Heroes (guild boss) | Auto-battle of your team, once a day, damage leaderboard; you choose the team and the moment to fire ultimates | Your build IS the fight; daily ritual; low effort |
| Raid: Shadow Legends (Clan Boss) | Auto-battle with fixed boss rotation (AoE, stun, debuff); value comes from team composition and speed tuning | Deep build puzzle against a known pattern |
| Slay the Spire | Turn-based; enemy shows its intent next turn | Decisions over reflexes; readable |
| Monster Hunter / Dark Souls | Telegraphed attacks, punish windows, part breaks | Reading the boss; visible reactions |
| Destiny raids / WoW world bosses | Phases, mechanics everyone must learn, damage windows | Phases change the fight; community moment |
| Legends of IdleOn (world bosses) | Short active session with simple dodge, rewards by damage | Idle-game friendly length |

## 3. Proposals

Each is a full replacement for the strike; the weekly calendar, registration,
HP sizing, rewards and the server-owns-scoring rule stay.

### P1. War council: a turn-based duel with boss intents

The strike is a **5-round duel**. Each round the boss shows its **intent** for
that round (Slay the Spire style): *Crushing Blow*, *Black Flame* (damage over
time), *Serpent Coil* (guards, armour up), *Curse* (halves your next hit),
*Summon* (a shade that must be killed first). The player picks one of four
actions drawn from their build: *Strike*, *Guard*, *Break Armour*, and a
**build ability** (from their keystone / tree lane / pet: e.g. Bleed, Sunder,
a pet's charge). Good answers to the intent multiply damage; bad ones waste the
round. The server deals the whole duel from a seed and scores choices.
- Pros: decisions not reflexes, readable, your build matters, trivial to make
  cheat-proof (choices only), works with reduced motion, phone-friendly.
- Cons: can become solved (a lookup table) unless intents combine; less
  "action"; needs ~10 ability definitions.

### P2. Live arena: real-time telegraph dodging

A 30 s real-time scene: your character auto-attacks the boss; the boss throws
telegraphed area attacks onto a **3-lane floor** (red zone fills, then lands).
The player swipes between lanes to dodge. Hitting **exposed weak points**
(they glow after a big attack) by tapping them gives crits. Damage = time
alive x attack x weak-point hits.
- Pros: most "action", strong spectacle, feels like fighting.
- Cons: reflex test again (the owner's playtest had trouble reading a 1 s
  window); hardest to make cheat-safe (positions over time); heaviest to build
  and to keep smooth on a 4x throttled phone; reduced-motion version is hard.

### P3. Rhythm of the forge: timed combo strikes

Svarog's hammer rhythm: rings shrink onto rune targets on the boss's body
(osu!/rhythm style). Tapping on the beat builds a **combo** that multiplies
damage; the boss periodically **counterattacks** (a hold-to-block prompt).
Higher combo = bigger final **Hammer of Svarog** finisher.
- Pros: very readable, satisfying with sound, easy to verify (timestamps vs a
  server-issued beat map, like the wheel today), phone-friendly.
- Cons: still a timing test; character/build only scales the result; music and
  sound carry it, and the game has no music pipeline yet.

### P4. Watch the battle, call the shots: auto-fight with decision points

The strike shows **your own character fighting Chernobog** with the real
combat engine (your stats, tree, pet, potions), about 45 s of animated
auto-battle with damage numbers. Three or four times the boss **winds up a
named signature move** and the fight slows: the player has 3-4 s to choose a
response from 3 options (*Guard*, *Interrupt with a heavy blow*, *Drink a
potion*, *Use pet ability*, *Go all in*). The right answer depends on the move
and on the player's build (a Warden can tank Black Flame; a glass cannon must
dodge). Between calls, the player can hold a **Focus** button to charge a
finisher that releases at the end. The server simulates the fight from a seed;
the client replays it and sends only the choices.
- Pros: the build, gear and pet ARE the fight (fits the idle genre - AFK Arena
  / Raid style); decisions over reflexes; strong spectacle from the existing
  combat scene and sprites; cheat-proof (choices only); reduced motion easy.
- Cons: less direct control; the auto-battle must look good; signature moves
  need art/effects; risk that players skip-watch (needs a speed-up).

### P5. Body parts and the crowd: break the god piece by piece

Chernobog is drawn large with **five breakable parts** (horns, left arm, right
arm, chest rune, serpent tail), each with its own week-long HP bar shared by
the whole server. A strike is a short **targeting session**: the player picks
a part, then a 20 s tap-burst where weak spots flash on that part (tap them
for crits, tap speed capped by the server). Breaking a part changes the boss
for everyone for the rest of the week (arm broken = his counterattacks weaker;
horns broken = enrage delayed; chest rune = weak point exposed), and pays the
breakers a trophy.
- Pros: strongest community layer (the crowd decides what to break), the boss
  visibly changes during the week, juicy tapping.
- Cons: tap frenzy is shallow and bad for wrists every day for a year; three
  players is a thin crowd; anti-cheat on tap rate needs care.

### P6. Weekly saga (a meta layer, combinable with any of P1-P5)

The week becomes a story in phases: **Mon-Tue Chained** (registration, the
chest breaks), **Wed-Fri Unbound** (full fight), **Sat-Sun Black Serpent**
(enrage form, new moves). A daily **omen** changes one rule (e.g. "Svarog's
fire: fire skills +50%", "Mirror scales: crits are reflected"), so the best
build changes by day. A server-wide **Forge meter** fills from everyone's
strikes; when it fills, Svarog's hammer falls (a cinematic and a big shared
damage burst).
- Pros: variety over 365 strikes, makes builds and loadouts matter, a shared
  moment even with three players.
- Cons: not a fight by itself; needs pairing with a strike design.

## 4. Graphics and effects candidates (independent of the choice)

- Big boss art with an idle "breathing" animation and parallax background;
  hit flash, small screen shake, knock-back on heavy hits (reduced motion off).
- Damage numbers with crit sizes; a ticker of other players' hits ("Tomda hit
  Chernobog for 12,400").
- Phase transition cinematic: Chernobog -> Black Serpent at 25% HP.
- Particles: embers, runes, black smoke; the chest-break intro on Monday.
- Sound: a hit, a crit, a boss roar per signature move (ElevenLabs SFX works).
- A result card that recaps the strike and the player's rank today.
- The boss's HP bar as a carved stone bar with cracks that grow.

## 5. Evaluation (2026-11-14, one run, as the owner asked)

Four evaluators (Fable, Sonnet, Haiku, Opus) each read only sections 1-4 of
this document, with no recommendation in it, and answered once.

| Evaluator | Choice | P1 | P2 | P3 | P4 | P5 | P6 |
|---|---|---|---|---|---|---|---|
| Fable | P4 + P6 (P1's visible intent for the calls) | 7 | 3 | 5 | **9** | 4 | 8 |
| Sonnet | P4 + P6 | 6 | 3 | 4 | **8** | 3 | 7 |
| Haiku | P4 + P6 | 7 | 4 | 5 | **8** | 4 | 6 |
| Opus | P4 + P6 | 7 | 3 | 5 | **8** | 4 | 8 |

**Unanimous: P4 + P6.** What they agreed on, and what was built from it:

- ~40 s with a speed-up; three decision calls; 3 big buttons; a generous
  window (4-6 s) with a neutral default on timeout. Built: 6 s, 1x/2x.
- The right answer depends on the move AND the build AND the omen, or it
  becomes a lookup table (all four named this risk). Built: per-stance
  answers (Might / Precision / Guard, from the deepest combat column of the
  world-boss build) and a daily omen whose move always comes.
- The server is the single truth; the client never re-simulates (all four
  named drift as the top technical risk). Built: the server deals every blow
  at issue and answers each call with its hit; the base hit is quoted by the
  tick at issue and carried on the order, so shown == credited.
- Two of four (Fable, Opus) said drop the hold-to-charge Focus button (a
  reflex input, bad one-handed); the finisher should come from the calls.
  Built that way: three right calls land Svarog's Hammer (+0.1x).
- The Forge meter (P6's shared moment) was everyone's "last" item: not built
  yet.

## 6. What was built (PR "world boss duel", client 1.90.0)

- `BossDuelRules` (moves, answers, stances, omens, M), `BossDuelService`
  (REST: `GET/POST /api/v1/worldboss/duel`, `POST /api/v1/worldboss/duel/call`),
  `WorldBossTickCoordinator.DrainDuelQuotes`, mode `FOLKIDLE_BOSS_MINIGAME=duel`.
  The wheel stays in the code behind `wheel` until the owner has played the
  duel; then it can be deleted.
- Economy unchanged: M from 1.0 to 2.0, the wheel's Floor and Cap. Measured
  (`BossDuelTests` ledger, 20,000 duels per stance): random answers 1.36
  (Balanced) to 1.45 (Precision/Guard); best answers 2.00. The wheel's random
  tapping was 1.35. FairShare was 0.35 at first and paid guessing 1.43-1.50,
  so it is 0.15.
- No plates in duel mode: the weak plate's 3x and the crowd deduction are
  gone; auto-strike is a plain 1.0x.
- Client `BossDuel.svelte`: the boss art (Black Serpent art when enraged),
  the player's race portrait, blows with floating numbers, hit flash, a lunge,
  a small shake on heavy hits, a wind-up pulse and telegraph sound, verdict
  feedback by text and border style (not colour alone), a result card that
  names the right answer. Reduced motion turns every animation off.
- `exercise.mjs` world-boss block: practice touches nothing; a real duel
  spends the strike and the card's damage equals the board's gain; 32/32.

Open: the Forge meter; a roar per move (ElevenLabs SFX); a ticker of other
players' hits; re-measure the HP factor after the first duel week.

## 7. Questions for the owner (after you have played the duel)

1. **Delete the shield wheel?** It is still in the code behind
   `FOLKIDLE_BOSS_MINIGAME=wheel` (ShieldWheel.svelte, the wheel scorer and
   schedule, the plate UI, two exercise blocks - roughly 3,000 lines).
   Recommendation: delete it after one week of the duel, if you do not want
   it back. Switching back today is one line in the box `.env`.
2. **The Forge meter** (P6's shared moment, the one piece not built). Options:
   - (a) cosmetic only: every right call by anyone fills Svarog's forge; when
     it fills, everyone sees the hammer fall on the World Boss screen and gets
     a small mail (e.g. 5 diamonds). No change to damage or HP sizing.
   - (b) a damage burst: the full forge deals a fixed share of his health.
     Changes the HP math the Chernobog spec sized (5.5-7.0x) - needs a week
     of duel data first.
   Recommendation: (a) now, (b) only if the boss stops falling.
   **Decided 2026-11-15: (a). Built and DEPLOYED (#422, client 1.91.0,
   migration `WorldBossForge`):** `WorldBossForge` - 15 embers (right duel
   calls, anyone's) fill the forge; each feeder of that fill gets 5 diamonds;
   the hammer animation plays once per fill per device. No damage.
3. **Sounds** - the roar, the serpent hiss and the hammer were generated
   with ElevenLabs and loudness-matched by measurement, but no person has
   listened to them. If one is wrong, `tools/assets/manifest.json` has the
   prompts (`sfx_boss_*`, `sfx_serpent_hiss`).
