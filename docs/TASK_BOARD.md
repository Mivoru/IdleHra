# FolkIdle Task Board

> **NEWEST (2026-10-01): TASKS 89-110, at the bottom of this file**, from the
> UI/UX audit `docs/audits/2026-10-01-ui-ux-audit.md`. 89-93 are BUILT and
> verified together on `claude/ui-integration` (exercise 276/276, the four
> geometry checkers at 0), not merged yet. Read "Wave 1 result" at the end of
> that section, then work in the order of its table.

> **NEWEST (2026-09-30): read `docs/handoff-2026-09-30.md` first.** It lists
> what is live, the owner's decisions (offline = online; rebirth on demand; 83,
> 84, 85, 87 approved; 86 deferred), the branches in flight and their order.

> **START HERE (updated 2026-09-29). The front of the board is TASKS 68-88,
> at the bottom of this file: the second design audit (2026-09-29).** Work
> them in the order of that section's table; the "How to work" rules of tasks
> 48-62 still apply. The audit itself is the claude.ai artifact
> 5di35DqxEfb5zpNUh5Sxsy (Czech). Tasks 48-67 below are done except 62
> (parked).
>
> **Status at the end of 2026-09-29:** 68-72 DONE and 74 partly (PRs
> #102-#107, merged; deploy recorded in NEXT_STEPS_BACKLOG's handoff). **77,
> 81 and 78 are BUILT** (PRs #109, #110 and the 78 PR, which is stacked on
> #110; none deployed, and #110 carries a migration). Nothing is left
> without the owner. Show the owner a screenshot first:
> **73** (Home), **82** (desktop header). Owner decisions waiting: **75, 76,
> 79 (Treasury), 80**, and the designs **83-88**. Left from 74: one number
> format, reroll history.
>
> **PREVIOUS START HERE (2026-09-28). The front of the board was TASKS 48-62,
> at the bottom of this file: the follow-through of the 2026-09-28 design
> audit.** Do them in the order of the table in that section. Task 48 (the
> deploy) is URGENT: the active season ends on **2026-11-02 09:40 UTC**, and
> only the deploy of `main` pauses it.
>
> Context for a fresh session, in this order:
> 1. The table and "How to work" at the top of "Tasks 48-62" below.
> 2. `docs/superpowers/plans/2026-09-28-design-audit-phases.md`: what was built
>    on 2026-09-28, the owner's decisions, and why.
> 3. The audit itself: claude.ai artifact TTYuzRgizz25WUbVWrHjkw (Czech; read
>    it only for a task that points at an audit section).
>
> The block below this one is the previous START HERE (2026-09-26). Its
> items 1-7 are done or are standing measurements.

> **PREVIOUS START HERE (2026-09-26, late) - superseded by the block above. Tasks 1-37 are DONE and deployed;
> production runs `main` (1.0.756) with the shield wheel LIVE
> (`FOLKIDLE_BOSS_MINIGAME=wheel`, PRs #52 and #53).** The handoff with context
> is at the top of `docs/architecture/NEXT_STEPS_BACKLOG.md`.
>
> **TODO, in order:**
> 1. **Check the first weekly payout** (Sunday 2026-09-27 23:59:59 UTC).
>    - Everybody who struck gets mail.
>    - Compare `player_world_boss_attempts` before the reset with
>      `MailboxInstances` rows for `perun_avatar_reward_token`.
> 2. **Task 37 Phase 3: measure the Deep, due 2026-10-02.** The owner's PC
>    captures `D:\FolkIdleBackups\deep-phase3-week1.txt` automatically. Copy
>    it into section 37, "Week 1".
> 3. **Watch the wheel's first weeks:**
>    - how many strike daily;
>    - wheel versus auto-strike;
>    - how fast the board strips each day. The lever, if it strips too fast,
>      is breaking only on a Seam (spec 3.3.1).
>
>    The old "solo player finds the weak plate in 4 days" question is **moot**:
>    the weak plate is drawn per strike and the armour regrows daily.
> 4. **Task 38 (Guild Wars): parked** until the population nears the floor.
>    Re-measure before starting.
> 5. **Tasks 39-47, the 2026-09-26 architecture audit: DEPLOYED 2026-09-27 as
>    1.0.795 (PR #56).** Checked live: migrations applied, the commodity
>    unique index exists and 0 duplicates, `smoke:screens` 27/27, a trickled
>    body did not delay `/healthz` (0.08 s), tick p99 < 10 ms, 0 failed
>    checkpoints. Backup taken first: `folkidle-20260927T095853Z.dump`. Suite 1190/1190,
>    `exercise.mjs` 186/186, client vitest 590 passed, ratchet at baseline 4.
>    - 43 phase 2d (fixed timestep) DEPLOYED 2026-09-27 as 1.0.808 (PR #62,
     together with #61). Live: tick p99 0.3 ms, 0 catch-up, 0 dropped.
>    - 46 is step 1 only: frames measure ~5.3 KB (dev), an estimated
>      310-370 KB/player/min against the 150 KB threshold. The go/no-go needs a
>      week of production `/metrics` after the deploy.
>    - 45 needs the owner's phone, on a NEW APK: a first clear vibrates, backgrounding schedules the
>      reminder.
>    - Still owed: `docs/ops/funnel.sql` once new players arrive (0 rows at
>      deploy, nobody online) and the week of frame-size `/metrics` for 46
>      (from 2026-09-27). The two-browser chat check (41) passed 2026-09-27.
>    - `exercise.mjs`'s new player never lands a kill (onboarding only reaches
>      "Fight"), so it produces funnel step 1 but not step 2; the kill hook is
>      proven by the dev fixture's live kill and `FunnelRecorderTests`.
> 6. **Backlog follow-ups: CLOSED 2026-09-27** (branch `fix/backlog-ui-followups`):
>    - the Village "Got it" overlap and the practice ring in landscape were
>      CHECKER false positives: `check:overlap` exempted a fixed overlay only
>      when it was the covering button's parent (the coach's button is two
>      levels in; the Upgrade button scrolls clear), and `check:safearea`
>      measured a rotated SVG shape by its rotated bounding box (the ring's real
>      edge is at ~70px against a 44px inset). Both checkers fixed;
>    - the Forge row already passed `check:touch`;
>    - the real defect found on the way: the update prompt ignored the side
>      insets (under the landscape cutout on every screen) and sat on the chat
>      handle at desktop widths;
>    - drop record: the outbox replays under the original source, offline boss
>      drops are `OfflineBossGuarantee` (10), a fusion writes its row but no
>      second count, and both tables are pruned at 180 days (EcoTelemetry loop).
> 7. **Fusion affixes, reported by the owner 2026-09-27:** a fusion added its
>    affix as `<id>_<4 hex>`, which the reroll refused and the stat totals never
>    matched (17 items, one account, in production). Fusion rolls through
>    `AffixRegistry.TryRollOneAdditional` now; old keys are read by
>    `TryStripLegacyFusionSuffix` and rewritten by a reroll. `FusionAffixTests`.

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

**Phase 3 DONE 2026-09-26.**
- The owner chose to ship everything at once: merged (#52, #53), deployed as
  1.0.756, flipped to `wheel`, and the Wiki describes the wheel.
- Production checks: the migration applied, `ArmourDayKey` is stamped, and
  `smoke:screens` passed 27/27.
- **To roll back:** set `FOLKIDLE_BOSS_MINIGAME=practice` in the box's
  `ops/oracle/.env`, then run `deploy.sh`.

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

# Tasks 39-47: the 2026-09-26 architecture audit

Added 2026-09-27. **The plan is `docs/superpowers/plans/2026-09-26-audit-remediation.md`.**
It holds the steps, the tests to write first, and the "Done when" criteria.
This section is the index; do not copy the plan's steps here, because two
copies drift.

**Order is 39 → 47 as numbered.** It is not the audit's order:
- the funnel goes first, because every day it is not live is a cohort nobody can measure afterwards;
- 42 goes before 43, because both edit `FlushState`.

**Decisions, settled 2026-09-27:**
- **D1, set by the owner:** split-brain compensation is a flat **1,000 gold**, once per incident.
- **D2-D5, delegated by the owner and recorded with their reasoning in the plan's decisions block:**
  - first kill: reopened only by a pre-set funnel trigger;
  - haptics: on by default;
  - frame size: measure, then compression before deltas;
  - Kestrel: only if compression is needed, or if concurrent players pass about 200.

| # | Task | Plan item | Size |
|---|---|---|---|
| 39 | Funnel telemetry: `player_funnel_events`, 12 first-time steps | 4 | S-M |
| 40 | Concurrent HTTP router, per-account striped lock, bounded body reads, Caddy `request_buffers` | 1 | M |
| 41 | Per-session outbox: events are never dropped silently, and one slow peer no longer delays everyone | 3 | M |
| 42 | Cap the split-brain gold mail (flat 1,000, once per incident) | 6 | S |
| 43 | Checkpoints off the tick thread: `CheckpointWriter`, staggered boundaries, fixed timestep | 2 | L |
| 44 | Unique `(PlayerId, ItemId)` on `CommodityRecords`, and `CommodityLedger.AddAsync` | 5 | M |
| 45 | Haptics and local notifications (`OfflineCapSeconds` on the wire) | 7 | M |
| 46 | State-frame size: measure, then compression via Kestrel, then deltas only if needed | 8 | S to L |
| 47 | Audit leftovers | "Also found" | S each |

## 39. Funnel telemetry (S-M) - plan item 4

**Why:** 61 accounts, **1** at level 10 or above (2026-09-25). Nothing records where the
other 60 stopped. `PlayerRecord` has no creation timestamp, so the funnel's
"registered" row is also the only record of when an account was created.
Accounts from before the deploy are outside the cohort, and that is accepted.

**Done when:**
- `exercise.mjs`'s new-player context produces steps 1-2 locally;
- `docs/ops/funnel.sql` returns rows in production after the deploy;
- `CronWorkerGuardTests` lists the new worker.

**Follow-up:** apply the D2 trigger once the cohort holds at least 30 registrations.

**DONE (pending deploy), branch `feat/funnel-telemetry`.** Built:
- `player_funnel_events` (`PlayerFunnelEvent`, migration `AddPlayerFunnelEvents`), keyed `(PlayerId, Step)`, listed in CURRENT_IMPLEMENTATION_STATE.md §3.
- `Engine/FunnelRecorder.cs`: static queue, per-session guard (re-armed at login), and a budgeted cron worker that writes one `unnest` multi-row `INSERT ... ON CONFLICT DO NOTHING` per cycle. The try opens before `CreateScope`, and it prints a one-line heartbeat every minute with the queue depth. It is in the `CronWorkerGuardTests` inventory.
- All 12 hooks. `FunnelRecorderTests.EachStepHasItsDocumentedWriters` pins the writer count for each step.
- Levels 5/10/20 and onboarding_done are recorded in `FlushState` after it commits.
- Returned d1/d7 is a login probe: the worker joins it against the player's own step-1 row.
- `docs/ops/funnel.sql`.

Where it differs from the plan:
- **onboarding_done** is the client's tier-one "Completed" predicate: food in the larder, level at least 2, and a weapon worn. It is read at the checkpoint. The plan pointed at `OnboardingSeenIds`, but that has no "final id".
- **joined_guild** goes through one `GuildManagementEngine.PublishJoined`, shared by create, join and approve.
- **funnel.sql** uses `COALESCE` on the username. A guest has a NULL username, and without it the plan's query dropped every guest.
- **The warp path** (`ApplyBulkExperience`) has no callers any more. The test reaches it by reflection.

Still open: the `exercise.mjs` new-player check and the production SQL run, both after merge and deploy.

## 40. The HTTP accept loop handles one request at a time (M, P0) - plan item 1

**Why:**
- `ListenLoopAsync` (`NetworkBroadcastSystem.cs:663`) awaits 79 handlers inline, and 37 of them read bodies with `ReadToEndAsync`.
- One slow request stalls every login and WebSocket upgrade.
- A trickled body on `/api/v1/assets/handshake` freezes the server **without authenticating**.

**The trap:** handling one request at a time is what currently prevents a
double chest sale. Add the per-account lock **before** making the loop
concurrent.

**Done when:**
- `HttpRouterConcurrencyTests`, which fails on `main` today, passes;
- the double-sale test passes;
- `exercise.mjs` passes;
- `smoke:screens` passes against production;
- CLAUDE.md gains the "per-account striped lock" rule.

**DONE (pending deploy), branch `fix/concurrent-http-router`.** The accept loop
only accepts; `RouteAsync` runs each request on its own task with a 500 guard;
non-GET bearer requests hold a 1024-way striped per-account lock (10 s, then
429 `AccountBusy`); all 37 `ReadToEndAsync` calls go through `ReadBodyAsync`
(64 KB/413, 30 s deadline); Caddy's `@api` handle buffers bodies
(`request_buffers 1MB`, `max_size 1MB`). `HttpRouterConcurrencyTests`' healthz
case failed on main (the probe hit its 1 s timeout behind a stalled body) and
passes now. Caddy was dry-run locally against a single-threaded stub upstream:
a trickled body delayed `/healthz` 8.5 s with the old file and 0.02 s with the
new one. Still open: `exercise.mjs`, the production `smoke:screens` and the
production trickle `curl`, all after deploy.

## 41. Loot, combat and chat frames are dropped silently (M) - plan item 3

**Why:** `WebSocketSession.SendAsync` (`:126`) drops a frame when the socket is
busy. That is right for snapshots and wrong for events. The three dispatch
loops also await each send in turn, so one peer that has stopped reading
delays everyone for up to 20 s.

**Done when:**
- `SessionOutboxTests` (a) to (e) pass;
- `outbox_events_dropped_total` is in `/metrics`;
- `exercise.mjs` passes;
- a two-browser chat check under combat, done by hand, shows every message.

**Status: DONE.** Two-browser chat check done 2026-09-27 (scripted): a guest sent 12 messages at the rate limit to the dev fixture while it fought, and all 12 arrived.
Earlier status: DONE (pending deploy; manual two-browser chat check still owed; `exercise.mjs` not yet run).
`WebSocketSession` is now an outbox with one writer task per session: events
(loot, combat, chat, announcements) queue in a bounded 512 drop-oldest channel,
snapshots keep one latest-wins slot, the writer sends events before the snapshot
of the same wake-up, and `CloseAsync` is a sentinel the writer honours. The three
dispatch loops only enqueue. The 20 s timeout and `IsWedged` live in the writer.
Binary state frames copy into a rented buffer (`DiagnosticSendBuffer` is gone).
`/metrics` carries `folkidle_outbox_events_dropped_total` and
`folkidle_outbox_queue_depth`. Tests: `SessionOutboxTests` (a)-(e) plus the close
sentinel and a binary frame; `SocketBackpressureTests` rewritten against the new
API.

## 42. The split-brain gold mail has no cap and pays more than once per incident (S) - plan item 6

**Why:** `StateCheckpointManager.cs:265` mails `epochDelta * 500` gold, with no
ceiling and nothing keying it to one incident. The trigger is plausible, not
demonstrated.

**Owner decision:** a flat **1,000 gold** per incident, recorded once per
`(PlayerId, DbEpoch)` in `split_brain_incidents`.

**Done when:**
- the past-payout count from production is in the PR;
- the "flushed twice, mailed once" test passes.

**DONE 2026-09-27** (branch `fix/split-brain-gold-cap`). Production before the
change: `SELECT count(*), sum("GoldAttachment") FROM "MailboxInstances" WHERE
"BaseItemId"='GOLD_COMPENSATION'` = **2 rows, 1,000 gold** - the path does
fire. Now: table `split_brain_incidents` (migration `AddSplitBrainIncidents`),
`INSERT ... ON CONFLICT DO NOTHING` in the same transaction as the mail, flat
`SplitBrainCompensationGold = 1000`, and one log line per refusal with player,
both epochs and the Redis lock holder. `SplitBrainCompensationTests` pins it.

## 43. Blocking database checkpoints on the 10 Hz tick thread (L, the riskiest) - plan item 2

**Why:** `FlushStateAndAdvance` (`:172`) runs a Serializable transaction
synchronously on the one thread that simulates every player. It is called from
10 sites.

**Read before starting:**
- CLAUDE.md, "Two gold paths" and "A Redis frame is not a checkpoint";
- `RedisSessionCache.TryStoreFrame` (`:79-83`): with Redis up, the gold delta moves into Redis on the tick thread.

Three PRs:
- **2a:** the writer alone.
- **2b:** the callers, one per commit, plus deleting the `InventorySpaceRemaining` boundary.
- **2c/2d:** staggered boundaries, then a fixed timestep as its own PR.

**Done when:**
- a guard test finds no `FlushStateAndAdvance` call on the tick thread;
- tests (a) to (e) pass, including gold banked exactly once while a flush is in flight;
- `exercise.mjs` passes;
- tick p99 in production is under 25 ms.

**Status (2026-09-27, branch `perf/checkpoint-writer`, not yet merged or deployed):**
- **2a done.** `Domain/Shared/CheckpointWriter.cs` (4 partitions, per-job guard,
  logout retries + gold rescue + `CHECKPOINT-DEADLETTER` line, login fence),
  `StateCheckpointManager.RequestFlush`, `CheckpointAckTickCoordinator`,
  `TickStatePayload.FlushesInFlight`, `PlayerSessionRegistry.FlushAckQueue`,
  `CommandResultCode.CheckpointFailed` (44). Tests (a)-(e) in `CheckpointWriterTests`.
- **2b done.** TrackState, guild treasury + war supply, forge fusion + reroll, market
  (3 handlers), ReloadState, Logout - one commit each; the `InventorySpaceRemaining`
  boundary deleted. `CheckpointOffTickGuardTests` allows only the login call.
- **2c done.** `AddActivePlayer` staggers the boundary by `PlayerId % 3000` (`CheckpointStaggerTests`).
- `/metrics` now has `folkidle_tick_duration_recent_milliseconds{quantile="0.99"}`
  (last 600 ticks) and checkpoint queue/failure/dead-letter gauges.
- **2d DEPLOYED 2026-09-27 as 1.0.808 (PR #62).** `Domain/Shared/TickPacer.cs`
  keeps an absolute 100 ms schedule: an overrun or an oversleep is caught up
  instead of lost, at most 5 ticks owed at once, the rest dropped and counted.
  `/metrics` gains `folkidle_ticks_catch_up_total` and
  `folkidle_ticks_dropped_total`. `TickPacerTests` is the guard.
  `ProgressionRateTests` never runs `EngineLoop`, so its tables cannot move;
  what changes is live progress per wall-clock minute, which rises to a true
  10 Hz wherever ticks used to overrun.
- **Open:** the production p99 check after deploy, and a look at
  `folkidle_ticks_dropped_total` (should stay near 0).

## 44. `CommodityRecords` has no unique key; about 30 check-then-insert sites (M) - plan item 5

**DONE (pending deploy), 2026-09-27, branch `fix/unique-commodity-rows`.**
Production duplicate query returned 0 rows on 2026-09-27. Migration
`MakeCommodityRecordsPlayerItemUnique` (not additive: merges duplicates into
the lowest `Id`, repoints `MarketOrderRecords`/`historical_market_archives`,
then makes the index unique). `Engine/CommodityLedger.cs` is the only writer
(`AddAsync`, `AddManyAsync`); it rebases a row the context already tracks.
`CommodityLedgerTests` is the guard. Still to do: `exercise.mjs` on the dev
box, then re-run the duplicate query after deploy.

**Why:** a second `gold` row splits a balance. `MarketTickCoordinator`'s
settlement rescue (`:58-80`) inserts with no lock at all.

**Before writing the migration:** run the duplicate query in production and
put the result in the PR. The migration is **not additive**: it merges the
duplicates, then adds a unique index.

**Done when:**
- the guard test allows `new CommodityRecord` only in `CommodityLedger` and the two seeders;
- `exercise.mjs` passes;
- the production query returns 0 rows.

## 45. Haptics and local notifications (M) - plan item 7

**Status (2026-09-27): code DONE; on-device check owed (owner's phone).**
Branch `feat/haptics-local-notify`. `@capacitor/haptics` and
`@capacitor/local-notifications` installed and synced (both in
`nativeProjects.test.ts`); `haptics.ts` (boss first clear heavy - kill and crit dropped on the owner's playtest as noise; loot tier
10+ success, parry window light, plate break heavy; 80 ms throttle; Settings
toggle, default on); `localNotify.ts` (fixed id 4501, scheduled on background
at `now + OfflineCapSeconds - 3600`, cancelled on resume, permission from a
Settings button only). `OfflineCapSeconds` is on `StateUpdatePacket`
(801 -> 805), computed from the hydrated `VodnikMasteryLevel`. Owed: on the
phone (an APK built after 2026-09-27 - the plugins are native), a plate break
or a boss first clear vibrates, and backgrounding the app schedules the reminder.

**Why:** neither plugin is installed, and local notifications need no Firebase.

**Rules to follow (CLAUDE.md):**
- a plugin read off the global still has to be INSTALLED;
- `npm run sync`, not raw `cap sync`;
- **declare** `POST_NOTIFICATIONS`.

Haptics default to **on**. `OfflineCapSeconds` goes on the wire, using the
`add-command` skill (801 → 805). The client must not mirror the 12 h rule.

The larder run-out notification is **not** in scope. It needs a
server-computed field, so file it separately if wanted.

**Done when:**
- `nativeProjects.test.ts` lists both plugins;
- the unit tests pass;
- on the owner's Android phone, a boss first clear or a plate break vibrates and backgrounding schedules the notification.

## 46. State-frame size (S to L) - plan item 8

**Why:** web clients get JSON snapshots of about 230 fields, serialized on the
tick thread with no WebSocket compression.

**Step 1 (8a - Measure): code done, `perf/state-frame-metrics` off
`feat/session-outbox` (62bf676).** `/metrics` now carries
`folkidle_state_frame_bytes_total`, `folkidle_state_frames_total` and
`folkidle_state_frame_serialize_microseconds` (a histogram timed tightly
around `PacketJsonCodec.SerializeToUtf8` alone, in `SendToPlayer`,
`NetworkBroadcastSystem.cs`; binary-path frames count toward bytes/frames but
are not timed - see `StateFrameMetrics`'s own remarks for why). A **week of
live production numbers off that endpoint is still owed** before the actual
go/no-go call below can be made - what follows is a synthetic measurement
from `StateFrameSizeTests`, useful as an upper-bound estimate, not a
replacement for it.

**Measured (synthetic, 2026-09-27):**
- A `StateUpdatePacket` with every field reflectively set to a representative
  non-zero, multi-digit value (`StateFrameSizeTests.BuildRealisticMidGamePacket`,
  standing in for a levelled, geared, mid-to-late-game character) serializes
  to **6,205 bytes** of JSON. The 801-byte binary struct is not the right
  comparison for JSON overhead - an **all-zero** packet already serializes to
  **5,186 bytes**, because 230 PascalCase field names plus JSON punctuation is
  a fixed cost paid before a single stat is written. Actual gameplay values
  only add ~1 KB on top of that floor.
- `SerializeToUtf8` itself: median ~42-48us, p99 ~63-116us over 2,000 warmed
  runs on the realistic packet - tens of microseconds, not milliseconds.

**Implied bandwidth (upper-bound ESTIMATE, not a production measurement):**
send cadence is effectively **1 frame/second per connected session** - the
broadcast pass only runs once every `BroadcastKeepaliveTicks` (10) ticks at
10Hz, and that constant equals the loop's own cadence, so the keepalive
branch in `ShouldDispatchStateUpdate` fires on essentially every pass
regardless of the dirty-check (worth a separate look, but out of scope for
8a). At 6,205 bytes/frame x 60 s: **~372 KB/player/minute** (≈364 KiB); even
at the theoretical zero-value floor of 5,186 bytes/frame x 60 s: **~311
KB/player/minute**. Both are **over the ~150 KB/player/minute threshold**,
by roughly 2.1x-2.5x - the bandwidth trigger reads as crossed even before a
week of real traffic confirms it.

**Implied CPU cost:** all per-player serialization for a given broadcast
pass happens inside the SAME 100ms tick (the once-per-10-ticks gate), so the
10%-of-tick-budget (10ms) threshold is a per-tick concurrent-player count:
roughly 86 simultaneously-online JSON sessions at the measured p99 (116us
each), or ~208 at the median (48us each), before that tick's serialization
alone would reach 10ms. Current registered population is far below that
(memory notes: 61 registered accounts, 2026-09-25), so **the CPU trigger
does not read as crossed** at today's population - this is an estimate, not
a live count of concurrent sessions.

**Steps:**
1. **Measure (8a).** A week of `/metrics`, plus a test that asserts the frame size.
2. **Go if either threshold is crossed:**
   - bandwidth above about 150 KB per player per minute;
   - serialization above 10% of the tick budget.

   Otherwise close the task with the numbers written down.
3. **If bandwidth is the trigger:** Kestrel plus permessage-deflate (8c), then re-measure. Build deltas (8b), with capability negotiation, only if frames are still over the threshold.
4. **If only the CPU trigger fires:** skip compression and move serialization off the tick thread instead.

## 47. Audit leftovers (S each)

**Status 2026-09-27: all six handled on `fix/audit-leftovers`** - five fixed, market lock ordering recorded below and left alone on purpose.

- **DONE (fix/audit-leftovers).** **The Character sheet's HP bar uses an estimate.** `Character.svelte:370` uses `observedMaxPlayerHp`, while `PlayerMaxHp` is on the wire and `Combat.svelte:157` uses it. Switch it, and delete the estimate (`game.ts:66`). *Both screens now read `PlayerMaxHp` (clamped to at least `PlayerHp`); `observedMaxPlayerHp` is deleted.*
- **DONE (fix/audit-leftovers).** `MailboxInstances` has no index on `PlayerId`. *`IX_MailboxInstances_PlayerId`, migration `AddMailboxPlayerIdIndex` (additive).*
- **DONE (fix/audit-leftovers).** Offline catch-up makes up to 200,000 loot rolls one at a time (`OfflineSimulationEngine.cs:94-103, 954`). Use a binomial draw per table entry. *`DrawLootCounts` draws the multinomial as a chain of conditional binomials, so counts still sum to exactly the capped roll count and each entry keeps mean n*w/W. `SampleBinomial` is exact CDF inversion below a mean of 30 (every rare entry) and a rounded normal above. `OfflineLootDrawTests` pins the sum, the per-entry means, luck and the runtime bound.*
- **DONE (fix/audit-leftovers).** `visualState.set` runs on every animation frame (`game.ts:110`), even when nothing is moving. It is also listed under 46/8b; do it here if 46 is closed. *Now publishes only a frame that moved > 0.5 (the settling frame always lands exactly), and the rAF loop stops once the interpolator has settled and no damage number or toast is alive; a snapshot, combat event or local notice restarts it (`shouldPublishVisual`, `SnapshotInterpolator.isSettled`).*
- **DONE (fix/audit-leftovers).** All 26 screens load at startup (`App.svelte:3-31`, one 644 KB chunk). Load the large ones with `import()`. *Login and Hub stay static; the other 25 are `import()`ed on first visit (`SCREEN_LOADERS`), with a Reload answer when a chunk is gone after a deploy. `npx vite build`: the entry chunk went from 659.00 kB (215.37 kB gzip) to 249.80 kB (84.93 kB gzip); the largest lazy chunk is the Wiki at 114.20 kB.*
- Lock ordering in market matching (`MarketOrderBookEngine.cs:412-474`) only matters at a higher population. Record it and do nothing yet. *Recorded 2026-09-27, deliberately NOT fixed.* `MatchOrdersAsync` (`Engine/MarketOrderBookEngine.cs`, Serializable) takes its row locks in match order, not a global order: all BUY rows for the item/tier by price, then all SELL rows, then per match the SELLER's gold row, the escrowed `MarketEquipmentInstances` row, and (offline buyer with a refund) the BUYER's gold row. Two matches running at once for different items can therefore lock the same two players' gold rows in opposite orders (A sells to B in one, B sells to A in the other) and deadlock; Postgres aborts one with 40P01, and at Serializable a 40001 is just as likely. Harmless at today's population. The fix when it matters: collect every gold row a matching pass will touch, lock them up front in ascending `PlayerId` order, then match - and give the caller a retry on 40P01/40001 rather than a logged failure.
- **Larder run-out notification** (split out of 45): needs a server-computed `ProjectedLarderSeconds` on the wire from `OfflineSimulationEngine`'s food model; the client cannot compute the drain honestly.

---

# Tasks 48-62: the 2026-09-28 design audit, what is left

Added 2026-09-28. The audit covered the game as a player meets it: code,
production data, and 17 screenshots of a fresh guest at 390px.

**Already shipped from it, on `main` (PRs #68-#74), all in the plan doc:**
- the Village shows the server's real prices;
- support messages are logged;
- the Chronicle pass is hidden;
- achievements have names;
- the phone header is compact;
- the Map has "Right now" and "Closest goal" cards with an ETA;
- the owner can pause and end a season from the admin panel;
- a new account starts with a claymore and 10 fish, and the tutorial guides
  its first two steps;
- phones have a five-tab bottom bar.

**Owner decisions in force (2026-09-28). Do not re-litigate them:**
- A starter weapon IS given. This reverses the 2026-09-23 decision.
- The first two tutorial steps are a fence the player cannot click past.
- Season rollover is paused and run by hand.
- The Chronicle pass stays hidden. Its future is **cosmetics** (rare
  avatars, profile frames, skins), not a battle pass.
- The owner is effectively the only player. The ~70 other accounts are
  `smoke:screens` guests, so design for one real player plus friends.
- **Answer the owner in Czech.** Write repo prose in English.

## Order

| # | Task | Size | Needs the owner? |
|---|---|---|---|
| 48 | **Deploy `main`** (season pause, guided start, tab bar, goal ETA) | 15 min | confirm the deploy |
| 49 | Wear a drop from the loot list, compared with what is worn | S | no |
| 50 | A drop worth having LOOKS like one (rare+ reveal) | S-M | no |
| 51 | Personal records, and a toast when one falls | S | no |
| 52 | QoL bundle: remember the screen, hotkeys, offline "continue", Delve lock card, chat count | S | no |
| 53 | Fix the flaky world boss test | S | no |
| 54 | **Cosmetics: avatars and profile frames** | M-L | **design with the owner first** |
| 55 | Boss challenges (rewards = cosmetics) | M | after 54 |
| 56 | Statistics that say how you play | M | no |
| 57 | One book of goals: Deeds absorb achievements, plus a collection log | M-L | show the owner the shape first |
| 58 | Regional contracts | M | the reward size, once |
| 59 | Fewer menu entries: Boosts into Auto-Eat, genetics into one screen | S-M | yes, one line |
| 60 | Screens unlock as they become useful | M | yes |
| 61 | Weekly Deep seed | M | no |
| 62 | Parked until there is a population | - | - |

Recommended batches:
- **48** alone, now.
- **49 + 50 + 51** as one "loot" batch; these are felt every minute.
- **52 + 53** whenever there is a spare hour.
- **54 -> 55**, then **56 + 57**, then the rest.

## How to work (read before any task)

These are the traps that cost time on 2026-09-28. CLAUDE.md has the rest.

- **One branch and one PR per task, cut from `main`, with the PR based on
  `main`.** Never stack PRs: a stacked PR merged after its base lands on the
  dead branch, not on `main` (#69 needed #70 to reach `main`).
- **Verify in this order:**
  1. the server test suite (Docker up; stop the server first).
  2. `npm test` and `npx svelte-check --threshold error` (baseline: 4 errors,
     all in GuildOps).
  3. `run-dev.ps1`, then re-seed the fixture: `--seed-dev` with `--no-build`,
     `FOLKIDLE_ALLOW_DEV_SEED=1` and `FOLKIDLE_DB_CONN` set.
  4. `npm run exercise`.
  5. `check:clipping`, `check:overlap`, `check:touch`, `check:safearea`.
  6. Screenshot the change at 390px and look at it.
- **The stale-build hook blocks the WHOLE shell command** when the command
  text contains the words for building or testing the server while
  `FolkIdle.Server.exe` runs. That includes a PR body or a document mentioning
  them, and a kill command chained in front of it. Kill the server in its own
  call first. Write PR bodies and long prose with the Write tool.
- **A new migration must be applied locally by hand** (`--migrate`) before
  `run-dev.ps1`.
- **Editing files with Python heredocs:** many files are CRLF. Open with
  `newline=''` and match the file's own line ending, or the anchor is not
  found. Never put a backslash escape meant for C# into a Python string; it
  lands as a real newline/CR (it broke the build once). Prefer the Edit tool
  for anything with escapes.
- **Admin endpoints** check `IsAdmin` (username `Mivoru`). The dev fixture
  (`dev`) is NOT admin. To test locally: rename the local `Mivoru` row,
  register a test account named `Mivoru`, test, and rename both back.
- **The deploy** always goes through the `deploy` skill: SSH push, then
  `deploy.sh`, then `smoke:screens` against production. Ask the owner before
  each one.
- **Geometry checkers have produced false positives** (hidden SVGs, rotated
  boxes, the bottom-edge rule). Read the checker before "fixing" the page,
  and fix the checker if it is the one that is wrong.

---

## DONE - 48. Deploy `main` (URGENT, before 2026-11-02)

**DEPLOYED 2026-09-28 as 1.0.836.** Backup `folkidle-20260928T134022Z.dump` first.
`SeasonalEraRecords` era 1: `IsActive = t`, `IsRolloverPaused = t`. `smoke:screens`
27/27 against production, and a guest at 390px met the guided larder step and
the tab bar. The first smoke run FAILED: the guided fence blocked every nav
click for a new guest, so `signInAsGuest` now reports the guided layer and
presses Skip.

Original task:

`main` holds #71-#74; production runs 1.0.826 (#68-#70).
- **Migration `AddSeasonRolloverPause`**: adds a column, then
  `UPDATE ... SET IsRolloverPaused = TRUE WHERE IsActive`. It is additive, but
  take a backup first anyway, per the deploy skill.
- Follow the `deploy` skill.

**Done when:**
- `smoke:screens` passes 27/27 against production;
- `GET /api/v1/admin/season` (owner token) answers `Paused: true`;
- a new guest at 390px meets the guided larder step and the tab bar.

## DONE - 49. Wear a drop from the loot list

**Built 2026-09-28.**
- `ResponseLootDropPacket` carries `InstanceId` (22 -> 30 bytes), stamped
  after SaveChanges, so Wear names the exact row that dropped. Before, it
  would have had to guess among identical pieces.
- `GET /api/v1/player/worn` returns the MAIN character's worn pieces (at most
  11 rows), because that is who `EquipItem` with no target dresses. The
  inventory route is 3.2 MB on a big chest.
- `lootCompare.ts` scales base power by `powerMultiplier`, a mirror of
  `RarityTier.PowerMultiplier` pinned in `serverMirrors.test.ts`. The row
  says "+12 ATK · 2 tiers above your Rare".
- Found on the way, and still open: `Affixes.svelte` shows a piece's base
  Attack/Defence UNSCALED by rarity, so a Legendary and a Normal of one sword
  read the same. Use `pieceTotals` there.
- `exercise` wears a real drop, checks `/player/worn`, and puts the fixture's
  own piece back (`__folkidleEquip`, dev only).

Original task:

**Problem.** A drop lands in `SessionLoot` (Combat, Gathering). Wearing it
takes Character -> slot -> picker -> Wear, and nothing says whether it is
better than what is worn. That is the game's main decision, spread over four
taps.

**Build:**
- Each equipment row in `SessionLoot.svelte` gets "Wear".
- It also gets a one-line comparison against the item in the same slot of
  the fielded character: the attack or defence difference, and the rarity
  step.
- Equip through the same command `Character.svelte`'s `equipInstance` uses.
  Resolve the slot with `resolveSlotIndex`, and mind the ELEVEN slots.
- Show the attribute requirement exactly as the picker does
  (`equipRequirement`, fixed 2026-09-28; region 1 needs nothing).

**Done when:**
- `exercise.mjs` wears a dropped item from the Combat loot list and the
  worn item changes;
- `check:touch` passes.

## DONE - 50. A drop worth having looks like one

**Built 2026-09-28.**
- `lootFeel.ts` decides what a drop earns:
  - Rare+ (tier 4): a row burst, at most one every 5 s;
  - Legendary+ (tier 7): `LootReveal.svelte`, a non-modal card for 1.5 s
    with Wear. It holds on pointer MOVEMENT, not pointerenter, because a card
    that lands under a resting cursor otherwise never leaves;
  - Rare+ plays `lootRare` at `playbackRate` 2^((tier-4)/12), one semitone
    per tier. No new clip.
  - Below Rare, a drop is SILENT (owner, 2026-09-28: "I only want rare loot").
    The plain `lootDropped` clip is DELETED, the mail claim sound included:
    the owner wants no loot sound but the rare one.
- `__folkidleDemoDrop(tier)` (dev only) drives the real `acceptLootDrop`, and
  `exercise` checks that the card appears and leaves.

Original task:

**Problem.** A Legendary arrives as one more row. The audit's "game feel"
section asks for a reveal on Rare and better only.

**Build:**
- Rare+ (use `rarity.ts` thresholds): a short burst in the rarity colour on
  the loot row, at most one every 5 s.
- Legendary+: a card over the screen for about 1.5 s, with the item and a
  Wear button (reuse 49).
- A rising tone per rarity step, through `audio.ts`. Only add clips that exist;
  the owner's sound set is in `resources/`.
- Respect `prefers-reduced-motion`.

**Done when:**
- `check:perf` does not regress;
- a forced Legendary drop on the fixture shows the card (dev tools mail
  endpoint, or a seeded drop).

## DONE - 51. Personal records

**Built 2026-09-28** (`Domain/Progression/PersonalRecords.cs`, migration
`AddPersonalRecords`).
- Highest hit and each region boss's fastest kill are tick facts:
  - they live on the payload and ride `StateUpdate` (805 -> 819);
  - they are merged by BOTH checkpoint paths (max / fastest, never a copy)
    and hydrated at login.
- Best drop: the loot worker writes it AFTER its commit, as one conditional
  UPDATE, and only when the drop beats a per-player cache. The migration seeds
  it from `notable_item_events` (drop sources 1, 2, 3, 9, 10 only).
- Deepest Delve floor already existed. Gold per hour is left to 56, which
  builds the hourly buckets.
- `GET /api/v1/player/records`, and a Records block under Progress ->
  Statistics.
- "New record" toasts come from `stores/records.ts`:
  - the first packet is a baseline;
  - hit records are said at most once per 2 min;
  - drops count only from Rare up, and only once the durable baseline has
    loaded.
- `PersonalRecordsTests` pins the rules, the round trip, the conditional
  write, and every writer's call site plus the route. The route's first edit
  was lost to a hook-blocked command and only exercise noticed.

Original task:

**Server:**
- `PlayerRecords` columns (or a small table) for:
  - the highest single hit;
  - the fastest kill of each boss;
  - the best drop, as rarity and base id;
  - the deepest Delve floor (it exists already);
  - the most gold earned in one hour.
- Write them where the events already pass: the combat event feed, the loot
  worker, Delve.
- The "one hour" record needs hourly buckets. Keep it for 56 if that is
  cheaper there.

**Client:** a toast "New record: ..." from a new wire field or a REST poll.
Use the `add-command` skill for any packet change.

**Done when:** records survive a relogin (see CLAUDE.md, "a field on
StateUpdatePacket must be loaded at login"), and a test pins each writer.

## DONE - 52. QoL bundle

**Built 2026-09-28.** All five, plus one leftover:
- `lib/net/prefs.ts` wraps every storage access; the last screen (`App.svelte`),
  the Chest's tab and rarity floor, and the Wiki tab are remembered and
  validated on read. Sign-out writes `hub`, so the next account starts at the map.
- Keys 1-5 follow `lib/ui/tabs.ts`, the same list the phone's TabBar draws;
  ignored in any field and with a modifier held (`tests/qolBundle.test.ts`).
- `OfflineSummary`: "Stock the larder" when halted for food or the larder is empty.
- Delve: a player who cannot pay the gate gets one card with the price, their
  gold and a progress bar; "How the Delve works" opens the full screen.
- The chat handle shows the online count and fades at 0.
- Leftover: `Affixes.svelte` showed an owned piece's AUTHORED Attack/Defence,
  the same at every rarity. It now scales by quality through `pieceTotals`,
  the arithmetic the loot comparison already used.

Original task:

Every item here is independent. Each is a sub-PR or all in one.
- Remember the last screen, the Chest filters and the Wiki tab in
  localStorage, wrapped in try/catch.
- Desktop hotkeys: 1-5 for the five tabs' screens, only when no input has
  focus.
- `OfflineSummary`: when the halt reason is out of food, a button "Stock the
  larder" (`requestScreen('larder')`).
- Delve before the player can afford it: one card, "Opens at 7,000 g - you
  have X", instead of the full screen.
- The chat handle shows how many players are online and fades at 0.

**Done when:** `exercise` stays green and the geometry checkers stay clean.

## DONE - 53. Flaky: Test_WorldBoss_AnOldSessionAndYesterdaysStrikeRefuseNothing

**Fixed 2026-09-28, then fixed properly the same day.** The first fix made the
test assert on the player's own `TotalInflictedDamage` rather than the shared
boss HP - and it failed again on main at 16,000 vs 6,000. The real cause was
the strike's default plate 0: the weak plate is re-seeded randomly on every
`ActivateEventWindowAsync`, so one run in `PlateCount` tripled the blow. It
now steps past the seeded weak plate, as the test above it already did. The
test asserts on the player's own `TotalInflictedDamage`
now, not on the shared boss HP. Two exercise checks had the same flaw:
- the new-account strike read global HP, which LiveOps rescales within its
  10 s window (seen both ways: 75M -> 150M and 75M -> 50M). It reads the
  player's own row on `/worldboss/board` now;
- the loot panel had 25 s to fill, and passing runs held exactly one row by
  then. It has 60 s now.

Original task:

It failed once in a full run on 2026-09-28: the boss HP was off by exactly
another test's 10,000-damage strike. It passes alone. The boss HP is
static/shared across tests in the "Postgres collection". Either serialise the
world boss tests (their own collection) or assert on the damage this test's
player dealt instead of a global HP delta. Read the CLAUDE.md paragraph on
static queues first.

## DONE - 54. Cosmetics: avatars and profile frames

**Designed with the owner 2026-09-28**, plan:
`docs/superpowers/plans/2026-09-28-task-54-cosmetics.md`. Built the same day:
- `CosmeticRegistry` (4 chests, 25 monster-portrait avatars, 16 drawn frames),
  `cosmetic_items`, and three `PlayerRecords` columns.
- Chests from kills (live and offline, in the loot worker's transaction, at the
  twin item tier's rate - about 1 in 2,622 kills for Common, 1 in 131,111 for
  Legendary) and one per five levels (`CosmeticGrantEngine`, idempotent; the
  first login after release is the backfill).
- The Wardrobe screen, `Avatar`/`PlayerAvatar`, and faces in chat, both
  boards, the guild roster, the world boss board, the profile and Character.
- `CosmeticTests` (server), `tests/cosmetics.test.ts` (client mirrors),
  `exercise.mjs` opens and wears a chest and restores the face.

Phases 1-3 DEPLOYED 2026-09-28 as 1.0.862.

**Phase 4, the market (built 2026-09-28):** the Market's Cosmetics tab lists,
buys and takes down chests and cosmetics at the seller's own price (no
corridor, owner decision). `CosmeticMarketEngine`, its own
`cosmetic_market_listings` table (not `MarketOrderRecords`, whose readers all
assume equipment), the guild licence, and the fee and guild tax through
helpers shared with the equipment market (`MarketEscrowEngine.WealthFeeRate`,
`ApplyGuildSalesTaxAsync`). A listed item stays the seller's row with
`IsListed`, so it cannot be opened, worn or listed twice. `CosmeticMarketTests`;
`exercise.mjs` lists and takes down; the checkers visit the tab as the
`Market · cosmetics` overlay.

Original task:


This is the owner's replacement for the Chronicle pass: rewards that do not
add power (see `PowerCeilingTests`).

**Questions to settle with the owner before any code:**
- Where cosmetics show: the profile modal, chat names, leaderboards, the
  Character figure.
- Where the art comes from: `client/` artwork, generated art, or CSS frames
  first.
- How they are earned: deeds, boss challenges (55), Deep titles, the world
  boss payout.
- Can any be bought? Store rules and IAP apply if yes.

**Likely shape:**
- A `player_cosmetics` table (owned ids) and an equipped avatar/frame on
  `PlayerRecords`.
- A catalogue in GameData JSON (content validation hook).
- A picker on Character or Settings.

**Done when:** it is written as a plan in `docs/superpowers/plans/`,
agreed, and then built.

## DONE - 55. Boss challenges

**Built 2026-09-28.** Reward: a cosmetic chest per challenge, once - Rare for
regions 1-2, Epic 3-4, Legendary 5 (Claude's recommendation; the owner said
"continue" without picking). `BossChallengeRegistry` / `BossChallengeEngine`,
`boss_challenge_completions`, judged off the tick by `CosmeticGrantEngine`
(the tick only records the fight), listed under each region on Combat.

**Measured, then asserted** (`BossChallengeCalibrationTests`, with
`BossGearBenchmark.ProjectChallenge` - optionally with no food): against a boss already beaten once, at the wall's required gear, the
lowest winning level is 6 / 26 / 32 / 38 / 46 fed and 20 / 54 / 72 / 85 / 97
unfed. So challenges count on ANY kill - unfed is impossible on Malakor's first
clear at every level. Level caps are the midpoint to the reference level:
15 / 35 / 45 / 60 / 75.

**Third challenge replaced before release (owner, 2026-09-28):** "Humble blade"
(a Common weapon) asked nothing - a plain weapon still won every fight at the
reference level, because armour carries it. It is **Swift** now, a time limit
per boss: 80 / 110 / 120 / 110 / 110 s. Measured kill times on a beaten boss at
the reference level: the wall's required gear takes 82 / 115 / 135 / 127 / 122 s
(fails), the region's best gear at that level (quality +4) 76 / 106 / 117 / 105
/ 100 s (makes it), and the next region's gear roughly halves the time. The
fight's length is `CombatTargetTickAccumulator`, the same clock as the fastest
boss kill record.

**Not done:** the Book of Deeds line. Adding a deed to a chapter re-locks the
Seal progress of anyone partway through it, and task 57 reworks the Book - do it
there. Live kills only: the offline catch-up does not fight bosses one by one.

Original task:

Three optional conditions per region boss:
- no food in the larder;
- under level N;
- weapon rarity at most R.

The server evaluates them at the boss kill, where it already knows the
level, larder and gear. The reward is a cosmetic (54) and a line in the Book
of Deeds.

**Check every challenge by simulation.** The larder is the difficulty
ceiling (memory), so "no food" may be impossible for some bosses; measure it
with `ProgressionRateTests`' model before shipping.

## DONE - 56. Statistics that say how you play

**Built 2026-09-29 (with 57, one PR - both rebuild the Progress screen).**
`StatSampler` writes a ten-minute sample of every ONLINE account's cumulative
counters (kills from the codex, XP as level + progress, gold balance, harvests
from the Logistics row, crafts) plus what its three played characters are
doing, into `player_stat_samples` (kept 8 days). `/api/v1/player/insights`
turns them into per-hour rates over 10 min / 1 h / 24 h (closed by a reading
taken at request time), "your style" (fighting / gathering / crafting / idle
over 24 h and 7 d), gold earned vs spent (rises and falls of the balance -
honest label; a per-category gold ledger is NOT built) and a timeline from
`player_funnel_events` plus the best drop. Progress -> Statistics tab.
Offline players are not sampled; their away earnings land in the next window.


Progress -> Statistics is 12 flat numbers. Build:
- **rates** over 10 min, 1 h and 24 h: gold/h, XP/h, kills/h, materials/h;
- **records** (51);
- **a timeline**, from `player_funnel_events` (12 milestones per player,
  already written) plus deeds sealed;
- **"your style"**: time split between combat, gathering and crafting, and
  gold earned versus spent by category.

The server needs hourly aggregates per player (about 24 rows a day). The
same data can later feed an economy view.

## DONE - 57. One book of goals, and a collection log

**Built 2026-09-29 (owner: "make the best design you can and implement it; I
will critique it after").** Progress is four tabs: Goals / Collection /
Statistics / Daily & races.
- **Goals**: the Book of Deeds plus a **Lifetime** chapter - the four
  achievements as named tiers (Treasury: Purse/Coffer/Vault/Hoard, Master
  Smith: Apprentice..Grandmaster, Logistics: Porter..Logistician, Monster
  Slayer), goals and diamonds read from `AchievementMilestones`' own tables.
  No claim button. A **boss challenges** line (x / 15, link to Combat), and
  six **hidden deeds** shown as "???" + category until done (no reward).
- **DEFECT FOUND AND FIXED:** tiers 2-4 were paid by the checkpoint AND the
  Progress screen still offered "Claim tier N" (IsClaimed never set), and the
  claim paid the same tiers a second time. Monster Slayer's reward table was
  empty, so its claim paid nothing. Now: the claim queue is drained and
  ignored; Monster Slayer (100 diamonds at 10,000 codex kills - lowered from
  an unpaid 500, see AchievementMilestones.MonsterKillReward) is paid once
  on reading the Book (conditional UPDATE, safe for concurrent GETs); the old
  snapshot reports a paid tier as claimed. Worth checking prod for how many
  double payouts happened (player_lifetime_achievements IsClaimed = true on
  ids 2-4).
- **Collection**: `player_collection` keeps the best rarity ever owned of each
  of the 75 `eq_` pieces. Written by the loot worker after its commit
  (coalesced per worker) and folded from the Chest on read, so crafted, fused
  and bought pieces count. Per region: pieces x/15, a rarity bar, codex x/5,
  and one headline percentage.


Two parts:
- **Achievements into the Book of Deeds.** The four tiered achievements
  (Monster Slayer, Treasury, Master Smith, Logistics) become a "Lifetime"
  chapter with named tiers. Pay rewards automatically; there is no claim
  button, same as deeds. Add hidden deeds, shown as "???" with a category.
- **A collection log.** For every canonical item, record the highest rarity
  ever owned, written by the loot worker. Mind the drain budget and coalesce
  writes. Add the Codex, and show a percentage per region.

Show the owner a screenshot mock of the new Progress screen before building.

## DROPPED - 58. Regional contracts

**Owner, 2026-09-29: "regionální zakázky bych nedělal" - not doing it.** Kept below for the record.

Each unlocked region has 3 contracts, for example:
- kill N of a monster;
- deliver N of the region's log or ore;
- craft or fuse an item of rarity R+.

A finished contract is replaced; contracts never expire, and a region holds
at most 3. The reward is gold and materials worth about 1.2-1.5x the farm
time it costs; confirm the number with the owner once. Build it on the
counters `DeedContext` already reads. This gives old regions and surplus
gold a purpose (the Wiki itself says players end seasons with more gold than
they spent).

## DONE - 59. Fewer menu entries

**Built 2026-09-29 (owner: "souhlasím").** Supplies = Auto-Eat + Boosts (under
Items); Bloodline = Breeding + Ancestors + Inheritance (under You; the Genetics
group is gone). Client-only: each screen KEEPS its key and becomes a tab under
one menu entry (`TAB_FAMILIES` in App.svelte), so the tutorial, the guided
'larder' step, `requestScreen`, the back stack and the remembered screen all
work unchanged. 27 -> 24 menu entries. `screens.mjs` visits the tabs as
overlays ('Supplies · Boosts', 'Bloodline · Ancestors', 'Bloodline ·
Inheritance'); `exercise.mjs`'s `go('Breeding')` etc. take the entry then the
tab. The Wiki ledger is keyed by route file, which did not change.


Both merges are client-only:
- **Boosts into Auto-Eat**, as one "Supplies" screen with tabs. The chrono bank
  is already gone, and Boosts is one small panel.
- **Breeding, Ancestors and Inheritance into one "Bloodline" screen** with
  three tabs.

Update `screens.mjs` (`SCREENS`), the Wiki screen ledger (`wiki.test.ts`)
and the geometry checkers' lists. Ask the owner first: it renames things
they know.

## DONE - 60. Screens unlock as they become useful

**Built 2026-09-29.** Owner: "not sure anything should be locked, but if so,
do it". Greyed in the menu with the condition beside the label, never hidden:
Forge (level 5 or a built Forge), The Delve (7,000 gold), Market and Guild
(level 10, or already in a guild), Bloodline (Breeding Grounds, a Veteran
character, or 40 diamonds). The rules live in `lib/ui/unlocks.ts` and are the
SAME predicates as the discovery moments, so each moment is that screen's
one-time "new" card (Market's moment moved from 5,000 gold to level 10; a
Delve moment was added). A screen whose card was seen stays open, because gold
and a season reset go back down; the seen-set is already server-synced. It is
a menu decision only - links from other screens still open a locked screen,
and objectives never point at a greyed entry. `smoke:screens` reports a locked
entry as "locked for this account" instead of clicking it; exercise asserts
the fixture sees nothing greyed and a new account sees Market = Level 10.


A new player sees 27 destinations. Hide a screen from the Menu (show it
greyed, with its condition) until it is useful:
- Forge: two items for one slot;
- Delve: 7,000 g;
- Breeding: Breeding Grounds;
- Market and Guild: level 10.

Show a one-time "New: Forge - ..." card when one opens. Derive every rule
from state, as the tutorial does; nothing stored. The fixture must see
everything, which is a check in itself. Ask the owner which screens.

## DONE - 61. Weekly Deep seed

**Built 2026-09-29.** Every Deep floor (9+) takes its doors from
`DelveRegistry.WeeklyDeepFloorRandom(weekKey, floor, attempt)`, keyed on the
ISO week the RUN started in (a descent across Sunday midnight keeps its
course) and on the run's charges/lanterns at the roll, so two players in the
same state meet the same doors. Seeded: what each door wants and the number
its reveal is compared against. NOT seeded: the pass roll (a memorised
course says which door to try, never that it opens), and every price -
tolls, lanterns, payouts are untouched. Floors 1-8 are NOT seeded: they pay
diamonds, and a layout learnable over a week would beat the reveal chance
the diamond tap was calibrated on.
- Best of the week already existed (`DelveDeepestThisWeek`, the weekly
  board). Added `DelveDeepestLastWeek` (migration `AddDelveDeepestLastWeek`,
  additive), carried over in `RollWeek` only when the stored week is the one
  before; the view also carries `DeepWeekKey` and `DeepWeekEndsUtc`.
- The Delve screen shows "Last week" and a line saying this week's Deep is
  one course for everyone until Monday.
- `DelveWeeklySeedTests` (same week = same floors for two players, next week
  differs, the generator is pinned, last-week carry-over, a month away = no
  last week). Full suite 1299/1299, exercise 215/215.
- exercise's "walking out of the Deep pays nothing" read the gold BALANCE
  across a relogin, which drifts with passive income; it failed twice on a
  Deep that paid exactly 0 (checked against the API by hand). It now reads
  the server's answer to the Walk out click.


The Deep's floors come from a seed per ISO week, the same for everyone, so a
player races their own last week (and friends, when there are some). Store
the best result per week. Read task 37 and `DelveRegistry` first; the Deep's
economy is measured (task 37 Phase 3), so do not change payouts.

## 62. Parked until there is a population (about 30 active per week)

- Seasons with a modifier (O3's later half).
- The share of players holding each deed.
- Guild Wars (task 38).
- Larder auto-refill: demoted, because a slot holds 9,999 bites (plan doc
  2.1).
- A time estimate on Village cost lines: it needs the gathering rate formula
  on the client, so ask for a server figure first.
- Tag `smoke:screens` guests so the funnel excludes them. The owner knows the
  population is one, so this only matters once there are strangers.

## DONE - 63. Armour set bonuses must actually pay (owner decision 2026-09-28)

**Built 2026-09-29.**
- `ArmourSetRegistry.SetIdOf(baseId)` is the ONLY source of a piece's set id:
  `(tier - 1) * 2 + 1` for the tier's light family (offence), `+ 2` for the
  heavy one (defence). `EquipmentSlotEngine.ComputeEquippedTotalsAsync` reads
  it instead of the dead `EquipmentInstance.SetId` column, which feeds all four
  call sites (live equip, relogin hydration, parked slot, offline catch-up).
- A second defect on the way: `SetBonusEngine` only knew TWO set ids (1 and
  10) while items.json authors TEN families, so eight would have paid nothing
  even with the id written. Every family now pays its archetype: offence
  +8/15/26% damage, burn at 5; defence +10/18/32% armour, thorns and bulwark
  at 5; times the average-rarity scale (0.4-2.0).
- The Character screen used to invent "+10% armour / +15% damage / unique
  passive" for every family. It now reads `RosterCombatStats[].ActiveSets`
  from `/api/v1/player/inventory` - the server's own evaluation. Wiki updated.
- Tests: `ArmourSetTests` (one offence + one defence per tier, all ten ids pay
  through `StatsCalculator`), the two equip-pipeline tests now use real BaseIds
  and no `SetId` column. Full suite 1306/1306, exercise 214/214.

**Measured (for the owner's retune decision - NOT acted on):**
- `PowerCeilingTests`: its set lever read the WEAKEST full set (id 1 at quality
  0, floored to x0.4). Now Transcendent Linen: **1.52x** (was 1.10x); total
  ceiling 4,324x, 5.8x headroom over the 750x monster ladder. Passes its shapes.
- `BossWallTests.SetBonusEffectOnTheWall_Report` (5-piece set at the boss's
  required quality): an offence set cuts kill time about a third (region 5:
  1190 s -> 783 s); a defence set changes nothing at the requirement (the
  reference character already never dies there).
- **The one wall that moves:** a region-1 DEFENCE set at Transcendent now beats
  the region-2 boss (384 s, never dies) - bare it dies in 16 s. Regions 3-5
  still hold one region behind with either set (defence only survives longer:
  38 / 96 / 22 s).
- **Retuned 2026-09-29 (owner: "make the monsters and bosses harder, then"):**
  only the region-2 wall needed it. At quality 7 there was NO attack
  multiplier where region-2 gear at the bar won and a region-1 Transcendent
  defence set lost (the fights are binary: the larder sustains you or it
  does not). Region 2 now asks for quality 8 and its first-clear attack is
  2.78x (was 2.6x; measured window 2.75-2.80). `BossWallTests` now asserts
  the "one region behind loses" rule WITH both of that region's sets at
  every quality. Regions 1, 3, 4, 5 already held with sets - unchanged.
- **Regular monsters NOT retuned, on purpose.** Sets only speed kills (up to
  1.52x at Transcendent, about 1.26x at Rare), and the progression gate is
  the boss wall, which now holds. Faster farming with a finished set is the
  reward for building it.
- `ProgressionRateTests`' model wears no set, so its pacing figures do not
  move; a real player with an offence set kills up to 1.52x faster.

**Owner decision, 2026-09-28:** "I want armour sets to work, it feels only
right. If we need to boost monsters later, we will do it." So: make the bonuses
pay first, and retune the monster side afterwards only if the measurements say
so. Do not hold the fix back to balance it in the same change.

**What is actually true (checked 2026-09-28):**
- `EquipmentSlotEngine.cs:737` builds `EquippedSetIds` from
  `EquipmentInstance.SetId`, which **nothing ever writes** - 0 of 945 live
  items carry one. That feeds `CachedSetIds` on the payload (live equip,
  `StateCheckpointManager` hydration at ~1383 and ~1613, the parked-slot swap)
  and `SetBonusEngine.Evaluate` inside every `StatsCalculator.Calculate` call.
  So no set bonus has ever paid anyone.
- `ArmourSetRegistry` already knows every piece's set family from its BaseId.
  PR #79 fixed the "Wear two pieces of one set" deed by counting families
  there instead of `SetId` (`DeedProgressSource.cs:188`). The bonus should read
  the same source - one definition of "which set is this piece", not two.
  `ArmourSetRegistry.cs:24` warns that its ids are NOT the same numbers as
  `EquipmentInstance.SetId`, so check the numbering `SetBonusEngine` expects
  before wiring them together.

**Done when:**
- a character wearing 2 / 4 pieces of one family gets the bonus
  `SetBonusEngine` defines, on the live tick, after a relogin, on a parked
  slot, and in the offline catch-up (all four call sites);
- the Character screen shows the active bonus;
- `PowerCeilingTests` is rerun and its set-bonus lever is read in the output
  (it must still pass its shape rules), and `BossWallTests` /
  `ProgressionRateTests` are rerun so the effect on the boss wall and pacing is
  written down here;
- a monster retune is a SEPARATE follow-up, only if those numbers ask for it.

## DONE - 64. Ordinary kills: lower the diamond chance to 0.01% (owner decision 2026-09-28)

**Built 2026-09-29:** `SimulationEngine.OrdinaryKillDiamondChance = 0.0001`, read through
`OrdinaryKillPaysDiamond`; `OrdinaryKillDiamondTests` pins the constant and a seeded week of
one kill a second (~60, the Delve's ceiling). `BossDiamondTests` still passes.

**Owner decision, 2026-09-28:** the chance of 1 diamond on an ordinary
(non-boss) kill goes from **0.05% to 0.01%**. Regional bosses pay none at all
since PR #87.

**Why:** at 0.05%, a character killing about one monster a second earns about
300 diamonds a week - five times the Delve's calibrated 60 a week
(`DelveRegistry.MaxDiamondsPerWeek`). At 0.01% that is about 60 a week at the
same pace, in line with the Delve.

**Where:** `SimulationEngine.cs`, the `Random.Shared.NextDouble() < 0.0005`
roll beside the boss-diamond comment (after PR #87). The offline catch-up pays
no diamonds, so there is no second site.

**Done when:** the constant is named and says why, and a test pins it (for
example: a large simulated run of ordinary kills stays within the expected
band, and `BossDiamondTests` still passes).

## DONE - 65. Store: simulation speed and diamond packs removed (owner decision 2026-09-28)

**Owner:** "Why is there a simulation speed option in the Store? We get the
same XP and items offline and online, so delete simulation speed completely.
Delete the diamond packs too and just say the Store has nothing yet."

- **Simulation speed, removed end to end.** The 1x-4x buttons replayed
  `AccumulatedTimeBankMs` at up to 4x - and nothing had filled that bank since
  the chrono deletion (2026-09-02). Nine live accounts still held a leftover
  balance, the largest ~584,000 s (6.8 days) of 4x progress. The 3x button never
  worked at all (the server accepted 1, 2 and 4). Gone: the tick's extra
  iterations, the validator's speed check, `TickStatePayload.SpeedMultiplier` /
  `AccumulatedTimeBankMs`, the Redis frame field, the checkpoint writes, the
  season-reset SQL, `StateUpdatePacket.AccumulatedTimeBankMs` and
  `CurrentSimulationSpeedMultiplier` (819 -> 810), and the
  `PlayerRecords.AccumulatedTimeBankSeconds` column (migration
  `RemoveSimulationSpeedTimeBank` - **destructive, back up first**). Opcode 8 is
  retired and ignored (`HandleRetiredSimulationSpeed`) so an old phone build
  pressing it is not disconnected.
- **Diamond packs, removed from the screen.** They listed products with no
  price and no way to buy on the web. The Store says "Nothing here yet". The
  purchase path (`billing.ts`, `/api/v1/billing/*`, store registration) is
  untouched, for when payments are set up (Stripe is still parked).

## DONE - 66. Background music, separate volumes, quiet Forge, centred World Boss (owner 2026-09-28)

- **Music.** The owner's three soundtracks (`D:\FolkIdleSounds\soundtracks`)
  live in `client/Assets/Resources/Audio/Music/` - NOT in LFS (the box has no
  git-lfs; see `.gitattributes`), checked by `ops/validate_audio.py`. The server
  lists them at `/audio/music` (the client keeps no track list) and streams
  `/audio/music/<name>.mp3` with Range support. `lib/ui/music.ts` plays them
  through one `<audio>` element everywhere in the game, starting on the first
  gesture after sign-in, pausing when the app is hidden. Settings: separate
  **Sound effects** and **Music** volumes (defaults 0.35 and 0.15), "play the
  ticked songs in turn" or "repeat one song", a tick per track and a Play
  button. Rules in `musicPlaylist.ts`, tested in `tests/music.test.ts`.
- **Found on the way:** `audio.ts` read a MISSING volume as 0 (`Number(null)`),
  so every new player started with sound effects silent and saved that 0. Read
  fixed; the key moved to `folkidle.sfxVolume` so everyone gets the default once.
- **The Forge is silent.** Result codes 10, 16, 19, 20, 23, 24, 25 make no tone
  (`COMMAND_RESULT_SILENT_CODES`). The error tone also stopped firing on good
  news (auto-reroll "stop condition met", a birth), which it did because it
  tested `!== Success`. `tests/resultTones.test.ts`.
- **World Boss** column centred on wide screens.

## DONE - 67. Turning on notifications crashed the Android app, on every launch after (owner 2026-09-29)

**Cause:** the build has no `google-services.json`, so `PushNotifications.register()`
throws "Default FirebaseApp is not initialized" inside the NATIVE plugin and
kills the app - no JavaScript catch can see it. `refreshDeviceTokenIfPermitted`
re-registers at every sign-in once permission is granted, so one press became a
crash on every launch until the permission was revoked in Android settings.

**Fix:** `push.ts` never calls `register()` unless the build sets
`VITE_FOLKIDLE_PUSH=1`; without it the Settings button is replaced by "Push
notifications are not set up yet" (the local resting reminder, which needs no
Firebase, stays). `tests/push.test.ts` pins that `register` is never reached.

**To turn push on later:** add `android/app/google-services.json` (and
`GoogleService-Info.plist` for iOS) from a Firebase project, give the server its
FCM credentials, and build with `VITE_FOLKIDLE_PUSH=1` - the phone bundle is
built by Docker on the box, so the variable goes in `ops/oracle/docker-compose.yml`
beside `VITE_FOLKIDLE_SERVER`.

---

# Tasks 68-88: the 2026-09-29 second design audit

Added 2026-09-29 from the second audit (claude.ai artifact
5di35DqxEfb5zpNUh5Sxsy, Czech). It was built from the code, 51 screenshots
(dev fixture at 390 and 1366 px, a new guest) and read-only production
queries. It builds on 48-67 and does not repeat them.

**The two findings everything below hangs on (production, 2026-09-29):**
- **The top of the economy stops asking questions.** The owner's account
  (level 97, 77 h online) holds 202,420,654 gold, 1,831,272 frostpine logs,
  1,670,862 silver ore and ten more stacks over 100,000. The most expensive
  village level costs `100 * 1.5^(level mod 5)`, at most 507 of a material.
  Lifetime: 31,981 affix rerolls, 387 fusions, 57 crafts. The endgame is one
  loop, the reroll.
- **Six of 24 menu entries wait for players who are not there.** Market,
  Guild, Friends, Leaderboards, Chat and the world boss percentiles. The
  population is the owner plus friends (owner, 2026-09-28).

**Owner decisions in force:** everything listed under "Owner decisions in
force (2026-09-28)" in the 48-62 section. Answer the owner in Czech; repo
prose in English. Loot below Rare stays silent.

## Order

| # | Task | Size | Needs the owner? |
|---|---|---|---|
| 68 | ~~Three screens state rules the game does not have~~ **DONE, PR #102** | S | no |
| 69 | ~~Fuse a whole stack in one action~~ **DONE, PR #103** | S-M | no |
| 70 | ~~"New" cards stop covering the screen~~ **DONE (narrowed), PR #104** | S | no |
| 71 | ~~Chat handle off the controls; empty Store out of the menu~~ **DONE, PR #105** | S | no |
| 72 | ~~A locked region says what is missing; Continue and Again buttons~~ **DONE, PR #106** | S | no |
| 73 | Home answers "what now" before the painting | M | show a screenshot first |
| 74 | Small bundle - **3 of 5 DONE, PR #107**; number format and reroll history left (see the task) | S-M | no |
| 75 | Book of Deeds: chapters II-IV open together | S | **yes, one line** |
| 76 | One "Community" menu entry | S | **yes, one line** (renames) |
| 77 | Village "Work slots": verify, then remove or collapse | S | no |
| 78 | Hunting advisor: honest estimates on every monster | M-L | no |
| 79 | Gold ledger and material flow; Treasury counts gold spent | M | Treasury: **yes** |
| 80 | Breeding Grounds above level 1 | S | **yes, which option** |
| 81 | Chest rules and row actions | M | no |
| 82 | Desktop header as five groups | S-M | show a screenshot first |
| 83 | **BUILT** (branch `claude/tasks-83-84-85`) - Workshop commissions (the material sink) | L | decided 2026-09-30 |
| 84 | **BUILT** (branch `claude/tasks-83-84-85`) - Great Works (the long material sink) | L | decided 2026-09-30 |
| 85 | **BUILT** (branch `claude/tasks-83-84-85`) - Orders: automation rules as a reward | L | decided 2026-09-30 |
| 86 | A deterministic affix step beside the reroll | M | **design with the owner** |
| 87 | Boss Ascension ladder | M | reward shape, once |
| 88 | **BUILT** (branch `feat/88-rebirth`) - Rebirth on demand instead of a calendar season | XL | decided 2026-09-30 |

Recommended batches: **68 + 69 + 70** first (felt every session, no owner
input), then **71 + 72 + 74**, then **73 + 82** (one look at the shell), then
**78**, then the design tasks 83-88 one conversation at a time.

---

## DONE - 68. Three screens state rules the game does not have

**Built 2026-09-29, PR #102.** All three texts corrected; `tests/truthfulTexts.test.ts` reads ForgeSplicingEngine.cs and the Svelte files so the next drift fails.

**What is actually true:**
- `Gathering.svelte` prints the Mastery line as `-{level * 2}` ("-112
  mining") and says mastery "cuts two ticks per gather". That rule was
  retired; the server adds `40 * sqrt(level)` percent speed
  (`GatheringToolEngine.GetMasterySpeedBonusPct`), and the same screen's rates
  already use it (`masterySpeedPct`, pinned by `serverMirrors.test.ts`).
  Mastery 56 is +299 %, not "-112".
- `EventBanner.svelte` says Diamond Star gives "+5 percentage points forge
  success". Fusion has no roll since 2026-09-06; `ForgeSplicingEngine` turns
  the event into 5 % off the fee (`feeDiscount + 0.05`).
- `OfflineSummary.svelte` advises "leave a harder monster running and this
  goes up". XP and gold per kill are proportional to monster HP, so the rate
  is the player's DPS; a harder monster's armour and dodge lower it.

**Build:** correct all three. The mastery line reads `masterySpeedPct`. Add a
vitest in the style of `wiki.test.ts` that reads the event effect out of
`ForgeSplicingEngine.cs` and fails if the banner's text drifts from it.

**Done when:** the three texts are true, the guard test passes, and a grep for
`forge success` and `two ticks` in `client_web/src` finds only comments.

## DONE - 69. Fuse a whole stack in one action

**Built 2026-09-29, PR #103.** Opcode 78 `FuseStack` (TargetId = any piece of the stack, QualityTier = stop tier), `ForgeSplicingEngine.PlanStack` (pure) + `ExecuteStackFusionAsync` (one Serializable transaction, the single fusion's fee/ceiling/lock rules, max 10,000 fusions), `GET /api/v1/forge/stack-preview`, the Forge's "The whole stack" panel, dev route `/api/v1/dev/forge/stack` (grants 9 Normal Doom Gorgets via DevFixtureSeeder). `ForgeStackFusionTests`; exercise fuses 9 -> 1 and bins it. Side effect: one press on a big stack reaches Master Smith / "Fuse fifty times" at once.

**What is actually true:** fusion is three selects and a button, one fusion
per press (`ExecuteFusionAsync(target, sac1, sac2)`). The Forge lists "Ready to
fuse" chips; the audit said they do nothing when clicked, which was WRONG -
they fill the single fusion (`pickSet`). The dev fixture holds 7,550 Normal
Birch Axes; fusion is deterministic 3:1, so the only decision is how far up.

**Build:**
- Server: a batch that fuses one BaseItemId from rarity A up to rarity B (at
  most the Forge's ceiling), in one transaction, charging each step's fee
  through the same code the single fusion uses. It never consumes a locked or
  equipped item, and it stops cleanly when gold runs out. It returns what it
  made (counts per rarity, gold spent).
- A preview (read-only) of the same plan: fusions, gold, the result.
- Client: a click on a chip fills the single fusion; each chip also gets
  "Fuse up to..." with the preview and one confirm.
- Use the `add-command` skill if it goes over the wire; a REST POST under the
  account stripe lock is also fine (CLAUDE.md, mutating REST handlers).

**Done when:** a server test fuses 30 Normals up to Rare in one call (3 Rare,
the right gold charged, locked pieces untouched, one row per result);
`exercise.mjs` fuses a stack and the chest count moves by the expected amount
and is restored; `check:touch` passes.

## DONE (narrowed) - 70. "New" cards stop covering the screen

**Built 2026-09-29, PR #104:** a discovery/objective card folds to its title line on the next screen.

**CORRECTED 2026-09-29, before building.** The audit said the card sits "in
the middle of every screen". It does not: `OnboardingCoach` is
`position: fixed` at the BOTTOM, reserves body padding, and starts folded on a
phone. The audit's full-page screenshots drew the fixed panel at its viewport
offset, mid-page - the same artefact the 2026-09-28 plan already recorded as a
correction. So the toast redesign below was dropped.

**What IS true (a viewport screenshot at 1366x900):** on a desktop an
expanded discovery or objective card stays open across every screen until
"Got it", covering the bottom ~135 px of the view (Gathering's node lists, the
Forge's list) until the player scrolls.

**Built instead (small):** once the player moves to another screen, a
discovery or objective card folds to its title line (one tap re-opens it).
Tutorial steps are untouched. `exercise.mjs` opens a folded card by its
header before pressing "Got it".

The original task follows.

**Build:** a discovery becomes a toast in the corner (about 4 s, held while
the pointer moves over it, like `LootReveal`) with "Take me there", and it is
listed under a "New (n)" entry until opened. The guided first two tutorial
steps are NOT touched (owner decision: a fence).

**Done when:** no discovery covers a control (`check:overlap` clean on every
screen with a pending discovery), the seen-set still syncs, `exercise.mjs`
stays green.

## DONE - 71. Chat handle off the controls; empty Store out of the menu

**Built 2026-09-29, PR #105.** Quiet chat (nobody ELSE online - the count includes you - and nothing unread) moves to a header button; Store hidden via `MENU_HIDDEN`. Found on the way: `fetchOnlineStats` was a bare relative fetch, so the count read 0 on the dev box and in the APK; it uses `authedGet` now.

- The floating "Chat · 0" handle sits over Gathering's Gather button at
  1366 px. At 0 online it becomes a header icon; otherwise the main column
  reserves the corner.
- The Store says "Nothing here yet" (owner, 2026-09-28). Hide its menu entry
  until it has a product; keep the route.

**Done when:** no chat handle overlaps a button at 390 / 1366 px (measure it;
`check:overlap` does not see the handle as a page control, so extend it), and
`screens.mjs` / the Wiki ledger reflect the Store change.

## DONE - 72. A locked region says what is missing; Continue and Again buttons

**Built 2026-09-29, PR #106.** `bossGearProgress` (victories.ts) + "You wear N of 8" under the locked region; Combat's rules text read the retired flat 5x/2x wall and now reads the mirrored first-clear ranges; "Continue: <last monster>"; death card Again / One easier (not exercised - a death is random).

- A locked region's banner names the boss and the gear it asks for, against
  what the player wears ("Magma Wyrm asks for Mythic gear; you wear Epic").
  The requirement is already server data (`BossFirstClearRules`); send it, do
  not copy it into the client.
- Combat's idle state ("Not in combat.") offers "Continue: <last monster>"
  for the fielded character (remember it in `prefs.ts`).
- The death card offers "Again" (same monster) and "One easier" beside the
  existing Auto-Eat / Combat links.

**Done when:** `exercise.mjs` presses Continue and a fight starts; a new
account's locked region shows the requirement.

## 73. Home answers "what now" before the painting

**What is actually true:** at 1366 px the painted map is ~745 px tall and the
"Right now" / "Closest goal" cards are below the fold. "Right now" shows race
names ("Human · Idle"), not the characters' names, and "Give a job" goes to
Character's native select plus Assign (a known Android trap,
`client_web/CLAUDE.md`).

**Build:** cards first, map as a short strip below them. Character names.
"Give a job" = one tap for the character's last activity, two for a picker
(no native `<select>`). Add an offline-cap line ("the 12 h offline limit fills
in 9 h 40 min") and a "Next unlock" line (the next region's boss and what it
asks for, from 72's data). `exercise.mjs` clicks the map plates; keep them.

**Done when:** at 390 and 1366 px the cards are in the first viewport; the
owner has seen a screenshot; the geometry checkers are clean.

**BUILT 2026-09-30, awaiting the owner's look at a screenshot.** Cards render
before the map; the map is a capped 40 rem strip below (plates and `.scene`/
`.place` selectors unchanged). "Right now" shows character names (breeding
roster). Idle characters get "Continue: <last job>" (per-character pref, slot 1
falls back to Combat's last monster) and a button picker (Fight/Gather; crafting
sends to Character). Offline line is the server's `OfflineCapSeconds` only -
time already away is NOT on the wire, so the "fills in 9 h 40 min" countdown is
left out. "Next unlock" comes from `bossGearProgress`. Geometry checkers not run
(need the server).

## PARTLY DONE - 74. Small bundle

**Built 2026-09-29, PR #107:** rarity tooltips (`rarityTitle`), the desktop Character dot, `mat_` stripped from names. **Number format done:** one `formatNumber` in `ui/format.ts` (thin space to 100,000, `k`/`M`/`B`/`T` above, exact in `title`/`data-exact`, decimal mark by UI language); every `.toLocaleString()` on a quantity swept onto it, `exercise.mjs` reads `data-exact` instead of parsing. **Left open:** reroll history (auto-reroll reports only its end state; needs server-side roll results - do it with 86). The old gathering primitives (Raw Log, Wood, Oak Log) are left for 83/86.

Each is independent:
- Rarity tooltip everywhere a rarity is named: name, tier number, power
  multiplier (from the mirrored `powerMultiplier`).
- A badge on the Character tab while any character has unspent attribute
  points.
- DONE: One number format (`format.ts`): whole numbers with a thin space up to
  100,000, compact above, exact in the title; separator by UI language.
- Reroll history: the last 20 results on a piece and its best roll, client
  side from the command results.
- Rename the "Mat ..." items in `items.json` (dev prefix shown to players);
  decide whether the old gathering primitives (Raw Log, Wood, Oak Log) are
  converted or listed under "Old materials". Mind the two namespaces
  (server/CLAUDE.md).

**Done when:** `npm test`, the ratchet and `exercise.mjs` are green; content
validation passes.

## 75. Book of Deeds: chapters II-IV open together

**What is actually true:** chapters open in sequence, so Hunters waits on
Smiths' "Fuse fifty times". Chapter I already teaches the loops in order.

**Build:** after chapter I is sealed, II, III and IV are open together; V
opens at two Seals. Seals and their skill points are unchanged. Server
(`DeedRegistry` open rule), because a Seal pays permanent points.

**Ask the owner first:** it lets Seals come faster for a player who plays one
loop. **Done when:** `SealEngine` tests cover the new open rule.

**DONE 2026-09-29 (the owner left it to Claude: yes).**
- **The rule** is `DeedRegistry.IsOpen(chapter, doneMask)`: I first, then
  II-IV together, and V at two of those three. A chapter counts as done
  once it is sealed or complete.
- **The route** sends `IsOpen` and a new `OpensWhen` sentence, so the book
  no longer says "the chapter above".
- **Tested** in `DeedRegistryTests`.
- **Seals were never gated.** `SealEngine.AwardCompletedChaptersAsync` has
  always sealed ANY complete chapter, open or not. The worry above ("Seals
  come faster") was never a live constraint; the sequence was only what the
  book showed. Awarding is unchanged.

## 76. One "Community" menu entry

Market, Friends, Guild and Leaderboards become tabs of one entry
(`TAB_FAMILIES`, as Supplies and Bloodline did). Mail stays its own entry
(world boss rewards land there). Update `screens.mjs`, the Wiki ledger and
the unlock rules (Market/Guild at level 10 lock their tabs, not the entry).

**Ask the owner first** (it renames things they know).

**DONE 2026-09-29 (the owner left it to Claude: yes).**
- **Menu:** Community is one entry that opens Friends (never locked). Its tabs
  are Friends, Market, Guild and Leaderboards (`TAB_FAMILIES.social`). Mail
  stays its own entry.
- **Locks:** tabs now carry their screen's lock the way menu entries do
  (`data-locked` and the "Level 10" text). Market and Guild lock their tabs,
  not the entry.
- **Checks:** `screens.mjs` reaches the four tabs as sub-tabs; a locked tab
  is reported, not clicked. `exercise.mjs` checks a new account's Market tab
  is greyed at "Level 10" while Community stays open, and that the fixture
  sees no greyed tab.
- **No route changed,** so every `requestScreen('market')` and the map's
  plates still land where they did.

## 77. Village "Work slots": verify, then remove or collapse

The Village lists "Work slots" - 168 rows of "Slot N · idle" on the fixture
("production slots, not people"). Run the `wiring-auditor` agent on it: if no
server code reads those rows, remove the panel (and note the table under
"Known dead code"); if something does, collapse it to one summary line.

**DONE 2026-09-29 - removed.** Neither branch applied as written: the rows
were live, but they were `CharacterRecords` - the roster under a false
caption. A summary line would have kept the lie, so the panel went, with
the `Villagers` list on `/api/v1/player/statistics`. `VillageResidents` and
`EvictVillager` are under Known dead code.

## 78. Hunting advisor: honest estimates on every monster

**What is actually true:** a monster row shows HP and XP only. Because XP and
gold scale with HP, where to farm is a function of gear alone: survival and
the loot table. The server can already simulate a fight
(`BossGearBenchmark.ProjectChallenge`).

**Build:** `GET /api/v1/combat/projection?slot=N` - for each unlocked monster:
kill time as a range, XP/h, gold/h, wins with food / without, food per hour.
Cached per character for a minute. Combat shows one line per row and the drops
on expand. No formula on the client.

**Done when:** a server test pins the projection against a simulated fight
within a tolerance; the line reads "estimate"; `check:perf` does not regress.

**DONE 2026-09-29.**
- **Engine.** `HuntingProjection` is a tick-by-tick simulation of expected
  values. It uses RunCombatTick's order: player swing, monster swing,
  auto-eat, death, kill, and a swing clock reset on respawn. It covers one
  hour of back-to-back fights, with food and without. It calls the tick's own
  helpers, which were moved out of `RunCombatTick` unchanged: `LiveCombatStats`,
  `EffectiveMaxMilliHpFor`, `LiveAttackIntervalMs`, `LiveCritChancePct`,
  `LiveCritMultiplier`, `LiveKillXpMultiplierPct`, and the now-internal
  `EffectiveMilliAttackFor` and `HasCrossedInterval`. The result is kill
  time as an 80% band, XP/h, gold/h, whether an hour is survived with and
  without food, and food per hour.
- **Guard.** `HuntingProjectionTests` runs the real `RunCombatTick` for an
  hour. Kills/h landed within 3-5% on three rows and food within 10%. The
  test asserts ±10% on kills.
- **Route.** `GET /api/v1/combat/projection?slot=N` gets a copy of the live
  payload taken on the tick thread (`PayloadSnapshotOrder`). It swaps in
  slot N, projects all 25 canonical monsters, and caches the result for 60 s.
  It answers 409 `NoSession` when nothing is live.
- **Client.** Each open Combat row shows "Estimate: 7-9 s a kill · XP/h ·
  g/h · safe / needs food / you would die". Drops were already on expand.
- **Left out on purpose:** the food buff's regen, Death Ward, Last Stand and
  Thunderer. All four only help the player, so the estimate errs toward
  caution.
- **Found on the way, fixed 2026-09-30:** the offline projection's incoming
  damage ignored dodge and block (and the 1,000 floor and the Dreadnought
  cap), which the live tick applies. The monster swing is now one set of
  helpers in `SimulationEngine` (`MonsterHitChance`, `LandedMonsterMilliDamage`,
  `ExpectedMonsterMilliDamagePerSwing`) that `RunCombatTick` rolls and that
  `HuntingProjection` and `OfflineSimulationEngine.ProjectCombatSustain` take
  the expectation of; offline also reads the live health bar
  (`EffectiveMaxMilliHpFor`) instead of its copy. Offline was charging
  1.11-1.43x the live tick per swing; it is now within 1.1%
  (`OfflineDefenceParityTests`, which runs the real tick). So a
  food-limited offline window now lasts 11-43% longer - and earns that much
  more XP and gold - than before; a fed one eats 11-30% less. (The swing
  clock residue noted here - offline charging a monster swing every interval
  - is closed by the parity work below.)
- **Offline combat parity, 2026-09-30 (owner rule: offline and online pay
  the SAME per hour).** `OfflineSimulationEngine.ProjectCombat` no longer has
  a model of its own: it runs `HuntingProjection`'s fight (`FightSetup`,
  `FightState`, `Advance`) tick by tick for the whole window, re-deriving the
  character every 5 minutes of game time when a level lands. Closed, each by
  reusing the live helper and deleting offline's copy:
  - *Kill speed:* `LiveAttackIntervalMs` (Relentless), `LiveCritChancePct`,
    `LiveCritMultiplier` (Precision, Cruelty, Guile), Double Strike, burn and
    set fire, and `SimulationEngine.EffectiveMilliAttackFor` (offline's copy,
    now deleted, lacked the guild Damage buff and the legacy speed perk).
    How many swings a fight takes is the exact distribution of the live rolls
    (`HuntingProjection.SwingsToKill`), walked along a golden-ratio sequence,
    not a mean divided into the health.
  - *Swing clock:* reset at each kill, as `RunCombatTick` does.
  - *First-clear boss:* one fight at `BossFirstClearRules` health and attack,
    every respawn after it farmable (offline used first-clear ATTACK for the
    whole window and farm HEALTH, `ExpectedSecondsPerKill` reading the
    registry by id); the kill marks the mask and opens the region
    (`SimulationEngine.ApplyKillProgression`, shared with the live kill).
  - *Healing:* lifesteal (1% cap) and Bloodthirst per swing; the food buff's
    regen and a Death Ward (spent through `ConsumableEngine`); auto-eat as the
    tick does it - threshold, best heal first, cooldown - instead of a pool.
  - *Death:* a window that dies ends the activity and records the death
    (`SimulationEngine.ApplyCombatDeath`, shared); the old model stopped
    counting and left the character deployed.
  - *XP:* `HuntingProjection.XpPerKill` - the global multiplier, Blood Moon,
    mentors, Human mastery, legacy perk, inheritance, skill tree, guild Exp
    buff and the mentorship penalty, truncated per kill. Offline took only
    inheritance. Also the seasonal pass XP and the kill-quest progress the
    live kill pays. Gold was already `CombatGoldReward.PerKill`.
  - **Guard:** `OfflineCombatParityTests` runs the REAL `RunCombatTick` for
    six hours per row and the offline projection for one: bare and geared in
    regions 1-5, fed and hungry, a fast killer, first-clear bosses, lifesteal,
    the skill tree with a burning set, thorns, the food buff with a ward, a
    levelling Warrior. Kills, XP, gold, food and seconds alive must agree
    within 5% (one kill, three bites or five seconds where 5% is less than one
    event), and the survival verdict must match. All 31 rows land within 1-3%
    except a 13-kill hungry row at 0.92 (one kill). Before: skill-tree
    characters earned 0.52-0.74x the live XP, lifesteal and buffed hungry
    characters 0.04-0.05x (they died where the live tick sustains), hungry
    windows 0.79-0.95x, and first-clear boss food ran 4-4.5x.
  - **Still different, not closed:** a kill away pays no diamond roll, no
    codex kill, no guild war points and no boss-challenge/personal-record
    notes; potions and the food buff that EXPIRE inside the window are
    already gone at hydration, so offline runs the window without them;
    three fighting slots are projected one after another on one larder,
    where the live tick interleaves them (and shares one eat cooldown);
    Scholar pays offline 25% more by design.

## 79. Gold ledger and material flow; Treasury counts gold spent

- Count gold out by category at the places it is charged (reroll, fusion,
  village, Delve/Deep, recruit, market fee) and in by source (kill, Town Hall,
  sale). Progress -> Statistics shows the split.
- Material flow per day: gathered, spent, lost to the Warehouse cap.
- Treasury (Lifetime) pays for HOLDING gold, which discourages spending in an
  economy that already under-spends. **Ask the owner** whether it moves to
  gold spent; tiers already paid stay paid.

**Done when:** the ledger survives a relogin and a test pins each writer.

**PHASE 1 DONE 2026-09-29 (gold out + Treasury; the owner said yes).**
- **Ledger.** `Engine.GoldLedger.RecordSpendAsync` runs inside each debit's
  own transaction. It upserts `gold_spend_daily` (player, UTC day, category)
  and increments `PlayerRecord.LifetimeGoldSpent` in raw SQL. That is
  migration `AddGoldLedger`, which is additive.
- **Sites.** All 15 debit sites are wired: reroll, fusion and stack fusion,
  village, recruit, breeding (both), Delve, Deep (lantern and toll), market
  (escrow buy, and the order-book match at the execution price), cosmetics,
  guild (gold contribution and the depot's gold) and guild raid. The
  order-book ESCROW itself is marked `// GoldLedger:` as not a spend,
  because a cancel refunds it.
- **Guard.** `GoldLedgerTests.EveryGoldDebit_IsRecordedOrSaysWhyNot` reads
  the source. It flags gold-named debits, plus any `.Quantity -=` within 30
  lines of a "gold" literal, which is how it caught the depot.
- **Treasury.** The deed and the legacy 100k flag now pay on
  `LifetimeGoldSpent`, with the same thresholds. Tiers already paid stay
  paid. The wire's toast term reads `GoldLedger.KnownLifetimeSpent`, which
  login seeds and every checkpoint refreshes.
- **Screen.** `GET /api/v1/player/gold-ledger` returns 7 d / 30 d / all by
  category. Progress -> Statistics shows "Where your gold went".
- **Phase 2 is still open:** gold IN by source (kill, Town Hall, sale,
  salvage, login, mail) and the material flow per day (gathered, spent,
  lost to the Warehouse cap). Income is harder, because combat gold is
  banked by the checkpoint as a delta rather than at a single site.

**PHASE 2 DONE 2026-09-29 (gold in by source; material flow per day).**
- **Two income writers.** An engine that credits the gold row itself calls
  `GoldLedger.RecordIncomeAsync` in its own transaction: chest sales,
  market and cosmetic sales (net, in the sale's transaction, for both the
  online and offline seller), login reward, mail claim, guild payout, Delve
  consolation, starting gold, offline Town Hall, and the retry outbox when a
  delayed grant lands. Gold on `RedisPendingGoldDelta` (kill, live Town Hall,
  auto-salvage, kills while away) is tallied with `GoldLedger.TallyIncome`
  onto `TickStatePayload.PendingGoldIncome`, and the CHECKPOINT writes it.
- **Why the checkpoint, and why once.** That gold is banked by Redis
  write-behind or by the checkpoint, depending on whether Redis is up, so a
  bank is not one place. The tally rides the job as the gold delta does:
  `RequestFlush` zeroes it live, `FlushState` writes the snapshot's copy in
  its transaction, and a failed ack hands it back. It also rides
  `FlushStateAndAdvance` (login), `FlushBatch` (shutdown, past the epoch
  sieve, which is what stops SIGTERM's second `ShutdownGracefully` counting it
  again) and `StateReloadMerge`. A frame never touches it. Lost, never
  doubled: a logout that fails every retry drops the tally, not the gold.
- **Material flow.** `material_flow_daily` (player, UTC day, item,
  direction), written by `Engine.MaterialLedger` inside the stack's own
  transaction. Gathered: live gathering, offline gathering, live village
  production (at write-behind) and offline production. Spent:
  `TryConsumeUnifiedAsync` (crafting, buildings, larder) and the three guild
  donations. Sold and Discarded: the chest. LostToWarehouseCap: the offline
  grant only, both its window ceiling and the live-storage clamp. The live
  tick PAUSES production at the cap, so nothing is thrown away there, and
  the screen says so.
- **Guard.** `GoldIncomeLedgerTests` reads the source. Each credit shape (a
  "gold" upsert, `gold….Quantity +=`, `RedisPendingGoldDelta +=`,
  `["gold"] =`, `.AddGold(`) needs a record, a tally or a `// GoldLedger:`
  reason within 3 lines, and each checkpoint hop is pinned by name.
  `GoldIncomeLedgerPostgresTests` pins the writers, one flush, a failed flush
  and its retry, and a relogin. Migration `AddGoldIncomeAndMaterialFlow` is
  additive.
- **Screen.** The same route adds `Income`, `Materials` and their own
  "since" dates. Progress -> Statistics shows "Where your gold came from",
  "Where your gold went" and a Materials table. It says tallied income lands
  at the next save, about five minutes.
- **Found, not fixed.** An ONLINE seller's market and cosmetic proceeds go
  through `MarketMatchQueue`, whose drain only moves `CurrentGold` (no
  `RedisPendingGoldDelta`), so nothing banks them. They are counted as
  income, but they appear to be lost at the next relogin. Separately,
  `FlushBatch` never applies `RedisPendingGoldDelta`, so gold still owed
  at shutdown with Redis down is dropped.

## 80. Breeding Grounds above level 1

The Wiki says it outright: "nothing above level 1 has an additional effect".
**Owner picks one:** a breeding cooldown cut per level (for example 5 % a
level, floor 50 %), or a cap at level 1 with "complete" on the card. Either
way the player stops paying for nothing.

**DONE 2026-09-29 - the premise was false. No gameplay change.** The owner
left the choice to Claude, and checking the code showed the player was not
paying for nothing. The breeding rework (2026-09-12) had already wired the
level in:
- +1 % a level to each aptitude's up-mutation chance (base 25 %,
  `BreedingAptitudes.UpMutationPercentFor`);
- +1 % a level to a new-trait mutation (base 4 %, `BreedingTraits`);
- 1, 2 or 3 aptitudes chosen outright at levels 4, 7 and 10
  (`SelectableCount`).

Only the Wiki row was stale, and the Village card already said part of it.
The breeding cooldown is one hour, so a percentage cut of it would buy almost
nothing. The Wiki row now quotes the real numbers, and `wiki.test.ts` holds
it to the server constants.

## 81. Chest rules and row actions

- Five buttons per equipment row (Unequip, Reroll, Lock, Sell, Bin) become one
  primary action plus the existing `ContextMenu`.
- Saved rules ("sell everything below tier X from region Y; locked never")
  that run on each drop - an extension of `AutoSalvageBelowTier`. Mind the two
  gold paths (server/CLAUDE.md).
- A 5 s undo on a sale (the client delays the send; no server change).

**DONE 2026-09-29.**
- **Row.** The row keeps Equip/Unequip. Reroll in Forge, Lock/Unlock, Sell and
  Bin moved to a "More" menu. `ContextMenu` is generic now and Chat uses the
  same component. A locked piece shows a "Locked" badge on the row, and a
  phone row is one line (56px, down from 78).
- **Rules.** The Chest has an "Auto-sell rules" panel: one floor for all
  regions plus one per region. A region rule can only RAISE the floor,
  because the server takes the larger (`ChestSalvageRules`). The rules are
  stored packed on `PlayerRecord.AutoSalvageRegionTiers` (migration
  `AddAutoSalvageRegionRules`) and folded into the request's one tier in
  `CombatLootDropRequest.Build`. The worker and both gold paths are
  unchanged. Settings only points to the Chest now.
- **Undo.** Sell and Sell all wait 5 s with an Undo button. Leaving the screen
  sends the sale. Closing the tab keeps the item.
- **Not "locked never".** A rule runs before the row exists, so there is
  nothing locked for it to skip.

## 82. Desktop header as five groups

24 buttons in two rows plus the event chip take ~215 px. Collapse to five
dropdown groups (Play / Items / Village / You / Community) at desktop widths;
keys 1-5 stay. Show the owner a screenshot first.

**BUILT 2026-09-30, awaiting the owner's look at a screenshot.** Above 40 rem
the header is five dropdown toggles (Play / Items / Village / You / Community;
Codex moved to Village, Wiki and Settings to You). Escape, a click outside,
focus leaving the group, and ArrowDown on a toggle are handled; entries keep
`data-nav`/`data-label`. Phone menu and hotkeys 1-5 (tab bar, `tabs.ts`) are
untouched. Scripts navigate through one `navButton()` in `screens.mjs`, which
opens the phone Menu or the group first. Screenshot of the open Items group:
`docs/screenshots/2026-09-30/header-dropdown-1366.png` (rendered with a stub
token and no server, so the header only).

## BUILT - 83. Workshop commissions (the material sink)

**BUILT 2026-09-30, not merged or deployed.** Spec with the price derivation:
`docs/superpowers/specs/2026-09-30-workshop-commissions.md`. A commission makes
one of the 75 canonical region pieces (regions the player has opened) at a
rarity floor of Common-Epic by Workshop level 1-5, capped two tiers below the
region's boss-wall requirement (only region 1 is capped: Common), with one
chosen affix at Common; the tier is `max(floor, zero-luck drop roll)`. 1 h
(Common) to 8 h (Epic), one at a time, wall-clock `CompletionEpoch` so it
finishes while the player is away. Price = what the region's reference
gatherer (GatheringEconomyTests' own profiles) harvests of its log, ore, golden
log and rare ore in those hours: 6,600 (region 1) to 144,400 (region 5 Epic).
REST (`GET /api/v1/workshop`, `POST .../commission`, `POST .../collect`, dev
`POST /api/v1/dev/workshop/finish`), no opcode, no packet field; migration
`AddWorkshopCommissions` (three additive columns on the formerly writerless
`PlayerCraftingSlots`). Rebirth and account purge delete a running order.
Guards: `WorkshopCommissionTests`, `PowerCeilingTests
.TheWorkshopCommissionFloorStaysBelowEveryRegionsUsualDrop` (floor < the wall at
every Workshop level, and a full commissioned wardrobe LOSES every first clear,
so time to region 5 cannot move), `GatheringEconomyTests
.Test_WorkshopCommission_IsPricedFromThisSupply`; `exercise.mjs` places,
collects and bins one on the fixture and restores its stock.

The original proposal:


The Crafting Workshop's level is read by nothing and crafting always makes a
Normal (57 crafts, lifetime). Proposal: a commission makes a region piece with
a rarity FLOOR set by the Workshop level (T2-T6) and one chosen affix at
Common, for materials in the tens of thousands, taking real time to finish
(`PlayerCraftingSlot.CompletionEpoch` exists and nothing writes it). Prices
come from `GatheringEconomyTests`, not guesses. The floor must stay below the
region's median drop, checked in `PowerCeilingTests`. Time to region 5 (~26
days, LONG_GAME_SPEC section 7) must not drop below ~20.

## DONE - 84. Great Works (the long material sink)

**Built 2026-09-30 (owner decision 2026-09-30: solo, 5 stages, small permanent
capped bonuses that survive rebirth).** Five monuments, one per region; each
stage eats that region's common log or ore (any mix) and pays a bonus:
Birchwood Cairn and Acacia Gate +1 % gathering yield a stage, Willow Hearth and
Frostpine Beacon +15 min offline limit a stage, The Ebon Crown both. Stage costs
50k / 150k / 400k / 1M / 2M. Ceilings: +15 % yield, +225 min offline, both in
`PowerCeilingTests`. Migration `AddGreatWorks`, opcode `DepositGreatWork = 80`,
result codes 60-62. The Village panel deposits, and the Map draws a landmark that
grows a layer per built stage. **Completion** (added the same day) pays a bound
frame per monument and, for The Ebon Crown, one Hall of Ancestors slot above the
diamond ceiling (14 -> 15). Spec: `docs/superpowers/specs/2026-09-30-great-works.md`.

Original brief, kept: Village monuments in five stages, each stage eating 50,000
to 2,000,000 of one region's materials, each visibly changing the Home map, each
paying a small permanent bonus that survives the season (for example +1 %
gathering, +1 h offline limit, a Hall slot, a frame). The owner's task 38 notes
already name "Great Works"; this is the solo version. Every bonus goes into
`PowerCeilingTests` with a cap.

## BUILT - 85. Orders: automation rules as a reward

**Owner decision 2026-09-30, built the same day on `claude/automation-rules-85`,
not deployed.** Design and status: `docs/superpowers/specs/2026-09-30-automation-rules.md`.

Up to three rules per character, one slot opening at each of level 20, 40 and
60 (the account level; the game has no per-character level). A slot above the
level keeps its rule but it is inert, so a rebirth re-locks them until the
level returns.

- **When the larder runs dry, fish at X.** Auto-eat wants a bite and has none
  (OutOfFood); the next combat tick sends the character to the chosen fishing
  spot, if it has been reached and no other slot works it. Halt reason 6.
- **After a death, one monster easier.** `ApplyCombatDeath` (the one death
  both paths share) respawns the character on the previous regular of the
  canonical ladder (a boss steps to its region's strongest regular; a
  region's first regular skips the boss behind it; 91 has nothing below).
  Halt reason 7. The death is still counted and carded.
- **Fuse stacks up to tier N.** The drop request carries the tier
  (`CombatLootDropRequest.Build`, live and offline); after each loot-worker
  cycle the stacks the drops landed in are fused from tier 1 to N through
  `ForgeSplicingEngine.FuseStackInTransactionAsync` - the Forge button's
  own path, extracted rather than copied. Gold comes off the row; the session
  follows through `ChestSaleGoldQueue` (display only).
- **Offline = online.** `OfflineSimulationEngine.ProjectCombatLegs` spends a
  window as legs: the fight stops at a death or (with the rule) the starving
  tick, the rule acts through the same function, and the rest of the window is
  spent where it sent the character. `AutomationRuleParityTests` runs the real
  tick beside it: the fishing switch within 10% of the live second, fishing XP
  within 5%, and the step-down ending on the same rung after the same number
  of deaths.
- **Storage and wire.** `characters."AutomationRules"` (bigint, migration
  `AddAutomationRules`), swapped with the character in the tick's register.
  No packet field and no opcode: `GET/POST /api/v1/automation-rules`, then
  `AutomationRulesQueue` to the tick. The "Orders" panel is on the Character
  screen; `exercise.mjs` sets an order through it, reads it back, checks a
  locked slot is refused with `SlotLocked`, and restores the fixture's rules.

**Not done, on purpose:** a restocked larder does not send the fisher back to
the fight (the player redeploys). **Fixed 2026-09-30:** slots 2 and 3's
activity change (a rule's, and a death's before it) used to be live-only -
the checkpoint wrote only slot 1's activity, so a relogin put slot 2 back on
the monster it was deployed to. `PersistFieldedActivitiesAsync` now writes all
three through the same guard, each at its own rank
(`FieldedActivityPersistenceTests`).

## 86. A deterministic affix step beside the reroll - design with the owner

For region materials, add or replace one CHOSEN affix at Common rarity, so
randomness stays in magnitude only. Also: an auto-reroll stop on a
combination, and the reroll history from 74.

## DONE - 87. Boss Ascension ladder

**Built 2026-09-30 (owner decision 2026-09-30).** Every region boss can be
fought again at ten steps; each step is the one below it PLUS one modifier.
First clear of a step pays a title (`Wolfbane I`..`X`, `Lynxbane`, `Wyrmbane`,
`Titanbane`, `Scourge of Malakor`: 50 in all) and, at steps 5 and 10, a **bound**
frame (10 in all). **Cosmetics and titles only** - no gold, diamonds, gear or
chest. A chest is a tradeable cosmetic, i.e. gold by the back door, so the
frames are `Bound` (`CosmeticDefinition.Bound`): never in a chest pool,
refused on the cosmetic market with `CosmeticMarketResult.Bound`.

| Step | Adds | In force at this step (region 1) |
|---|---|---|
| 1 | boss attack +15% | attack +15% |
| 2 | time limit 200% of the boss's Swift limit | + kill within 160 s |
| 3 | boss health +10% | + health +10% |
| 4 | limit 175% | 140 s |
| 5 | boss attack +15% | attack +30% (frame) |
| 6 | limit 155% | 124 s |
| 7 | boss health +10% | health +20% |
| 8 | limit 140% | 112 s |
| 9 | boss attack +15% | attack +45% |
| 10 | limit 120% | 96 s (frame) |

**The ladder is one table**, `BossAscensionRegistry.Steps`; the tick, the
projection, the REST view (`GET /api/v1/boss-ascension`, which also carries the
server's own effect sentences and reward names - the client keeps no copy) and
the tests all read it through `ModifiersFor`. The owner's example "one fewer
larder slot" is NOT on it, and neither is a slower bite or a no-food rule: they
were measured and each moves nothing the projection can see (against the
region's best gear the boss is a burst the larder never answers - region 5
eats 0-1 bites in a whole fight), and a step that cannot be asserted harder
does not belong on a calibrated ladder. Attack, health and the time limit
(Swift's, reused) are the three that do.

**Calibration** (`BossChallengeCalibrationTests`, best-in-slot = the wall's
required quality +4 at the region's reference level, cleared boss): each step is
ASSERTED harder than the last on the scalar its modifier acts on - survival
headroom (the extra boss attack the gear still wins against) for attack steps,
kill time for health steps, kill time over the limit for time steps - and may
ease none of the three. Step 10, all modifiers on:

| Region | BiS kills in / limit | attack headroom | the wall's own gear |
|---|---|---|---|
| 1 | 91 s / 96 s | x3.42 | 98 s - loses on time |
| 2 | 125 s / 132 s | x2.36 | 136 s - loses |
| 3 | 140 s / 144 s | x5.34 | 162 s - loses |
| 4 | 125 s / 132 s | x11.90 | 152 s - loses |
| 5 | 120 s / 132 s | x21.64 | 146 s - loses |

The 120% last limit is the window where best-in-slot still clears (91-97% of
the limit) and the wall's gear does not - 100% leaves no room for the two
health steps.

**Persistence and the tick.** `boss_ascension_progress` (additive migration
`AddBossAscensionProgress`): highest step cleared per player per boss, only
raised, inside the transaction that pays that step's titles and frames - one
fact, exactly-once, and a lost note is paid with the next clear (every step
between stored and cleared is paid). The payload keeps a packed cache
(`BossAscensionPacked`, filled at login like `DefeatedRegionBossMask`) for
start-step validation; the table is the authority. The armed attempt
(`AscensionStep/Region/CharacterId`) is runtime only, pinned to the character
that started it, and ends on a change of activity, a death or a reload. A
step is cleared only by a kill inside its time limit; a slower kill answers
result 49 and leaves the attempt armed for the respawn.

**Wire.** `StartBossAscension = 79` (TargetId = region, SecondaryId = step; no
struct change). `StateUpdatePacket.AscensionStep` (+1 byte, 810 -> 811, runtime
only by design). Result codes: 46 boss never beaten, 47 step locked (both
answers to the command - an honest client is never disconnected for a stale
ladder), 48 step cleared (sent by the reward worker AFTER the commit, so the
ladder refetched on it is right), 49 too slow, 50 reward could not be saved yet.

**Client.** `BossAscension.svelte` under each unlocked region on Combat: ten
44px buttons (not a `<select>`), cleared / open / locked, the modifiers and
reward of the step being looked at, a Start button. `exercise.mjs` reads the
ladder, checks a locked step cannot be started and the buttons' size, starts
the next step on the fixture (marking region 1's boss beaten for the run, which
the fixture has not done) and puts the fight, the ladder and the boss mark
back through `POST /api/v1/dev/boss-ascension/restore` (dev tools only).

**Not done:** the Book of Deeds line (task 57 reworks the Book); the offline
catch-up does not run Ascension attempts (live kills only, like the challenges);
the title picker lists all earned titles, so a player at the top of five ladders
scrolls a long list.

## BUILT - 88. Rebirth on demand instead of a calendar season

**Owner decision 2026-09-30, built the same day on `feat/88-rebirth`, not
deployed.** Design and status: `docs/superpowers/specs/2026-09-30-rebirth-on-demand.md`.

- **The calendar ends nobody's run.** `SeasonalRotationEngine` no longer
  closes an era on its date, paused or not. The admin's typed END SEASON is
  the only global rollover left. The live era (due 2026-11-02 09:40 UTC)
  is now safe without the pause.
- **A player rebirths when they choose.** `GET /api/v1/rebirth/preview` and
  `POST /api/v1/rebirth {ExpectedRebirthCount}`. The panel is on the
  Ancestors screen, with a two-step confirm.
  - The reset is the rollover's own `AwardLegacyShardsAsync` and
    `ResetPlayersAsync`, run for one player. There is no second wipe list.
  - When the player is online, the tick suspends and flushes the payload,
    and the flush's continuation resets and reloads it
    (`RebirthTickCoordinator`).
  - The count token makes a double submit a 409.
  - A reborn account gets the registration starter kit.
- **Carries:**
  - the Hall (aptitudes, genes, generation, epic, Keep marks, bought slots);
  - Seals, with 2 skill points per Seal back at once;
  - Inheritance;
  - shards and Legacy perks;
  - diamonds and paid respecs;
  - village buildings;
  - race, gathering and codex progress;
  - deeds, cosmetics and records;
  - the larder, mail and guild.
- **Resets:**
  - level, XP, attributes and unspent points;
  - the skill tree;
  - potions and the free respec;
  - all gear in all 11 slots;
  - gold, materials and unescrowed listings;
  - ages (back to adult) and activities (idle);
  - the gene pool, with its clock and price;
  - the chronicle pass;
  - the Hall past its cap (culled).
- **Renown**, the permanent bonus: `floor(15 * (1 - 0.8^n))`% damage, where
  n counts rebirths taken at level 50 or above. It is asymptotic below 15%
  and sits in `PowerCeilingTests`.
- **Fixed in the shared reset on the way:**
  - attributes stacked on every climb;
  - the tool slots 8-10 were left pointing at wiped ids;
  - characters kept yesterday's fight.
- **Changed deed:** Chapter V's "top fifty" is now "be reborn at level 50+",
  and an old top-50 finish still counts.
- **Before deploying:** migration `AddRebirthCounters` is additive. Run
  `npm run exercise`; it was not run here, because the machine has no
  Playwright browser.

---

# Tasks 89-110: the 2026-10-01 UI/UX audit

Added 2026-10-01. **The report is `docs/audits/2026-10-01-ui-ux-audit.md`.**
Read its section 4 (findings by root cause) for any task below before you
estimate it; each task names the section it comes from.

**Where the evidence comes from.**
- Five read-only passes:
  - CSS architecture;
  - mobile and Android WebView;
  - UI states;
  - the mechanical evidence (below);
  - five visual reviews of about 120 screenshots of the dev fixture and of
    new guests, at 390 and 1440 px.
- The four geometry checkers were run at 360, 390, 412, 768 and 1440 px.
  - 0 findings at phone widths.
  - `check:touch` found **153 undersized controls at 768**, because the 44 px
    floor applies only below 40rem.
  - `check:overlap` **silently skips 10 of 29 destinations**, the ones that
    have no menu button of their own.
- Nothing was run on a phone. Items marked **(device)** need one.

**Owner decisions in force (2026-10-01).**
- **The APK is portrait-only** (branch `claude/ui-audit-portrait`, a3eac07).
  Tablets, unfolded foldables, split-screen and mobile web still rotate,
  because Android 16 ignores `screenOrientation` on large screens at
  targetSdk 36. So the ShieldWheel landscape query
  (`ShieldWheel.svelte:868`) and the landscape half of `check:safearea` stay.
- **A redesign is welcome where a screen clearly benefits.** The report's
  section 8 has a sketch for each redesign. Show the owner a 390 px
  screenshot before merging one.
- Everything under "Owner decisions in force" in 48-62 and 68-88 still
  holds. **Answer the owner in Czech.** Write repo prose in English.

**How to work.** The "How to work" rules of 48-62 apply. In addition:
- **One branch and one PR per task, cut from `main`.**
  - Name the branch `claude/ui-<topic>`.
  - Never stack PRs.
  - Where a task touches a file that 89-93 also touch, wait for those to
    merge, or rebase on them before opening the PR.
- **Verify in this order:**
  1. `npm test` and `npm run check:ratchet` (baseline: the 4 GuildOps
     errors).
  2. The server suite, only if the server changed (94, and any wire
     addition).
  3. `run-dev.ps1`, re-seed the fixture, then `npm run exercise`.
  4. `check:clipping`, `check:overlap`, `check:touch` and `check:safearea`
     at their default widths.
  5. **A 390 px screenshot of every screen you changed, looked at**, plus
     1440 px when the desktop layout changed.
  6. For anything a new player meets, a freshly registered account, not the
     fixture.
- **Use the report's tokens and primitives.** Once 89 is merged, new CSS
  uses the `:root` tokens (`--fs-*`, `--sp-*`, `--z-*`). Once 106 lands a
  primitive (Button, Modal, Tabs, ItemRow, Hint), a redesign adopts it rather
  than adding a thirteenth `.tiny-btn`.
- **Do not trust a checker's 0 on a screen it does not visit.** Until 106
  fixes `overlap-check`, measure Friends, Market, Guild, Leaderboards, the
  shield wheel, Market cosmetics, Supplies Boosts and Bloodline
  Ancestors/Inheritance by hand.

## Order

| # | Task | Size | Needs the owner? |
|---|---|---|---|
| 89 | **BUILT** (`claude/ui-css-foundation`, verified on `claude/ui-integration`) - CSS foundation | M | no |
| 93 | **BUILT** (`claude/ui-query-states`, verified on `claude/ui-integration`) - Load errors stop looking like empty states | M | no |
| 90 | **BUILT** (`claude/ui-overlays-nav`, verified on `claude/ui-integration`) - Overlays, back button, scroll | L | no |
| 91 | **BUILT** (`claude/ui-money-feedback`, verified on `claude/ui-integration`) - Money and feedback | M | no |
| 92 | **BUILT** (`claude/ui-confirm-hints`, verified on `claude/ui-integration`) - Confirm, pending, visible reasons | M | no |
| 94 | Leave guild (server + client) | M | no |
| 108 | Phone GPU budget | S | no |
| 96 | Settings: account first, explanations folded, no developer text | M | show a screenshot first |
| 95 | Phone navigation: More sheet, sticky header, one name per concept | L | **yes**: renames, and a screenshot |
| 97 | Character: gear first on a phone | M-L | show a screenshot first |
| 98 | Combat: phone redesign | L | show a screenshot first |
| 99 | Chest: rows with stats, a Worn group, no Gold row | M-L | no |
| 101 | Gathering and Crafting: actions first, real status, guided empty states | M | no |
| 100 | Forge: one fusion row per item | M | no |
| 102 | Market: Buy / Sell / My orders | L | show a screenshot first |
| 103 | Village: buildings first | M | no |
| 104 | Ancestors: carried vs lost | M | no |
| 105 | World Boss and Delve | M-L | **yes**: Delve dark or parchment; boss name source |
| 107 | Guild and Community structure, copy contradictions | M | show a screenshot first |
| 106 | Shared primitives and sweeps (runs alongside 95-105) | L | no |
| 109 | Polish bundle | L (many S) | no |
| 110 | Decisions for the owner | S | **yes** |

**Recommended batches.**
- **Merge 89 and 93 now.** They are built and everything after builds on
  them.
- **Finish 90 + 91 + 92.** These are the systemic defects felt on every
  screen.
- **94 alone.** It is the only server change.
- **108 whenever there is a spare hour.**
- **The shell, one look at both:** 96, then 95.
- **The phone redesigns, one screen per PR,** in the order of the table.
  Start each with the matching 106 primitive if it is not there yet.
- **107 and 109 last.** Put 110 to the owner in one message, at any time.

---

## 89. CSS foundation

**BUILT on branch `claude/ui-css-foundation`, not merged.** Three commits:
- 1650c75: design tokens, undefined vars, touch hover, sticky.
- 3c69a6d: no white flash on Android, per-theme theme-color, keyboard resize.
- 07b5ca5: the wood-beam header styling is scoped to the app's top bar.

Report sections 4.C, 4.D and 7.

**What is actually true (on `main`).**
- **Sticky is broken.** `app.css:472-476` sets `overflow-x: hidden` on both
  `html` and `body`. That makes `body` a scroll container that never
  scrolls, so `position: sticky` sticks to a box that does not move **(spec)**.
  - The ConnectionNotice scrolls away (`ConnectionNotice.svelte:153`).
  - So does the Wiki sidebar, which `client_web/CLAUDE.md` already records as
    never sticking.
- **15 custom properties are read but never defined:** `--bad`, `--err`,
  `--bg-sunken`, `--bg-dark`, `--panel`, `--fg`, `--bg-hover`, `--success`,
  `--ok`, `--dim`, `--line`, `--bg-surface` and `--bg-light`.
  - The Settings email error is not red (`Settings.svelte:613`).
  - The PlayerProfileModal is dark-on-dark in the light theme
    (`PlayerProfileModal.svelte:199,216,247`).
  - Three different "bad" reds show (`Market.svelte:967,993`,
    `Character.svelte:790`, `ChatDock.svelte:264`).
- **There is no token beyond colour and `--radius`.** That gives 37
  font-size lengths, 45 spacing lengths, 22 radii, 16 z-index values and 7
  breakpoints.
- **Sticky hover.** There are 25 `:hover` rules and no `(hover: hover)`
  guard (`app.css:244,585-590,918-922`).
- **WebView defaults are left on.**
  - No `-webkit-tap-highlight-color`.
  - No `overscroll-behavior`, so pull-to-refresh reloads the SPA on mobile
    web.
  - iOS zooms into 14 px inputs.
- **The body background is fixed, with four layers** (two of them SVG
  turbulence; `app.css:530-537`). It repaints on every scroll frame.
- **`index.html` is incomplete.**
  - There is no `interactive-widget` (`:5`), so mobile web and the APK
    disagree on keyboard resize.
  - `theme-color` is hard-coded dark (`:24`).
- **Android white flash.** `styles.xml:12-16` has no `windowBackground`, and
  `capacitor.config.json` has no `backgroundColor`. That is a likely white
  flash after the splash **(device)**.
- **Breakpoints are per file.** There are seven: `40rem`, `52rem`, `30rem`,
  `46rem`, `64rem`, `560px` and `600px`. `PersonPicker.svelte:63`
  re-declares `NARROW_QUERY`.
- **A global `header` rule leaks into panels.** `app.css:661` (and the light
  `:799`) paints the app bar's beam background and brass border, both
  `!important`, on **every** `<header>`. 17 components use `<header>` inside
  a panel. `header strong` (`:675`, `:808`) turns body words into 1.15rem
  brass serif (the Skill tree intro, the Book of Deeds subtitle). It is also
  why the Delve intro is unreadable.

**Build (done on the branch).**
- `overflow-x: clip`.
- Define or replace the 15 variables, with a vitest guard that fails on an
  undefined `var(--x)`.
- The `:root` tokens of report section 7.
- Hover only inside `(hover: hover) and (pointer: fine)`.
- `-webkit-tap-highlight-color: transparent`, `overscroll-behavior-y: none`,
  and a 16 px input font on phones.
- The fixed background moves to `body::before`.
- `interactive-widget=resizes-content` and per-scheme `theme-color` metas.
- `backgroundColor` and `windowBackground`.
- The breakpoints collapse to phone / tablet / wide, mirrored in `media.ts`.
- The `header` rule is scoped to the app bar.

**Done when:**
- The guard test passes.
- The 17 in-panel headers render as plain panel titles: Skill tree, Book of
  Deeds and Delve each get a 390 px screenshot.
- All four checkers are clean. `check:clipping` matters most, because panel
  heads lose padding.
- A disconnected long screen keeps its notice pinned.
- **On a phone:** no white flash in the light system theme.

**Size:** M.

## 90. Overlays, back button and scroll

**IN PROGRESS on branch `claude/ui-overlays-nav`.** Report sections 4.A, 4.B
and 4.C.

**What is actually true.**
- **The chat dock traps its children.**
  - `.window` has `backdrop-filter: blur(10px)` and `overflow: hidden`
    (`ChatDock.svelte:156,158`), so it is the containing block for fixed
    descendants.
  - A profile opened from chat **measured 356×414 at (17,299)**, clipped to
    the dock (`Chat.svelte:424`).
  - The name ContextMenu (`Chat.svelte:409`) lands offset and clamps
    against the wrong box.
- **A 20-character username crushes the chat body to 0 px.** `button.who`
  takes 224 px of a 296 px row, and `span.text` is 0 px wide and 1,203 px
  tall. No checker opens the dock.
- **PlayerProfileModal is out of date.**
  - It shows 4 of 11 equipment slots (`PlayerProfileModal.svelte:130-165`).
  - It passes the raw slug as the item name (`:136`).
  - It shows "Last Online" even for a player who is online (`:122`).
  - It has no `role=dialog` and no `aria-modal`.
- **The chat window with the keyboard up** **(device).**
  `height: min(26rem, calc(100vh - 8rem))` (`ChatDock.svelte:148`) puts the
  header and close button at about y -2 px, under the status bar.
- **Back misses four layers.**
  - What's New is not one of `resolveBackPress`'s layers
    (`backButton.ts:94-114`), and it shows after every OTA update.
  - The profile is excluded on purpose (`App.svelte:491-497`).
  - The ContextMenu has no back hook.
  - The shield wheel unmounts mid-run (`WorldBoss.svelte:510-522`).
  - Only ContextMenu and PersonPicker answer Escape.
- **Scroll is never reset or restored** (`App.svelte:371-378,536-541`).
- **Tapping a tab leaves the phone Menu open** (`App.svelte:809`).
- **OfflineSummary has three faults.**
  - Its backdrop and the TabBar are both z 50, and the TabBar comes later, so
    the tab bar paints over "Welcome back" **(spec)**.
  - The card has no max-height (`OfflineSummary.svelte:268-275`).
  - The backdrop's `onclick` dismisses it on any tap on the card (`:67-73`).
- **Modals ignore insets and use `vh`.**
  - DeathCard, VictoryCard, OfflineSummary, PlayerProfileModal and the exit
    confirm do not read `--sa-*`.
  - The caps are in `vh` with no `dvh` (`VictoryCard:140`, `WhatsNew:111`,
    `PlayerProfileModal:204`, `PersonPicker:435`).
- **The ConnectionNotice sticks at `top: 0`**, under the status bar
  (`ConnectionNotice.svelte:153-154`).
- **The notification layers collide.**
  - On a phone, AchievementToast has `bottom: 5rem` with no `--tabbar-h` or
    `--sa-bottom` (`AchievementToast.svelte:188-194`). It lands on the tab bar
    and on the coach bar.
  - Toasts cover LootReveal (z60 over z55).
  - The loot reveal plus the "New record" toast hide the whole header,
    including Menu.
  - The coach bar covers Chest row buttons at rest.
- **The bottom chrome stays above the keyboard.** There is no
  `scroll-padding`, so a focused field near the bottom can sit behind the tab
  bar **(device)**.
- **Keyboard hints are missing:**
  - no `enterkeyhint="send"` on the chat input (`Chat.svelte:381-389`);
  - no `autocapitalize="none"` or `autocorrect="off"` on usernames
    (`Chat.svelte:374`, `Login.svelte:141`).

**Build.**
- **Portal** ContextMenu and PlayerProfileModal to `<body>`, using
  PersonPicker's `portal` action.
- **Fix the chat row:** the sender goes above the message, or the `.who`
  column gets a max-width with ellipsis.
- **PlayerProfileModal:** all 11 slots, item names, dialog role, theme
  tokens.
- **Size the chat window from `100dvh`** minus `--sa-top`, the tab bar and
  the handle.
- **Make `openSheetCloser` a stack**, so back and Escape close the top layer:
  - What's New calls `acknowledgeNotes()`;
  - the profile and the context menu close themselves;
  - the wheel consumes back during play and closes in its result phase.
- **Scroll:** to the top on forward navigation, restored on back.
- **TabBar** sets `navOpen = false`.
- **OfflineSummary** moves to the modal layer and gets a max-height, inner
  scroll and `stopPropagation`.
- **Modal insets:** backdrop padding `max(1rem, var(--sa-*))` and `dvh` caps.
- **ConnectionNotice** gets `top: var(--sa-top)`, plus a status-bar scrim
  **(device)**.
- **One bottom notification stack**: toasts, the achievement card, the record
  toast and the coach, with a gap. Toasts stay clear of the loot reveal.
- **An `html.typing` class** hides the tab bar and the coach while a field
  has focus, plus `scroll-padding-bottom`.
- **The keyboard hint attributes.**

**Done when:**
- `tests/backButton.test.ts` has rows for What's New, the profile, the
  context menu and the wheel.
- On a phone viewport, a profile opened from chat fills the screen, and a
  20-character name leaves the message readable. Screenshot both.
- Chest → Village → back returns to the Chest's scroll position.
- "Welcome back" covers the tab bar and scrolls.
- The four checkers are clean.
- **On a phone:** typing in chat keeps the dock's header on screen, and a
  focused Market price field stays visible.

**Size:** L.

## 91. Money and feedback

**IN PROGRESS on branch `claude/ui-money-feedback`.** Commits so far cover:
- gold formatting and the Mailbox gate;
- required notice tone and the deed reward;
- pre-snapshot copy, grouped quantities and guild medals;
- the equip picker cap;
- the leaderboards.

Report sections 4.F, 4.G and 4.J.

**What is actually true.**
- **"150 kg".** 27 sites append a bare `g` to `formatNumber`, so a compacted
  value prints "150 kg" or "1.24 Mg". By file: Market 10 (e.g. `:363`,
  `:440`), Forge 7 (`:497`, `:556`), Chest 2 (`:267`), Mailbox 2 (`:91`),
  Wiki 2, and one each in Breeding (`:372`), VillageFolk (`:144`) and
  ChildPreview. `Money.svelte:42-58` documents the player report about this,
  and it is used at only 21 sites.
- **Successes in the error tone.** `pushLocalNotice` defaults to `'error'`
  and plays the error sound (`stores/game.ts:392,411`). Affected:
  - "Member kicked/promoted/demoted" (`GuildOps.svelte:141,155,169`);
  - "Friend request sent" and "Player blocked" (`Chat.svelte:170,179`);
  - the Book of Deeds seals and diamonds (`BookOfDeeds.svelte:60,75-77`).
- **The achievement card shows the next tier's reward.** It reads
  `row.NextTierReward` from the snapshot after the crossing
  (`stores/game.ts:677`; server `AchievementMilestones.cs:239-254`). The top
  tier therefore shows no reward.
- **Mailbox shows "Your backpack is full" before the first snapshot**
  (`Mailbox.svelte:26-27,74-79,144`) and disables claims. It can never fire
  afterwards, because the server pins the value
  (`InventoryCensusTickCoordinator.cs:44-59`).
- **The Character equip picker renders every owned piece for the slot**
  (`Character.svelte:131-145,605-623`). Tool slot 8 uses it, and the fixture
  holds 7,550 Birch Axes.
- **Raw quantities.**
  - `x{qty}` at `Boosts.svelte:152`, `Mailbox.svelte:118`,
    `SessionLoot.svelte:224` and `GuildOps.svelte:623`.
  - "131m of 120m" (`Boosts.svelte:191`), where the 120 is a second copy of
    `MAX_BUFF_TICKS`.
  - A stale comment at `Money.svelte:62`.
- **Pre-snapshot copy differs.** There are three "Waiting for…" variants, and
  the header prints the raw connection phase (`App.svelte:729-733`).
- **The leaderboards have four faults.**
  - `.board li` is a 4-column grid fed 5-6 children
    (`Leaderboards.svelte:244-251,90-116`), so the progress line lands in the
    2.5rem rank column as "Mou…" at every width.
  - The tier colours are #ffd970 and #e2e8f0 on cream
    (`leaderboardTiers.ts:43-49`).
  - Only the "Deepest" tab can open a profile (`:61`).
  - An error reads "No ranked players yet." (overlaps 93).
- **Guild medals and guild names.**
  - "1st/2nd place" are #f0c040 and #c0c0c0 on cream
    (`GuildOps.svelte:1077-1079`).
  - The guild name input has no `maxlength` (`Social.svelte:242`; the server
    allows 1-100 characters, `GuildManagementEngine.cs:89`).
  - Toasts do not wrap user text (`Toasts.svelte:54-66`).

**Build.**
- **Every gold amount goes through `<Money>`**, or a `formatGold()` for
  strings. Add a grep test against `formatNumber(...)}g`.
- **Make `tone` a required parameter.**
- **Achievement reward:** price the tier just crossed, from the
  before-snapshot or a server field.
- **Delete the Mailbox gate.**
- **Equip picker:** cap it, and collapse identical pieces.
- **Quantities:** use `formatNumber`, and derive the cap.
- **Pre-snapshot:** one line, and no phase in the header.
- **Leaderboards:**
  - named grid areas;
  - a light-theme tier palette;
  - profiles from every row;
  - an error state;
  - "rating" for "MMR";
  - optionally a pinned "your rank" row (needs the server to return it).
- **Guild:** medal colours that work on parchment, a `maxlength`, and
  `overflow-wrap: anywhere` on toast, achievement and loot-reveal text.

**Done when:**
- The grep test passes.
- A 390 px Market, Forge and Mailbox show "150 k gold", never "kg".
- A deed card's reward matches the diamonds paid.
- Opening the axe slot on the fixture stays responsive.
- The leaderboard's second line is readable at 390 and 1440.
- `exercise.mjs` is green.

**Size:** M.

## 92. Confirm, pending and visible reasons

**IN PROGRESS on branch `claude/ui-confirm-hints`.** Report sections 4.E and
4.F.

**What is actually true.**
- **One tap, no confirm:**
  - Guild Kick (`GuildOps.svelte:136-148,837`).
  - Villager "Send on", which is permanent and stays tappable for 900 ms
    (`VillageFolk.svelte:41-55,131-137`).
  - Ancestors "One more slot", which spends diamonds
    (`Ancestors.svelte:63-67,152`).
  - Friend Remove and Block (`Social.svelte:222-225`).
- **Two native `confirm()` calls** remain: attribute respec
  (`AttributePanel.svelte:54`) and high-rarity reroll (`Forge.svelte:403`).
  In the WebView they render unstyled.
- **WebSocket buttons have no in-flight state.** Market Buy
  (`Market.svelte:177-183,364`), Mailbox Claim, Forge Fuse, Crafting Craft
  and Send on can double-submit, giving a success toast and then "Target not
  found." Village's `pendingId` (`Village.svelte:212`) is the model.
- **Reasons that exist only in `title`, invisible on touch:**
  - Skill-tree buy and respec (`SkillsPanel.svelte:453,479,501,526`).
  - Great Works deposit (`GreatWorks.svelte:98-99`).
  - Chest menu Sell (`Chest.svelte:413-416`).
  - Inheritance (`Inheritance.svelte:111-112`).
  - The Combat first-clear chip and estimate (`Combat.svelte:674,680`).
  - SessionLoot Wear (`SessionLoot.svelte:232`).
  - The Village requirement (`Village.svelte:213`).
  - Gathering caveats (`Gathering.svelte:336-342`).
  - Chest "Locked" (`Chest.svelte:789`).
  - The event chip's effect on a phone (`EventBanner.svelte:55,87-98`).
  - The aptitude numbers (`VillageFolk.svelte:122-125`,
    `Ancestors.svelte:205`).
- **Disabled with no reason anywhere:**
  - Crafting "Put to work" (`Crafting.svelte:227-233`).
  - Ancestors "One more slot" (`:152`).
  - Forge Fuse, which has 6 conditions and shows only the gold one
    (`Forge.svelte:504-511`).
  - Social Join when the guild is full.
- **Guild Join ignores `MinApplicationLevel`** (`Social.svelte:268-270`). A
  level-1 guest gets an enabled Join on a "lv 20+" guild, and a server
  refusal.

**Build.**
- **A `ConfirmButton`.** The first tap arms it and relabels it ("Really
  kick?"); the second commits; it disarms after 4 s. Use it for Kick, Send
  on, One more slot, Remove and Block, and replace both native `confirm()`
  calls.
- **An `inFlight` set keyed by target id.** It is set on send and cleared on
  the next command result or after 3 s.
- **A `Hint` component**: tap-to-toggle, the TraitBadge pattern.
- **Every disabled button shows its reason** in its label or on a `.dim`
  line under it, never only in `title`. The event chip becomes tappable.
- **Join** respects the minimum level, with a "Lv 20" hint.

**Done when:**
- No destructive or diamond-spending action commits on one tap.
- A double tap on Buy or Claim sends one command.
- A greyed Skill-tree node, deposit and Sell each say why at 390 px.
- `exercise.mjs` is green; its Kick, Send on and slot steps press twice.

**Size:** M.

## 93. Load errors stop looking like empty states

**BUILT on branch `claude/ui-query-states`, not merged.** Two commits:
- ca944db: QueryState/QueryError, and the content registry as a query.
- fbbf66c: a failed query says so instead of claiming "empty".

Report section 3, item 1.

**What is actually true (on `main`).** The global `retry: 1`
(`net/queryClient.ts:43`) means a dead endpoint surfaces in about a second.
Then:
- **Chest** reads "Nothing here." (`Chest.svelte:751-754`). Players primed by
  the 17k-row incident read that as item loss.
- **Leaderboards** read "No ranked players/guilds yet."
  (`Leaderboards.svelte:52-55,82-85,126-129`).
- **Social** reads "No guilds exist yet. Create the first."
  (`Social.svelte:249-252`).
- **Chat** reads "No conversations yet."
- **GuildOps** reads "No members listed." (`GuildOps.svelte:811-814`).
- **Progression** shimmers forever (`Progression.svelte:92,119-121`).
- **Boosts and Larder** stay on a skeleton when `loadContent()` rejects.
- 13 of 21 query-using route files never read `isError`.

**Build (done on the branch).** A shared `QueryState`/`QueryError` that
handles pending, error (with Retry) and empty, adopted on the screens above.
A guard test flags `createQuery` in a file with no error handling.

**Known leftovers, to finish before or right after merging:**
- **The GuildOps content load has no error handling.**
- **The Wardrobe/cosmetics tab** says "needs a guild" when the membership
  check *failed*. That is the same lie in a new place, because `hasGuild`
  derives from a query that can fail (`Social.svelte:39`,
  `GuildOps.svelte:44`).

**Done when:**
- With the API stopped, every screen above says "Could not load …" with
  Retry.
- The guard test passes.
- The two leftovers are fixed.

**Size:** M.

## 94. Leave guild

**BUILT on branch `claude/ui-94-leave-guild`, not merged.** Report section 4.F (states audit F11).
Open: the 390 px screenshot of the armed confirm and the `exercise.mjs` run are
the coordinator's (not run here). When the fixture is a guild's last member the
exercise step closes and refounds it, so that guild's depot, treasury and buffs
reset each run. A closed guild's depot/buff/war rows are left orphaned, as
before. The guild-name cap is now 32 on the server too.

**What is actually true.**
- `Social.svelte:245-246` tells a guild member "Leave it first to join or
  create a new one."
- `GuildManagementEngine.LeaveGuildAsync` exists
  (`server/FolkIdle.Server/Domain/Social/GuildManagementEngine.cs:331`), and
  leader succession is handled in it (`:395-401`).
- A grep finds **no caller** of `LeaveGuildAsync` on the server and no leave
  call in the client. A player in a dead guild is stuck, and every Join and
  Create stays disabled (`Social.svelte:243,272`).
- This is the "grep for a WRITER" trap from CLAUDE.md, in the form of a
  route.

**Build.**
- **Server:** a REST route under the account stripe lock (a mutating POST,
  per CLAUDE.md), or a WebSocket command through the `add-command` skill. It
  calls `LeaveGuildAsync`.
  - Decide what the client must refresh: membership, roster and chat
    channels.
  - Write a server test: leave as a member, and leave as the leader, where
    succession picks the next member.
  - Write a second test for the last member leaving. Read what the engine
    does to an empty guild before you promise anything in the UI.
- **Client:** a "Leave guild" button on the Guild tab.
  - Use `ConfirmButton` (92).
  - A leader is told who will lead next, or that the guild will close.
- Until this ships, change the "Leave it first" copy so it does not promise
  an action that does not exist.

**Done when:**
- The server tests pass.
- `exercise.mjs` leaves and rejoins a guild, round-tripping the fixture's
  membership.
- A 390 px screenshot shows the confirm.

**Size:** M (touches the server). **Needs the owner:** no.

## 95. Phone navigation: More sheet, sticky header, one name per concept

**BUILT on branch `claude/ui-95-nav`, not merged.** Renames owner-approved
2026-10-02. The fifth tab is More, a bottom sheet holding every other
destination plus the chat entry. It badges unclaimed mail and dots unspent
skill points. Village moved into the sheet, and hotkey 5 still opens it.
The header is sticky and one row on a phone: name, purse, ≡. Its measured
height is published as `--sticky-header-h`, which ConnectionNotice,
Character's switcher, Chest's detail, the Wiki sidebar and the chat sheet
add to their offsets. Every tab family has a breadcrumb and one tab row
that scrolls sideways. On desktop the active group is bold and underlined,
and the coach-mark on a group toggle is a dot. There is one chat entry
(`ChatButton.svelte`) with "N online": in the header on desktop, in the
sheet on a phone. On a phone the chat is a full-height sheet with the
sender above the message, and the Guild channel is hidden without a guild.
Groups are now Play / Hero / Make / Friends & Guilds / Game, and group
labels use `--fs-xs` at full dim colour. Renames: Map -> Home,
Supplies -> Auto-Eat, Community -> Friends (Guild and Market have entries
of their own), and Bloodline is the family name on the page and in the
Wiki. Time convention: `lib/ui/when.ts`. `screens.mjs`, `exercise.mjs`
and `mobile-check.mjs` use the new labels. `navButton` reaches the tab bar
and the sheet, and the new destination "Home · More sheet" measures the
sheet open. Verified so far: `check:ratchet` 4 and vitest green.
**Not yet run:** `exercise`, `check:touch`, `check:overlap`, the 390px
two-tap check and the owner's screenshot. "The Deep" vs "the Delve" was
not touched. Report sections 4.M and 8 (sketch R1). This is visual review
C, N1-N7.

**What is actually true.**
- **The Menu is only at the top.** The header is in flow, not sticky
  (`App.svelte:899-906`).
  - The TabBar has 5 fixed entries and no "More" (`lib/ui/tabs.ts:6-12`).
  - "Menu · X" sits top-right (`App.svelte:1025-1041`).
  - Of 26 destinations, 21 are reachable only after scrolling to the top and
    reaching the far corner.
- **On desktop, "you are here" is weaker than the tutorial.**
  `.group-toggle.active` only changes the background to `--bg-raised`, which
  is almost the header colour (`App.svelte:976-978`). The coach ring is a
  2 px accent outline plus a pulse (`App.svelte:1146-1151`), so the header
  points at **Items** while you are in Community.
- **One concept has several names:**
  - The **Bloodline** menu entry opens tabs "Breeding / Ancestors /
    Inheritance", and the word Bloodline appears nowhere on the page. The
    Wiki says "Breeding".
  - The **Community** group contains an entry also called Community, which
    opens a page whose tab and heading are **Friends**
    (`App.svelte:122,137,142,165-182`).
  - **Supplies** vs **Auto-Eat** vs larder: the first guided step says "Go to
    Auto-Eat".
  - **The Delve** vs **the Deep**.
  - **Map** vs a screen that is two-thirds dashboard.
  - Resets are written as **"midnight UTC"** on World Boss and **"Monday,
    02:00 CEST"** on Delve, for the same instant.
- **The phone menu groups are odd.**
  - "You" mixes Skill Tree and Progress with Settings and Wiki.
  - Market lives only as a Community tab.
  - Group labels are 0.6rem at 65 % opacity (`App.svelte:926-933`).
- **"Close · Map" reads as "close the map"** (`App.svelte:633`).
- **Chat has two entry points**, depending on who is online.
  - `$chatHandleInHeader` (`App.svelte:719-726`) gives a header button.
  - ChatDock gives a floating pill.
  - With the dock open, both close controls show.
  - The Guild channel shows to the guildless.
  - The online dot has no label.
- **Chat is cramped on a phone.** It is a floating 26rem window with double
  frames and about 130 px of message area. The channel tabs wrap.
- **The guest tab row wraps** because of the lock suffixes (+52 px).

**Build.**
- **The fifth tab becomes More**, opening the grouped nav as a bottom sheet.
  - Village moves into the sheet.
  - The sheet carries badges for mail and points.
- **A slim sticky header** (`top: var(--sa-top)`) with ≡, gold and diamonds.
- **A "Family > Tab" breadcrumb** above every tab row, which becomes one
  scrollable row.
- **Desktop:** the active group gets an accent underline and bold text, and
  the coach mark becomes a dot.
- **One name per concept** across menu, tab, heading, Wiki and tutorial.
  - Proposed: Bloodline (family) > Breeding / Ancestors / Inheritance;
    "Friends & Guilds" or no group; Supplies > Auto-Eat; "the Deep" is
    introduced once as "below floor 8"; Map → Home.
  - Show the full rename list to the owner first; 76 set the precedent.
- **One time convention:** local time plus relative ("Mon 02:00 - in 3 d"),
  from a shared formatter.
- **One chat entry**, in the header or the sheet, with an "N online" label.
  - On a phone, chat is a full-height sheet with one frame, one scrollable
    channel row, and the sender stacked above the message (report section 8).
  - Hide the Guild channel without a guild.
- **Regroup the phone menu** (visual review C, N3) and raise the group labels
  to `--fs-xs` at full dim colour.

**Done when:**
- From the bottom of the Chest at 390 px, Mail and Settings are two taps away
  without scrolling.
- The owner has approved the renames and a screenshot.
- `screens.mjs`, the Wiki and the tutorial copy use the new names.
- `check:touch` and `check:overlap` are clean with the sheet open.
- `exercise.mjs` is green; it navigates by labels, so update them.

**Size:** L. **Needs the owner:** yes, the renames and a screenshot.

## 96. Settings

**BUILT on branch `claude/ui-96-settings`, not merged.** One column of
closed panels (`lib/ui/SettingsFold.svelte`, open state remembered per
browser), Account with Sign out first and never folded; explanations list only
the seen ones plus "N more unlock as you play"; translation keys/coverage, cue
tester and Session moved into an admin-only Developer panel; admin grid
`minmax(min(300px, 100%), 1fr)`. Still open: the page height, Sign out's
position and 360 px fit are unmeasured (no browser run on the branch), and
`exercise.mjs` step 5 was rewritten for the folds and has not been run. Owner:
the screenshot. Report section 5. This is visual review C, S1-S5.

**What is actually true.**
- **The page is 7,417 px tall at 390** (`settings-fixture-390.png`).
- **Sign out, the only sign-out on a phone, is about 6,900 px down.** The
  header hides it there (`App.svelte:1063-1066`).
- **The "Tutorial" panel holds everything.** It has 27 fully expanded
  explanation cards, plus Auto-salvage, Notifications, Email, Accessibility
  and Session, all as `<h3>` under `<h2>Tutorial`
  (`Settings.svelte:500-635`).
- **A guest sees all 27 explanations in full** ("2 of 27 shown so far"), so
  every future system is spoiled in one wall.
- **Developer text is visible to players:**
  - raw keys "EventNone" and "ActiveEventPrefix" in the language sample
    (`:408-411`);
  - "Only 30 keys exist…" (`:414`), and "30/30" coverage per language;
  - cue ids such as buttonClick and rollFlare (`:491`);
  - "one-time interlock… save generation";
  - "Player #1 / Last save 0s ago".
- **Two heading styles** on one page.
- **The admin grid** uses `minmax(300px, 1fr)` (`:1072`), which overflows a
  360 px phone. It is admin-only.
- **On desktop**, four columns of very unequal height.

**Build.**
- **Separate panels:** Account (with Sign out) first, then Language, Sound,
  Notifications & email, Gameplay (auto-salvage), Accessibility, Tutorial &
  explanations, Support / About / Delete.
- **Fold the 27 explanations** into one disclosure ("14 of 27 seen"). Show
  only the seen ones, plus "N more unlock as you play".
- **Put the developer content behind the admin flag:** the sample keys, the
  cue tester and Session.
- **Copy:** "partially translated" instead of "30/30", and cut the interlock
  sentence.
- **One heading style.**

**Done when:**
- Sign out is in the first 390 px viewport.
- The page is under about 2,500 px at 390 px with the panels closed.
- A guest sees no unseen explanation text.
- An admin still reaches the cue tester.
- `exercise.mjs` is green (it signs out by the label).

**Size:** M. **Needs the owner:** a screenshot.

## 97. Character: gear first on a phone

**BUILT on branch `claude/ui-97-character`, not merged.** Still open: run
`exercise.mjs` (new gear-grid, take-off/re-wear and tab-aware steps) and the
four geometry checkers on the merge; a 390 px guest screenshot for the owner;
check by scrolling that the switcher actually sticks on a phone; a 24-character
person name looked at, not just reasoned about. Attributes stay account-wide
and health is slot 1's only (no per-person wire data for either); DPS is the
server projection's monster health over seconds-per-kill. Report sections 4.H
and 8. This is visual review A2, CH-1 to CH-10.

**What is actually true.**
- **The gear slots are about 1,500 px down at 390 px**, behind
  Character/Health, Combat rating and four attribute cards (about 1,000 px).
  - The "Wear a weapon" deed says "Open Character and tap the weapon slot".
  - For a guest, the first screen is four cards of zeros with disabled
    +1/+10 buttons.
- **The character has no name or level on its own screen.**
  - The title is the word "Character", and the doll says "Human / Adult /
    Idle".
  - The name appears only in Orders ("Slot 1 - Cadoc").
  - The level appears only in Orders copy.
- **The slot switcher exists only inside Equipment**, while Combat rating,
  Attributes and Health are per-slot too.
- **Orders, for a new player:** twelve dead controls ("Opens at level
  20/40/60") and a disabled Save. It also lists a slot that Work calls
  locked. There are three select styles.
- **Changelog copy:** "…used to share one row" (`Character.svelte:656`).
- **"Skill pts 40" in Combat rating** links nowhere.
- **"→ Prospector"** is unexplained.
- **Population "184/35"** has no over-cap marking (`:733`) **(fixture?)**.
- **The equip picker is not windowed** (fixed in 91).
- **Untested:** a long *person* name in the header or in Orders (the audit
  tested only the account name).

**Build.**
- **A sticky person switcher** with portrait, name, level, race/class and
  activity. It drives every panel.
- **Segmented tabs: Gear | Attributes (badge = unspent) | Work & orders.**
  - Gear is the default. It shows all **eleven** slots as a 4-column icon
    grid (8 combat + Axe, Pickaxe, Rod) and the set line.
  - Attributes is the default only when points > 0 and the weapon slot is
    filled.
- **Orders:** below level 20, collapse to one line, and hide the people in
  locked slots.
- **Copy:** fix the Work and Prospector text. Replace Skill pts with Attack
  or DPS.

**Done when:**
- At 390 px a guest sees the weapon slot in the first viewport.
- All 11 slots are present.
- The tutorial's "tap the weapon slot" step completes.
- `exercise.mjs` equips and unequips by the new layout.
- The checkers are clean.
- A person renamed to 24 characters fits.

**Size:** M-L. **Needs the owner:** a screenshot.

## 98. Combat: phone redesign

**BUILT on branch `claude/ui-98-108-combat-gpu`, not merged.** Report sections 4.H and 8. This is visual review A1, C1-C11. Left open: nothing was run against a live stack - exercise.mjs (updated: "Stand down", the boss fold, the estimate line), check:perf, the geometry checkers and the owner's screenshot; the fold hides "Young blood", which CAN be met on a first clear (owner call); the strip sits under a sticky header only if that header sets `--sticky-header-h`.

**What is actually true.**
- **The first Fight is about y 915 at 390 px** (about y 1060 for a guest),
  below:
  - a "Not in combat." panel;
  - an empty Loot panel;
  - a 6-line rules paragraph.

  All three are in DOM order (`Combat.svelte:389-720`, rules `:622-630`).
- **Tapping a monster card shows its drop table off-screen.** The table is
  rendered *above* the Monsters panel (`:562-606` vs the card at `:663`), so
  on one column the only visible response is a border colour.
- **Fight is styled like the card** (`:693-699`). The card is the bigger
  target.
- **Rows wrap raggedly.** The verdict ("safe", "you would die") is the last,
  orphaned token (`.row` flex-wrap, `:957-964`).
- **Boss Challenges plus Ascension take about 560 px per region**, including
  a placeholder for unbeaten bosses.
- **The headings are inconsistent.** SessionLoot's `<h2>` is larger than the
  screen's own `h2` (`SessionLoot.svelte:167`). Region banner titles are
  low-contrast.
- **SessionLoot has two nested 16rem scrollers** (`SessionLoot.svelte:306-307`).
- **"XP 0" at level 40** has no denominator. Gold duplicates the header.
- **"Not in combat."** offers nothing when there is no last monster.
- **Desktop** has three equal columns, so the 25-monster list sits in a
  440 px column with about 1,200×940 px blank.

**Build** (sketch in report section 8):
- **A sticky status strip** with level, an XP bar to the next level, HP,
  current target and Stand down. When idle it shows "Pick a monster below"
  or Continue.
- **A one-line loot strip** that expands, or links to the Chest. No inner
  scroller on a phone.
- **Rules behind an (i) per region.**
- **A two-line monster row:** name and a verdict chip, plus a filled Fight
  button.
- **The drop table expands inline** under the tapped card.
- **Challenges and Ascension fold into one line** under the boss, hidden
  until the boss is beaten.
- **Desktop:** a sticky left column and a wide monster table.

**Done when:**
- At 390 px the first Fight is in the first viewport, for the fixture and
  for a guest.
- Tapping a card shows its drops without scrolling.
- `exercise.mjs` (Fight, Continue, Again) is green.
- `check:perf` is no worse.
- The checkers are clean.

**Size:** L. **Needs the owner:** a screenshot.

## 99. Chest: rows with stats, a Worn group, no Gold row

**BUILT on branch `claude/ui-99-chest`, not merged.** Left open: Sell shows
no number, because the price (`VillageChestEngine.ValueEquipment`) never
reaches the client and a client copy of it would be a second source; the
server would have to send it on the inventory row. The server still lets
`/api/v1/chest/sell` and `/discard` take `"gold"`, deleting it for 0 (it has
no items.json price), so the client hiding the row is the only guard.
`exercise.mjs`, `check:perf` and the geometry checkers have not been run on it.
Report sections 4.I and 8. This is visual review B1, C1-C9 and
M1-M3.

**What is actually true.**
- **Rows carry no stats.** A row renders icon, name and rarity only
  (`Chest.svelte:771-830`), so two "Hunter Amulet" T2 Relic rows, one of them
  worn, are indistinguishable. The file's own comment (`:12-16`) says the
  affix roll is what makes them different. The "…" menu has no Inspect
  (`:400-445`).
- **Gold is a material.** `materials` is every stack with Quantity > 0, with
  no exclusion (`:100-104`). A guest sees "Gold 2000" with Sell all and Bin,
  counted in "Materials 1". **Very low effort, high impact.** Check what the
  server does with Sell or Bin on gold.
- **Worn looks like loose.** Only the Equip/Unequip label differs.
- **Nested scrollers.** Materials scroll in a 26rem box (`:985-997`), cut
  mid-row.
- **"Equipment" means two things.** The tab is "equipment minus weapons"
  (`:138`), but the heading is "Equipment 5 638 shown".
- **Material row buttons.** Bin weighs the same as Sell all, 6 px apart.
  Material icons are mostly letter placeholders (art gap).
- **The menu.** Sell shows no price. The header lacks rarity and tier.
- **The rarity glow** is a red halo on parchment (with 108).
- **Desktop:** the card stops at about 880 px of 1440.
- At 768 px, `More` is 32×22 (see 110a).

**Build.**
- **Remove Gold** from materials.
- **A shared `ItemRow`** (106) with a meta line: tier, rarity, the top
  affixes ("+12 % crit, +40 HP, 2 more").
- **A "Worn (n)" group on top** and a Worn chip.
- **Inspect** as the first menu item, opening the Forge's `Affixes.svelte`.
- **Sell shows its estimate** ("Sell - 1,240 gold").
- **Bin moves into the materials' "…" menu.**
- **Materials flow in the page.**
- **Rename the tab** to "Armour & tools".
- **Desktop:** list | detail pane.

**Done when:**
- A guest's Chest has no Gold row.
- Two same-named pieces differ visibly.
- Worn pieces are grouped.
- `exercise.mjs` (equip, sell, bin, sweep) is green.
- Chest scroll in `check:perf` is no worse. It is a VirtualList, so
  `rowHeight` must match the new row (CLAUDE.md).

**Size:** M-L.

## 100. Forge: one fusion row per item

**BUILT on branch `claude/tasks-100-102`, not merged.** One row per base
item (`fusionRows.ts`): worn lines first, counts per rarity with a
`RarityPip` rank, "3 Mythic -> 1 Relic - up to 12k" and Fuse / Stack in the
row; search past six rows; the three selects behind "Choose which ones"; the
explainer moved into Fusion. The one-tap target is the piece with the most
affixes. `/api/v1/forge/inventory` now says `IsEquipped` for every character
and all eleven slots, so a piece worn by character 2 or a worn tool is never
offered. ItemBrowser: the grey slabs were the global button box-shadow;
compact grows to ~8 rows (`clamp(14rem, 45vh, 28rem)`) with a fade while
`VirtualList.moreBelow`; "Sort:" label; rows name their strongest affix. A
global `button.primary` fill. **The owner's "the forge breaks for a second
when I fuse quickly"** was a real disconnect: the next Fuse picked from the
list fetched before the previous fusion committed, re-sent a destroyed
sacrifice, the engine answered TargetNotFound with `InvalidRequest`, and
`ForgeTickCoordinator` turned every `InvalidRequest` into `ForceDisconnect`.
Fixed on both sides - the coordinator always ends in ReloadState (source
guard `ForgeFusionRefusalGuardTests`), and the client hides claimed pieces
until the refetched list no longer holds the sacrifices (8 s backstop).
Verified: `npx vitest run`, `check:ratchet` (4), server suite. NOT run:
`exercise.mjs` (the stack step now uses the row; a new rapid-fuse round trip
was added), the geometry checkers, and the 390 px first-viewport claim.
Report sections 4.H and 8. This is visual review B1, F1-F7.

**What is actually true.**
- **About 75 "Ready to fuse" chips run about 1,750 px at 390 px** before the
  Fuse form.
- **Tapping a chip appears to do nothing.** `pickSet` only fills the three
  selects (`Forge.svelte:184-190`): no scroll, no selected state (`.settag`,
  `:438`). Task 69 already corrected the claim that chips do nothing at all.
  On a phone they *look* inert.
- **The chips are sorted by rarity only and are unbounded.** "Fuse the whole
  stack" (`:510-555`) appears only after picking a target in the dropdowns.
- **The fusion explainer** sits in the Affix reroll panel (`:577-582`).
- **ItemBrowser rows** are grey slabs (`ItemBrowser.svelte:258`; the fill is
  likely a global button style). The compact list caps at 14rem (`:157`), so
  "8 of 8 shown" displays 4.
- **The third filter "Rarity" is the sort** but reads as a second rarity
  filter.
- **The 14 rarities are hard to tell apart by colour**: two greys, two blues
  and three close purples (`app.css:141-154`). The T1/T2 badge is region
  tier, a second confusable number.
- **The Fuse form is three native selects.**

**Build.**
- **Cheap, first:** after `pickSet`, scroll the form into view and mark the
  chip.
- **Proper:** one row per base item with counts per rarity, the next fusion
  ("3 Mythic → 1 Relic, 12k gold") and Fuse / Fuse stack in the row. Worn
  items come first, with search.
  - The manual three-piece choice moves behind "choose which ones".
- **Move the explainer** into Fusion.
- **ItemBrowser:** fix the row style; compact grows to about 8 rows on
  desktop; a fade at the cut edge.
- **"Sort:" label.**
- **A rarity rank pip** distinct from the region tier.

**Done when:**
- At 390 px a fusion for the worn weapon's line is reachable in the first
  viewport.
- `exercise.mjs`'s fuse and stack-fuse steps are green, with the stack
  restored.
- The checkers are clean.

**Size:** M.

## 101. Gathering and Crafting: actions first, real status, guided empty states

**BUILT on branch `claude/tasks-100-102`, not merged.** Gathering: status
-> profession cards -> haul -> a closed "How fast and why". The status names
the job ("Mining Copper Ore - Sunlit Plains", `workers.ts` + `gatheringNodes.ts`,
the node yields pinned against ContentRegistry's loot rows) with Stop beside
it; node rows carry the yield's icon and a filled Gather; mastery is a bar in
each card (`50 * (level + 1)^2`, pinned). **Other slots CAN gather** - checked
first: ChangeActivity takes any character by TargetGuid and
`ProcessAllSlotSubTicks` runs every working slot - so a named `WorkerPicker`
(buttons, not a select) is shared with Crafting; only slot 1's progress is on
the wire, so the bar shows for slot 1 alone. Crafting: commissions are a tab;
with nothing craftable it leads with `firstStepLine` ("the first tools need
Birch Log and Copper Ore") and Go gather; Ready / Missing materials / Locked
(folded); cards show icon, tool slot and speed effect, have/need with a thin
bar, an aligned button group with a filled Craft; "Make 1 / Make 10" with the
batch on the button. Verified: vitest, `check:ratchet`. NOT run:
`exercise.mjs` (updated: mastery read by `data-mastery`, the commissions tab,
the batch toggle, a new status-line check), the checkers, 390 px viewports.
Report sections 4.H, 4.N and 8. This is visual review B1, G1-G7 and
CR1-CR9.

**What is actually true.**

*Gathering:*
- **The first Gather is about y 990 at 390 px**, below the Mastery table,
  the Speed and yield table (with a guild monolith paragraph) and an empty
  "Hauled this session".
- **The status says where, not what:** "Working Sunlit Plains - 40 %"
  (`Gathering.svelte:209-217`). Every region has three nodes. Idle is the
  single word "Idle."
- **Only slot 1 can gather.** The command always uses
  `snap.Slot1_CharacterId` (`:88`), and the copy names no character.
- **The embedded SessionLoot `h2` outranks the card title.**
- **A stray leading "·"** (`:323-325`).
- **Mastery rows have no progress bar.**
- **Developer empty state:** "No gathering nodes in the content files."
  (`:366`).

*Crafting:*
- **A new player sees "0 of 30 craftable now"**: 30 faded cards and every
  button disabled, about 6,000 px, with no next step.
- **Level-locked and missing-materials cards look the same.**
- **Craft and Put to work float with the name length**, so they form a
  ragged column (`Crafting.svelte:215-235,298-303`). `class="tiny-btn
  primary"` (`:220`) looks the same as secondary.
- **The worker picker says "Slot 1"** (`:180-183`).
- **Workshop commissions sit above the recipes.** For a guest that is 9 lines
  of locked prose; on the fixture the recipes start below 850 px.
- **Cards have no icon or outcome.** "Equipment" is printed on all 30
  (`:241-245`).
- **Big counts read as two numbers:** "0/1 226".
- **"Craft x10" is a detached checkbox.**

**Build.**
- **Gathering:**
  - order is status → profession cards → haul → a collapsed "How fast and
    why";
  - the status line is "Mining Copper Ore - Sunlit Plains [Stop]" with the
    active row highlighted;
  - a named character picker, shared with Crafting. **First check whether the
    server accepts other slots for gathering.**
- **Crafting:**
  - with 0 craftable, lead with "Nothing craftable yet - the first tools
    need Birch Log and Copper Ore. [Go gather]";
  - groups Ready / Missing materials / Locked (collapsed);
  - an aligned button group with a filled Craft;
  - an icon, the slot and the effect on each card;
  - named workers ("Aila (Warrior) - hunting Wolves");
  - commissions collapsed into a card or a tab;
  - batch size on the button;
  - compact have/need with a thin bar.

**Done when:**
- At 390 px a guest sees a Gather button and a "what to do" line in the
  first viewport of each screen.
- `exercise.mjs` (gather, craft, put to work) is green.
- The checkers are clean.

**Size:** M.

## 102. Market: Buy / Sell / My orders

**BUILT on branch `claude/tasks-100-102`, not merged.** Phone: segmented
Buy | Sell | My orders; wide: results wide with Sell and orders beside them.
Buy is a search field plus "Filters (n)" opening a `DetailSheet` of chips
(type, region, rarity range), a labelled "Sort:", and two empty states. Sell
is the item browser until a pick, then a summary card and "List Sentry Helm
for 1,000 gold". **My orders needed a server route** - there was none:
`GET /api/v1/market/mine` (open listings and standing orders, both sides,
newest first, max 200; `MarketOwnOrdersTests`). **Cancel added
2026-10-02 (owner: yes):** `POST /api/v1/market/cancel { OrderId }` ->
`MarketEscrowEngine.CancelOrderAsync`, answering `Ok | Sold | Gone | NotYours |
Unsupported`; a SELL's piece returns to the chest as a new row (base, rarity,
affixes, affix lock kept), a BUY's escrowed gold to the row. It locks the order
row FOR UPDATE like the buy and the matcher, so a cancel racing a purchase ends
in exactly one of the two (`MarketCancelTests`). Two-tap Cancel on each My
orders row; `exercise.mjs` lists, cancels and checks the piece is back. Standing orders moved under My orders; the copy says
"Placing an order takes a moment."; sub-tabs underlined; cosmetic sell tiles
say "Rare avatar". Desktop is not a table - the results keep their cards.
`marketFilters.test.ts` now pins the chip design. Verified: vitest,
`check:ratchet`, server suite. NOT run: `exercise.mjs` (market steps
rewritten for the sheet, the request-level slot check, the named List button
and My orders), and the hand measurement the task asks for.
Report sections 4.H and 8. This is visual review B2, MK1-MK6 and
MC1-MC4.

**What is actually true.**
- **On a phone, the browse panel opens with about 650 px of filters**
  (11 slot checkboxes, 5 region checkboxes, two selects and a sort). The
  results start at about y 1060.
- **The empty state blames filters when none are set:** "Nothing matches
  those filters. The market is empty…" (`Market.svelte:338-341`).
- **The sell picker shows 4 rows of 5,625, as grey slabs**
  (`ItemBrowser.svelte:157,258`; the same cause as 100). The chosen item is
  not echoed, so "List for 1000g" does not say what is listed.
- **Developer copy:** "flushes your state to the database first"
  (`Market.svelte:593`).
- **There is no list of the player's own listings or orders on the screen.**
- **"Any rarity" sits next to "Rarity"**, which is the sort.
- **Cosmetics.**
  - The sub-tabs look like the top tabs.
  - Sell tiles show only a name, so duplicate "River Stone"s cannot be told
    apart (`CosmeticMarket.svelte:197-198`).
  - The Equipment tab uses different filter idioms.
- **Desktop:** three equal columns.

**Build.**
- **Segmented Buy | Sell | My orders.** The orders tab needs an API that
  lists them; check the server first.
- **A search field plus "Filters (n)"** opening a bottom sheet of chips.
  Results follow directly.
- **Two empty-state messages.**
- **Sell:** a full-screen picker on a phone → a summary card → "List Sentry
  Helm for 1 000 gold".
- **Copy:** "Placing an order takes a moment."
- **"Sort:" label.**
- **Cosmetic tiles** show "Rare avatar".
- **Underline sub-tabs** (106 `Tabs`).
- **Desktop:** a wide results table with Sell and Orders on the side.

**Done when:**
- At 390 px the first listing, or the empty message, is in the first
  viewport.
- The sell flow names the item before listing.
- `exercise.mjs`'s market steps are green.
- The checkers are clean. Measure by hand, because `overlap-check` skips the
  Market (see 106).

**Size:** L. **Needs the owner:** a screenshot.

## 103. Village: buildings first

**BUILT on branch `claude/ui-103-104-village-ancestors`, not merged.** Report sections 4.H and 8. This is visual review B2, V1-V7. The two fractions, checked against the server: "184/35" is REAL, not a fixture artefact - it is every character the account owns against 10 + 5 x Inn, and no handler enforces that capacity, so it is labelled "Household n / m housing" and marked over without claiming a penalty. "101/11" is a FIXTURE artefact (the seeder tops up 12 villagers with no regard for the cap), but note for the owner: elders still count against the newcomer cap and cannot be sent on, so a real player who marries in Inn+6 villagers stops arrivals for the rest of the season. Still open: no exercise step presses Upgrade (it spends and is not reversible; the new step checks the row shape read-only); a guest's first viewport at 390 px and the checkers were not measured on this branch; exercise.mjs (buildings, feast, Send on, Great Works deposit via the new sheet) was updated and not run.

**What is actually true.**
- **The page order is Gene pool → Village (buildings) → Great Works.** At
  390 px the buildings start at about 1,050 px, after about 260 px of prose.
- **Two population fractions contradict each other.** "101 / 11" with no
  label (`VillageFolk.svelte:75`, newcomers / cap) and "184/35 population"
  (`Village.svelte:128`). Both over cap **(fixture?)**.
- **The gene pool is a 28rem inner scroller cut mid-row**
  (`VillageFolk.svelte:217-218`). Married-in elders sit in the same list. The
  aptitude key sits under the list.
- **The footer text runs onto the panel's corner ornament** on desktop. The
  same defect is on the Bloodline aptitude panel.
- **Building rows.**
  - The level is a lone "5".
  - Buttons are half-width and left-floating.
  - "Maxed" is a disabled button.
  - Affordable upgrades do not stand out.
- **Great Works** is five tall identical cards, each repeating the stage
  ladder, with a 0 % bar that reads as a rule. That is about 1,500 px. For a
  new player all ten deposits are disabled "(0 held)".
- **A new player meets marriage and bloodlines first**, then seven red "Not
  enough" buildings.

**Build** (sketch in report section 8):
- **Buildings first.** Affordable ones sort up, with a "Lv 5 / 5" pill and a
  primary Upgrade only where affordable. Maxed is plain text.
- **Label both fractions** and mark over-cap.
- **Gene pool:** the top N by aptitude sum, "Show all", a collapsed "Married
  in (n)", and the S K E F column header above the numbers. Collapse it until
  Inn ≥ 1 or a newcomer exists.
- **Great Works:** one compact row each (stage pips, "0 / 50 000",
  Deposit → a sheet). The ladder shows once. While nothing is held, collapse
  to one line.
- **Panel bottom padding** for the ornament.

**Done when:**
- At 390 px a guest sees a building and its Upgrade button in the first
  viewport.
- `exercise.mjs` (upgrade, Send on, feast) is green, with the villager pool
  re-seeded.
- The checkers are clean.

**Size:** M.

## 104. Ancestors: carried vs lost

**BUILT on branch `claude/ui-103-104-village-ancestors`, not merged.** Report sections 4.H and 8. This is visual review B2, AN1-AN5. Still open: the Lost list has no filters (sort is total only); the page height at 390 px and the checkers were not measured on this branch; `exercise.mjs` (Hall: carried split, trait sheet, pedigree, keep round trip, fielding) was rewritten for the new structure and not run.

**What is actually true.**
- **The phone page is 37,468 px tall.** `Ancestors.svelte:182-247` renders
  every member of the line, grouped by generation, with no cap, collapse or
  pagination: about 200 rows of about 180 px each.
- **Most rows will be lost.** They are `class:doomed={!m.WouldCarry}`
  (`:186,387-388`), at 45 % opacity: people culled at the next rebirth. The
  list grows with every breeding. This is not fixture-only.
- **"Kept" shows on doomed rows.** Keep and Kept differ by one letter
  (`:239-240`). The rule that marks past the cap are allowed sits in the
  intro.
- **Every row carries "Field into [1][2][3] [Keep]".** Slot state is shown
  three ways.
- **The diamond button is the biggest control.** "One more slot · 250
  diamonds" is full-width at the top. It has no confirm (in 92).
- **Five number chips have no header.** The fifth ("16 / 200") is unique to
  this screen.

**Build.**
- **Two sections.** "Carried into next season (10/10)" is expanded, in
  cull-rank order. "Lost at rebirth (n)" is collapsed, sorted by total, 20 at
  a time or virtualised, with filters.
- **Generation becomes a tag** on the row.
- **A doomed but kept row reads "Kept - over the cap"** in the warning
  colour, with the count shown ("Kept 14/10"). Keep is a pin toggle.
- **Tapping a row opens a detail sheet** (traits, parents, field into slot,
  keep). The list shows one state badge.
- **"+ slot (250 diamonds)"** sits beside the counter. The rules go into a
  "What survives a rebirth" disclosure.
- **A column header "S K E F Total".**

**Done when:**
- The fixture's Ancestors page at 390 px is under 3,000 px with "Lost"
  collapsed.
- No row says "Kept" without saying whether it carries.
- `exercise.mjs`'s field and keep steps are green.
- The checkers are clean. Measure by hand; `overlap-check` skips this tab.

**Size:** M.

## 105. World Boss and the Delve

**BUILT 2026-10-02, branch `claude/ui-105-delve`, not merged or deployed.**
Owner decisions: the Delve goes full dark "underground" (page background
included, both themes), and the boss is named from content.
- *Boss name:* `WorldBossIdentity` names it from monsters.json id 30
  ("Perun's Celestial Avatar", the boss the payout token already belonged to)
  and `/api/v1/worldboss/board` carries `BossName` + `BossMonsterId`. REST
  only, no packet change. Id 30 has no portrait art yet, so `MonsterPortrait`
  shows its initials over the yggdrasil banner.
- *World Boss:* hero block with name, "Active" in the good colour and
  "4d 1h left" (`lib/game/worldBossTime.ts`); the HP numbers sit above the bar;
  the strike is a filled primary straight under HP with a "1 strike ready"
  chip; armour is read-only status and the plate is picked only inside
  "Quick strike (no skill bonus)" (or beside the strike with the wheel off);
  the rules and payout tiers are a disclosure, open until the first strike;
  two columns from 60rem.
- *Shield wheel:* portalled to `<body>`, its own top bar with "Leave
  practice" / "Leave - finish later" (a real strike resumes as "Finish your
  strike"), a one-line legend during play, a solid "Throw - 5 left" button,
  upright plate numbers, and a ring sized to the height.
- *Delve:* the `--ug-*` palette and `.underground` in app.css, no colour
  literals in Delve.svelte (guarded by `tests/worldBossDelveUi.test.ts`);
  the gate leads, the records fold to one line, "Wear a title" with a Worn
  mark, an owned next title is not shown as a goal, one reset time, and the
  glitches fixed.
- `exercise.mjs`'s world boss step now opens Quick strike and reads the spent
  count off the strike chip. **Not yet run**: exercise, check:touch and the
  geometry checks still need a dev box, and the wheel's above-the-header fix
  needs a real phone.

Report sections 4.A8, 4.H, 4.J and 8. This is visual review A1,
W1-W6, S1-S3 and D1-D6.

**What is actually true.**

*World Boss:*
- **There is no name, face or art.** `WorldBoss.svelte:307-322` renders
  `<h2>World Boss</h2>`, a state pill, a timer and a bar, and nothing in the
  file names the boss.
- **The strike is at about y 640, after three paragraphs.** One is an 8-line
  armour rule in 0.75rem dim text (`:351-358`).
- **The plate selector feeds only the secondary button.** It sets
  `selectedPlate` (`:364-389`), which only "Auto-strike plate n (1x skill)"
  uses (`:427-434`). The prominent wheel strike ignores it.
- **Status colours misread.**
  - "Active" is a red outlined pill.
  - The primary strike is red text in a red border.
  - "Ready" is a lone green pip that looks like an empty bar.
  - The HP label is dark on dark orange.
  - "97h 33m left".
- **Desktop:** a 34rem column (`:527-531`), with the damage board below the
  fold at every width.

*Shield wheel:*
- **No leave during play** (Close only at `ShieldWheel.svelte:516,548,557`).
- **The legend shows only during the countdown** (`:419`).
- **About 180 px of empty space**, and upside-down segment numbers.
- **"Tap to throw" looks like a drop zone.**
- **The header and part of the panel show above the overlay**: cause
  unknown, maybe partly a capture artefact **(device)**.

*The Delve:*
- **A private dark palette.** `Delve.svelte:527-781` hard-codes 34 colour
  literals and its own `button.primary`/`.secondary`, so it is a dark island
  on the parchment page. The intro is pale grey on parchment, partly from the
  global `header` rule fixed in 89.
- **"Pay and descend" is the 7th block** (about y 865).
- **Delve vs Deep, and the reset time:** "on Monday" vs "Monday, 02:00 CEST"
  (naming in 95).
- **"Clear floor 10 to earn Lamplighter"** sits above an owned "Lamplighter"
  button with no Wear/Worn label (`:477-503`).
- **Small glitches:**
  - "60 /" wraps from "60";
  - double spaces before units;
  - "-" for none;
  - "BREADTH" in caps;
  - the 4+2 stats grid on desktop.

**Build.**
- **World Boss** (sketch in report section 8):
  - a hero block with the boss name and portrait. **Find where the name can
    come from first:** content data or a wire field. A wire field means
    `generate:protocol` (CLAUDE.md).
  - the strike directly under HP, as a filled primary;
  - plates as read-only status, chosen only in "Quick strike (no skill
    bonus)";
  - rules and payout tiers in a disclosure that is open until the first
    strike;
  - "Active" in the good colour, a "1 strike ready" chip, "4d 1h left";
  - two columns on desktop.
- **Shield wheel:**
  - a portalled overlay above the shell header, with its own top bar and
    "Leave practice" / "Leave - finish later";
  - a one-line legend during play;
  - a solid "Throw - 5 left" thumb target;
  - the numbers drawn upright;
  - no dead gap.
- **Delve:**
  - **owner decision:** a full dark "underground" mode (page background
    included) or the parchment tokens; the audit recommends dark;
  - either way, every hex value becomes a token;
  - the gate goes first and the records fold into one line;
  - a titles picker labelled "Wear a title" with a Worn mark;
  - the glitches fixed.

**Done when:**
- At 390 px the strike and "Pay and descend" are each in the first viewport.
- The boss is named.
- The wheel can be left during practice, and back during play does not
  unmount it (with 90).
- The Delve reads in the owner's chosen theme, and in both themes if the
  choice is parchment.
- `exercise.mjs`'s world boss (with the dev window route) and Delve steps are
  green.

**Size:** M-L. **Needs the owner:** yes, Delve dark or parchment, and where
the boss name comes from.

## 106. Shared primitives and sweeps

**BUILT on branch `claude/ui-106-primitives`, not merged, not run in a browser.** Button vocabulary in `app.css` (default/primary/danger/ghost/quiet, `.tiny-btn`/`.btn-sm` and `.btn-md`; the 12 copies deleted). `ui/Modal.svelte` + `modalStack.ts` (portal, inert app root, focus trap and return, closer stack, `--scrim`, dvh cap, sheet variant); DeathCard, VictoryCard, WhatsNew, OfflineSummary, PlayerProfileModal, the exit confirm and Login's Android promo migrated, `tests/modal.test.ts` keeps the rest on a shrinking list (DetailSheet, GuidedOverlay, ShieldWheel - 105). `ui/Tabs.svelte` (ARIA, roving tabindex, pill/underline) and `ui/ChipGroup.svelte`, adopted on Leaderboards, Market and the gold ledger. `Bar` takes `size`, `tone`, `ariaLabel` on a `--bar-track` well; GoldLedger's `.bar` is `.share`. One `.panel` base and `.panel--sunken` in `app.css` (30 copies trimmed; five panels that had no surface now get one). `folk-rise` fills `backwards`; global `:focus-visible` for `a`, `summary`, `[role=button]`, `[tabindex]`; TabBar press tint; OfflineSummary and Gathering rethemed. overlap-check reaches OVERLAYS through `go()`; clipping-check opens the chat dock. `tests/primitives.test.ts` holds the app.css rules. **Left:** `ItemRow` exists (task 99) but only the Chest uses it - the other five row shapes; the type/spacing sweep (with 109); the z-index literals in components onto the scale; `check:overlap`/`check:clipping`/`check:touch` and the light-theme screenshots were not run (no stack in this session).

**OPEN (original brief).** It runs alongside 95-105, and each redesign adopts what it needs.
Report sections 4.I, 4.J, 4.K and 7.

**What is actually true.**
- **Buttons.**
  - `.tiny-btn` is used 46 times in 14 files and **defined 12 times**, in
    three sizes (`Boosts:331`, `Character:1131`, `Chest:1180`,
    `Crafting:411`, `Gathering:535`, `GuildOps:1029`, `Larder:391`,
    `Mailbox:263`, `Market:812`, `Settings:951`, `Social:458`,
    `Village:534`). It is used undefined in `AutomationRulesPanel.svelte` and
    `Forge.svelte`.
  - "primary" has five meanings and **no rule at all** in `LootReveal.svelte`
    (`:91`, the Wear call to action) and `CosmeticMarket.svelte:212`.
  - "danger" has three meanings.
  - Close buttons come in two classes.
- **Modals:** nine implementations, with no focus management. `inert` is
  used nowhere.
- **Tabs and chips.**
  - 4 `role=tablist`s with 4 class names, 3 non-ARIA tab sets, 2 chip groups
    and 3 `.filters` copies.
  - Four "active" looks, including a live slate-blue tint
    (`Character.svelte:825`).
  - Nested tab rows look the same as their parents.
- **Item rows:** six shapes. Chest, the ItemBrowser grey slab, Mailbox
  "[Epic]", the fusion chip, the Crafting card with no icon and the Larder
  slot with no icon.
- **Bars and panels.**
  - `Bar` is used 16 times, against 11 or more hand-rolled bars. GoldLedger's
    `.bar` collides with the global class, and Bar has no `aria-label`.
  - Progress and Codex meters have no track.
  - The `.panel` base is copied into 29 files. Two copies use the undefined
    `--panel`.
- **Typography and spacing.**
  - 37 font sizes, 23 of them below 0.7rem.
  - 45 spacing lengths.
  - Weight `650`.
  - `line-height: 1.1rem`.
- **Z-index:** 16 values, from 1 to 10000.
- **Colour:** 254 hard-coded colours outside `app.css`.
  - 22 white-alpha borders vanish on parchment: Character, Forge, Market,
    ChatDock, Mailbox, OfflineSummary, Wiki.
  - Text on accent is coded three ways.
  - The Delve remainder is in 105.
- **The pressed state is lost** on the TabBar (`TabBar.svelte:129`).
- **There is no focus-visible style** for `[role=button]`, `a`, `summary` or
  `[tabindex]`.
- **Checker coverage gaps.**
  - `overlap-check` navigates with `navButton()`, so it skips the 10
    destinations that exist only in `OVERLAYS`.
  - `clipping-check` never opens the chat dock.

**Build** (token values in report section 7):
- **`Button`**, or global classes: variants default, primary (filled),
  danger, ghost and quiet; sizes sm and md. Delete the 12 `.tiny-btn` copies.
- **`Modal`:** a portal to body, `role=dialog`, Escape and back through 90's
  closer stack, a focus trap with return, `--scrim`, `--z-modal`, safe-area
  padding, a dvh cap and a bottom-sheet variant. Migrate DeathCard,
  VictoryCard, WhatsNew, OfflineSummary, PlayerProfileModal and the exit
  confirm.
- **`Tabs`** (ARIA, roving tabindex, an underline variant for a nested row)
  and **`ChipGroup`**.
- **`ItemRow`.**
- **`Bar`** with size, tone and `ariaLabel`, plus a visible track. Rename
  GoldLedger's class.
- **The `.panel` base** goes into `app.css`, with `.panel--sunken`.
- **The z-index scale.** `folk-rise` changes from `both` to `backwards`.
- **Retheme** the white-alpha edges and the blue tint onto `--edge-soft` and
  `--tint-selected`.
- **Pressed and focus states:** a `.tab:active` tint and a global
  `:focus-visible` for those selectors.
- **Type and spacing sweep**, file by file, alongside other work.
- **Fix the checkers:** `overlap-check` uses `go()` for `OVERLAYS`
  destinations, and `clipping-check` opens the dock.

**Done when:**
- A grep finds one `.tiny-btn` or `.btn-sm` definition and one `.panel` base.
- Every modal is a `Modal`.
- `check:overlap` visits all 29 destinations, and is clean.
- No `font-size` below `--fs-badge`.
- A light-theme screenshot of Character, Forge and Market shows card edges.

**Size:** L (spread across PRs: one per primitive, plus sweeps).

## 107. Guild and Community structure, and copy contradictions

**BUILT on branch `claude/task-107-guild`, not merged, not run in a browser.** Friends is friends only (`Social.svelte`). The Guild tab shows `GuildBrowser.svelte` (directory with Join/Apply plus Create) while guildless, and otherwise a dashboard in the spec's order: header card (name, tier, members, tax, your role, weekly rank), Members with Leave, Applications (leaders only, `GuildApplications.svelte`), Treasury & buffs, Depot & donations, Weekly material ranking, Guild war last and collapsed while locked. One column, so the desktop hole is gone. The contradiction is settled from the server: gold donations fill the treasury and grant guild XP but write no member points, only Treasury material deposits earn the weekly ranking, and both cards now say so. Donate is one material picker, a quantity with Max, a Depot / Chain / Treasury choice and one "Deposit to ..." button with a hint. `exercise.mjs` follows: it opens the war toggle, clicks "Deposit to Treasury" and rejoins from the Guild tab. Left for the owner: the 390 px screenshot, running `exercise`, `check:clipping/touch/overlap` on both tabs (overlap-check skips them), and the "Open" label for your own guild's row was dropped rather than built, because a member never sees the directory now.

**OPEN (original brief).** Report section 5. This is visual review C, C1-C6 and G1-G5.

**What is actually true.**
- **The "Friends" tab is two-thirds guild.** It holds Friends, Guilds and My
  guild (`Social.svelte:238-316`). "My guild" duplicates the Guild tab's
  Members.
- **The Guild dashboard has no identity.** It never shows the guild's name:
  `GuildName` is used only for `hasGuild` (`GuildOps.svelte:44`). It opens on
  "Guild war: unlock at 50 players, 1/50" (`:450-452`), and Members is the
  last card, about 1,700 px down on a phone.
- **The copy contradicts itself.** Contribute gold "raises your own
  contribution ranking", while Contributors says "only material
  contributions count toward the leaderboard, not gold donations". Find out
  from the server which is true before rewriting either.
- **A new player gets mixed signals.** The tab says "Guild · Level 10"
  (locked), while the same page offers enabled Join and Create (Join's
  minimum level is in 92). Your own guild's row shows a disabled Join.
- **"None pending, or you are not the leader."** makes the player work out
  which applies (`Social.svelte:303`).
- **Donate needs a paragraph to make sense:** "To depot / To chain / Donate"
  are three controls of different widths.
- **Desktop:** an auto-fit grid leaves a hole under the tall Depot card
  (`GuildOps.svelte:850-856`).
- **Medal colours are in 91.**

**Build.**
- **Friends = friends only.**
- **Guild =** the guild browser plus Create when not in a guild, and the
  dashboard when in one.
- **Dashboard order:** a header card (name, tier, members x/y, tax, your
  role, weekly rank), then Members, Treasury & buffs, Depot / donate,
  Contributors, and Guild war last, collapsed while locked.
- **Name the two rankings distinctly,** or state the gold effect correctly.
- **Applications show only to leaders.**
- **Own guild row:** "Open" instead of Join.
- **Donate:** a material picker plus a quantity, then "Deposit to: Depot |
  Chain | Treasury" with a one-line hint.
- **The "Leave guild" button from 94** goes on the dashboard.

**Done when:**
- The guild's name is the first thing on its dashboard at 390 px.
- No copy on the two tabs contradicts the server.
- `exercise.mjs`'s guild steps (donate, apply, kick) are green.
- The checkers are clean. Measure by hand; `overlap-check` skips both tabs.

**Size:** M. **Needs the owner:** a screenshot.

## 108. Phone GPU budget

**BUILT on branch `claude/ui-98-108-combat-gpu`, not merged.** Report section 4.L. Left open: the check:perf numbers (Chest scroll, Chat with 50 messages) were not measured on this branch and belong in the PR; the chat WINDOW keeps its blur (task 90's call); the glow colour is untouched (110f).

**What is actually true.**
- **`.rarity-glow` animates `text-shadow`**, 2.2 s and infinite
  (`app.css:304`). It is applied to every tier ≥10 row (`rarity.ts:100`) in
  the Chest VirtualList (`Chest.svelte:782`), Character (`:529,563,593,629`),
  Mailbox (`:110`), SessionLoot (`:220`) and Forge (`:628`). Each glowing row
  repaints its text every frame.
- **`CosmeticFrame` animates `filter: drop-shadow`**, 3.2 s and infinite
  (`CosmeticFrame.svelte:65`). That reaches every `PlayerAvatar`: every chat
  message, the leaderboards, GuildOps and WorldBoss.
- **The always-visible chat handle has `backdrop-filter: blur(8px)`**
  (`ChatDock.svelte:249`), which re-blurs on every scroll frame. The window's
  blur is removed by 90's portal fix or kept: decide there.
- **`will-change: transform, filter` on the monster sprite is permanent**
  (`Combat.svelte:786`).

All of these already have reduced-motion fallbacks.

**Build.**
- In lists, the glow is static. Animate only in detail views, on a `::after`
  layer via `opacity`.
- The frame glow moves to an opacity-animated pseudo-layer, or is static in
  chat and boards.
- No `backdrop-filter` on narrow screens, with a 96 % colour mix instead.
- `will-change` only during the hit animation.

**Done when:**
- `check:perf` on Chest scroll and on Chat with 50 messages shows no more
  long tasks than before. Record the numbers in the PR.
- No infinite `text-shadow` or `filter` animation is left on a repeated list
  item.

**Size:** S.

## 109. Polish bundle

**OPEN.** These are small, independent fixes. Take them a few at a time, one
PR per screen or per theme. Each line names its report source.

**Login and Register** (A2 LG-1 to LG-3, RG-1 and RG-2):
- **The first impression says nothing.** "Play as guest / Sign in / Create an
  account" are three identical buttons with no pitch and no art
  (`Login.svelte:129-131`).
  - Fix: a filled "Play now" plus a one-line pitch, with the map art.
  - Write the guest note honestly, depending on whether a guest can upgrade
    to an account.
- **The Android promo opens on the very first visit** (`Login.svelte:55`),
  and its "Not now" looks primary while Download looks secondary
  (`:229-234`).
  - Fix: show it after the first session, with Download filled.
- **Register.** Back weighs the same as Create account. There is no username
  hint and no show-password toggle.

**Map / Home** (A1 H1, H2, H3 and H5; A2 MP-1, MP-3 and MP-5):
- The disc labels use `clamp(0.42rem, …)`, about 6.7 px
  (`Hub.svelte:185`).
  - Fix: a ribbon under each disc at 11 px or more, and discs of 44 px or
    more.
- Name the player's own person: "Brennus (you)".
- Below the region-boss threshold, "Next unlock" shows the nearest actionable
  unlock (`HomeCards.svelte:220-222`, `homeNow.ts:60-63`).
- Desktop: `align-items: start` on the cards.

**Skill tree** (A2 ST-2 to ST-8; evidence item 8):
- "40 points" becomes "40 to spend", repeated above the list.
- A guest with 0 points sees "You earn a skill point every N levels" instead
  of a live Respec.
- "Respec(free)" is missing a space (`SkillsPanel.svelte:457`).
- A maxed node shows a bare "—" button (`:482`).
- "pts" next to percentages.
- The cost buttons have no verb.
- The branch labels on the tree picture are about 6 px.
- Desktop: the content width is uncapped.

**Progress** (A2 PR-1, PR-2 and PR-4 to PR-6):
- Collapse sealed chapters to one line, and make done deeds quiet.
- The tabs wrap at 390 px ("Daily & races" alone on a row). Use one
  scrollable segmented row (106 `Tabs`).
- Meters have no track.
- "Monster Slayer / [Monster Slayer]" stutters.
- Desktop: cap the width.

**Codex** (A2 CX-1 to CX-5):
- Developer note: "Only the 25 canonical monsters appear…"
  (`Codex.svelte:77`).
- The page never says what a codex level gives.
- The region completion block costs a phone screen. Fold it into the region
  headings.
- Purple `--rarity-6` kill bars with straddling labels (`:162`).
- "lv 0".
- Check blank portraits on a phone: probably lazy-load in the capture.

**Wardrobe** (A2 WR-1 to WR-5):
- Four "0" chest cards come first. With none held, show one line.
- Locked tiles are disabled with no "how to get" (`Wardrobe.svelte:195,228`).
  Make them tappable to show the source, after checking whether content data
  has one.
- Faded rarity labels on locked tiles.
- Unexplained "×4".
- No Owned/All toggle.

**Wiki** (C W1 to W3):
- The 16-entry table of contents sits above the article (about 1,300 px at
  390, `Wiki.svelte:1619-1644`).
  - Fix: a "Contents ▾" control and Previous/Next links.
- Desktop prose: `max-width: 70ch`.
- The low-contrast Gold/Diamonds card.

**Loot reveal and achievement toast** (B1 LR1 to LR3 and AT2/AT3; evidence
item 5):
- "New record" is announced twice. Fold it into the reveal as a badge.
- "Same rarity as yours" sits next to a primary Wear
  (`lootCompare.ts:15,78-90`). Say "same base stats - compare affixes", and
  make Wear secondary.
- The bare "III" on the seal: write "Treasury - tier III".
- The card should open the Book of Deeds.

**Supplies: Auto-Eat** (B1 S1 to S5):
- The flow runs bottom to top. Make each slot a row: "+ Add food" opens a
  picker.
- No heal amounts and no icons.
- The desktop slider is browser-blue, because the range styles sit inside the
  phone media query (`app.css:1113,1134-1170`).
- "50" has no unit, and "Applied (50)" looks disabled.
- Changelog copy at `Larder.svelte:178`.

**Supplies: Boosts** (B1 B1 to B3; states F3):
- The copy talks about a backpack and a bank (`Boosts.svelte:125-127`),
  which the file's own comment (`:60-70`) says is untrue. Also check the
  "four foods and two potions" count against items.json.
- The empty state is a dead end. List all eight boosts with a source.
- "Refused by disconnecting you" (`:131-135`).

**Mailbox** (B1 ML1, ML2 and ML4):
- Developer explainer (`Mailbox.svelte:67-71`).
- The empty state does not say what mail is for.
- "Claim up to 10" exposes a batch limit. Use "Claim all" that loops.

**Bloodline: Breeding and Inheritance** (B2 BR1 to BR5, IN1 and IN2):
- Five blocks of rules prose around the lab. Keep one line per control, and
  put "partner is spent for ever" next to Breed.
- "Choose up to 1" sits on checkboxes (`Breeding.svelte:338`). Use "Choose
  one" with radio behaviour.
- The unexplained mark tick (`AptitudePanel.svelte:74`).
- The footer collides with the ornament (shared with 103).
- **Verify against the fixture:** the Inn roll cap copy ("up to 9",
  `Breeding.svelte:311-313`) vs newcomers at 10.
- Inheritance: "not bought" should state the current and maximum effect.
  Cap the desktop grid at 3 columns.

**Shared small type** (CSS A3):
- Item stack and tier badges are 9.3 px (`ItemIcon.svelte:124,151,164`).
- Tab-bar labels are 10.9 px (`TabBar.svelte:168`).
- PersonPicker is 8.8 px (`PersonPicker.svelte:331`).

**Tutorial and new player** (evidence items 6 and 8; C T1 to T3):
- **The tutorial reappeared** after a skip, on sign-in in a new browser
  context. Find where the skip is stored. If it is per browser, store it on
  the account.
- **The guided card and the coach pill show at once.** Hide the coach while
  GuidedOverlay is up. "Skip tutorial" should be a right-aligned link.
- **A guest sees "you would die" on every Sunlit Plains monster**, plus
  "Your larder is empty" (`combat-guest-390.png`). Check whether that is
  honest for a level-1 character with the starter claymore and 10 fish. If
  it is not, fix the estimate. If it is, the first monster is a wall again
  (the 2026-09-02 precedent).
- **Locked destinations are offered.** The Map shows Market and Guild
  hotspots to a level-1 player. The Chest menu offers "Reroll in Forge" while
  the Forge is locked.

**Developer-phrased empty and loading states** (states F5 and the empty-state
table):
- "No gathering nodes in the content files." (`Gathering.svelte:366`).
- "No region requirements are defined." (`Codex.svelte:128`).
- "Join a guild to use its depot." with no link (`GuildOps.svelte:569-570`).

**Done when:** each item is fixed or explicitly dropped in its PR. 390 px
screenshots of the touched screens. Checkers clean. `exercise.mjs` green.

**Size:** L in total, about S per line.

## 110. Decisions for the owner

**OPEN.** Ask these in one message, in Czech.

- **a. A touch floor for tablets.** **DECIDED 2026-10-02: yes. BUILT on
  `claude/ui-106-primitives`:** the global floor in `app.css` and the scoped
  floors in ContextMenu and AttributePanel read
  `(max-width: 40rem), (pointer: coarse)`. Not yet measured at 768px with a
  coarse pointer; scoped component sizes outside a floor block may still be
  short there. At 768 px, `check:touch` finds 153
  controls under 44 px. Examples: header toggles 32 px tall, Chest `More`
  32×22, Supplies `+`/`−` 24×26, Ancestors trait buttons 18 px. The floor
  lives only in `@media (max-width: 40rem)`, and a portrait tablet is
  744-834 CSS px, so the APK on a tablet gets desktop sizes.
  - Option: apply the floor under `(pointer: coarse)` as well as the
    40rem query.
  - **Question:** are tablets a target?
- **b. iOS portrait-only too?** `ios/App/App/Info.plist:64-69` still allows
  landscape. Android phones are now locked.
- **c. MOBILE.md wording.** The A2 checklist's "Rotation" line should read
  "Android phone: locked; tablet, foldable and split-screen: still rotates".
  Add the phone checks from report section 6 to the same list. This is a doc
  change; it needs the owner only to confirm the phone list.
- **d. Mobile web as a supported path?** If yes, add a `manifest.webmanifest`
  and an apple-touch-icon (`index.html` has neither), so "Add to Home Screen"
  works.
- **e. Is world chat live-only by design?** A player who signs in again a
  minute later sees "Nothing in this channel yet" (evidence item 7). If
  history is meant to load, that is a defect to file.
- **f. The rarity glow's colour.** **DECIDED 2026-10-02: a gold sheen. BUILT
  on `claude/ui-106-primitives`:** `--rarity-sheen` (pale gold dark, darker
  gold on parchment) draws `.rarity-glow`, the loot reveal's halo and
  ItemIcon's box glow, for every tier that glows (10+); the name and border
  keep the tier colour. On parchment, the top-tier red halo reads
  as an error (Chest, Character, loot reveal). Choose between a gold sheen, a
  rarity pip and a left border. 108 removes the animation regardless.

**Size:** S. **Needs the owner:** yes.

**Owner decisions (2026-10-02):** b - iPhone portrait-only; d - yes, the web
app should be installable; e - world chat loads its history at sign-in.
a and f are handled on another branch.

**b, c, d, e BUILT on branch `claude/ui-110-chat-mobile`, not merged.**
- **b.** `Info.plist`: the unsuffixed (iPhone) orientation list is portrait
  only; `~ipad` keeps all four, because iPad multitasking requires them
  unless the app sets `UIRequiresFullScreen` (not done - a separate call).
  `nativeProjects.test.ts` pins both platforms.
- **c.** `MOBILE.md`'s A2 checklist: Rotation per platform (phones locked;
  tablet, foldable, split-screen and iPad still rotate), the audit's
  section-6 phone checks, and "Add to Home Screen" on both mobile browsers.
- **d.** `public/manifest.webmanifest` (standalone, portrait, dark `--bg`)
  plus `apple-touch-icon` and the `apple-*` metas in `index.html`; the 192,
  512 and 180 px icons come from `resources/icon.svg` via `generate:icons`.
  A test checks every file the manifest and the touch-icon link name exists.
- **e.** World, guild and announcement messages are written to
  `chat_channel_messages` once, on the sender's pod (`ChatEngine.Publish*`,
  the announcement worker and the admin announce route), with the live
  packet's timestamp. `GET /api/v1/chat/recent` returns the newest 50 per
  channel (own CURRENT guild only, blocked senders filtered); the client
  fetches it each time the socket goes live and dedupes on (channel, sender,
  timestamp). Bounded: rows older than 14 days are pruned every 200th write.
  History rows get negative ids so sign-in does not show "50 unread".
  **Carries an additive migration, `AddChatChannelMessages`** - deploys apply
  it; locally run `--migrate` by hand. Verified: 17 server tests matching Chat or
  Migration (incl. `ChatHistoryTests`), vitest 784, svelte-check 4, EF reports no
  pending model changes. NOT run here: `npm run exercise` (its world chat
  step now also checks the reload shows the line exactly once) and a real
  phone install.

## Wave 1 result (2026-10-01): 89-93 built and verified together

89-93 were built in parallel on five branches and merged into
`claude/ui-integration` together with the portrait lock (a3eac07). That branch
is what was verified, and the PR is opened from it rather than from five
branches, because their conflicts (PersonPicker, Boosts, Social, Mailbox,
Market, Forge, Crafting, Ancestors, VillageFolk, Village) were resolved there
once and checked as a whole.

**Verified on `claude/ui-integration`:** `npm test` 730 passed; svelte-check 4
(the GuildOps baseline); `npm run exercise` 276/276 after a re-seed;
`check:clipping`, `check:overlap`, `check:touch` and `check:safearea` all 0
findings. Looked at by hand at 390 px: Skill tree (the leaking `header` rule
is gone and the top bar is unchanged), Leaderboards (the monster name is
whole, a name opens the profile), the profile (11 slots, readable on
parchment, Escape closes it), Market, and scroll reset (Chest at 832px ->
Village at 0).

**Found by verifying, and fixed on the integration branch:**
- **A new player's Auto-Eat hung on "Checking the chest..." for ever, and the
  tutorial stalled on step one.** 93 had made the content registry a TanStack
  query; with a freshly registered account the list never resolved and no
  error was raised, while the dev fixture was fine. The same screen passed
  whenever any `$effect` also read the query's status. The registry is now
  read through `contentQuery` in `lib/net/registry.svelte.ts` (a plain promise
  with error and retry). Only exercise.mjs's new-account steps caught it.
- **check:touch went from 0 to 42** with 92's Hint triggers (the event chip at
  91x19 on every screen, fourteen 50x17 timings on Gathering). A Hint's
  `::after` hit area adds 20px, which is not enough. The chip is 44px on a
  phone now, and Gathering explains its timings once per profession.
- exercise.mjs: "Send on" is a two-tap confirm, so the step taps twice and
  also asserts that one tap only arms it.

**Still open from wave 1 (fold into the named task):**
- The header still prints the raw connection phase ("live", "reconnecting
  (retry 3)") - `App.svelte` around 729. -> 95.
- The guild-name server cap is 100 while the client now stops at 32
  (`GuildManagementEngine.cs:89`). -> 94 (same server change).
- The player profile lists EVERY character on the account (185 on the
  fixture, most of them bred children with nothing worn), and the level is
  blank ("Level - Female - Child"). -> 107.
- The Wiki sidebar now can stick (89), but at 1440x900 it is 1,216px tall,
  taller than the viewport and the article, so it still scrolls away. Cap its
  height and let it scroll. -> 109.
- Not yet seen on a phone: the profile and the name menu opened from CHAT (the
  portal fix for the dock's blur), the chat window with the keyboard up, the
  bottom chrome hiding while typing, back on What's new and on the shield
  wheel, and the white flash after the splash. -> 110 (device checklist).
- Focus trapping and `inert` behind modals (audit B4) were not done. -> 106
  (the shared Modal).

## Wave 2 result (2026-10-01): 94, 96, 97, 98, 99, 103, 104, 108 built and verified together

Built in parallel on six branches and merged into `claude/ui-wave2`, which is
what was verified. No merge conflicts.

**Verified on `claude/ui-wave2`:** server suite 1547/1547; `npx vitest run`
750 passed; svelte-check 4 (the GuildOps baseline); `npm run exercise`
292/292 after a re-seed; `check:clipping`, `check:overlap`, `check:touch` and
`check:safearea` all 0 findings. `check:perf` Chest scroll: 22 long tasks on
both main and this branch (longest 227/242 ms on main, 320/153 ms here - noise,
not a regression). 390 px screenshots in `docs/screenshots/2026-10-01-wave2/`:
a guest sees the weapon slot (Character) and a Fight button (Combat) in the
first viewport, and Sign out is the first control on Settings.

**Found while integrating, and fixed here:**
- **Gold could be sold or binned as a material** - `/chest/sell` and
  `/chest/discard` accepted `itemId: "gold"`, took the coins and paid
  `ValueMaterial("gold") = 0`. The server refuses with `NotRemovable` now
  (`VillageChestEngine`, test in `GoldIncomeLedgerPostgresTests`).
- The 94 exercise step refounded the fixture's guild whenever the fixture was
  its last member - a new guild id with an empty depot every run. It only arms
  that confirm now; `GuildLeaveTests` commits the close.
- The Delve lantern check compared two gold balances while the fixture was
  still fighting and failed on combat income (+580 g). It reads the route's own
  `GoldCharged` now.
- The loot strip read "0 pieces ·0 materials" (Svelte drops the space before
  `{/if}`).

**Owner questions raised by this wave:**
- 98: "Young blood" can be earned on the first boss kill, but the challenge
  fold stays hidden until the boss is beaten (the spec's rule).
- 97: attributes are account-wide and health is only known for slot 1, so the
  person switcher does not change them; and DPS is the server projection
  (monster HP / seconds per kill). Confirm that is the number to show.
- 103: married-in elders count against the newcomer cap and cannot be sent on,
  so ~Inn+6 marriages stop arrivals for the season - intended? The Household
  housing cap (e.g. 188/35) is enforced by nothing: give it an effect or drop it.
- 94: leave/rejoin has no cooldown (guild payouts could be collected twice -
  unchecked); succession ignores Officer rank.
- 99: Sell shows no price because the client never receives the server's sell
  value; sending it per inventory row is a server change.

**Still open:** 95, 100, 101, 102, 105, 106, 107, 109, 110. The coach pill
("Do this next") still sits over content at the bottom of every phone screen
(seen on Settings, Chest, Village, Guild) - fold into 109's tutorial items.

## Wave 3 result (2026-10-02): 100, 101, 102, 107 and owner bugs, built and verified together

Four branches merged as `claude/ui-wave3`: tasks 100-102 (Forge rows, Gathering
and Crafting, Market tabs), task 107 (guild structure), and the owner's bug list
of 2026-10-02 - combat SFX only on the Combat screen, a bigger monster portrait,
the Work & orders tab width, smaller chat rows, one-line announcements and a
reroll announcement rule (Legendary only, one per player per 10 minutes,
auto-reroll announces its final result once).

**The forge "breaks for a second" was a real disconnect.** A quick second Fuse
sent the ids of pieces the first fusion had just destroyed, and
`ForgeTickCoordinator` turned the refusal into `ForceDisconnect`. It now answers
`ReloadState`, and the client hides the pieces it just sent until the refreshed
list drops them.

**Verified on the merge:** server suite 1557/1557; vitest 767 passed;
`check:ratchet` at the 4-error baseline; `exercise` 295/296; clipping, overlap,
touch and safearea all clean. Measured by hand at 390px with mobile emulation:
Character keeps a 390px document on all three tabs, the fight portrait is
136x136, a one-line chat row is 28px with 12.8px text. The one exercise miss is
"a drop can be worn from the loot list" (no wearable drop in 90 s), which passed
on the previous run of the same code.

**Fixed during the merge pass:** `exercise.mjs` read the Crafting recipes while
the screen was still on the Commissions tab (navigating to the screen you are on
does not remount it); the chat name button's global drop shadow drew a pale slab
across neighbouring 28px rows.

**Open questions for the owner:** gate the death and boss first-clear stingers
outside Combat too?; let guild members still browse the guild directory?; a
Cancel for own equipment market orders needs a new command; is the reroll
announcement rule right?

## Player profile and public guild view (2026-10-02, branch `claude/player-profile`)

Owner request: a name opens a profile almost at once, from chat, the guild
roster and Friends; the profile shows equipment, level, guild and statistics;
and a guild member, who no longer sees the directory (107), can open another
guild from a person's profile.

- **Server.** `GET /api/v1/players/profile?id=` answers from
  `Domain/Social/PublicProfiles.cs` now: level, online, guild (id, name, tier,
  role), the MAIN character (`PlayerGuid`) plus at most four others who wear
  something - all eleven slots, affixes parsed - and statistics the server
  already tracked (kills and bosses from the codex, regions, achievements, play
  time, best hit and drop, Delve floor, rebirths, seals, best season rank,
  crafted, deaths, gathering mastery). It used to serialise every
  CharacterRecord whole (185 on the fixture) with a blank per-character level;
  that is the Wave 1 open item, closed. New `GET /api/v1/guilds/view?id=`:
  tier, members x/max, rating and rank (the guild board's order, counted in
  SQL), tax, join rule, monoliths, active buffs, the week's summed points and
  the member list. **Deliberately not on it:** treasury gold, depot,
  per-member contribution, applications (`PublicProfileTests` pins that).
- **Client.** One host: `PlayerProfileModal` is mounted once in App and draws
  the top of `stores/profile.ts`'s stack (profile -> guild -> member; back pops
  one, the X closes all). Any name uses `use:profileLink` (`ui/profileLink.ts`):
  the shell opens at once with the tapped row's name and the cached avatar, and
  the fetch starts on pointerdown/hover/focus, cached 60 s. Wired: chat (own
  name opens your profile directly; others through the menu, View Profile
  first), Friends, guild roster, the guild's weekly ranking, guild
  applications, all three leaderboards (guild rows open the guild view), world
  boss board. Tapping a worn piece opens its stats in a DetailSheet (Affixes).
- **exercise.mjs:** roster name -> profile (11 slots, item sheet, Escape),
  guild view from the profile and a member back to a profile; own chat name;
  and in the new-account block the fixture befriends the throwaway, opens it
  from Friends and removes it again.
