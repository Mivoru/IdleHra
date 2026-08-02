<script lang="ts">
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

  const CHANNELS = [
    { id: GLOBAL, label: 'World' },
    { id: GUILD, label: 'Guild' },
    { id: WHISPER, label: 'Whispers' },
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
</script>

<div class="wrap">
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
          <span class="text">{message.text}</span>
        </li>
      {/each}
    </ul>

    {#if visible.length === 0}
      <p class="dim empty">
        Nothing in this channel yet.
        {#if active === GUILD}Guild messages only arrive if you are in a guild.{/if}
      </p>
    {/if}

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
  </section>
</div>

<style>
  .wrap {
    padding: 1rem;
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
    grid-template-columns: 3rem auto 1fr;
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
