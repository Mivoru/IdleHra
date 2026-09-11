<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import {
    queryKeys,
    fetchLeaderboard,
    fetchGuildLeaderboard,
  } from '../lib/net/rest';
  import { connection } from '../lib/net/connection';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import { tierNameStyle } from '../lib/ui/leaderboardTiers';

  const leaderboard = createQuery(() => ({ queryKey: queryKeys.leaderboard, queryFn: fetchLeaderboard }));
  const guildBoard = createQuery(() => ({ queryKey: queryKeys.guildLeaderboard, queryFn: fetchGuildLeaderboard }));
</script>

<div class="grid">
  <section class="panel">
    <h2>Player Leaderboard</h2>
    <p class="dim tiny">
      Ranked by level, then by the hardest monster you have ever beaten, then
      by how many times you have beaten it.
    </p>
    {#if leaderboard.isPending}
      <Skeleton />
    {:else if (leaderboard.data ?? []).length === 0}
      <p class="dim">No ranked players yet.</p>
    {:else}
      <ol class="board">
        {#each leaderboard.data ?? [] as row (row.PlayerId)}
          <li class:self={row.PlayerId === connection.currentPlayerId}>
            <span class="rank dim">#{row.Rank}</span>
            <!-- Modul: the colour and the glow come from the tier the SERVER
                 resolved, not from Rank read again here - see
                 lib/ui/leaderboardTiers.ts for why the thresholds are not
                 mirrored. An untiered row gets an empty style and keeps the
                 page's own text colour, so the decorated names stand out by
                 contrast rather than by shouting. -->
            <span class="who" style={tierNameStyle(row.TierId)} title={row.TierName}>
              {row.DisplayName}
            </span>
            {#if row.TierName}
              <span class="tier tiny" style={tierNameStyle(row.TierId)}>{row.TierName}</span>
            {/if}
            <span class="dim tiny">lv {row.Level}</span>
            {#if row.WeeklyDiamonds > 0}
              <span class="reward tiny" title="Paid every week while this rank holds">
                {row.WeeklyDiamonds}&#9670;/wk
              </span>
            {/if}
            <span class="progress dim tiny">
              {#if row.HardestMonsterName}
                {row.HardestMonsterName}
                {#if row.KillsOfHardest > 0}&times;{row.KillsOfHardest.toLocaleString()}{/if}
              {:else}
                no kills yet
              {/if}
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
    {:else if (guildBoard.data ?? []).length === 0}
      <p class="dim tiny">No ranked guilds yet.</p>
    {:else}
      <ol class="board">
        {#each guildBoard.data ?? [] as row (row.GuildId)}
          <li>
            <span class="rank dim">#{row.Rank}</span>
            <span class="who">{row.Name}</span>
            <span class="dim tiny">tier {row.GuildTier}</span>
            <span class="xp">{row.GuildMMR.toLocaleString()} MMR</span>
          </li>
        {/each}
      </ol>
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
    color: var(--diamond, #7dd3fc);
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }

  .progress {
    text-align: right;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
</style>
