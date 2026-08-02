<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import {
    queryKeys,
    fetchFriends,
    fetchGuilds,
    fetchGuildRoster,
    fetchGuildApplications,
    approveGuildApplication,
    rejectGuildApplication,
    resolvePlayer,
    fetchPlayerNames,
  } from '../lib/net/rest';
  import {
    addFriend,
    removeFriend,
    blockPlayer,
    unblockPlayer,
    type CommandOutcome,
  } from '../lib/net/commands';
  import { pushLocalNotice } from '../lib/stores/game';
  import { api } from '../lib/net/config';
  import { storedToken } from '../lib/net/auth';
  import Skeleton from '../lib/ui/Skeleton.svelte';

  const client = useQueryClient();
  const friends = createQuery(() => ({ queryKey: queryKeys.friends, queryFn: fetchFriends }));
  const guilds = createQuery(() => ({ queryKey: queryKeys.guilds, queryFn: fetchGuilds }));
  const roster = createQuery(() => ({ queryKey: queryKeys.guildRoster, queryFn: fetchGuildRoster }));
  const applications = createQuery(() => ({
    queryKey: queryKeys.guildApplications,
    queryFn: fetchGuildApplications,
  }));

  function refreshFriends() {
    setTimeout(() => client.invalidateQueries({ queryKey: queryKeys.friends }), 600);
  }

  // --- friends --------------------------------------------------------------
  let friendName = $state('');
  let busy = $state(false);

  // Modul: the relationship commands take a numeric player id, but a player
  // knows a username - hence the resolve endpoint. Two steps rather than one
  // because the wire has no room for a name on the command packet.
  async function addByName() {
    const name = friendName.trim();
    if (!name) return;
    busy = true;
    try {
      const { PlayerId } = await resolvePlayer(name);
      if (!PlayerId) {
        pushLocalNotice(`No player called "${name}".`);
        return;
      }
      const outcome = addFriend(PlayerId);
      if (!outcome.ok) pushLocalNotice(outcome.reason);
      else friendName = '';
      refreshFriends();
    } catch {
      pushLocalNotice(`No player called "${name}".`);
    } finally {
      busy = false;
    }
  }

  function act(fn: (id: number) => CommandOutcome, playerId: number) {
    const outcome = fn(playerId);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
    refreshFriends();
  }

  // --- guilds ---------------------------------------------------------------
  // Guild create/join are HTTP POSTs, not WebSocket commands: a guild name is a
  // variable-length string and ClientCommandPacket's fixed layout has no field
  // for one. Same reason email/password auth uses HTTP.
  let newGuildName = $state('');

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
      // The endpoint reads `guildName`, not `name` - a mismatch here is a
      // bare 400 with no body, which says nothing about which field was wrong.
      const response = await post('/api/v1/guilds/create', { guildName: name });
      if (response.ok) pushLocalNotice(`Guild "${name}" created.`, 'info');
      else pushLocalNotice(`Could not create "${name}".`);
      if (response.ok) newGuildName = '';
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
        pushLocalNotice(`Could not join "${name}".`);
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

  async function reviewApplication(applicationId: number, approve: boolean) {
    busy = true;
    try {
      // Modul: `Success: false` arrives with HTTP 200 and MUST be checked.
      //
      // ApproveApplicationAsync returns it when the caller is not the leader,
      // when the guild is full, or when someone else already handled the
      // application - all normal outcomes, none of them an HTTP error. This
      // used to fire and forget, so a refusal looked exactly like an approval
      // and the only symptom was a roster that never grew.
      const result = approve
        ? await approveGuildApplication(applicationId)
        : await rejectGuildApplication(applicationId);

      if (result?.Success === false) {
        pushLocalNotice(
          approve
            ? 'Not approved - you may not be the leader, or the guild is full.'
            : 'Not rejected - it may already have been handled.',
        );
      } else {
        pushLocalNotice(approve ? 'Application approved.' : 'Application rejected.', 'info');
      }

      client.invalidateQueries({ queryKey: queryKeys.guildApplications });
      client.invalidateQueries({ queryKey: queryKeys.guildRoster });
    } catch {
      pushLocalNotice('Could not reach the server.');
    } finally {
      busy = false;
    }
  }

  const ROLE_NAMES: Record<number, string> = { 0: 'Member', 1: 'Officer', 2: 'Leader' };

  // The roster identifies members numerically, so names are resolved in one
  // batched request - the same shape chat uses, for the same reason.
  const rosterIds = $derived((roster.data ?? []).map((m) => m.PlayerId).sort());
  const rosterNames = createQuery(() => ({
    queryKey: queryKeys.playerNames(rosterIds),
    queryFn: () => fetchPlayerNames(rosterIds),
    enabled: rosterIds.length > 0,
    staleTime: 10 * 60_000,
  }));
  const rosterNameById = $derived(
    new Map((rosterNames.data ?? []).map((n) => [n.PlayerId, n.Username])),
  );
</script>

<div class="grid">
  <section class="panel">
    <h2>Friends</h2>

    <div class="adder">
      <input placeholder="Username" bind:value={friendName} onkeydown={(e) => e.key === 'Enter' && addByName()} />
      <button disabled={busy || !friendName.trim()} onclick={addByName}>Add</button>
    </div>

    {#if friends.isPending}
      <Skeleton />
    {:else if friends.isError}
      <p class="err">{friends.error?.message}</p>
    {:else if (friends.data ?? []).length === 0}
      <p class="dim">No friends yet.</p>
    {:else}
      <ul class="rows">
        {#each friends.data ?? [] as friend (friend.PlayerId)}
          <li>
            <span class="dot" class:online={friend.IsOnline} title={friend.IsOnline ? 'Online' : 'Offline'}></span>
            <span class="name" class:blocked={friend.IsBlocked}>{friend.Username}</span>
            <span class="dim tiny">lv {friend.Level}</span>
            {#if friend.IsBlocked}
              <button class="tiny-btn" onclick={() => act(unblockPlayer, friend.PlayerId)}>Unblock</button>
            {:else}
              <button class="tiny-btn" onclick={() => act(blockPlayer, friend.PlayerId)}>Block</button>
              <button class="tiny-btn" onclick={() => act(removeFriend, friend.PlayerId)}>Remove</button>
            {/if}
          </li>
        {/each}
      </ul>
    {/if}
  </section>

  <section class="panel">
    <h2>Guilds</h2>

    <div class="adder">
      <input placeholder="New guild name" bind:value={newGuildName} />
      <button disabled={busy || !newGuildName.trim()} onclick={createGuild}>Create</button>
    </div>

    {#if guilds.isPending}
      <Skeleton />
    {:else if (guilds.data ?? []).length === 0}
      <p class="dim">No guilds exist yet. Create the first.</p>
    {:else}
      <ul class="rows">
        {#each guilds.data ?? [] as guild (guild.GuildId)}
          <li class="guild">
            <span class="name">{guild.Name}</span>
            <span class="dim tiny">
              tier {guild.CurrentTier} &middot; {guild.ActiveMembers}/{guild.MaxMembers}
              &middot; {guild.TaxRatePct}% tax
              {#if guild.MinApplicationLevel > 0}&middot; lv {guild.MinApplicationLevel}+{/if}
            </span>
            <button
              class="tiny-btn"
              disabled={busy || guild.ActiveMembers >= guild.MaxMembers}
              onclick={() => joinGuild(guild.Name)}
            >
              {guild.JoinType === 0 ? 'Join' : 'Apply'}
            </button>
          </li>
        {/each}
      </ul>
    {/if}
  </section>

  <section class="panel">
    <h2>My guild</h2>

    {#if (roster.data ?? []).length === 0}
      <p class="dim">You are not in a guild. Trading needs one - it doubles as a trade licence.</p>
    {:else}
      <ul class="rows">
        {#each roster.data ?? [] as member (member.PlayerId)}
          <li>
            <span class="dot" class:online={member.IsOnline} title={member.IsOnline ? 'Online' : 'Offline'}></span>
            <span class="name">{rosterNameById.get(member.PlayerId) ?? `Player #${member.PlayerId}`}</span>
            <span class="dim tiny">{ROLE_NAMES[member.Role] ?? `Role ${member.Role}`}</span>
            <span class="dim tiny">{member.ContributionPoints.toLocaleString()} pts</span>
          </li>
        {/each}
      </ul>
    {/if}

    <h3>Applications</h3>
    <!-- Leader-only: the endpoint returns an empty list for everyone else
         rather than a 403, so an empty list here is not evidence of none. -->
    {#if (applications.data ?? []).length === 0}
      <p class="dim tiny">None pending, or you are not the leader.</p>
    {:else}
      <ul class="rows">
        {#each applications.data ?? [] as application (application.Id)}
          <li>
            <span class="name">{application.Username}</span>
            <span class="dim tiny">lv {application.ApplicantLevel}</span>
            <button class="tiny-btn" disabled={busy} onclick={() => reviewApplication(application.Id, true)}>
              Approve
            </button>
            <button class="tiny-btn" disabled={busy} onclick={() => reviewApplication(application.Id, false)}>
              Reject
            </button>
          </li>
        {/each}
      </ul>
    {/if}
  </section>
</div>

<style>
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(20rem, 1fr));
    gap: 1rem;
    padding: 1rem;
    align-items: start;
  }

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

  h3 {
    margin: 1.1rem 0 0.4rem;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  .dim {
    color: var(--text-dim);
  }
  .tiny {
    font-size: 0.72rem;
  }
  .err {
    color: var(--danger);
  }

  .adder {
    display: grid;
    grid-template-columns: 1fr auto;
    gap: 0.4rem;
    margin-bottom: 0.7rem;
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
    align-items: center;
    gap: 0.5rem;
    font-size: 0.85rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.3rem;
  }

  .rows li.guild {
    flex-wrap: wrap;
  }

  .name {
    font-weight: 600;
    margin-right: auto;
  }

  .name.blocked {
    text-decoration: line-through;
    color: var(--text-dim);
  }

  .dot {
    width: 0.5rem;
    height: 0.5rem;
    border-radius: 50%;
    background: var(--border);
    flex: none;
  }

  .dot.online {
    background: var(--good);
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.45rem;
    flex: none;
  }
</style>
