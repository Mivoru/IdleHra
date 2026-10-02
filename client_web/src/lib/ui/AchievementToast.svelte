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
  import { requestScreen } from '../stores/navigation';

  // Task 109: the card is a way in, not only a notice - it opens the Book of
  // Deeds (Progress, Goals tab), where the deed and its next tier live.
  function openBook(id: number): void {
    dismissAchievementToast(id);
    requestScreen('progression', { tab: 'goals' });
  }
</script>

<div class="deeds" role="status" aria-live="polite">
  {#each $achievementToasts as toast (toast.id)}
    <div class="deed">
      <!-- Modul: was a private .sweep span with its own keyframes. Now the
           shared .folk-sweep in app.css, because four other moments wanted the
           same flourish and five copies of one effect drift.
           The sweep is a sibling rather than a background, so it can cross the
           whole card once and be done without fighting the frame's own paint. -->
      <span class="sweep" aria-hidden="true"></span>

      <span class="seal" aria-hidden="true">
        {#if toast.tierLabel}
          {toast.tierLabel}
        {:else}
          <!-- Modul: the fallback when a tier has no label. It was a ★, which
               is the one shape in this file most likely to render as a
               different weight - or as a coloured emoji star - depending on
               the platform's font stack. -->
          <svg class="sealmark" viewBox="0 0 24 24" aria-hidden="true">
            <path d="M12 2 L14.9 8.6 L22 9.4 L16.7 14.2 L18.2 21.2 L12 17.6 L5.8 21.2 L7.3 14.2 L2 9.4 L9.1 8.6 Z"
                  fill="currentColor" />
          </svg>
        {/if}
      </span>

      <!-- The seal's bare "III" said nothing on its own; the title carries
           the tier in words. -->
      <button class="body" type="button" onclick={() => openBook(toast.id)} aria-label="Open the Book of Deeds: {toast.title}">
        <span class="eyebrow">Deed accomplished</span>
        <strong class="title">{toast.title}{#if toast.tierLabel} - tier {toast.tierLabel}{/if}</strong>
        {#if toast.reward}<span class="reward">{toast.reward}</span>{/if}
      </button>

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
    right: calc(1rem + var(--sa-right));
    /* Clear of Toasts.svelte, which owns the bottom-right corner - and both
       stack on top of the home indicator, so both carry the same inset. */
    bottom: calc(5.5rem + var(--sa-bottom) + var(--tabbar-h));
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

  /* A fixed layer is outside .panel's wrap rule (app.css), so it carries its
     own: a long deed title wraps rather than widening the card. */
  .body {
    display: grid;
    gap: 0.05rem;
    min-width: 0;
    overflow-wrap: anywhere;
    flex: 1;
    /* A button that reads as the card's text, not as a control on it. */
    background: none;
    border: 0;
    padding: 0;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
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
     pinned right would otherwise sit half off a 320px viewport.

     Modul: ABOVE THE TAB BAR, MEASURED FROM IT. This was a bare `bottom: 5rem`,
     which ignored both the tab bar and the gesture inset: on a phone with a
     34px gesture bar the tab bar is ~5.6rem tall, so this z-61 card sat on top
     of it - and on the chat handle (1rem above the bar) on every phone - for
     its four seconds. 4.25rem over the bar clears the handle; it is the band
     the onboarding coach and the update prompt already use. */
  @media (max-width: 30rem) {
    .deeds {
      right: calc(0.5rem + var(--sa-right));
      left: calc(0.5rem + var(--sa-left));
      bottom: calc(4.25rem + var(--sa-bottom) + var(--tabbar-h));
      max-width: none;
    }
  }

  /* The loot reveal takes the band above the tab bar wherever there is one
     (LootReveal.svelte). A deed and a Legendary can land together - stand on
     top of the reveal rather than under it. */
  @media (max-width: 40rem) {
    :global(body:has([data-loot-reveal])) .deeds {
      bottom: calc(10rem + var(--sa-bottom) + var(--tabbar-h));
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

  .sealmark {
    width: 1em;
    height: 1em;
  }
</style>
