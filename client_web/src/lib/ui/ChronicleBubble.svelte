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

  // Modul: TWO PLACES, ONE COMPONENT (owner, 2026-11-19). On a phone three
  // floating things - the chat bubble, this bubble and the coach - covered
  // about 130 px of every screen, so a phone shows the Chronicle as a chip in
  // the header row instead. A desktop keeps the floating bubble: its header
  // has 50-60 px free at 1280-1366 px and none at 1024 (measured for the
  // potion strip, Game.svelte), and nothing floats over its content there.
  // Both instances read the same two queries, so TanStack polls once.
  interface Props {
    placement?: 'float' | 'header';
  }
  const { placement = 'float' }: Props = $props();

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
    class={placement === 'header' ? 'head' : 'bubble'}
    onclick={open}
    aria-label={claimable > 0 ? `Chronicle, ${claimable} to claim` : `Chronicle, level ${level}`}
    title="Chronicle and daily missions"
    data-testid={placement === 'header' ? 'chronicle-head' : 'chronicle-bubble'}
  >
    <span class="glyph" aria-hidden="true">
      <svg viewBox="0 0 24 24" width="26" height="26">
        <path d="M6 3h10a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z" fill="none" stroke="currentColor" stroke-width="1.7" />
        <path d="M8.5 8.5l1.5 1.5 3-3M8.5 14.5l1.5 1.5 3-3" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </span>
    <span class="progress" aria-hidden="true">Lv {level}</span>
    {#if claimable > 0}
      <span class="badge" data-testid={placement === 'header' ? 'chronicle-head-badge' : 'chronicle-badge'} aria-hidden="true">{claimable}</span>
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

  /* The header chip: a phone only (see the Props comment). */
  .head {
    display: none;
  }

  @media (max-width: 40rem) {
    .bubble {
      display: none;
    }

    /* The floating bubble's face, shrunk to the 44px touch floor: the
       header row at 360px has no room for a wider pill (brand mark, purse,
       this and the menu add up to 352px). */
    .head {
      position: relative;
      display: inline-flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      flex: none;
      width: 44px;
      height: 44px;
      min-width: 44px;
      min-height: 44px;
      padding: 0;
      border-radius: 50%;
      color: var(--bubble-ink);
      background: radial-gradient(circle at 35% 30%, var(--bubble-hi), var(--bubble-lo));
      border: 1.5px solid var(--bubble-rim);
    }

    .head .glyph {
      line-height: 0;
    }

    .head .glyph :global(svg) {
      width: 16px;
      height: 16px;
    }

    .head .progress {
      font-size: 0.55rem;
    }

    .head .badge {
      top: -6px;
      right: -6px;
      min-width: 18px;
      height: 18px;
      padding: 0 4px;
      font-size: 0.65rem;
      border-width: 1.5px;
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
