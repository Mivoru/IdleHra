<script lang="ts">
  // Modul: this is the chat PANEL. It renders full-page on its own route and
  // inside the floating dock (see ChatDock.svelte) - the dock is where the
  // collapse state and the unread marker live, so this file stays a plain
  // channel view either way.
  let { docked = false }: { docked?: boolean } = $props();

  import { createQuery } from '@tanstack/svelte-query';
  import { chatLog, type ChatEntry } from '../lib/stores/game';
  import { connection } from '../lib/net/connection';
  import { queryKeys, fetchPlayerNames } from '../lib/net/rest';

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
  let whisperTarget = $state(0);

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
    // Modul: SENDER 0 IS THE SERVER, not a player.
    //
    // Announcements are authored by the system and carry SenderPlayerId 0 -
    // no real player has id 0, which is exactly how the client is meant to
    // tell them apart. This asked the name map anyway, missed, and fell back
    // to the id, so every announcement in the game was attributed to
    // "Player #0".
    if (playerId === 0) return 'World';
    if (playerId === connection.currentPlayerId) return 'You';
    return nameById.get(playerId) ?? `Player #${playerId}`;
  }

  function timeOf(atMs: number): string {
    return new Date(atMs).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  function send() {
    const text = draft.trim();
    if (!text) return;
    // A whisper needs a recipient; the server drops one with an invalid target
    // silently rather than disconnecting, but refusing here is clearer.
    if (active === WHISPER && whisperTarget <= 0) return;

    connection.sendChat(text, active, active === WHISPER ? whisperTarget : 0);
    draft = '';
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
        </button>
      {/each}
    </div>

    <ul class="log">
      {#each visible as message (message.id)}
        <li>
          <span class="time dim">{timeOf(message.atMs)}</span>
          <span class="who" class:self={message.senderPlayerId === connection.currentPlayerId}>
            {displayName(message.senderPlayerId)}
          </span>
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

    {#if active !== ANNOUNCEMENT}
    <div class="composer">
      {#if active === WHISPER}
        <input
          class="target"
          type="number"
          min="1"
          placeholder="Player id"
          bind:value={whisperTarget}
        />
      {/if}
      <input
        placeholder={active === GUILD ? 'Message your guild...' : 'Say something...'}
        bind:value={draft}
        onkeydown={(e) => e.key === 'Enter' && send()}
        maxlength="128"
      />
      <button onclick={send} disabled={!draft.trim() || (active === WHISPER && whisperTarget <= 0)}>
        Send
      </button>
    </div>
    <!-- RequestChatMessagePacket's MessageText is a fixed 128-byte buffer, so
         the input is bounded rather than truncated silently server-side. -->
    <p class="dim tiny">Up to 128 bytes per message.</p>
    {/if}
  </section>
</div>

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
