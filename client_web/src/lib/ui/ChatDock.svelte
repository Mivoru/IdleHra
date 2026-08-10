<script lang="ts">
  import { chatLog, type ChatEntry } from '../stores/game';
  import Chat from '../../routes/Chat.svelte';
  import { createQuery } from '@tanstack/svelte-query';
  import { fetchOnlineStats, queryKeys } from '../net/rest';

  // Modul: chat was a NAVIGATION TAB, which is the wrong shape for it. Chat is
  // ambient - it happens while you are doing something else - and a tab means
  // you can only read it by leaving whatever you were watching, and can only
  // find out something was said by going to look.
  //
  // So: a floating translucent window that slides out, and a red dot on the
  // handle when something arrived while it was shut.
  let open = $state(false);

  // The newest message id the player has actually had on screen. chatLog is
  // newest-first and ids are a monotonic counter assigned on receipt, so one
  // number is enough - no per-channel bookkeeping, and no way for an unread
  // marker to survive a message being trimmed off the end of the log.
  let seenId = $state(0);

  const newestId = $derived(($chatLog as ChatEntry[])[0]?.id ?? 0);
  const unread = $derived(Math.max(0, newestId - seenId));

  // While the window is open the player is looking at it, so anything arriving
  // is read on arrival.
  $effect(() => {
    if (open) seenId = newestId;
  });

  function toggle() {
    open = !open;
    if (open) seenId = newestId;
  }

  const onlineStatsQuery = createQuery(() => ({
    queryKey: queryKeys.onlineStats,
    queryFn: fetchOnlineStats,
    refetchInterval: 10000
  }));
  const onlineCount = $derived(onlineStatsQuery.data?.OnlineCount ?? 0);
</script>

<div class="dock" class:open>
  {#if open}
    <div class="window">
      <header>
        <div class="header-left">
          <strong>Chat</strong>
          <span class="online-indicator" title="{onlineCount} online">
            <span class="online-dot"></span> {onlineCount}
          </span>
        </div>
        <button class="close" aria-label="Close chat" onclick={toggle}>&times;</button>
      </header>
      <div class="body">
        <Chat docked />
      </div>
    </div>
  {/if}

  <button class="handle" onclick={toggle} aria-label={open ? 'Hide chat' : 'Show chat'}>
    <span class="glyph">{open ? '▾' : '▴'}</span>
    Chat
    {#if !open && unread > 0}
      <span class="dot" aria-label="{unread} unread">
        {unread > 9 ? '9+' : unread}
      </span>
    {/if}
  </button>
</div>

<style>
  /* Modul: THE HANDLE HAS TO PAY FOR ITS OWN FOOTPRINT.
     It is fixed to the bottom-right corner, so it floats over whatever the
     page ends with - and a hit test across every screen found it sitting on
     top of three real controls: "Kept" on Ancestors, "Bin" in the chest, and
     a skill-point button on the tree. Not a near miss; a player aiming at
     those hit the chat handle.

     The reservation is declared HERE rather than on the app shell, so the
     component that occupies the corner is the one that books the space and
     the two cannot drift apart when either changes. */
  :global(body) {
    padding-bottom: 4.25rem;
  }

  .dock {
    position: fixed;
    right: 1rem;
    bottom: 1rem;
    z-index: 40;
    display: flex;
    flex-direction: column;
    align-items: flex-end;
    gap: 0.4rem;
    pointer-events: none;
  }

  .dock > * {
    pointer-events: auto;
  }

  .window {
    width: min(30rem, calc(100vw - 2rem));
    height: min(26rem, calc(100vh - 8rem));
    display: flex;
    flex-direction: column;
    border-radius: 10px;
    border: 1px solid rgba(255, 255, 255, 0.16);
    /* Translucent so the game keeps showing through - the point of a dock
       rather than a page. */
    background: color-mix(in srgb, var(--bg-panel, #14161c) 82%, transparent);
    backdrop-filter: blur(10px);
    box-shadow: 0 12px 32px rgba(0, 0, 0, 0.45);
    overflow: hidden;
    animation: slide-up 140ms ease-out;
  }

  @media (prefers-reduced-motion: reduce) {
    .window {
      animation: none;
    }
  }

  @keyframes slide-up {
    from {
      opacity: 0;
      transform: translateY(8px);
    }
    to {
      opacity: 1;
      transform: translateY(0);
    }
  }

  header {
    padding: 0.5rem;
    background: var(--bg-dark);
    display: flex;
    justify-content: space-between;
    align-items: center;
    border-bottom: 1px solid var(--border);
  }

  .header-left {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  .online-indicator {
    display: flex;
    align-items: center;
    gap: 0.25rem;
    font-size: 0.8rem;
    color: var(--dim, #888);
  }

  .online-dot {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background-color: var(--success, #4caf50);
    display: inline-block;
  }

  .close {
    background: none;
    border: none;
    color: inherit;
    font-size: 1.2rem;
    line-height: 1;
    cursor: pointer;
    padding: 0 0.2rem;
    width: auto;
  }

  .body {
    flex: 1;
    min-height: 0;
    overflow: hidden;
  }

  .handle {
    position: relative;
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    padding: 0.45rem 0.8rem;
    border-radius: 999px;
    border: 1px solid rgba(255, 255, 255, 0.16);
    background: color-mix(in srgb, var(--bg-panel, #14161c) 88%, transparent);
    backdrop-filter: blur(8px);
    cursor: pointer;
    font-size: 0.85rem;
    width: auto;
    box-shadow: 0 4px 14px rgba(0, 0, 0, 0.35);
  }

  .glyph {
    opacity: 0.65;
    font-size: 0.75rem;
  }

  .dot {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 1.15rem;
    height: 1.15rem;
    padding: 0 0.3rem;
    border-radius: 999px;
    background: var(--bad, #e5484d);
    color: #fff;
    font-size: 0.7rem;
    font-weight: 700;
    line-height: 1;
  }
</style>
