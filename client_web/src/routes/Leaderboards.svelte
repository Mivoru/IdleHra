<script lang="ts">
  import { formatNumber } from '../lib/ui/format';
  import PlayerAvatar from '../lib/ui/PlayerAvatar.svelte';
  import { createQuery } from '@tanstack/svelte-query';
  import {
    queryKeys,
    fetchLeaderboard,
    fetchGuildLeaderboard,
    fetchDeepestBoard,
  } from '../lib/net/rest';
  import { connection } from '../lib/net/connection';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import PlayerProfileModal from '../lib/ui/PlayerProfileModal.svelte';
  import { tierNameStyle } from '../lib/ui/leaderboardTiers';

  const leaderboard = createQuery(() => ({ queryKey: queryKeys.leaderboard, queryFn: fetchLeaderboard }));
  const guildBoard = createQuery(() => ({ queryKey: queryKeys.guildLeaderboard, queryFn: fetchGuildLeaderboard }));

  /*
    Modul: THE DEEPEST BOARD PAYS NOTHING, and says so. It is a plain query on
    the server, not a Redis board, so the diamond payout cannot pick it up.
    Its own tab rather than a third panel, because it is a different question
    ("how far down did anyone get this week") and it resets weekly.
  */
  let tab = $state<'standing' | 'deepest'>('standing');
  /** The board row whose profile is open - a title is worn to be seen. */
  let inspectPlayerId = $state<number | null>(null);
  const deepest = createQuery(() => ({
    queryKey: queryKeys.deepestBoard,
    queryFn: fetchDeepestBoard,
    enabled: tab === 'deepest',
  }));
</script>

<div class="tabs" role="tablist">
  <button role="tab" class:active={tab === 'standing'} aria-selected={tab === 'standing'} onclick={() => (tab = 'standing')}>
    Standing
  </button>
  <button role="tab" class:active={tab === 'deepest'} aria-selected={tab === 'deepest'} onclick={() => (tab = 'deepest')}>
    Deepest this week
  </button>
</div>

{#if tab === 'deepest'}
  <div class="grid">
    <section class="panel deepest">
      <h2>Deepest this week</h2>
      <p class="dim tiny">
        The deepest floor of the Deep cleared since Monday. Glory only: this board pays nothing, and it
        starts again every week.
      </p>
      {#if deepest.isPending}
        <Skeleton />
      {:else if deepest.isError}
        <p class="warn">Could not load the leaderboard.</p>
        <button class="retry" onclick={() => deepest.refetch()}>Retry</button>
      {:else if (deepest.data ?? []).length === 0}
        <p class="dim">Nobody has gone below the Delve this week.</p>
      {:else}
        <ol class="board">
          {#each deepest.data ?? [] as row (row.PlayerId)}
            <li class:self={row.PlayerId === connection.currentPlayerId}>
              <span class="rank dim">#{row.Rank}</span>
              <button class="who who-btn" onclick={() => (inspectPlayerId = row.PlayerId)}>
                <PlayerAvatar playerId={row.PlayerId} size="sm" />
                {row.Name}
                {#if row.Title}<span class="title tiny">{row.Title}</span>{/if}
              </button>
              <span class="xp">floor {row.Floor}</span>
            </li>
          {/each}
        </ol>
      {/if}
    </section>
  </div>
{:else}

<div class="grid">
  <section class="panel">
    <h2>Player Leaderboard</h2>
    <p class="dim tiny">
      Ranked by level, then by the hardest monster you have ever beaten, then
      by how many times you have beaten it.
    </p>
    {#if leaderboard.isPending}
      <Skeleton />
    {:else if leaderboard.isError}
      <!-- Modul: an error is not an empty board. This fell through to "No
           ranked players yet", which reads as "nobody plays this". -->
      <p class="warn">Could not load the leaderboard.</p>
      <button class="retry" onclick={() => leaderboard.refetch()}>Retry</button>
    {:else if (leaderboard.data ?? []).length === 0}
      <p class="dim">No ranked players yet.</p>
    {:else}
      <ol class="board players">
        {#each leaderboard.data ?? [] as row (row.PlayerId)}
          <li class:self={row.PlayerId === connection.currentPlayerId}>
            <span class="rank dim">#{row.Rank}</span>
            <!-- Modul: the colour and the glow come from the tier the SERVER
                 resolved, not from Rank read again here - see
                 lib/ui/leaderboardTiers.ts for why the thresholds are not
                 mirrored. An untiered row gets an empty style and keeps the
                 page's own text colour, so the decorated names stand out by
                 contrast rather than by shouting.

                 A button, like the Deepest tab's rows: this board is where a
                 player meets everyone else, and only the weekly tab could
                 open a profile. -->
            <button
              class="who who-btn tiered"
              style={tierNameStyle(row.TierId)}
              title={row.TierName}
              onclick={() => (inspectPlayerId = row.PlayerId)}
            >
              <PlayerAvatar playerId={row.PlayerId} size="sm" />
              {row.DisplayName}
            </button>
            <span class="lv dim tiny">lv {row.Level}</span>
            <!-- Modul: ONE cell for everything optional. The row used to put up
                 to six children into a four-column grid, so a tiered, paid row
                 wrapped its last two cells onto a second line - the monster
                 name landing in the 2.5rem rank column, cut to "Mou...", even
                 at 1440 wide. The second line is a named area now. -->
            <span class="meta tiny">
              {#if row.TierName}
                <span class="tier tiered" style={tierNameStyle(row.TierId)}>{row.TierName}</span>
              {/if}
              {#if row.WeeklyDiamonds > 0}
                <span class="reward" title="Paid every week while this rank holds">
                  {row.WeeklyDiamonds}&#9670;/wk
                </span>
              {/if}
              <span class="progress dim">
                {#if row.HardestMonsterName}
                  {row.HardestMonsterName}
                  {#if row.KillsOfHardest > 0}&times;{formatNumber(row.KillsOfHardest)}{/if}
                {:else}
                  no kills yet
                {/if}
              </span>
            </span>
          </li>
        {/each}
      </ol>
    {/if}
  </section>

  <section class="panel">
    <h2>Guild Leaderboard</h2>
    {#if guildBoard.isPending}
      <Skeleton />
    {:else if guildBoard.isError}
      <p class="warn">Could not load the leaderboard.</p>
      <button class="retry" onclick={() => guildBoard.refetch()}>Retry</button>
    {:else if (guildBoard.data ?? []).length === 0}
      <p class="dim tiny">No ranked guilds yet.</p>
    {:else}
      <ol class="board">
        {#each guildBoard.data ?? [] as row (row.GuildId)}
          <li>
            <span class="rank dim">#{row.Rank}</span>
            <span class="who">{row.Name}</span>
            <span class="dim tiny">tier {row.GuildTier}</span>
            <span class="xp">{formatNumber(row.GuildMMR)} MMR</span>
          </li>
        {/each}
      </ol>
    {/if}
  </section>
</div>
{/if}

{#if inspectPlayerId !== null}
  <PlayerProfileModal playerId={inspectPlayerId} onClose={() => (inspectPlayerId = null)} />
{/if}

<style>
  /* Task 54: the face sits inside the name cell, so the grid keeps its columns. */
  .who :global(.avatar) {
    margin-right: 0.3rem;
  }

  .tabs {
    display: flex;
    gap: 0.5rem;
    padding: 1rem 1rem 0;
    flex-wrap: wrap;
  }

  .tabs button {
    min-height: 44px;
    flex-shrink: 0;
    padding: 0.4rem 0.9rem;
    border-radius: var(--radius);
    border: 1px solid var(--border);
    background: var(--bg-panel);
    color: inherit;
    font: inherit;
    cursor: pointer;
  }

  .tabs button.active {
    border-color: var(--accent);
    color: var(--accent);
    font-weight: 700;
  }

  .deepest .board li {
    grid-template-columns: 2.5rem 1fr auto;
    align-items: center;
  }

  .who-btn {
    min-height: 44px;
    min-width: 0;
    text-align: left;
    background: none;
    border: none;
    padding: 0;
    color: inherit;
    font: inherit;
    font-weight: 600;
    cursor: pointer;
  }

  .title {
    margin-left: 0.35rem;
    font-style: italic;
    color: var(--accent);
    font-weight: 600;
  }

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
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
  }

  .dim {
    color: var(--text-dim);
  }

  .tiny {
    font-size: 0.72rem;
  }

  .board {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.2rem;
    max-height: 28rem;
    overflow-y: auto;
  }

  .board li {
    display: grid;
    grid-template-columns: 2.5rem 1fr auto auto;
    gap: 0.5rem;
    align-items: baseline;
    font-size: 0.83rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.2rem;
  }

  .board li.self {
    color: var(--accent);
    font-weight: 700;
  }

  .rank,
  .xp {
    font-variant-numeric: tabular-nums;
  }

  .who {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    /* The tier colour arrives as an inline style, so the weight is the part
       that has to live here - a glow on thin text reads as a smudge. */
    font-weight: 600;
  }

  .tier {
    flex: none;
    font-weight: 600;
    letter-spacing: 0.02em;
    opacity: 0.9;
  }

  .reward {
    flex: none;
    color: var(--diamond);
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }

  .progress {
    text-align: right;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  /* Two named lines: name and level on top, tier / reward / hardest monster
     below. Rank spans both. */
  .players li {
    grid-template-columns: 2.5rem minmax(0, 1fr) auto;
    grid-template-areas:
      'rank who lv'
      'rank meta meta';
    row-gap: 0;
    align-items: center;
  }

  .players .rank {
    grid-area: rank;
  }

  .players .who {
    grid-area: who;
  }

  .players .lv {
    grid-area: lv;
  }

  .players .meta {
    grid-area: meta;
    display: flex;
    align-items: baseline;
    gap: 0.5rem;
    min-width: 0;
  }

  .players .progress {
    flex: 1 1 auto;
    min-width: 0;
  }

  /* leaderboardTiers.ts declares both colours as custom properties; the
     theme picks one here. Undefined on an untiered row, so the colour falls
     back to inherit. The white halo is for dark wood - on parchment it only
     blurs the ink. */
  .tiered {
    color: var(--tier-color);
    text-shadow: var(--tier-shadow);
  }

  @media (prefers-color-scheme: light) {
    .tiered {
      color: var(--tier-color-light);
      text-shadow: none;
    }
  }

  .warn {
    color: var(--danger);
    margin: 0 0 0.5rem;
  }

  .retry {
    min-height: 44px;
  }
</style>
