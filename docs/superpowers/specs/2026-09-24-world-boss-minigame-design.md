# World boss minigame: the shield wheel with parries (task 36), design

Date: 2026-09-24
Status: SPEC. The owner decided this on 2026-09-24. Plan:
`docs/superpowers/plans/2026-09-24-task-36-world-boss-minigame.md`.
Brief and option menu: `docs/superpowers/plans/2026-09-23-task-36-world-boss-minigame-design.md`.

Builds on **task 25** (branch `fix/world-boss-attack`). That PR adds:

- the dev-only manual window (`POST /api/v1/dev/worldboss/window`, `FOLKIDLE_DEV_TOOLS=1`);
- `WorldBossAttackOutcome` and the guarded `QueueAttack`;
- `CommandResultCode` 38-42 (37 is `GuildWarsLocked`, PR #22);
- the removal of the larder rule.

This spec assumes all of it is merged, and it does not redo any of it.

---

## 1. Decisions (owner, final, 2026-09-24)

| # | Question | Answer |
|---|---|---|
| 1 | Which candidate | **E2 (free-aim shield wheel) combined with D (parry)**: "interrupts + counter" |
| 2 | Floor and cap | Skill multiplier `M` runs from **1.0x** (auto-strike, today's value) to **2.0x** |
| 3 | Weak plate | Stays **3.0x** |
| 4 | Auto-strike | Offered, paid at the floor |
| 5 | Practice | Free and server-issued. It costs no attempt and deals no damage |
| 6 | 300 s battle session | **Dropped** |
| 7 | Abandoned challenge | Resolves as an auto-strike at the floor (the exact rule is in §5.6) |
| 8 | Difficulty over the encounter | **Harder in the boss's last 25% of HP**: the enraged schedule, §3.4 (owner, follow-up answer) |
| 8b | Auto-strike floor | A played attempt is **never worth less than auto-striking the same plate**: `max(played, auto)` per attempt, §3.2 (owner, follow-up answer) |
| 8c | Break rule | **One plate break per attempt, the first hit** (§3.3), confirmed |
| 9 | Haptics | `navigator.vibrate` on a hit. No Capacitor Haptics plugin |
| 10 | Ship window | Flagged rollout for the **Oct 15-22** window, after a phone playtest of practice mode |
| - | Weak-plate feedback | A weak-plate hit glows **only for the player who threw it, during that attempt**. The global reveal comes only through the plate-break mechanic, so the crowd-deduction layer survives |
| - | Pipeline | The brief's server pipeline: REST challenge/strike, a server-issued schedule including the parry tells, `RandomNumberGenerator`, a pure scorer, and timestamps and choices from the client, never a number the server trusts. Plausibility checks, one unique telemetry code, and `security-review` as a gate |

## 2. How it plays

The World Boss screen keeps its plate board: five plates, broken plates cracked, a revealed weak plate glowing. It offers three buttons:

- **Strike** (the wheel);
- **Auto-strike** (pick a plate, floor multiplier);
- **Practice**.

**Strike** issues a challenge and opens a full-screen overlay:

1. **Countdown** 3-2-1 (3,000 ms).
2. **The wheel.** The boss's five plates form a ring that spins at changing speed and reverses. The player holds **5 spears**. Tapping anywhere in the lower half throws one upward, and it lands on whatever part of the ring is at the impact point (the bottom of the ring) **120 ms later**. Every spear strikes the plate it lands on:
   - the centre band of a plate is a **Seam**;
   - the rest of the plate is a **Plate** hit;
   - a narrow rivet band on either side of each border between plates is a **Glance**.
3. **Interrupts.** 2 or 3 times in the 20 s of play, the boss winds up a blow and **the wheel stops**. A tell shows where the blow will land: **left**, **right** or **overhead**. Three buttons appear: **Dodge left / Block / Dodge right**.
   - **Correct read, inside the window:** a **counter window** opens with the wheel still frozen. The player taps a plate on the frozen ring (or one of five numbered plate buttons). That spear is a **guaranteed Seam hit on the plate tapped**.
   - **Wrong read, or no read inside the window:** the player **loses one spear** (it counts as None).
4. **Result card.** It shows each spear's plate and class, `M`, the plate multiplier, the damage dealt, and whether this attempt broke a plate. It says "no damage dealt" in practice.

A spear that lands on the weak plate **glows on this player's ring only**. The glow is a server answer (§5.3) and lasts for this attempt. It is never broadcast.

### 2.1 The correct read

| Tell | Correct answer | Why |
|---|---|---|
| Left | **Dodge right** | move away from the blow |
| Right | **Dodge left** | move away from the blow |
| Overhead | **Block** | there is no side to step to |

The tell is drawn by **position and shape**: a raised arm and a ghosted strike arc on that side, or above. The accessibility rule forbids colour alone. With reduced motion, the tell is a static glyph with no wind-up animation.

### 2.2 "Aimed spot", defined

**The aimed spot is the plate the player taps during the counter window.** The wheel is frozen at that moment, so the five plates are stationary targets:

- each ring sector is about 200 px of arc on a 390 px phone;
- a row of five numbered plate buttons (44 px or more) gives the same choice without precision.

If the counter window closes with no plate tapped, the counter lapses. **No spear is spent**, and the next spear is an ordinary wheel throw.

Why this definition and not "the plate you last selected" or "the plate your last spear hit":

- **It is the only moment the player has a free choice.** On the spinning wheel, the plate a spear hits is a timing outcome. The counter turns a correct read into a *decision*, and the decision uses exactly what the crowd layer produces: the shared board (broken plates, a revealed weak plate) and the player's own glow from this attempt. That is the link between D and the task-10 board.
- **It adds no persistent state and no new kind of client input.** The client already sends plate indices (opcode 32's `TargetedPlateIndex`). A plate index 0-4 is a *choice*, bounded and validated, never a quantity. "Last selected" would need an aim control that is live during the spin, and a way to report aim changes with timestamps. "Last hit" gives no choice at all, and before the first landing it is undefined.
- **It is scorable without geometry on the server.** The client maps the tap to a plate. The server receives `0-4` and applies Seam to it.

## 3. Numbers

Every constant lives in `WorldBossStrikeRules` with a `// Modul:` comment. The **phone playtest gate** (plan, end of Phase 1) exists to retune them, **in this spec first, then in code**.

| Constant | Value | Note |
|---|---|---|
| `CountdownMs` | 3,000 | |
| `MaxPlayMs` | 20,000 | includes frozen interrupt time |
| `Spears` | 5 | |
| `FlightMs` | 120 | tap to landing |
| `MinReloadMs` | 350 | client locks the throw zone for this long |
| `ToleranceMs` | 35 | display/touch latency benefit of the doubt |
| `PlateDegrees` | 72 | 360 / 5 |
| `SeamDegrees` | **12** | the Seam band is centred on the plate. Narrowed from the brief's 20; see §3.1 |
| `RivetDegrees` | 3 | Glance band on each side of every border between plates |
| Class values | **Seam 1.0, Plate 0.15, Glance 0, None 0** | changed from the brief (Plate 0.6, Glance 0.15); see §3.1 |
| `BestOf` | best 4 of 5 spears | one mistake is forgiven |
| `SaturationScore` | **0.90** | `s` at which `M` reaches the cap |
| `Floor` / `Cap` | 1.0 / 2.0 | owner |
| `WeakPlateMultiplier` | 3.0 | owner. The existing `WeakPlateDamageMultiplier` |
| Segment speed | \|w\| in [90, 210] deg/s | piecewise-constant. Sign flips allowed |
| Segment duration | 700-2,200 ms | |
| `Interrupts` | 2 or 3, uniform | |
| `InterruptMs` | 2,400 | the wheel is frozen for this long from `TellAtMs` |
| `ReactionFloorMs` | 150 | a choice earlier than `TellAtMs + 150` is a guess, scored as a wrong read |
| `ResponseCloseMs` | 1,000 | a choice must arrive by `TellAtMs + 1,000` |
| Counter window | from the correct choice to `TellAtMs + InterruptMs` | at least 1,400 ms |
| Interrupt placement | first `TellAtMs` >= 3,000; gap between interrupts >= 4,000; the last ends by `MaxPlayMs - 2,000` | |

### 3.1 Why the class values changed from the brief (a finding, not a preference)

The brief priced Seam/Plate/Glance for E, where a spear that missed the *chosen* plate scored None. Under free aim, **every landing hits some plate**. With the brief's values (Seam 20 degrees, Plate 0.6, Glance 0.15, saturation 0.85), a player tapping at random scores `M` of about **1.85**, so the skill lever pays out almost fully for no skill. Simulated (200,000 random attempts, best 4 of 5):

| Setting | random `s` | random `M` | 2 correct parries + 3 random | 3 correct parries + 2 random |
|---|---|---|---|---|
| Brief's (20/3, 0.6, 0.85) | 0.73 | **1.85** | - | - |
| **This spec** (12/3, 0.15, 0.90) | 0.33 | **1.36** | 1.75 | 1.91 |

With this spec's values:

- random tapping earns about 1.36x (close to the brief's 1.31x for E);
- reading the parries is worth most of the lever;
- the cap needs good parries **and** some seams.

A 12 degree seam at 150 deg/s is an 80 ms window, plus the 35 ms tolerance either side. That is inside a good human's reach, which is the anti-bot economics of §6: **the cap must be reachable by a good human.**

**Finding, 2026-09-25 (implementation, `WorldBossStrikeLedgerTests`): the table above omits the tolerance, so random tapping earns 1.53, not 1.36.**
The simulation behind the table scored each landing at its exact angle. The scorer this spec defines takes the best class within ±35 ms. At a typical speed, that widens the 12° seam to about 22°. The ledger runs the real scorer over generated schedules, 8,000 attempts each:

| Seam | Tolerance | Plate value | random `M` | 2 reads + random | enraged, 3 reads + random |
|---|---|---|---|---|---|
| 12 | 35 ms | 0.15 (**this spec**) | **1.527** | 1.825 | 1.947 |
| 12 | 0 ms | 0.15 | 1.360 | 1.746 | 1.914 |
| 12 | 20 ms | 0.15 | 1.457 | 1.793 | 1.934 |
| 8 | 35 ms | 0.15 | 1.464 | 1.795 | 1.937 |
| 12 | 35 ms | 0 | 1.425 | 1.779 | 1.929 |
| **8** | **35 ms** | **0** | **1.351** | **1.743** | **1.917** |
| 8 | 20 ms | 0.15 | 1.394 | 1.763 | 1.923 |

The tolerance-0 row reproduces the table above exactly, which confirms the cause. **Seam 8° with Plate worth 0** restores all three of the spec's targets and keeps the full 35 ms latency allowance.

This is an **owner decision for the Phase 1 playtest gate.** Until it is made, the ledger's random-tap check is **skipped with this reason**, not widened. Practice shows the current numbers and deals no damage. Phase 2 must not ship while that check is skipped.

### 3.2 Damage for one attempt

```
A = CachedEffectiveMilliAttack / 1000                      (unchanged, from the payload)
G = 1 + Giantslayer%                                       (unchanged)
s = mean of the best 4 of 5 spear class values             (None for unthrown and lost spears)
M = min(Cap, Floor + (Cap - Floor) * s / SaturationScore)  in [1.0, 2.0]
P = mean over spears whose class is Plate or Seam of (3.0 if that plate is weak else 1.0)
    P = 1.0 if no spear reached Plate class                in [1.0, 3.0]
Auto = max over the plates struck by a Plate-or-Seam spear of (3.0 if weak else 1.0)
       Auto = 1.0 if no spear reached Plate class          in [1.0, 3.0]
played = max(M * P, Auto)                                  (owner, 2026-09-24: the auto-strike floor)
damage = ComputeAppliedDamage(currentHp, A * G * played)   (existing clamp [1,000, 100,000,000] and to remaining HP)
```

- **Auto-strike** is today's strike exactly: `M = 1.0`, and `P` is 3.0 or 1.0 for the one plate chosen.
- **The auto-strike floor (owner decision, final).** A played attempt is never worth less than auto-striking the same plate: `played = max(M x P, Auto)` per attempt.
  - "The same plate" is **any plate one of the attempt's spears struck with Plate or Seam class**, and the floor takes the best of them. A player who lands even one spear on the weak plate is guaranteed at least the 3.0x an auto-strike on it would have paid.
  - Glance-only and empty attempts floor at 1.0, the value of an auto-strike on a non-weak plate.
  - This is the simplest definition that honours "the same plate" when five spears strike up to five plates. It needs nothing new from the client; the landings are already in the order.
- **Ceiling:** `played <= 6.0` (M x P maxes at 2.0 x 3.0, and Auto maxes at 3.0), the same maximum as the brief's E.
- **Playing is never worse than not playing.** This holds by construction and is asserted in the ledger test: a wheel attempt on a revealed weak plate pays at least the 3.0 of auto-striking it.

### 3.3 What an attempt does to the shared board

- **Break.** Each attempt breaks **at most one** plate for everyone: the first spear, in tap order, that lands with class Plate or Seam on a **non-weak, unbroken** plate. Today one strike breaks one plate. This rule keeps that information rate (three attempts means at most three breaks per player) even though an attempt now throws five spears. Without it, one attempt would break three or four plates and solve the board for everyone within minutes, which is the brief's own objection to E2.
- **Reveal.** A weak-plate hit **no longer** sets `WeakPlateRevealed`. `WeakPlateRevealed` becomes 1 when `BrokenPlateMask` covers all four non-weak plates, because at that point the board has solved itself by elimination and the flag only shows what everyone can already deduce. This applies to auto-strike too: an auto-strike on the weak plate tells *that player* "weak point", in its REST result, and nobody else.
- Every server response and broadcast mirror still carries 255 for an unrevealed weak plate, with **one** exception. The `/throw` answer tells the thrower whether *their* spear hit it (§5.3).

### 3.4 The enraged wheel: the boss's last 25% of HP (owner decision, final)

**When:** a challenge is **enraged** if, at issue time, `CurrentHp <= 0.25 x MaxHp` (the boss snapshot row, read in the eligibility step). The phase is decided **once, at issue**, and written into the schedule (`"Enraged": true`). A challenge never changes difficulty mid-play, and its score never depends on when the HP crossed the line.

**What changes:**

| Constant | Normal | Enraged |
|---|---|---|
| Segment speed \|w\| | 90-210 deg/s | **120-260 deg/s** |
| Segment duration | 700-2,200 ms | **500-1,600 ms** (more reversals) |
| Interrupts | 2 or 3 | **always 3** |
| `ResponseCloseMs` | 1,000 | **850** (the reaction floor stays 150) |

**What does not change:** `SeamDegrees`, the class values, `SaturationScore`, `Floor`/`Cap` and the weak multiplier. Damage is not re-priced. The wheel is only harder to play. A 12 degree seam at 260 deg/s is a 46 ms window (plus the 35 ms tolerance either side). Three guaranteed counters still let a good reader reach the cap: the 3-counter simulation in §3.1 gives 1.91. The auto-strike floor (§3.2) protects anyone who cannot.

**Tests:**

- the generator property test runs for both phases, including the "every plate's seam passes the impact point at least 3 times" rule;
- the ledger asserts that the mean `M` for a "3 reads + random" simulation on the enraged schedule stays at 1.75 or more.

**Client:** the overlay shows "The boss is enraged" before the countdown. It does not tint only in colour; it adds a shape or glyph change too.

## 4. What is dropped or changed on the server

- **The 300 s battle session is dropped.** Removed:
  - `BattleSessionCapSeconds`;
  - the `SessionStartEpoch` check in `ExecuteAttackAsync`;
  - the `WorldBossAttackOutcome.SessionClosed` producer;
  - the `StateUpdatePacket.WorldBossSessionEndsEpoch` wire field, **via the `add-command` skill** (`ExpectedStateUpdateSize` 809 -> 801; `npm run generate:protocol`; drop the field from `StateUpdatePacketFieldCoverageTests` if it is listed; the client's session countdown and `commands.ts`'s session pre-check go with it).

  `CommandResultCode.WorldBossSessionClosed = 41` stays in the enum, **reserved and unproduced**, with a comment. Codes are never reused. The `SessionStartEpoch` column stays (additive schema; dropping it is not worth a non-additive migration) and nothing reads it.
- **The weak-plate seed** (`ActivateEventWindowAsync`: `Random.Shared.Next(PlateCount)`) moves to `RandomNumberGenerator.GetInt32(PlateCount)`. A predictable seed is a precomputable secret.
- **The reveal rule** changes as described in §3.3.
- **Opcode 32 (`AttackWorldBoss`)**:
  - **Flag `wheel`:** it is answered with a new `CommandResultCode.WorldBossUpdateRequired`, taking the **next free code** at implementation time (43 as of 2026-09-24, after PR #22 = 37 and task 25 = 38-42), and changes nothing. A stale bundle cannot show the private weak-hit result or the damage, so it is told to restart and update over the air. It is never `TerminateSessionForSecurity`.
  - **Flag `off` or `practice`:** opcode 32 behaves exactly as task 25 left it, with the new reveal rule.

## 5. Server pipeline

### 5.1 Flag

`FOLKIDLE_BOSS_MINIGAME` = `off` (default) | `practice` | `wheel`. It is read once at startup, as the other `FOLKIDLE_*` variables are.

| Flag | Practice | Strike (wheel) | Auto-strike (REST) | Opcode 32 |
|---|---|---|---|---|
| `off` | `Disabled` | `Disabled` | `Disabled` | today's strike |
| `practice` | yes | `Disabled` | `Disabled` | today's strike |
| `wheel` | yes | yes | yes | `WorldBossUpdateRequired` |

`GET /api/v1/worldboss/challenge` answers `{ Result: "Disabled", Mode: "off" }` so the client falls back to the plate buttons.

### 5.2 Challenge (explicit schedule, never a seed)

`POST /api/v1/worldboss/challenge` (body `{ "Practice": false }`):

```json
{
  "Result": "Issued",
  "Challenge": {
    "ChallengeId": "b3f1...",               // 128-bit random, hex; not guessable
    "Practice": false,
    "CountdownMs": 3000, "MaxPlayMs": 20000, "Spears": 5,
    "FlightMs": 120, "MinReloadMs": 350, "ToleranceMs": 35,
    "PlateDegrees": 72, "SeamDegrees": 12, "RivetDegrees": 3,
    "StartAngleDeg": 137.0,
    "Segments": [ { "StartMs": 0,    "DurationMs": 1400, "DegPerSec": 150 },
                  { "StartMs": 1400, "DurationMs": 900,  "DegPerSec": -210 },
                  { "StartMs": 4100, "DurationMs": 2400, "DegPerSec": 0, "Interrupt": 0 }, ... ],
    "Interrupts": [ { "Index": 0, "TellAtMs": 4100, "Tell": "Left",
                      "ReactionFloorMs": 150, "ResponseCloseMs": 1000, "InterruptMs": 2400 }, ... ],
    "BrokenPlateMask": 5, "RevealedWeakPlate": 255
  }
}
```

- Interrupts are **frozen segments** (`DegPerSec: 0`) inside `Segments`, so `angleAt(t)` stays a pure function of time. A frozen interval lasts `InterruptMs` whatever the player does.
- The tell direction is in the schedule because the client must draw it. That is not a secret. What is scored is whether the **choice timestamp** is at least 150 ms after the tell and inside the window, and whether the choice matches.
- Everything is generated with `RandomNumberGenerator`.
- **Idempotent:** a second call while a challenge is outstanding returns the same challenge, with `Result: "Outstanding"`.
- **Eligibility** is the **one** function task 25's attack path uses. Do not write a second copy of the gates. It covers:
  - flag on;
  - event active;
  - boss alive;
  - attempts left (counting an outstanding challenge as a spent attempt for eligibility);
  - the challenge fits before `EventEndEpoch` (`now + Countdown + MaxPlay + 15 s`).

  Each refusal answers **200 with a `Result`** (§5.7).
- **Practice** challenges are separate: one outstanding practice challenge per player, no attempt accounting, and **a decoy weak plate drawn per practice challenge**. Practice must never answer from the real weak plate, or it becomes a free probe for the secret. `BrokenPlateMask` in practice is 0.

### 5.3 Throw (per spear, for the glow)

`POST /api/v1/worldboss/throw`, one call per spear, idempotent by `Seq`:

```json
{ "ChallengeId": "b3f1...", "Seq": 0, "TapMs": 2140 }
{ "ChallengeId": "b3f1...", "Seq": 3, "TapMs": 5400,
  "Counter": { "Interrupt": 0, "Choice": "DodgeRight", "ChoiceMs": 4390, "Plate": 2 } }
```

The answer is `{ Result, Seq, Plate, Class, WeakHit }`.

- `Plate` and `Class` come from the pure scorer.
- The client computes them itself for instant animation, and the server's answer is authoritative. On a disagreement the client redraws the server's.
- `WeakHit` needs the secret, so it is the one thing the round trip exists for. It is read from the snapshot row with **no lock and no mirror**: `WorldBossEngine.IsWeakPlateAsync(plate)`, or the decoy for practice. It is returned **only** to the thrower.
- **Latency:** the answer delays the glow only, never the score.
- Recorded throws are kept on the challenge. **A repeated `Seq` returns the stored answer.** A `Seq` above `Spears - 1 - spearsLost` returns `OutOfSpears`.
- **Wall clock:** `ReceivedAtMs - IssuedAtMs >= CountdownMs + TapMs - 50`, else `TooEarly`. The throw is refused, recorded to telemetry, and not stored. An honest client always passes, because the network only adds time.

### 5.4 Finish (the scored log)

`POST /api/v1/worldboss/strike`, with two shapes.

```json
{ "Mode": "Wheel", "ChallengeId": "b3f1...",
  "Taps":     [ { "Seq": 0, "TapMs": 2140 }, ... ],
  "Parries":  [ { "Interrupt": 0, "Choice": "DodgeRight", "ChoiceMs": 4390 }, ... ],
  "Counters": [ { "Interrupt": 0, "Seq": 3, "TapMs": 5400, "Plate": 2 } ] }

{ "Mode": "Auto", "Plate": 2 }
```

- **Auto** is the auto-strike. It needs no challenge, and it is refused with `ChallengeOutstanding` while one is open (finish or let it expire first). It is scored `M = Floor`, `P` = that plate, and breaks that plate if it is non-weak and unbroken.
- **Wheel:** the server scores the log with `ShieldWheelScorer` (pure, §5.5).
  - **Consistency rule:** every tap already answered by `/throw` must appear in the log with the identical `TapMs`, `Seq` and counter fields. On a mismatch the submission is `Refused` and resolved at the floor (§5.6).
  - Taps the server never received as throws (a dropped request) are accepted from the log.
- The REST handler enqueues a `WorldBossStrikeOrder` and awaits its completion for up to 5 s. On a timeout it answers `Queued`. The attempt pip and boss HP still arrive on the `StateUpdate` stream.
- Practice finishes use `POST /api/v1/worldboss/practice/score`. It runs the same scorer and returns classes, `M`, `P` against the decoy, and "no damage dealt". There is no queue and no damage.

### 5.5 The pure scorer (`ShieldWheelScorer`)

Its inputs are the schedule and the log. It returns `ThrowLanding[]` plus a `SubmissionVerdict`, and it never throws. It scores **both halves**.

**Wheel landings.**

- The landing angle at the impact point is `angleAt(tapMs + FlightMs)`.
- `plate` is the plate containing that angle at the exact tap.
- `class` is the best class **on that same plate** over `[tap - ToleranceMs, tap + ToleranceMs]`, sampled every 5 ms. The tolerance can never move a spear to a different plate.
- A non-counter tap inside a frozen interval is **dropped**. It counts as not thrown and is flagged to telemetry (detail 3), but the submission is not refused. An honest client locks the zone, but boundary jitter must not cost a whole attempt.

**Parry exchanges.** For each interrupt `i` reached before the player ran out of spears:

| Choice arrives | Outcome |
|---|---|
| Missing | **miss**: one spear lost |
| At `ChoiceMs < TellAtMs + ReactionFloorMs` | **guess**: scored as a miss (one spear lost), plus telemetry detail 2 if it matches the tell |
| At `ChoiceMs > TellAtMs + ResponseCloseMs` | **miss** |
| Otherwise, wrong choice | **miss** |
| Otherwise, correct choice | **read**: a counter window opens at `[ChoiceMs, TellAtMs + InterruptMs]` |

A counter tap inside that window, with `Plate` in 0-4, lands **Seam on `Plate`**. A counter tap outside the window is dropped (telemetry detail 3).

An interrupt is "reached" if `TellAtMs <= the time of the last spear actually thrown`. After the last spear there is nothing to lose.

**Shape refusals.** Each resolves at the floor (§5.6), is recorded to telemetry, and **never throws**:

- more than `Spears` taps;
- non-increasing tap times or duplicate `Seq`;
- a negative time, or one past `MaxPlayMs`;
- two wheel taps less than `MinReloadMs - 50` apart;
- more counters than read parries;
- an unknown `Interrupt` index;
- `Plate` outside 0-4;
- NaN, huge numbers or wrong JSON types;
- `ReceivedAt - IssuedAt < CountdownMs + lastTapMs - 50`;
- an expired or foreign `ChallengeId`.

**Suspicion** is flagged to telemetry and **never lowers the score**:

- every wheel tap within 3 ms of its seam centre-crossing (detail 1);
- three or more reads with a reaction under 180 ms (detail 2).

### 5.6 Anti-scum: abandoned, expired and refused challenges

- An issued real challenge is a commitment. It expires at `IssuedAt + CountdownMs + MaxPlayMs + 60 s`.
- **Abandoned or expired:** it resolves lazily, on the player's next `GET/POST /api/v1/worldboss/challenge` or on opening the screen. It is scored from **the throws the server already answered**, at **`M = Floor`**, with `P` and the break rule applied to those throws. With no answered throws, it is `A x G x 1.0`, and nothing breaks. It spends the attempt.
- Abandoning is therefore never better than finishing: a finished run has `M >= Floor` on the same throws. It also cannot be a free re-roll, and probing the weak plate with one throw costs an attempt.
- **No new `StartCron`.** Resolution is lazy, so `CronWorkerGuardTests` is untouched.
- **Refused** (a shape error or a consistency mismatch) resolves the same way, with `Result: "Refused"` and a sentence.
- Leaving the app (`visibilitychange` -> hidden) makes the client submit what was thrown. Unthrown spears count as None.
- **A server restart forgets in-memory challenges.** The attempt was never spent, so the player simply gets a new challenge. That is a re-roll the player cannot trigger. Accepted, and written into a `// Modul:` comment.
- **An encounter rollover** during a challenge (a new `EventEndEpoch`) makes resolution answer `NotActive` **without** spending an attempt.

### 5.7 The strike order, inside the transaction

```
REST /strike -> scorer -> WorldBossStrikeOrder { PlayerId, EncounterEndEpoch, Landings[] (plate, class, order), IsAuto, Completion TCS }
  -> PlayerSessionRegistry.WorldBossStrikeQueue
tick: WorldBossTickCoordinator drains it with a BUDGET (depth read once per tick), computes A x G from the payload
      exactly as today (extract the existing computation into one method, do not copy it), then calls
      WorldBossEngine.QueueStrike(order, attack)
ExecuteAttackAsync (task 25's guarded shape): inside the Serializable FOR UPDATE transaction, where the weak index lives:
      computes s, M, P, applies the break rule and the elimination reveal, applies damage, increments AttemptCount,
      commits, completes the TCS with { Result, Damage, M, P, Landings with WeakHit, BrokePlate }
```

- `_playerDamageMap`, the Redis contribution hash, `WorldBossAttemptUpdateQueue` and the reward brackets in `ProcessDefeatedBossAsync` are unchanged.
- **Every return path completes the TCS with a named result.** A path with no result is a silent rollback (CLAUDE.md), and a result no test produces is dead.

`WorldBossStrikeResult` is the REST contract. Every value has a client sentence, pinned by a mirror test:

| Result | Where | Sentence (English; translate via the i18n layer if present) |
|---|---|---|
| `Issued` / `Outstanding` | challenge | (opens the wheel) |
| `Disabled` | any | "The boss fight is not open to the wheel yet. Use the plate buttons." |
| `NotActive` | any | "The boss is not here right now." |
| `AlreadyDefeated` | any | "The boss has already fallen." |
| `NoAttemptsLeft` | challenge, strike | "You have used all three attempts for this encounter." |
| `TooLateInWindow` | challenge | "The encounter ends before a strike could finish." |
| `ChallengeOutstanding` | auto | "Finish your open strike first." |
| `NoChallenge` | throw, strike | "That strike has expired. Start a new one." |
| `TooEarly` | throw | (client bug; logged, spear not recorded) |
| `OutOfSpears` | throw | (client bug; logged) |
| `Landed` | strike | the result card |
| `ResolvedAtFloor` | challenge, when an old one was resolved | "Your unfinished strike was resolved at the base multiplier: N damage." |
| `Refused` | strike | "That strike could not be scored and was resolved at the base multiplier: N damage." |
| `Queued` | strike | "Strike sent. The result will show on the board." |
| `Failed` | strike | "The strike could not be recorded. Nothing was spent. Try again." |
| `PracticeScored` | practice | the practice card |

REST DTOs are **hand-written** in `rest.ts`, because REST is not the generated protocol. They stay small, and the `Result` list is pinned by `client_web/tests/worldBossResults.test.ts` against a server-side list exported by a test fixture (the `commandsAudit.test.ts` pattern).

## 6. Anti-cheat position

- **Nothing the client sends is a quantity the server adopts.** The client sends timestamps, parry choices (an enum), counter plates (0-4), and `Seq`. All are bounded, and all are scored against the server's own schedule.
- **The challenge is sent as explicit data, not as a seed.** No TypeScript port of a C# PRNG exists. The client's `angleAt` draws; it does not score. A shared fixture (`shield_wheel_cases.json`) is read by the C# and TS tests, so the drawn ring can never disagree with the scored ring.
- **Bots:** a modified client can compute perfect taps and reads. Economically, **a bot earns exactly the cap a good human earns** (`M <= 2.0`, saturating at `s = 0.90`), on coarse reward brackets. The cheat ratio over a skilled human is 1.0x, and over random tapping about 1.47x (2.0 / 1.36).
- **Telemetry only, never a penalty.** Nothing in this feature calls `RequestShadowBan`, sets `Quarantine_Active`, or feeds `RecordCommand`/`ValidateNetworkThroughput`. It is REST, so no per-tap WebSocket traffic reaches the macro detector.
- **One unique telemetry code:** a new `EventType = 8`, meaning "improbable input, accepted, no penalty". Every existing writer uses 3-7. It carries `Value1 = 32` (the world boss opcode, per the `Value1 = commandType` convention) and `Value2` detail:

  | Detail | Meaning |
  |---|---|
  | 1 | precise wheel |
  | 2 | inhuman reaction or guess-hit |
  | 3 | dropped tap |
  | 4 | shape refusal |
  | 5 | wall clock |
  | 6 | throw/finish mismatch |

  A test greps the server tree and fails if `EventType = 8` is written anywhere but `WorldBossStrikeTelemetry`. The memory note says codes have collided across unrelated checks before.
- **The secret weak index** leaves the database only as a boolean to the player whose spear hit it, or when revealed by elimination. A test serialises every REST response of a whole attempt and asserts the index is absent unless revealed or thrown at.
- **`security-review`** is a required gate on the PR that turns scoring into damage (plan Phase 2).

## 7. Client

- `client_web/src/lib/game/shieldWheel.ts`: `angleAt`, `plateAt`, `classAt`, `interruptAt(t)`, `correctChoice(tell)`. These draw and preview only.
- `client_web/src/lib/ui/ShieldWheel.svelte`, the overlay:
  - **Svelte rules:** runes mode only; no local named `derived`; snippets rendered with `{@render}`.
  - **Animation:** one `requestAnimationFrame` loop, started at the end of the countdown and cancelled in the `$effect` cleanup. The rotation is written straight to the SVG element's `style.transform` from `angleAt(now - t0)`, never through Svelte state.
  - **Timestamps:** `pointerdown.timeStamp - t0`, never `click`. `touch-action: manipulation`.
  - **Throw zone:** the lower half, locked for `MinReloadMs`, disabled during frozen intervals except for counter plate taps.
  - **Controls:** the parry buttons and the plate buttons are **absent** (`{#if}`) outside their windows, never hidden with `display: none` or `<details>`. They are 44 px or more with `flex-shrink: 0`, fixed at the bottom with `var(--safe-area-inset-bottom, env(safe-area-inset-bottom, 0px))`.
  - **No ticking inside a control.** The countdown and the window bars sit outside the buttons.
  - **Haptics:** `navigator.vibrate(30)` on a Seam, `navigator.vibrate([30, 40, 60])` on a weak hit, guarded with `'vibrate' in navigator`.
  - **Reduced motion:** static tell glyphs. The wheel still spins, because the spin is the game. Auto-strike is the accessibility path.
  - **Test hook:** the overlay exposes `data-schedule` (the JSON it received). It is not a secret, and `exercise.mjs` uses it.
- `WorldBoss.svelte`:
  - **Strike**, **Auto-strike** (the existing plate buttons, now `POST /strike {Mode:"Auto"}`) and **Practice**;
  - the result card;
  - the session countdown removed;
  - with `Disabled`, today's plate buttons over opcode 32.
- The Wiki entry is updated (`wikiData.ts` + `tests/wiki.test.ts`).
- Every geometry check (`check:touch/overlap/clipping/safearea`) runs with the overlay open, via one entry in `scripts/screens.mjs`. `check:perf` samples while the wheel spins at 4x throttle.

## 8. Docs to update when it lands

- `docs/world_boss_design.md`: a new "Task 36" section. The "What is deliberately NOT in this design: Minigames" line is updated, not left contradicting the code.
- `docs/TASK_BOARD.md` task 36: a result write-up.
- `CURRENT_IMPLEMENTATION_STATE.md`: the routes, the queue, the flag, the removed wire field.
- `NEXT_STEPS_BACKLOG.md`: the top section.
- `.claude/skills/add-command/SKILL.md`: the stale "359"/700-byte figures become 341/801.

## 9. Owner answers (2026-09-24, final)

1. **Floor the wheel at auto-strike.** A played attempt is never worth less than auto-striking the same plate. Specced in §3.2.
2. **One plate break per attempt (the first hit), and the wheel gets harder in the boss's last 25% of HP.** The breaks are in §3.3, the enrage in §3.4.
