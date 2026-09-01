<script lang="ts">
  // Modul: this is the chat PANEL. It renders full-page on its own route and
  // inside the floating dock (see ChatDock.svelte) - the dock is where the
  // collapse state and the unread marker live, so this file stays a plain
  // channel view either way.
  let { docked = false }: { docked?: boolean } = $props();

  import { createQuery } from '@tanstack/svelte-query';
  import { chatLog, type ChatEntry, pushLocalNotice } from '../lib/stores/game';
  import { connection } from '../lib/net/connection';
  import { addFriend, blockPlayer } from '../lib/net/commands';
  import ContextMenu from '../lib/ui/ContextMenu.svelte';
  import PlayerProfileModal from '../lib/ui/PlayerProfileModal.svelte';
  import {
    queryKeys,
    fetchPlayerNames,
    resolvePlayer,
    fetchConversations,
    fetchConversationHistory,
    markConversationRead,
  } from '../lib/net/rest';
  import { useQueryClient } from '@tanstack/svelte-query';

  // Modul: ChatEngine's channel numbering. Whisper is send-only from this
  // screen's point of view - an incoming whisper arrives tagged as the
  // channel it was published on, and the server filters guild traffic by
  // membership before it ever reaches us.
  const GLOBAL = 0;
  const GUILD = 1;
  const WHISPER = 2;
  // Modul: ANNOUNCEMENTS ARE A FOURTH CHANNEL, not global messages with
  // special text. ChatEngine gives them their own channel byte precisely so a
  // client can tell them apart without parsing - and a client that only knows
  // 0/1/2, as this screen originally did, drops every one of them on the floor
  // with nothing anywhere saying so.
  const ANNOUNCEMENT = 3;

  const CHANNELS = [
    { id: GLOBAL, label: 'World' },
    { id: GUILD, label: 'Guild' },
    { id: WHISPER, label: 'Whispers' },
    { id: ANNOUNCEMENT, label: 'Announcements' },
  ];

  let active = $state(GLOBAL);
  let draft = $state('');
  let whisperTarget = $state('');

  let contextMenuOpen = $state(false);
  let contextMenuX = $state(0);
  let contextMenuY = $state(0);
  let contextMenuUsername = $state('');
  let contextMenuPlayerId = $state(0);
  
  let inspectingPlayerId = $state<number | null>(null);

  function openContextMenu(e: MouseEvent, username: string, playerId: number) {
    e.preventDefault();
    // Do not open menu for yourself or system messages (id <= 0)
    if (playerId <= 0 || playerId === connection.currentPlayerId) return;
    contextMenuUsername = username;
    contextMenuPlayerId = playerId;
    contextMenuX = e.clientX;
    contextMenuY = e.clientY;
    contextMenuOpen = true;
  }

  // ---------------------------------------------------------------------
  // Conversations.
  //
  // Modul: the Whispers tab used to be ONE FLAT LOG filtered by channel, so
  // every whisper from everybody sat intermixed and the only thing telling
  // them apart was the name on each row. There was no thread, and after a
  // reload there was nothing at all - chat was never written down.
  //
  // This is a list of PEOPLE, then that person's history. The live socket
  // still delivers arrivals; these queries supply everything said before the
  // page was open, which is the half that did not exist.
  // ---------------------------------------------------------------------
  const client = useQueryClient();

  let openThreadWith = $state<number | null>(null);
  let openThreadName = $state('');

  const conversations = createQuery(() => ({
    queryKey: queryKeys.conversations,
    queryFn: fetchConversations,
    enabled: active === WHISPER,
    // Arrivals push into chatLog live, so this only has to catch what changed
    // while the tab was closed - and the unread counts other sessions cleared.
    refetchInterval: 30_000,
  }));

  const threadHistory = createQuery(() => ({
    queryKey: queryKeys.conversationHistory(openThreadWith ?? 0),
    queryFn: () => fetchConversationHistory(openThreadWith!),
    enabled: openThreadWith !== null,
  }));

  const totalUnread = $derived(
    (conversations.data ?? []).reduce((sum, c) => sum + c.UnreadCount, 0),
  );

  async function openThread(playerId: number, username: string) {
    openThreadWith = playerId;
    openThreadName = username;
    whisperTarget = username;

    // Clearing the badge is a write, so the list has to be refetched after it
    // rather than trusted to be stale-but-right.
    try {
      await markConversationRead(playerId);
      client.invalidateQueries({ queryKey: queryKeys.conversations });
    } catch {
      // A badge that stays lit is a cosmetic problem; refusing to open the
      // thread because it could not be cleared would not be.
    }
  }

  function closeThread() {
    openThreadWith = null;
    openThreadName = '';
  }

  // Modul: history from the server, PLUS anything that has arrived on the
  // socket since it was fetched. Without the second half a message sent or
  // received while the thread is open does not appear until a refetch, which
  // reads as the message having failed.
  const threadMessages = $derived.by(() => {
    if (openThreadWith === null) return [];
    const stored = (threadHistory.data ?? []).map((m) => ({
      key: `s${m.Id}`,
      mine: m.Mine,
      text: m.MessageText,
      atMs: m.SentAtEpochMs,
    }));
    const newest = stored.length > 0 ? stored[stored.length - 1].atMs : 0;

    const live = $chatLog
      .filter((m: ChatEntry) => m.channelType === WHISPER)
      .filter((m: ChatEntry) =>
        m.senderPlayerId === openThreadWith || m.senderPlayerId === connection.currentPlayerId)
      .filter((m: ChatEntry) => m.atMs > newest)
      .map((m: ChatEntry) => ({
        key: `l${m.id}`,
        mine: m.senderPlayerId === connection.currentPlayerId,
        text: m.text,
        atMs: m.atMs,
      }));

    return [...stored, ...live].sort((a, b) => a.atMs - b.atMs);
  });

  function handleWhisper(username: string) {
    active = WHISPER;
    whisperTarget = username;
    // Resolve the name to an id so the context menu lands in the THREAD
    // rather than merely pre-filling a composer, which is all it used to do.
    resolvePlayer(username)
      .then((r) => openThread(r.PlayerId, username))
      .catch(() => {
        /* Unknown name - the composer still works and will report it on send. */
      });
  }

  async function handleAddFriend(playerId: number) {
    try {
      await addFriend(playerId);
      pushLocalNotice('Friend request sent.');
    } catch {
      pushLocalNotice('Failed to add friend.');
    }
  }

  async function handleBlock(playerId: number) {
    try {
      await blockPlayer(playerId);
      pushLocalNotice('Player blocked.');
    } catch {
      pushLocalNotice('Failed to block player.');
    }
  }

  function handleViewProfile(playerId: number) {
    inspectingPlayerId = playerId;
  }

  const visible = $derived($chatLog.filter((m: ChatEntry) => m.channelType === active).slice(0, 200));

  // Modul: THE WIRE CARRIES NO SENDER NAME. ResponseChatMessagePacket has room
  // for a numeric SenderPlayerId and nothing else, so every row would read
  // "Player #1042" without this. Resolved in ONE batched request for whatever
  // ids are currently on screen rather than one per row - which is exactly why
  // the endpoint batches.
  const visibleIds = $derived([...new Set($chatLog.map((m: ChatEntry) => m.senderPlayerId))].sort());

  const names = createQuery(() => ({
    queryKey: queryKeys.playerNames(visibleIds),
    queryFn: () => fetchPlayerNames(visibleIds),
    enabled: visibleIds.length > 0,
    // Names effectively never change, so re-resolving them as the log grows
    // would be pure waste.
    staleTime: 10 * 60_000,
  }));

  const nameById = $derived(new Map((names.data ?? []).map((n) => [n.PlayerId, n.Username])));

  function displayName(playerId: number): string {
    if (playerId === -1) return 'Dev';
    if (playerId === 0) return 'World';
    if (playerId === connection.currentPlayerId) return 'You';
    return nameById.get(playerId) ?? `Player #${playerId}`;
  }

  function timeOf(atMs: number): string {
    return new Date(atMs).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }
  
  let resolveError = $state('');

  async function send() {
    const text = draft.trim();
    if (!text) return;
    resolveError = '';

    if (active === WHISPER) {
      // An open thread already knows the id, so no name lookup is needed - and
      // more importantly the message cannot land on a different person because
      // the target box was edited after the thread was opened.
      if (openThreadWith !== null) {
        connection.sendChat(text, active, openThreadWith);
        draft = '';
        // The socket echo appears immediately via threadMessages; this settles
        // the durable copy and moves the thread up the list.
        setTimeout(() => {
          client.invalidateQueries({ queryKey: queryKeys.conversations });
          client.invalidateQueries({ queryKey: queryKeys.conversationHistory(openThreadWith!) });
        }, 900);
        return;
      }

      if (!whisperTarget) return;
      try {
        const result = await resolvePlayer(whisperTarget);
        connection.sendChat(text, active, result.PlayerId);
        draft = '';
        // Starting a conversation from the name box opens it, so the reply
        // has somewhere to arrive.
        await openThread(result.PlayerId, whisperTarget);
        setTimeout(() => client.invalidateQueries({ queryKey: queryKeys.conversations }), 900);
      } catch (e) {
        resolveError = 'Player not found.';
      }
    } else {
      connection.sendChat(text, active, 0);
      draft = '';
    }
  }

  // Modul: the congratulate button. NOT a dedicated command - the Unity client
  // sends the literal string "gz!" on the Global channel, so it inherits the
  // ordinary chat rate limit and profanity path rather than needing its own.
  // Kept identical rather than "improved" into something friendlier, because
  // both clients write into the same world chat.
  function congratulate() {
    connection.sendChat('gz!', GLOBAL);
  }
</script>

<div class="wrap" class:docked>
  <section class="panel">
    <div class="tabs">
      {#each CHANNELS as channel}
        <button class:active={active === channel.id} onclick={() => (active = channel.id)}>
          {channel.label}
          <!-- The count sits on the tab because an unread whisper is otherwise
               invisible from any other channel. -->
          {#if channel.id === WHISPER && totalUnread > 0}
            <span class="badge">{totalUnread}</span>
          {/if}
        </button>
      {/each}
    </div>

    {#if active === WHISPER}
      {#if openThreadWith === null}
        <!-- The conversation list. One row per person, newest thread first,
             which is the order the server returns them in. -->
        <ul class="threads">
          {#each conversations.data ?? [] as convo (convo.PlayerId)}
            <li>
              <button class="thread" onclick={() => openThread(convo.PlayerId, convo.Username)}>
                <span class="who-line">
                  <span class="name">{convo.Username}</span>
                  {#if convo.IsOnline}<span class="dot online" title="Online"></span>{/if}
                  {#if convo.UnreadCount > 0}<span class="badge">{convo.UnreadCount}</span>{/if}
                </span>
                <span class="preview dim">
                  {convo.LastMessageWasMine ? 'You: ' : ''}{convo.LastMessage}
                </span>
                <span class="time dim tiny">{timeOf(convo.LastMessageAtEpochMs)}</span>
              </button>
            </li>
          {/each}
        </ul>
        {#if conversations.isPending}
          <p class="dim empty">Loading conversations...</p>
        {:else if (conversations.data ?? []).length === 0}
          <p class="dim empty">
            No conversations yet. Type a name below to start one, or use Whisper
            from a player's name in any channel.
          </p>
        {/if}
      {:else}
        <div class="threadhead">
          <button class="back" onclick={closeThread}>&larr; All</button>
          <strong>{openThreadName}</strong>
        </div>
        <ul class="log thread-log">
          {#each threadMessages as message (message.key)}
            <li class:mine={message.mine}>
              <span class="time dim">{timeOf(message.atMs)}</span>
              <span class="who" class:self={message.mine}>{message.mine ? 'You' : openThreadName}</span>
              <span class="text">{message.text}</span>
            </li>
          {/each}
        </ul>
        {#if threadHistory.isPending}
          <p class="dim empty">Loading history...</p>
        {:else if threadMessages.length === 0}
          <p class="dim empty">Nothing said yet. Say something.</p>
        {/if}
      {/if}
    {:else}
    <ul class="log">
      {#each visible as message (message.id)}
        <li>
          <span class="time dim">{timeOf(message.atMs)}</span>
          <button 
            class="who" 
            class:self={message.senderPlayerId === connection.currentPlayerId}
            onclick={(e) => openContextMenu(e, displayName(message.senderPlayerId), message.senderPlayerId)}
          >
            {displayName(message.senderPlayerId)}
          </button>
          <span class="text" class:announcement={message.channelType === ANNOUNCEMENT}>
            {message.text}
          </span>
          {#if message.channelType === ANNOUNCEMENT && message.senderPlayerId !== connection.currentPlayerId}
            <button class="gz" title="Say gz! in world chat" onclick={congratulate}>gz!</button>
          {/if}
        </li>
      {/each}
    </ul>

    {#if visible.length === 0}
      <p class="dim empty">
        Nothing in this channel yet.
        {#if active === GUILD}Guild messages only arrive if you are in a guild.{/if}
        {#if active === ANNOUNCEMENT}High-rarity drops across the world show up here.{/if}
      </p>
    {/if}
    {/if}

    {#if active !== ANNOUNCEMENT}
    <div class="composer">
      <!-- Only when STARTING one. Inside a thread the recipient is already
           decided, and leaving an editable name box there invites a message
           addressed to whoever was typed last rather than to the person on
           screen. -->
      {#if active === WHISPER && openThreadWith === null}
        <input
          class="target"
          type="text"
          placeholder="Player username"
          bind:value={whisperTarget}
        />
      {/if}
      <input
        placeholder={active === GUILD
          ? 'Message your guild...'
          : active === WHISPER && openThreadWith !== null
            ? `Message ${openThreadName}...`
            : 'Say something...'}
        bind:value={draft}
        onkeydown={(e) => e.key === 'Enter' && send()}
        maxlength="128"
      />
      <button
        onclick={send}
        disabled={!draft.trim() || (active === WHISPER && openThreadWith === null && !whisperTarget.trim())}
      >
        Send
      </button>
    </div>
    {#if resolveError}
      <p class="warn tiny" style="margin-top: 0.5rem; text-align: right;">{resolveError}</p>
    {/if}
    <!-- RequestChatMessagePacket's MessageText is a fixed 128-byte buffer, so
         the input is bounded rather than truncated silently server-side. -->
    <p class="dim tiny">Up to 128 bytes per message.</p>
    {/if}
  </section>
</div>

{#if contextMenuOpen}
  <ContextMenu
    x={contextMenuX}
    y={contextMenuY}
    username={contextMenuUsername}
    playerId={contextMenuPlayerId}
    onClose={() => (contextMenuOpen = false)}
    onWhisper={handleWhisper}
    onAddFriend={handleAddFriend}
    onBlock={handleBlock}
    onViewProfile={handleViewProfile}
  />
{/if}

{#if inspectingPlayerId !== null}
  <PlayerProfileModal 
    playerId={inspectingPlayerId} 
    onClose={() => (inspectingPlayerId = null)} 
  />
{/if}

<style>
  .wrap {
    padding: 1rem;
  }

  /* Inside the dock the panel IS the window, so it drops its own page
     padding, its border and its background - the dock supplies all three,
     translucent. */
  .wrap.docked {
    padding: 0;
    height: 100%;
  }

  .wrap.docked .panel {
    background: transparent;
    border: none;
    border-radius: 0;
    padding: 0.6rem 0.75rem;
    height: 100%;
    display: flex;
    flex-direction: column;
  }

  .wrap.docked .log {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
    max-width: 46rem;
  }

  .tabs {
    display: flex;
    flex-wrap: wrap;
    gap: 0.25rem;
    margin-bottom: 0.6rem;
  }

  .tabs button {
    background: transparent;
    border-color: transparent;
    color: var(--text-dim);
    font-size: 0.85rem;
    padding: 0.3rem 0.7rem;
  }

  .tabs button.active {
    background: var(--bg-raised);
    border-color: var(--border);
    color: var(--text);
  }

  /* Modul: the channel log is newest-FIRST in the store and flipped visually
     by column-reverse. A thread is read the other way round - it comes back
     oldest-first, in the order it was said - so it opts out rather than being
     re-sorted to suit a style rule. */
  .thread-log {
    flex-direction: column !important;
    justify-content: flex-end;
  }

  .threads {
    list-style: none;
    margin: 0;
    padding: 0.35rem;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    max-height: 22rem;
    overflow-y: auto;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
  }

  .thread {
    width: 100%;
    display: grid;
    grid-template-columns: 1fr auto;
    grid-template-areas: 'who time' 'preview time';
    gap: 0.1rem 0.5rem;
    text-align: left;
    padding: 0.45rem 0.55rem;
    background: none;
    border: 1px solid transparent;
    border-radius: var(--radius);
  }

  .thread:hover {
    border-color: var(--border);
  }

  .thread .who-line {
    grid-area: who;
    display: flex;
    align-items: center;
    gap: 0.35rem;
  }

  .thread .name {
    font-weight: 600;
  }

  .thread .preview {
    grid-area: preview;
    /* One line. A preview that wraps turns the list into a log again, which is
       the thing this replaced. */
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: 0.8rem;
  }

  .thread .time {
    grid-area: time;
    align-self: start;
  }

  .badge {
    display: inline-block;
    min-width: 1.1rem;
    padding: 0 0.25rem;
    border-radius: 999px;
    background: var(--danger, #b34);
    color: #fff;
    font-size: 0.65rem;
    line-height: 1.1rem;
    text-align: center;
  }

  .dot.online {
    width: 0.45rem;
    height: 0.45rem;
    border-radius: 50%;
    background: var(--ok, #4b8);
    display: inline-block;
  }

  .threadhead {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.35rem 0.1rem;
  }

  .threadhead .back {
    background: none;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.15rem 0.4rem;
    font-size: 0.75rem;
  }

  .log {
    list-style: none;
    margin: 0;
    padding: 0.5rem;
    display: flex;
    flex-direction: column-reverse;
    gap: 0.2rem;
    height: 22rem;
    overflow-y: auto;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    font-size: 0.85rem;
  }

  .log li {
    display: grid;
    grid-template-columns: 3rem auto 1fr auto;
    gap: 0.5rem;
    align-items: baseline;
  }

  .time {
    font-size: 0.7rem;
    font-variant-numeric: tabular-nums;
  }

  .who {
    font-weight: 700;
    white-space: nowrap;
  }

  .who.self {
    color: var(--accent);
  }

  .text {
    overflow-wrap: anywhere;
  }

  .text.announcement {
    color: var(--rarity-12);
  }

  .gz {
    padding: 0 0.35rem;
    font-size: 0.7rem;
    line-height: 1.4;
  }

  .composer {
    display: flex;
    gap: 0.4rem;
    margin-top: 0.6rem;
  }

  .composer input {
    flex: 1;
  }

  .composer .target {
    flex: none;
    width: 7rem;
  }

  input {
    font: inherit;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.45rem 0.6rem;
  }

  .dim {
    color: var(--text-dim);
  }
  .tiny {
    font-size: 0.72rem;
    margin: 0.35rem 0 0;
  }
  .empty {
    margin: 0.5rem 0 0;
    font-size: 0.85rem;
  }
</style>
