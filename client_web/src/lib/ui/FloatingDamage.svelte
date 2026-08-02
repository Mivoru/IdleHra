<script lang="ts">
  import { damageEvents, typicalHit } from '../stores/game';
  import { DAMAGE_TEXT_LIFETIME_MS } from '../stores/damage';

  // Modul: big hits are coloured and sized differently from ordinary ones.
  //
  // THIS IS NOT A CRIT FLAG. The wire carries no such thing - damage is
  // inferred here from the monster's health falling between snapshots, so all
  // this client can honestly say is "that one was larger than your usual". A
  // real crit and a hit that landed on a weakened enemy look identical from
  // outside, and calling either of them "CRIT" would be inventing information.
  //
  // The threshold is relative to the running median rather than an absolute
  // number, so it keeps meaning the same thing as the player's damage grows.
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
