<script lang="ts">
  // Modul: THE CHRONICLE BUBBLE (owner, 2026-11-15: "in Clash Royale it sits
  // in a bubble at the side of the screen, with a red number of rewards ready
  // to claim"). It floats bottom-left, opposite the chat dock, on every
  // screen, and opens the Chronicle window - on the missions tab when only
  // missions are waiting, on the pass otherwise.
  //
  // The badge is the server's two Claimable counts added up - pass levels
  // reached and unclaimed, and finished missions - polled every 20 s, so
  // the client never decides what is done. The ring shows the season level.
  // Every 20 s (was 60): a finished mission waiting a minute for its badge
  // read as "it did not count" (owner, 2026-11-15).
  import { createQuery } from '@tanstack/svelte-query';
  import { fetchChronicle, fetchMissions, queryKeys } from '../net/rest';
  import { playerState } from '../stores/game';
  import { onboardingDismissed } from '../stores/tutorial';
  import { guidedStage } from '../stores/guided';
  import { chronicleView } from './missions';
  import { chatDockOpen } from '../stores/chatDock';

  // Not during the guided first minute: that has one thing to point at.
  // Not while chat is open either (owner, 2026-11-15: "the bubble overlaps the
  // chat"): on a phone the chat window covers the whole screen and the bubble,
  // drawn after it at the same z-index, sat on its message box.
  const visible = $derived(
    $playerState !== null && !$chatDockOpen && guidedStage($playerState, $onboardingDismissed) === null,
  );

  const day = createQuery(() => ({
    queryKey: queryKeys.missions,
    queryFn: fetchMissions,
    enabled: visible,
    refetchInterval: 20_000,
  }));
  const pass = createQuery(() => ({
    queryKey: queryKeys.chronicle,
    queryFn: fetchChronicle,
    enabled: visible,
    refetchInterval: 20_000,
  }));

  // A failed poll counts as nothing waiting rather than showing a stale count:
  // the bubble is a shortcut, and the window loads its own data.
  const missionsReady = $derived(day.isError ? 0 : (day.data?.Claimable ?? 0));
  const passReady = $derived(pass.isError ? 0 : (pass.data?.Claimable ?? 0));
  const claimable = $derived(missionsReady + passReady);
  const level = $derived(pass.data?.Level ?? 0);

  function open() {
    chronicleView.set(missionsReady > 0 && passReady === 0 ? 'missions' : 'pass');
  }
</script>

{#if visible && (day.data || pass.data)}
  <button
    class="bubble"
    onclick={open}
    aria-label={claimable > 0 ? `Chronicle, ${claimable} to claim` : `Chronicle, level ${level}`}
    title="Chronicle and daily missions"
    data-testid="chronicle-bubble"
  >
    <span class="glyph" aria-hidden="true">
      <svg viewBox="0 0 24 24" width="26" height="26">
        <path d="M6 3h10a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z" fill="none" stroke="currentColor" stroke-width="1.7" />
        <path d="M8.5 8.5l1.5 1.5 3-3M8.5 14.5l1.5 1.5 3-3" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </span>
    <span class="progress" aria-hidden="true">Lv {level}</span>
    {#if claimable > 0}
      <span class="badge" data-testid="chronicle-badge" aria-hidden="true">{claimable}</span>
    {/if}
  </button>
{/if}

<style>
  .bubble {
    position: fixed;
    left: calc(1rem + var(--sa-left));
    /* The same row as the chat dock, which sits bottom-right. */
    bottom: calc(1rem + var(--sa-bottom) + var(--tabbar-h));
    z-index: 40;
    width: 56px;
    height: 56px;
    min-width: 56px;
    min-height: 56px;
    padding: 0;
    border-radius: 50%;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 0;
    color: var(--bubble-ink);
    background: radial-gradient(circle at 35% 30%, var(--bubble-hi), var(--bubble-lo));
    border: 2px solid var(--bubble-rim);
    box-shadow: 0 4px 14px rgba(0, 0, 0, 0.35);
  }

  .bubble:hover {
    filter: brightness(1.08);
  }

  .progress {
    font-size: 0.65rem;
    font-weight: 700;
    line-height: 1;
    font-variant-numeric: tabular-nums;
  }

  .badge {
    position: absolute;
    top: -4px;
    right: -4px;
    min-width: 22px;
    height: 22px;
    padding: 0 6px;
    border-radius: 11px;
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 0.75rem;
    font-weight: 700;
    color: #fff;
    background: #d63a2f;
    border: 2px solid var(--bubble-badge-rim);
    animation: pulse 1.8s ease-in-out infinite;
  }

  @keyframes pulse {
    0%,
    100% {
      transform: scale(1);
    }
    50% {
      transform: scale(1.12);
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .badge {
      animation: none;
    }
  }

  /* Modul: ON A PHONE THE CHAT BUBBLE OWNS THE CORNER (ChatBubble.svelte: 3rem,
     0.75rem from the left and from the tab bar), and this sat right on top of
     it (owner, 2026-11-15, with a screenshot). Same column, same size, one
     step up - the onboarding coach already starts to the right of that
     column, so nothing else is covered. */
  @media (max-width: 40rem) {
    .bubble {
      left: calc(0.75rem + var(--sa-left));
      bottom: calc(0.75rem + 3rem + 0.6rem + var(--sa-bottom) + var(--tabbar-h));
      width: 3rem;
      height: 3rem;
      min-width: 3rem;
      min-height: 3rem;
    }

    .glyph :global(svg) {
      width: 20px;
      height: 20px;
    }

    .progress {
      font-size: 0.58rem;
    }
  }

  :global(:root) {
    --bubble-ink: #3a2410;
    --bubble-hi: #f6d98a;
    --bubble-lo: #c8902f;
    --bubble-rim: #7a5418;
    --bubble-badge-rim: #fff8e6;
  }
</style>
