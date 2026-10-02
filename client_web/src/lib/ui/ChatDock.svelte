<script lang="ts">
  import { chatLog } from '../stores/game';
  import { chatDockOpen, markChatRead, setChatOpen } from '../stores/chatDock';
  import Chat from '../../routes/Chat.svelte';
  import { createQuery } from '@tanstack/svelte-query';
  import { fetchOnlineStats, queryKeys } from '../net/rest';

  // Modul: chat was a NAVIGATION TAB, which is the wrong shape for it. Chat is
  // ambient - it happens while you are doing something else - and a tab means
  // you can only read it by leaving whatever you were watching.
  //
  // So: a window over the game. On a desktop it floats in the corner and the
  // game shows through it; on a phone it is a full-height sheet (task 95),
  // because a 26rem floating box left about 130px for messages.
  //
  // Task 95: this is ONLY the window now. The handle that opened it - and the
  // header button that replaced the handle when nobody was online - became
  // one entry, ChatButton.svelte. The openness and the unread mark live in
  // stores/chatDock.ts, which the back button and the entry also read.
  const open = $derived($chatDockOpen);

  // While the window is open the player is looking at it, so anything arriving
  // is read on arrival.
  $effect(() => {
    if (open && $chatLog) markChatRead();
  });

  const onlineStatsQuery = createQuery(() => ({
    queryKey: queryKeys.onlineStats,
    queryFn: fetchOnlineStats,
    refetchInterval: 10000,
    enabled: open,
  }));
  const onlineCount = $derived(onlineStatsQuery.data?.OnlineCount ?? 0);
</script>

{#if open}
  <div class="dock">
    <div class="window" role="dialog" aria-label="Chat">
      <header>
        <strong>Chat</strong>
        {#if onlineStatsQuery.isSuccess}
          <span class="online-indicator">
            <span class="online-dot" aria-hidden="true"></span>{onlineCount} online
          </span>
        {/if}
        <button class="close" aria-label="Hide chat" onclick={() => setChatOpen(false)}>&times;</button>
      </header>
      <div class="body">
        <Chat docked />
      </div>
    </div>
  </div>
{/if}

<style>
  /* Modul: THE BODY CLEARANCE LIVES IN app.css, ONCE. It was booked for the
     floating handle this file used to draw in the corner; the handle is gone
     (task 95), and the clearance stays as the bottom margin every screen has
     been laid out against. */

  .dock {
    position: fixed;
    right: calc(1rem + var(--sa-right));
    /* Fixed to the VIEWPORT, so body's padding does not move it. */
    bottom: calc(1rem + var(--sa-bottom) + var(--tabbar-h));
    z-index: 40;
  }

  /* Modul: SIZED FROM WHAT IS LEFT, not from a guess at it. With the soft
     keyboard up the APK's viewport shrinks to ~350px, and a fixed 26rem put
     the window's top under the status bar, close button and all. Every term
     the dock stands on is subtracted. The vh line is the fallback for an
     engine without dvh. */
  .window {
    position: relative;
    isolation: isolate;
    width: min(30rem, calc(100vw - 2rem));
    height: min(26rem, calc(100vh - 8rem));
    height: min(26rem, calc(100dvh - var(--sa-top) - var(--sa-bottom) - var(--tabbar-h) - 2rem));
    display: flex;
    flex-direction: column;
    border-radius: 10px;
    border: 1px solid var(--border);
    /* Translucent so the game keeps showing through - the point of a dock
       rather than a page. */
    background: color-mix(in srgb, var(--bg-panel) 82%, transparent);
    box-shadow: 0 12px 32px rgba(0, 0, 0, 0.45);
    overflow: hidden;
    animation: slide-up 140ms ease-out;
  }

  /* Modul: THE BLUR LIVES ON A PSEUDO-ELEMENT, NOT ON THE WINDOW.
     A backdrop-filter makes its element the containing block for every
     position: fixed descendant. The window holds the whole chat, and chat
     opens the player profile and the name menu - so both were laid out
     inside this box instead of the screen. A pseudo-element is not an
     ancestor of anything. */
  .window::before {
    content: '';
    position: absolute;
    inset: 0;
    z-index: -1;
    border-radius: inherit;
    backdrop-filter: blur(10px);
    -webkit-backdrop-filter: blur(10px);
    pointer-events: none;
  }

  /* Modul: TASK 95 - ON A PHONE, A SHEET, NOT A WINDOW. The floating 26rem box
     had a frame inside a frame and about 130px of messages. Here it takes the
     whole height between the sticky header and the tab bar, with one frame (the
     sheet's own top edge), and it is opaque: no blur under a thumb (task 108),
     and nothing behind it worth seeing through a full-height panel. While the
     keyboard is up --tabbar-h is 0 (App.svelte), so the composer sits on it. */
  @media (max-width: 40rem) {
    .dock {
      inset: calc(var(--sa-top) + var(--sticky-header-h)) 0 calc(var(--sa-bottom) + var(--tabbar-h)) 0;
    }
    .window {
      width: 100%;
      height: 100%;
      border: none;
      border-top: 1px solid var(--border);
      border-radius: 0;
      background: var(--bg-panel);
      box-shadow: none;
      padding-left: var(--sa-left);
      padding-right: var(--sa-right);
    }
    .window::before {
      content: none;
    }
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
    padding: 0.35rem 0.5rem 0.35rem 0.75rem;
    /* --bg-raised, not --bg-dark: the theme never defined --bg-dark. */
    background: color-mix(in srgb, var(--bg-raised) 70%, transparent);
    display: flex;
    align-items: center;
    gap: 0.6rem;
    border-bottom: 1px solid var(--border);
  }

  .online-indicator {
    display: inline-flex;
    align-items: center;
    gap: 0.3rem;
    font-size: var(--fs-xs);
    color: var(--text-dim);
  }

  .online-dot {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background-color: var(--good);
    display: inline-block;
  }

  .close {
    margin-left: auto;
    background: none;
    border: none;
    box-shadow: none;
    color: inherit;
    font-size: 1.3rem;
    line-height: 1;
    cursor: pointer;
    min-width: 44px;
    min-height: 44px;
    padding: 0;
    width: auto;
    flex-shrink: 0;
  }

  .body {
    flex: 1;
    min-height: 0;
    overflow: hidden;
  }
</style>
