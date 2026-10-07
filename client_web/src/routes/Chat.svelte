<script lang="ts">
  import PlayerAvatar from '../lib/ui/PlayerAvatar.svelte';
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
  import QueryError from '../lib/ui/QueryError.svelte';
  import { openProfile } from '../lib/stores/profile';
  import { profileLink } from '../lib/ui/profileLink';
  import {
    queryKeys,
    fetchPlayerNames,
    resolvePlayer,
    fetchConversations,
    fetchConversationHistory,
    markConversationRead,
    fetchStatistics,
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
    // 'News', not 'Announcements': at 390px the four tabs only fit on one
    // row with a short label, and a second row of 44px tabs is a row of chat
    // the player does not get to see.
    { id: ANNOUNCEMENT, label: 'News' },
  ];

  let active = $state(GLOBAL);

  // Modul: TASK 95 - NO GUILD TAB WITHOUT A GUILD. The server filters guild
  // traffic by membership, so for the guildless the tab could only ever say
  // "Nothing in this channel yet" - a channel offered and then refused. Shown
  // while the answer is unknown, so a member's tab does not blink in.
  const statistics = createQuery(() => ({ queryKey: queryKeys.statistics, queryFn: fetchStatistics }));
  const guildless = $derived(statistics.isSuccess && (statistics.data?.GuildName ?? '') === '');
  const channels = $derived(CHANNELS.filter((c) => !(c.id === GUILD && guildless)));
  $effect(() => {
    if (guildless && active === GUILD) active = GLOBAL;
  });
  let draft = $state('');
  let whisperTarget = $state('');

  let contextMenuOpen = $state(false);
  let contextMenuX = $state(0);
  let contextMenuY = $state(0);
  let contextMenuUsername = $state('');
  let contextMenuPlayerId = $state(0);
  function openContextMenu(e: MouseEvent, username: string, playerId: number) {
    e.preventDefault();
    if (playerId <= 0) return;
    // Modul: YOUR OWN NAME OPENS YOUR PROFILE. Whisper, Add Friend and Block
    // mean nothing aimed at yourself, so there is no menu to show - but "what
    // do others see when they tap me" is a fair question, and it is the one
    // name in chat that is always there to tap.
    if (playerId === connection.currentPlayerId) {
      openProfile(playerId, username);
      return;
    }
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
      pushLocalNotice('Friend request sent.', 'info');
    } catch {
      pushLocalNotice('Failed to add friend.', 'error');
    }
  }

  async function handleBlock(playerId: number) {
    try {
      await blockPlayer(playerId);
      pushLocalNotice('Player blocked.', 'info');
    } catch {
      pushLocalNotice('Failed to block player.', 'error');
    }
  }

  function handleViewProfile(playerId: number, username: string) {
    openProfile(playerId, username);
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
    <div class="tabs" role="tablist" aria-label="Channels">
      {#each channels as channel (channel.id)}
        <button role="tab" aria-selected={active === channel.id} class:active={active === channel.id} onclick={() => (active = channel.id)}>
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
        {:else if conversations.isError && conversations.data === undefined}
          <QueryError query={conversations} what="your conversations" />
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
        {:else if threadHistory.isError && threadMessages.length === 0}
          <QueryError query={threadHistory} what="this conversation" />
        {:else if threadMessages.length === 0}
          <p class="dim empty">Nothing said yet. Say something.</p>
        {/if}
      {/if}
    {:else}
    <ul class="log">
      {#each visible as message (message.id)}
        {#if message.channelType === ANNOUNCEMENT}
          <!-- Modul: AN ANNOUNCEMENT IS A SYSTEM LINE, NOT A MESSAGE. It used
               to render exactly like a player's row - a name button reading
               "World" that opened nothing (id 0 is refused by the menu) beside
               the text. The server already names the player inside the line,
               so the button was a second, inert name. -->
          <li class="sys">
            <span class="time dim">{timeOf(message.atMs)}</span>
            <span class="text announcement">{message.text}</span>
            <button class="gz" title="Say gz! in world chat" onclick={congratulate}>gz!</button>
          </li>
        {:else}
          <li class="msg">
            <span class="time dim">{timeOf(message.atMs)}</span>
            <button
              class="who"
              class:self={message.senderPlayerId === connection.currentPlayerId}
              use:profileLink={{ playerId: message.senderPlayerId, open: false }}
              onclick={(e) => openContextMenu(e, displayName(message.senderPlayerId), message.senderPlayerId)}
            >
              <PlayerAvatar playerId={message.senderPlayerId} size="sm" />
              <span class="who-name">{displayName(message.senderPlayerId)}</span>
            </button>
            <span class="text">{message.text}</span>
          </li>
        {/if}
      {/each}
    </ul>

    {#if visible.length === 0}
      <p class="dim empty">
        Nothing in this channel yet.
        {#if active === GUILD}Guild messages only arrive if you are in a guild.{/if}
        {#if active === ANNOUNCEMENT}Rare drops, Legendary rerolls, boss clears and season results show up here.{/if}
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
        enterkeyhint="send"
      />
      <!-- Modul: ONE TAP SENDS (owner, APK). The first tap on Send only closed the
           keyboard: the press blurred the input, App drops `html.typing` on
           focusout and the tab bar/coach come back, the layout shifted under the
           finger, and the click that followed landed on nothing. Cancelling the
           press default keeps focus in the input (the keyboard stays up, which
           is also what you want while chatting) and click still fires. Both
           events, because Android's WebView moves focus on the synthesized
           mouse event as well as on the pointer one. -->
      <button
        onpointerdown={(e) => e.preventDefault()}
        onmousedown={(e) => e.preventDefault()}
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
    title={contextMenuUsername}
    onClose={() => (contextMenuOpen = false)}
    items={[
      { label: 'View Profile', onSelect: () => handleViewProfile(contextMenuPlayerId, contextMenuUsername) },
      { label: 'Whisper', onSelect: () => handleWhisper(contextMenuUsername) },
      { label: 'Add Friend', onSelect: () => handleAddFriend(contextMenuPlayerId) },
      { label: 'Block', danger: true, separated: true, onSelect: () => handleBlock(contextMenuPlayerId) },
    ]}
  />
{/if}


<style>
  /* Task 54: the face sits inside the name cell, so the grid keeps its columns.
     18px rather than the 28px 'sm' face: at 28 the avatar alone set every row's
     height, taller than the line of text beside it. */
  .who :global(.avatar) {
    width: 18px;
    height: 18px;
  }

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
    height: auto;
    overflow-y: auto;
  }

  /* Modul: TASK 95 - ONE FRAME, AND THE SENDER ABOVE THE MESSAGE, ON A PHONE.
     Docked on a phone the window is a full-height sheet (ChatDock.svelte), and
     the log's own border and darker well were a frame inside its frame. And a
     row of time | name | text gave a long name the width the message needed:
     the text column was crushed to a few characters a line. The name and time
     go on their own line now, and the message gets the whole width. */
  @media (max-width: 40rem) {
    .wrap.docked .log {
      border: none;
      background: transparent;
      padding: 0.2rem 0;
      gap: 0.35rem;
    }
    .wrap.docked .log li.msg {
      grid-template-columns: auto 1fr;
      grid-template-areas: 'who time' 'text text';
      row-gap: 0;
      align-items: baseline;
    }
    .wrap.docked .log li.msg .who {
      grid-area: who;
      max-width: 14rem;
    }
    .wrap.docked .log li.msg .time {
      grid-area: time;
    }
    .wrap.docked .log li.msg .text {
      grid-area: text;
    }
    .wrap.docked .threads {
      border: none;
      background: transparent;
      max-height: none;
      flex: 1;
      min-height: 0;
    }
  }

  .panel {
    max-width: 46rem;
  }

  /* Task 95: ONE row that scrolls sideways, never two. A wrapped second row of
     44px tabs was a row of chat the player did not get to see. */
  .tabs {
    display: flex;
    flex-wrap: nowrap;
    overflow-x: auto;
    scrollbar-width: none;
    gap: 0.25rem;
    margin-bottom: 0.6rem;
  }

  .tabs button {
    flex-shrink: 0;
  }

  .tabs button {
    background: transparent;
    border-color: transparent;
    color: var(--text-dim);
    font-size: var(--fs-sm);
    padding: 0.3rem 0.6rem;
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

  @media (hover: hover) and (pointer: fine) {
    .thread:hover {
      border-color: var(--border);
    }
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
    background: var(--danger);
    color: #fff;
    font-size: 0.65rem;
    line-height: 1.1rem;
    text-align: center;
  }

  .dot.online {
    width: 0.45rem;
    height: 0.45rem;
    border-radius: 50%;
    background: var(--good);
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

  /* Modul: CHAT DENSITY, 2026-10-02 - "the bubble with the player's name is
     too big, same for the chat text". The name was a full raised, bordered
     button with a 28px face in it, and on a phone app.css's 44px touch floor
     made every row at least 44px tall: about eight lines in the dock. The name
     is now plain bold text, the face 18px, the text --fs-sm, and on a phone the
     floor is kept by the BUTTON BOX rather than the ROW (see the media block
     below), so a one-line message is a ~28px row. */
  .log {
    list-style: none;
    margin: 0;
    padding: 0.4rem 0.5rem;
    display: flex;
    flex-direction: column-reverse;
    gap: 0.1rem;
    height: 22rem;
    overflow-y: auto;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    font-size: var(--fs-sm);
    line-height: var(--lh-snug);
  }

  .log li {
    display: grid;
    grid-template-columns: auto auto 1fr;
    gap: 0.4rem;
    align-items: center;
  }

  .time {
    font-size: var(--fs-badge);
    font-variant-numeric: tabular-nums;
  }

  .who {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    /* Text, not a chip: no fill, no border, no padding. */
    background: none;
    border: none;
    /* Modul: app.css gives every button a drop shadow. On this 44px box that
       overhangs its 28px row it drew a pale slab across the neighbouring rows. */
    box-shadow: none;
    border-radius: var(--radius-xs);
    padding: 0 0.1rem;
    font-weight: var(--fw-medium);
    line-height: inherit;
    color: var(--text);
    white-space: nowrap;
    /* A 20-character name must not take the message's width from it. */
    max-width: 9rem;
  }

  .who-name {
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .who.self {
    color: var(--accent);
  }

  .text {
    overflow-wrap: anywhere;
    min-width: 0;
  }

  /* An announcement is a system line: smaller, coloured, marked by an edge
     rather than drawn as somebody's message. */
  .log li.sys {
    grid-template-columns: auto 1fr auto;
    padding-left: 0.35rem;
    border-left: 2px solid var(--rarity-12);
    background: color-mix(in srgb, var(--rarity-12) 7%, transparent);
    border-radius: 0 var(--radius-xs) var(--radius-xs) 0;
    font-size: var(--fs-xs);
  }

  .text.announcement {
    color: var(--rarity-12);
  }

  .gz {
    padding: 0 0.35rem;
    font-size: var(--fs-badge);
    line-height: 1.4;
  }

  /* Modul: THE FLOOR IS ON THE BUTTON, NOT ON THE ROW. app.css gives every
     button a 44px min-height on a phone, and it stays: the name and gz! boxes
     still MEASURE 44px tall (check:touch reads the element's own box). The
     negative block margin only stops that box from setting the row's height,
     so it overhangs its 28px row by 8px each way instead of padding it out.
     The overhang lands on the row gap and the blank band above or below the
     neighbouring line's centred text, not on that row's name. A taller row
     (a wrapped message) only widens the clearance. */
  @media (max-width: 40rem) {
    .log li .who,
    .log li .gz {
      margin-block: -8px;
      flex-shrink: 0;
    }

    .log li {
      min-height: 28px;
    }
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
