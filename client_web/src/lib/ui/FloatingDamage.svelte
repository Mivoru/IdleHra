<script lang="ts">
  import { formatNumber } from './format';
  import { damageEvents, typicalHit } from '../stores/game';
  import { DAMAGE_TEXT_LIFETIME_MS } from '../stores/damage';

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
      data-size={magnitude(event.amount)}
      style="--x: {10 + event.offset * 80}%; --life: {DAMAGE_TEXT_LIFETIME_MS}ms; animation-delay: -{age(event.atMs)}ms"
    >
      <span class="num">-{formatNumber(event.amount)}</span>
      {#if event.isDoubleCrit}
        <span class="tag">DOUBLE CRIT!!</span>
      {:else if event.isCrit}
        <span class="tag">CRIT!</span>
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
  }
</style>
