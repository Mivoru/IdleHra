<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import {
    queryKeys,
    fetchFriends,
    resolvePlayer,
  } from '../lib/net/rest';
  import {
    addFriend,
    removeFriend,
    blockPlayer,
    unblockPlayer,
    type CommandOutcome,
  } from '../lib/net/commands';
  import { pushLocalNotice } from '../lib/stores/game';
  import ConfirmButton from '../lib/ui/ConfirmButton.svelte';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import QueryError from '../lib/ui/QueryError.svelte';
  import { profileLink } from '../lib/ui/profileLink';

  const client = useQueryClient();
  const friends = createQuery(() => ({ queryKey: queryKeys.friends, queryFn: fetchFriends }));

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
        pushLocalNotice(`No player called "${name}".`, 'error');
        return;
      }
      const outcome = addFriend(PlayerId);
      if (!outcome.ok) pushLocalNotice(outcome.reason, 'error');
      else friendName = '';
      refreshFriends();
    } catch {
      pushLocalNotice(`No player called "${name}".`, 'error');
    } finally {
      busy = false;
    }
  }

  function act(fn: (id: number) => CommandOutcome, playerId: number) {
    const outcome = fn(playerId);
    if (!outcome.ok) pushLocalNotice(outcome.reason, 'error');
    refreshFriends();
  }
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
      <QueryError query={friends} what="your friends" />
    {:else if (friends.data ?? []).length === 0}
      <p class="dim">No friends yet.</p>
    {:else}
      <ul class="rows">
        {#each friends.data ?? [] as friend (friend.PlayerId)}
          <li>
            <span class="dot" class:online={friend.IsOnline} title={friend.IsOnline ? 'Online' : 'Offline'}></span>
            <button 
              class="name-btn" 
              class:blocked={friend.IsBlocked}
              data-testid="friend-name"
              use:profileLink={{ playerId: friend.PlayerId, name: friend.Username }}
              title="View profile"
            >
              {friend.Username}
            </button>
            <span class="dim tiny">lv {friend.Level}</span>
            {#if friend.IsBlocked}
              <button class="tiny-btn" onclick={() => act(unblockPlayer, friend.PlayerId)}>Unblock</button>
            {:else}
              <!-- Modul: two taps each. Both were one, and both drop a
                   friendship a mis-tap cannot restore without asking again. -->
              <ConfirmButton small label="Block" confirmLabel="Really block?" onConfirm={() => act(blockPlayer, friend.PlayerId)} />
              <ConfirmButton small label="Remove" confirmLabel="Really remove?" onConfirm={() => act(removeFriend, friend.PlayerId)} />
            {/if}
          </li>
        {/each}
      </ul>
    {/if}
  </section>
</div>

<style>
  .grid {
    display: grid;
    grid-template-columns: minmax(0, 40rem);
    justify-content: center;
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

  .dim {
    color: var(--text-dim);
  }
  .tiny {
    font-size: 0.72rem;
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

  .name {
    font-weight: 600;
    margin-right: auto;
  }
  
  /* Modul: THE NAME SITS IN THE CORNER OF AN INVISIBLE 44px BOX.
     Reported from a phone as "the name is not in the center of it".

     This is styled as bare text - no border, no background, `padding: 0` - but
     it is still a <button>, so below 40rem app.css's touch floor gives it
     `min-height: 44px` and `min-width: 44px`. A short name like "rajus" is
     about 35px wide, so the control is larger than its label in BOTH axes, and
     `text-align: left` with no padding pinned that label to the top-left of the
     empty space around it.

     The floor is right and stays - it is why the name is tappable with a thumb
     at all. What was wrong is that nothing centred the label inside the box the
     floor created. inline-flex does it on both axes at once, and the padding
     means a short name fills the 44px rather than rattling around inside it. */
  .name-btn {
    font-weight: 600;
    margin-right: auto;
    background: none;
    border: none;
    color: var(--text);
    display: inline-flex;
    align-items: center;
    justify-content: center;
    padding: 0 0.4rem;
    cursor: pointer;
    text-align: center;
    font-family: inherit;
    font-size: inherit;
  }
  
  @media (hover: hover) and (pointer: fine) {
    .name-btn:hover {
      text-decoration: underline;
    }
  }

  .name-btn.blocked {
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

