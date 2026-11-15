<script lang="ts">
  import { formatNumber } from './format';
  import { damageEvents, typicalHit } from '../stores/game';
  import { DAMAGE_TEXT_LIFETIME_MS } from '../stores/damage';
  import { calloutFor } from './skillProcs';

  // Modul: CRITS ARE YELLOW, ordinary hits are red.
  //
  // This comment used to say the opposite - that the wire carried no crit flag,
  // so the client could only honestly say "that one was larger than usual" and
  // must never call anything a CRIT. That was true and is not any more: the
  // server now sends LastHitWasCrit, set at the one place that rolls it. A crit
  // is a stat players spend skill points and affixes on, and until now it was
  // completely invisible.
  //
  // The size ramp below stays, and stays independent: it says how big the hit
  // was relative to the player's usual, which is a different question from
  // whether it crit. A crit on a resistant target can be small; an ordinary
  // blow on a weakened one can be huge.
  const BIG_HIT_RATIO = 1.6;
  const HUGE_HIT_RATIO = 2.5;

  // Modul: A REMOUNT RESUMES, IT DOES NOT REPLAY. Leaving Combat unmounts this
  // layer; coming back mounted every live number at once, and each started its
  // animation from frame one - so hits that had landed at different moments
  // rose together as one stack. A negative delay starts each where it already
  // was. Read once per number, at mount: the each-block is keyed, so a number
  // already on screen is not re-rendered when the next one arrives.
  function age(atMs: number): number {
    return Math.max(0, performance.timeOrigin + performance.now() - atMs);
  }

  function magnitude(amount: number): 'normal' | 'big' | 'huge' {
    const median = $typicalHit;
    if (median === null || median <= 0) return 'normal';
    if (amount >= median * HUGE_HIT_RATIO) return 'huge';
    if (amount >= median * BIG_HIT_RATIO) return 'big';
    return 'normal';
  }
</script>

<!-- Keyed on id so Svelte reuses nodes and each number animates exactly once,
     which is what UIComponentPool was hand-rolling in the Unity client. -->
<div class="layer" aria-hidden="true">
  {#each $damageEvents as event (event.id)}
    <span
      class="hit"
      class:crit={event.isCrit}
      class:double={event.isDoubleCrit}
      class:burn={event.isBurn}
      data-source={event.source}
      data-proc={calloutFor(event.proc)?.tone ?? null}
      data-size={event.source === 'hit' ? magnitude(event.amount) : null}
      style="--x: {10 + event.offset * 80}%; --life: {DAMAGE_TEXT_LIFETIME_MS}ms; animation-delay: -{age(event.atMs)}ms"
    >
      <span class="num">-{formatNumber(event.amount)}</span>
      {#if calloutFor(event.proc)}
        <span class="tag skill">{calloutFor(event.proc)?.label}</span>
      {:else if event.isDoubleCrit}
        <span class="tag">DOUBLE CRIT!!</span>
      {:else if event.isCrit}
        <span class="tag">CRIT!</span>
      {:else if event.source === 'bleed'}
        <span class="tag">BLEED</span>
      {:else if event.source === 'thorns'}
        <span class="tag">THORNS</span>
      {:else if event.isBurn}
        <span class="tag">BURN</span>
      {/if}
    </span>
  {/each}
</div>

<style>
  /* Modul: THE NUMBERS LIVE OVER THE SCENE (owner, 2026-10-28). They rose in a
     2.25rem strip above a thumbnail; the fight is a full-width scene now, so
     the layer is the scene's own box and a number rises from the monster's
     middle. Pinned by the parent (.floatwrap), never in flow. */
  .layer {
    position: absolute;
    inset: 0;
    overflow: hidden;
    pointer-events: none;
  }

  /* Modul: A BLACK OUTLINE, THEN THE GLOW. Over a painted background a plain
     coloured number vanished into whatever the scene was behind it. The
     stroke is painted UNDER the fill (paint-order) so it outlines the glyph
     without eating into it, and the glow sits outside both. */
  /* Modul: NEVER PAST THE EDGE (owner, 2026-10-28). The number is centred on
     a random --x, and a centred box wider than the distance to the edge was
     cut off - "DOUBLE CRIT!!" most of all. The centre is clamped to half the
     widest thing it can carry (--half, set per kind below) from each side,
     plus the 18% the pop scales it by - measured on a 360px phone. */
  .hit {
    --half: 2.6rem;
    position: absolute;
    top: 46%;
    left: clamp(var(--half), var(--x), calc(100% - var(--half)));
    translate: -50% 0;
    display: flex;
    flex-direction: column;
    align-items: center;
    font-weight: 900;
    font-size: 1.3rem;
    line-height: 1;
    font-variant-numeric: tabular-nums;
    color: #ff4a3a;
    -webkit-text-stroke: 4px #000;
    paint-order: stroke fill;
    text-shadow: 0 2px 4px rgba(0, 0, 0, 0.7);
    animation: float-up var(--life) cubic-bezier(0.2, 0.7, 0.3, 1) forwards;
    white-space: nowrap;
  }

  .tag {
    margin-top: 0.1rem;
    font-size: 0.62em;
    letter-spacing: 0.06em;
    -webkit-text-stroke: 3px #000;
  }

  /* Yellow, louder and glowing. The hue is the whole signal here - a player
     glancing at the scene should be able to tell a crit landed without
     reading the number. */
  .hit.crit {
    --half: 3.4rem;
    font-size: 1.6rem;
    color: #ffd65c;
    text-shadow:
      0 2px 4px rgba(0, 0, 0, 0.8),
      0 0 12px rgba(255, 200, 60, 0.9);
  }

  /* Double Strike: hotter still, orange-white with a wider glow. */
  .hit.double {
    --half: 5.6rem;
    font-size: 1.8rem;
    color: #fff0b0;
    text-shadow:
      0 2px 4px rgba(0, 0, 0, 0.8),
      0 0 10px rgba(255, 150, 40, 1),
      0 0 22px rgba(255, 90, 20, 0.85);
  }

  .hit.double .tag {
    color: #ff9a3c;
  }

  /* A skill's callout (skillProcs.ts): its own colour and a heavier pop, so a
     Crushing Blow reads as a different event from a crit at a glance. */
  .hit[data-proc='crush'] {
    --half: 4.2rem;
    font-size: 1.75rem;
    color: #ffd2c4;
    text-shadow:
      0 2px 4px rgba(0, 0, 0, 0.85),
      0 0 10px rgba(226, 103, 74, 1),
      0 0 20px rgba(180, 40, 20, 0.85);
    animation-name: float-up, crush-shake;
    animation-duration: var(--life), 260ms;
    animation-timing-function: cubic-bezier(0.2, 0.7, 0.3, 1), linear;
  }

  .hit[data-proc='crush'] .tag.skill {
    color: #e2674a;
    font-size: 0.7em;
  }

  /* Echo: pale cyan, with a fading copy of the number trailing it - the
     repeat the player is being told about. */
  .hit[data-proc='echo'] {
    --half: 3.8rem;
    font-size: 1.6rem;
    color: #d8f6ff;
    text-shadow:
      0 2px 4px rgba(0, 0, 0, 0.8),
      0 0 10px rgba(87, 199, 227, 0.95),
      14px -6px 0 rgba(87, 199, 227, 0.35);
  }

  .hit[data-proc='echo'] .tag.skill {
    color: #57c7e3;
    font-size: 0.66em;
  }

  /* Riposte: verdigris, the Guard column's colour, swept in sideways - the
     answer comes from the player's side, not from above. */
  .hit[data-proc='riposte'] {
    --half: 3.6rem;
    font-size: 1.5rem;
    color: #d9ffe8;
    text-shadow:
      0 2px 4px rgba(0, 0, 0, 0.8),
      0 0 10px rgba(95, 194, 138, 1);
    animation-name: float-up, riposte-sweep;
    animation-duration: var(--life), 220ms;
    animation-timing-function: cubic-bezier(0.2, 0.7, 0.3, 1), ease-out;
  }

  .hit[data-proc='riposte'] .tag.skill {
    color: #5fc28a;
    font-size: 0.66em;
  }

  /* Ruin: the heaviest blow in the game - dark red, big, with a shockwave
     ring behind the number. */
  .hit[data-proc='ruin'] {
    --half: 5rem;
    font-size: 2.1rem;
    color: #ffe1d6;
    text-shadow:
      0 2px 4px rgba(0, 0, 0, 0.9),
      0 0 12px rgba(160, 20, 10, 1),
      0 0 26px rgba(226, 103, 74, 0.9);
    animation-name: float-up, crush-shake;
    animation-duration: var(--life), 320ms;
    animation-timing-function: cubic-bezier(0.2, 0.7, 0.3, 1), linear;
  }

  .hit[data-proc='ruin']::before {
    content: '';
    position: absolute;
    inset: -0.6rem -1rem;
    border: 2px solid rgba(226, 103, 74, 0.8);
    border-radius: 50%;
    animation: ruin-ring 420ms ease-out forwards;
    pointer-events: none;
  }

  .hit[data-proc='ruin'] .tag.skill {
    color: #b8321f;
    font-size: 0.6em;
  }

  @keyframes ruin-ring {
    from { transform: scale(0.4); opacity: 1; }
    to { transform: scale(1.6); opacity: 0; }
  }

  /* Perfect Sequence: white with a gold rim - the crit the rhythm promised. */
  .hit[data-proc='perfect'] {
    --half: 4.4rem;
    font-size: 1.8rem;
    color: #ffffff;
    text-shadow:
      0 2px 4px rgba(0, 0, 0, 0.85),
      0 0 3px rgba(232, 193, 90, 1),
      0 0 18px rgba(232, 193, 90, 0.9);
  }

  .hit[data-proc='perfect'] .tag.skill {
    color: #e8c15a;
    font-size: 0.64em;
  }

  @keyframes riposte-sweep {
    from { translate: -18px 0; }
    to { translate: 0 0; }
  }

  @keyframes crush-shake {
    0%, 100% { translate: 0 0; }
    20% { translate: -3px 1px; }
    40% { translate: 3px -1px; }
    60% { translate: -2px 0; }
    80% { translate: 2px 1px; }
  }

  /* Modul: DAMAGE THAT NO SWING DEALT LOOKS LIKE ITS SOURCE. A bleed share,
     a reflected blow and a burn used to float as the same red number as a
     swing - the bleed not at all, since the server published nothing for it.
     Each has its own hue AND its own shape of motion, so the difference
     survives for someone who cannot tell the colours apart. */

  /* Bleed: small, dark crimson, slanted, and it DRIPS - sinks instead of
     rising, with a drop shape beside it. It ticks every second, so it is
     the quietest number on screen on purpose. */
  .hit[data-source='bleed'] {
    --half: 2.2rem;
    top: 40%;
    font-size: 1.05rem;
    font-style: italic;
    color: #e0304a;
    -webkit-text-stroke: 3px #1a0004;
    text-shadow: 0 0 6px rgba(150, 0, 20, 0.9);
    animation-name: bleed-drip;
  }

  .hit[data-source='bleed'] .num::before {
    content: '';
    display: inline-block;
    width: 0.45em;
    height: 0.45em;
    margin-right: 0.22em;
    vertical-align: 0.1em;
    background: #b0102a;
    border: 1px solid #1a0004;
    border-radius: 0 50% 50% 50%;
    transform: rotate(45deg);
  }

  .hit[data-source='bleed'] .tag {
    color: #d42a44;
    font-style: normal;
  }

  /* Thorns: briar green, swept in from the player's side - it is the
     monster's own blow coming back, not a swing from above. */
  .hit[data-source='thorns'] {
    --half: 2.6rem;
    font-size: 1.15rem;
    color: #a8e07a;
    text-shadow:
      0 2px 4px rgba(0, 0, 0, 0.8),
      0 0 8px rgba(70, 140, 40, 0.95);
    animation-name: float-up, riposte-sweep;
    animation-duration: var(--life), 220ms;
    animation-timing-function: cubic-bezier(0.2, 0.7, 0.3, 1), ease-out;
  }

  .hit[data-source='thorns'] .tag {
    color: #6fae3c;
  }

  /* Burn: the swing still sets the colour (a burning crit stays yellow),
     but it gets a flickering fire glow, and an ordinary hit turns ember
     orange with a BURN tag. */
  /* A skill callout keeps its own glow and motion; the tag still says BURN
     only when nothing louder claimed it. */
  .hit.burn:not([data-proc]) {
    text-shadow:
      0 2px 4px rgba(0, 0, 0, 0.8),
      0 -2px 8px rgba(255, 120, 20, 0.95),
      0 -6px 16px rgba(255, 60, 0, 0.7);
    animation-name: float-up, burn-flicker;
    animation-duration: var(--life), 180ms;
    animation-timing-function: cubic-bezier(0.2, 0.7, 0.3, 1), steps(2);
    animation-iteration-count: 1, 8;
  }

  .hit.burn:not(.crit):not([data-proc]) {
    color: #ff9a3c;
  }

  .hit.burn .tag {
    color: #ff6a1a;
  }

  @keyframes bleed-drip {
    0% {
      transform: translateY(-0.4rem) scale(0.7);
      opacity: 0;
    }
    12% {
      transform: translateY(0) scale(1.05);
      opacity: 1;
    }
    70% {
      opacity: 1;
    }
    100% {
      transform: translateY(1.8rem) scale(0.95);
      opacity: 0;
    }
  }

  @keyframes burn-flicker {
    from { filter: brightness(1); }
    to { filter: brightness(1.35); }
  }

  /* Size carries the difference as well as hue, so the distinction survives
     for someone who cannot separate orange from red. It scales a non-crit
     only: a crit is already the loudest thing on screen. */
  .hit:not(.crit)[data-size='big'] {
    color: var(--rarity-11);
    font-size: 1.45rem;
  }

  .hit:not(.crit)[data-size='huge'] {
    --half: 3rem;
    color: var(--gold);
    font-size: 1.6rem;
  }

  /* A pop, a long readable hold, then a rise and fade. */
  @keyframes float-up {
    0% {
      transform: translateY(0.6rem) scale(0.6);
      opacity: 0;
    }
    10% {
      transform: translateY(0) scale(1.18);
      opacity: 1;
    }
    18% {
      transform: translateY(-0.2rem) scale(1);
      opacity: 1;
    }
    70% {
      opacity: 1;
    }
    100% {
      transform: translateY(-3.2rem) scale(0.95);
      opacity: 0;
    }
  }

  /* The numbers are decoration; the health bar carries the same information. */
  @media (prefers-reduced-motion: reduce) {
    .hit {
      animation: none;
      opacity: 0.9;
    }
    .hit[data-proc='ruin']::before {
      display: none;
    }
    .hit[data-source='bleed'],
    .hit[data-source='thorns'],
    .hit.burn {
      animation: none;
    }
  }
</style>
