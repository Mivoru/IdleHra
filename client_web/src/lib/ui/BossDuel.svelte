<script lang="ts">
  // Modul: THE DUEL THAT REPLACED THE SHIELD WHEEL (owner, 2026-11-14: "the
  // wheel is nothing much"). Four independent model evaluators picked the same
  // design - docs/design/WORLD_BOSS_REWORK_2026_11_14.md: your own character
  // fights Chernobog, and three times he winds up a named move and you call
  // the answer. Decisions, not reflexes; your build changes the right answer;
  // the day's omen changes one more.
  //
  // Modul: THIS FILE ANIMATES, IT NEVER COMPUTES. Every number on screen came
  // from the server: the blows (they sum to the base hit), the hit a call
  // landed, and the strike's damage. The running total is a sum of those, and
  // the result card shows the server's figure - the evaluators' one shared
  // risk was a fight replayed on two machines that drifts apart.
  import { onDestroy } from 'svelte';
  import { answerDuelCall, type DuelChallenge, type DuelCall, type DuelOption, type DuelStrike } from '../net/rest';
  import { formatNumber, numberTitle } from './format';
  import { play } from './audio';
  import { tap } from '../net/haptics';
  import { prefersReducedMotion } from 'svelte/motion';
  import { worldBossResultSentence } from '../game/worldBossResults';
  import { portal } from './portal';
  import { openModal } from './modalStack';
  import { registerOverlay } from '../stores/sheet';
  import { LAYER_Z } from '../net/backButton';

  interface Props {
    challenge: DuelChallenge;
    bossArt: string;
    serpentArt: string;
    bossName: string;
    heroArt: string | null;
    onclose: () => void;
    onagain?: () => void;
    onleave?: () => void;
  }
  let { challenge, bossArt, serpentArt, bossName, heroArt, onclose, onagain, onleave }: Props = $props();

  type Phase = 'intro' | 'fight' | 'call' | 'answered' | 'sending' | 'result';
  const SERPENT_MOVES = new Set(['VenomSpit', 'TailSweep', 'DeathCoil']);
  let phase = $state<Phase>('intro');
  let calls = $state<DuelCall[]>(challenge.Calls.map((c) => ({ ...c })));
  let round = $state(0);
  let total = $state(0);
  let strike = $state<DuelStrike | null>(null);
  let error = $state('');
  let speed = $state(1);
  let callLeftMs = $state(0);
  let bossHit = $state(false);
  let heroLunge = $state(false);
  let shake = $state(false);
  let windup = $state(false);

  interface Floater { id: number; text: string; big: boolean; tone: 'hit' | 'call' | 'finisher'; x: number }
  let floaters = $state<Floater[]>([]);
  let floaterId = 0;

  const art = $derived(challenge.Enraged ? serpentArt : bossArt);
  const foe = $derived(challenge.Enraged ? 'the Black Serpent' : bossName);
  const current = $derived(phase === 'call' || phase === 'sending' || phase === 'answered' ? (calls[round - 1] ?? null) : null);
  const stanceText: Record<string, string> = {
    Balanced: 'a balanced build',
    Might: 'a Might build',
    Precision: 'a Precision build',
    Guard: 'a Guard build',
  };

  const OPTIONS: { option: DuelOption; label: string; glyph: string; hint: string }[] = [
    { option: 'Guard', label: 'Guard', glyph: '⛨', hint: 'raise your shield' },
    { option: 'Strike', label: 'Strike', glyph: '⚔', hint: 'hit him first' },
    { option: 'Evade', label: 'Evade', glyph: '↷', hint: 'step aside' },
  ];

  // Modul: A FULL-SCREEN STAGE, LIKE ChestOpening (task 106's rule): Modal's
  // own parts - the portal, openModal (inert root, focus trap and return) and
  // the overlay stack for Escape/back - without putting the arena in a card.
  let stageEl = $state<HTMLElement | null>(null);
  let arenaEl = $state<HTMLElement | null>(null);
  $effect(() => {
    if (!stageEl || !arenaEl) return;
    return openModal(arenaEl, stageEl);
  });
  // Back/Escape leaves a running duel (the server keeps it) and closes a finished one.
  $effect(() => registerOverlay(() => (phase === 'result' || !onleave ? onclose() : onleave()), LAYER_Z.modal));

  let timers: ReturnType<typeof setTimeout>[] = [];
  let ticker: ReturnType<typeof setInterval> | undefined;
  let destroyed = false;
  onDestroy(() => {
    destroyed = true;
    timers.forEach(clearTimeout);
    clearInterval(ticker);
  });

  const wait = (ms: number) =>
    new Promise<void>((resolve) => {
      timers.push(setTimeout(resolve, ms / speed));
    });

  function pulse(set: (v: boolean) => void, ms: number) {
    set(true);
    timers.push(setTimeout(() => set(false), ms));
  }

  function float(text: string, tone: Floater['tone'], big = false) {
    const id = ++floaterId;
    floaters = [...floaters, { id, text, big, tone, x: 30 + Math.round(Math.random() * 40) }];
    timers.push(setTimeout(() => (floaters = floaters.filter((f) => f.id !== id)), 1100));
  }

  async function blow(amount: number, heavy: boolean) {
    pulse((v) => (heroLunge = v), 220);
    await wait(120);
    pulse((v) => (bossHit = v), 200);
    if (heavy && !prefersReducedMotion.current) pulse((v) => (shake = v), 260);
    play(heavy ? 'duelHeavy' : 'duelHit');
    total += amount;
    float(formatNumber(amount), heavy ? 'call' : 'hit', heavy);
  }

  async function playRound(r: number) {
    for (const amount of challenge.Rounds[r] ?? []) {
      if (destroyed) return;
      await blow(amount, false);
      await wait(560);
    }
  }

  // A resumed duel starts at the first unanswered call: the rounds and the
  // answered calls before it are already struck, so they are summed, not replayed.
  function resumeFrom(): number {
    const answered = calls.filter((c) => c.Answer !== null).length;
    for (let r = 0; r < answered; r++) total += (challenge.Rounds[r] ?? []).reduce((a, b) => a + b, 0);
    for (const c of calls) if (c.Hit) total += c.Hit;
    return answered;
  }

  async function begin() {
    phase = 'fight';
    const done = resumeFrom();
    for (let r = done; r < calls.length; r++) {
      round = r;
      await playRound(r);
      if (destroyed) return;
      round = r + 1;
      await openCall();
      if (destroyed || (phase as Phase) === 'result') return;
    }
  }

  function openCall(): Promise<void> {
    return new Promise((resolve) => {
      phase = 'call';
      play(challenge.Enraged || SERPENT_MOVES.has(calls[round - 1]?.Move ?? '') ? 'worldBossHiss' : 'worldBossRoar');
      pulse((v) => (windup = v), 1400);
      callLeftMs = challenge.CallWindowMs;
      const started = Date.now();
      clearInterval(ticker);
      ticker = setInterval(() => {
        callLeftMs = Math.max(0, challenge.CallWindowMs - (Date.now() - started));
        if (callLeftMs === 0 && phase === 'call') void answer('None');
      }, 100);
      pendingResolve = resolve;
    });
  }

  let pendingResolve: (() => void) | null = null;

  async function answer(option: DuelOption) {
    if (phase !== 'call') return;
    const call = calls[round - 1];
    if (!call) return;
    clearInterval(ticker);
    phase = 'sending';
    let response;
    try {
      response = await answerDuelCall(challenge.ChallengeId, call.Index, option);
    } catch (err) {
      error = err instanceof Error ? err.message : 'The answer could not be sent.';
    }
    if (destroyed) return;
    if (!response || !response.Call) {
      error = error || (response ? worldBossResultSentence(response.Result) : '') || 'The answer could not be sent.';
      phase = 'result';
      pendingResolve?.();
      return;
    }
    calls[round - 1] = response.Call;
    phase = 'answered';
    const verdict = response.Call.Verdict;
    if (verdict === 'Right') {
      play('dungeonDodged');
      tap('heavy');
    } else if (verdict === 'Wrong') {
      play('dungeonMissed');
    }
    await wait(350);
    if (response.Call.Hit && response.Call.Hit > 0) await blow(response.Call.Hit, true);
    await wait(900);

    if (response.Strike) {
      // The last call: round 4, then the finisher, then the server's figure.
      await playRound(calls.length);
      if (calls.every((c) => c.Verdict === 'Right') && challenge.FinisherHit > 0) {
        await wait(300);
        float("Svarog's Hammer!", 'finisher', true);
        await blow(challenge.FinisherHit, true);
        play('worldBossHammer');
        await wait(700);
      }
      strike = response.Strike;
      phase = 'result';
    } else {
      phase = 'fight';
    }
    pendingResolve?.();
  }

  const rightCount = $derived(calls.filter((c) => c.Verdict === 'Right').length);
  const verdictWord: Record<string, string> = { Right: 'Right call', Fair: 'It held', Wrong: 'Wrong call' };
</script>

<div class="duel-overlay" style:z-index={LAYER_Z.modal} bind:this={stageEl} use:portal data-testid="boss-duel">
  <div
    class="arena"
    class:shake
    class:serpent={challenge.Enraged}
    role="dialog"
    aria-modal="true"
    aria-label="Duel with {foe}"
    tabindex="-1"
    bind:this={arenaEl}
  >
    <div class="topbar">
      <span class="dim tiny">{challenge.Practice ? 'Practice duel' : "Today's strike"}</span>
      <span class="total" title={numberTitle(total)} data-testid="duel-total">{formatNumber(total)}</span>
      <button
        class="speed tiny-btn"
        type="button"
        aria-pressed={speed === 2}
        onclick={() => (speed = speed === 1 ? 2 : 1)}
        data-testid="duel-speed">{speed}x</button
      >
    </div>

    <div class="stage">
      <img class="foe" class:hit={bossHit} class:windup src={art} alt={foe} decoding="async" />
      {#each floaters as f (f.id)}
        <span class="floater {f.tone}" class:big={f.big} style="left: {f.x}%" aria-hidden="true">{f.text}</span>
      {/each}
      {#if heroArt}
        <img class="hero" class:lunge={heroLunge} src={heroArt} alt="Your character" decoding="async" />
      {/if}
    </div>

    <div class="panel-area">
      {#if phase === 'intro'}
        <div class="intro" data-testid="duel-intro">
          <p class="omen"><strong>Omen of the day: {challenge.Omen.Name}.</strong> {challenge.Omen.Text}</p>
          <p class="small">
            You fight with {stanceText[challenge.Stance] ?? 'your build'}. Three times {foe} will wind up a move - read it and
            call your answer. Your build changes which answers are right.
          </p>
          <button class="primary big-btn" type="button" onclick={begin} data-testid="duel-begin">Fight</button>
          {#if onleave}
            <button class="link-btn tiny" type="button" onclick={onleave}>Not now</button>
          {/if}
        </div>
      {:else if (phase === 'call' || phase === 'sending') && current}
        <div class="call" data-testid="duel-call" data-move={current.Move}>
          <p class="move-name"><span class="glyph" aria-hidden="true">!</span> {current.Name}</p>
          <p class="tell small">{current.Tell}</p>
          {#if challenge.Omen.Move === current.Move}
            <p class="tiny omen-hint">The omen speaks of this move.</p>
          {/if}
          <div class="options">
            {#each OPTIONS as o (o.option)}
              <button
                type="button"
                class="option"
                disabled={phase !== 'call'}
                onclick={() => answer(o.option)}
                data-testid="duel-option-{o.option}"
              >
                <span class="opt-glyph" aria-hidden="true">{o.glyph}</span>
                <span class="opt-label">{o.label}</span>
                <span class="opt-hint tiny">{o.hint}</span>
              </button>
            {/each}
          </div>
          <div class="timer" role="progressbar" aria-label="Time to answer" aria-valuenow={callLeftMs} aria-valuemin="0" aria-valuemax={challenge.CallWindowMs}>
            <div class="timer-fill" style="width: {(callLeftMs / challenge.CallWindowMs) * 100}%"></div>
          </div>
        </div>
      {:else if phase === 'answered' && current}
        <div class="verdict {current.Verdict?.toLowerCase()}" role="status" data-testid="duel-verdict" data-verdict={current.Verdict}>
          <p class="verdict-word">{verdictWord[current.Verdict ?? 'Wrong']}</p>
          <p class="small">{current.Explain}</p>
        </div>
      {:else if phase === 'fight'}
        <div class="pips" aria-hidden="true">
          {#each calls as c (c.Index)}
            <span class="pip" class:done={c.Answer !== null}></span>
          {/each}
        </div>
        <p class="dim small fight-note">
          {calls.every((c) => c.Answer !== null) ? 'The last exchange.' : 'Your character fights on - he will wind up again soon.'}
        </p>
      {:else if phase === 'result'}
        <div class="result" data-testid="duel-result" data-damage={strike?.Damage ?? 0}>
          {#if error}
            <p class="error small" role="alert">{error}</p>
          {:else if strike}
            {#if strike.NoDamageDealt}
              <p class="small">Practice - no damage dealt, no strike spent.</p>
              <p class="big-num">{strike.Multiplier.toFixed(2)}x</p>
            {:else if strike.Result === 'Landed'}
              <p class="big-num" title={numberTitle(strike.Damage)}>{formatNumber(strike.Damage)}</p>
              <p class="small">damage to {foe} ({strike.Multiplier.toFixed(2)}x)</p>
            {:else}
              <p class="small">{worldBossResultSentence(strike.Result, strike.Damage)}</p>
            {/if}
            <p class="tiny dim">{rightCount} of {calls.length} calls right.</p>
            <ul class="recap">
              {#each calls as c (c.Index)}
                <li class={c.Verdict?.toLowerCase()}>
                  <strong>{verdictWord[c.Verdict ?? 'Wrong']}</strong> - {c.Explain}
                </li>
              {/each}
            </ul>
          {/if}
          <div class="result-actions">
            {#if strike?.NoDamageDealt && onagain}
              <button class="primary" type="button" onclick={onagain}>Practice again</button>
            {/if}
            <button type="button" onclick={onclose} data-testid="duel-close">Close</button>
          </div>
        </div>
      {/if}
    </div>
    {#if phase !== 'result' && phase !== 'intro' && onleave}
      <button class="leave link-btn tiny" type="button" onclick={onleave}>Leave - finish later</button>
    {/if}
  </div>
</div>

<style>
  .duel-overlay {
    position: fixed;
    inset: 0;
    background: rgba(5, 4, 8, 0.86);
    display: grid;
    place-items: center;
    padding: max(12px, env(safe-area-inset-top)) 16px max(12px, env(safe-area-inset-bottom));
  }
  .arena {
    position: relative;
    width: min(100%, 30rem);
    max-height: 100%;
    overflow-y: auto;
    border-radius: 14px;
    background:
      radial-gradient(120% 70% at 50% 20%, rgba(120, 30, 20, 0.35), transparent 70%),
      linear-gradient(180deg, #1b1216, #0d0a0c);
    border: 1px solid rgba(255, 140, 80, 0.25);
    box-shadow: 0 10px 40px rgba(0, 0, 0, 0.6);
    padding: 12px;
    color: #f3e9dc;
  }
  .arena.serpent {
    background:
      radial-gradient(120% 70% at 50% 20%, rgba(30, 110, 60, 0.35), transparent 70%),
      linear-gradient(180deg, #0f1a14, #090c0a);
    border-color: rgba(120, 220, 140, 0.25);
  }
  .arena.shake {
    animation: shake 260ms ease-out 1;
  }
  @keyframes shake {
    0%, 100% { transform: translate(0, 0); }
    25% { transform: translate(-5px, 2px); }
    50% { transform: translate(4px, -2px); }
    75% { transform: translate(-2px, 1px); }
  }
  .topbar {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
  }
  .total {
    font-size: 1.4rem;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
    color: #ffd38a;
  }
  /* Modul: the overlay's own buttons. The global button is a cream panel
     with dark text, and this overlay sets light text - the two together made
     Close and 2x cream-on-cream (first screenshot run, 2026-11-15). */
  .arena button:not(.primary):not(.option):not(.link-btn) {
    background: rgba(255, 255, 255, 0.08);
    color: #f3e9dc;
    border: 1px solid rgba(255, 210, 140, 0.45);
    border-radius: 8px;
    box-shadow: none;
  }
  .arena button.link-btn {
    background: none;
    border: none;
    box-shadow: none;
    color: #f3e9dc;
  }
  .speed {
    min-width: 44px;
    min-height: 44px;
    font-weight: 700;
  }
  .pips {
    display: flex;
    justify-content: center;
    gap: 6px;
    margin-top: 1.5rem;
  }
  .pip {
    width: 10px;
    height: 10px;
    border-radius: 50%;
    border: 1px solid rgba(255, 210, 140, 0.6);
  }
  .pip.done {
    background: #ffb347;
  }
  .stage {
    position: relative;
    height: 15rem;
    margin: 6px 0;
    overflow: hidden;
  }
  .foe {
    position: absolute;
    left: 50%;
    top: 0;
    height: 12rem;
    width: auto;
    max-width: 80%;
    object-fit: contain;
    transform: translateX(-50%);
    filter: drop-shadow(0 6px 18px rgba(0, 0, 0, 0.7));
    transition: filter 120ms;
  }
  .foe.hit {
    animation: flash 200ms ease-out 1;
  }
  @keyframes flash {
    0% { filter: brightness(2.2) drop-shadow(0 0 12px #fff); }
    100% { filter: brightness(1) drop-shadow(0 6px 18px rgba(0, 0, 0, 0.7)); }
  }
  .foe.windup {
    animation: windup 1400ms ease-in-out 1;
  }
  @keyframes windup {
    0% { transform: translateX(-50%) scale(1); }
    40% { transform: translateX(-50%) scale(1.07); filter: drop-shadow(0 0 22px rgba(255, 80, 40, 0.9)); }
    100% { transform: translateX(-50%) scale(1); }
  }
  .hero {
    position: absolute;
    left: 6%;
    bottom: 0;
    height: 6.5rem;
    width: auto;
    border-radius: 10px;
    border: 2px solid rgba(255, 210, 140, 0.5);
    background: #20181a;
  }
  .hero.lunge {
    animation: lunge 220ms ease-out 1;
  }
  @keyframes lunge {
    50% { transform: translate(28px, -18px) rotate(4deg); }
  }
  .floater {
    position: absolute;
    top: 38%;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
    text-shadow: 0 2px 4px #000;
    pointer-events: none;
    animation: rise 1100ms ease-out 1 forwards;
  }
  .floater.hit { color: #ffe8c2; font-size: 1rem; }
  .floater.call { color: #ffb347; font-size: 1.5rem; }
  .floater.finisher { color: #ffd700; font-size: 1.6rem; top: 20%; }
  .floater.big { letter-spacing: 0.02em; }
  @keyframes rise {
    0% { transform: translateY(0); opacity: 1; }
    100% { transform: translateY(-60px); opacity: 0; }
  }
  .panel-area {
    min-height: 11rem;
  }
  .omen {
    border-left: 3px solid #ffb347;
    padding-left: 8px;
    margin: 0 0 8px;
  }
  .big-btn {
    width: 100%;
    min-height: 48px;
    font-size: 1.1rem;
    margin-top: 8px;
  }
  .link-btn {
    background: none;
    border: none;
    color: inherit;
    text-decoration: underline;
    min-height: 44px;
    opacity: 0.8;
  }
  .leave {
    display: block;
    margin: 6px auto 0;
  }
  .move-name {
    font-size: 1.25rem;
    font-weight: 700;
    margin: 0;
    color: #ff9a6a;
  }
  .glyph {
    display: inline-grid;
    place-items: center;
    width: 1.5rem;
    height: 1.5rem;
    border-radius: 50%;
    background: #ff6a3d;
    color: #1b1216;
    font-size: 1rem;
  }
  .tell {
    margin: 4px 0 6px;
  }
  .omen-hint {
    color: #ffd38a;
    margin: 0 0 6px;
  }
  .options {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 8px;
  }
  .option {
    display: grid;
    justify-items: center;
    gap: 2px;
    min-height: 76px;
    padding: 8px 4px;
    border-radius: 10px;
    border: 1px solid rgba(255, 210, 140, 0.4);
    background: rgba(255, 255, 255, 0.06);
    color: inherit;
  }
  .option:disabled {
    opacity: 0.55;
  }
  .opt-glyph {
    font-size: 1.5rem;
  }
  .opt-label {
    font-weight: 700;
  }
  .opt-hint {
    opacity: 0.75;
  }
  .timer {
    height: 6px;
    border-radius: 3px;
    background: rgba(255, 255, 255, 0.1);
    margin-top: 10px;
    overflow: hidden;
  }
  .timer-fill {
    height: 100%;
    background: #ffb347;
  }
  .verdict {
    padding: 10px;
    border-radius: 10px;
    border: 2px solid;
  }
  .verdict.right { border-color: #6fd38a; }
  .verdict.fair { border-color: #e2c26a; border-style: dashed; }
  .verdict.wrong { border-color: #e2645a; border-style: dotted; }
  .verdict-word {
    font-size: 1.2rem;
    font-weight: 700;
    margin: 0 0 4px;
  }
  .fight-note {
    text-align: center;
    margin-top: 0.75rem;
  }
  .result {
    text-align: center;
  }
  .big-num {
    font-size: 2.2rem;
    font-weight: 800;
    color: #ffd38a;
    margin: 0;
    font-variant-numeric: tabular-nums;
  }
  .recap {
    text-align: left;
    padding-left: 1.1rem;
    margin: 8px 0;
  }
  .recap li { margin: 2px 0; }
  .recap li.right strong { color: #6fd38a; }
  .recap li.fair strong { color: #e2c26a; }
  .recap li.wrong strong { color: #e2645a; }
  .result-actions {
    display: flex;
    gap: 8px;
    justify-content: center;
    flex-wrap: wrap;
  }
  .result-actions button {
    min-height: 44px;
    min-width: 7rem;
  }
  @media (prefers-reduced-motion: reduce) {
    .arena.shake, .foe.hit, .foe.windup, .hero.lunge, .floater {
      animation: none;
    }
    .floater { opacity: 1; }
  }
</style>
