<script lang="ts">
  // Modul: THE SHIELD WHEEL OVERLAY (task 36, spec 2 and 7). Practice only in
  // Phase 1: it posts to /practice/score and deals no damage.
  //
  // What it sends is times and choices, never a score. Every timestamp is
  // pointerdown.timeStamp minus t0 (performance.now() at the end of the
  // countdown); the server scores those against its own copy of the schedule.
  // angleAt() here draws the ring and previews a landing so the spear can land
  // instantly - the server's answer is authoritative and redraws it.
  //
  // One requestAnimationFrame loop, and the rotation is written straight to the
  // ring's style.transform - never through Svelte state, which would re-render
  // the overlay sixty times a second. State changes only when something the
  // player can read changes: the seconds left, an interrupt opening or closing.
  import { onDestroy } from 'svelte';
  import {
    angleAt,
    interruptAt,
    isRead,
    landWheelTap,
    PLATE_COUNT,
    PLATE_DEGREES,
    SEAM_DEGREES,
    RIVET_DEGREES,
    type WheelSchedule,
    type ParryChoice,
    type SpearClass,
  } from '../game/shieldWheel';
  import {
    throwSpear,
    scoreBossPractice,
    type ShieldWheelChallenge,
    type ParryEntryDto,
    type CounterEntryDto,
    type PracticeScoreResponse,
  } from '../net/rest';

  // `challenge` is read ONCE, on purpose, which is what svelte-check's
  // state_referenced_locally warnings on this file are about: a challenge
  // never changes during a run, and the parent keys this component on the
  // ChallengeId, so a new challenge is always a new instance.
  let { challenge, onclose, onagain }: {
    challenge: ShieldWheelChallenge;
    onclose: () => void;
    onagain: () => void;
  } = $props();

  const schedule: WheelSchedule = {
    StartAngleDeg: challenge.StartAngleDeg,
    Segments: challenge.Segments,
    Interrupts: challenge.Interrupts,
  };

  type Phase = 'countdown' | 'play' | 'submitting' | 'result' | 'error';
  let phase = $state<Phase>('countdown');
  let countdownLeft = $state(Math.ceil(challenge.CountdownMs / 1000));
  let secondsLeft = $state(Math.ceil(challenge.MaxPlayMs / 1000));
  let errorText = $state('');
  let result = $state<PracticeScoreResponse | null>(null);

  interface Spear {
    seq: number;
    plate: number;
    cls: SpearClass;
    weak: boolean;
    counter: boolean;
  }
  let spears = $state<Spear[]>([]);
  let lost = $state(0);

  const taps: { Seq: number; TapMs: number }[] = [];
  const parries: ParryEntryDto[] = [];
  const counters: CounterEntryDto[] = [];
  let nextSeq = 0;
  let lastWheelTap = -Infinity;

  // The interrupt on screen, and where the player is in it.
  let activeInterrupt = $state<number>(-1);
  let parryOpen = $state(false);
  let parryRemainingMs = $state(0);
  let counterOpen = $state(false);

  // Modul: THE BUTTONS ARE NAMED BY THE BLOW, NOT BY THE DODGE (playtest,
  // 2026-09-25, spec 3.5). "From the left" used to be "Dodge right": the player
  // had to read the tell and then invert it inside a second. The player now
  // taps the side the blow comes from, and this map sends the dodge that
  // answers it. The wire and the scorer did not change - the server still
  // receives a ParryChoice, still checks it against the tell, and still
  // refuses anything faster than the reaction floor.
  const ANSWER_FOR: Record<'Left' | 'Overhead' | 'Right', ParryChoice> = {
    Left: 'DodgeRight',
    Overhead: 'Block',
    Right: 'DodgeLeft',
  };
  const answered = new Set<number>();
  const readInterrupts = new Set<number>();
  const counterUsed = new Set<number>();

  const spearsLeft = $derived(challenge.Spears - spears.length - lost);
  const weakPlates = $derived(new Set(spears.filter((s) => s.weak).map((s) => s.plate)));

  let ring: SVGGElement | undefined = $state();
  let t0 = 0;
  let raf = 0;
  let countdownTimer: ReturnType<typeof setInterval> | undefined;
  let finished = false;

  function now(): number {
    return performance.now() - t0;
  }

  function vibrate(pattern: number | number[]) {
    if (typeof navigator !== 'undefined' && 'vibrate' in navigator) {
      try {
        navigator.vibrate(pattern);
      } catch {
        /* a phone that refuses is fine */
      }
    }
  }

  // Test hook, beside data-schedule: when play began on the page's own clock
  // (performance.now()). exercise.mjs aims by scheduling taps against it. Not a
  // secret - the schedule already is public, and this is the player's own clock.
  let t0Attr = $state(0);

  function startPlay(alreadyPlayedMs: number) {
    t0 = performance.now() - alreadyPlayedMs;
    t0Attr = t0;
    phase = 'play';
    raf = requestAnimationFrame(frame);
  }

  // Modul: A REOPENED RUN RESUMES FROM WHAT THE SERVER KEPT (code review,
  // 2026-09-25). Starting again at Seq 0 with five spears reused Seqs the
  // server had already answered, and it replayed the OLD answers against the
  // new taps: wrong chips, a stale weak glow, and a card that disagreed with
  // them. The challenge carries its answered throws and the parries they
  // reported, and those are the starting state here.
  {
    const throws = [...(challenge.Throws ?? [])].sort((a, b) => a.Seq - b.Seq);
    for (const t of throws) {
      spears.push({ seq: t.Seq, plate: t.Plate, cls: t.Class, weak: t.WeakHit, counter: t.Counter !== null });
      if (t.Counter) {
        counters.push(t.Counter);
        counterUsed.add(t.Counter.Interrupt);
      } else {
        taps.push({ Seq: t.Seq, TapMs: t.TapMs });
        lastWheelTap = Math.max(lastWheelTap, t.TapMs);
      }
      nextSeq = Math.max(nextSeq, t.Seq + 1);
    }
    const playedSoFar = challenge.ElapsedMs - challenge.CountdownMs;
    for (const p of challenge.Parries ?? []) {
      parries.push(p);
      answered.add(p.Interrupt);
      const interrupt = schedule.Interrupts.find((i) => i.Index === p.Interrupt);
      if (interrupt && isRead(interrupt, p.Choice, p.ChoiceMs)) readInterrupts.add(p.Interrupt);
      else lost += 1;
    }
    // A response window that closed while the screen was away, unanswered.
    for (const i of schedule.Interrupts) {
      if (!answered.has(i.Index) && i.TellAtMs + i.ResponseCloseMs < playedSoFar) {
        answered.add(i.Index);
        lost += 1;
      }
    }
  }

  // Resume where a reopened screen left off; otherwise count down.
  {
    const playedMs = challenge.ElapsedMs - challenge.CountdownMs;
    if (playedMs >= challenge.MaxPlayMs) {
      queueMicrotask(finish);
    } else if (playedMs > 0) {
      startPlay(playedMs);
    } else {
      const countdownEnds = performance.now() + (challenge.CountdownMs - challenge.ElapsedMs);
      countdownTimer = setInterval(() => {
        const left = countdownEnds - performance.now();
        if (left <= 0) {
          clearInterval(countdownTimer);
          startPlay(0);
        } else {
          countdownLeft = Math.ceil(left / 1000);
        }
      }, 50);
    }
  }

  function frame() {
    if (phase !== 'play') return;
    const t = now();
    if (ring) ring.style.transform = `rotate(${-angleAt(schedule, t)}deg)`;

    const s = Math.max(0, Math.ceil((challenge.MaxPlayMs - t) / 1000));
    if (s !== secondsLeft) secondsLeft = s;

    const interrupt = interruptAt(schedule, t);
    const index = interrupt ? interrupt.Index : -1;
    if (index !== activeInterrupt) activeInterrupt = index;

    const wantParry =
      interrupt !== null && !answered.has(interrupt.Index) && t <= interrupt.TellAtMs + interrupt.ResponseCloseMs;
    if (wantParry && !parryOpen && interrupt) {
      // The time bar animates over whatever is left of the window from the
      // frame it appears on - set once, then CSS runs it (nothing ticks here).
      parryRemainingMs = Math.max(0, interrupt.TellAtMs + interrupt.ResponseCloseMs - t);
    }
    if (wantParry !== parryOpen) parryOpen = wantParry;

    // A response window that closed with no answer costs a spear.
    if (interrupt !== null && !answered.has(interrupt.Index) && t > interrupt.TellAtMs + interrupt.ResponseCloseMs) {
      answered.add(interrupt.Index);
      if (spearsLeft > 0) lost += 1;
    }

    const wantCounter =
      interrupt !== null && readInterrupts.has(interrupt.Index) && !counterUsed.has(interrupt.Index) && spearsLeft > 0;
    if (wantCounter !== counterOpen) counterOpen = wantCounter;

    if (t >= challenge.MaxPlayMs || (spearsLeft <= 0 && !counterOpen)) {
      finish();
      return;
    }
    raf = requestAnimationFrame(frame);
  }

  async function send(seq: number, tapMs: number, counter: CounterEntryDto | null) {
    try {
      const answer = await throwSpear({
        ChallengeId: challenge.ChallengeId,
        Seq: seq,
        TapMs: tapMs,
        Counter: counter,
        Parries: [...parries],
      });
      if (!answer || answer.Result !== 'Landed') return;
      spears = spears.map((s) =>
        s.seq === seq ? { ...s, plate: answer.Plate, cls: answer.Class, weak: answer.WeakHit } : s,
      );
      if (answer.WeakHit) vibrate([30, 40, 60]);
    } catch {
      // The glow is the only thing a lost answer costs: the score comes from the finish.
    }
  }

  function throwAtWheel(event: PointerEvent) {
    if (phase !== 'play' || spearsLeft <= 0) return;
    const tapMs = event.timeStamp - t0;
    // Code review, 2026-09-25: the run ends on the NEXT frame, so a tap in the
    // last ~16 ms after MaxPlayMs was still sent - and the server refuses the
    // whole log for one time past MaxPlayMs, throwing away every good spear.
    if (tapMs > challenge.MaxPlayMs) return;
    if (interruptAt(schedule, tapMs) !== null) return;
    if (tapMs - lastWheelTap < challenge.MinReloadMs) return;
    lastWheelTap = tapMs;

    const seq = nextSeq++;
    taps.push({ Seq: seq, TapMs: tapMs });
    const landing = landWheelTap(schedule, tapMs);
    spears = [...spears, { seq, plate: landing.plate, cls: landing.cls, weak: false, counter: false }];
    if (landing.cls === 'Seam') vibrate(30);
    void send(seq, tapMs, null);
  }

  function parry(choice: ParryChoice, event: MouseEvent) {
    if (activeInterrupt < 0 || answered.has(activeInterrupt)) return;
    const interrupt = schedule.Interrupts.find((i) => i.Index === activeInterrupt);
    if (!interrupt) return;
    const choiceMs = event.timeStamp - t0;
    answered.add(interrupt.Index);
    parries.push({ Interrupt: interrupt.Index, Choice: choice, ChoiceMs: choiceMs });
    parryOpen = false;
    if (isRead(interrupt, choice, choiceMs)) {
      readInterrupts.add(interrupt.Index);
      counterOpen = spearsLeft > 0;
    } else if (spearsLeft > 0) {
      lost += 1;
    }
  }

  function counterStrike(plate: number, event: MouseEvent) {
    if (!counterOpen || activeInterrupt < 0 || spearsLeft <= 0) return;
    const interrupt = activeInterrupt;
    const tapMs = event.timeStamp - t0;
    if (tapMs > challenge.MaxPlayMs) return;
    counterUsed.add(interrupt);
    counterOpen = false;
    const seq = nextSeq++;
    const entry: CounterEntryDto = { Interrupt: interrupt, Seq: seq, TapMs: tapMs, Plate: plate };
    counters.push(entry);
    spears = [...spears, { seq, plate, cls: 'Seam', weak: false, counter: true }];
    vibrate(30);
    void send(seq, tapMs, entry);
  }

  async function finish() {
    if (finished) return;
    finished = true;
    cancelAnimationFrame(raf);
    clearInterval(countdownTimer);
    phase = 'submitting';
    try {
      const scored = await scoreBossPractice({
        ChallengeId: challenge.ChallengeId,
        Taps: taps,
        Parries: parries,
        Counters: counters,
      });
      if (!scored || scored.Result !== 'PracticeScored') {
        errorText = 'That practice run could not be scored. Start a new one.';
        phase = 'error';
        return;
      }
      result = scored;
      phase = 'result';
    } catch (err) {
      errorText = err instanceof Error ? err.message : 'the request failed';
      phase = 'error';
    }
  }

  // Leaving the app submits what was thrown: unthrown spears count as None.
  function onVisibility() {
    if (document.visibilityState === 'hidden' && (phase === 'play' || phase === 'countdown')) void finish();
  }
  document.addEventListener('visibilitychange', onVisibility);

  onDestroy(() => {
    cancelAnimationFrame(raf);
    clearInterval(countdownTimer);
    document.removeEventListener('visibilitychange', onVisibility);
  });

  const tell = $derived(activeInterrupt >= 0 ? schedule.Interrupts.find((i) => i.Index === activeInterrupt)?.Tell ?? null : null);

  // The ring, drawn once: a plate sector per 72 degrees, with its seam band and
  // its two rivet bands, at SVG angle 90 + local angle (the impact point is the
  // bottom of the ring, and the whole group is rotated by -angleAt(t)).
  const R_OUT = 100;
  const R_IN = 62;
  function arc(fromDeg: number, toDeg: number, rOut = R_OUT, rIn = R_IN): string {
    const rad = (d: number) => ((90 + d) * Math.PI) / 180;
    const p = (r: number, d: number) => `${(r * Math.cos(rad(d))).toFixed(2)} ${(r * Math.sin(rad(d))).toFixed(2)}`;
    const large = toDeg - fromDeg > 180 ? 1 : 0;
    return `M ${p(rOut, fromDeg)} A ${rOut} ${rOut} 0 ${large} 1 ${p(rOut, toDeg)} L ${p(rIn, toDeg)} A ${rIn} ${rIn} 0 ${large} 0 ${p(rIn, fromDeg)} Z`;
  }
  function labelAt(plate: number): { x: number; y: number } {
    const rad = ((90 + plate * PLATE_DEGREES + PLATE_DEGREES / 2) * Math.PI) / 180;
    const r = (R_OUT + R_IN) / 2;
    return { x: r * Math.cos(rad), y: r * Math.sin(rad) };
  }
  const plates = Array.from({ length: PLATE_COUNT }, (_, i) => i);
  const seamHalf = SEAM_DEGREES / 2;

  const scheduleJson = JSON.stringify(challenge);
</script>

<div
  class="overlay"
  role="dialog"
  aria-modal="true"
  aria-label="Shield wheel practice"
  data-schedule={scheduleJson}
  data-t0={t0Attr > 0 ? t0Attr : undefined}
  data-phase={phase}
>
  <div class="top">
    <span class="mode">Practice - no damage, no attempt spent</span>
    {#if challenge.Enraged}
      <span class="enraged"><span aria-hidden="true">&#x2620;</span> The boss is enraged</span>
    {/if}
  </div>

  {#if phase === 'countdown'}
    <p class="big" aria-live="assertive">{countdownLeft}</p>
    <p class="hint">Tap the lower half of the screen to throw a spear. It lands on whatever part of the ring is at the bottom a moment later. The seam in the middle of each plate is the best hit.</p>
  {/if}

  {#if phase === 'play' || phase === 'countdown'}
    <div class="stage">
      <svg class="wheel" viewBox="-110 -110 220 220" aria-hidden="true">
        <g bind:this={ring} class="ring">
          {#each plates as plate (plate)}
            {@const from = plate * PLATE_DEGREES}
            <path d={arc(from, from + PLATE_DEGREES)} class="plate" class:weak={weakPlates.has(plate)} />
            <path d={arc(from + PLATE_DEGREES / 2 - seamHalf, from + PLATE_DEGREES / 2 + seamHalf)} class="seam" />
            <path d={arc(from, from + RIVET_DEGREES)} class="rivet" />
            <path d={arc(from + PLATE_DEGREES - RIVET_DEGREES, from + PLATE_DEGREES)} class="rivet" />
            <text x={labelAt(plate).x} y={labelAt(plate).y} class="label">{plate + 1}</text>
          {/each}
        </g>
        <path d="M -7 104 L 7 104 L 0 92 Z" class="impact" />
      </svg>
    </div>

    <div class="status">
      <span>{secondsLeft}s</span>
      <span aria-label="{spearsLeft} spears left">Spears: {spearsLeft}</span>
    </div>

    <div class="landed" aria-live="polite">
      {#each spears as spear (spear.seq)}
        <span class="chip" data-class={spear.cls} class:weakhit={spear.weak}>
          {spear.plate >= 0 ? spear.plate + 1 : '-'}
          {spear.cls}{spear.weak ? ' - weak!' : ''}
        </span>
      {/each}
    </div>

    <div class="controls">
      {#if parryOpen}
        <!-- D: the tell sits right above the buttons, so reading and pressing
             happen in one place. E: the bar shrinks over the response window,
             OUTSIDE every button. C: each button is named by the blow. -->
        {#if tell}
          <div class="tell" data-tell={tell} aria-live="assertive">
            <span class="tell-glyph" aria-hidden="true">{tell === 'Left' ? '◀' : tell === 'Right' ? '▶' : '▼'}</span>
            <span>The blow comes {tell === 'Left' ? 'from the left' : tell === 'Right' ? 'from the right' : 'from above'}!</span>
          </div>
        {/if}
        <div class="timebar" aria-hidden="true"><span style="animation-duration: {parryRemainingMs}ms"></span></div>
        <div class="row parry-row" role="group" aria-label="Where does the blow come from?">
          <button type="button" class="ctl side" onclick={(e) => parry(ANSWER_FOR.Left, e)}>&#x25C0; From the left</button>
          <button type="button" class="ctl side" onclick={(e) => parry(ANSWER_FOR.Overhead, e)}>&#x25BC; From above</button>
          <button type="button" class="ctl side" onclick={(e) => parry(ANSWER_FOR.Right, e)}>From the right &#x25B6;</button>
        </div>
      {:else if counterOpen}
        <p class="hint">Read it! Pick a plate for a sure seam hit.</p>
        <div class="row" role="group" aria-label="Counter on a plate">
          {#each plates as plate (plate)}
            <button type="button" class="ctl plate-btn" onclick={(e) => counterStrike(plate, e)}>{plate + 1}</button>
          {/each}
        </div>
      {:else if phase === 'play'}
        <button
          type="button"
          class="throw-zone"
          aria-label="Throw a spear"
          disabled={spearsLeft <= 0 || activeInterrupt >= 0}
          onpointerdown={throwAtWheel}
        >
          {activeInterrupt >= 0 ? 'The wheel has stopped' : 'Tap to throw'}
        </button>
      {/if}
    </div>
  {/if}

  {#if phase === 'submitting'}
    <p class="big small-big">Scoring...</p>
  {/if}

  {#if phase === 'result' && result}
    <div class="card" data-testid="practice-card">
      <h3>Practice result</h3>
      <ul class="spears">
        {#each result.Landings as landing (landing.Seq)}
          <li>
            Spear {landing.Seq + 1}: plate {landing.Plate + 1}, {landing.Class}{landing.IsCounter ? ' (counter)' : ''}{landing.WeakHit ? ' - weak plate!' : ''}
          </li>
        {/each}
        {#if result.SpearsLost > 0}
          <li class="lost">{result.SpearsLost} spear{result.SpearsLost === 1 ? '' : 's'} lost to missed reads</li>
        {/if}
      </ul>
      <dl class="numbers">
        <div><dt>Skill</dt><dd data-testid="practice-m">{result.Multiplier.toFixed(2)}x</dd></div>
        <div><dt>Plates</dt><dd>{result.PlateMultiplier.toFixed(2)}x</dd></div>
        <div><dt>Strike</dt><dd>{result.Played.toFixed(2)}x</dd></div>
      </dl>
      <p class="hint">No damage dealt: this was practice. In the real fight a weak plate hit is shown only to you.</p>
      <div class="row">
        <button type="button" class="ctl" onclick={onagain}>Practice again</button>
        <button type="button" class="ctl" onclick={onclose}>Close</button>
      </div>
    </div>
  {/if}

  {#if phase === 'error'}
    <div class="card">
      <p class="hint">{errorText}</p>
      <div class="row">
        <button type="button" class="ctl" onclick={onclose}>Close</button>
      </div>
    </div>
  {/if}
</div>

<style>
  .overlay {
    position: fixed;
    inset: 0;
    z-index: 60;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.6rem;
    background: var(--bg, #111);
    color: var(--text, #eee);
    padding: calc(0.8rem + var(--safe-area-inset-top, env(safe-area-inset-top, 0px)))
      calc(0.8rem + var(--safe-area-inset-right, env(safe-area-inset-right, 0px)))
      calc(0.8rem + var(--safe-area-inset-bottom, env(safe-area-inset-bottom, 0px)))
      calc(0.8rem + var(--safe-area-inset-left, env(safe-area-inset-left, 0px)));
    overflow-y: auto;
    touch-action: manipulation;
  }

  .top {
    display: flex;
    flex-wrap: wrap;
    gap: 0.6rem;
    justify-content: center;
    font-size: 0.8rem;
  }

  .mode {
    color: var(--text-dim);
  }

  .enraged {
    color: var(--danger);
    font-weight: 700;
    border: 1px dashed var(--danger);
    border-radius: 999px;
    padding: 0 0.5rem;
  }

  .big {
    font-size: 3rem;
    font-weight: 800;
    margin: 0;
  }

  .small-big {
    font-size: 1.4rem;
  }

  .hint {
    font-size: 0.8rem;
    color: var(--text-dim);
    text-align: center;
    max-width: 26rem;
    margin: 0;
  }

  .stage {
    position: relative;
    width: min(78vw, 22rem);
  }

  .wheel {
    width: 100%;
    height: auto;
    display: block;
  }

  .ring {
    transform-origin: 0 0;
  }

  .plate {
    fill: var(--bg-panel, #222);
    stroke: var(--border, #555);
    stroke-width: 1;
  }

  .plate.weak {
    fill: color-mix(in srgb, var(--good, #4c4) 40%, var(--bg-panel, #222));
  }

  .seam {
    fill: color-mix(in srgb, var(--accent, #d9c48b) 55%, transparent);
  }

  .rivet {
    fill: color-mix(in srgb, var(--text-dim, #999) 45%, transparent);
  }

  .label {
    fill: var(--text, #eee);
    font-size: 13px;
    font-weight: 700;
    text-anchor: middle;
    dominant-baseline: middle;
  }

  .impact {
    fill: var(--danger, #e55);
  }

  .tell {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
    font-weight: 800;
    font-size: 1.05rem;
    padding: 0.35rem 0.6rem;
    border: 2px solid var(--danger, #e55);
    border-radius: 8px;
    text-align: center;
  }

  /* The side the blow comes from is also WHERE the tell leans, so it is
     readable by position and shape, never by colour alone. */
  .tell[data-tell='Left'] {
    justify-content: flex-start;
  }

  .tell[data-tell='Right'] {
    justify-content: flex-end;
  }

  .timebar {
    height: 6px;
    border-radius: 999px;
    background: color-mix(in srgb, var(--border, #555) 60%, transparent);
    overflow: hidden;
  }

  .timebar span {
    display: block;
    height: 100%;
    background: var(--danger, #e55);
    transform-origin: left center;
    animation-name: shrink;
    animation-timing-function: linear;
    animation-fill-mode: forwards;
  }

  @keyframes shrink {
    from {
      transform: scaleX(1);
    }
    to {
      transform: scaleX(0);
    }
  }

  /* One row, always: left, above, right - the button for a blow from the right
     has to BE on the right, or naming them by the blow buys nothing. The
     .ctl.side specificity is what beats .ctl's own 44px further down. */
  .parry-row {
    flex-wrap: nowrap;
  }

  .ctl.side {
    flex: 1 1 0;
    min-width: 44px;
    min-height: 60px;
    padding: 0.3rem 0.35rem;
    font-size: 0.9rem;
    line-height: 1.15;
    white-space: normal;
  }
  .tell-glyph {
    font-size: 1.4rem;
  }

  .status {
    display: flex;
    gap: 1.2rem;
    font-variant-numeric: tabular-nums;
    font-weight: 700;
  }

  .landed {
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
    justify-content: center;
    min-height: 1.6rem;
  }

  .chip {
    font-size: 0.72rem;
    border: 1px solid var(--border);
    border-radius: 999px;
    padding: 0.05rem 0.45rem;
  }

  .chip[data-class='Seam'] {
    border-color: var(--accent);
    color: var(--accent);
  }

  .chip.weakhit {
    border-color: var(--good);
    color: var(--good);
    font-weight: 700;
  }

  .controls {
    width: 100%;
    max-width: 26rem;
    margin-top: auto;
    display: grid;
    gap: 0.5rem;
  }

  .row {
    display: flex;
    gap: 0.5rem;
    justify-content: center;
    flex-wrap: wrap;
  }

  .ctl {
    min-height: 44px;
    min-width: 44px;
    flex: 1 0 auto;
    flex-shrink: 0;
    padding: 0.4rem 0.8rem;
    font-weight: 700;
  }

  .plate-btn {
    flex: 1 0 44px;
  }

  .throw-zone {
    width: 100%;
    min-height: 34vh;
    font-weight: 700;
    border: 2px dashed var(--border);
    border-radius: var(--radius, 8px);
    background: color-mix(in srgb, var(--bg-panel, #222) 60%, transparent);
    touch-action: manipulation;
    user-select: none;
  }

  .card {
    width: 100%;
    max-width: 26rem;
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius, 8px);
    padding: 0.9rem;
    display: grid;
    gap: 0.6rem;
  }

  .card h3 {
    margin: 0;
  }

  .spears {
    margin: 0;
    padding-left: 1.1rem;
    font-size: 0.85rem;
  }

  .lost {
    color: var(--danger);
  }

  .numbers {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 0.5rem;
    margin: 0;
  }

  .numbers div {
    display: grid;
    gap: 0.1rem;
  }

  dt {
    font-size: 0.7rem;
    color: var(--text-dim);
  }

  dd {
    margin: 0;
    font-weight: 800;
    font-variant-numeric: tabular-nums;
  }

  /*
    Modul: A PHONE ON ITS SIDE IS ~390 PX TALL, and the stacked layout does not
    fit: the ring, the status, the spears and a thumb-sized throw zone came to
    well over the screen, so the ring ran off the bottom under the gesture bar -
    and the overlay is fixed, so scrolling the page could never bring it back
    (check:safearea, landscape). Side by side instead: the ring on the left,
    sized to the HEIGHT that is actually left, and everything to press on the
    right, under the thumb.
  */
  @media (orientation: landscape) and (max-height: 540px) {
    .overlay {
      display: grid;
      grid-template-columns: auto minmax(0, 1fr);
      grid-auto-rows: min-content;
      align-content: center;
      column-gap: 1rem;
    }

    .top,
    .card {
      grid-column: 1 / -1;
    }

    .stage {
      grid-column: 1;
      grid-row: 2 / span 5;
      align-self: center;
      width: min(
        45vw,
        calc(
          100vh - 6rem - var(--safe-area-inset-top, env(safe-area-inset-top, 0px)) -
            var(--safe-area-inset-bottom, env(safe-area-inset-bottom, 0px))
        )
      );
    }

    .big,
    .hint,
    .status,
    .landed,
    .controls {
      grid-column: 2;
    }

    .controls {
      margin-top: 0;
    }

    .throw-zone {
      min-height: 38vh;
    }
  }
</style>
