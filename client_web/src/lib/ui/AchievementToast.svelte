<script lang="ts">
  // Modul: THE CELEBRATION, which the game had none of.
  //
  // Tiered achievements auto-award in StateCheckpointManager and the only
  // evidence was a diamond count quietly going up. The moment of earning is
  // most of what an achievement is worth, and this is the whole of that
  // moment: a struck bell, a light sweep across brass, four seconds.
  //
  // Separate from Toasts.svelte deliberately. That one reports whether a
  // command worked and must stay small and dismissible; this one is meant to
  // be looked at. Sharing a component would have made both worse.
  import { achievementToasts, dismissAchievementToast } from '../stores/game';
</script>

<div class="deeds" role="status" aria-live="polite">
  {#each $achievementToasts as toast (toast.id)}
    <div class="deed">
      <!-- The sweep is a sibling rather than a background, so it can cross the
           whole card once and be done without fighting the frame's own paint. -->
      <span class="sweep" aria-hidden="true"></span>

      <span class="seal" aria-hidden="true">
        {#if toast.tierLabel}{toast.tierLabel}{:else}★{/if}
      </span>

      <span class="body">
        <span class="eyebrow">Deed accomplished</span>
        <strong class="title">{toast.title}</strong>
        {#if toast.reward}<span class="reward">{toast.reward}</span>{/if}
      </span>

      <button
        class="close"
        aria-label="Dismiss"
        onclick={() => dismissAchievementToast(toast.id)}>×</button
      >
    </div>
  {/each}
</div>

<style>
  .deeds {
    position: fixed;
    right: 1rem;
    /* Clear of Toasts.svelte, which owns the bottom-right corner. */
    bottom: 5.5rem;
    display: grid;
    gap: 0.5rem;
    z-index: 61;
    max-width: min(23rem, 92vw);
    pointer-events: none;
  }

  .deed {
    position: relative;
    overflow: hidden;
    display: flex;
    align-items: center;
    gap: 0.7rem;
    padding: 0.7rem 0.8rem;
    pointer-events: auto;

    background:
      linear-gradient(180deg, rgba(216, 180, 90, 0.13), rgba(0, 0, 0, 0)),
      var(--bg-raised);
    border: 1px solid var(--brass);
    border-left: 3px solid var(--brass-lit);
    border-radius: var(--radius);
    box-shadow:
      0 0 0 1px rgba(216, 180, 90, 0.18),
      0 10px 26px rgba(0, 0, 0, 0.45);
    animation:
      deed-in 260ms cubic-bezier(0.2, 0.9, 0.3, 1),
      deed-glow 1.4s ease-out;
  }

  .sweep {
    position: absolute;
    inset: 0;
    background: linear-gradient(
      100deg,
      transparent 20%,
      rgba(255, 240, 200, 0.34) 48%,
      transparent 74%
    );
    translate: -110% 0;
    animation: deed-sweep 900ms ease-out 140ms;
    pointer-events: none;
  }

  .seal {
    flex: none;
    display: grid;
    place-items: center;
    width: 2.3rem;
    height: 2.3rem;
    border-radius: 50%;
    background: radial-gradient(circle at 35% 30%, var(--brass-lit), var(--brass));
    color: #21180a;
    font-weight: 700;
    font-size: 0.8rem;
    letter-spacing: 0.02em;
    box-shadow: inset 0 -2px 4px rgba(0, 0, 0, 0.35);
  }

  .body {
    display: grid;
    gap: 0.05rem;
    min-width: 0;
  }

  .eyebrow {
    font-size: 0.62rem;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    /* --brass-lit and not --brass: the light theme already redefines
       --brass-lit to a dark #6d5322, which measures 5.6:1 against this card's
       parchment. Substituting --brass here looks like a contrast fix and is
       the opposite - it drops to 3.9:1, under AA for text this small. */
    color: var(--brass-lit);
  }

  .title {
    font-size: 0.95rem;
    line-height: 1.2;
  }

  .reward {
    font-size: 0.76rem;
    color: var(--text-dim);
  }

  .close {
    background: none;
    border: none;
    margin-left: auto;
    padding: 0 0.15rem;
    color: var(--text-dim);
    font-size: 1.1rem;
    line-height: 1;
  }

  @keyframes deed-in {
    from {
      translate: 0.9rem 0;
      opacity: 0;
    }
  }

  @keyframes deed-sweep {
    to {
      translate: 110% 0;
    }
  }

  @keyframes deed-glow {
    0%,
    100% {
      box-shadow:
        0 0 0 1px rgba(216, 180, 90, 0.18),
        0 10px 26px rgba(0, 0, 0, 0.45);
    }
    30% {
      box-shadow:
        0 0 0 1px rgba(216, 180, 90, 0.5),
        0 0 22px rgba(216, 180, 90, 0.4),
        0 10px 26px rgba(0, 0, 0, 0.45);
    }
  }

  /* On a narrow screen the card takes the bottom edge outright - a 23rem card
     pinned right would otherwise sit half off a 320px viewport. */
  @media (max-width: 30rem) {
    .deeds {
      right: 0.5rem;
      left: 0.5rem;
      bottom: 5rem;
      max-width: none;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .deed {
      animation: none;
    }

    .sweep {
      display: none;
    }
  }
</style>
