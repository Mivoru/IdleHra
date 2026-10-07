<script lang="ts">
  // Modul: THE QUEST LINE'S "SHOW ME", the DOM half. See stores/questSpotlight.ts
  // for why it is not the guided overlay's fence.
  //
  // It draws a ring around the first `data-guide` target the page has, from the
  // server's list for the step, and a small caption beside it. The ring takes no
  // clicks (pointer-events: none) so the lit button is pressed exactly like any
  // other, and nothing else on the screen is covered. When none of the step's
  // targets is on the page - an empty chest has no Fuse button, a screen is still
  // loading - the caption is shown on its own at the foot of the screen and the
  // ring waits, which is also how a step whose first move is "pick an item"
  // reads: the picker is the second target, the button the first.
  //
  // RETIRES ITSELF: when the player dismisses it, when the server's list says the
  // step is no longer open (done, claimed or locked), and while the guided first
  // minute or a modal is up - one tutorial voice at a time.
  import { onMount } from 'svelte';
  import { createQuery } from '@tanstack/svelte-query';
  import { fetchQuests, questKeys } from '../net/quests';
  import { currentScreen } from '../stores/navigation';
  import { guidedShowing } from '../stores/guided';
  import { tutorialPrompt } from '../stores/tutorial';
  import { clearSpotlight, questSpotlight } from '../stores/questSpotlight';
  import { pickGuideTarget, spotlightStillWanted } from './questLine';

  const spot = $derived($questSpotlight);

  // Shared with the panel (same key, so one cache entry); only fetched while a
  // spotlight is up, and polled so finishing the act on the lit button retires
  // the ring within a few seconds.
  const quests = createQuery(() => ({
    queryKey: questKeys.all,
    queryFn: fetchQuests,
    enabled: spot !== null,
    refetchInterval: 4_000,
  }));

  $effect(() => {
    // A failed refetch says nothing about the step, so it keeps the ring
    // (null = "not loaded") rather than retiring it on a network blip.
    if (spot && !spotlightStillWanted(quests.isError ? null : quests.data, spot.stepId)) clearSpotlight();
  });

  const onScreen = $derived(spot !== null && $currentScreen === spot.screen);
  const hiddenByOthers = $derived($guidedShowing || $tutorialPrompt !== null);

  let rect = $state<{ top: number; left: number; width: number; height: number } | null>(null);
  let modalUp = $state(false);
  let scrolledTo = '';

  const PAD = 5;

  function measure(): void {
    modalUp = document.querySelector('.backdrop') !== null;
    if (!spot || !onScreen) {
      rect = null;
      return;
    }
    const target = pickGuideTarget(spot.targets, (t) => {
      const el = document.querySelector<HTMLElement>(`[data-guide="${t}"]`);
      if (!el) return false;
      const box = el.getBoundingClientRect();
      return box.width > 0 && box.height > 0;
    });
    if (!target) {
      rect = null;
      return;
    }
    const el = document.querySelector<HTMLElement>(`[data-guide="${target}"]`)!;
    const key = `${spot.stepId}:${target}`;
    if (scrolledTo !== key) {
      scrolledTo = key;
      el.scrollIntoView({ block: 'center', behavior: 'instant' as ScrollBehavior });
      return; // measure again once the scroll has landed
    }
    const box = el.getBoundingClientRect();
    rect = { top: box.top - PAD, left: box.left - PAD, width: box.width + PAD * 2, height: box.height + PAD * 2 };
  }

  onMount(() => {
    const timer = setInterval(measure, 200);
    const onMove = () => measure();
    window.addEventListener('scroll', onMove, true);
    window.addEventListener('resize', onMove);
    return () => {
      clearInterval(timer);
      window.removeEventListener('scroll', onMove, true);
      window.removeEventListener('resize', onMove);
    };
  });

  // A fresh spotlight scrolls its target into view once, not once per tick.
  $effect(() => {
    if (spot) scrolledTo = '';
  });

  const visible = $derived(spot !== null && onScreen && !hiddenByOthers && !modalUp);
  const bubbleBelow = $derived(rect ? rect.top + rect.height + 150 < window.innerHeight : true);
</script>

{#if visible && spot}
  {#if rect}
    <div class="ring" data-testid="quest-ring" style="top:{rect.top}px;left:{rect.left}px;width:{rect.width}px;height:{rect.height}px"></div>
  {/if}
  <div
    class="bubble"
    class:floor={!rect}
    role="status"
    data-testid="quest-spotlight"
    data-step={spot.stepId}
    style={rect
      ? (bubbleBelow ? `top:${rect.top + rect.height + 10}px` : `bottom:${window.innerHeight - rect.top + 10}px`)
      : undefined}
  >
    <strong>{spot.title}</strong>
    <p>{spot.caption}</p>
    <button class="quiet" onclick={clearSpotlight}>Got it</button>
  </div>
{/if}

<style>
  .ring {
    position: fixed;
    z-index: 45;
    pointer-events: none;
    border-radius: 10px;
    box-shadow: 0 0 0 3px var(--accent), 0 0 16px 4px var(--accent);
    /* Modul: the pulse FADES the ring, it never redraws its shadow - an endless
       box-shadow animation repaints every frame (gpuBudget.test.ts). */
    animation: quest-pulse 1.4s ease-in-out infinite;
  }

  @keyframes quest-pulse {
    50% {
      opacity: 0.45;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .ring {
      animation: none;
    }
  }

  .bubble {
    position: fixed;
    z-index: 46;
    left: 50%;
    translate: -50% 0;
    box-sizing: border-box;
    width: max-content;
    max-width: min(22rem, calc(100vw - 1.5rem));
    display: grid;
    gap: 0.3rem;
    padding: 0.55rem 0.75rem;
    background: var(--bg-raised);
    border: 1px solid var(--accent);
    border-radius: 0.6rem;
    font-size: var(--fs-sm);
    box-shadow: 0 6px 18px rgba(0, 0, 0, 0.35);
  }

  /* No control to point at: sit above the tab bar and the chat handle, where
     the coach panel also lives, rather than on top of the page's content. */
  .bubble.floor {
    bottom: calc(4.25rem + var(--sa-bottom) + var(--tabbar-h));
  }

  .bubble p {
    margin: 0;
    color: var(--text-dim);
    overflow-wrap: anywhere;
  }

  .bubble button {
    justify-self: start;
    min-height: 2rem;
  }
</style>
