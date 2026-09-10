<script lang="ts">
  // Modul: A SPINNER IS NOT AN ANSWER TO A DROPPED PHONE SIGNAL.
  //
  // The whole no-network state used to be two things: the word "reconnecting"
  // in the header, and a thin banner repeating it with an attempt count. On a
  // desktop that is proportionate - a browser tab either has a network or the
  // machine does not. A phone loses signal several times an hour, in a lift,
  // on a train, walking into a shop, and each time the player is left reading
  // a status word with no idea whether their idle game is still earning.
  //
  // What this adds is PRESENTATION ONLY. It starts no timers, opens no
  // sockets and knows nothing about backoff: connection.ts already reconnects
  // with jittered exponential backoff and lifecycle.ts already forces a
  // reconnect when the app returns to the foreground. Adding a second
  // mechanism here would be a second source of one truth, which is this
  // codebase's dominant bug class. The Retry button calls the one that exists.
  //
  // IT CLEARS ITSELF. Everything below is derived from `connectionStatus`, so
  // the moment the phase goes back to `live` the panel is gone - there is no
  // dismiss button and no reload, because a state the player has to
  // acknowledge is a state that outlives the problem it described.
  import { untrack } from 'svelte';
  import { connectionStatus } from '../stores/game';
  import { connection } from '../net/connection';
  import {
    describeConnection,
    shouldShowConnectionPanel,
    CONNECTION_GRACE_MS,
  } from './connectionMessage';

  const status = $derived($connectionStatus);

  // Modul: `navigator.onLine` is read through an event rather than polled, and
  // it is only ever used to choose WORDS. See connectionMessage.ts for why it
  // may not gate anything.
  let online = $state(true);
  $effect(() => {
    if (typeof window === 'undefined') return;
    online = navigator.onLine !== false;
    const up = () => (online = true);
    const down = () => (online = false);
    window.addEventListener('online', up);
    window.addEventListener('offline', down);
    return () => {
      window.removeEventListener('online', up);
      window.removeEventListener('offline', down);
    };
  });

  // How long the session has been off the air. Reset to 0 whenever the phase
  // is one the panel never shows for, so a brief blip starts the grace period
  // from scratch rather than inheriting the last outage's clock.
  let disconnectedSince = $state(0);
  // Modul: a state variable rather than reading Date.now() in the $derived.
  // A derived recomputes only when something it READS changes, and time is not
  // one of those things - without this tick, a connection that dropped and
  // then sat still would never cross the grace threshold and the panel would
  // never appear at all. This is the "computed but never consumed" trap with
  // the clock as the missing dependency.
  let now = $state(0);

  $effect(() => {
    const phase = status.phase;
    const hidden = phase === 'live' || phase === 'idle' || phase === 'signedout';

    if (hidden) {
      disconnectedSince = 0;
      now = 0;
      return;
    }

    // Modul: read through untrack, because this effect WRITES it. An effect
    // that both reads and writes one $state re-runs itself forever
    // (`effect_update_depth_exceeded`), and the read here is only asking "have
    // I already stamped this outage?" - it is not a dependency.
    const started = untrack(() => disconnectedSince) || Date.now();
    disconnectedSince = started;
    now = Date.now();

    // One timer, armed for the moment the grace period expires. Not an
    // interval: nothing on the panel counts up, so there is nothing to
    // re-render between "not yet" and "now".
    const remaining = started + CONNECTION_GRACE_MS - Date.now();
    if (remaining <= 0) return;
    const timer = setTimeout(() => (now = Date.now()), remaining + 20);
    return () => clearTimeout(timer);
  });

  const visible = $derived(
    disconnectedSince > 0 &&
      shouldShowConnectionPanel(status.phase, Math.max(0, now - disconnectedSince)),
  );

  const message = $derived(describeConnection(status.phase, status.attempt, online));

  function retryNow() {
    // The existing reconnect, asked to stop waiting. `resumeFromBackground`
    // clears the backoff timer and reopens a socket that is closed or stale -
    // exactly what a player pressing "Try again" is asking for.
    connection.resumeFromBackground();
  }
</script>

{#if visible}
  <!-- role="status" + aria-live so a screen reader announces the outage once,
       without stealing focus from whatever the player was doing. -->
  <div class="notice" data-tone={message.tone} role="status" aria-live="polite">
    <span class="mark" aria-hidden="true">
      {#if message.tone === 'stuck'}
        <!-- Signal bars with a stroke through them. Inline SVG rather than a
             glyph: this repo ships no text emoji, and an icon font would be a
             request that can fail in exactly the state this panel describes. -->
        <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor"
             stroke-width="2" stroke-linecap="round">
          <path d="M4 20v-3" />
          <path d="M10 20v-6" />
          <path d="M16 20v-9" />
          <path d="M3 3l18 18" />
        </svg>
      {:else}
        <!-- An open arc: the same "working on it" the platform uses, drawn
             rather than spelled. -->
        <svg class="spin" viewBox="0 0 24 24" width="22" height="22" fill="none"
             stroke="currentColor" stroke-width="2" stroke-linecap="round">
          <path d="M12 3a9 9 0 1 1-6.4 2.6" />
        </svg>
      {/if}
    </span>

    <span class="words">
      <strong class="title">{message.title}</strong>
      <span class="body">{message.body}</span>
      {#if status.detail}
        <span class="detail">{status.detail}</span>
      {/if}
    </span>

    {#if message.showRetry}
      <button class="retry" onclick={retryNow}>Try again</button>
    {/if}
  </div>
{/if}

<style>
  /* Modul: STICKY, AND IN FLOW - not fixed.
     Fixed would have been the obvious choice for something that must stay
     visible on a scrolled phone, and it would have put an out-of-flow box on
     top of whatever the screen renders at that position, which is precisely
     the class of defect check:overlap exists to find. Sticky reserves its own
     height in the document, so nothing is ever covered at rest, and it still
     follows the player down a long screen. */
  .notice {
    position: sticky;
    top: 0;
    /* Below the modal cards (50-60) and the chat dock (40), above ordinary
       page content. It is information, not an interruption. */
    z-index: 30;
    display: flex;
    align-items: center;
    gap: 0.7rem;
    padding: 0.6rem 1rem;
    background: var(--bg-panel, #1b1713);
    border-bottom: 1px solid var(--border, rgba(255, 255, 255, 0.12));
    font-size: 0.85rem;
  }

  .notice[data-tone='working'] {
    box-shadow: inset 3px 0 0 var(--accent, #c9a227);
  }

  .notice[data-tone='stuck'] {
    background: rgba(224, 85, 63, 0.16);
    border-bottom-color: var(--danger, #e0553f);
    box-shadow: inset 3px 0 0 var(--danger, #e0553f);
  }

  .mark {
    display: inline-flex;
    flex: none;
    color: var(--text-dim);
  }

  .notice[data-tone='stuck'] .mark {
    color: var(--danger, #e0553f);
  }

  .spin {
    animation: notice-spin 1.1s linear infinite;
  }

  @keyframes notice-spin {
    to {
      rotate: 360deg;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .spin {
      animation: none;
    }
  }

  .words {
    display: flex;
    flex-direction: column;
    gap: 0.1rem;
    min-width: 0;
  }

  .title {
    letter-spacing: 0.02em;
  }

  .body {
    color: var(--text-dim);
    line-height: 1.3;
  }

  /* The raw close reason. Useful in a bug report, noise to everybody else, so
     it is smaller and quieter than the sentence that answers the question. */
  .detail {
    color: var(--text-dim);
    opacity: 0.7;
    font-size: 0.75rem;
    line-height: 1.25;
  }

  .retry {
    flex: none;
    margin-left: auto;
    /* Modul: the floor is stated here, not left to app.css. That rule reaches
       a bare `button` at (0,1,1) and this component's own scoped selector
       outranks it - the same specificity trap that made the header's menu
       button 37px on all twenty-six screens. */
    min-height: 44px;
    min-width: 44px;
    padding: 0 0.9rem;
  }

  /* At phone width the row becomes a block: three things side by side put the
     sentence into a 12-character column and the button off the edge. */
  @media (max-width: 40rem) {
    .notice {
      flex-wrap: wrap;
      align-items: flex-start;
      padding: 0.7rem 0.8rem;
    }

    .words {
      /* Takes the rest of the first row beside the icon; the button wraps
         underneath it and spans the full width, which is where a thumb is. */
      flex: 1 1 0;
    }

    .retry {
      margin-left: 0;
      flex: 1 0 100%;
    }
  }
</style>
