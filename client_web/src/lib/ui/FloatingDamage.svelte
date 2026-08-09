<script lang="ts">
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
      data-size={magnitude(event.amount)}
      style="left: {8 + event.offset * 78}%; --life: {DAMAGE_TEXT_LIFETIME_MS}ms"
    >
      -{event.amount.toLocaleString()}
    </span>
  {/each}
</div>

<style>
  .layer {
    position: relative;
    height: 2.25rem;
    overflow: hidden;
    pointer-events: none;
  }

  .hit {
    position: absolute;
    bottom: 0;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
    color: var(--danger);
    text-shadow: 0 1px 3px rgba(0, 0, 0, 0.85);
    animation: float-up var(--life) ease-out forwards;
    white-space: nowrap;
  }

  /* Yellow, and a little louder. The hue is the whole signal here - a player
     glancing at the bar should be able to tell a crit landed without reading
     the number. */
  .hit.crit {
    color: #ffd65c;
    text-shadow:
      0 1px 3px rgba(0, 0, 0, 0.9),
      0 0 10px rgba(255, 214, 92, 0.75);
  }

  /* Size carries the difference as well as hue, so the distinction survives
     for someone who cannot separate orange from red. */
  .hit[data-size='big'] {
    color: var(--rarity-11);
    font-size: 1.15em;
  }

  .hit[data-size='huge'] {
    color: var(--gold);
    font-size: 1.35em;
    text-shadow: 0 1px 3px rgba(0, 0, 0, 0.85), 0 0 10px currentColor;
  }

  @keyframes float-up {
    from {
      transform: translateY(0.4rem) scale(0.85);
      opacity: 0;
    }
    18% {
      transform: translateY(0) scale(1.12);
      opacity: 1;
    }
    to {
      transform: translateY(-1.6rem) scale(1);
      opacity: 0;
    }
  }

  /* The numbers are decoration; the health bar carries the same information. */
  @media (prefers-reduced-motion: reduce) {
    .hit {
      animation: none;
      opacity: 0.9;
    }
    .hit[data-size='huge'] {
      text-shadow: 0 1px 3px rgba(0, 0, 0, 0.85);
    }
  }
</style>
