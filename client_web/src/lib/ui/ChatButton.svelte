<script lang="ts">
  // Modul: THE ONE WAY INTO CHAT (task 95).
  //
  // There used to be two, and which one a player saw depended on who else was
  // online: a floating handle in the bottom-right corner, and - when nobody
  // was - a small "Chat" button in the header instead (task 71). With the
  // window open both close controls showed, and the online dot beside the
  // count had no label, so "2" read as unread messages.
  //
  // Now there is this button, in exactly one place per layout: the header on a
  // desktop, the More sheet on a phone. It says how many people are online in
  // words and carries the unread count as a badge. It also stops the handle
  // floating over the last control of every screen, which the corner had been
  // doing since chat stopped being a tab.
  import { createQuery } from '@tanstack/svelte-query';
  import { fetchOnlineStats, queryKeys } from '../net/rest';
  import { chatDockOpen, chatUnread, setChatOpen } from '../stores/chatDock';

  interface Props {
    /** Called after the window is toggled - the More sheet closes itself. */
    onactivate?: () => void;
    class?: string;
  }

  const { onactivate, class: className = '' }: Props = $props();

  const onlineStatsQuery = createQuery(() => ({
    queryKey: queryKeys.onlineStats,
    queryFn: fetchOnlineStats,
    refetchInterval: 10000,
  }));
  const onlineCount = $derived(onlineStatsQuery.data?.OnlineCount ?? 0);
  const open = $derived($chatDockOpen);

  function toggle(): void {
    // Computed once: `open` is derived from the store and would read the new
    // value straight after the set.
    setChatOpen(!open);
    onactivate?.();
  }
</script>

<button
  class="chat-entry {className}"
  class:open
  data-chat-entry
  data-label="Chat"
  aria-label={open ? 'Hide chat' : `Show chat, ${onlineCount} online${$chatUnread > 0 ? `, ${$chatUnread} unread` : ''}`}
  onclick={toggle}
>
  <span class="name">Chat</span>
  {#if onlineStatsQuery.isSuccess}
    <span class="online" class:alone={onlineCount <= 1}>
      <span class="online-dot" aria-hidden="true"></span>{onlineCount} online
    </span>
  {/if}
  {#if $chatUnread > 0 && !open}
    <span class="unread" aria-hidden="true">{$chatUnread > 9 ? '9+' : $chatUnread}</span>
  {/if}
</button>

<style>
  .chat-entry {
    display: inline-flex;
    align-items: center;
    gap: 0.4rem;
    width: auto;
  }

  .chat-entry.open {
    border-color: var(--accent);
  }

  .online {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    font-size: var(--fs-xs);
    color: var(--text-dim);
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }

  .online-dot {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: var(--good);
  }

  /* The count includes you, so one online is nobody to talk to. */
  .online.alone .online-dot {
    background: var(--text-dim);
  }

  .unread {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 1.15rem;
    height: 1.15rem;
    padding: 0 0.3rem;
    border-radius: 999px;
    background: var(--danger);
    color: #fff;
    font-size: var(--fs-badge);
    font-weight: 700;
    line-height: 1;
  }
</style>
