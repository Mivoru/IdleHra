<script lang="ts">
  // Modul: THE CHRONICLE BUBBLE (owner, 2026-11-15: "in Clash Royale it sits
  // in a bubble at the side of the screen, with a red number of rewards ready
  // to claim"). It floats bottom-left, opposite the chat dock, on every
  // screen. Today it opens the daily missions; when the Chronicle pass lands
  // (step 3) the same bubble opens the pass and the badge counts its rewards
  // too.
  //
  // The badge is the server's Claimable - finished, unclaimed missions plus a
  // ready bonus - polled once a minute, so the client never decides what is
  // done.
  import { createQuery } from '@tanstack/svelte-query';
  import { fetchMissions, queryKeys } from '../net/rest';
  import { playerState } from '../stores/game';
  import { onboardingDismissed } from '../stores/tutorial';
  import { guidedStage } from '../stores/guided';
  import { missionsOpen } from './missions';

  // Not during the guided first minute: that has one thing to point at.
  const visible = $derived($playerState !== null && guidedStage($playerState, $onboardingDismissed) === null);

  const day = createQuery(() => ({
    queryKey: queryKeys.missions,
    queryFn: fetchMissions,
    enabled: visible,
    refetchInterval: 60_000,
  }));

  // A failed poll hides the bubble rather than showing a stale or empty count:
  // it is a shortcut, and the missions are still in the window next time.
  const claimable = $derived(day.isError ? 0 : (day.data?.Claimable ?? 0));
  const done = $derived(day.data ? day.data.Missions.filter((m) => m.Done).length : 0);
  const total = $derived(day.data?.Missions.length ?? 0);
</script>

{#if visible && day.data && !day.isError}
  <button
    class="bubble"
    onclick={() => missionsOpen.set(true)}
    aria-label={claimable > 0 ? `Daily missions, ${claimable} to claim` : `Daily missions, ${done} of ${total} done`}
    title="Daily missions"
    data-testid="chronicle-bubble"
  >
    <span class="glyph" aria-hidden="true">
      <svg viewBox="0 0 24 24" width="26" height="26">
        <path d="M6 3h10a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z" fill="none" stroke="currentColor" stroke-width="1.7" />
        <path d="M8.5 8.5l1.5 1.5 3-3M8.5 14.5l1.5 1.5 3-3" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </span>
    <span class="progress" aria-hidden="true">{done}/{total}</span>
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

  :global(:root) {
    --bubble-ink: #3a2410;
    --bubble-hi: #f6d98a;
    --bubble-lo: #c8902f;
    --bubble-rim: #7a5418;
    --bubble-badge-rim: #fff8e6;
  }
</style>
