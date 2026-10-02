<script lang="ts">
  // Modul: THE GUIDED FIRST MINUTE - the DOM half. See lib/stores/guided.ts
  // for why it exists and how small it is kept on purpose.
  //
  // One control is lit and everything else is covered. The cover is FOUR
  // panels around the lit control rather than one sheet with a hole, because a
  // sheet swallows the click on the control too - pointer-events cannot be cut
  // out of an element. The lit control keeps its own position in its own
  // screen: nothing is cloned or moved, so the press the player makes is the
  // real press, on the real button, and that is the lesson.
  //
  // FAILS OPEN, three ways, because a fence with no way through is worse than
  // no fence (the same rule the repo's hooks follow):
  //   - the control it wants is not on the page after a moment -> no cover;
  //   - a modal is up (anything with .backdrop: offline summary, death card,
  //     what's new) -> no cover, the modal is answered first;
  //   - Skip is on screen the whole time.
  import { onMount } from 'svelte';
  import { playerState } from '../stores/game';
  import { onboardingDismissed, skipTutorial } from '../stores/tutorial';
  import { currentScreen, requestScreen } from '../stores/navigation';
  import { guidedStage, guidedShowing } from '../stores/guided';

  const stage = $derived(guidedStage($playerState, $onboardingDismissed));
  const onScreen = $derived(stage !== null && $currentScreen === stage.screen);

  /** The lit control's box, in viewport pixels, or null when not found. */
  let rect = $state<{ top: number; left: number; width: number; height: number } | null>(null);
  let caption = $state('');
  let modalUp = $state(false);
  let missingSince = $state(0);
  let now = $state(Date.now());
  let lastScrolledTo = '';

  const PAD = 6;
  /** How long a missing control is waited for before the cover lets go. */
  const GIVE_UP_MS = 2000;

  function measure(): void {
    now = Date.now();
    modalUp = document.querySelector('.backdrop') !== null;
    if (!stage || !onScreen) {
      rect = null;
      missingSince = 0;
      return;
    }
    for (let i = 0; i < stage.targets.length; i++) {
      const el = document.querySelector<HTMLElement>(`[data-guide="${stage.targets[i]}"]`);
      if (!el) continue;
      const box = el.getBoundingClientRect();
      if (box.width === 0 || box.height === 0) continue;
      if (lastScrolledTo !== stage.targets[i]) {
        lastScrolledTo = stage.targets[i];
        el.scrollIntoView({ block: 'center', behavior: 'instant' as ScrollBehavior });
        return; // measure again on the next pass, once the scroll has landed
      }
      rect = { top: box.top - PAD, left: box.left - PAD, width: box.width + PAD * 2, height: box.height + PAD * 2 };
      caption = stage.captions[i];
      missingSince = 0;
      return;
    }
    rect = null;
    if (missingSince === 0) missingSince = now;
  }

  onMount(() => {
    const timer = setInterval(measure, 150);
    const onMove = () => measure();
    window.addEventListener('scroll', onMove, true);
    window.addEventListener('resize', onMove);
    return () => {
      clearInterval(timer);
      window.removeEventListener('scroll', onMove, true);
      window.removeEventListener('resize', onMove);
    };
  });

  // A picker that opened with nothing to wear has no "wear" to light and the
  // slot button would only close it again - let go rather than trap.
  const gaveUp = $derived(rect === null && missingSince > 0 && now - missingSince > GIVE_UP_MS);

  const mode = $derived(
    !stage || modalUp ? 'off' : !onScreen ? 'elsewhere' : rect ? 'lit' : gaveUp ? 'off' : 'waiting',
  );

  $effect(() => {
    guidedShowing.set(mode !== 'off');
  });
  onMount(() => () => guidedShowing.set(false));

  /** Caption below the lit control, or above it when there is no room below. */
  const bubbleBelow = $derived(rect ? rect.top + rect.height + 140 < window.innerHeight : true);

  function swallow(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
  }
</script>

{#if mode === 'elsewhere' && stage}
  <!-- The player is not on the right screen: everything is covered except the
       way there. -->
  <div class="cover full" data-guided="elsewhere" role="presentation" onclick={swallow}></div>
  <div class="bubble centre" role="dialog" aria-label="Tutorial">
    <p>{stage.captions[stage.captions.length - 1]}</p>
    <button class="go" data-guided-go onclick={() => requestScreen(stage.screen)}>{stage.screenLabel}</button>
    <button class="skip" onclick={skipTutorial}>Skip tutorial</button>
  </div>
{:else if mode === 'lit' && rect}
  <!-- Four panels around the control; the control itself stays uncovered. -->
  <div class="cover" data-guided="lit" role="presentation" style="top:0;left:0;right:0;height:{Math.max(0, rect.top)}px" onclick={swallow}></div>
  <div class="cover" role="presentation" style="top:{rect.top + rect.height}px;left:0;right:0;bottom:0" onclick={swallow}></div>
  <div class="cover" role="presentation" style="top:{rect.top}px;left:0;width:{Math.max(0, rect.left)}px;height:{rect.height}px" onclick={swallow}></div>
  <div class="cover" role="presentation" style="top:{rect.top}px;left:{rect.left + rect.width}px;right:0;height:{rect.height}px" onclick={swallow}></div>
  <div class="ring" style="top:{rect.top}px;left:{rect.left}px;width:{rect.width}px;height:{rect.height}px"></div>
  <div
    class="bubble"
    role="dialog"
    aria-label="Tutorial"
    style={bubbleBelow
      ? `top:${rect.top + rect.height + 12}px`
      : `bottom:${window.innerHeight - rect.top + 12}px`}
  >
    <p>{caption}</p>
    <button class="skip" onclick={skipTutorial}>Skip tutorial</button>
  </div>
{:else if mode === 'waiting'}
  <!-- On the right screen, control not found YET (the screen is still
       loading). Covered, briefly - see GIVE_UP_MS - so a fast tap cannot land
       on the wrong thing while the page settles. -->
  <div class="cover full" data-guided="waiting" role="presentation" onclick={swallow}></div>
{/if}

<style>
  .cover {
    position: fixed;
    z-index: 70;
    background: rgba(12, 9, 5, 0.62);
  }

  .cover.full {
    inset: 0;
  }

  .ring {
    position: fixed;
    z-index: 71;
    pointer-events: none;
    border-radius: 10px;
    box-shadow: 0 0 0 3px var(--accent), 0 0 18px 4px var(--accent);
    animation: guidepulse 1.4s ease-in-out infinite;
  }

  @keyframes guidepulse {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.45; }
  }

  .bubble {
    position: fixed;
    z-index: 72;
    left: 50%;
    transform: translateX(-50%);
    width: min(22rem, calc(100vw - 2rem));
    background: var(--bg-panel);
    border: 1px solid var(--accent);
    border-radius: var(--radius);
    padding: 0.8rem 1rem;
    display: grid;
    gap: 0.6rem;
    justify-items: start;
    box-shadow: 0 8px 28px rgba(0, 0, 0, 0.35);
  }

  .bubble.centre {
    top: 50%;
    transform: translate(-50%, -50%);
  }

  .bubble p {
    margin: 0;
    font-weight: 600;
  }

  .go {
    justify-self: stretch;
  }

  /* Small but always there - the way out of the fence. A link at the right
     edge (task 109), so it reads as the way out and not as a second answer. */
  .skip {
    justify-self: end;
    background: transparent;
    border-color: transparent;
    color: var(--text-dim);
    font-size: 0.85rem;
    text-decoration: underline;
    padding: 0.2rem 0;
    min-height: 44px;
  }

  @media (prefers-reduced-motion: reduce) {
    .ring {
      animation: none;
    }
  }
</style>
