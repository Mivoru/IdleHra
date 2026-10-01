<script lang="ts">
  import { chatLog, type ChatEntry } from '../stores/game';
  import { chatDockOpen, chatHandleInHeader } from '../stores/chatDock';
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
  //
  // Modul: the openness lives in a store rather than here, because the Android
  // back button has to know whether a layer is covering the screen before it
  // decides to navigate. See stores/chatDock.ts.
  const open = $derived($chatDockOpen);

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
    // Modul: the next value is computed ONCE. `open` is a $derived over the
    // store and Svelte updates it synchronously, so re-reading it after the
    // set would read the value that was just written and invert the branch.
    const next = !open;
    chatDockOpen.set(next);
    if (next) seenId = newestId;
  }

  const onlineStatsQuery = createQuery(() => ({
    queryKey: queryKeys.onlineStats,
    queryFn: fetchOnlineStats,
    refetchInterval: 10000
  }));
  const onlineCount = $derived(onlineStatsQuery.data?.OnlineCount ?? 0);

  // Task 71: a handle that floats over the page corner has to be worth its
  // footprint. With nobody online and nothing unread it has nothing to say,
  // so it moves into the header (App.svelte) and gives the corner back. Only
  // once the count has actually loaded - a handle that blinks away and back
  // on every sign-in is worse than one that stays.
  //
  // THE COUNT INCLUDES YOU. A signed-in player is always one of the online
  // sessions, so "nobody to talk to" is a count of one - the handle's fade
  // (task 52) tested for zero and so, on the live server with one real
  // player, never faded at all.
  const nobodyElse = $derived(onlineCount <= 1);
  const quiet = $derived(onlineStatsQuery.isSuccess && nobodyElse && unread === 0 && !open);
  $effect(() => {
    chatHandleInHeader.set(quiet);
  });
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

  <!-- Task 52: how many are online, on the handle, so "is anyone here to
       talk to?" is answered before opening it. Faded at 0, not hidden - an
       empty game is also an answer. -->
  {#if !quiet}
  <button class="handle" class:empty={nobodyElse} onclick={toggle} aria-label={open ? 'Hide chat' : `Show chat, ${onlineCount} online`}>
    <svg class="caret" class:up={!open} viewBox="0 0 12 12" aria-hidden="true">
      <path d="M2 4.5 L6 8.5 L10 4.5" fill="none" stroke="currentColor" stroke-width="1.8"
            stroke-linecap="round" stroke-linejoin="round" />
    </svg>
    Chat
    <span class="handle-online" title="{onlineCount} online">· {onlineCount}</span>
    {#if !open && unread > 0}
      <span class="dot" aria-label="{unread} unread">
        {unread > 9 ? '9+' : unread}
      </span>
    {/if}
  </button>
  {/if}
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
  /* Modul: THE BODY CLEARANCE LIVES IN app.css, ONCE.

     This block used to set `:global(body) { padding-bottom: 4.25rem }` while
     app.css set 4.5rem for the same reason, on the same element - two copies
     of one number, and whichever the bundler emitted last won. It mattered
     once the clearance had to carry the home-indicator inset too: a stray
     4.25rem here would have quietly dropped `var(--sa-bottom)` back off.

     app.css is the one that knows about both concerns. */

  .dock {
    position: fixed;
    right: calc(1rem + var(--sa-right));
    /* Fixed to the VIEWPORT, so body's padding does not move it - the handle
       would sit under the gesture bar on its own. */
    bottom: calc(1rem + var(--sa-bottom) + var(--tabbar-h));
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

  /* Modul: SIZED FROM WHAT IS LEFT, not from a guess at it.
     It was `min(26rem, 100vh - 8rem)`. With the soft keyboard up the APK's
     viewport shrinks to ~350px, and the dock already stands on the tab bar,
     the gesture inset, its 1rem offset, the 44px handle and the gap - so the
     window's top landed about 2px ABOVE the screen, under the status bar,
     with the header and its close button underneath the clock. Every term the
     dock stands on is subtracted now, plus the status bar and a margin.
     The vh line is the fallback for an engine without dvh. */
  .window {
    position: relative;
    isolation: isolate;
    width: min(30rem, calc(100vw - 2rem));
    height: min(26rem, calc(100vh - 8rem));
    height: min(
      26rem,
      calc(100dvh - var(--sa-top) - var(--sa-bottom) - var(--tabbar-h) - 1rem - 44px - 0.4rem - 0.5rem)
    );
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
     inside this ~416px box instead of the screen, offset and clipped by its
     overflow. They are portalled to <body> now as well (ui/portal.ts); this
     keeps the trap from being set for the next thing chat opens. A
     pseudo-element is not an ancestor of anything. */
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
    /* --bg-raised, not --bg-dark: the theme never defined --bg-dark, so the
       header had no background at all. Translucent like the window. */
    background: color-mix(in srgb, var(--bg-raised) 70%, transparent);
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

  .handle-online {
    color: var(--good);
    font-variant-numeric: tabular-nums;
  }

  .handle.empty {
    opacity: 0.6;
  }

  .handle.empty .handle-online {
    color: inherit;
  }

  .online-indicator {
    display: flex;
    align-items: center;
    gap: 0.25rem;
    font-size: 0.8rem;
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
    border: 1px solid var(--border);
    background: color-mix(in srgb, var(--bg-panel) 88%, transparent);
    backdrop-filter: blur(8px);
    cursor: pointer;
    font-size: 0.85rem;
    width: auto;
    box-shadow: 0 4px 14px rgba(0, 0, 0, 0.35);
  }

  /* Modul: TASK 108 - NO BLUR UNDER A THUMB. The handle is on screen all the
     time, over whatever is scrolling, and a backdrop-filter re-blurs that
     region on every scroll frame - on a phone, a cost paid continuously for a
     pill-sized frosted edge. Narrow screens get an almost opaque fill instead,
     which reads the same at that size. */
  @media (max-width: 40rem) {
    .handle {
      background: color-mix(in srgb, var(--bg-panel) 96%, transparent);
      backdrop-filter: none;
    }
  }

  .dot {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 1.15rem;
    height: 1.15rem;
    padding: 0 0.3rem;
    border-radius: 999px;
    background: var(--danger);
    color: #fff;
    font-size: 0.7rem;
    font-weight: 700;
    line-height: 1;
  }

  /* Modul: a caret drawn rather than typed. A glyph is a font's opinion about
     a shape - it differs by family, is not guaranteed to be present, and a
     screen reader announces it as "black down-pointing small triangle" in the
     middle of a label. `rotate` rather than `transform`, for the reason
     app.css records on button:active: transform is one property holding a
     whole list, so setting it here would replace whatever else used it. */
  .caret {
    width: 11px;
    height: 11px;
    flex: none;
    transition: rotate 140ms ease;
  }

  .caret.up {
    rotate: 180deg;
  }
</style>
