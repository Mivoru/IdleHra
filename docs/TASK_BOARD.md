# FolkIdle Task Board

> **START HERE (updated 2026-09-26). Tasks 1-35 and 37 are DONE and deployed;
> production runs `main` (1.0.742).** The handoff with context is at the top of
> `docs/architecture/NEXT_STEPS_BACKLOG.md`.
>
> **TODO, in order:**
> 1. **Task 36 Phase 3: deploy and flip.** Phase 2 (the wheel deals damage,
>    weekly payout by damage rank, damage board) is **PR #52**, which has passed
>    `security-review`.
>    1. Merge it.
>    2. Deploy with `FOLKIDLE_BOSS_MINIGAME=practice` kept. The deploy applies
>       the `ArmourDayKey` migration.
>    3. Flip production to `wheel` before the week of Oct 12 or Oct 19.
> 2. **Task 37 Phase 3: measure the Deep, due 2026-10-02.** The owner's PC
>    captures `D:\FolkIdleBackups\deep-phase3-week1.txt` automatically. Copy
>    it into section 37, "Week 1".
> 3. **Watch the weekly boss / one-strike-a-day cadence** (#48) for its first
>    weeks, and decide the open question: a solo player can find the weak
>    plate in 4 days (`docs/world_boss_design.md`, section 3).
> 4. **Task 38 (Guild Wars): parked** until the population nears the floor.
>    Re-measure before starting.
> 5. Backlog follow-ups (not blocking) are in the handoff: the Village "Got it"
>    overlap and the drop-record lows from #25.

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

**Tasks 1-10 are all done.** The table below is kept as the record of what each one
turned out to be. **Tasks 11 and 12 were added 2026-09-09 and are open** - a gold
sink built as a minigame (**DONE - shipped as The Delve**) and the tutorial past
its first ten minutes (**DONE - shipped as tier three, the objective track**).
Both write-ups are at the bottom of this file. **Task 13, added 2026-09-10, is
the mobile app** - four phases, written against what is actually in the repo
rather than what MOBILE.md claims. **Tasks 14-24, added 2026-09-17 from a
GitHub Copilot audit, are ALL DONE and deployed.** 18 is PR #11 and 22 is
PR #12, both merged 2026-09-19. 21's three phases were done 2026-09-23. 24
(the bug 22's test found) was fixed 2026-09-23. The Unity project was retired
in task 34.

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

## DONE — 9. Rarity barely does anything, and the numbers agree

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

## DONE — 10. World boss rework: make the fight a fight

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

# OPEN — 11 and 12, added 2026-09-09

Two requests, both from the same observation: the game has a lot of content, and
neither the *economy* nor the *player* has anywhere to put it. Written against
measured numbers rather than instinct — the income table below is printed by
`GoldSinkAffordabilityTests`, which is where to re-derive it.

---

## DONE 2026-09-09 — 11. Gold has nowhere to go, and the sink should be a game

**Shipped as The Delve.** What landed, against the design below:

- `DelveRegistry` - the rules, no infrastructure. Eight floors asking 20 to 270,
  pinned to the attribute milestone ladder (25/60/120/200/300) so the bottom
  floor sits under the last rung and depth is bought with the BREADTH of a
  sheet. Entry fee 7,000 to 250,000 by region reached, measured at **39-41
  minutes of that region's own income at every tier** and asserted into a band
  by `GoldSinkAffordabilityTests.TheDelveCostsAboutAnEveningPerRegion` - which
  also asserts the share does not collapse across regions, the single defect
  every other sink in that file has.
- `DelveEngine` - off the tick, one Serializable transaction per action, gold
  charged the way `BreedingEngine` charges it and a `ReloadState` afterwards.
  The client's whole vocabulary is start / door N / bank; a tampered request has
  nothing profitable to change, and `ADoorOutsideTheOfferedRangeIsRefusedAndChangesNothing`
  pins it.
- **The weekly ceiling shipped in v1**, at 60 diamonds - about 780 a season
  against a 950-diamond premium pass. Past it a run still pays gold back, so the
  sink keeps working after the tap shuts, and
  `TheWeeklyCeilingCapsTheDiamondsAndPaysGoldForTheRest` asserts the return is
  still less than the fee.
- Fortune got its second home: `DoorRevealChance` is 55% bare, 77% at 100, and
  reaches its 90% cap at about 253 - so a point is worth less the more you hold
  and the reveal rate can never be certain.
- A full clear pays 20 diamonds; banking at floor 5 pays 6.

Verified on the running stack, not just in tests: `exercise.mjs` pays the gate,
asserts the gold left **to the exact fee**, opens a door, asserts the SERVER
resolved it, climbs out, **reloads**, and asserts the run is closed and the gold
is still gone. 138/138, 635/635 server tests, clean at 390/900/1500 px.

Two things the design got wrong and the build corrected:

- `MaxSuccessChance` was 0.92 against a curve that asymptotes at 0.90 - a
  constant that read like a rule and clamped nothing. The ceiling is the curve's
  own asymptote now, and the test asserts it as an equality, which is what found
  it.
- The first diminishing-curve test compared par->x2 against x2->x10 and failed a
  perfectly good curve. Equal doublings are the only honest interval.

### The original design follows.

### Still open

- **The consolation payout is gold**, which is a smaller sink working against a
  bigger one. Materials would be better and were scoped out of v1.
- ~~Nothing teaches the Delve.~~ **CLOSED by task 12** the same day: tier
  three's `try_the_delve` objective fires once a player is holding three times
  the cheapest gate fee. Tier two could never have done it - it fires on
  REACHING a system, and nothing reaches a screen it has not heard of, which is
  the clearest single argument for the tier existing.

## The design as written, 2026-09-09

**Requested 2026-09-09:** a gold sink — "maybe some minigame where we pay the
entrance with gold and have a chance to win diamonds", and it must be
"detailed, about skill and luck, engaging and fun", not simple.

### What is actually true today

`GoldSinkAffordabilityTests` prints both sides. Income first, at a kill every
twenty seconds against the strongest regular of the region:

| Region | Gold / hour |
|---|---|
| 1 | 10,440 |
| 2 | 25,560 |
| 3 | 62,280 |
| 4 | 152,100 |
| 5 | **369,000** |

And every sink the game has, in the same units:

| Sink | Cost | Share of one hour in region 5 |
|---|---|---|
| Affix reroll (r2 → r5) | 2,000 → 10,000 | 2.7% |
| Fusion fee (tier 1 → 10) | 270 → 4,022 | 1.1% |
| Breeding, first child | 500 | 0.1% |
| Village feast | ~275,000 | 74%, but one-off per villager |
| Village upgrades | tiered | one-off |

**Every recurring sink in the game is under 3% of an hour at the top.** Gold is
not a currency there, it is a counter — which is the report. Note this is the
*opposite* of the region-2 problem that produced these tests ("five rerolls took
100,000 gold"), so whatever ships must scale with region rather than sit flat.

### Why a pure casino is the wrong answer

Two hard constraints, both learned here:

1. **The server owns the simulation.** A minigame the client plays and reports a
   score for is a diamond printer — precedent is on record: opcode 39 granted
   diamonds from an unsigned client field. Every decision must be one command,
   and every outcome rolled server-side against state the server holds.
2. **Diamonds are a purchased currency.** They already have four in-game sources
   (chronicle pass, day-7 login streak, and the two `AchievementEngine` paths).
   A fifth with no ceiling converts the gold surplus into free premium currency
   and undercuts the store. The payout needs a **hard periodic ceiling**, not a
   soft rate.

### The proposal: The Delve — push your luck, with your own character

Pay gold at the gate, descend a generated crypt floor by floor, bank or push
after every floor. Dying loses the run; walking out converts what you banked.

- **The entry fee scales with the player's highest unlocked region** — about
  forty minutes of that region's income, so ~5,000 in region 1 and ~250,000 in
  region 5. That makes it the first sink whose weight survives to the end of the
  game.
- **Eight floors, three doors each.** Every door advertises the stat it wants —
  *"a narrow crack"* (Finesse), *"a jammed slab"* (Might), *"a cold draught"*
  (unknown). The unknown door is the luck, and **Fortune reduces how often a
  door is unknown**, which finally gives LCK a second home outside the loot roll.
- **Resolution is a stat check the server rolls** against the character's real
  sheet — the attributes reworked on 2026-09-06 and the gear that now gates
  them. A pass banks embers and opens the next floor; a fail costs one of three
  lantern charges, and zero charges ends the run with nothing.
- **Bank or push.** Leaving converts embers to diamonds at a published rate;
  pushing raises the multiplier on everything already banked. This is where the
  skill lives, and it is the tension Farkle and Greater Rifts both run on: the
  arithmetic is public and the answer still depends on your own sheet.
- **Skill vs luck, stated plainly.** Skill is reading a door against your own
  attributes, knowing when the multiplier stops paying for the charge, and
  building a character that covers more door types. Luck is the generation, the
  rolls, and the rare shrine floor.

### The guard rails, which are not optional

- **A weekly diamond ceiling per account**, on the wire and *shown* — "90 of 150
  earned this week". Past it a run still pays, in materials and gold-back, so
  the sink keeps working while the tap does not.
- **Run state lives in a table**, one row, server-owned. The client sends "I
  choose door 2" and nothing else — never an outcome, a score or a reward.
- **Every rejection must be visible.** Not enough gold, ceiling reached, run
  already in progress. A silent rollback here would present as a dead button,
  which is this server's favourite way to lie.

### Done when

- A run can be entered, played to eight floors and banked entirely through the
  UI, with `exercise.mjs` asserting the world changed: **gold fell by the entry
  fee**, and a banked run **raised the diamond balance** — both re-read after a
  reload, not from the in-session packet.
- The entry fee is measured against income in `GoldSinkAffordabilityTests`, in
  minutes of play, per region, and asserted into a band. A number a test prints
  is not a number a test checks.
- The weekly ceiling is enforced server-side, and a test drives an account past
  it and asserts the payout changes form rather than the request being refused
  in silence.
- No client-supplied value influences a reward. A test posts a tampered command
  and asserts the server ignores it.
- The run survives a checkpoint and a relogin — any wire field for it is
  hydrated at login or on `RuntimeOnlyByDesign` with a reason.

### Risk

**High, and mostly in scope rather than difficulty.** The push-your-luck core is
a few hundred lines and a table; the risk is that it grows a combat model of its
own. It must reuse `StatsCalculator` and the attribute sheet, not a second one.

The second risk is monetisation. Ship the ceiling in the first version, not the
second — a tap is much harder to take away than to never open.

---

## DONE 2026-09-09 — 12. The tutorial teaches the first ten minutes and then stops

**Shipped as tier three, the objective track** (`tutorialObjectives.ts`), on the
same rule the two working tiers use: a predicate over the state packet, pure,
node-testable, and never a stored copy of progress.

Nine objectives, ordered by what is worth doing rather than by what unlocks
first — unplaced attribute points lead, because that is power the player already
owns. Each fires when the player is READY and has not done it, which is the
exact mirror of tier two firing when they have already arrived. `nextObjective`
hands over one at a time and goes quiet when everything due is acknowledged, and
tier one still wins outright, then tier two, then this.

Two of the three gaps are closed with it:

- **The Delve had nothing teaching it at all** — task 11 shipped a whole screen
  that tier two could never announce, because tier two fires on reaching a
  system and nothing reaches a screen it has not heard of. `try_the_delve` is
  the clearest single example of what tier three is for.
- Market, mail and the Forge reroll now each have an objective, all without a
  wire change: `sell_on_the_market` fires on a nearly-full bag,
  `read_your_mail` on the level where payouts start arriving, `reroll_an_affix`
  on owning a Forge and wearing something.

Three things the build found that the design had not:

- **The panel buried controls.** `exercise.mjs` timed out clicking Village's
  "Marry" under the coach, and `check:overlap` then found **eleven** more pairs
  at 390px — "Reroll" under "Got it", the auto-eat input under the panel
  entirely. It had always been a fixed overlay; what changed is that an
  objective stands there indefinitely on a mature account where a discovery
  used to be dismissed and gone. The panel reserves its own measured height in
  the body now AND folds to its title line under 560px.
- **The fold read the viewport once per cue**, so a window narrowed after the
  cue arrived kept an expanded panel — which is precisely what the checker
  does, and it caught it. It is derived from a resize-tracked width now.
- **The in-game Wiki has a coverage ledger** (`SCREEN_COVERAGE`) that fails the
  suite when a screen has no row, and it duly failed on The Delve. That is the
  guard working: the Delve now has a wiki section with its fee table, floor
  ladder, what Fortune buys and the weekly ceiling.

Verified: 16 new node tests (each rule asserted to fire AND not to fire, plus
that every objective points at a real nav key and shares no id with a
discovery), 331 client tests, and `exercise.mjs` works a mature account through
the cue chain one "Got it" at a time until an OBJECTIVE appears, follows it, and
asserts it retires. 136/136, clean at 390/900/1500 px.

### Still open

- **Teaching is still per-device.** The seen-set is `localStorage` keyed by
  player id, so a phone re-teaches everything. Accepted at the time and now
  more visible, because there is more to re-teach.
- **Chat has no objective and no discovery.** Nothing on the packet reflects
  chat at all, and the fetched-fact route used for guild membership has no
  equivalent here.

## The design as written, 2026-09-09

**Requested 2026-09-09:** guide players through the game with pop-ups and hints
— "there is a lot of content and I want the player not to be lost".

### What is actually true today — more than expected

The third of these to turn out half-built. There are **two working tiers**, both
documented in `docs/onboarding_steps.md` and both tested in
`client_web/tests/tutorial.test.ts` and `exercise.mjs`:

- **Tier one**, `tutorialSteps.ts` — three instructions: fill the larder, win a
  fight, wear a drop. Ordered that way because a new player who fought first
  **died to the first monster in the game** and the tutorial stalled forever.
- **Tier two**, `tutorialDiscoveries.ts` — **seventeen** one-shot explanations
  that fire the first time a player reaches a system: gathering, crafting,
  tools, skills, village, region 2, market, forge, town hall, guild, breeding,
  first child, world boss, deeds, ancestors, inheritance, a full backpack.

Both rest on one rule worth keeping: **a moment is a predicate over the state
packet.** Nothing is stored except which explanations have been read, so a
player who unlocked something in a closed tab is still told about it.

So this is not "write a tutorial". It is three specific gaps.

### The gaps

**1. Nothing sequences the game after minute ten.** Tier one ends at three
steps; tier two is *reactive* — it explains a system once you have already found
it and says nothing about one you have not. Between "wear a drop" and the world
boss there is no answer to *what should I do now*, which is the actual report.

*Proposal — tier three, the objective track.* One always-available panel naming
the single highest-value next thing, derived the same way: a predicate over the
packet, in priority order. *"You have 23 unspent attribute points"* → *"Region 2
opens when you beat the region 1 boss"* → *"Your Town Hall caps your village at
level 5"*. Re-derived every frame, so it is never stale, on the rule the two
working tiers already prove.

**2. Four systems are taught by nothing**, and the reasons are recorded in
`docs/onboarding_steps.md` §5: chat, the mailbox, market listing and buying, and
affix rerolls specifically. All four are REST-shaped, which is why they were
skipped, and all four are reachable from the objective track without a wire
change — a fetched fact rather than a packet field, the way `hasGuild` already
is.

**3. Teaching is per-device.** The read-set is `localStorage` keyed by player
id, so signing in on a phone teaches everything again. Accepted at the time; it
becomes worth fixing only once the track lands, because a track that repeats
itself is worse than one that does not exist.

### Done when

- A brand-new account is never without a stated next objective, from
  registration through the first region boss, asserted in `exercise.mjs`'s own
  new-account browser context — **the fixture cannot verify this, by
  construction**: it is level 40, geared and an admin.
- The track's rules are a pure predicate table with a node-runner test per rule,
  matching `tutorial.test.ts`.
- Chat, mail, market and rerolls are each reachable from a stated objective.
- The track never contradicts tier one: while an onboarding step is outstanding,
  it *is* the objective.
- `check:clipping` and `check:overlap` at 390 px — a persistent overlay is
  exactly the shape that has buried controls before.

### Risk

**Low technically, medium in taste.** The failure mode is nagging: a permanent
panel that always wants something is worse than silence. It needs a dismissed
state that lasts, and it must say *why* a thing is worth doing, not just name it.

---

# OPEN — 13. Ship the mobile app

**Requested 2026-09-10:** what is missing for a fully working, complete mobile
app, and a plan to get there.

Written against what is actually in the repo, not against what MOBILE.md says -
two of its claims are already stale, which is noted below and fixed as part of
D5.

## What is already true

More than the docs admit.

- **Capacitor 8.5 is installed and configured.** `capacitor.config.json` is
  real, and both `android/` and `ios/` projects are generated (the Android
  manifest, resources and Java sources exist; `App.xcodeproj` and `CapApp-SPM`
  exist).
- **The three things that differ on native are handled and documented** - the
  server address, HTTPS-therefore-WSS, and token storage moving to Capacitor
  Preferences. `configurationProblem()` detects the first two and says so on the
  login screen rather than hanging.
- **Push is done on the server.** `RegisterPushToken` is opcode 33 with a
  handler in `SimulationEngine`, two validator paths, and
  `PushNotificationTriggerEngine` behind it. Only the client half is missing.
- **Purchases are wired end to end except the vendor SDK.** `lib/net/billing.ts`
  exists, receipts go over REST to `/api/v1/billing/verify-receipt` (which
  checks the signature) and never through opcode 39. `purchaseUnavailableReason()`
  disables the Buy buttons and says why. **MOBILE.md's "in-app purchases: not
  built" is out of date** - what is missing is one adapter.
- **The mobile ergonomics pass landed 2026-09-09:** 221 undersized touch targets
  to 0, enforced by `npm run check:touch`; nothing clipped or buried at 390px;
  `env(safe-area-inset-bottom)` respected; big numbers compacted.
- **App-resume handling landed 2026-09-09** (`lib/net/lifecycle.ts`).

## What is missing, in the order it blocks things

---

### AUDIT PASS, 2026-09-10: four things that were unfinished and did not know it

Written after PHASE B, C and D landed, by looking for what was half-built rather
than by working down a list. All four are done.

**A1a. `GuildMatchmakingEngine` was still unguarded, and the fix did not land
the first time.**

CLAUDE.md named it as the last `StartCron` loop with no catch in its body. It
was, and its `ExecutePairingCycleAsync` acquires the scope, the DbContext and
the transaction OUTSIDE its own try - which is the exact shape that took loot
down for every player on the live server, because that is where a pool
exhaustion throws.

Guarded now, at the loop, with a thirty-second delay inside the catch so a
failure that repeats instantly cannot become a hot loop hammering a database
that is already refusing connections.

*And the count in CLAUDE.md was wrong.* It said "the other eleven StartCron
loops do wrap their bodies, checked 2026-09-09" - a manual audit with a date on
it, which is a guard that expires the moment somebody adds the twelfth. There
are **fourteen**. Seven had never appeared in any audit; all seven turned out to
be guarded, which was luck rather than process. `CronWorkerGuardTests` is that
sentence made mechanical, and it caught something on its first run that no
human review would have: **the guard above had not actually been applied** - the
stale-build hook had blocked the shell command that was supposed to write it,
and the build afterwards passed because the OLD code compiles too.

**A1b. `npm run build` had been broken on every machine for months, and so had
`npm run sync` and `npm run build:android`.**

`build` chained raw `svelte-check`, which exits 1 whenever there is any error -
and this repository has a documented baseline of four. So the documented build
command had never completed, and neither had the two commands MOBILE.md tells
you to run to package the app.

Nobody noticed because production calls `npx vite build` directly and CI
reimplemented the ratchet as a shell block inside `deploy.yml` - one rule,
written twice, in the one place a developer cannot run it.

`client_web/scripts/typecheck-ratchet.mjs` is now the single implementation;
`npm run build` and CI both call it. It prints every error every time rather
than only on failure (a number nobody reads is how the baseline silently turned
over from one 9 into a different 9), says so when the count is BELOW the
baseline so the slack cannot be spent quietly, and treats a svelte-check that
could not run at all as a failure rather than as zero errors.

`npm run build` completes.

**A1c. `exercise.mjs` covered none of the new server round-trips.**

The onboarding seen-set moved to the server in D1, and the unit tests exercise
it against a STUB. A route that silently 404s looks exactly like a working
feature from inside the browser - which is precisely how the push token spent a
day posting into nothing.

Two checks added, inside the brand-new-account context where a silent failure
would hurt most: that the seen-set reaches the server at all, and that the
browser and the server agree about it. 137/137.

**A1d. Two dead `resetForTests` exports** in `storeAdapter.ts` and
`storeRegistration.ts` - test seams nothing used. Removed. The recurring
"computed but never consumed" trap, in its smallest form.

---
### PHASE A — prove it runs on glass

Nothing below this line is worth doing until this is done, because every
estimate after it is a guess. **The web build has never been run on a phone.**

**A1. Make a mobile build possible on a machine without the server. — DONE
2026-09-10.**

`npm run sync` runs `npm run build`, which runs `generate:protocol` - and that
shells out to the C# server's `--dump-protocol`. A Mac doing an iOS build will
not have a working .NET server checked out, and the failure will look like a
Capacitor problem rather than a missing toolchain.

*Done when:* there is a `sync:web` path that builds and syncs from an
already-generated protocol, `generate-protocol.mjs --check` still guards drift
in CI, and MOBILE.md says which to use when.

*What landed:* `build:web` / `sync:web` / `build:android:web` in
`client_web/package.json`, and a `--assume-committed` mode in
`generate-protocol.mjs` that asserts the committed generated file is present
and is really generator output, warns if it differs from the commit, and
contacts nothing. `--check` is untouched and still runs in CI, because drift
can only be detected by asking the server and that is exactly what the machine
running `sync:web` cannot do. `generate:sprites` was CHECKED rather than
assumed: it reads only `client/Assets/Images/SpritesWeb` (WebP, not LFS) and
`server/GameData`, needs no .NET, and `sync:web` keeps it.

*Found on the way, and it matters:* **`npm run build` does not currently
succeed at all**, on any machine, and has not for as long as the four-error
`svelte-check` baseline has existed. `svelte-check` exits 1 on any error, so
`build` - and therefore `sync` and `build:android` - stops before Vite runs.
Production deploys never noticed because they call `npx vite build` directly.
`build:web` therefore does not chain `svelte-check`; type-checking stays
`npm run check`, which CI ratchets. Fixing `build` itself means resolving the
Guild War question, which is a product decision and out of this task's scope.

**A2. One recorded device session, against a written checklist.**

Not "try it and see". The checklist is the deliverable, because the answers feed
straight into B:

- Suspend and resume after 30 seconds, 5 minutes, and 1 hour. Does the game come
  back, and how fast? **This settles the zombie-socket question** that could not
  be reproduced in Chromium - `resumeFromBackground`'s stale check is defensive
  and unproven until a real suspend either triggers it or does not.
- Lock the screen mid-combat. Does offline catch-up credit correctly on return?
- Airplane mode on, then off. What does the player see in between?
- Rotate. Open the keyboard over a text input (chat, market price, guild
  donation) - does the input stay visible?
- Android hardware back button on every screen. Today it will exit the app.
- An hour of play: battery drain, heat, and whether the 10 Hz packet stream is
  survivable on a mid-range phone.

*Done when:* the checklist is answered in writing in MOBILE.md, with the device
and OS version named.

---

### PHASE B — the things that make it an app rather than a bookmark

**B1. Push notifications - the client half. — DONE 2026-09-10.**

The server is finished. What is missing: `@capacitor/push-notifications`,
requesting permission at a moment that earns it (not on first launch), obtaining
the FCM/APNs token, and sending it through opcode 33 - whose field is 64 bytes,
which an APNs token fits and an FCM token may not, so measure before assuming.

*Measured, and it does not fit.* An FCM registration token is around 160 ASCII
characters against a `fixed byte DeviceTokenBytes[64]`, so **Android push could
never have worked through opcode 33** and iOS fitted with nothing to spare.
Worse, `RegisterDeviceAsync` refused anything whose length was not *exactly* 64
and returned silently, so the failure would have been invisible. The token goes
over REST instead - `POST /api/v1/player/push-token` - which is the same answer
the purchase receipt reached for the same reason, and the bound is now a range
(16-512 bytes) pinned by `PushTokenBoundsTests`.

*"The server is finished" was wrong in three places, and none of them would have
announced itself:*

1. **There was no REST route at all.** The client half was written against
   `/api/v1/player/push-token` and the server has 91 routes, none of them that
   one. Every registration would have been a 404 behind a Settings screen
   saying "this device is registered". `HandlePushTokenRegistration` exists now
   and answers 400/401/503/500 rather than dropping a token quietly.
2. **The FCM message was data-only.** No `notification` block, which means FCM
   delivers it to a *running* app and drops it on the floor of a backgrounded
   one - so the single moment the whole feature exists for, a player who is not
   looking at the game, arrived as silence, and there was nothing to tap. The
   send is now `BuildFcmMessageJson`, a pure function, so
   `PushMessageShapeTests` can stand between the trigger and the wire; it
   asserts a displayable title and body, a named destination screen, and
   `android.priority = high` so a dozing phone is woken.
3. **`DeviceTokenRaw` was `HasMaxLength(64)`** - the width of the wire field
   claimed as a property of the model. Now `MaxDeviceTokenBytes`, via an empty
   migration (`WidenPushDeviceTokenColumn`) that exists purely so the snapshot
   change does not ride along inside somebody's next unrelated migration.

*Where the tap goes is decided on the SERVER*, in `DescribeTrigger`, and travels
in `data.screen`. A switch in the client keyed on trigger codes would be that
table written twice in two languages - the shape `KNOWN_AFFIX_IDS` had when ten
of its twelve entries turned out to have drifted.

*Still needed, and it is not code:* a Firebase project. `SendFcmV1Async` returns
without sending unless `FCM_PROJECT_ID`, `FCM_CLIENT_EMAIL` and `FCM_PRIVATE_KEY`
are set. iOS needs more than that - FCM v1 addresses a device by an FCM
registration token issued by the Firebase iOS SDK, and Capacitor hands over the
raw APNs token, which FCM will not accept. Android is complete once the project
exists; iOS is a C-phase job. The `apns` block is declared and commented as
inert.

**B2. Session length. The 24-hour JWT has no refresh token. — DONE 2026-09-10.**

In a browser tab you re-login occasionally. On a phone, "open the app tomorrow"
means logged out, every day, in a game whose entire promise is that it runs while
you are gone.

*The third option was taken*, as the note here said it should be: a long-lived
revocable credential exchanged for short JWTs. `PlayerRefreshToken` is 32 bytes
of CSPRNG output stored as a SHA-256 hash, good for 60 **idle** days, rotated on
every use. `POST /api/v1/auth/refresh` spends one; `POST /api/v1/auth/revoke`
ends one; login and register issue one alongside the JWT.

*Why not simply lengthen `TokenLifetimeSeconds`, which was the cheap option:* a
JWT is a bearer credential this server does not store, so it cannot be revoked.
A stolen 60-day JWT is valid for 60 days and nothing anybody does - not a
password change, not a sign-out, not support - shortens that by a second. A
refresh token is a row. **`TokenLifetimeSeconds` is unchanged at 24 hours**;
nothing about a live session moved.

*Consequences that had to be handled, and are:*

- A password reset now revokes every refresh token on the account. It already
  cleared the remembered `DeviceId` for exactly this reason - somebody resetting
  a password because another person has been in their account would otherwise
  hand them back sixty days of access.
- A replayed (already-spent) token revokes the **whole family**. The two causes -
  a client that lost the reply and retried, and a stolen credential - cannot be
  told apart from the server, and only one of the two readings is defensible.
- The client therefore **discards** a refused token and **keeps** an unreachable
  server's. Getting that backwards would mean a phone in a tunnel signs the
  player out of every device they own on the next launch.
- The login form no longer flashes on a cold start with an expired JWT; there is
  a `restoring` state, because appearing to be signed out and then rescued reads
  as a bug.

*Guards:* `RefreshTokenTests` (13, against a real Postgres) and
`tests/sessionRefresh.test.ts` (11).

*Not done, and deliberately:* nothing lists or names a player's live sessions.
"Sign out my other devices" needs a UI and a device label, and neither is
required to stop the daily logout.

**B3. The Android back button. — DONE 2026-09-10.**

Today it exits the app from any screen. Expected behaviour is: close the open
modal, else go back a screen, else ask before exiting.

*What landed:* `src/lib/net/backButton.ts`, whose decision is a **pure function**
over the layers that are open - so the ordering is testable without a device
(`tests/backButton.test.ts`). The order is the paint order, topmost first: exit
prompt, death card, victory card, offline summary, chat dock, nav, screen
history, map, exit confirmation. Get it wrong and back appears to skip a layer,
closing something behind whatever is covering the screen while the player sees
nothing happen.

*The layers are read individually rather than from a registry*, because every
one of them already keeps its open state somewhere durable and a registry would
be a fourth copy of state that exists - the shape this codebase's worst defects
have taken. `ChatDock`'s openness moved into `stores/chatDock.ts` for this.

*Registering the listener is what disables Capacitor's own exit*, so the handler
owes the player a way out; the confirmation dialog is rendered outside the
signed-in branch because it has to work on the login screen too.

*Not covered, deliberately:* `PlayerProfileModal` (local to two routes) and the
inline equipment picker on Character, which is a disclosure panel in the page
flow with its own Close button - consuming a back press for something that
obscures nothing makes back feel like it missed.

**B4. A no-network state that says something. — DONE 2026-09-10.**

*What landed:* `ConnectionNotice`, replacing a one-line banner that read
"Connection lost - reconnecting (attempt 4)" and nothing else - proportionate to
a browser tab, useless to a phone that loses signal several times an hour. It
says whose fault it is, that the character is still earning while the socket is
down, and what the player can do; it clears itself when the phase returns to
live. `connectionMessage.ts` holds the phrasing as a pure function.

Presentation only: the reconnect loop it reports on is untouched.

---

### PHASE C — store readiness

**C1. App identity. — DONE 2026-09-10.**

*The bundle id is decided: `com.folkidle.game`*, which was the placeholder and
is now the answer. It cannot change after the first store upload on either
platform, so it is written here rather than left as a thing somebody remembers.

*What landed:* `resources/icon.svg` and `resources/splash.svg` in the game's own
palette (charred oak, brass), and `npm run generate:icons`, which rasterises
them into all thirty files Android and iOS want. What shipped before was
Capacitor's placeholder - a blue asterisk on white - which nobody had looked at.

The icon is drawn for the MASK: Android crops an adaptive icon to whatever
shape the launcher fancies and only the middle 66% is guaranteed, so everything
that carries meaning is inside that circle and the corners hold only background.
`ic_launcher_background.xml` went from `#FFFFFF` to `#100D0A` in the same step -
left white, a circular mask draws a white ring around dark artwork.

*Not @capacitor/assets,* which does the same job and pulls in `sharp`, a native
binary that has to match the platform. Playwright is already a devDependency
(five checkers use it) and rasterises an SVG the way a browser would. The
outputs are committed, because the CI device lane runs `cap sync` and then fails
on a dirty tree.

**C2. Signing. — CODE SIDE DONE 2026-09-10. The key itself is yours to make.**

*What landed:* `android/app/build.gradle` now has a `signingConfigs.release`
that reads `keystore.properties` (gitignored) or, failing that, the environment
variables `FOLKIDLE_KEYSTORE_PATH`, `FOLKIDLE_KEYSTORE_PASSWORD`,
`FOLKIDLE_KEY_ALIAS`, `FOLKIDLE_KEY_PASSWORD`. `android/.gitignore` refuses
`keystore.properties`, `*.jks` and `*.keystore`.

It FAILS OPEN: with nothing configured, `signingConfig` stays null and a release
build produces an unsigned apk rather than an error. That keeps every machine
without the key building - which is every machine except one - and an unsigned
artefact announces itself the moment anybody tries to upload it.

*What is still yours, and cannot be anybody else's:*

```
keytool -genkey -v -keystore folkidle-upload.jks -keyalg RSA -keysize 2048 \
        -validity 10000 -alias folkidle
```

**Back it up somewhere that is not the laptop it was made on.** Play signs every
release with this key and it cannot be replaced: lose it and the listing can
never be updated again, by anybody, and the only remedy is a new listing with no
installs and no reviews.

**C3. Store listings and compliance. — MOSTLY DONE 2026-09-10.**

*Screenshots:* `npm run screenshots:store` writes eighteen, at the sizes the
stores actually demand - 1080x1920 for Play, and Apple's 6.7" (1290x2796) and
6.5" (1242x2688), which are the two sets a new submission is rejected without.
Six frames each, ordered as an argument rather than as a tour.

Two things had to be pinned after the first run got them wrong, and both are now
commented in the script: `colorScheme: 'dark'`, because the client follows the
system and Playwright's default light theme produced parchment screenshots
beside a dark app icon; and `locale: 'en-GB'`, because `initLanguage` reads
`navigator.language` and a Czech dev box produced an English UI with three Czech
words in the header.

*It also found a real defect.* The onboarding coach and the chat handle are both
`position: fixed` at the bottom at `z-index: 40`, so DOM order decided and the
dock won - the handle sat on the last two words of every coach instruction on a
phone. `check:overlap` never saw it because it compares CONTROL pairs and the
thing being covered was text. Fixed by lifting the coach to the same 4.25rem
clearance ChatDock already reserves for its own handle.

*Privacy policy and account deletion:* `public/privacy.html` and
`public/delete-account.html`, served as real files by Caddy's static handle
(`try_files {path} /index.html` then `file_server`, so only unmatched paths fall
through to the SPA). Play now requires the deletion route to be reachable
**without the app**, for somebody who has already uninstalled - an in-app path
alone is a rejection. Every claim in the policy was checked against the server
rather than copied from a template.

*The one thing left, and it is thirty seconds of work:* both pages say
`CONTACT_EMAIL`. Replace it before submitting; a reviewer checks, and should.

*Still outstanding and unavoidably manual:* Play's Data Safety form, Apple's
privacy nutrition labels, and an age rating. All three are questionnaires in the
consoles, and the privacy policy is the document to answer them from - the table
under "What is stored" maps to Data Safety's categories almost line for line.

**C4. In-app purchases: the store adapter. — CODE SIDE DONE 2026-09-10, and it
turned out to be much more than an adapter.**

*The receipt path could never have verified a real receipt.*
`ProductionIapReceiptValidator.Validate` checks an envelope of
`{provider, payload, signature}` with an RSA signature over the payload, and NO
STORE PRODUCES THAT. Google hands a client a purchase token, Apple hands it a
transaction id, and neither hands over anything signed with a key this server
holds - a client could not manufacture one either, because signing it needs a
private key a client must never have. The scheme was satisfiable only by the
test that invented it, and nothing noticed because
`/api/v1/billing/verify-receipt` had never been called by any client.

The two REAL verification calls already existed and were wired to nothing -
`VerifyViaGooglePlayDeveloperApiAsync` and
`VerifyViaAppleAppStoreServerApiAsync` - and that file's own comment said a
deployment moving off the JWS scheme "would call these directly from
BillingVerificationEngine.VerifyReceiptAsync". `StoreApiReceiptVerifier` is that
call. Asking the store whether somebody paid is strictly stronger than checking
a signature over bytes the client chose.

*What landed:*

- `cordova-plugin-purchase` (no revenue share beyond the stores' own cut), read
  off the injected global so it never enters the web bundle - the same pattern
  as `push.ts` and `lifecycle.ts`.
- `storeAdapter.ts` implements `billing.ts`'s existing interface and sends
  `{provider, productId, transactionId, purchaseToken}`, base64. It signs
  nothing, and the absence of a signature is the server's discriminator between
  the two schemes.
- `storeRegistration.ts` registers it with the product ids from
  `/api/v1/store/catalog` - the same `IapProductPrices` the server pays out
  against, so the mapping is not written down twice.
- `VerifyReceiptAsync` tries the store path first and falls back to the legacy
  envelope untouched. It FAILS CLOSED: an envelope naming a store with no
  credentials configured is refused rather than handed to a validator that would
  evaluate a missing signature.
- A refusal after the store has charged somebody is LOGGED, because it is the
  one failure in this system that costs a player money.

*Done when* still needs a sandbox purchase on both platforms, which needs the
products created in Play Console and App Store Connect with ids matching
`GameBalanceConfig.json`, and the credentials of C5's shape:
`FOLKIDLE_IAP_GOOGLE_SERVICE_ACCOUNT_PATH`,
`FOLKIDLE_IAP_APPLE_PRIVATE_KEY_PATH`, `FOLKIDLE_IAP_APPLE_KEY_ID`,
`FOLKIDLE_IAP_APPLE_ISSUER_ID`.

**C5. A Firebase project, and the iOS half of push. — PARTLY PREPARED
2026-09-10; the project itself needs a Google account.**

*What landed:* `PushNotificationTriggerEngine` now says at start-up whether it
can send anything, naming the missing variables:

```
Push: NOT CONFIGURED - missing FCM_PROJECT_ID, FCM_CLIENT_EMAIL, FCM_PRIVATE_KEY.
Device tokens will be stored and triggers scheduled, but NOTHING WILL BE SENT.
```

Without that line the failure is perfectly silent - triggers schedule, the queue
drains, no phone rings - and a deployment can believe push works for months.

*Still needed, and nobody else can do it:* a Firebase project and a service
account, then those three variables in the server's environment. iOS needs
strictly more; see the original note below.

iOS needs strictly more, and it is the reason B1 stopped where it did: FCM v1
addresses a device by an **FCM registration token** issued by the Firebase iOS
SDK, and what `@capacitor/push-notifications` hands over on iOS is the raw APNs
token. FCM will not accept it. Either add the Firebase iOS SDK to the native
project so Capacitor's `registration` event carries an FCM token, or send to
APNs directly and give `SendFcmV1Async` a sibling. The `apns` block on the
message is already declared and commented as inert.

*Done when:* a real Android device receives a notification from
`PushNotificationTriggerEngine` on a locked screen, tapping it opens the screen
named in `data.screen`, and iOS either does the same or is deliberately shipped
without push and says so in the store listing.

---

### PHASE D — the mobile quality bar

**D1. Teaching is per-device. — DONE 2026-09-10.**

The onboarding seen-set was `localStorage` keyed by player id, so a phone
re-taught everything a browser already taught. It was accepted deliberately when
the cost was one wire field; with three tiers and twenty-six explanations it is
the difference between a returning player and an annoyed one.

*What landed:* `PlayerRecord.OnboardingSeenIds`, a nullable JSON array, behind
`GET/PUT /api/v1/player/onboarding-seen`. The server is the truth and
`localStorage` is now a cache in front of it - which cannot be removed, because
`adoptPlayer` and the derived cue run on every packet and must answer
synchronously.

*NULL IS NOT AN EMPTY SET,* and the whole design turns on it. Empty-and-present
means "taught nothing yet, teach everything as it arrives". Absent means "never
baselined anywhere", which is the signal to mark everything ALREADY TRUE as
read - the thing that stops seventeen explanations queueing at somebody who has
been playing for weeks. A `defaultValue: '[]'` on the migration would have
buried exactly the player the baseline protects.

*Adoption became two-phase,* and that is a safety property rather than a
detail. The first call attaches the account and asks the server; it can never
baseline, because doing so on the strength of an empty local cache would write
over a returning player's real set. The signal comes on the first call after the
answer arrives.

*The merge is a UNION and never a subtraction* - a set that is behind shows one
explanation twice, a set that over-forgets buries somebody - and a server that
cannot be reached changes nothing at all, so an offline phone behaves exactly as
this did before.

Guarded by six new tests in `tests/tutorial.test.ts`, including the case the
feature exists for: an account with a record on the server and nothing in this
browser.

**D2. Performance on a mid-range device. — MEASURED 2026-09-10, as far as a
desktop honestly can.**

*What landed:* `npm run check:perf`. Chromium's CDP throttles the main thread
4x - the usual stand-in for a mid-range Android phone - and a
`PerformanceObserver` inside the page counts long tasks, total blocking time,
main-thread busy ratio and heap growth over twelve seconds per screen. It
ASSERTS budgets rather than printing numbers, because a measurement a test only
prints is decoration and this repo has the scar.

*First run, throttled 4x:*

| Screen | long tasks | longest | busy | heap |
|---|---|---|---|---|
| Combat (10 Hz stream, interpolating health bar) | 0 | 0ms | 0.0% | +0.0MB |
| Chest (VirtualList, standing still) | 0 | 0ms | 0.0% | +0.0MB |
| Chest, scrolling | 19 | 129ms | - | - |
| Village (densest static layout) | 0 | 0ms | 0.0% | +0.0MB |

So the 10 Hz stream is nearly free even at a quarter speed, and the only place
with any cost is scrolling a windowed list - which is what you would predict and
is now a number rather than a belief. The heap figure is real: `performance
.memory` was checked to exist rather than assumed, and the script reports `n/a`
where it does not, so a missing API cannot masquerade as a passing measurement.

*What this does NOT model, and the script says so:* GPU, memory pressure, radio
wake-ups, battery, and thermal behaviour after twenty minutes. Also the Chest
measured is the FIXTURE's, not the 17,836-row account - `VirtualList` is
supposed to be flat in the row count, and a few hundred rows cannot prove it.

**D3. Background behaviour. — ANSWERED, AND IT WAS THE WRONG ANSWER. Fixed
2026-09-10.**

The question was whether a backgrounded app holds a socket and drains a battery
or shuts down cleanly and relies on offline catch-up, with a guess that "the
last is correct and is probably already what happens".

*It was not.* Nothing closed anything. `lifecycle.ts` listened for the app
coming BACK and had no notion of it going away, so the socket survived until the
OS froze the WebView - minutes on Android - and for those minutes a pocketed
phone went on receiving and decoding a 10 Hz packet stream. The behaviour was
the operating system's to decide and differed per platform.

*What landed:* `connection.suspendForBackground()`, called from both the
`visibilitychange` and Capacitor `appStateChange` paths. It closes the socket
deliberately and does NOT set `closedByUs` - this is a suspension, not a
sign-out, and setting it would make the app return to a permanently dead socket,
which is far worse than the battery it saves.

*NATIVE ONLY, and that is the care taken.* A hidden desktop tab is a legitimate
way to leave an idle game running - the player switched tabs, they did not
leave - and closing there costs the live view and any chat in it for no battery
saved on a machine that is plugged in. The platform separates the two, not the
visibility state, which is identical in both.

Closing is free here because the SIMULATION IS ON THE SERVER: a disconnected
client is one that is not watching, and offline catch-up pays for the gap - the
same mechanism somebody who closes the app entirely already relies on. Five new
tests in `tests/lifecycle.test.ts`, including that a desktop tab is left alone
and that an app-switcher round trip inside the 400ms debounce still reconnects.

**D4. A device lane in CI. — DONE 2026-09-10.**

At minimum, `cap sync` and an Android assemble on every push, so the native
project cannot rot silently the way three separate screen lists once did.

*What landed, in two passes.* First `npx cap sync` plus
`git diff --exit-code -- android ios` as a ratchet: `cap sync` regenerates the
Gradle and SPM glue from the installed plugin set, so a dirty tree means
somebody added or removed a Capacitor plugin without re-syncing. Both platforms
sync on Linux because the iOS project is SPM (`ios/App/CapApp-SPM`), not
CocoaPods - `cap update ios` never shells out to `pod`.

Then the half that was deferred and is now done: a JDK, the Android SDK, a
Gradle cache keyed on the build files, `./gradlew assembleDebug`, and the APK
kept as an artefact for fourteen days. That catches everything `cap sync`
cannot see - a plugin whose minSdk is above ours, two plugins pulling
incompatible AndroidX versions, a manifest merge conflict - and each of those is
otherwise a wall somebody hits on a Friday.

DEBUG rather than release, because a release assemble needs the upload keystore,
which is not a CI secret and should not become one. `app/build.gradle` produces
an unsigned release build when no key is configured, so adding the secret later
is the only change needed.

*Still not proven:* that the app LAUNCHES. That needs an emulator or a device -
A2 - and no amount of compiling substitutes for it. The kept APK is the shortest
path there: it can be installed on a phone by somebody with no toolchain at all.

**D6. `check:touch` is at 5, not 0. — DONE 2026-09-10, and four of the five were
the checker's fault.**

*Measured, not argued.* Scrolling the page moved every "on the bottom edge"
failure except one, and at the true document end nothing was flagged at all. The
rule fired on whatever happened to be sitting at the fold when the page was
measured at `scrollY=0` - three buttons on Chest and one on Wiki that a player
reaches by scrolling, exactly as they already scroll to see them.

*Two fixes to the checker, and both widen what it covers:*

1. **It only ever measured the first viewport.** `rect.top > window.innerHeight`
   skipped everything below the fold, so on a 3147px Wiki it was checking the
   top 844px and reporting as though it had checked the screen. It now measures
   in overlapping passes down the page - which immediately found a real failure
   on Settings that had never been visible.
2. **Crowding is now decided empirically.** `position: fixed/sticky` was tried
   as the test and is not one: the Wiki's sidebar is `sticky` and at 390px the
   layout stacks so it never actually sticks. A control counts as crowding only
   if it sat at the bottom edge in EVERY pass that saw it, across at least two -
   scrolling is what tells pinned apart from coincidental.

*The genuine failures were both `input[type="range"]`,* on Auto-Eat and
Settings, at 32px tall. Ranges sat in the `:not()` list beside checkbox and
radio - which belong there, being fixed 44x44 squares further down - and a
slider is the one control on a phone that is nothing but a thumb target. Given
an explicit 44px height with a drawn track and a 28px thumb, because a minimum
leaves the browser free to render a 32px track inside a 44px box and it
hit-tests what it drew.

**Now 0 across all 26 screens, with the whole page measured rather than the top
of it.**

**D5. Fix MOBILE.md. — DONE 2026-09-10.** Its "Not built yet" section listed
purchases as unbuilt (they are, bar the adapter) and did not mention the touch
checker or the lifecycle handler.

*What landed:* purchases rewritten to say what is actually missing (a
`StoreAdapter` implementation behind the interface already declared in
`billing.ts`, plus the store-side products) and why opcode 39 is refused;
`lifecycle.ts` and `check:touch` added to "What is already done" with a new
"Checking the phone build" section covering all four geometry checkers; "Not
verified" now separates what is answered mechanically from what only a device
can answer, and carries the A2 checklist to be filled in. Also corrected a
third stale claim, this one in the task board above rather than in MOBILE.md:
native token storage is `localStorage`, not Capacitor Preferences - Preferences
is installed but `storedToken()` is synchronous and Preferences is async. The
comment at `src/lib/net/platform.ts:13` still says Preferences and is wrong.

---

## The order, and why

A is first because it is the only step that produces facts; everything after it
is currently an estimate. B is what makes the app worth installing. C is what
makes it publishable and contains the two items with external lead times
(signing, store review) - start C1 and C2 in parallel with B, because they are
waiting-on-other-people work rather than coding work. D is the difference
between shipping it and being glad you did.

**The single highest-value next action is A2** - one hour with the app on a real
phone, against the checklist above. It will answer questions that a week of
reasoning cannot, and it may well reorder everything below it.

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

---

# 14 through 23, added 2026-09-17: a GitHub Copilot audit, verified claim by claim

**Status as of 2026-09-19 (end of session, mid-flight - see
`docs/architecture/NEXT_STEPS_BACKLOG.md`'s 2026-09-19b handoff before
touching any of these three): 14, 15, 16, 17, 19, 20, 23 are DONE and merged
to main.** 18 and 22 were both brainstormed/re-validated and dispatched to
background agents that were **still running, unreviewed, when the session
paused** - do not trust either is correct until reviewed per the 2026-09-19b
handoff's checklist. **21 (the `SimulationEngine.cs` split) was brainstormed
and its plan corrected (a real bug found: Task 3.1 was built to preserve a
bug PR #7 already fixed), but deliberately NOT started** - Phase 1 is
verified ready to dispatch exactly as written, next session's first move if
continuing this line of work. Unity retirement (tracked in
`docs/architecture/NEXT_STEPS_BACKLOG.md`, not numbered here) is scoped but
gated on an explicit human go-ahead since it touches the live prod Docker
build and CI.

17/19/20/23 were each implemented in an isolated worktree (three of them via
parallel agents dispatched at once, since the plans themselves said they were
independent), independently re-verified against a clean branch off `main`
before merging - see PR #8 (20), #9 (17+19, combined because both edit
`GrantVillagePassiveProductionAsync`), #10 (23). A live, unrelated gameplay
bug (a crafting character silently fighting monster 1 every tick) was found
while scoping task 21 and shipped separately as PR #7 - see the CLAUDE.md
trap entry next to `CombatIdentityTests`. All nine implementation plans live
under `docs/superpowers/plans/2026-09-17-*.md`; each numbered task below
links its own.

The owner ran GitHub Copilot's repo-analysis tool and asked for every claim to
be checked against the actual code before anything from it landed here — the
password-reset handoff earlier this same day had just turned out to be stale,
which is exactly the failure mode a AI-written audit can repeat at scale if
trusted uncritically. Four parallel read-only investigations checked every
claim in the six sections of the audit against real files, line numbers, and
where possible real measured values, rather than the audit's own prose.

**One claim was found FALSE, and it is worth stating first because it means
`CLAUDE.md` itself was stale, not just the audit:** the codex DAMAGE
multiplier, described everywhere (including `CLAUDE.md`'s own "A multiplier
with no ceiling" section, now corrected) as "142x and deliberately still
open," was fixed 2026-09-06. `CodexEngine.DamageMultiplierFor` is
`1 + 0.04 * sqrt(levelSum)`, a diminishing curve; the reporting account reads
5.76x today, not 142.8x. No task needed — it's done, the doc just hadn't
caught up.

Everything below this line **was** confirmed against real code, with file:line
evidence. Severity follows the audit's own P0/P1/security-MEDIUM labels where
it gave one; each task says which.

---

## 14. Access tokens survive logout and password reset (security, was P0)

**DONE 2026-09-18.** A per-account `PlayerRecord.CurrentSessionNonce`
(additive migration, `null` = bootstrap/no-op) is now checked against the
JWT's `nonce` claim on every REST request and WebSocket handshake
(`NetworkBroadcastSystem.IsNonceCurrentAsync`, cached the same way
`_accountIdToPlayerIdCache` is). Bumped - and the account's live WebSocket
force-disconnected via `EvictAccountSessionAsync` - on logout
(`HandleAuthRevoke`), password reset completing (`PasswordResetEngine
.CompleteResetAsync`), and a detected refresh-token replay
(`RedeemRefreshTokenAsync`'s `Replayed` branch, which also had to stop
returning `Guid.Empty` for the account it was revoking). Along the way,
`RevokeAllRefreshTokensAsync` was confirmed to still be genuinely dead code
(zero callers) - `CompleteResetAsync`'s inline revoke already covered that
half. Two stale comments claiming the nonce fed the (separate)
`RedisPlayerSessionLock` eviction mechanism were corrected. Full spec:
`docs/superpowers/specs/2026-09-18-session-security-design.md`; plan:
`docs/superpowers/plans/2026-09-18-session-security.md`.

**Confirmed.** `AuthenticationEngine.cs:76` sets `TokenLifetimeSeconds = 86400L`
(24h). `AuthenticationEngine.ValidateJwt` (`:130-178`) checks only the HMAC
signature and `exp` — no session/deny-list lookup. `HandleAuthRevoke`
(`NetworkBroadcastSystem.cs:8196-8224`) calls only `RevokeRefreshTokenAsync`,
which stops future token *refreshes* but does nothing to a still-live access
token. Logout does not touch the access token at all. A stolen bearer token
stays fully usable for up to 24 hours after the player logs out, resets their
password, or reports the theft.

**A cheap fix exists that needs no new subsystem.** The JWT and the refresh
token both already carry a session nonce (grep `SessionNonce` in
`AuthenticationEngine.cs` and wherever `RefreshTokenRow` is defined). Store the
CURRENT nonce per account (a column on `PlayerRecords`, or a small keyed
Redis/DB table) and bump it on logout, password reset, and confirmed
refresh-token-theft detection; have `ValidateJwt` reject any token whose
embedded nonce doesn't match the account's current one. This is one extra
lookup per validated request (cacheable) rather than a full session-store
build-out.

**Done when:** a token issued before a logout/reset/theft-revocation event is
rejected by `ValidateJwt` immediately after that event, not merely blocked
from refreshing. A test that logs in, captures the access token, triggers
logout, and replays the old token against an authenticated endpoint expecting
a 401 is the shape.

**Risk:** medium to implement (touches the hot validation path on every
request), low to get wrong in a way that's dangerous (fail-closed is the safe
failure mode here, same as the rest of this auth stack).

---

## 15. DeviceId is a bearer credential with no proof of possession (security, was P0)

**DONE 2026-09-18, option (b).** Checked against live code rather than this
task's own prose: this game has no password-change/email-change/account-
deletion command at all, so the only two authenticated actions worth gating
today are completing a purchase and linking an OAuth identity - and,
separately and unrelated to this task, `HandleVerifyReceipt` (a THIRD,
REST-reachable purchase path that trusted a client-supplied AccountId with
no signature check at all) was found and removed the same day; see
`docs/architecture/NEXT_STEPS_BACKLOG.md`'s 2026-09-18b handoff. Every JWT
now carries an `m` claim (`"pw"`/`"dev"`), threaded through refresh-token
rotation via a new `PlayerRefreshToken.AuthMethod` column so a refreshed
session keeps the method it was born with. `HandleBillingVerify` and
`HandleOAuthLink` require a `password` field, verified against
`PlayerRecord.PasswordHash`, whenever the session is `"dev"` **and** the
account actually has a password (a pure guest has nothing to step up to) -
`403 {"StepUpRequired":true}` otherwise, never `401`. Caught in review
before shipping: the new password check on `/api/v1/billing/verify` was not
initially covered by `AuthThrottle`, which would have made it an
unthrottled, unlogged, 210,000-iteration-PBKDF2-per-guess password oracle
for anyone holding a stolen DeviceId - fixed. Full credential-lifecycle
rework (DeviceId rotation/revocation) remains a larger follow-up, as noted
below. Spec/plan: same as task 14.

**Confirmed.** `AuthenticationEngine.TryLoginByDeviceIdAsync` (`:666-676`) is a
bare `WHERE DeviceId == deviceId` lookup — no password, no device attestation,
no expiry, no rotation, no revocation path. The client generates it with
`crypto.randomUUID()` (`auth.ts:90-97`, not guessable), so the realistic attack
is anyone who can *read* that `localStorage` value — XSS, a synced browser
profile, a compromised device — not brute force. A comment near
`AuthenticationEngine.cs:814` already says "DeviceId is a convenience
shortcut here, not a security boundary," which is a documented tradeoff, not
an oversight — but it currently backs full silent login with no password,
which is a bigger promise than "convenience shortcut" implies.

**Fix shape:** stop treating the client-persisted UUID as sufficient alone.
Options, cheapest first: (a) require the remembered-device flow to also
re-verify a short-lived secondary signal (e.g. a device-bound refresh token
issued at first login, rotated on each use, rather than the bare DeviceId
column value itself carrying authority); (b) cap what a device-only login can
do — e.g. still require step-up (password) before sensitive actions (email
change, purchase, account deletion) even when auto-logged-in by device.
(b) is far cheaper and may be the pragmatic v1: audit every
`RequireAuthenticated`-gated sensitive command and add a "was this session
established by password or by device-bearer" flag, gating the riskiest
commands on the former.

**Done when:** a device-bearer session cannot perform at least
password-change, email-change, and purchase/redemption commands without a
fresh password check — and that gate has a test. Full credential-lifecycle
rework (rotation, revocation) is a larger follow-up, not required for v1.

**Risk:** medium — this changes the login UX contract (CLAUDE.md's whole
"remembered device" flow), so scope the step-up list with the owner before
building it.

---

## 16. Village upgrade and villager recruitment don't update the live session's gold (economy bug, was P1)

**DONE 2026-09-17, deployed 2026-09-18 (`f7b8cf8`, `6ebe4cd`, `d0c548a`).**
Both paths now mirror `BirthNotification.GoldSpent`; `RecruitAsync` changed
signature to also return `GoldSpent`, which surfaced a caller in
`BreedingTraitsIntegrationTests.cs` the plan's own grep had missed. Covered
by new `HardenedEngineIntegrationTests` cases and an `exercise.mjs` assertion
that the header updates live for a feast.

**Confirmed, and now fully scoped — see the separate implementation plan at
`docs/superpowers/plans/2026-09-17-village-gold-sync.md`.** Same bug shape as
the breeding gold fix from round 1 (`BirthNotification.GoldSpent`,
`PlayerSessionRegistry.cs:235-241`, consumed at `SimulationEngine.cs:816`):
`VillageManagementEngine.cs:460` (`goldRecord.Quantity -= goldCost;`, building
upgrades) and `VillageArrivalEngine.cs:120` (`gold!.Quantity -= cost;`,
recruiting a villager for gold — the "feast" path) both debit the database row
directly and never touch `CurrentGold`/`RedisPendingGoldDelta`, so the header
shows the pre-spend balance until relogin. The recruit path is worse than the
upgrade path: it enqueues **no notification of any kind** back to the live
session today (confirmed at `VillageManagementEngine.cs:195-234`,
`ExecuteRecruitVillagerAsync` — commits and returns, nothing enqueued), so
fixing it needs a new lightweight notification, not just a new field on an
existing one.

**Risk:** low — this is a narrow, well-precedented mirror of an already-shipped
fix. See the plan for exact steps.

---

## 17. Offline village production fails silently on a database error (reliability, was P0)

**DONE, merged 2026-09-19 (PR #9).** The plan it followed is
`docs/superpowers/plans/2026-09-17-audit-fixes-14-17-19-20.md` (Task 2). The
scoping below is kept as the record.

**Confirmed.** `OfflineSimulationEngine.cs:396-399`:
`catch { await transaction.RollbackAsync(); }` — no log line, no failure
counter, nothing. Compare `CombatLootEngine.cs:571-576` and `:680-685`, which
log every failure (`Console.WriteLine($"Loot: ... failed: {ex.Message}")`)
after the "cron worker silent death" fix from 2026-09-06 — this specific path
was never given the same treatment. A player can log in, see their offline
summary, and have village production silently vanish underneath a transient
database error with **no signal anywhere** — not in the log, not in the
summary, not in a counter a dashboard could alert on.

**Fix shape (phase 1, cheap):** add the same log-and-count pattern
`CombatLootEngine` already uses — a `Console.WriteLine` naming the player and
the failure, plus a counter surfaced the way `Loot: tick saw N kills...`
already reports at heartbeat. This alone turns an invisible failure into a
debuggable one. **Phase 2 (durable retry) is task 18** — don't block phase 1
on it.

**Done when:** a forced database failure during
`GrantVillagePassiveProductionAsync` produces a log line naming the player and
the lost production, and a test asserts the log/counter fires (mirroring
whatever `CombatLootEngine`'s existing failure-counter test already asserts).

**Risk:** very low. This is a few lines, additive, no behavior change beyond
visibility.

---

## 18. No durable retry for loot, gathering, or offline production grants (reliability, was P0)

**DONE, PR #11 merged 2026-09-19.** See
`docs/superpowers/plans/2026-09-17-durable-grant-retry.md`, all 4 tasks. A `pending_grants` table stores the already-resolved outcome
(never "redo this roll," since combat loot rolls randomness before the
write that can fail) keyed by `(PlayerId, SourceType, SourceSequence)`,
proven on offline village production first, then wired to gathering and
combat loot (including auto-salvage gold), then a budgeted
`SELECT ... FOR UPDATE SKIP LOCKED` drain worker (`PendingGrantDrainEngine`,
guarded per `CronWorkerGuardTests`, the 15th `StartCron` consumer of the
bounded pool) with exponential backoff to a dead-letter after 10 attempts,
plus a live-session-notify step so an online player sees a landed retry
immediately rather than at next relogin. Full server suite on a fresh
rebase onto current `main`: 886/888, the 2 failures both pre-existing/
environmental (see the PR description). Built by a background agent that
also found and fixed a real defect left by an earlier attempt (the actual
enqueue call had been left commented out behind a
`TEMP-DISABLED-FOR-RED-CHECK` marker) and one false plan assumption
(neither `CommodityRecords` nor `EquipmentInstances` actually has an FK to
`PlayerRecords` in this schema — the poison-row test technique was adjusted
accordingly, see the PR). **Needs the owner's own read before merging** —
this is reliability-critical, DB-schema-changing code that has not had
human eyes on it yet.

**Confirmed missing.** Repo-wide grep for "outbox", "retry_queue", "DeadLetter"
across `server/`: zero matches. `CombatLootEngine`'s per-item try/catch (the
2026-09-06 fix) gives fault *isolation* — the worker survives a bad item — not
fault *recovery*: a failed grant is logged and gone. Same for offline
production (task 17) once it starts logging. A transient Supabase pooler
hiccup (the exact failure mode that caused the original starvation incident)
can permanently cost a player a real reward with nothing to replay it.

**This is the biggest item on this list and needs its own planning pass, not
a paragraph here.** Shape to scope, at minimum: what's the persistence
mechanism (a `PendingGrants` table works with the existing Postgres, no new
infra); what's the idempotency key (player + source event + a monotonic
counter, so a retried grant can't double-apply); what drains it (a
`StartCron` worker, guarded per `CronWorkerGuardTests`' existing convention);
what's the backoff/give-up policy. Loot, gathering, and offline production
all want the same mechanism — build it once, wire it to all three.

**Done when:** a forced database failure during any of the three grant paths
results in the reward being delivered on retry rather than lost, with a test
that kills the DB connection mid-grant and asserts the reward eventually
lands.

**Risk:** high effort, low risk to existing systems if built as a genuinely
separate outbox table rather than woven into the hot paths — plan it as an
addition, not a rewrite.

---

## 19. Village passive production discards overflow with no record (reliability, cheap)

**DONE, merged 2026-09-19 (with task 17, PR #9).** A full warehouse now
reports what offline production discarded. The plan it followed is
`docs/superpowers/plans/2026-09-17-audit-fixes-14-17-19-20.md` (Task 3).
Bigger than "cheap": this is a wire change (`TickStatePayload`/
`StateUpdatePacket` both need the new field, plus `generate:protocol` and
a client display line), not just a backend log line — the plan corrects
the audit's own risk label.

**Confirmed.** `OfflineSimulationEngine.cs:333-334` and `:402-413` clamp
granted production to warehouse capacity twice (once against the theoretical
max, once against live current storage) — correct, prevents unbounded growth
— but the clamped-away amount is never computed or stored anywhere. The
player's offline summary only ever sees what was actually granted; a full
warehouse silently eats the rest, which reads exactly like a broken building
(CLAUDE.md already documents this class of bug: "a system that renders
correctly while doing nothing").

**Fix shape:** compute `requested - granted` at the clamp site, sum it per
resource, and add a "lost to a full warehouse" line to whatever struct the
offline summary is already built from (check what feeds the client's offline
summary screen — likely the same notification path `GrantVillagePassiveProductionAsync`
already populates).

**Done when:** logging in with a full warehouse and pending production shows
the player how much was discarded, not just how much was kept.

**Risk:** very low.

---

## 20. The wire's field-coverage guard only covers one of four state layers (testing infra, was P1)

**DONE, merged 2026-09-19 (PR #8).** The plan it followed is
`docs/superpowers/plans/2026-09-17-audit-fixes-14-17-19-20.md` (Task 4).
`StateUpdatePacketFieldCoverageTests` covers the layers.

**Confirmed, but narrower in scope than the audit's "build a manifest"
framing.** `StateUpdatePacketFieldCoverageTests` is real and mechanical — it
already guards the exact "field is read but never written or written but
never loaded" class of bug this whole board keeps re-finding — but it is
scoped to precisely one hop: payload-to-packet copy coverage. It says nothing
about whether a field also survives a Redis frame, a DB checkpoint, or a REST
cache invalidation key, which are the other three layers CLAUDE.md's own
"A Redis frame is not a checkpoint" trap describes.

**Fix shape: extend the existing pattern, don't build a new one.** The
narrower, honest task is three more coverage tests shaped like the existing
one — one for Redis-frame inclusion, one for checkpoint persistence, one for
REST-cache invalidation-key coverage — each with its own `RuntimeOnlyByDesign`-
style escape hatch for fields that genuinely don't need that layer, exactly
like the existing test already does for the wire.

**Done when:** all four layers have a mechanical coverage test, and adding a
new field to `TickStatePayload` forces a decision on all four rather than just
the wire.

**Risk:** low — this is test-only work, no production code path changes.

---

## 21. `SimulationEngine.cs` is a single 6,650-line file spanning every subsystem (architecture, was P1)

**ALL THREE PHASES DONE, 2026-09-23.** Phase 2 (command dispatch: the
anti-cheat/epoch gate is `Domain/Shared/CommandGate.cs`, pinned by the new
`CommandGateOrderingTests`; 61 commands go through a dispatch table in
`CommandCoordinatorContext.cs` to per-domain `*TickCoordinator`s; only the
challenge response, ChangeActivity, ReloadState and Logout stay inline)
and Phase 3 (`ProcessSubTick`'s crafting/gathering/combat bodies are
`RunCraftingProgressTick`/`RunGatheringTick`/`RunCombatTick`, PR #7's
crafting `return` kept) were built by a background agent and reviewed
independently. The file went from 6,057 to 5,020 lines. Zero existing
test files changed. Review: no added line schedules work, no coordinator
touches `_activePlayers`/`_guildMembersIndex`, `// Modul:` count 408 ->
429, full suite twice clean (only this machine's Python test), and
`npm run exercise` 146/146 against the refactored server (Task 3.4).
Two defects surfaced on the way and were fixed separately: loot workers
no test ever stopped draining the static queues into the wrong database
(PR #15 - the real cause of the "all 20 gatherers got nothing" CI
failure), and an exercise check that found a bred child by a name nine
characters shared.

**Phase 1 (the drain-plane extraction, Tasks 1.1–1.21) is DONE and MERGED,
2026-09-23** — reviewed and merged directly to `main` (no PR; the branch
had gone unreviewed for 4 days after the background agent that built it
finished). Review confirmed both invariants the plan called for: no
coordinator introduces its own scheduling (`Task.Run`/`Timer`/etc — every
extracted drain is still a callee of the tick thread), and none write
directly to `_activePlayers`/`_guildMembersIndex`. Build clean, 875/876
tests pass — the one failure is this machine's broken local Python
install (confirmed identical on pre-merge `main`, unrelated to the
refactor). The worktree and its branch are cleaned up. **Phase 2
(command-dispatch coordinators) and Phase 3 (`ProcessSubTick`'s three
branch bodies) are not started** — per the owner's 2026-09-19 checkpoint
decision, each needs its own go-ahead. See
`docs/superpowers/plans/2026-09-17-simulationengine-split-scoping.md`**
(the scoping document: what the file actually contains, the full
`TickStatePayload` shared-state analysis, three candidate decomposition
options with honest tradeoffs, and documented landmines) **and
`docs/superpowers/plans/2026-09-17-simulationengine-split-plan.md`** (the
46-task executable plan across three phases, decided with the owner
2026-09-17). **A live bug was found and fixed independently while scoping
this** (`ProcessSubTick`'s crafting branch had no `return`, so a crafting
character silently fought monster 1 every tick) — shipped as PR #7,
unrelated to this task, see CLAUDE.md.

**Confirmed exactly.** `wc -l` = 6,650. It contains material touching Guild,
Breeding, WorldBoss, Market, Village, Gathering, and Crafting concerns (280
keyword hits across those seven areas alone), plus persistence, anti-cheat
telemetry, and notification-queue draining. This is not a new observation —
CLAUDE.md's own conventions describe reading it carefully — but nobody has
scoped splitting it.

**This needs its own dedicated planning session, the same as task 18** — a
paragraph here would either be too vague to act on or presumptuous about a
decomposition the owner hasn't weighed in on. The one constraint worth
recording now: the single-writer tick invariant (`SimulationEngine` is the
only thing that mutates `TickStatePayload`) has to survive any split, or every
concurrency guarantee in this codebase's design goes with it. Scope this as
"bounded domain coordinators with explicit state ownership and adapters for
tick mutation, persistence, and notifications" (the audit's own phrasing is
accurate) — not a rewrite.

**Risk:** high if rushed, and explicitly NOT a task to start without a
dedicated brainstorming/spec pass first.

---

## 22. No sustained-load test combining the systems that actually interact in production (testing infra, was P1)

**DONE, PR #12 merged 2026-09-19.** See
`docs/superpowers/plans/2026-09-17-sustained-load-test.md`, both tasks. The
bug its test found is task 24, fixed 2026-09-23. Task 1 extracted a shared `E2ETestHarness` out of
`E2EGameLoopTest.cs`'s two duplicated engine-graph blocks, adding
`CombatLootEngine` to the constructed graph (neither pre-existing E2E test
had ever started it, so neither had ever actually observed a granted item
end to end). Task 2 is `SustainedLoadTests.cs`: 40 concurrent sessions,
half fighting half gathering, against a connection pool bounded to 5,
combined with checkpoint-forcing and market-listing traffic on a
reconnect schedule.

**This PR ships the test DELIBERATELY RED, per the plan's own instruction
to report a real finding rather than patch it green — and it found one.**
Queue drain and checkpoint age (the symptoms this test was originally
built to catch, matching the 2026-09-06 loot-starvation incident) are both
fine. Instead: **all 20 fighter sessions land zero kills** while all 20
gatherer sessions succeed. Root cause, traced to source (NOT fixed here,
out of scope by the plan's own "Not in this plan" section — **this needs
its own follow-up task, see #24 below**): `MarketListItem`/`MarketBuyItem`
(and by the same mechanism guild/crafting/breeding commands) trigger a
`ReloadState`, which replaces the live `TickStatePayload` from the DB.
`StateReloadMerge.CarryLiveOnlyFields` only preserves the command-result
ring buffer across that reload, not `ActiveActivityId` — and the legacy
single-character path (`TargetGuid == Guid.Empty`, still live production
traffic) never persists activity changes to the `characters` row, so any
such player fighting while also touching market/guild/crafting/breeding
screens is silently kicked back to idle. Full suite on a fresh rebase onto
current `main`: 875/877, the only failures the documented
`SustainedLoadTests` finding plus the same pre-existing environmental
issue as PR #11. **Needs the owner's own read before merging** — merging a
red test on purpose is an unusual thing to wave through without a look.

---

## 23. Market (and similar REST+WebSocket screens) can apply a stale REST result over newer WebSocket state (correctness, unconfirmed in the wild)

**Done, 2026-09-19.** Implemented per
`docs/superpowers/plans/2026-09-17-market-stale-rest-race.md`'s Task 1: the
"simplify" direction (route through the existing `CommandResult` ring-buffer
ack), not epoch-stamping. All four engines the plan named as needing a
per-engine coverage check were re-confirmed against live source before
fixing, and all four confirmations held:

- `MarketOrderBookEngine.PlaceLimitOrderAsync` had zero
  `EnqueueCommandResult` calls on any of its 7 exit paths — added all 7.
- `EquipmentSlotEngine.EquipAttemptOutcome.Success` hardcoded `ResultCode`
  to `null` — now reports `Success`, covering Character's equip/unequip.
- `MailboxAndBankEngine.CommitMailClaimAsync`'s `isSuccess` branch never
  enqueued anything — added, covering Mailbox's claim/claimAll.
- `LarderEngine` already enqueued unconditionally on every deposit/
  withdraw — Larder's `setTimeout` was pure dead weight, deleted with no
  server change needed.

`game.ts`'s inline `CommandResult` handling was extracted into an exported
`processCommandResults()`, and the now-redundant `setTimeout` invalidations
in Market/Larder/Character/Mailbox `.svelte` were deleted. Equip/unequip and
mail-claim/claimAll now surface a "Done." toast on success — an accepted,
named UX change, not a regression (`claimAll()` keeps its own internal
command-staggering `setTimeout`, unrelated to cache invalidation).

Tests: 4 new server tests (one per engine) proving the success/rejection
path now enqueues a `CommandResult`; 2 new client tests
(`commandAckInvalidation.test.ts`) proving one ack produces exactly one
refetch with nothing left to race a redundant timer; a source-scan test
(`restInvalidationTiming.test.ts`) pinning all four screens against the
`setTimeout` pattern reappearing. Full client suite: 0 new failures;
`check:ratchet` unchanged at 4 (all pre-existing, `GuildOps.svelte`). Full
server suite: 866/868 passed, the 2 failures pre-existing and unrelated (a
port-8081 conflict in `E2EGameLoopTest`, and a Python content-validator
environment issue) — neither touches any file this fix changed.

---

## 24. A `ReloadState` silently discards the legacy path's live activity, dropping a fighting player back to idle (correctness, found 2026-09-19)

**DONE 2026-09-23.** Fixed in the reload merge, not the flush:
`StateReloadMerge.CarryLiveActivity` carries each slot's in-flight activity
(activity id, progress, monster, monster HP, player HP, halt reason) across
a reload **only while the same character still holds that slot**. Why not
the "more correct" write-back in the flush: `FlushState` (the per-player
path the reload uses) writes no `characters` row at all — only `FlushBatch`
(shutdown) does — and the Hall of Ancestors benches a displaced character
idle *before* its reload, so a flush-first write-back by character id would
overwrite that bench reset with the stale fight. Equipment is not carried
(a reload is how a fresh equip reaches the session). Side effect worth
knowing: a reload is no longer a free full heal.

Proved both ways: with the carry commented out every fighter in
`SustainedLoadTests` has 0 XP; with it, they land kills. Unit tests in
`StateReloadCarryTests` cover same-character, swapped-character, slots 2/3
and equipment.

**The load test also had five defects of its own, hidden behind this
one** — assertion C failed first, so nothing below it had ever run:
1. fighters targeted monster 55, a pre-canon monster whose loot table is
   deliberately empty — C could never pass for them;
2. a bare level-0 character with an empty larder dies to the first canon
   monster (now: monster 91, stocked larder, raised Might);
3. "every fighter got loot" is dice — a kill yields nothing ~55% of the
   time, so with ~4-5 kills it passed about a third of the time. Gatherers
   (deterministic) must all receive materials; fighters must reach at least
   half, which a dead or starved worker (0%) still fails;
4. `gear <= 1` allowed for the seeded listing, but a successful listing
   moves it to escrow, so one equipment drop read as barren — the seed is
   now excluded by id;
5. assertion D compared `CurrentXp` alone, which restarts at every
   level-up — now (level, XP).
Per-session diagnostics (activities, halts, XP, codex kills) now print, so
a failure explains itself. Full suite 892/893 (the one is this machine's
broken Python), the load test green inside the full run.

**Follow-ups found on the way:**
- **DONE 2026-09-23 — the fielded character's activity is durable now.**
  Carrying fixed the reload; a relogin still came back idle (every
  session's post-reconnect packet showed activity 0), and offline catch-up
  simulates the loaded activity, so a legacy-path player earned nothing
  offline. `StateCheckpointManager.PersistFieldedActivityAsync` writes it
  from both `FlushState` and `FlushBatch` — GUARDED: only while the row is
  still the character `LoadPlayerState` would field (first non-escrowed by
  `SlotIndex`, `Id`), so a stale flush after a Hall of Ancestors swap
  writes nothing. `FieldedActivityPersistenceTests` covers the round trip,
  the idle reset and the Hall race (the first two fail with the call
  removed).
- **NOT a defect — the ~60s first kill is the design.** Measured against
  `SecondsToKill` (HardenedEngineIntegrationTests): region-1 arrival is
  modelled as a character with nothing, and the regulars are pinned at
  12-180s on arrival (52s / 82s / 117s / 169s, boss 894s). A starter
  claymore would have made it 30s / 95s / boss 497s; the owner decided
  2026-09-23 to keep the design. Recorded so nobody "fixes" it again.

*Original report follows.*

Found by task 22's sustained-load test (PR #12), not fixed there on purpose
— its plan explicitly scopes out engine fixes.

**Confirmed, traced to source.** `CommandType.MarketListItem`/
`MarketBuyItem` (and, by the same mechanism, guild/crafting/breeding
commands — anything that ends by enqueueing `CommandType.ReloadState`)
replace the live in-memory `TickStatePayload` wholesale with one loaded
fresh from the database (`StateCheckpointManager.LoadPlayerState`).
`StateReloadMerge.CarryLiveOnlyFields` (`server/FolkIdle.Server/Engine/
StateReloadMerge.cs:41-49`) only carries the command-result ring buffer
across that replacement. For the legacy single-character path
(`TargetGuid == Guid.Empty`, confirmed still live production traffic —
`SimulationEngine.cs` around line 2111, `ApplyActivityChangeToPayload`),
`ActiveActivityId` is mutated only in memory and never persisted to the
`characters` row anywhere in `StateCheckpointManager.cs`'s flush path. So
every `ReloadState` silently resets that player to idle — measured via 40
concurrent sessions in PR #12: every fighter who also touched the market
every ~4s got reset to idle before landing a single kill, while gatherers
survived only because one harvest cycle (30 ticks) fit inside the window
before the first reset.

**This is not a load/contention bug** — it reproduces with a single
player and no pool pressure at all. It affects any legacy-path player who
fights while also using market, guild, crafting, or breeding screens —
plausibly a real, currently-live defect, not just a test artifact.

**Fix shape (to scope properly before coding, not a rubber stamp):**
either persist `ActiveActivityId` to the `characters` row on every change
(matching how `AgeTicks`/`AgePhase` are already written back) so a reload
picks up the truth rather than a stale row, or extend
`StateReloadMerge.CarryLiveOnlyFields` to also carry the live
`ActiveActivityId`/combat-progress fields across a reload. The first is
probably more correct (the DB should not lie about what a player is
doing) but touches the checkpoint flush path; the second is narrower but
only patches the symptom for this one field — check whether other
live-only fields have the same gap while in the file.

**Done when:** a player who fights while issuing a `MarketListItem`/
`MarketBuyItem`/guild/crafting/breeding command mid-fight does not lose
their activity, verified by re-enabling/re-running PR #12's
`SustainedLoadTests` assertion C and confirming fighters now land kills.

**Risk:** medium — touches either the checkpoint persistence path or the
reload-merge path, both load-bearing and shared across every screen that
triggers a `ReloadState`.

---

**Not a task — a decision the owner should make, not something to silently
"fix":** offline catch-up is capped at 12 hours per login gap
(`OfflineSimulationEngine.cs:22`, `MaxOfflineSeconds = 43200L`), but the cap is
per-gap, not a rolling daily budget — confirmed by reading
`elapsedSeconds = Math.Min(effectiveMaxOfflineSeconds, rawDeltaSeconds)`
against `rawDeltaSeconds = T_current - T_last_checkpoint`. Logging in every
4 hours six times a day legitimately nets ~24h of simulated production, each
individual gap staying under the cap. Whether that is a real exploit worth
closing (a rolling per-day budget) or acceptable/intended (rewards engagement,
which many idle games do on purpose) is a design call, not a bug — record the
decision here once made, either way.

**DECIDED 2026-09-23 by the owner: keep the offline cap as it is.** The cap is
per gap, and the gaps of a day can never add up to more than the day itself -
six logins four hours apart simulate 24h of production in 24h of real time.
There is nothing to close.

---

# 25 through 38, added 2026-09-23: the owner's phone playtest, cleanup, and three designs

**This is the front of the board.** Written after a few hours of play on the
Android APK, plus the cleanup and decisions from the same day. Every fact below
was checked against the code or the PRODUCTION database (Supabase, read-only)
on 2026-09-23 - the evidence is written into each task so the next session does
not have to re-derive it. Everything before this section is closed.

**Work them in this order.** Phase 1 is player-visible defects, broken-first.
Phase 2 is cheap, riskless cleanup the owner has approved. Phase 3 is design
work: each of those needs a brainstorming pass WITH the owner before any code
(use the brainstorming skill), then a written plan, then implementation.

| Phase | # | Task | Size |
|---|---|---|---|
| 1 | 25 | World boss: no attack has ever landed | M, a real defect |
| 1 | 26 | Rarity: no Ancient+ drop in ~5 days - measure, then decide | M, investigate first |
| 1 | 27 | Loot drops list: the top row is cut off | S |
| 1 | 28 | Village stopwatch: the whole icon spins on the phone | S |
| 1 | 29 | Market filters: the checkboxes are misaligned | S |
| 2 | 30 | Docs match reality | S |
| 2 | 31 | CI: GitHub actions off Node 20 | S |
| 2 | 32 | Branch and worktree cleanup | S |
| 2 | 33 | Delete the 40 legacy crafting materials | M, **done 2026-09-24** |
| 2 | 34 | Retire the Unity project | M, deletion ready, deploy pending |
| 2 | 35 | Small leftovers | S, **done 2026-09-24** |
| 3 | 36 | World boss fight: a skill/reflex minigame, not a plate guess | L, design first |
| 3 | 37 | A late-game gold sink (the Delve tops out at 250k; the owner earns 100M easily) | L, design first |
| 3 | 38 | Guild Wars: design the whole system | XL, design first |

Standing rules for all of them: verify gameplay with `npm run exercise` (and add
a check to it for anything new a player can do); deploy after each merged fix
with the `deploy` skill (the owner approved deploying after every fix on
2026-09-23); CI only runs on push to `main`; two copies of the server suite
cannot run at once on this machine (`SustainedLoadTests` binds port 8095);
`exercise` spends onboarding state, so re-seed with `--seed-dev` between two runs.

## 25. World boss: no attack has ever landed (P0, a real defect)

**DONE, PR #23 merged and deployed 2026-09-24.** Every strike lands or answers with a result code (38-42); the dev window; exercise strikes on any day.

**Reported:** "I can never attack."

**Measured in production 2026-09-23:** the whole Sep 15-22 window ended with
`WorldBossSnapshots` at `CurrentHp = MaxHp = 50,000,000`,
`TotalDamageContributed = 0`, `EventState = 2` (failed), and
`player_world_boss_attempts` **completely empty** - not one attempt was recorded
for any player, although the owner tried. So attacks either never leave the
client or are rolled back on the server before the attempt row commits.

**Windows:** `LiveOpsTickEngine.EvaluateWorldBossEventWindowAsync` opens the
boss on the 1st-7th and 15th-22nd of each month (UTC). On the 8th-14th and
23rd-31st it is dormant by design - **so a repro needs a window forced open
locally** (`WorldBossEngine.ActivateEventWindowAsync`, or the admin dev tools),
not the calendar. The next live window opens Oct 1.

**Where to look, in order:** CLAUDE.md's "Silent rollback is this server's
favourite way to lie" names three silent rollbacks in `ExecuteAttackAsync`
(attempt cap, empty larder, a battle session nothing puts on the wire) - check
which one a real account hits; the client button's own disable conditions on
the world boss screen; that the command reaches the server at all (since task
21 it goes through the dispatch table - `WorldBossTickCoordinator`).
`WorldBossArmourTests` covers the engine but evidently not what a real client
does.

**Done when:** on a locally forced-open window, the dev fixture AND a fresh
account can attack, the damage and the attempt row land, a refusal says why on
screen, and `exercise.mjs` attacks and asserts the boss HP moved (opening the
window itself, and round-tripping what it spends).

### Investigation and fix, 2026-09-24 (branch `fix/world-boss-attack`)

**Status: fixed on the branch, not deployed. It is verified in production only
once a row exists in `player_world_boss_attempts` during the Oct 1-7 window.**

- **Owner's answer:** the Strike button was **grey**, and he tried outside a
  window. **H1 is confirmed.** The production cause was the calendar plus a
  screen that did not say why the button was grey, not a server defect.
- **Production, re-read 2026-09-24 (SELECT only):** unchanged. `EventState 2`,
  `CurrentHp = MaxHp = 50,000,000`, `TotalDamageContributed 0`, 0 attempt rows.
  The Oracle box logs and Redis telemetry were not read (no SSH in this session).
- **`wiring-auditor`:** "chain intact apart from the known list". It found no
  missing link beyond the ones this plan already named.
- **Local repro on a forced window, before any fix:**
  - As the dev fixture at 390 px, a strike **landed**: attempt row 1, HP down
    by 1000.
  - As a fresh account, the button was **grey** with "Your larder is empty", so
    nobody new could ever take part.
  - The server path works. What blocked players was the calendar, the larder
    rule and the screen.
- **Fixed anyway, as the task requires:**
  - Every refusal is now a result code: 38 not active, 39 defeated, 40 no
    attempts, 41 session closed, 42 strike failed.
  - The closed-window and dead-boss races are answered with a code instead of a
    disconnect.
  - A double-tap no longer disconnects. Opcode 32 left the 100 ms rule; the
    test was confirmed red with the old rule in place.
  - `ExecuteAttackAsync`'s scope and transaction moved inside its `try`, and
    `QueueAttack` is a guarded dispatch.
  - The larder rule is dropped, on the owner's decision.
  - The reason for a grey button is shown next to the button.
  - `exercise.mjs` opens its own window through
    `POST /api/v1/dev/worldboss/window`. That route answers 404 unless
    `FOLKIDLE_DEV_TOOLS=1`.
- **New finding, not changed (balance):** `ComputeAppliedDamage` floors every
  strike at 1,000, and a level-40 fixture's own attack is below that. So the
  fixture and a level-1 account both deal exactly 1,000, and **the weak plate's
  3x does nothing** until a character's attack exceeds 1,000 HP. The soft-plate
  strike in `exercise` did 1,000, the same as a broken plate. This belongs to
  task 36 or an owner balance call.
- **To do on Oct 1-2:** run `SELECT count(*) FROM player_world_boss_attempts`
  and the snapshot SELECT, and ask the owner to strike once from the phone.

## 26. Rarity: no Ancient+ drop in ~5 days (investigate before changing anything)

**DONE, PR #25 merged and deployed 2026-09-24.** The bough is elevation; canonical area completion; the drop record and `/api/v1/player/loot-odds`; the Wiki odds line.

**Reported:** Godly and 2x Demonic earlier; for about five days only
Mythic / Relic / Ancient at best.

**Measured in production 2026-09-23** (player 8 "Mivoru", level 94, 1,604
items, `AutoSalvageBelowTier = 0`, `BaseLuck = 300`):
- The newest 800 equipment rows (ids 64216-65015, consecutive, so nothing was
  deleted between them) are all region-5 gear (doom/dread/abyssal/dreadnought),
  tiers 1-9: **9 Legendary+ (1.1%), 0 Ancient+**.
- Older rows: 804 survivors, 621 Legendary+, 29 Ancient+ (tiers 10-13). **That
  set is biased** - an earlier auto-salvage threshold deleted low tiers, so its
  denominator is unknown. Do not compare the two percentages directly.

**The roll** (`RarityTier.RollTier`, `CombatLootEngine.cs:126`): Normal weight
100; tiers 2-13 weights 50, 25, 12.5, 5, 2.5, 1, 0.5, 0.1, 0.05, 0.01, 0.005,
0.001, all multiplied by `1 + LootLuckPct/100`. At luck 0: Legendary+ ~0.85% per
drop, Ancient+ ~0.03%, Godly ~0.0005%. **The recent 1.1% Legendary+ matches a
LootLuckPct near zero** - suspicious for a level-94 account with 300 base luck.
`LootLuckPct` is summed in `CombatLootDropRequest.Build` (`CombatLootEngine.cs`
~line 330): combat stats (LCK via `AttributeRegistry.DiminishedPercent`, area
bonus, affixes), inheritance, the skill tree's Loot Rarity branch, the guild
DropRate buff; plus `BonusRarityTiers` (Golden Fleece) and `rarityElevationPct`.

**Hypotheses, cheapest first:**
1. **The old Godly/Demonic pieces were FORGED, not dropped.** The forge fuses
   three same-rarity pieces into the next tier. If the tier 10+ pieces came from
   fusion, drops were always ~0.03% and nothing changed. There is no drop log in
   the database (`EcoTelemetryLedgers` is the only telemetry table - check what
   it records).
2. **The account's real `LootLuckPct` is far lower than expected.** Compute it
   for player 8 with the live formula (a test or an admin endpoint), printing
   each term, and check whether any term changed around the 2026-09-18 deploy
   of `84fe16c`.
3. **Offline catch-up vs live kills** - both build `CombatLootDropRequest`;
   check both fill in every luck term.

**Then decide with the owner** whether Ancient+ should be this rare at level 94
in region 5 (`PowerCeilingTests` and task 9's "rarity is worth one region step"
constrain the answer - read task 9 first). **Add a drop record** either way
(tier, source = drop/forge/craft, time), so the next "this feels sus" is
answered from data rather than reconstructed from row ids.

### Investigated 2026-09-23 (plan: `docs/superpowers/plans/2026-09-23-task-26-rarity-investigation.md`)

**The odds did not change.** Everything observed fits the authored table at
the account's real loot luck, which a test now computes rather than a person:
`RarityRollDistributionTests.ALevel94Region5Build_EveryLuckTermIsWhatTheFormulaSays`
reproduced the hand sum exactly (60.78 luck, 6.06% elevation before the fixes
below). Ancient+ was ~1 drop in 2,018; seeing none in 976 drops happens 62% of
the time. H1 (forged, not dropped): ruled out - no fusion affix key on any row.
H2/H2b (luck lower than expected, or changed at a deploy): ruled out. H3
(offline fills fewer terms): ruled out, fixed 2026-09-03. **Luck multiplies
tiers 2-14 equally**, so it can only shrink Normal and never more than about
doubles the top (1.72% Legendary+ at infinite luck on the plain roll); the
Ancient+ : Legendary+ ratio is luck-invariant (~4.15%) and is the one
comparison a survivor-biased chest allows. Two unrelated defects were found on
the way (H4, H5). H6 - the drought is volume, not odds (~10 days mean wait at
~195 drops/day) - is now measurable with the drop record below.

### Decided with the owner, 2026-09-24

- **B: the Rarity bough is rarity ELEVATION (+1%/level, +8% at cap), as its
  card always said** - it had been added to loot luck. Card text kept
  identical on both sides; `serverMirrors.test.ts` now holds all twenty node
  blurbs together.
- **C: area completion counts the five canonical regions only** (ids 91-115):
  1,000 kills of each regular monster and **100 of the region boss**. It had
  counted the 90 unfightable legacy monsters, so no region could complete and
  the +1 luck/region was 0 for everyone (0 rows in production). One rule now,
  `RegionCompletionRules`, asked by the login recompute, the codex worker and
  `/api/v1/codex/regions`. The Codex screen read the flags one bit off; fixed.
- **No weight, pity or tilt change** (options D, E, F declined).
- **Ship the drop record and an odds line in the Wiki.**

### Result (branch `fix/rarity-26`, not yet deployed)

| level-94 build (player 8) | luck | elevation | Legendary+ / drop | Ancient+ / drop |
|---|---|---|---|---|
| before task 26 | 60.78 | 6.06% | 1.195% | 0.0495% (1 in 2,018) |
| B | 52.78 | 14.06% | 1.299% | 0.0539% (1 in 1,854) |
| B + C, all five regions done | 57.78 | 14.06% | 1.316% | 0.0546% (1 in 1,831) |

Player 8 completes no region yet under C (region 2 and 4 regulars are done;
their bosses stand at 8 and 2 of 100).

**Balance:** `ItemRarityPowerTests`' two task-9 measurements run at zero luck
and are unchanged. A new one
(`Task26_TheDropOddsChange_MovesXpPerSecondByUnderTwoPercent`) runs the
invested build through the same best-of-N power model: XP/sec moves by at most
**+0.97%** (region 4, B+C), inside the owner's +/-2%. No monster HP change.

**Drop record:** `loot_tier_daily_counts` (a count per player/day/source/
region/final tier for every piece created, salvaged ones included) and
`notable_item_events` (a row for every Legendary+, every fusion, every
trophy), written only through `Engine/DropRecord.cs` inside the creating
transaction; one upsert per loot request. The migration is additive. A
rolled-back drop leaves no count and the retry outbox records it as
`OutboxRetry`. `DropRecordTests.EveryCreationSite_RecordsOrIsExcludedOnPurpose`
fails on an unrecorded creation site.

**Odds line:** the Wiki's "Drop chances and luck" section opens with the
player's own odds, computed server-side (`/api/v1/player/loot-odds`) from the
luck and elevation their last drop request rolled with.

**Verified 2026-09-24:** full server suite 954/954; `npm run exercise`
156/156 (including the new odds-line check); smoke:screens 26/26;
check:clipping 0; svelte-check at its 4-error baseline. Locally, the
exercise run's crafts wrote `loot_tier_daily_counts` rows (Source 5) through
the running server; it made only three kills, none of which dropped gear, so
live-kill and forge rows were proven by the Testcontainers tests rather than
by the dev box.

**Still open:** deploy (plan Task 6), then the next-day production query
(`SELECT "Source","QualityTier",sum("Count") FROM loot_tier_daily_counts
WHERE "PlayerId"=8 GROUP BY 1,2 ORDER BY 1,2;`) to measure drops/day and close
H6. The admin loot-stats view (plan Task 4b) was optional and not built.

## 27. Loot drops list: the top row is cut off (S)

**DONE, PR #21 merged and deployed 2026-09-24** (with 28 and 29).

**Reported:** with many items the loot drops table glitches and the top item is
cut off.

**Where:** `client_web/src/lib/ui/SessionLoot.svelte` - a `max-height: 16rem;
overflow-y: auto` list with `overflow-anchor: none` (deliberate, so new rows
inserted at the top stay visible; CLAUDE.md "Scroll anchoring"). Suspects: the
first row covered by a sticky header or the list's own padding/border, or rows
taller than their slot. Reproduce with a long session's worth of drops at
390px; then make `npm run check:clipping` catch it if it did not (it only
measures what its fixtures render, and a short list hides this).

## 28. Village stopwatch: the whole icon spins on the phone (S)

**DONE, PR #21 merged and deployed 2026-09-24** (with 27 and 29).

**Reported:** while a building upgrades the whole timer icon spins, not just
the hands.

**Already fixed once, for browsers:** `4926886` (2026-09-12) split the hands
from the dial and rotates only `.stopwatch .hand` about
`transform-origin: 12px 13px` with `transform-box: view-box` (`Village.svelte`
~lines 270-300). The live-update bundle (`/api/v1/app/bundle` ->
`1.0.464.zip`) contains that fix. So either the phone still runs the pre-fix
bundle (the APK was built 2026-09-12 00:04, before the fix; Capacitor
live-update should replace it on launch) or the Android WebView ignores
`transform-box: view-box` there.

**Fix it robustly instead of guessing:** draw the hands around the SVG origin
inside `<g transform="translate(12 13)">` so a plain `rotate()` needs no
`transform-box`; also check which bundle version the app reports. Verify on
the phone.

## 29. Market filters: the checkboxes are misaligned (S)

**DONE, PR #21 merged and deployed 2026-09-24** (with 27 and 28).

`client_web/src/routes/Market.svelte` ~lines 243-285: two
`<fieldset class="checks">` (Type, Tier) of
`<label><input type="checkbox">text</label>`. The CSS around lines 587-615 (read
the comment on `.filters > input` - the `>` is load-bearing) does not lay the
labels out as a grid, so they wrap raggedly. Make a tidy grid of equal cells,
box and text vertically centred; keep the 44px touch floor (a checkbox cannot be
enlarged by padding - size the label as the target). Run `check:touch` and
`check:overlap` after.

## 30. Docs match reality (S)

**DONE 2026-09-25.** This file's header and every stale status line in 14-38 were checked against GitHub. `CURRENT_IMPLEMENTATION_STATE.md` section 2.1 describes the tick layout after task 21. `CLAUDE.md` has no `SimulationEngine.cs` line references left to rot, and it now records the static-loot-queue test trap.

- This file's header (top ~30 lines) still says PR #11/#12 are unmerged and task
  21 Phase 2/3 not started; the 14-23 status paragraph and the first lines of
  tasks 17/18/19/20/22 ("Not started", "PR open, NOT YET MERGED") are stale.
  All of 14-24 are DONE and deployed. Point the header at this section.
- `docs/architecture/CURRENT_IMPLEMENTATION_STATE.md`: describe the new tick
  layout - `Domain/Shared/CommandGate.cs`, the dispatch table in
  `CommandCoordinatorContext.cs`, the per-domain `*TickCoordinator` classes, the
  Phase 1 drain coordinators, and `RunCraftingProgressTick` / `RunGatheringTick`
  / `RunCombatTick`. `SimulationEngine.cs` is 5,020 lines.
- CLAUDE.md: check every `SimulationEngine.cs` line reference still holds after
  the split; add the static-loot-queue test trap (PR #15: a worker a test never
  stops drains another test's loot into the wrong database).

## 31. CI: GitHub actions off Node 20 (S)

**DONE, PR #20 merged 2026-09-24.** Every action is on its Node 24 major.

`.github/workflows/deploy.yml` uses `actions/checkout@v4`, `cache@v4`,
`setup-dotnet@v4`, `setup-java@v4` (deprecated; v5 exists), `setup-node@v4`,
`upload-artifact@v4`, `android-actions/setup-android@v3`. GitHub already forces
them onto Node 24 with a warning. Bump each to its current major after reading
its breaking changes, push to `main`, watch the run go green.

## 32. Branch and worktree cleanup (S)

**DONE 2026-09-24** (no PR; branch deletions only). Anything not merged was listed for the owner rather than deleted.

Local: `combat-readability-and-rarity`, `feat/breeding-round-1`,
`fix/drops-progression-oracle-migration`,
`fix/guest-registration-and-reset-notice`, `mivoru-repository-audit`,
`perf/chest-drain`, `tooling/claude-setup-and-guild-donate-fix`, and today's
merged `fix/*` / `refactor/*` branches. Remote: `task-18-durable-grant-retry`,
`task-22-sustained-load-test`, `worktree-breeding-traits` and the merged PR
branches. Worktree: `copilot-worktrees/IdleHra/mivoru-improved-engine` (branch
`mivoru-repository-audit`). **Check each with `git branch --merged main` before
deleting; anything NOT merged is listed for the owner, not deleted.** Three
empty, git-ignored folders under `.claude/worktrees/` were locked by Windows on
2026-09-23 - delete them if still there.

## 33. Delete the 40 legacy crafting materials (M, approved by the owner) - DONE 2026-09-24

**Done** on branch `chore/delete-legacy-materials` (plan:
`docs/superpowers/plans/2026-09-23-task-33-delete-legacy-materials.md`). Three
facts a reader will want:

- **No id shifted.** Ids are explicit and act as array slots, so the 40 became
  holes; `ItemIdLedger.txt` + `ItemCatalogueIntegrityTests` now fail on any
  shifted, reused or dangling id (proved by sabotage before the deletion).
- **Production held nothing**: re-checked 2026-09-24, 0 rows across every table
  that stores an item id or slug (list in `docs/crafting_material_audit.md`).
- **The sprite aliases were the only live dependency** (`Iron bar`,
  `Silver bar` named two of the bars). Missing-art budget 165 -> 127.

Found on the way: the ten *surviving* `*_crafting_material` ores are
unreachable too (their loot rows belong to the pre-renumber mining nodes
201-205). **Follow-up, owner-approved 2026-09-24:** deleted the same way on
`chore/delete-legacy-ores`. Production held 0 rows. The `*_crafting_material`
namespace is empty now, and the missing-art budget went from 127 to 119.

Original brief:

`docs/crafting_material_audit.md` lists them: ten bars (ids 184-193) and thirty
superseded monster-drop materials (ids 2, 6, 12, 22, 25, 40, 43, 46, 58, 61, 64,
67, 76, 79, 82, 85, 100, 112, 115, 118, 130, 133, 136, 148, 151, 154, 166, 172,
175, 177). Its precondition was "query production first": **done 2026-09-23 -
no `CommodityRecords` row in production holds any of them** (the only regex hit
was `mat_magic_bark`, a false positive on `_bar`).

Still to check before deleting: every other place an item is stored or
referenced by numeric id (bank, mailbox, market listings, recipes, the loot
tables in `ContentRegistry`, client icon tables, `sprites.missing.txt`), and
CLAUDE.md's warning that **item ids are positional in several places** -
removing an `items.json` entry must not shift another item's id. Then remove the
definitions, regenerate sprites, let the content-validator hook pass, run the
full suite and `exercise`.

## 34. Retire the Unity project (M, approved by the owner) - DONE, PR #27 merged and deployed 2026-09-24

**Deletion done** on branch `chore/retire-unity`: 1,422 files (1,421 under
`client/` plus `unity_client.yml`), leave-in-place, the survivors match the
643-file manifest exactly. Both production images (`ops/oracle/web.Dockerfile`,
`server/Dockerfile`) were built from a clean, non-LFS export before and after:
215 sprites in the web image both times, and "Audio: 11 real clips in the
publish output". **Still to do after merge:** the deploy and the production
checks in the revalidated plan's steps 5-6, then close the backlog entry
(`NEXT_STEPS_BACKLOG.md`, "retire the Unity project").

Original brief:

Plan: `docs/superpowers/plans/2026-09-17-retire-unity-project.md` - three
independently revertible steps. The last touches the production Docker build
and CI, because the server serves artwork/audio out of `client/` (CLAUDE.md:
"Kept only for artwork/audio the web client fetches from the server").
Re-validate the plan against the current tree first (it is six days old), move
whatever the server actually serves before removing anything, and deploy +
smoke-test production after the last step.

## 35. Small leftovers (S) - DONE 2026-09-24

**Status.**
- **35a:** the four warnings are fixed, and `HandleStatsOnline` now logs its
  500. `FolkIdle.Server.csproj` builds with 0 warnings (branch
  `fix/35-warnings-and-art-list`).
- **35b:** fixed on the machine. DaVinci Resolve's installer had set
  `PYTHONHOME` at user level, and removing it restored the three hooks.
  `Test_ContentValidatorScript_MalformedJson_ExitsNonZero` passes locally
  now.
- **35c:** the ranked list is in `docs/art_backlog.md`. **24** items are worth
  drawing: the drops of the 25 canonical monsters, in tiers A, B and C. The
  other **95 cannot be obtained by anything**. They are a delete-or-wire
  decision for the owner, not art.

Original brief:

- Nullable warnings and an unused `ex` in
  `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs`.
- This machine's Python cannot import `encodings`, so
  `Test_ContentValidatorScript_MalformedJson_ExitsNonZero` and the content hook
  fail locally (CI is fine) - a machine fix, not a repo change.
- 165 items have no art (`sprites.missing.txt`; 40 go away with task 33). Rank
  the rest by how often a player sees them and hand the owner a prioritised list
  for an artist or generator.

## 36. World boss fight: skill and reflex, not a plate guess (L, design with the owner first)

**Owner, 2026-09-23:** the five-plate choice is boring - whether you click the
right plate is chance. Wanted: a minigame that needs some skill and reflexes.

Constraints that must survive (task 10's write-up above, `WorldBossEngine`):
**the client sends a CHOICE or a measured outcome, never a damage figure** - a
reflex game's result must be validated or bounded server-side (e.g. the server
issues the timing windows and checks the timestamps); 3 attempts per encounter;
one shared HP pool across all players; the armour-plate identity can stay as the
theme. It must work with a thumb on a phone (44px targets, no hover) and take
under a minute. Brainstorm candidates with the owner (timing bar, weak-spot
tapping, rhythm, dodge-and-strike), prototype one, then implement. **Task 25
first** - there is no point redesigning a fight nobody can start.

**Decided 2026-09-24: the shield wheel with parries.**
- Spec: `docs/superpowers/specs/2026-09-24-world-boss-minigame-design.md`.
- Plan: `docs/superpowers/plans/2026-09-24-task-36-world-boss-minigame.md`.

**Phase 1 (practice only) DONE 2026-09-25, behind `FOLKIDLE_BOSS_MINIGAME=practice`.**
- Practice spends no attempt, deals no damage and answers from a decoy weak
  plate. Every real path answers `Disabled` whatever the flag says.
- What shipped:
  - the pure rules, the schedule generator and the scorer, with a fixture
    shared by the C# and TypeScript tests;
  - the ledger;
  - the in-memory challenge registry and the REST routes;
  - the overlay (`ShieldWheel.svelte`) and a Practice button on the World
    Boss screen;
  - exercise runs: aimed (M 2.00), wrong reads (lost spears, M 1.40), and a
    brand-new account.
- Every geometry check and `check:perf` runs with the overlay open.

**PHASE GATE: first phone playtest done 2026-09-25. The owner's verdict:**
- **"Too fast. I cannot click anything, and the choices disappear."** Fixed in the spec first (section 3.5), then in code:
  - the response window is 2.0 s, was 1.0 s (enraged 1.7 s, was 0.85 s);
  - a freeze is 3.4 s and the run is 24 s, so the wheel spins no less;
  - the buttons are named by the blow (◀ From the left / ▼ From above / From the right ▶) and sit in one row, 60 px tall;
  - the tell sits directly above them, with a shrinking time bar.
- **Numbers decided: seam 8 degrees, Plate worth 0.** The ledger's random-tap check is un-skipped. Measured on the real scorer: random tapping **1.349**, 2 reads + random **1.744**, enraged 3 reads + random **1.917**. These are the spec's targets.
- **Second playtest on the retuned build (1.0.740), 2026-09-25: "it looks good now".** **The gate is PASSED.** Phase 2 is next; nothing of it is written yet. Start from `main`, with challenges metered per day (#48).
**World boss cadence changed 2026-09-25 (owner), shipped ahead of Phase 2:**
- **One encounter a week, Monday to Sunday UTC, back to back** (was the 1st-7th and 15th-22nd).
- **One strike a day** (was 3 per encounter). The 300 s battle session is gone.
- It lives in `WorldBossCalendar`, the daily count in `player_world_boss_attempts.AttemptDateKey` (migration `AddWorldBossAttemptDateKey`), and LiveOps refills online players at UTC midnight.
- Found on the way: two strikes meeting on the boss row made the loser fail with a Postgres serialization error ("could not be recorded"). `WorldBossEngine` now queues every boss-row writer.
- Open design question (`docs/world_boss_design.md`): with about 7 strikes a week, a solo player can find the weak plate alone in 4 days.

**Phase 2 DONE 2026-09-26 (PR #52), behind `FOLKIDLE_BOSS_MINIGAME=wheel`.
Production stays on `practice` until the owner flips it.**

What shipped:
- **Real challenges.** Each one draws its own weak plate at issue, only from
  the unbroken plates. The armour regrows every UTC midnight, keyed on the
  persisted `ArmourDayKey` (migration `AddWorldBossArmourDayKey`).
- **Scoring.** `/strike` scores the log and checks it against the answered
  throws. The tick prices it with A x G from the payload (budgeted drain,
  `WorldBossStrikeQueue`), and the engine applies it in the Serializable
  transaction.
- **Auto-strike over REST.** Opcode 32 under `wheel` answers
  `WorldBossUpdateRequired` (43).
- **Unfinished strikes.** An abandoned challenge resolves at the floor, and
  the owner is told once.
- **The wire.** `WorldBossSessionEndsEpoch` is gone (StateUpdatePacket 809 ->
  801).
- **The screen.** Strike (wheel), auto-strike, result card, and resuming an
  open run.
- **exercise.mjs** strikes blind and aimed for real. The aimed run's higher M
  reaches the damage on the same base hit.

Found on the way:
- **The 1,000 damage floor sat after the multiplier.** A typical A x G is
  100-200, so every strike dealt exactly 1,000 and neither skill nor the weak
  plate showed. The floor is now on the base hit.
- **`security-review`: a free re-roll.** A strike with no game session
  answered "nothing spent" after its throws had been answered. It is now spent
  at the floor, and a real challenge needs a live session (`NoGameSession`).
- **The owner decided the boss need not fall.** Rewards are paid at the end of
  every encounter by damage rank, and a damage board with the server's total
  is on the screen (`docs/world_boss_design.md`, "The weekly payout").

**Next (Phase 3):**
1. Deploy with `practice` kept.
2. Flip to `wheel` before the week of Oct 12 or Oct 19.
3. Watch the first week.

## 37. A late-game gold sink (L, design with the owner first)

**Owner, 2026-09-23:** earning 100M gold is easy now, so the Delve's entry fee
(`DelveRegistry.EntryFeeByRegion` = 7k / 17k / 42k / 100k / 250k by region) is
no sink. Rescale the Delve or add something new and fun.

Read first: task 11 (the Delve's design), `GatheringEconomyTests` (supply and
every sink in the same units), `LeaderboardRewardTests` and
`DelveRegistry.MaxDiamondsPerWeek` (60 - the Delve is the calibrated diamond
tap; do not inflate it). A sink that scales with income (a percentage, or prices
that climb with use) survives the next balance change; a fixed price does not.
Directions to put to the owner: fees scaling with wealth or level, a
prestige/cosmetic track, guild-level projects (ties into 38), high-roll forge or
reroll services, a gold-funded endless Delve depth.

**Decided 2026-09-24: "The Deep".** Spec
`docs/superpowers/specs/2026-09-24-the-deep-gold-sink-design.md`, plan
`docs/superpowers/plans/2026-09-24-task-37-the-deep.md`, brief (the measured
economy) `docs/superpowers/plans/2026-09-23-task-37-late-game-gold-sink-design.md`.
Phase 0.1 (combat gold is the named `EconomyDecisions.CombatGoldPercent = 75`)
merged in PR #32.

**Phase 0.2, measured income** (`GoldSinkAffordabilityTests`, all asserted):

- The top account's REAL sheet and gear (player 8, read from production
  2026-09-24: level 94, Warrior lineage, a tier-13 scepter, six attack-speed,
  four crit-chance and three crit-damage rolls, codex level sum 17,660 = 6.32x,
  inheritance damage 4) kills the Death Knight in **3.59 s = about 1.67M gold/h**.
- The brief's long-run gross (22 days, +633.8M and at least -152.1M) is
  **1.49M/h**; the profile is asserted within 3x of that.
- The brief's **10M/h "working figure" is NOT this account's combat**: it came
  from two single offline catch-up snapshots the telemetry cannot separate from
  chest sales, and the combat model's absolute ceiling on the Death Knight (a
  lethal swing every 600 ms) is 9.96M/h. The plan's "within 3x of 10M/h" does
  not hold; the test asserts the account sits under a third of it instead.
- The sink table at 1.67M/h: reroll r5 0.36 min, fusion into tier 14 0.36 min,
  village L20 15 min, Delve gate r5 9 min (all repeatable ones under 10% of an
  hour); feast n=16 166 min, n=20 18 h, n=25 190 h (bounded by the Inn, so not
  repeatable). **No repeatable sink absorbs 30% of an hour**: that row is
  skipped until the Deep lands.

**DONE 2026-09-25, all three phases shipped, and the Deep is ON in
production** (`FOLKIDLE_DELVE_DEEP=on` in the box's `ops/oracle/.env`,
deployed as 1.0.719).
- **Phase 0.2** is PR #34.
- **Phase 1** (descend on a frozen stake, tolls, records, the 7-day
  high-water mark) was re-opened as PR #39 after #35 stranded.
- **Phase 2** (lanterns at stake x 2^k up to 8, six titles at Deep floors
  10-50, the weekly Deepest board that pays nothing) is PR #40.

The sink table after the Deep: one descent at the top account's 492M
holdings is a 2.46M stake, **about 88 minutes of top income (1.67M/h) for the first
floor alone**. `ARepeatableSinkAbsorbsAThirdOfAnHourAtTheTop` is no longer
skipped, and `TheDeepsWorkedPricesAreTheSpecs` pins pushes to floors 12 and
20 at 0.5-4 h and 8-24 h of income. A wallet parked on an alt still pays on
the 7-day high.

**Phase 3 (measure) is due on 2026-10-02**, a week after the flag went on.
It is one command:

    ssh folkidle-server "cd ~/folkidle/ops/oracle && docker compose exec -T postgres psql -U folkidle -d folkidle" < ops/oracle/deep-phase3.sql

Compare it against the day-0 baseline below, then write the week's numbers
under it.

**Found while preparing it: nothing recorded what the Deep took.** Tolls
and lanterns came straight off the chest row, and `EcoTelemetryEngine`
counted only guild sinks and market fees as consumed. The week would have
produced depths and titles but no gold figure. Since 2026-09-25 two things
fix that:
- `PlayerRecords.DelveDeepGoldSpent` is a lifetime total of every toll and
  lantern, written in the same transaction as the debit (migration
  `AddDelveDeepGoldSpent`);
- the eco audit sums that column into `TotalGoldConsumed`.

Tolls paid on the flag's first hours, before the column existed, are not
counted.

**Day-0 baseline, 2026-09-25 about 18:05 UTC** (deployed as 1.0.726, about
an hour after the flag went on). Nobody had entered the Deep yet:
- 0 players had paid a toll, and `DelveDeepGoldSpent` totalled 0;
- 0 records past floor 8, 0 titles, 0 open Deep runs.

The economy it has to act on:
- 558.7M gold held in total;
- **one account holds 558.6M of it**, the only holder of 10M or more;
- the eco ledger's `TotalGoldMinted` stood at 559.3M and
  `TotalGoldConsumed` at 0.57M.

So the Deep's effect is, in practice, whether that one account descends.
Its stake would be 0.5% of its 7-day high, about 2.8M.

**Week 1 (2026-10-02):** *to be filled in from `deep-phase3.sql`.* The
owner's PC runs it automatically: the one-time scheduled task **"FolkIdle
Deep phase 3 capture"** fires at 10:30 that day, or at the next logon if the
PC is off, and writes `D:\FolkIdleBackups\deep-phase3-week1.txt`. Copy its
numbers here.

## 38. Guild Wars: design the whole system (XL, design with the owner first)

**PARKED by the owner's rule: it waits until the population nears the floor (50).** Measured in production on 2026-09-25: 61 accounts, **1** at level 10 or above, 15 active in the last 7 days, **1** guild. The Guild War lock (PR #22) needs 4 or more guilds of 3 or more level-10 members. The design (H2H async plus Great Works) is in `docs/superpowers/specs/2026-09-24-guild-wars-design.md`. Re-measure before starting.

**Owner, 2026-09-23:** design Guild Wars completely - format, rewards,
everything; take inspiration from popular games.

What exists: a hidden Guild War UI in `GuildOps.svelte` (its four handlers -
`defend`, `attackShard`, `takeTurn`, `damageDelta` - are the four permanent
`svelte-check` errors), `GuildMatchmakingEngine` (a guarded `StartCron` loop),
`GuildWarTickCoordinator` (turn/defence/shard commands since task 21), and guild
war point queues fed from combat. Start with an end-to-end audit of what is
actually wired (the `wiring-auditor` agent exists for exactly this), then
brainstorm with the owner: format (async season vs live), matchmaking, what a
player does day to day in an idle game, rewards and how they sit against the
diamond economy and task 37's sink, anti-abuse. Then a written spec, then phased
plans. Do not delete the hidden handlers before this lands - they are the
`svelte-check` baseline.
