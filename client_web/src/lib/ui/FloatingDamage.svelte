<script lang="ts">
  import { damageEvents } from '../stores/game';
  import { DAMAGE_TEXT_LIFETIME_MS } from '../stores/damage';
</script>

<!-- Keyed on id so Svelte reuses nodes and each number animates exactly once,
     which is what UIComponentPool was hand-rolling in the Unity client. -->
<div class="layer" aria-hidden="true">
  {#each $damageEvents as event (event.id)}
    <span class="hit" style="left: {8 + event.offset * 78}%; --life: {DAMAGE_TEXT_LIFETIME_MS}ms">
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
  }
</style>
