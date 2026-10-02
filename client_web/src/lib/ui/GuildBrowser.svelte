<script lang="ts">
  // Modul: task 107. The guild directory and Create used to sit on the Friends
  // tab, two-thirds of a tab called Friends, next to a "My guild" roster that
  // duplicated the Guild tab. They live where a guildless player looks for them
  // now: the Guild tab shows this browser instead of the dashboard until the
  // player belongs to one. Only ever rendered for a player who is in no guild,
  // so there is no "your own guild" row to special-case.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { queryKeys, fetchGuilds } from '../net/rest';
  import { pushLocalNotice, playerState } from '../stores/game';
  import { GUILD_JOIN_MIN_LEVEL } from './unlocks';
  import { api } from '../net/config';
  import { storedToken } from '../net/auth';
  import Skeleton from './Skeleton.svelte';
  import QueryError from './QueryError.svelte';

  const client = useQueryClient();
  const guilds = createQuery(() => ({ queryKey: queryKeys.guilds, queryFn: fetchGuilds }));

  let busy = $state(false);
  let newGuildName = $state('');

  // Modul: WHY A JOIN IS GREY, in its label. Join used to check only "full",
  // so a level-1 player saw a live Join on a "lv 20+" guild and got a bare
  // "Could not join" from the server, which gates on
  // max(MinGuildInteractionLevel, MinApplicationLevel). The level is unknown
  // until the first snapshot; nothing is greyed on a guess.
  const myLevel = $derived(Number($playerState?.CurrentLevel ?? 0));
  function joinBlock(guild: { ActiveMembers: number; MaxMembers: number; MinApplicationLevel: number }): string | null {
    if (guild.ActiveMembers >= guild.MaxMembers) return 'Full';
    const needed = Math.max(GUILD_JOIN_MIN_LEVEL, guild.MinApplicationLevel);
    if (myLevel > 0 && myLevel < needed) return `Needs level ${needed}`;
    return null;
  }

  // Guild create/join are HTTP POSTs, not WebSocket commands: a guild name is a
  // variable-length string and ClientCommandPacket's fixed layout has no field
  // for one. Same reason email/password auth uses HTTP.
  async function post(path: string, body: unknown): Promise<Response> {
    return fetch(api(path), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${storedToken()}` },
      body: JSON.stringify(body),
    });
  }

  function refreshGuilds() {
    client.invalidateQueries({ queryKey: queryKeys.guilds });
    client.invalidateQueries({ queryKey: queryKeys.guildRoster });
    client.invalidateQueries({ queryKey: queryKeys.statistics });
  }

  async function createGuild() {
    const name = newGuildName.trim();
    if (!name) return;
    busy = true;
    try {
      // The endpoint reads `guildName`, not `name` - a mismatch is a bare 400
      // with no body, which says nothing about which field was wrong.
      const response = await post('/api/v1/guilds/create', { guildName: name });
      if (response.ok) {
        pushLocalNotice(`Guild "${name}" created.`, 'info');
        newGuildName = '';
      } else {
        // The endpoint answers a refusal with { reason }, including the level
        // gate, so surface it instead of a generic "Could not create".
        const reason = await response
          .json()
          .then((body: { Reason?: string; reason?: string }) => body.Reason ?? body.reason ?? '')
          .catch(() => '');
        pushLocalNotice(reason || `Could not create "${name}".`, 'error');
      }
      refreshGuilds();
    } finally {
      busy = false;
    }
  }

  async function joinGuild(name: string) {
    busy = true;
    try {
      const response = await post('/api/v1/guilds/join', { guildName: name });
      if (!response.ok) {
        pushLocalNotice(`Could not join "${name}".`, 'error');
      } else {
        const body = await response.json().catch(() => ({ Joined: false }));
        // Application-required guilds file a request instead of joining, and
        // saying "joined" for that would be a lie the player only discovers
        // when the roster stays empty.
        pushLocalNotice(body.Joined ? `Joined "${name}".` : `Application sent to "${name}".`, 'info');
      }
      refreshGuilds();
      client.invalidateQueries({ queryKey: queryKeys.guildApplications });
    } finally {
      busy = false;
    }
  }
</script>

<section class="panel">
  <h2>Join a guild</h2>
  <p class="dim tiny">
    A guild is your trade licence and unlocks shared buffs. You can belong to one at a time.
  </p>

  {#if guilds.isPending}
    <Skeleton />
  {:else if guilds.isError}
    <QueryError query={guilds} what="the guild list" />
  {:else if (guilds.data ?? []).length === 0}
    <p class="dim">No guilds exist yet. Found the first one below.</p>
  {:else}
    <ul class="rows">
      {#each guilds.data ?? [] as guild (guild.GuildId)}
        {@const block = joinBlock(guild)}
        <li class="guild">
          <span class="name">{guild.Name}</span>
          <button class="tiny-btn" disabled={busy || block !== null} onclick={() => joinGuild(guild.Name)}>
            {block ?? (guild.JoinType === 0 ? 'Join' : 'Apply')}
          </button>
          <span class="dim tiny meta">
            tier {guild.CurrentTier} &middot; {guild.ActiveMembers}/{guild.MaxMembers}
            &middot; {guild.TaxRatePct}% tax
            {#if guild.MinApplicationLevel > 0}&middot; lv {guild.MinApplicationLevel}+{/if}
          </span>
        </li>
      {/each}
    </ul>
  {/if}
</section>

<section class="panel">
  <h2>Found a guild</h2>
  <div class="adder">
    <!-- Modul: 32, the same as the server's cap
         (GuildManagementEngine.MaxGuildNameLength, task 94). A 100-character
         unbroken name overflowed toasts and rosters. -->
    <input placeholder="New guild name" maxlength="32" bind:value={newGuildName} />
    <button disabled={busy || !newGuildName.trim()} onclick={createGuild}>Create</button>
  </div>
</section>

<style>
  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
  }
  h2 {
    margin: 0 0 0.6rem;
    font-size: 1.05rem;
  }
  .dim {
    color: var(--text-dim);
  }
  .tiny {
    font-size: 0.72rem;
    margin: 0 0 0.6rem;
  }
  .adder {
    display: grid;
    grid-template-columns: 1fr auto;
    gap: 0.4rem;
  }
  input {
    font: inherit;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.42rem 0.55rem;
    width: 100%;
  }
  .rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
    max-height: 26rem;
    overflow-y: auto;
  }
  .rows li {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.2rem 0.5rem;
    font-size: 0.85rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.3rem;
  }
  .name {
    font-weight: 600;
    margin-right: auto;
    overflow-wrap: anywhere;
  }
  .meta {
    flex-basis: 100%;
    margin: 0;
  }
</style>
