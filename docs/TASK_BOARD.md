# FolkIdle Task Board

Seven tasks, restated against what the code actually does as of 2026-09-01.
Every "today" claim below was checked in the source or the live database rather
than remembered — where a task turned out to be different from its one-line
description, the difference is called out, because two of these are much smaller
than they sound and two are much larger.

Each task carries **Done when** criteria. A task with no way to tell it is
finished is a mood, not a task.

Ordering for the closed set was 7 → 1 → 2 → 6 → 5 → 4 → 3, cheapest and most
visible first, riskiest last. **For the open set it is 8 → 9 → 10, and that is a
dependency rather than a preference — see "Execution plan" below.**

**Tasks 1-10 are all done.** The table below is kept as the record of what each one turned out to be:

| # | Open task | Shape |
|---|---|---|
| 8 | ~~Combat is not readable as a fight~~ | **DONE 2026-09-04.** The bar was never broken — the wire carried no combat event at all. Feed, log and death animation shipped |
| 9 | ~~Rarity barely does anything~~ | **DONE.** Ladder 1.48x → 3.00x, equal to one region step; monster health buffed per region so kill time and XP/sec land within 0.2% of neutral |
| 10 | ~~World boss rework~~ | **DONE 2026-09-05.** Five armour plates, one soft, re-seeded per encounter. The client sends a choice, not a damage figure. Two invisible-failure defects fixed on the way |

---

## Execution plan for 8, 9 and 10

Written 2026-09-04, before any of the three was started. Order, phases, and a
gate at the end of each phase. A phase that cannot pass its gate stops; it does
not get carried into the next one.

### Order: 8 -> 9 -> 10, and the reason is a dependency, not a preference

**8 first.** It contains the only thing confirmed broken, it is the most visible
to the player who reported it, and — the real reason — **its middle phase builds
infrastructure the other two need**. A server-side combat event feed is what
makes a fight readable; task 9 is unfelt without it (a player who cannot read
what a hit did cannot notice that rarity now matters) and task 10 is unbuildable
without it (a boss fight that is "a fight" has to narrate itself).

**9 second.** Medium-high balance risk, and it wants measurement before code. It
also wants the log from 8 in place, so "rarity now matters" can be confirmed by
a player reading their own hits, not only by a printed table.

**10 last.** The biggest *design* and the least specified. By then the combat
presentation layer exists and the boss can reuse it instead of inventing a
second one.

### Three findings already made, so they are not re-derived

All from reading the source on 2026-09-04, none observed in a running game yet —
treat them as leads with a known address, not as confirmed symptoms.

1. **The client's boss denominator ignores First Blood.** `Combat.svelte`'s
   `shownMaxHp` is a flat `MaxHp * FIRST_CLEAR_HP` (5). The server's
   `BossFirstClearRules.MaxHpFor` *softens the penalty* by the First Blood
   bough — about 3.4x at level 8. A player who has invested in First Blood sees
   a bar whose maximum is far too large: it starts part-empty and the monster
   dies well before the bar does. That is candidate cause (1) above, with an
   address.
2. **The client reads raw `monster.MaxHp`; the server reads
   `ContentRegistry.GetScaledMonsterMaxHp`.** They agree for regions 1-5 and
   diverge past `MaxAuthoredRegionTier`, where endgame scaling multiplies. Not
   the reported symptom — the report is early-game — but the same class of
   defect and cheap to fix while in the file.
3. **A hit reaction on the monster portrait already exists.** `struck` in
   `Combat.svelte`, rate-limited to one flash per 140 ms and keyed off the
   authoritative HP rather than the interpolated one. The "hit reaction" bullet
   in the polish list above is therefore *partly built*. Look before rebuilding.

---

### Phase A — diagnose the frozen bar (task 8). Half a day.

Nothing changes in this phase except possibly the two denominators above.

1. `.\run-dev.ps1`, sign in as the dev fixture, start a fight in region 1.
2. Instrument, do not eyeball. The board's own standard: a `MutationObserver` on
   the monster bar's width, logging every change with a timestamp, against the
   same observer on the player bar. Run it across 60 s of one fight.
3. Read the two traces together. There are three outcomes and each points
   somewhere different:
   - **The monster bar changes, but rarely** — the fight is one or two packets
     long. That is a *pacing* finding, not a rendering one. Confirm on a fresh
     level-1 account where the first fight takes 75 s; if it animates there, the
     fixture is over-geared and the player's report needs their own level to
     reproduce.
   - **The monster bar never moves while the HP text under it does** — the
     denominator. Finding 1 or 2, or `activeMonster` resolving to a different
     monster than the snapshot describes.
   - **Neither bar moves and the numbers jump** — interpolation is off, which
     happens when `push()` classifies every arrival as a discontinuity. The
     stall threshold in `SnapshotInterpolator` has been wrong in exactly this
     way before, and its comment records it.
4. Write the answer into this file, one paragraph, before any fix.

**Gate:** a written cause. Phases B-D are worth doing regardless, but doing them
*instead* of this is the failure mode the task warns about.

---

### Phase B — the combat event feed (task 8). The largest piece.

**The shape is already decided by the codebase, and it is not new fields on
`StateUpdatePacket`.** That struct is `Pack = 1`, flat, has no array field
anywhere, and its own comment records it at **699 of a documented 700-byte
ceiling**. There is neither room nor shape for a variable-length list.

**Copy `ResponseLootDropPacket`.** It exists for this exact reason — one message
per event, bursty rather than per-tick, dedicated packet, fixed size, blittable.
Its header comment is the argument, already written.

1. Define `ResponseCombatEventPacket`: `PlayerId`, `MonsterId`, `Amount`,
   `EventKind`, plus a flags byte. `EventKind` covers what the server actually
   resolves — **player hit, player miss, monster hit, monster miss, lifesteal,
   kill** — with **crit as a flag, not a kind**. No `Blocked`: armour mitigation
   is a reduction applied to a hit, so if it is surfaced at all it belongs as a
   number on the hit line.
2. **The size must be unique.** Both receive loops distinguish message types by
   byte count alone; `NetworkPacketLayoutGuard` is where that is enforced and
   where the new size gets pinned.
3. Emit from `SimulationEngine`'s existing resolution points. It already
   computes every one of these and throws the detail away: the hit roll, the
   crit roll, `NetMilliDamage`, the lifesteal block at ~5625, the kill check.
   This is emission, not new mechanics — **if a number has to be recomputed in
   order to emit it, the emit is in the wrong place.**
4. **Rate-limit at the source.** An idle account fights forever and the server
   should not narrate a session nobody is watching. Cap events per tick and drop
   rather than queue.
5. `npm run generate:protocol`. Never hand-write the mirror.

**Gate:** `dotnet test` green (stop the server first), `generate-protocol.mjs
--check` clean, and a server test asserting **a resolved miss emits an event** —
a miss is the one event no HP delta can imply, so it is the proof the feed is
real rather than a re-derivation.

---

### Phase C — the log UI (task 8)

1. A ring buffer store, last N events (start at 50). It is a log, not a ledger.
2. Rendered **under the monster card**, where the report puts it.
3. Lines name only real mechanics. Shapes to tune in the file: `You hit Field
   Mouse for 412` / `Critical! 861` / `You miss` / `Field Mouse hits you for 8`
   / `Lifesteal +4`.
4. **Retire the inference where the feed replaces it.** `stores/damage.ts`
   exists only because the wire carried no damage event; once it does, the
   floating numbers should read the feed. Keep `inferDamage` and its tests until
   the feed is proven in a real session — but this must *end* as one source of
   truth, because two copies of one fact is this repo's dominant bug class.

**Gate:** `npm run check`, plus `check:clipping` and `check:overlap` at 390 px —
the log sits in exactly the real estate those two walk — and `npm run exercise`
still green, with a **new exercise step asserting the log gains a line during a
fight**. A log that renders and never fills is precisely the "output side was
never wired" defect this project ships worst.

---

### Phase D — motion (task 8)

Only after A-C, scoped to three things in this order, stopping as soon as it
reads as a fight:

1. **A death animation** — the monster currently vanishes and is replaced, and
   this is the one "Done when" item no other phase covers.
2. **Extend the existing `struck` flash** into a shake, if the flash alone still
   reads flat. Extend it; do not add a second effect keyed to the same signal.
3. **A wind-up telegraph**, only if 1 and 2 are not enough. Most expensive, and
   easiest to get wrong at a 1.6 s cadence.

**Never key an effect on the interpolated value or on the damage array.** Both
are recorded traps; the second starved the main thread.

**Gate:** the task's "Done when" in full, including the written cause from A.

---

### Phase E — measure before touching rarity (task 9). No behaviour change.

This phase writes a test and changes nothing else. It exists so phase F has a
baseline to be neutral *against*.

1. A test shaped like `ProgressionRateTests` / `GatheringShareTests`: print
   expected equipped power for regions 1-5 at each of the 14 quality tiers, plus
   a **median-drop row** per region — the power of the item an average kill
   actually produces, weighted by the drop table's own rarity odds.
2. That median row is what the monster ladder is tuned against. Record it here.
3. Confirm the two dead levers while in the file: `AffixRegistry.RollAffixes`
   takes an `itemRarityTier` it never reads, and `RollAffixRarity()` takes no
   arguments at all.

**Gate:** the table prints and the suite is green. **Do not start F without the
median numbers written down** — "roughly the same" is not checkable after the
fact.

---

### Phase F — redistribute tier weight into rarity (task 9)

1. **Wire the dead `itemRarityTier`** into `RollAffixRarity` first: the largest
   felt change per line altered, and it moves *magnitude* rather than only
   count.
2. **Give the 14 tiers 14 distinct outcomes.** A guaranteed affix count plus a
   probabilistic extra is enough; no two adjacent tiers may be identical.
3. **Only then move base power**, which is the actual redistribution: part of
   the x3-per-region geometric term becomes a rarity multiplier whose *expected*
   value across the drop table is 1.0 at the median. It has to ride the same
   curve as the gear — a flat multiplier re-creates the bug
   `CalculateMagnitude`'s comment describes, where affixes grew linearly against
   geometric gear.
4. Re-print phase E's table. **The median row must land inside a stated
   tolerance of its baseline.** The top and bottom rows are supposed to move;
   how far is the design decision, and it is recorded here.
5. Run `MonsterLadderTests`, `ProgressionRateTests`, `GatheringShareTests`. **If
   they move, the redistribution was not neutral — fix the factors.** Retuning
   monsters is a separate change with its own table, and the ladder may never
   descend.

**Gate:** median within tolerance, three balance suites green, and a real
character in a real session whose hits visibly differ between a low-rarity and a
high-rarity weapon — which is what phase C's log is for.

---

### Phase G — write the world boss design down (task 10). Before any code.

The task's own risk line says the danger is scope and genre fit, so this phase
produces a page in `docs/`, not a commit to `WorldBossEngine`. It must answer,
in order:

1. **What decision does the player make?** Not what they press. The constraint
   is on record: decisions over dexterity, because the anti-cheat has banned
   clickers and the four active skills were removed at "+90% damage for clicking
   every three seconds". A weak-point choice, an ordering puzzle, a resource
   commitment.
2. **Where is the server bound?** No client-supplied score becomes damage
   without a server-side cap. `clientPredictedDamage` is already capped at
   100,000,000 for exactly this reason.
3. **Does it fit three attempts?** `MaxAttemptsPerEncounter` is 3. Either the
   interaction fits three tries, or the attempt budget is part of the redesign —
   decide it, do not discover it.
4. **What survives?** The shared HP pool, `_playerDamageMap` attribution, the
   event window and the defeat path are sound and stay untouched.

**Gate:** all four answered in writing, checked against "this is an idle game"
before a line is written.

---

### Phase H — build the world boss (task 10)

Reuses phase B's feed for narration and phase C's log for readback: a boss fight
that says what happened is most of "a fight" already. `exercise.mjs` must drive
the new interaction, not the Attack button.

**Gate:** the task's "Done when", including two players on one boss with
attribution intact.

---

### What would change this order

If phase A finds the frozen bar is a **pacing** finding rather than a rendering
one — the fight genuinely over inside one packet — then 8 and 9 stop being
separate tasks. Both would then be statements about the same thing: combat
resolves too fast to be read, and too fast for gear to be legible. In that case
run phase E's measurement *before* phase D's polish and decide the two together.

---

## DONE — 8. Combat is not readable as a fight

**Closed 2026-09-04.** Phases A-D of the execution plan below were worked in one
session. The short version: **the bar was never broken, the wire was empty.**

### What was actually wrong

Measured, not guessed - see "PHASE A RESULT" below for the traces. A geared
character kills an early monster every ~1400 ms while StateUpdate snapshots
arrive every ~1090 ms, so across 27 consecutive snapshots `CurrentMonsterHp`
took **exactly one value**. Spawn and death both happened between two samples.
`interpolation.ts`, the `struck` flash, the hit sparks and `inferDamage` were
all correct and all being handed a constant.

The player's own bar moved because its numerator changes on almost every
snapshot - damage landing and auto-eat healing between samples. That is the
whole asymmetry, and it means **no change to the bar could ever have fixed it.**

### What was built

| Piece | Where |
|---|---|
| `ResponseCombatEventPacket` - 26 bytes, one per resolved blow | `server/.../Network/` |
| `CombatEventFeed` - bounded static queue, tick publishes, never touches a socket | `server/.../Domain/Combat/` |
| Dispatch loop, 20 ms idle, copied from the loot feed | `NetworkBroadcastSystem` |
| Emit points: hit, **miss**, monster hit, monster miss, lifesteal, kill | `SimulationEngine` |
| `CurrentMonsterMaxHp` + `PlayerMaxHp` on the snapshot, 779 -> 787 bytes | `StateUpdatePacket` |
| The log store and its wording, pure and tested | `client_web/.../stores/combatLog.ts` |
| The panel, under the monster's picture and bar | `Combat.svelte` |
| The death animation, keyed on a kill counter | `Combat.svelte` |

### Three defects found on the way, all real, all fixed

1. **The monster bar's maximum was a client-side copy of a server rule.**
   `shownMaxHp` was `MaxHp * 5` for an unbeaten boss - which ignores First Blood
   softening the penalty (about 3.4x at level 8) and ignores endgame scaling
   past region 5. The server states the maximum now, from the same call the
   spawn uses.
2. **The player bar's maximum was a session high-water mark.**
   `observedMaxPlayerHp` is `max(seen, PlayerHp)`, so the bar read
   **"2320 / 2320" while PlayerHp was 3701** in a captured trace. `PlayerMaxHp`
   is on the wire now; `CachedEffectiveMaxHp` carries it out of the tick, where
   every term that feeds it was being discarded.
3. **"Blocked" is real, but only in one direction.** Monsters carry no block
   stat, so a player's swing is never blocked; the player's own
   `BlockStrengthPct` (CON-derived) shaves incoming hits. The log says it only
   on incoming lines, and a test pins that. Armour is never its own line - it
   reduces every hit rather than stopping any, so it is already inside the
   number shown.

### What was deliberately NOT done

- **The wind-up telegraph.** Phase A showed slower fights already animate
  correctly (20 distinct health values over 30 s against a 71,000 HP boss), so
  the telegraph would be solving a problem that is not there.
- **Retiring `stores/damage.ts`.** The floating numbers still infer from the
  snapshot. The feed makes that inference redundant and it should end as one
  source of truth - but not in the same change that introduced the replacement.
  **This is the one loose end**, and it is the repo's dominant bug shape, so it
  should be closed deliberately rather than left to drift.

### Verification

- **568/568** server tests, including 6 new ones in `CombatEventFeedTests`. The
  headline is `AResolvedMissIsReported_WhichNoHealthDifferenceCouldEverImply` -
  a miss moves no health, so it is the proof the feed is a real report from the
  simulation rather than a re-derivation of something already on the wire.
- **302/302** client tests, 8 new in `combatLog.test.ts`.
- **114/114** `npm run exercise` (was 99), with four new checks: the log renders,
  the log *fills*, it reports **both** sides of the fight, and no health bar
  reports more health than its maximum.
- 0 clipping findings, 0 overlaps, `svelte-check` at the 4 known `GuildOps`
  errors.
- Measured live on the fast case that started all this - Field Mouse, killed in
  one hit: 34 combat events including 10 kills in 20 s, the death animation
  firing, and the log reading "You miss Field Mouse" / "You hit Field Mouse for
  985" / "Lifesteal heals you for 7" / "Field Mouse dies - 93 xp".

### A note for whoever runs the exercise next

It went 114 -> 108 -> 105 across three consecutive runs on the same fixture,
and all the losses were the documented state-spending checks (the villager pool
emptying, a donation material running out). `--seed-dev` restores it and the
count returns to 114. That is CLAUDE.md's "a check that spends fixture state
passes once and fails forever" behaving exactly as written; none of it was a
regression.

---

## ORIGINAL REPORT — 8. Combat is not readable as a fight

**Reported 2026-09-03, by the player, unprompted:**

> "I can't properly see the fight against the monster. I see his picture, name
> and health bar, but I don't see the health bar moving or effects of my hits.
> I just see my health bar moving."

**Diagnosed 2026-09-04 — the answer is immediately below.** What follows it is
the original symptom and a map of what already exists, kept because it is still
the right map; only the "not investigated" part is out of date.

### PHASE A RESULT, measured 2026-09-04: the bar is not broken, it is starved

**The monster health bar works. The wire never gives it anything to draw.**

Measured against the local stack, dev fixture, by capturing the WebSocket frames
themselves rather than by watching the screen.

**Run 1 — Field Mouse (the first monster), 28 s, 27 StateUpdate frames:**

| what | value |
|---|---|
| distinct `CurrentMonsterHp` values across all 27 snapshots | **1** — always 465, its full health |
| distinct `CurrentMonsterId` values | 1 (91) |
| XP gained | 71,394 → 73,302 = **+1,908**, and a Field Mouse pays 93, so **~20 kills** |
| snapshot cadence | mean **1092 ms** |
| `ResponseLootDrop` frames | 9, arriving in bursts |

A kill every ~1.4 s against a snapshot every ~1.09 s. **Spawn and death both
happen between two snapshots, every single time**, so `CurrentMonsterHp` is
sampled at full health and never anywhere else. There is nothing for
`interpolation.ts` to interpolate, nothing for the `struck` flash to fire on,
and nothing for `inferDamage` to infer. Every one of those is working correctly
and being handed a constant.

**Run 2 — the control, monster 100 at 71,000 HP (first-clear boss), same
account, same 30 s window:**

| what | value |
|---|---|
| distinct `CurrentMonsterHp` values | **20**, descending smoothly 71,000 → 50,945 |
| player HP range | **1,888 .. 3,701** |

So the bar animates perfectly when the fight lasts longer than the sampling
interval. **This is a pacing and sampling finding, not a rendering one**, which
is candidate cause (2) — and it means no amount of work on the bar, the flash or
the sparks can fix the reported symptom.

### The asymmetry, explained

The player's report was "I just see MY health bar moving", and the two bars are
asymmetric for two independent reasons that happen to point the same way:

1. **The monster bar's numerator is sampled once per ~1.09 s** and a fast kill
   fits inside one sample, so it is pinned at full.
2. **The player bar's numerator changes on almost every snapshot** — run 2 shows
   it swinging 1,888 → 3,676 → 3,243 → 2,594 → 3,701 as damage lands and auto-eat
   heals between snapshots. It is *never* still.

So the player is describing exactly what the wire contains. Nothing was
imagined, and nothing about the bar component is wrong.

**A third thing fell out of run 2 and is a real defect on its own:**
`observedMaxPlayerHp` is a session high-water mark (`stores/game.ts:460`,
`Math.max(seen, packet.PlayerHp)`), and the trace shows PlayerHp reaching
**3,701 while the bar had been reading `2320 / 2320`**. So the player's bar
displays a maximum that is whatever the largest number seen so far happens to
be — it starts wrong, grows during play, and is never the character's actual
maximum health. Max HP is not on the wire for either combatant. Fixing that is
small and independent of everything else here.

### What this means for the plan

Phase A's gate is met, and its answer **validates phase B and invalidates most
of phase D**:

- **No bar-side change can fix this.** The value is constant in the data.
- **The combat event feed (phase B) is the fix**, not a nice-to-have: a fight
  that resolves inside one snapshot can still be *narrated* — "you hit for 944,
  critical, Field Mouse died" — even when there is no intermediate health to
  animate. The log is the only thing that can make a sub-second kill readable.
- **A death animation (phase D.1) becomes more valuable, not less**: on a fast
  kill the death is the *only* moment there is, and today the monster silently
  vanishes and is replaced.
- The slower fights already look right, so the wind-up telegraph (phase D.3)
  should be considered dropped unless something later argues for it.

---

### The important half of the report

**"I just see MY health bar moving."** Both bars are built the same way, from
the same interpolated snapshot, in the same component — so one moving and the
other not is a real asymmetry and is the thread to pull first. It is much more
specific than "combat feels flat" and should not be folded into the polish half
below.

### What already exists — do not rewrite these

| Piece | Where |
|---|---|
| Monster HP smoothing | `net/interpolation.ts` — `CurrentMonsterHp` **is** in `INTERPOLATED_FIELD_NAMES` |
| The monster bar | `routes/Combat.svelte` ~line 303, `value={visual?.CurrentMonsterHp ?? snap.CurrentMonsterHp}` |
| Floating damage numbers | `ui/FloatingDamage.svelte`, fed by `stores/damage.ts` |
| Hit sparks, per weapon family, crit-aware | `ui/HitSpark.svelte`, fed by `hitSparks` in `stores/game.ts` |
| Crit + weapon on the wire | `StateUpdatePacket.LastHitWasCrit`, `EquippedWeaponKind` |
| Death / victory cards | `ui/DeathCard.svelte`, `ui/VictoryCard.svelte` |

So the feedback layer is **wired**, which makes this a "why is the wired thing
not visible" question rather than a "build it" question. That distinction is
the whole reason this task is worth reading before starting.

### Candidate causes, in the order worth checking

1. **The denominator, not the numerator.** The bar's max is
   `shownMaxHp(activeMonster)`. A boss carries 5x its authored HP until first
   clear (`BossFirstClearRules`). If the max is wrong the bar can look frozen
   near-full while the number under it changes.
2. **The fight may genuinely be one packet long.** `interpolation.ts` records
   a *measured* 1637 ms mean between monster-HP changes. A geared character on
   an early monster can take it from full to zero between two snapshots — there
   is nothing to animate, and that is a *pacing* finding, not a rendering one.
3. **Main-thread starvation.** The recorded trap "keying an effect on the
   damage array starves the main thread" is in this exact area. Until
   2026-09-03 the Combat screen also sat behind 3.2 MB inventory refetches;
   that is fixed, so **re-check the symptom on the current build before
   assuming it is still present**.
4. **`observedMaxPlayerHp` has no monster equivalent.** Max HP is not on the
   wire for either; the player bar derives a session high-water mark. Check
   whether the monster bar has an honest denominator at all.

### The half the player actually asked for

Death animations for monsters, and more motion during a fight. Scope it before
starting — "more entertaining" is unbounded. A concrete starting list:

- A death animation, since the monster currently just vanishes and is replaced.
- A hit reaction on the monster sprite (shake, flash) — the sparks exist but
  the sprite itself never acknowledges a hit.
- Attack telegraph or wind-up, so a 1.6 s swing reads as an action rather than
  a number changing.

### Also requested, 2026-09-04: a fight log

> "It needs a combat log, so the player doesn't only *watch* it happen but can
> read it back — who did how much and with which attack, whether it was a crit,
> whether the attack was blocked, lifesteal and so on — sitting under the
> monster's picture and health bar."

**This is not a UI task. The wire carries no damage event.** `stores/damage.ts`
says so in its first paragraph: there is no "you hit for N" packet anywhere in
this protocol, only `CurrentMonsterHp` on a snapshot, so every number the player
sees today is *inferred* from the difference between two snapshots. That
inference is deliberately conservative — it refuses a reading when the monster
changed, when health went up, or when more than 3000 ms passed — and it cannot
produce the lines being asked for:

| Line the player wants | Why an HP delta cannot say it |
|---|---|
| "You hit for 412" | Two swings inside one snapshot are one delta, and at the measured 1637 ms cadence that is common |
| "Miss" / "it dodged" | A miss moves no HP at all, so it is invisible by construction |
| "Critical" | On the wire (`LastHitWasCrit`) but as *last-hit* state, not per event — it cannot label the right line once two hits collapse into one delta |
| "The mouse hits you for 8" | Player HP delta, which lifesteal, regeneration and eating also move |
| "Lifesteal +4" | Applied server-side (`SimulationEngine` ~5625, capped at 1% of the bar per hit) and only ever visible as HP going up |

So the log needs a **real event feed from the server**: the tick already
resolves every one of these and then throws the detail away. That is the same
"the output side was never wired" shape as the rest of this file, and it makes
the log a *protocol* change — see the `add-command` skill, and remember the wire
is generated (`npm run generate:protocol`), never hand-written.

**Name only mechanics that exist.** The report says "blocked"; there is no block
stat. What `CombatDamageModel` resolves per swing is a **hit/miss** roll
(accuracy against the monster's dodge, clamped to 5-95%), a **crit** roll,
**armour mitigation** (`raw * K / (K + armour)` — a reduction, never a
subtraction, with a 1 HP floor), the **codex** multiplier and the **set fire**
bonus. A line reading "Blocked" for what was really 60% armour mitigation
teaches the player a stat the game does not have. Lifesteal is real, and is the
`lifesteal_pct` weapon affix.

### Constraints on the log

- **Bounded.** At ~1.6 s per swing an idle session produces thousands of lines.
  A ring buffer of the last N events — it is a log, not a ledger.
- **It sits under the monster card**, where the report puts it, which is exactly
  the real estate `check:clipping` and `check:overlap` walk at 390 px.
- **Sized on the wire.** A per-tick event array would be the first
  variable-length thing this packet carries: cap the events per tick, and read
  the size notes at the top of `StateUpdatePacket.cs` before adding fields.
- "Who did how much" only has more than one attacker in world boss and guild
  content. Do not build a party system to satisfy a column.
- The same feed answers the asymmetry above for free: if the server states each
  hit, the monster bar stops being the only evidence that one happened.

### Done when

- The **asymmetry is explained** — a written cause for why the player's bar
  moved and the monster's did not, not just a change that makes it move.
- Monster HP is observably animating in a real fight, verified the way
  `interpolation.ts` was: a MutationObserver on the bar against the live
  cadence, not by eye.
- A monster death is visually distinct from a monster being swapped out.
- The fight log is fed by a **server event**, not by re-inferring from
  `CurrentMonsterHp`, and a **miss** — which moves no HP — appears in it.
- The log names only mechanics the server actually resolves; no "block".
- `npm run exercise` still green; any new effect is checked at 390 px with
  `check:clipping` and `check:overlap`.

### Risk

Low to change, **medium to scope**. The trap is spending the effort on new
animation and never answering (1) — the player would still be looking at a
static monster bar, now with more going on around it.

The log raises this to **medium**: it is a packet change, and this repo's
dominant bug class is drift between two copies of one truth. Generated wire
only.

### Order within the task

The diagnosis (1-4 above) is cheap and comes first. The log is the largest
piece and is worth doing before the animation polish, because it is the half
that is *missing* rather than the half that is *invisible* — and it makes the
diagnosis observable in the client instead of in a MutationObserver.

---

## OPEN — 9. Rarity barely does anything, and the numbers agree

**Reported 2026-09-04, by the player:**

> "There isn't much difference between them, and the biggest difference is in
> tiers and not the 14 rarities. I think we should boost that."

**Checked in the source, and the instinct is right — more right than reported.**
This is not a feel problem, it is arithmetic.

### THE MONSTER BUFF, applied 2026-09-05: option 2, and it landed neutral

**Decided with the player: keep BOTH kill time and levelling pace.** Monster
health rises per region; XP and gold are deliberately left where they are.

The other two options were rejected on arithmetic, and one of them was rejected
on arithmetic that had been stated wrongly the first time:

| | kill time | XP per kill | **XP per second** |
|---|---|---|---|
| no buff at all | /1.9 | unchanged | **x1.9** |
| raise health, let XP follow | unchanged | x1.9 | **x1.9** |
| raise monster ATTACK instead | /1.9 | unchanged | **x1.9** |
| **raise health, XP left alone** | **unchanged** | **unchanged** | **x1.0** |

Raising attack does not touch kill time or the reward, so levelling runs away
just as fast as with no buff at all - it only adds danger. And raising health
*with* XP following is the worst of the three: the fight looks identical to
today and the only thing that changes is that more XP falls out of it.

**A second reason, which is arguably the stronger one.** Task 8 established that
combat is unreadable *because kills are shorter than the snapshot interval* - a
Field Mouse dies between two samples, so its health bar has nothing to draw.
Making a geared player 1.9x stronger would have pushed monsters that are
currently just barely observable into the same hole. The health buff is what
gives the fight its length back; neither of the other options does.

### What was applied

`server/GameData/monsters.json`, `MaxHp` only, on the 25 canonical monsters:

| region | player power inflation | health buff | kill time | **XP/sec** |
|---|---|---|---|---|
| 1 | 1.225x | **1.00x, untouched** | 0.817x | **1.225x** |
| 2 | 1.357x | 1.36x | 1.002x | **0.998x** |
| 3 | 1.472x | 1.47x | 0.999x | **1.001x** |
| 4 | 1.580x | 1.58x | 1.000x | **1.000x** |
| 5 | 1.696x | 1.70x | 1.002x | **0.998x** |

**Regions 2-5 land inside two tenths of a percent of neutral on both kill time
and XP rate.** That is the claim the whole exercise was for, and it is printed
by `ItemRarityPowerTests.WhatTheMonsterBuffActuallyRestored_KillTimeAndXpRate`
rather than asserted in prose.

**Region 1 is unbuffed on purpose.** A brand-new player has seen about one drop
and is 1.07x stronger, so a buff there would be a straight nerf to the people
least able to absorb it - and this game has already shipped a closed entrance
once, where a new account that followed onboarding's own first instruction died
to the first monster and the tutorial never moved again. A veteran farming
region 1 now finds it 22% quicker, which is what a tutorial region should do.

### Two things that went wrong on the way, both caught by tests

1. **The first table (1.00/1.30/1.50/1.70/1.90) broke the attention span.**
   `Test_Content_EveryMonsterDiesInsideTheAttentionSpan` models a player ON
   ARRIVAL in a region - no affixes, no set bonuses - and refuses a regular
   monster that takes over 180s for them. Death Knight came out at 195s and
   Malakor at 970s against a 900s ceiling. The buff is therefore capped by the
   arrival case, not by the inflation alone.
2. **The second table overshot region 4** to 0.929x XP rate - the compensation
   taking back more than the rework gave. The buffs are now matched to the
   measured inflation per region rather than picked, and the test asserts no
   region ends up slower than it was.

### The invariant that had to move, and what replaced it

`XP = MaxHp / 5` and `gold = MaxHp / 20` were exact across the whole ladder and
asserted in two places. Raising health without raising XP breaks that by
construction - which was the known cost of this option.

What replaced it is a rule that says the thing the old one was actually for:
**within a region, every monster pays the same rate**, so no monster is a
strictly better grind than its neighbours. Stated as a ratio rather than as a
table of multipliers, so a future rebalance that rescales a region uniformly
keeps passing while a hand edit to one monster still fails. Region 1 keeps the
original exact relation and has its own test saying so.

`ItemRarityPowerTests.AppliedMonsterBuff` reads the multiplier back **out of the
content** - `buff = MaxHp / (5 * BaseXpReward)` - rather than restating the
table, so the test cannot drift from the file it describes.

### Verification

576/576 server tests, 302/302 client, 114/114 `npm run exercise`, 0 clipping,
0 overlaps.

---

### PHASE F RESULT, 2026-09-04: rarity is worth one region step

**Decided with the player: a full rarity ladder should be worth the same as one
region step, 1:1.** The alternative on the table was 2.24x / 2.24x - meeting in
the middle by pulling the region curve down as well as pushing rarity up - and
it was dropped because it reaches the *same ratio* while deflating a region-5
player to 31% of today's power, which would have meant retuning the entire
monster ladder for no change in the thing actually being fixed.

| | before | after |
|---|---|---|
| whole 14-tier rarity ladder, one region | 1.48x | **3.00x** |
| one region step (base attack x3) | 3.00x | **3.00x, untouched** |
| region 1 -> 5, same tier | 81.2x | **81.2x, untouched** |
| ratio rarity : region | 0.49 | **1.00** |

A Transcendent from one region below still loses to a Normal from two regions
above, because 3.00 < 9.00. Rarity became an axis of progression without
overtaking playing the game.

### What changed, three levers

1. **`RarityTier.PowerMultiplier(tier)`** - quality tier now scales an item's
   authored `FlatAttackPower` / `FlatDefenseRating`, smoothly across all
   fourteen tiers at `2.12^((tier-1)/13)`. Applied at the single place base
   power is read, `EquipmentSlotEngine`, so there is one authority.
2. **`AffixRegistry.RollAffixRarity(int itemRarityTier)`** - the dead parameter
   is wired. Best-of-N rather than a second weight table: a higher tier gets
   more attempts at each affix's rarity and keeps the best, so it can never
   produce a magnitude the base table could not - it just stops handing you the
   bottom of it. The extra attempts are fractional, so all fourteen tiers
   differ.
3. **Nothing else.** The region curve, the drop weights, `GetAffixCount` and
   the monster tables are untouched.

**2.12 was tuned, not guessed.** The first estimate of 2.53 overshot to 3.43x
because wiring the affix-rarity bias contributed more than the estimate
budgeted for. `ItemRarityPowerTests` prints the resulting ladder and fails
outside 2.85-3.15, so the constant and the table cannot separate.

**No two adjacent tiers are identical any more.** Nine of thirteen pairs were,
including Godly and Transcendent. Every step is now worth at least 7.4%.

### THE MONSTER BUFF IS STILL OWED, and the suite cannot tell you so

**574/574 server tests pass, including `ProgressionRateTests`,
`MonsterLadderTests` and `GatheringShareTests` - and that is NOT evidence of
neutrality.** They pass because `ProgressionRateTests` simulates a brand-new
character with **no equipment at all**, which is precisely the case this change
does not touch. Read that before quoting the green suite as proof.

The real effect is measured by
`ItemRarityPowerTests.HowMuchStrongerThePlayerGets_WhichIsExactlyTheMonsterBuffOwed`,
which models what a player actually wears - the best of N drops for a slot, not
a random one - at region 3:

| drops seen for the slot | typical tier | power before | after | inflation |
|---|---|---|---|---|
| 1 | 1.9 | 166.3 | 178.8 | **1.075x** |
| 10 | 4.3 | 176.1 | 225.3 | 1.279x |
| 50 | 6.1 | 185.1 | 267.8 | 1.447x |
| 200 | 7.5 | 193.2 | 307.0 | 1.589x |
| 1000 | 8.9 | 199.6 | 347.8 | 1.742x |
| 5000 | 10.3 | 208.5 | 396.5 | **1.902x** |

**The inflation is progressive, not flat.** A brand-new player is 1.07x
stronger - effectively unchanged, which is deliberate: the curve is anchored at
tier 1 so nothing anyone already owns got weaker, and the entrance to the game
stays exactly where it was. An established player is up to 1.9x stronger, and
that IS the feature - it is what "rarity matters" means.

So a single flat monster buff is wrong by construction. Region is the natural
proxy for drops seen, so the buff should scale with it: near nothing at region
1, approaching 1.9x at region 5.

### The decision the buff needs, which phase F deliberately did NOT take

Monster health, XP and gold are locked together in content: **`XP = MaxHp / 5`
and `gold = MaxHp / 20`, asserted in two places** (`MonsterLadderTests` line
167-168 and `HardenedEngineIntegrationTests` line 7938). So raising monster
health raises XP with it, and a geared player would level FASTER, not the same.

Three ways out, and they preserve different things:

1. **Keep kill time constant.** Raise monster HP by the inflation. XP follows,
   levelling accelerates by up to 1.9x - the measured ~13h to level 100 becomes
   ~7h for a geared player.
2. **Keep time-to-level-100 constant.** Raise monster HP and break the
   `XP = MaxHp/5` invariant, updating both assertions to the new relationship.
   Kill time is preserved AND pacing is preserved; the cost is that a clean
   content rule becomes a rule with a per-region constant.
3. **Buff monster ATTACK instead of health.** Fights get more dangerous rather
   than longer, XP and gold are untouched by construction, and levelling keeps
   its measured pace. The player's defensive gear also inflated by the same
   amount, so the buff is real rather than cancelled - but it changes what the
   game asks of a player from patience to survivability.

**Option 2 is the one that matches what was asked for** ("keep the output
roughly the same"), and it is the only one that preserves both kill time and
pacing. It needs its own before/after table, which is why it is a separate
change and not folded in here.

---

### PHASE E RESULT, measured 2026-09-04: the baseline, before anything changes

`ItemRarityPowerTests` prints these and pins them. It asserts almost nothing on
purpose - two of its assertions are *characterisations* that phase F is expected
to break, and the failure messages say so.

**The player's complaint, stated as three numbers:**

| what moves | how much it is worth |
|---|---|
| the **whole 14-tier rarity ladder**, at one region | **1.48x** |
| **one region step** (authored base attack x3) | **3.00x** |
| region 1 to region 5, same tier | **81.18x** |

**Going from the worst item in the game to the best one is worth less than half
of a single region step.** That is "the biggest difference is in tiers and not
the 14 rarities", measured rather than felt.

**Why it is that flat.** A weapon's legal affixes are almost all `Percentage`
law, and `CalculateMagnitude`'s percentage branch reads **neither the region nor
the 1.6^ rarity multiplier** - only the affix rarity index, linearly. So an
item's quality tier moves exactly one thing: how many of those it rolls, 1 to 5,
each worth a few percent. Expected uplift runs 2.4% at tier 1 to 12.0% at tier
14, at every region.

**Expected weapon power, region 1** (base attack 12) and **region 5** (base 972):

| tier | affixes | region 1 effective | vs tier 1 | region 5 effective | vs tier 1 |
|---|---|---|---|---|---|
| 1 Normal | 1 | 13.5 | 1.000x | 1093.7 | 1.000x |
| 4 Rare | 2 | 15.0 | 1.113x | 1219.4 | 1.115x |
| 7 Legendary | 3 | 16.6 | 1.230x | 1352.4 | 1.236x |
| 10 Ancient | 4 | 18.2 | 1.351x | 1484.9 | 1.358x |
| 14 Transcendent | 5 | 19.9 | 1.476x | 1625.9 | 1.487x |

The ladder has the same shape at both ends of the game, which is the one piece
of good news here: whatever replaces it does not have to undo a region-dependent
distortion first.

**And the drop table makes it worse than the table above suggests.** At zero
loot luck:

| tier | share | cumulative |
|---|---|---|
| 1 Normal | 50.85% | 50.85% |
| 2 Common | 25.42% | 76.27% |
| 3 Uncommon | 12.71% | **88.98%** |
| 7 Legendary | 0.51% | 99.66% |
| 14 Transcendent | 0.0001% | 100% |

**The median drop is Tier 1** - the lowest tier in the game - and 89% of drops
are in the bottom three, which `GetAffixCount` treats as one. So the tiers a
player actually meets are overwhelmingly the ones that are mechanically
identical.

A player does not *wear* a median drop, though: they wear the best of many. The
neutrality target in phase F is therefore the median of what is EQUIPPED, and
this table is an input to that, not the answer.

**The two dead levers, confirmed from outside the code as well as in it:**

- `AffixRegistry.RollAffixes` takes an `itemRarityTier` and never reads it.
  Rolling at tier 1 and tier 14 with the same affix count produces the same
  expected magnitude, which is what
  `RollAffixesIgnoresTheItemRarityTierItIsHanded` asserts.
- `RollAffixRarity()` takes no arguments at all: a Transcendent's affixes roll
  from the same 520/280/150/40/10 table as a Normal's.

**Nine of thirteen adjacent tier pairs are mechanically identical:**
Normal==Common, Common==Uncommon, Rare==UltraRare, UltraRare==Epic,
Legendary==Mythic, Mythic==Relic, Ancient==Divine, Divine==Demonic, and
**Godly==Transcendent** - the top of the ladder.

---

### What an item's quality tier actually controls

**One thing: how many affixes it rolls.** And `CombatLootEngine.GetAffixCount`
buckets the fourteen tiers into **five** values:

| Quality tiers | Affixes |
|---|---|
| 1-3 — Normal, Common, Uncommon | 1 |
| 4-6 — Rare, Ultra Rare, Epic | 2 |
| 7-9 — Legendary, Mythic, Relic | 3 |
| 10-12 — Ancient, Divine, Demonic | 4 |
| 13-14 — Godly, Transcendent | 5 |

So **9 of the 14 tiers are mechanically identical to a neighbour.** A Normal
and an Uncommon are the same item with different coloured text. So are Godly
and Transcendent — the top of the ladder, where it should matter most.

### What it does NOT control — the part worth reading

- **Base power.** `FlatAttackPower` / `FlatDefenseRating` come from
  `items.json` per BASE ITEM and are read straight into
  `EquipmentSlotEngine.ComputeEquippedTotalsAsync`. Quality tier contributes
  **zero**. Weapon base attack by region: **12 / 36 / 108 / 324 / 972** — an
  **81x** spread that quality tier plays no part in. That is precisely the
  "the biggest difference is in tiers" the player described.
- **Affix magnitude.** `AffixRegistry.CalculateMagnitude` takes `regionTier`
  and the per-affix `AffixRarity`. Not the item's tier.
- **Affix rarity odds.** `RollAffixRarity()` takes **no arguments** and rolls a
  fixed weight table. A Transcendent's affixes roll from the same table as a
  Normal's.

**`AffixRegistry.RollAffixes` accepts an `itemRarityTier` parameter and never
references it in the body.** It is a dead parameter — checked, it appears only
in the signature. That is a strong hint the influence was intended and never
wired, which is this codebase's most-repeated defect shape.

### Do not "just multiply the numbers"

`AffixRegistry.CalculateMagnitude` carries a long comment about a previous pass
here: affixes grew *linearly* against gear that grew *geometrically*, so rarity
stopped mattering exactly at depth. Whatever is done must keep affix growth on
the same curve as the gear it sits on, or it re-creates that bug in the other
direction. Read that comment first.

### Candidate directions, cheapest first

1. **Give each of the 14 tiers its own affix count**, or a fractional
   equivalent (e.g. guaranteed count plus a probabilistic extra), so no two
   tiers are identical.
2. **Wire the dead `itemRarityTier`** into `RollAffixRarity` so a higher-tier
   item biases toward better affix *rarities* — magnitude, not just count.
   This is likely the largest felt change per line altered.
3. **Let quality tier scale base power**, e.g. a multiplier on
   `FlatAttackPower`. Biggest impact, biggest balance risk: it interacts with
   the monster ladder and the XP curve, both of which are pinned by tests.

### Added 2026-09-04: hold the player's power curve where it is

> "If we improve how items scale through rarity and not mostly tier — the tier
> would stay similar, or the same — then we may have to make monsters harder,
> because I don't know whether it can be done so the player's damage output, HP,
> armour and so on stay roughly the same while we lower the weight of the item's
> tier and boost the weight of its rarity. That would probably be best, but
> probably hard."

**That is the right framing, and it should be the constraint this task is
measured against, not an afterthought.** Boosting rarity *on top of* what exists
inflates player power, and the monster ladder, the XP curve and the measured
~13 h to level 100 are all pinned against today's power. Inflate it and either
the monsters get retuned to chase it, or the game quietly gets easier.

The redistribution described — take weight OUT of tier, put it INTO rarity, hold
the total — needs no monster retune at all if it holds. Harder to build, much
cheaper to ship, and it does not spend the ladder's credibility.

#### What "hold the total" can and cannot mean

It cannot mean nobody is affected; that would mean nothing changed. Widening the
rarity spread necessarily moves someone. What *can* be held is one honest point
on the distribution:

**The median drop at each region keeps the power it has today.** Below the
median gets slightly weaker, above it stronger, and the ladder still faces the
character it was tuned against. That is measurable and testable, unlike
"roughly the same".

#### The lever exists

Base power is currently the **only** geometric term — weapon base attack runs
12 / 36 / 108 / 324 / 972, x3 per region — and quality tier contributes zero. So
part of that x3 can become a rarity-driven multiplier whose *expected* value
across the drop table is 1.0 at the median: the same average item, a much wider
spread. Tier keeps its shape, exactly as the report asks; it just stops being
the whole story.

Watch the interaction this file already warns about — `CalculateMagnitude`'s
comment on affixes growing linearly against geometric gear. **A rarity
multiplier that is flat across regions re-creates that bug**: it has to ride the
same curve, or rarity stops mattering at depth all over again.

#### Measure it before changing anything

`ProgressionRateTests` and `GatheringShareTests` print their tables; this gets
the same treatment, in this order:

1. Print expected equipped power for the **median** drop at regions 1-5 under
   today's rules. That is the baseline the ladder is tuned to.
2. Apply the redistribution — lower the tier factor, raise the rarity factor.
3. Print the same table. The median row must land inside a **stated tolerance**
   of the baseline; the top and bottom rows are *supposed* to move, and by how
   much is the actual design decision.
4. Only then run `MonsterLadderTests` and `ProgressionRateTests`. If they move,
   the redistribution was not neutral — **fix the factors, do not retune the
   monsters.**

#### If neutrality turns out to be impossible

Then say so in writing and make the monsters harder as a **separate, deliberate
change with its own table**, not as a silent correction folded into the rarity
work. Two balance changes landing as one is how a regression becomes
unattributable. CLAUDE.md's rule stands either way: the ladder may never
descend.

### Done when

- No two adjacent quality tiers are mechanically identical.
- A table is printed showing expected item power at each of the 14 tiers, at
  region 1 and region 5, before and after — the same way `ProgressionRateTests`
  and `GatheringShareTests` print theirs.
- **The median row of that table is within a stated tolerance of its own
  baseline**, so the monster ladder does not have to move.
- `MonsterLadderTests`, `ProgressionRateTests` and `GatheringShareTests` still
  pass, or their movement is explained deliberately.
- If the monsters do end up being retuned, it is a separate change with its own
  table — not folded into this one.

### Risk

**Medium-high.** This is the balance curve, and CLAUDE.md says not to touch it
casually. Option 1 is contained; option 3 is a progression change and needs the
measured tables above before it ships. Under the neutrality constraint option 3
becomes the *main* move rather than the risky one — it is the only lever big
enough to take weight off tier — but only with step 1 measured first.

---

### PHASE G RESULT, 2026-09-05: the design is written down

**`docs/world_boss_design.md`.** It answers the four questions this task
requires, in order, and checks itself against "this is an idle game" before a
line of code exists.

**The shape:** the boss is armoured in three plates and the player chooses which
to strike. One plate is the weak point, seeded server-side per encounter.
Striking an intact plate breaks it - permanently, for every player in the world,
visibly - so the state of the boss when you arrive is a message from everyone
who came before you. Three plates against three attempts is the same puzzle
twice: enough to solve alone, much cheaper with a crowd.

**The security half is worth doing even if the rest never ships.** Today the
client posts `clientPredictedDamage` - a damage figure it computes about itself -
and the only thing between it and the shared health pool is a 100,000,000 clamp.
Under this design the client sends **a plate index, 0-2**, and the server
computes the damage from the equipped totals it already holds. There is no score
to inflate, because nothing the client sends is a quantity.

**Nothing that works gets touched:** the `FOR UPDATE` health pool,
`_playerDamageMap`, the Redis contribution hash, the event window,
`ScaleActiveBossAsync`, `ProcessDefeatedBossAsync` and the ranked rewards, the
larder check and both caps. New state is three plate flags on
`WorldBossSnapshots` and one seeded index that is never sent to a client.

**The three open questions are now decided, and one was decided against the
instinct that wrote it.**

- **Five plates, not three.** Three matched the attempt budget exactly, which
  turned out to be the flaw: with three of each a blind player cannot fail to
  find the weak point, so knowing where it is beats not knowing by only **1.2x**
  and nobody would ever read the board. At five it is **1.67x**. The table is in
  the design doc; this is the second time this week that measuring a balance
  intuition reversed it.
- **A wrong strike is not punished.** The first draft had it do reduced damage.
  It now does full normal damage and breaks the plate - so a player who guesses
  badly loses an upside rather than paying a fine, and there is no reason to wait
  for someone else to strip the armour.
- **No discovery bonus.** Paying only the finder decides a reward by timezone
  rather than skill, in a global game with one shared boss; paying only the later
  strikers punishes whoever created the information. The 3x weak-point multiplier
  applies to every strike that lands on it, the finder's included.
- **Plate state never resets on a health rescale**, only when the boss itself
  resets. Knowledge destroyed by something a player cannot see reads as the game
  lying rather than as a rule.

**Numbers to build against:** 5 plates, 3x on the weak point, 1.0x and a break
elsewhere, 3 attempts unchanged, weak point re-seeded every encounter.

### PHASE H RESULT, 2026-09-05: built, and it found two defects on the way

The design shipped as written. Five plates, one soft, re-seeded per encounter;
a strike on the soft one pays 3x, a strike anywhere else pays in full and breaks
that plate for everyone.

**The security half landed too, and it is the part worth reading.** The client
no longer sends a damage figure at all - `AttackWorldBoss` carries a
`TargetedPlateIndex` (0-4) and the validator now *disconnects* a client that
still posts `ClientPredictedDamage`. The server takes the damage from
`TickStatePayload.CachedEffectiveMilliAttack`, which is the same number the live
tick swings with, extracted into one helper so there is not a second authority
over how hard a character hits.

`ClientPredictedDamage` survives on the packet only because the Guild War shard
attack still uses it. That is its own problem for whenever Guild Wars comes off
the roadmap.

### Two defects found while building it, neither of them in the new code

**1. Spent world boss attempts did not survive a logout.**
`WorldBossAttemptCount` was written in exactly one place - the notification
raised after an attack resolves - and read straight onto the wire. Nothing
loaded it. So a player who spent their attempts, logged out and came back saw
three unspent pips; clicking Attack hit the cap inside `ExecuteAttackAsync`,
which rolls back in silence. The screen only told the truth after they had
wasted a click on it.

Found by the exercise script's own numbers rather than by reading: it reported
an attempt going **"0 -> 2 spent" on a single strike**, which is not a thing one
strike can do. Hydrated at login now, with two tests.

**2. THE BATTLE SESSION CAP WAS INVISIBLE FROM EVERY ANGLE, and this is the
worse one.**

`WorldBossEngine.BattleSessionCapSeconds` is **300**. A player gets five minutes
from their FIRST strike to spend the other two - inside an encounter that runs
for **up to seven days**. After that every attack rolls back with no damage, no
message and no telemetry they will ever see, and the button stays enabled
forever.

**An idle player who strikes once and comes back an hour later is the normal
case in this genre**, and it silently cost them two thirds of their
participation. The screen's own header comment already listed this as one of
three silent rollbacks it existed to explain - and it explained the other two.

`WorldBossSessionEndsEpoch` is on the wire now (789 -> 797 bytes), hydrated at
login and refreshed on every attack. The screen shows a countdown while the
session is open and says outright when it has closed; the button disables
itself. Verified live: an account with **1 of 3 attempts left** and a closed
session now reads "Your battle session has closed" with the button greyed,
where before it read "1 of 3 left" beside a button that did nothing.

**The 300-second value itself is left alone deliberately** - that is a balance
decision, not a bug. But it is worth a look: see the audit notes.

### Verification

585 server tests (9 new in `WorldBossArmourTests`), 306 client tests,
**112/112 `npm run exercise`** including four new world boss checks, 0 clipping
findings, 0 overlaps, `svelte-check` at the four known `GuildOps` errors.

One pre-existing test was made deterministic on the way:
`Test_WorldBoss_AttemptLimitingAndScaling` asserted damage lands exactly as
sent, which a randomly-seeded weak plate would have broken one run in five - a
test that passes four times out of five is worse than one that fails.

---

## OPEN — 10. World boss rework: make the fight a fight

**Requested 2026-09-04:** rework the world boss, possibly with minigames, to
make it more engaging.

### What it is today

A button. `WorldBoss.svelte` renders the boss, a shared HP bar and three
attempt pips; `WorldBossEngine.MaxAttemptsPerEncounter` is **3**. Each attempt
posts one `clientPredictedDamage` figure and the server applies it. There is no
fight — there are three presses of **Attack**, and the only skill expressed is
having stocked the larder beforehand.

Everything around it is real and working: a server-authoritative HP pool shared
across players, per-player damage attribution (`_playerDamageMap`), an event
window, and a defeat path. **The content is fine; the interaction is the gap.**

### Constraints any design must respect

- **The client cannot be trusted with damage.** `clientPredictedDamage` is
  already capped at `MaxClientPredictedDamage` (100,000,000) because it comes
  from the client. Any minigame that turns player *input* into *damage* is an
  exploit surface — the score must be validated or bounded server-side, or the
  minigame decides its own reward.
- **This is an idle game.** The anti-cheat has already banned players for
  clicking too regularly, and the four active skills were removed after being
  measured at "+90% damage for clicking every three seconds" — see
  `SkillTreeRegistry`. A minigame that rewards *reflexes* fights the genre and
  the existing balance philosophy. Prefer decisions over dexterity: a
  weak-point choice, a timing/ordering puzzle, a resource commitment.
- **Three attempts per encounter is a small budget** for anything with a
  learning curve. Either the minigame is short enough to fit three tries, or
  the attempt budget is part of the redesign.

### Done when

- A player can describe what they *did* in the fight, not just that they
  pressed Attack three times.
- No client-supplied score converts to damage without a server-side bound.
- Damage attribution and the shared HP pool still work with more than one
  player attacking (that is what `_playerDamageMap` is for).
- `exercise.mjs` drives the new interaction, not just the Attack button.

### Risk

**Medium.** The server half is sound and should mostly survive. The risk is
scope and genre fit — write the specific design down before building, and check
it against the "this is an idle game" constraint above.

---

## Status at the end of the 2026-09-02 pass

Everything below was worked in one session. Read this table before the task
bodies — several of them still describe the world as it was on 2026-09-01.

| # | Task | State |
|---|---|---|
| 7 | Missing icons | **Done.** Tools reach `ITEM_ICONS` by base id; `sprites.missing.txt` + a budget ratchet; all ten "missing" ores/logs turned out to have real art. |
| 1 | Verify the daily login | **Done.** No defect. Date key is UTC `floor(unix/86400)`; 7 new tests, mutation-checked. |
| 2 | Audio + panel clipping | **Done.** Audio: LFS was shipping 130-byte stubs; production now serves 35 KB of real WAV. Clipping: automated as `npm run check:clipping`, 0 findings across 25 screens × 3 widths. |
| 6 | Wiki | **Done.** Opened in a browser: 15 pages, no console errors. Its core loop taught the old, wrong order and said the *fourth* monster kills an unfed character - fixed. |
| 5 | Tutorial | **Done.** Discovery moments, seen-state, re-openable list, 64 tests, and `exercise.mjs` now drives a real new account. Doing that found the entrance defect below. |
| 4 | Breeding | **Done.** `docs/breeding_model.md`, explaining preview, interlocks, terminology canon. Found and fixed a real server defect. |
| 3 | Delete the chrono bank | **Done, not deployed.** See §8b of `CURRENT_IMPLEMENTATION_STATE.md`. |

**Verification at hand-off:** 537/537 server tests, 294/294 client tests,
**99/99 `npm run exercise`**, 25/25 `smoke:screens` (local and production),
0 clipping findings, 0 overlaps, `svelte-check` at 4 errors — the four pre-existing
`GuildOps.svelte` ones — and 16 warnings. Server builds clean. Nothing
committed, nothing deployed.

---

## The 2026-09-02 evening pass: running the exercise

The item at the top of the list below was "run `npm run exercise`". It had
never been run against this session's work. Running it found four things, and
only the last is a game defect — but it is the worst kind.

### The game's entrance was closed

**A brand-new player who followed onboarding step 1 died and the tutorial never
moved again.** Measured against the live server:

| start | outcome |
|---|---|
| naked, empty larder | dead at **29 s**, Field Mouse still on **264 of its 465 HP** |
| after 60 s of fishing | dead at **65 s**, mouse down to **73** — closer, still a loss |

The character has 100 HP; the mouse deals 8 every 2 s and has more than twice
the health the player can chew through. Tier one blocks in order, so with step 1
unreachable the food advice — step 3 — was never shown to the only people who
needed it.

**The balance was not at fault and was not touched.**
`ProgressionRateTests.TheFirstMonsterTakesAboutSeventyFiveSeconds` passes, and
passes because it hands its simulated character a million bites of food; its own
comment says that without them "the character dies in about thirty seconds",
which is within four seconds of what a real account does. The model was always
right *given food*. **Fixed by reordering tier one to larder → fight → gear**,
which is the true dependency; see `docs/onboarding_steps.md` §2. Guarded by
`tutorial.test.ts` and by the exercise driving a real new account through
fishing and stocking.

### The exercise had been quietly rotting

Three checks failed on a working game, because the script consumed the state its
own later steps needed and so could only ever pass on a fresh fixture:

- **Ancestors "Keep"** — marking is a flag nothing clears, so each run marked one
  more until all 23 read "Kept" and the check failed permanently. Now a round
  trip that asserts both directions and puts the flag back.
- **Doll "Wear"** — the picker lists the piece already worn and sorts it first,
  so the script re-equipped what was on and read no change. Now picks a
  different item.
- **Village / breeding** — "Send on" ate the last unmarried villager that the
  marriage step needed. It now holds the last one back, and an exhausted pool
  is reported honestly: the assertion is that a greyed-out option *states a
  reason*, not that the reason is one I enumerated (the first attempt failed on
  "(both women)").

`DevFixtureSeeder`'s standing villager pool went 2 → 6 per sex: the top-up only
runs on an explicit `--seed-dev`, so two-per-sex lasted about two runs.

### One thing the fixture was hiding

`/api/v1/admin/status` 403s for an ordinary account — correct, but the dev
fixture is an admin so the console error never appeared until a new account
drove the client. Tolerated in the exercise alongside the deliberate 404.

### Task 2b, finished by automating it

Eyeballing 26 screens does not scale and does not run again next month, so the
sweep became a script: **`npm run check:clipping`** walks all 25 screens at
1500 / 900 / 390px and reports content wider than its box in a container that
cannot scroll. It reads **0 findings**.

Getting there needed three refinements, each of which is the difference between
a signal and 700 lines of noise:

- a box with `overflow-x: auto` is a **deliberate scroller**, not a clip — CLAUDE.md
  actually asks for those on wide content;
- `text-overflow: ellipsis` **says** it truncated, with a visible "…". The Chest's
  item list does it 724 times on a phone and is right every time. What is hunted
  is the silent slice;
- SVG reports `clientWidth` in a different coordinate system, so the Skill Tree's
  labels read as overflowing by 91px in a 29px box. Arithmetic, not a defect.

It found one real bug: **Gathering's node rows** hung 9px past their panel at
900px, slicing the Gather button, because `minmax(7rem, …)` + `minmax(5rem, …)`
+ three gaps demand more than a 245px panel has. The floors are `minmax(0, …)`
now, so a `fr` track still takes its proportional share without demanding a
width the panel cannot give.

`overlap-check.mjs` also reads 0 now. Its four findings were all the fixed
ChatDock covering whatever sits in the bottom-right corner at the current
scroll offset — measured reachable by scrolling, so a floating overlay is no
longer counted. `app.css` reserves `padding-bottom` so content at the very END
of a screen, where there is nothing left to scroll, can still clear it.

### The three checkers shared one rotting list

`SCREENS` lived in three files and each copy rotted separately. It is
`scripts/screens.mjs` once now, with the sign-in, the hamburger-aware `go()` and
`assertMatchesNav`, which makes the nav the authority rather than the file.

### What is genuinely left

1. **The 40 legacy `*_crafting_material` entries** — see
   `docs/crafting_material_audit.md`. Classified, deliberately not deleted;
   deleting them is a product decision, not a cleanup.

---

## 7. Missing icons — and a list of everything without art

**DONE, 2026-09-02.** What follows is what was actually true and what changed;
the remaining art backlog is now a generated file rather than a paragraph.

### What was wrong

**The tool art existed.** `client/Assets/Images/SpritesWeb/Tools&Equipment/`
holds `axes/`, `pickaxes/`, `fishing rods/` with per-wood art. All 33 tools
rendered as two-letter initials on the paper doll, the Chest and the Forge
because `generate-sprites.mjs` routed those three directories into
`toolIcons[kind][tier]` only — a matrix reachable through `toolIcon(kind, tier)`
and nowhere else — while `ItemIcon.svelte` asks `itemIcon(baseItemId)`, which
reads `ITEM_ICONS`. Fixed in the generator: the same path is now emitted under
the item's own BaseId as well, and `toolIcon()` is untouched. The BaseId shape
is `<wood>_<axe|pickaxe|fishing_rod>_tool`, and the wood token is **`acacia`
where the art file says "Acatia"** — the one place a slug() of the filename
would silently produce a non-item.

**The generator was reading a stale catalogue.** It parsed
`client/Assets/StreamingAssets/GameData/items.json`, a retired-Unity copy that
is 111 items adrift from `server/GameData/items.json`: it still carries the
whole legacy equipment line and the five `_helper_offhand_base` pieces the
catalogue cut removed, and it lacks `copper_ore` / `iron_ore` / `obsidian_ore` /
`silver_ore` entirely. Now reads the server's. This changed no mapping — the
generated file diffed clean — but every coverage number before this was
measured against a catalogue no player sees.

**Four logs and one ore had art pointed at their dead twin.** `golden_willow_log`,
`golden_acacia_log`, `golden_frostpine_log`, `golden_ebon_log` are the live rare
woodcutting drops (loot table indices 80/82/84/86, `TierMaterials`,
`GuildContributionEngine`). The art named exactly after them —
`Golden Willow log.webp` and friends — was aliased to `whispering_willow_log` /
`ironwood_log` / `glacier_pine_log` / `void_bark_log`, an older duplicate family
that appears in **no** C# file. The alias table now takes a list, and each of
those files maps to both ids: the live one so it renders, the legacy one because
players can still be holding it. `Absidian.webp` likewise now serves the live
`obsidian_ore` as well as `obsidian_ore_crafting_material`.

**One equipment piece was drawn and never shown.** `brawler_pelt.webp` is a
wolf's head worn as a hood; the noun table filed `pelt` under *chest*, where it
collided with `brawler_harness.webp` for the Frost Brawler set's single chest
slot. One overwrote the other and `eq_brawler_pelt_helmet_armor_slot_base` — a
BaseId that names its slot outright — got nothing. `pelt` is a helmet now, and
every one of the 75 equipment pieces has art.

### What shipped

- `ITEM_ICONS` went from **121 to 165** of 330 items; missing art from **209 to
  165**. All 75 equipment pieces now have art.
- `client_web/src/lib/ui/sprites.missing.txt` — generated, sorted, grouped,
  committed. `MISSING_ART_BUDGET` in the generator asserts the count and carries
  a comment saying it may only fall.
- `node scripts/generate-sprites.mjs --check` (npm: `check:sprites`) fails on a
  stale generated file OR on the count rising, and runs in CI beside the
  protocol check.
- `client_web/scripts/draw-placeholder-ores.mjs` draws the five ores that had no
  art at all — `copper_ore`, `iron_ore`, `silver_ore`, `cobalt_ore`,
  `darksteel_ore` — as faceted nuggets into
  `client/Assets/Images/SpritesWeb/Generated/`. Deterministic; Chromium's canvas
  is the WebP encoder because the repo has none and Playwright was already here.
  Delete a placeholder when real art lands: the walk visits `Generated/` before
  `Locations/`, so a hand-drawn file of the same name wins on its own.
- The auto-matcher now prefers an **exact** BaseId filename match over a prefix
  one, which is what lets a file called `copper_ore.webp` land on `copper_ore`
  rather than being thrown out as ambiguous with `copper_ore_crafting_material`.

### The `*_crafting_material` question — answered

**All 50 of them are unreachable.** Not "several are legacy": zero of the 50 can
be obtained or spent. Traced through `_lootSegments`, which is the only thing
that makes a `_lootEntries` index reachable — the nine-ore Mining table at
indices 61-76 and the coal entry at 21 are orphaned, keyed to activity ids
201-205 that moved to 2001-2005 — and through `_recipes`, which was cut to the
30 tool recipes and consumes none of them.

Deleting them is still a product decision and was **not** taken. Two things
argue for care: live inventories hold them (one account was measured with 5,017
`copper_ore_crafting_material`), and `copper_ore_crafting_material`,
`iron_bar_crafting_material` and `silver_bar_crafting_material` currently carry
the only bar/ingot artwork in the game. They stay listed in
`sprites.missing.txt` like anything else; nobody should commission art for them.

### Still open

- 165 items have no art: 46 crafting materials (all legacy, per above), 46
  gathering and profession materials, 3 consumables and 70 uncategorised (boss
  drops, `premium_diamond`, alchemy reagents). Work it by frequency of
  appearance, not alphabetically. The full list is in `sprites.missing.txt`.
- The five generated ores are placeholders, not painted art.

---

## 1. Verify the daily login

**Verification, not construction — it is fully built.**

### What is actually true

`DailyLoginRewardEngine` has rotating weekly gold matrices
(`GoldRewardMatrices`), a day-7 bonus of 100 diamonds
(`PremiumDiamondsOnDay7Completion`), and streak state on
`PlayerRecord.LastLoginTimestamp` / `LoginStreakDays`. `Progression.svelte:142`
renders the seven days with collected / today / upcoming states and explains
that rewards arrive on sign-in rather than being claimed.

**The thing worth checking:** every account in the live database has
`LoginStreakDays = 1`, including a level-74 account played across several weeks.
That is *consistent with* legitimate resets — a missed day sets it back to 1,
and this account was quarantined for most of August — so it is **evidence, not
proof**. It is also exactly what a streak that never advances would look like.

### Scope

1. Settle the ambiguity with a test rather than by staring: drive
   `DailyLoginRewardEngine` across a simulated day boundary and assert the
   streak goes 1 → 2 → 3, resets to 1 after a skipped day, and pays diamonds
   exactly once on day 7.
2. Pin the **date key**. A streak turns on "what day is it", and a UTC-vs-local
   disagreement is the classic way one becomes unwinnable for players in some
   timezones. Whatever the rule is, assert it.
3. Check the reward is idempotent within a day — two sign-ins must not pay
   twice.
4. Only if the test shows a real defect, fix it.

### Done when

- Tests cover advance, reset-after-gap, day-7 diamonds, and same-day
  idempotence.
- The date-key rule is written down where the engine is.
- A real account observed advancing past streak 1, or a defect found and fixed.

### Risk

Low, and it pays real currency, so it is worth being certain.

---

## 2. UI polish and sound

**Split this in two — one half is a live bug, the other is taste.**

### The bug half: production has no audio at all

Eleven real clips exist locally (`client/Assets/Resources/Audio/`, 4 KB–132 KB:
`level_up.wav`, `loot_rare_dropped.wav`, `ui_button_click.wav`, …). They are
tracked in **Git LFS** — and **git-lfs is not installed on the deploy box**, so
what ships is 130-byte pointer stubs. The game is silent in production and
always has been, while sounding fine on a developer machine.

`exercise.mjs` already tolerates missing audio as expected 404s, which is why
nothing has ever complained.

Fix options, in order of preference:
1. Stop shipping audio through LFS — the runtime needs 11 small files, and the
   1 GB of LFS in this repo is source PNGs nothing serves (see the LFS item in
   `NEXT_STEPS_BACKLOG.md`).
2. Install git-lfs on the box and fetch only `client/Assets/Resources/Audio/`.

### The taste half: modernisation

Scope this deliberately rather than as "make it nicer". Candidates observed:
- Panels are visually uniform; nothing signals which is the primary action on a
  screen.
- The buff tier rows were cropped until today — same class of bug likely exists
  in other dense panels. Audit every panel at a **narrow** container width, not
  just a narrow viewport; the panel grid means those are different things.
- Sound design is a separate question from sound *delivery*: decide which
  events deserve audio before adding more clips.

### Done when

- A `level_up.wav` actually plays on the live site.
- Every panel is screenshotted at two container widths with no clipped content.
- Any visual rework is described concretely enough that "done" is checkable.

### Risk

Low for the audio delivery. The polish half is unbounded unless scoped — write
the specific list before starting.

---

## 6. Make the Wiki complete and readable

### What is actually true

`Wiki.svelte` is 435 lines with **eight sections**: Basics & Progression,
Combat & Stats, Skill Tree, Items & Tiers, Map & Regions, Gathering & Crafting,
Genetics & Breeding, Guilds & Social. Three sub-components add real data:
`WikiItemDatabase`, `WikiDropChances` (a luck calculator), `WikiMonsterDrops`
(live drop tables from `/api/v1/monsters/loot`).

The game has **26 screens**. Systems with no Wiki section at all:

- **The Village** — buildings, the Town Hall ceiling, what each upgrade does,
  the tier materials. Nothing. This is the system players most recently could
  not use.
- **Tools** — that they are equipment, that they have eleven slots, what they
  accelerate.
- **The Long Game** — Book of Deeds, Seals, Hall of Ancestors, Inheritance,
  what a season resets and what survives. Four systems, no page.
- **The Market**, **Forge/fusion and affix rerolls**, **World Boss**,
  **Mailbox**, **Chrono/Boosts**, **Daily login**, **Achievements**.

### Scope

1. Write the missing sections, weighted by what a confused player actually
   opens the Wiki for. Village and the Long Game first.
2. Make the data-driven parts do more of the work. `WikiMonsterDrops` already
   reads live drop tables; the same trick suits recipes, village upgrade costs
   and buff tiers, and data that reads itself cannot drift from the game.
3. Readability pass: the sidebar-plus-page layout is sound; the pages are dense
   prose. Tables, per-region breakdowns, and worked examples ("a tier-3 reroll
   costs X, which is about N minutes of region-3 income").
4. Search across all sections, not just the item database.

### Done when

- Every one of the 26 screens is either documented or explicitly listed as not
  needing a page.
- Village, tools and the Long Game have pages.
- At least the village upgrade costs and buff tiers are generated from server
  data rather than retyped.

### Risk

Low, but it is a lot of writing. Content generated from live data is the part
that keeps paying.

---

## 5. Tutorial, hints, and teaching the game

**Larger than it sounds. A tutorial exists but covers almost nothing.**

### What is actually true

`lib/stores/tutorial.ts` (111 lines) and `tutorialSteps.ts` (105 lines)
implement a three-step first session, driven purely off the state packet:

1. **Win a fight** — done when `CurrentLevel >= 2`
2. **Equip a drop**
3. **Stock the larder**

Then `Completed`. The design is good — each step is "a fact on the wire that
means it is done", which is why it needed no bespoke tracking. It simply stops
after three steps.

**Nothing teaches:** gathering, crafting (including the new Craft ×10), tools
and their slots, the village and the Town Hall ceiling, region unlocking via
bosses, the forge, affix rerolls, the market, guilds and buffs, breeding, the
skill tree, deeds and Seals, the Hall of Ancestors, inheritance, the world boss,
or conversations.

### Scope

1. **Extend the existing pattern rather than replacing it.** Keep "a step is a
   predicate over the state packet"; it is testable without a browser and it is
   why the current three work.
2. Add a second tier: **discovery moments**. When a player first unlocks or
   reaches a system, explain that system once. Region-2 unlock, first tool
   crafted, first guild joined, Town Hall available, first child bred.
3. Decide the **teaching surface**: modal, coach-mark on the real control, or a
   dismissible panel. Coach-marks on the real control are the most effective and
   the most work; pick knowingly.
4. Persistence: which steps a player has seen must survive a reload, and a
   season reset must not re-teach everything.
5. Make it skippable and re-openable — an idle game is often replayed by people
   who already know it.

### Done when

- Every major system has a first-encounter explanation.
- Steps are predicates over the state packet, tested in a node runner.
- `exercise.mjs` drives a new account through the whole onboarding chain.
- Seen-state survives reload and behaves sanely across a season reset.

### Risk

Medium, and it is the task most likely to sprawl. Write the full step list
before building any of it.

---

## 4. Breeding: make it understandable

### What is actually true

Breeding is *complete* — see `LONG_GAME_SPEC.md` sections 3 and 5. Two pairing
modes (hero × hero, hero × villager), aptitudes, genetic loci, epic mutations,
a village gene pool with arrivals and recruitment, cooldowns, and a two-tab
Breeding screen. `exercise.mjs` drives it end to end.

The problem is not that it does not work. It is that it is the most
mechanically dense system in the game with the least explanation, and it
interlocks with four others: the Village (the Inn produces the gene pool),
Ancestors (the Hall culls at rollover), Inheritance, and the season reset.

### Scope

1. Write down the player-facing model first, in one page, before touching code:
   what a child inherits, what a villager contributes, what survives a season,
   what is lost. If that page is hard to write, the design is what needs work —
   not the UI.
2. Make the **preview** carry the explanation. The screen already quotes what a
   child would inherit and what it costs; that is the natural teaching moment,
   and expanding it beats a separate help page nobody opens.
3. Surface the interlocks where they bite: on the Village screen say the Inn
   feeds the gene pool; on Ancestors say what the rollover will cull.
4. Name things consistently. "Aptitudes", "loci", "genes", "inheritance" and
   "legacy" are currently distinct concepts with overlapping names.
5. Only then consider mechanical simplification — and if any is proposed,
   measure it against `LONG_GAME_SPEC.md` §7, which argues some of this
   complexity is deliberate.

### Done when

- A one-page model exists and matches the code.
- The Breeding screen explains a child's outcome without leaving the screen.
- Village and Ancestors mention their breeding interlocks.
- Terminology is consistent across screens, Wiki and tooltips.

### Risk

Medium. Mostly explanatory, but touching the mechanics risks the season-long
progression the Long Game is built on. Do the writing first.

---

## 3. Delete the chrono bank

**DONE, 2026-09-02 — but read this first, because four of the claims below
turned out to be wrong.** The full record is §8b of
`CURRENT_IMPLEMENTATION_STATE.md`. In short:

- **"Deleting it is a balance change" — no.** `BankOverflowSeconds` was already
  a no-op; over-cap offline time was already discarded.
- **"One of the few things diamonds and the Store are wired to" — no.** There
  was no diamond price, no gold price and no exchange rate anywhere.
- **"87 server files" / "~700-byte ceiling" / three wire fields** — really 22
  hand-editable source files, a ceiling of 832, and **four** state fields
  (`VisualBankedChronoSeconds` was a second copy of `BankedChronoSeconds`, and
  the two were read by two different screens).
- **`PlayerChronoRegistry` and `SeasonEraEngine` did not exist.** §9 of
  `CURRENT_IMPLEMENTATION_STATE.md` had been wrong for months; corrected.

Outcome: `StateUpdatePacket` 800 → **779**, `ClientCommandPacket` 359 → **339**,
opcode 8 kept but renamed `SetSimulationSpeed` (it was never chrono), opcodes
24/47/48 retired as gaps, and nine accounts compensated 1:1 into
`AccumulatedTimeBankSeconds` by `20260902180220_DeleteChronoBank`.

The original task text follows, for the reasoning it records.

**Much larger than it sounds. Do this last, and only if the answer to "should
this exist" is genuinely no.**

### What is actually true

The chrono bank converts offline overflow into banked seconds a player spends to
accelerate the game. Its surface:

- **87 server files** mention `Chrono`.
- Table `AccountChronoRegistry` / `account_chrono_registry` — 13 live rows —
  holding `BankedChronoSeconds`, `ActiveSpeedMultiplier`,
  `AccelerationTerminationEpoch`, `LastClockSyncEpoch`.
- **Wire fields on `StateUpdatePacket`**: `BankedChronoSeconds`,
  `IsChronoAccelerating`, `ActiveChronoLockExpirationTicks`.
- A `ChronoAccelerationQueue` on `PlayerSessionRegistry`, drained by the tick.
- Client: `Boosts.svelte` (the bank UI), `Store.svelte`, `VictoryCard.svelte`,
  and `activateChronoBoost` in `commands.ts`.
- `OfflineSimulationEngine` pushes overflow time into the bank — so deleting it
  changes what happens to time beyond the 12-hour offline cap.

### Decide before deleting

Deletion is not free and not obviously right:
- What happens to offline time past the cap once the bank is gone? Today it is
  banked rather than discarded. Discarding it is a **balance change**, not a
  cleanup.
- Players hold banked seconds now. Deleting the table destroys a currency they
  earned — the same class of problem as the stranded ores, and it needs the same
  decision.
- It is one of the few things diamonds and the Store are wired to.

### Scope, if the answer is still delete

1. Write down what replaces it for over-cap offline time.
2. Client first — remove the Boosts bank UI and the Store hook, ship, confirm
   nothing else calls it.
3. Then the engine and the queue.
4. Then the wire fields. Removing them **frees space on `StateUpdatePacket`**,
   which is near its ~700-byte ceiling — a real benefit. Both layout-guard
   constants and `npm run generate:protocol` move in the same commit.
5. Migration last: decide compensation for banked seconds, then drop the table.
6. `SeasonEraEngine` and `PlayerChronoRegistry` are already listed as dead code
   in `CURRENT_IMPLEMENTATION_STATE.md` §9 — fold them into the same pass.

### Done when

- No `Chrono` symbol remains outside migration history.
- `StateUpdatePacket` is smaller and the guard says so.
- Over-cap offline time has a documented, deliberate behaviour.
- Existing banked seconds were compensated or explicitly written off.

### Risk

**High.** Touches the wire, the tick, offline progression and the store, and
carries a destructive migration. The single largest task here.

---

## Follow-up, 2026-09-05: the drop report

"It looks like no items are dropping", then "nothing better than Rare from
23,804 Ice Bat kills", then "something broke in an update". Measured rather than
argued: the roll, the clamp, the per-monster tables, the drop chance's whole git
history and the player's own last 91 drops all say the pipeline is intact. The
defect was the loot PANEL - one shared ring buffer whose material volume evicted
every piece of equipment within minutes.

`docs/drop_rates_investigation_2026_09_05.md` has the evidence table and the
odds. Two new server tests print their tables (`RarityRollDistributionTests`,
`EquipmentDropTableTests`), so the same question is answerable next time without
re-deriving anything.

---

## Cross-cutting notes

- **Two of these are already half-built** (tutorial, daily login) and one is
  half-broken in a way nobody could see (audio in production). Check what exists
  before estimating any of them.
- Anything touching the wire — task 3 — costs a layout-guard change and a
  protocol regeneration. Anything that can be REST instead should be.
- The village, ore and equipment-slot traps in the 2026-09-01 handoff are the
  same shape as several of these: a system that renders correctly while doing
  nothing. Prefer `exercise.mjs` assertions over screenshots when closing any of
  these out.
