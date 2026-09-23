# Task 36: world boss minigame - design brief and phased plan

> **For agentic workers:** this file is a DESIGN BRIEF first and a plan second.
> Sections 1-3 are the agenda for a brainstorm with the owner; **no code is
> written until the owner has answered section 3's questions and a spec exists
> in `docs/superpowers/specs/`.** Section 4's phased plan is for the
> *recommended* option and must be re-read against the owner's answers before
> anyone runs it. When it is run: REQUIRED SUB-SKILL superpowers:subagent-driven-development
> or superpowers:executing-plans; steps use checkbox (`- [ ]`) syntax.

**Goal:** replace "pick one of five plates" (chance) with a short skill-and-reflex
strike that works under a thumb on an Android phone, **without** letting the
client send anything the server has to believe about damage.

**Hard prerequisite: task 25.** Production shows no attack has *ever* landed
(`player_world_boss_attempts` empty, boss at 50,000,000/50,000,000 for the whole
Sep 15-22 window). Redesigning the input to a fight nobody can start is wasted
work. Task 25 also has to leave behind two things this plan leans on: a way to
**force the event window open locally** (exercise and manual testing), and
**visible refusal reasons** instead of `ExecuteAttackAsync`'s silent rollbacks.

**Read before the brainstorm:** `docs/TASK_BOARD.md` task 36 (~line 3556) and
task 10's PHASE G/H write-ups (~lines 1032-1200); `docs/world_boss_design.md`
(especially "Why not the obvious alternatives" - task 10 argued *against*
reflex minigames, and the owner has now overruled that; the arguments it made
are the risk list below, not a veto); `WorldBossEngine.cs`;
`WorldBossTickCoordinator.cs`; `ClientCommandPacket.cs`;
`NetworkPacketLayoutGuard.cs`; `AntiCheatTelemetryEngine.cs`; the Delve REST
handler (`NetworkBroadcastSystem.HandleDelveAction`, ~line 2779) - it is the
precedent for an interactive minigame over REST.

---

## 1. Constraints recap (what any option must survive)

### What exists today, checked in the source (2026-09-23)

- `AttackWorldBoss` (opcode 32) carries **one byte**, `TargetedPlateIndex` 0-4.
  The validator *requires* `ClientPredictedDamage == 0`; a violation, **or an
  inactive event**, returns false and `WorldBossTickCoordinator` calls
  `TerminateSessionForSecurity` - i.e. a disconnect. (Side note for task 25: an
  attack sent while the client *thinks* the event is active but the server does
  not is a disconnect, not a message.)
- Damage is `CachedEffectiveMilliAttack / 1000` x (1 + Giantslayer%) on the tick
  thread, then `WorldBossEngine.QueueAttack` -> `Task.Run(ExecuteAttackAsync)`
  under `SERIALIZABLE` + `FOR UPDATE`. Weak plate 3.0x and reveals it globally;
  any other plate 1.0x and breaks it globally. `ComputeAppliedDamage` clamps to
  `[1000, 100,000,000]` and to remaining HP.
- The weak plate index **never leaves the database transaction** unless revealed
  (`RefreshLocalSnapshot` mirrors 255 while secret). Any design must keep that.
- 3 attempts per encounter (`MaxAttemptsPerEncounter`), a 300 s battle session
  from the first strike (`BattleSessionCapSeconds`), and an empty larder refuses
  the attack. One shared HP pool: `BaseHp * online * 1.5 + mastery * 250`.
- Rewards are ranked by contribution (`ProcessDefeatedBossAsync`: top 1/10/50%).
  **Anything that multiplies damage moves a player's rank**, so a cheat's value
  is "rank above honest players", not an absolute amount.

### The rules

1. **Server-authoritative.** The client sends a *choice* or a *measured outcome*
   (tap timestamps), never damage, never a score the server adopts. The server
   generates the challenge, holds it, and scores the client's timestamps against
   its own copy.
2. **The challenge is sent as explicit data, not as a seed the client expands.**
   A seed + a TypeScript port of a C# PRNG is two implementations of one truth -
   this codebase's dominant bug class (`KNOWN_AFFIX_IDS` drifted 10 of 12
   entries). The server sends the full schedule as JSON.
3. **3 attempts per encounter**, and an abandoned challenge must not be a free
   re-roll (see "anti-scum" below).
4. **Shared HP pool and `_playerDamageMap`/Redis attribution unchanged.**
5. **Phone thumb:** 44 px floor (`@media (max-width: 40rem)` in `app.css`), no
   hover, `flex-shrink: 0` on controls in flex rows, safe-area insets on any
   `position: fixed` overlay, nothing that ticks *inside* a control (Android
   `<select>` lesson), and `check:touch`/`check:overlap`/`check:clipping`/
   `check:safearea` must pass with the minigame open.
6. **Under a minute per attempt** (target: 3 s countdown + <= 20 s play + result).
7. **Latency.** Mobile RTT is 50-300 ms with jitter. **No score may depend on
   when a packet arrives.** See "Latency, once, for every option" below.
8. **Anti-cheat history** (memory: macro detector): an absolute-variance test
   permanently banned fast humans; the fix was coefficient of variation plus a
   minimum window, timer traffic excluded, and a reversal path. Rules for this
   feature: **no automatic penalty of any kind**, only telemetry; never send
   per-tap traffic as WebSocket commands (they would feed `RecordCommand`'s
   macro detector *and* the `ValidateNetworkThroughput` token bucket, whose
   failure is `Socket.Abort()` plus a flood infraction).
9. **Ledger philosophy** (`PowerCeilingTests`): every multiplier declares a hard
   cap or a measurably diminishing curve; linear-and-uncapped is never allowed.
   The world boss is not on the monster-ladder ledger today (grep:
   `PowerCeilingTests` mentions neither Giantslayer nor `WeakPlate`), so this
   feature brings its own small ledger (Task 1 below).
10. **Genre.** Task 10 rejected reflexes because this is an idle game. The
    owner's call overrides that, but it leaves a design duty: **a player who
    does not want to play a reflex game must still be able to contribute**
    (an "auto-strike" at the floor multiplier), and playing must never be worse
    than not playing.

### Latency, once, for every option

The trick that makes every option below latency-immune: **all timing is measured
on the client's own clock, relative to the client's own start of the
challenge**, and the server only checks *wall-clock plausibility*.

- Client: `t0` = the `requestAnimationFrame` timestamp of the first frame that
  draws schedule time 0 (after a 3-2-1 countdown). Each tap is
  `pointerdown.event.timeStamp - t0` (same `performance.now()` origin). **Not
  `click`** (fires after `pointerup`, adds the hold time) and not the next rAF
  (quantises to 16.7 ms, 66 ms at 4x throttle). Animation position is always
  computed *from time* (`angle(now - t0)`), never accumulated per frame, so a
  dropped frame on a slow phone moves nothing.
- Server: stores `IssuedAtUtcMs`. On submit it requires
  `ReceivedAtUtcMs - IssuedAtUtcMs >= countdownMs + lastTapMs - 50` (an honest
  client *always* passes: the network only ever adds time) and
  `<= countdownMs + maxPlayMs + 60,000` (else the challenge has expired).
  RTT jitter appears in neither the score nor any tight bound.
- What latency *cannot* remove: touch-to-event (~20-60 ms) and display
  (frame-to-glass, ~16-50 ms) latency make an honest player's taps
  systematically a little late. Every scoring rule below therefore evaluates
  each tap at `t` and gives the benefit of the doubt over `[t - 35 ms, t + 35 ms]`
  (the `ToleranceMs` constant), or has windows wide enough to absorb it.
  A later refinement is a per-device calibration derived from practice runs; not
  in the first slice.

### What cannot be prevented, stated plainly

A modified client that reads the schedule can compute perfect timestamps and
add human-looking noise. **No client-measured reflex game is bot-proof.** The
design answer is economic, not forensic:

- **The cap must be reachable by a good human** (the multiplier saturates
  before perfect - e.g. best 4 of 5 throws, or full credit at 85 %). Then a bot
  earns exactly what a skilled player earns, and cheating buys nothing over
  practising.
- The worst a cheat can do is move itself from the median to the cap - a
  bounded ratio (the option's `cap / typical` in section 2), on a reward table
  ranked in coarse brackets.
- Plausibility checks refuse only *impossible* submissions (shape errors,
  faster-than-wall-clock), and flag *improbable* ones (inhuman precision) to
  telemetry. Nothing is ever penalised automatically.

---

## 2. Candidates

Common to all five: the server issues a challenge over REST, the client plays it
locally, submits tap timestamps over REST once, and the server scores. Wire
shape and pipeline are in "Server pipeline shared by every option" at the end of
this section.

### A. Strike meter (timing bar)

**Inspiration:** the golf swing meter; Paper Mario / Mario & Luigi "action
commands"; the power bar of every fishing minigame.

**How it plays.** A cursor sweeps across a horizontal bar; a sweet zone sits
somewhere on it. Tap to stop the cursor. Five sweeps per attempt, each faster or
with a narrower zone, some ping-pong, some easing. ~12 s of play.

**Server issues:** per sweep `{startMs, periodMs, easing, zoneCentre, zoneHalfWidth, perfectHalfWidth}`.
**Client reports:** one `tapMs` per sweep (or none).
**Server scores:** cursor position at `tapMs` (+-`ToleranceMs`, best of) ->
perfect / good / miss. Anticipatory, not reactive, so no reaction-time floor.
Plausibility: monotonic, one tap per sweep, each inside its sweep, wall clock.

**Score -> damage:** value per sweep {perfect 1.0, good 0.6, miss 0}; `s` = mean
of best 4 of 5; `M = min(Cap, Floor + (Cap - Floor) * s / 0.85)`.

**Wire:** REST only (see shared pipeline). **Client:** one bar, one
`transform: translateX()` per frame, tap-anywhere area; trivial on a 4x
throttle. **Accessibility:** good (one big target, no spatial search);
colour-blind-safe if the zone has a pattern, not only a colour. **Effort:** S-M
(server ~2 days, client ~2 days). **Risk:** low technically; **design risk
high - it is the most generic and the least "boss fight"**, and it throws the
plate theme away entirely unless bolted on.

### B. Weak-spot tapping (cracks)

**Inspiration:** whack-a-mole; osu!'s approach circles; Monster Hunter Now's
weak-point targeting; Fruit Ninja's "don't hit the bomb".

**How it plays.** Glowing cracks appear on the boss's five plates at seeded
moments, each alive for 600-1100 ms with a shrinking ring; tap them before they
fade. A few decoys (the boss's eye opens - tapping it is a miss). ~15 s.

**Server issues:** `[{spawnMs, cell (0-11 on a 3x4 grid), lifetimeMs, isDecoy}]`.
**Client reports:** `[{index, tapMs}]` for the targets it tapped.
**Server scores:** a tap counts if `spawnMs + 120 <= tapMs <= spawnMs + lifetimeMs + ToleranceMs`;
earlier than 120 ms after an *unpredictable* spawn is below human visual
reaction and is refused as implausible (this is the one option where a
reaction floor is honest). Decoys subtract.

**Score -> damage:** hits minus decoys over targets, saturating as in A.
**Cheat ratio:** same cap; the 120 ms floor makes a naive bot trip telemetry.

**Wire:** REST only. **Client:** up to ~12 absolutely positioned elements
fading in/out; still cheap, but must avoid re-rendering the list per frame
(key by index, CSS animation for the ring). A 3x4 grid on a 390 px phone gives
~110 px cells - comfortably over 44 px. **Accessibility:** weaker - spatial
search under time pressure is harder for low vision; decoys punish
colour-blindness unless shaped differently. **Effort:** M (client grid,
animation, hit areas; server as A). **Risk:** medium - more UI to get right on
glass, and a "which cell did I hit" dispute on small phones.

### C. Rhythm (war drums)

**Inspiration:** Taiko no Tatsujin; Piano Tiles; Guitar Hero lanes.

**How it plays.** Notes fall down three lanes toward a hit line; tap the lane as
a note crosses. ~20 s song.

**Server issues:** a chart `[{atMs, lane}]`. **Client reports:** `[{lane, tapMs}]`.
**Server scores:** match each tap to the nearest unmatched note in its lane within
+-90 ms (good) / +-40 ms (perfect).

**Why it ranks last:** a rhythm game is only a rhythm game with music, and
**Android WebView audio output latency is 100-250 ms and device-specific**; it
needs per-device calibration to be fair. Production audio is also still Git LFS
pointer stubs (memory: task board 2026-09-01), so there is no music to sync to.
Without audio it is option A with lanes. Three lanes at 44 px is fine; 20+
moving notes per chart is the heaviest render of the five (canvas, not DOM).
**Effort:** L. **Risk:** high (audio latency, content authoring per chart,
perf on throttled CPU).

### D. Dodge and strike (parry)

**Inspiration:** Infinity Blade (the mobile-native version of this idea);
Punch-Out!!'s tells; Sekiro's deflect windows.

**How it plays.** The boss telegraphs a blow (wind-up glow on the left, right or
overhead, 600-900 ms tell). Tap **Dodge left / Block / Dodge right** (three big
buttons at the bottom) in the window; a correct read opens a 500 ms counter
window - tap **Strike**. Six exchanges, ~18 s.

**Server issues:** `[{tellAtMs, direction, windowOpenMs, windowCloseMs, counterMs}]`.
**Client reports:** `[{exchange, choice, choiceMs, strikeMs?}]`.
**Server scores:** choice correct AND inside the window (reaction floor 150 ms
after the tell - unpredictable stimulus, so honest), counter inside its window.
This is the only option where the input is part **decision** (read the tell)
and part timing, which answers task 10's "prefer decisions over dexterity"
objection best.

**Score -> damage:** counters landed, saturating. **Wire:** REST only.
**Client:** needs the boss to *visibly* wind up - CSS transforms and a glow on
the existing sprite can do it, but it is art-direction work the others do not
need. Four buttons, each >= 44 px, fixed at the bottom with a safe-area inset.
**Accessibility:** medium - tells must be shape/position, not colour; a
reduced-motion setting must keep the tell readable. **Effort:** M-L.
**Risk:** medium - it is the most "boss fight", and the most sensitive to the
tell being readable on a small screen.

### E. Shield wheel (own proposal) - RECOMMENDED

**Inspiration:** **Knife Hit** (Ketchapp, 2018, 100M+ installs) - a log spins at
changing speeds and you tap to throw a knife into it; also Stack/Helix Jump's
one-thumb, tap-anywhere timing. Both are the dominant idiom of one-thumb mobile
timing games, which is the exact input budget here.

**How it plays.** The boss's five armour plates form a ring that **spins at
changing speeds and reverses**. The player has chosen a target plate (the
existing decision). They have **five spears**; tapping anywhere in the lower half
of the screen throws one upward into the ring. Each spear lands wherever the
ring *is* 120 ms later. Hit the chosen plate's **seam** (a 20 degree band in its
centre) for a perfect, the rest of the plate for a hit, a rivet between plates
or another plate for a glance. Broken plates are drawn cracked on the ring and a
revealed weak plate glows - **the shared board every player already sees
becomes the thing you aim at.** ~3 s countdown + <= 20 s.

**Why this one:** it is **the only candidate that keeps task 10's whole design
alive** (five plates, the global break/reveal board, the 1.67x value of reading
it) and turns the part the owner called chance - "did I click the right plate" -
into "can I *land* on the plate I chose". It is a pure function of time
(`angle(t)`), so scoring is exact and latency-immune, and "tap anywhere" makes
the 44 px problem disappear for the core input.

**Server issues** (`POST /api/v1/worldboss/challenge`):
```json
{
  "ChallengeId": 918273,
  "CountdownMs": 3000, "MaxPlayMs": 20000, "Spears": 5,
  "FlightMs": 120, "MinReloadMs": 350, "ToleranceMs": 35,
  "SeamDegrees": 20, "RivetDegrees": 3,
  "StartAngleDeg": 137.0,
  "Segments": [ { "DurationMs": 1400, "DegPerSec": 150 },
                { "DurationMs": 900,  "DegPerSec": -210 }, ... ],
  "TargetPlate": 2
}
```
Segments are piecewise-constant angular velocity, |w| in [90, 210] deg/s,
durations 0.7-2.2 s, covering `MaxPlayMs`; generated server-side with
`RandomNumberGenerator` (not `Random.Shared` - a predictable seed would let a
tool precompute). Sent explicitly (rule 2), never as a seed.

**Client reports** (`POST /api/v1/worldboss/strike`):
`{ "ChallengeId": 918273, "TapMs": [2140, 3310, 4012, 6100, 7355] }`.

**Server scores** (pure, `ShieldWheelScorer`): for each tap,
`landing = angleAt(tap + FlightMs)`, evaluated across
`[tap - ToleranceMs, tap + ToleranceMs]` taking the best class; the impact point
is fixed at the ring's bottom. Classes: **Seam 1.0, Plate 0.6, Glance 0.15,
None 0**. `s = mean of best 4 of 5`.
Plausibility (refuse = resolve as auto-strike + telemetry, see anti-scum): at
most `Spears` taps, strictly increasing, `>= 0`, `<= MaxPlayMs`, consecutive
gaps `>= MinReloadMs - 50` (the client locks the throw button for `MinReloadMs`,
so an honest client never violates it), and the wall-clock rule.
Telemetry only: all taps within +-3 ms of the seam centre-crossing (inhuman
precision) - one new, unique `Value1` code (memory: telemetry codes have
collided across unrelated checks before; grep for the number first).

**Score -> damage:** `M = min(Cap, Floor + (Cap - Floor) * s / 0.85)`.
Recommended `Floor = 1.0` (= auto-strike = today's value, so nobody loses
anything by not playing), `Cap = 2.0`, saturating at `s = 0.85` (four seams out
of five, or seams plus a hit). Final strike =
`attack x (1 + Giantslayer) x plateMultiplier (3.0 weak / 1.0) x M`,
still through `ComputeAppliedDamage`'s clamp. Random tapping: P(seam) = 20/360,
P(plate) = 52/360, P(glance) ~ 0.8 -> expected s ~ 0.26 -> M ~ 1.31; a practised
player ~ 1.8-2.0. **Cheat ratio over a skilled human: 1.0x** (both hit the cap);
over random tapping: ~1.5x.

**Wire:** REST only; `ClientCommandPacket` and `StateUpdatePacket` unchanged
(0 bytes), no protocol regeneration. **Client:** one SVG ring (five paths +
crack/glow overlays) rotated with a single `transform: rotate()` per frame from
`angleAt(now - t0)`, written straight to the element (not through Svelte state -
no 60 Hz reactivity churn beside a 10 Hz packet stream), spears as a small fixed
pool of absolutely positioned elements. Compositor-only work; the rAF loop stops
when the overlay closes. **Accessibility:** tap anywhere (no small target);
plates distinguished by number and pattern, not colour alone; a "Slow wheel"
setting is NOT offered (it changes the score) - the accessibility answer is
**Auto-strike at the floor** and a free **Practice** mode with no attempt cost.
`navigator.vibrate` for haptics (Android WebView supports it; do not add a
Capacitor Haptics plugin without installing it - see CLAUDE.md's Capacitor
paragraph). **Effort:** M (server ~3 days incl. tests; client ~3 days; exercise
+ checks ~1 day). **Risk:** medium-low technically; the design risk is only the
numbers, which the owner sets.

**Variant E2 (free aim, for the owner to consider):** no pre-chosen plate - every
spear's landing plate is struck (breaks/reveals per spear), and the server
answers each throw so a weak-plate hit glows *mid-attempt*. It removes chance
completely, but (a) needs a request per throw for the reveal (latency-tolerant -
it only delays feedback, not scoring - but 5 round trips per attempt), and (b)
with 15 spears per player **the first competent player solves the board for
everyone**, so the crowd-deduction layer from task 10 collapses in the first
hour of each encounter. E (chosen plate) keeps that layer; E2 trades it for
pure skill.

### Server pipeline shared by every option

```
POST /api/v1/worldboss/challenge   (REST, like the Delve)
  -> eligibility (event active, attempts left, session open for the whole
     challenge, larder) - the SAME function task 25 makes the attack use,
     answering a Result string on refusal, never a silent 200
  -> WorldBossChallengeRegistry (in memory, one outstanding per player,
     IDEMPOTENT: asking again returns the same challenge)
POST /api/v1/worldboss/strike
  -> shape + wall-clock checks, ShieldWheelScorer (pure) -> landings
  -> enqueue WorldBossStrikeOrder{playerId, plate, landings, completion TCS}
     on PlayerSessionRegistry.WorldBossStrikeQueue
tick: WorldBossTickCoordinator drains the queue (BUDGETED - CLAUDE.md
      "unbounded drain"), computes attack x Giantslayer from the payload as
      today, calls QueueAttack(order)
ExecuteAttackAsync: inside the FOR UPDATE transaction, where the weak index
      already lives, applies the plate rule, computes M from the landings,
      applies damage, commits, completes the TCS with {Result, Damage, M,
      landings, plate outcome}
REST handler awaits the TCS (5 s timeout -> "Queued") and returns it; the
      screen shows a result card. Attempt pip + boss HP still arrive on the
      StateUpdate stream as today.
```

Why REST and not the fixed-layout packet: a tap log is variable-length, and
`ClientCommandPacket` (341 bytes, demultiplexed by exact size) would need a new
fixed array - e.g. `fixed ushort StrikeTapMs[8]` (+16 bytes -> 357; check it
collides with none of 22/26/139/147/530/809). The Delve already proved the REST
shape for a server-authoritative minigame, and REST also keeps per-tap traffic
away from the macro detector and the token bucket. Note: REST DTOs are
hand-written on the client (`rest.ts`), not generated - keep them tiny and pin
the enum of `Result` strings with a test (Task 5).

**Anti-scum.** An issued challenge is a commitment: re-requesting returns the
same one, and a challenge that expires unsubmitted resolves as an auto-strike
at the floor the next time the player asks for a challenge or opens the screen
(lazy - no new `StartCron` loop, so no `CronWorkerGuardTests` inventory entry).
Leaving the app mid-throw (`visibilitychange` -> hidden) submits what was thrown;
unthrown spears count as None. The screen says this before the countdown.

**Legacy path.** With the flag on, a bare `AttackWorldBoss` from a stale bundle
must be refused *visibly* (not `TerminateSessionForSecurity`) or it bypasses the
minigame. With the flag off, nothing changes.

---

## 3. Recommendation and the brainstorm agenda

### Recommendation: E, the shield wheel, with a chosen plate

1. It is the only option that answers the owner's actual complaint ("whether you
   click the right plate is chance") **without** discarding the task-10 design
   that works: the shared, global plate board stays the heart of the fight.
2. It is exactly scorable - landing is a pure function of the server's own
   schedule and the tap time - and latency-immune by construction.
3. It is the cheapest to make good on a phone: one thumb, tap anywhere, one
   rotating SVG. No audio (C), no telegraph art (D), no spatial search (B).
4. Its popular reference (Knife Hit) is a proven one-thumb idiom that players
   read without a tutorial.
5. With `Floor = auto-strike = 1.0x`, the change is a pure upside for every
   player, including idle ones who never play it - which is the honest answer
   to task 10's genre objection.

**Runner-up: D (parry)** if the owner wants the fight to *feel* like fighting
more than aiming; it costs roughly one extra week of client/art work.
**Cheapest prototype if the owner is unsure: A** (two days), then decide.

### Questions for the owner (the agenda)

1. **Which candidate?** (A / B / C / D / E / E2.) If E: chosen plate (keeps the
   crowd deduction) or free aim E2 (pure skill, board solved early)?
2. **Floor and cap.** Recommended 1.0x-2.0x (nobody loses vs today). Or
   0.75x-1.5x (skill matters, idle players lose a quarter)? Or 0.5x-2x?
3. **Keep the weak plate at 3x** on top of the skill multiplier (max 6x of base
   attack), or drop it to 2x (max 4x)? The boss has never been killed, so there
   is no measured HP-vs-damage balance to protect yet - but the Top 1 % bracket
   is decided by exactly this product.
4. **Auto-strike:** offer it (recommended - idle genre, accessibility), and at
   the floor? Or require playing?
5. **Practice mode** with no attempt cost and no damage (recommended - three
   attempts is too small a budget to learn in)? Server-issued (one generator)
   is recommended over a client-side practice generator.
6. **The 300 s battle session.** Three ~25 s attempts fit, but the cap was
   already flagged by task 10 as hostile to idle play. Keep, lengthen (e.g.
   30 min), or drop now that each attempt is an explicit sitting?
7. **Abandoned challenge = auto-strike at the floor** - acceptable? (The
   alternative - free retries - makes the attempt budget meaningless.)
8. **Difficulty over the encounter:** constant, or harder as the boss's HP
   falls (faster wheel in the last 25 %) - a "phase" feel without a schedule?
9. **Haptics/sound:** vibrate on a seam hit? (Sound waits for real audio assets.)
10. **Which window to ship for:** Oct 1-7 is too close after task 25; Oct 15-22
    is realistic for a flagged rollout with a phone playtest first.

---

## 4. How to approach this (for the implementing agent)

1. **Brainstorm first.** Put section 3's questions to the owner (in Czech -
   memory says answer in Czech), with section 2's table as the menu. Do not
   start with a prototype "to show them" - the owner asked to decide first.
2. **Write the spec** to `docs/superpowers/specs/2026-09-XX-world-boss-minigame-design.md`:
   the chosen option, every number from question 2-8, the challenge JSON, the
   refusal `Result` list, what the screen says for each. Append a "Task 36"
   section to `docs/world_boss_design.md` pointing at it (that doc's "What is
   deliberately NOT in this design: Minigames" line must be updated, not left
   contradicting the code).
3. **Prototype behind a flag.** `FOLKIDLE_BOSS_MINIGAME` = `off` (default) |
   `shieldwheel`. There is no feature-flag system in this server - read it once
   at startup like the other `FOLKIDLE_*` env vars and expose it on
   `GET /api/v1/worldboss/challenge` (`Result: "Disabled"`) so the client can
   fall back to today's plate button. The first phase is the smallest playable
   slice: Tasks 1-5 below with **Practice only** (no damage), so the owner can
   play it on the phone before it touches the shared pool.
4. **Then the phased build** below, one PR per phase, `security-review` on the
   PR that turns scoring into damage (task index: 36 is an anti-cheat surface).

The plan below assumes the recommendation (E, chosen plate, Floor 1.0, Cap 2.0,
weak plate 3x kept). **Re-derive every number from the spec before starting.**

### Global constraints

- **Task 25 merged first**, including its window-forcing hook and visible
  refusals. If task 25 did not add a way to force the window open from
  `exercise.mjs`, Task 6 adds it (admin-only REST, dev-fixture account).
- **Nothing the client sends is a quantity the server adopts.** The only client
  numbers are tap timestamps, bounded and scored against the server's schedule.
- **No change to `ClientCommandPacket` or `StateUpdatePacket`.** If a phase
  finds it needs one, stop and use the `add-command` skill (note: that skill's
  "currently 359" for `ExpectedClientCommandSize` is stale - the constant is
  **341**, and `ExpectedStateUpdateSize` is **809**; fix the skill text in the
  same PR).
- **Stop the server before `dotnet build`** (the hook blocks it anyway); Docker
  must be up for `dotnet test`.
- **Each task is its own commit; each phase its own PR.** Flag stays `off` in
  production until the owner has played it on the phone.
- **No automatic penalties.** Telemetry only. Nothing in this feature may call
  `RequestShadowBan` or set `Quarantine_Active`.

---

### Task 1: The rules as pure code, with their own ledger

**Files:**
- Create: `server/FolkIdle.Server/Domain/Combat/WorldBossStrike/ShieldWheelSchedule.cs` (record types + `Generate(RandomNumberGenerator)` + `AngleAt(ms)`)
- Create: `server/FolkIdle.Server/Domain/Combat/WorldBossStrike/ShieldWheelScorer.cs` (`Land(schedule, taps) -> ThrowLanding[]`, `Validate(...) -> SubmissionVerdict`)
- Create: `server/FolkIdle.Server/Domain/Combat/WorldBossStrike/WorldBossStrikeRules.cs` (Floor, Cap, SaturationScore, class values, best-k-of-n; `Multiplier(landings, targetPlate)`)
- Create: `server/FolkIdle.Server.Tests/ShieldWheelScorerTests.cs`
- Create: `server/FolkIdle.Server.Tests/WorldBossStrikeLedgerTests.cs`

- [ ] **Step 1: Write the failing scorer tests with synthetic tap logs.** One fixed
  schedule (hand-built segments, not generated) and these logs, each asserting
  classes and `M`:
  - *perfect*: taps at the exact seam-centre crossing minus `FlightMs` -> 5 Seam, `M == Cap`.
  - *human*: the perfect log plus deterministic noise N(0, 25 ms) from a fixed seed -> `M` in [1.7, 2.0].
  - *late device*: the perfect log shifted +30 ms (display latency) -> still Seam via `ToleranceMs`.
  - *spray*: 5 evenly spaced taps -> `M` in [1.0, 1.5].
  - *no taps* -> `M == Floor`.
  - *one miss forgiven*: 4 Seam + 1 None -> `M == Cap` (best 4 of 5).
  - *reversal*: a tap across a direction flip lands where `AngleAt` says, not where constant speed would.
- [ ] **Step 2: Write the failing cheat/shape tests** - each must produce
  `SubmissionVerdict.Refused*` (resolves as auto-strike) and never an exception:
  6 taps; non-increasing; negative; beyond `MaxPlayMs`; two taps 100 ms apart
  (< `MinReloadMs`); `ReceivedAt - IssuedAt` shorter than `CountdownMs + lastTap`
  (a bot submitting without waiting); expired; wrong `ChallengeId`; NaN/huge
  numbers from JSON. And one *precision* case (all taps within +-1 ms) that is
  **accepted** at `Cap` and sets `SuspiciouslyPrecise = true` - the test pins
  that suspicion never lowers the score.
- [ ] **Step 3: Implement** until green. `AngleAt` integrates segments from
  `StartAngleDeg`; landing class from the angle relative to the target plate's
  72 degree sector, seam band and rivet bands; tolerance = best class over the
  window sampled every 5 ms.
- [ ] **Step 4: The ledger.** `WorldBossStrikeLedgerTests` prints Giantslayer cap
  x weak plate x `Cap` and asserts: the skill lever has a hard cap (`Cap <= 2.0`),
  auto-strike pays exactly `Floor` and every playable score pays `>= Floor`
  (playing never loses to not playing; `Floor`'s value is the spec's), `Cap / Floor <= 2.0`, the product of all three stays
  under a stated ceiling, and `M` is monotonic non-decreasing in `s` (sample
  0..1 in 0.01 steps). Assert on every number it prints (CLAUDE.md: a printed
  number is decoration).
- [ ] **Step 5: Generator property test.** 10,000 generated schedules: every
  segment speed within bounds, total duration >= `MaxPlayMs`, and the target
  plate's seam passes the impact point at least 5 times in `MaxPlayMs` (else the
  challenge is unwinnable - a real failure mode of random schedules).
- [ ] **Step 6: Commit** `feat(world-boss): shield wheel rules and scorer, pure and ledgered`.

### Task 2: Challenge registry and REST endpoints (flagged, practice first)

**Files:**
- Create: `server/FolkIdle.Server/Domain/Combat/WorldBossStrike/WorldBossChallengeRegistry.cs` (ConcurrentDictionary by player; issue/get/consume; lazy expiry)
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs` (routes beside the Delve's: `GET/POST /api/v1/worldboss/challenge`, `POST /api/v1/worldboss/strike`, `POST /api/v1/worldboss/practice`, `POST /api/v1/worldboss/practice/score`)
- Modify: `server/FolkIdle.Server/Program.cs` (register the registry; read `FOLKIDLE_BOSS_MINIGAME`)
- Create: `server/FolkIdle.Server.Tests/WorldBossChallengeRegistryTests.cs`

- [ ] **Step 1: Failing tests:** issuing twice returns the same challenge
  (anti-scum); issue refused with a named `Result` for each eligibility failure
  (event inactive, attempts spent, session would close before `CountdownMs +
  MaxPlayMs + 15 s`, larder empty, flag off -> `Disabled`); an expired
  challenge is reported as `ResolvedAsAutoStrike` on the next issue; practice
  challenges never touch attempts.
- [ ] **Step 2: Implement.** Eligibility must call the one function task 25
  introduced - do not write a second copy of the gates (two sources of truth).
  Every refusal answers **200 with a `Result` string** (Delve precedent), never
  a bare status the screen cannot explain.
- [ ] **Step 3: Practice scoring endpoint** returns classes and `M` only - no
  queue, no damage. This is the prototype slice the owner plays.
- [ ] **Step 4: Commit** `feat(world-boss): challenge issue and practice over REST, behind FOLKIDLE_BOSS_MINIGAME`.

### Task 3: The client ring, practice only

**Files:**
- Create: `client_web/src/lib/game/shieldWheel.ts` (`angleAt`, landing preview for drawing only - the server is the authority)
- Create: `client_web/src/lib/ui/ShieldWheel.svelte`
- Modify: `client_web/src/lib/net/rest.ts` (typed DTOs + four calls)
- Modify: `client_web/src/routes/WorldBoss.svelte` (a "Practice" button; the plate picker stays)
- Create: `client_web/tests/shieldWheel.test.ts`

- [ ] **Step 1: Shared fixture mirror.** Add
  `server/FolkIdle.Server.Tests/Fixtures/shield_wheel_cases.json` (schedule + taps
  + expected landing angle/class). `ShieldWheelScorerTests` and
  `shieldWheel.test.ts` both read it and must agree - the mechanical guard
  against the client drawing the ring somewhere the server does not score it
  (same idea as `serverMirrors.test.ts`).
- [ ] **Step 2: The component.** Runes mode only (no `export let` -
  `tests/runesMode.test.ts`); no local named `derived`; a snippet rendered with
  `{@render}`. One `requestAnimationFrame` loop started on countdown end and
  cancelled in the `$effect` cleanup; rotation written to the SVG element's
  `style.transform` directly. Input on `pointerdown` over a lower-half throw
  zone with `touch-action: manipulation`; the zone locks for `MinReloadMs`.
  `visibilitychange` -> hidden submits early. The overlay is `position: fixed`
  and pads with `var(--safe-area-inset-*, env(safe-area-inset-*, 0px))`.
- [ ] **Step 3: Result card**: five spear outcomes, `M`, and (practice) "no
  damage dealt". Everything that ticks (countdown) lives outside any control.
- [ ] **Step 4:** `npm run check:ratchet`, `npm test`, then **load the page**
  (svelte-check does not catch the Svelte runtime traps).
- [ ] **Step 5: Commit** `feat(world-boss): shield wheel practice on the World Boss screen`.

**PHASE GATE: deploy with the flag `shieldwheel` on the dev box only / or practice-only in production, and have the owner play it on the phone (APK live-update bundle). Tune the numbers in the spec, not in the code, before Task 4.**

### Task 4: Scoring becomes damage

**Files:**
- Modify: `server/FolkIdle.Server/Engine/PlayerSessionRegistry.cs` (`WorldBossStrikeQueue`)
- Modify: `server/FolkIdle.Server/Domain/Combat/WorldBossTickCoordinator.cs` (budgeted drain; reuse the attack/Giantslayer computation - extract, do not copy)
- Modify: `server/FolkIdle.Server/Engine/WorldBossEngine.cs` (`QueueAttack(WorldBossStrikeOrder)`; `ExecuteAttackAsync` applies `M` inside the transaction and completes the TCS with an outcome; every existing rollback completes it with a reason)
- Modify: `server/FolkIdle.Server/Engine/ClientCommandValidator.cs` / coordinator (flag on: bare `AttackWorldBoss` refused visibly, not `TerminateSessionForSecurity`)
- Create: `server/FolkIdle.Server.Tests/WorldBossStrikeIntegrationTests.cs` (Testcontainers)

- [ ] **Step 1: Failing integration tests:** a submitted strike reduces boss HP by
  `attack x giantslayer x plate x M` (weak plate forced via the snapshot, as
  `WorldBossArmourTests` does deterministically); the attempt row increments;
  the plate breaks/reveals exactly as today; `M == Floor` for auto-strike;
  a refused submission still spends the attempt at the floor and says so;
  `_playerDamageMap` and the Redis hash receive the same applied damage; the
  secret weak index never appears in any REST response before it is revealed
  (assert on the serialized JSON).
- [ ] **Step 2: Implement.** Keep `ComputeAppliedDamage`'s clamp. Drain budget =
  queue depth read once per tick (CLAUDE.md, `GatheringGrantStarvationTests`
  shape). The REST handler awaits the TCS with a 5 s timeout and answers
  `Queued` on timeout - the StateUpdate stream still carries the pips and HP.
- [ ] **Step 3: No silent rollback.** Enumerate every return path of
  `ExecuteAttackAsync`; each must complete the TCS with a named `Result`. A test
  walks the `WorldBossStrikeResult` enum and asserts each value is produced by
  some test (a result nobody can produce is dead; a path with no result is a lie).
- [ ] **Step 4: Commit** `feat(world-boss): the shield wheel strike deals server-computed damage`.

### Task 5: The screen plays for real

**Files:**
- Modify: `client_web/src/routes/WorldBoss.svelte` (Strike -> challenge -> wheel -> result; Auto-strike button; practice stays)
- Modify: `client_web/src/lib/net/commands.ts` (legacy `attackWorldBoss` only when the server says `Disabled`)
- Modify: `client_web/src/lib/ui/wikiData.ts` (+ `tests/wiki.test.ts`)
- Create/extend: `client_web/tests/worldBossResults.test.ts`

- [ ] **Step 1:** every server `Result` string has a player-facing sentence;
  the test fails on an unmapped one (mirror of the enum - list both in one place
  and compare, like `commandsAudit.test.ts`).
- [ ] **Step 2:** Auto-strike sends `strike` with an empty `TapMs` (the same
  code path, scored at the floor - not a second endpoint).
- [ ] **Step 3:** `npm run check:touch`, `check:overlap`, `check:clipping`,
  `check:safearea` with the wheel overlay open at 390 px (add the overlay state
  to `scripts/screens.mjs` once so all four see it), and `check:perf` sampling
  12 s *while the wheel spins* at 4x throttle - budgets unchanged.
- [ ] **Step 4: Commit** `feat(world-boss): strike, auto-strike and results on the World Boss screen`.

### Task 6: `exercise.mjs` plays the wheel deterministically

Playwright cannot have reflexes, but it does not need them: the schedule is not
a secret (the client already holds it), so the screen exposes it on the overlay
as `data-schedule` (JSON) - harmless, and the only test hook.

**Files:**
- Modify: `client_web/scripts/exercise.mjs` (the world boss block ~line 1043)
- Modify (only if task 25 did not): a dev/admin-only REST route that opens the
  window and clears the fixture's attempt rows

- [ ] **Step 1: Open a fresh window** through the hook (never depend on the
  calendar), so three attempts are available on every run; after the block,
  leave the window as found (round-trip - CLAUDE.md "a check that spends fixture
  state passes once and fails forever").
- [ ] **Step 2: Blind run.** Start a strike, tap the throw zone five times evenly;
  assert the pip moved, boss HP decreased, and the result card shows `M` inside
  `[Floor, Cap]`.
- [ ] **Step 3: Aimed run.** Read `data-schedule`, compute seam crossings with
  the same `angleAt` the client uses, and `page.mouse` tap at
  `crossing - FlightMs` (Playwright jitter ~10-30 ms is inside `ToleranceMs`);
  assert `M_aimed > M_blind` - **this is the check that proves skill is wired to
  damage**, the "output side" CLAUDE.md warns about.
- [ ] **Step 4: Auto-strike run** spends the third attempt and shows `M == Floor`.
- [ ] **Step 5: New-player pass:** the fresh-account context at the end of
  `exercise.mjs` opens World Boss and sees Practice work (the fixture is an
  admin at level 40 and cannot prove this).
- [ ] **Step 6:** re-seed if needed (`--seed-dev` is idempotent), run
  `npm run exercise` to green, commit `test(exercise): the world boss wheel, blind, aimed and auto`.

### Task 7: Docs, review, deploy, flip

- [ ] Update `docs/world_boss_design.md` (new section; remove the "no minigames"
  line), `docs/TASK_BOARD.md` task 36 (result write-up, like task 10's PHASE H),
  `docs/architecture/CURRENT_IMPLEMENTATION_STATE.md` (new REST routes, queue,
  flag), `docs/architecture/NEXT_STEPS_BACKLOG.md` top section, and the
  `add-command` skill's stale sizes.
- [ ] Run `code-review` and `security-review` on the PR (client-reported
  outcomes). Run the full `dotnet test` (Docker up) and `npm run build`.
- [ ] Deploy with the `deploy` skill (SSH push; `git pull` on the box does not
  work) with `FOLKIDLE_BOSS_MINIGAME=off`, verify `smoke:screens` against
  production, then set it to `shieldwheel` in `ops/oracle` compose env and
  redeploy **before** the target window opens. Watch the first live window's
  `player_world_boss_attempts` and the new telemetry code via the read-only
  Supabase MCP (SELECT only) - an empty table again means task 25's class of
  defect, not a balance question.

---

## Side findings made while writing this (not fixed - read-only brief)

- `.claude/skills/add-command/SKILL.md` says `ExpectedClientCommandSize` is
  "currently 359" and describes a 700-byte `StateUpdatePacket` ceiling with one
  byte of headroom; the constants are **341** and **809**.
- `ValidateWorldBossAttackRequest` returning false on an **inactive event**
  leads `WorldBossTickCoordinator` to `TerminateSessionForSecurity` - a
  disconnect for what is a normal race at a window boundary. Worth checking in
  task 25.
- `WorldBossEngine.QueueAttack` is a bare `Task.Run` with its body guarded by
  try/catch, but the scope and transaction are created *before* the `try`
  (`CreateScope`/`BeginTransactionAsync` at lines ~498-500) - CLAUDE.md's
  "a guard that starts AFTER BeginTransactionAsync is not a guard". A pooler
  refusal there is an unobserved task exception and a silently lost strike.
