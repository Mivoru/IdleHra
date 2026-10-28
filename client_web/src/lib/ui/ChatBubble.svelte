<script lang="ts">
  // Modul: THE CHAT BUBBLE IS BACK, ON A PHONE ONLY (owner, 2026-10-28: chat
  // "only through the burger menu is impractical").
  //
  // Task 95 took the old floating handle away for two reasons, and this one is
  // built around both:
  //  - it sat in the bottom-RIGHT corner, over the last control of nearly
  //    every screen. This one sits bottom-LEFT, and body's bottom padding
  //    (app.css, "chat-dock clearance") already keeps a screen's last control
  //    scrollable out from under it - check:overlap measures that;
  //  - with the window open, two close controls showed. This one is gone
  //    while the window is open; the window's own control closes it.
  // A desktop has the header's Chat button and does not get this.
  //
  // It fits UNDER the onboarding coach: on a phone the coach already stands
  // 4.25rem above the tab bar to leave the old handle's slot free
  // (OnboardingCoach.svelte), and a 3rem bubble 0.75rem up fits in it.
  import { chatDockOpen, chatUnread, setChatOpen } from '../stores/chatDock';

  interface Props {
    /** The More sheet is open: the bubble would sit inert under its scrim. */
    covered?: boolean;
  }

  const { covered = false }: Props = $props();

  const open = $derived($chatDockOpen);
  const unread = $derived($chatUnread);
</script>

{#if !open && !covered}
  <button
    class="chat-bubble"
    data-testid="chat-bubble"
    aria-label={`Show chat${unread > 0 ? `, ${unread} unread` : ''}`}
    onclick={() => setChatOpen(true)}
  >
    <svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round">
      <path d="M4 5.5h16v10.5H10l-4.5 3.5V16H4z" />
      <path d="M8 9.5h8M8 12.5h5" />
    </svg>
    {#if unread > 0}
      <span class="unread" aria-hidden="true">{unread > 9 ? '9+' : unread}</span>
    {/if}
  </button>
{/if}

<style>
  .chat-bubble {
    display: none;
  }

  @media (max-width: 40rem) {
    .chat-bubble {
      position: fixed;
      left: calc(0.75rem + var(--sa-left));
      bottom: calc(0.75rem + var(--sa-bottom) + var(--tabbar-h));
      /* Under the coach (40) and the chat window (40, later in the DOM), over
         the page. */
      z-index: 39;
      display: grid;
      place-items: center;
      width: 3rem;
      height: 3rem;
      padding: 0;
      border-radius: 50%;
      border: 1px solid color-mix(in srgb, var(--sec-community) 70%, #fff);
      background-color: var(--sec-community-deep);
      background-image: linear-gradient(180deg, var(--sec-community), var(--sec-community-deep));
      color: #fff;
      box-shadow:
        inset 0 1px 0 rgba(255, 255, 255, 0.25),
        0 3px 10px rgba(0, 0, 0, 0.45);
    }

    :global(html.typing) .chat-bubble {
      display: none;
    }
  }

  .unread {
    position: absolute;
    top: -0.25rem;
    right: -0.25rem;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 1.2rem;
    height: 1.2rem;
    padding: 0 0.3rem;
    border-radius: 999px;
    border: 1.5px solid var(--bg-panel);
    background: var(--danger);
    color: #fff;
    font-size: var(--fs-badge);
    font-weight: 700;
    line-height: 1;
  }
</style>
