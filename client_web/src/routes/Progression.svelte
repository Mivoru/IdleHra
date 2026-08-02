<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import {
    queryKeys,
    fetchAchievements,
    fetchLoginBonus,
    fetchLeaderboard,
    fetchGuildLeaderboard,
    fetchRaceMastery,
    fetchStatistics,
    type AchievementEntry,
  } from '../lib/net/rest';
  import { claimAchievement } from '../lib/net/commands';
  import { connection } from '../lib/net/connection';
  import Bar from '../lib/ui/Bar.svelte';

  const client = useQueryClient();
  const achievements = createQuery(() => ({ queryKey: queryKeys.achievements, queryFn: fetchAchievements }));
  const loginBonus = createQuery(() => ({ queryKey: queryKeys.loginBonus, queryFn: fetchLoginBonus }));
  const leaderboard = createQuery(() => ({ queryKey: queryKeys.leaderboard, queryFn: fetchLeaderboard }));
  const guildBoard = createQuery(() => ({ queryKey: queryKeys.guildLeaderboard, queryFn: fetchGuildLeaderboard }));
  const raceMastery = createQuery(() => ({ queryKey: queryKeys.raceMastery, queryFn: fetchRaceMastery }));
  const statistics = createQuery(() => ({ queryKey: queryKeys.statistics, queryFn: fetchStatistics }));

  const snap = $derived($playerState);
  // Quarantine blocks every claim server-side, so the reason is stated rather
  // than leaving buttons that silently do nothing.
  const quarantined = $derived(snap ? snap.Quarantine_Active !== 0 : false);

  // Race names are not on the wire; RaceIds is a server-side enum. Listed here
  // because the mastery endpoint returns bare numeric ids.
  const RACE_NAMES: Record<number, string> = {
    1: 'Human',
    2: 'Vila',
    3: 'Draugr',
    4: 'Kobold',
    5: 'Vodnik',
  };

  function claim(entry: AchievementEntry) {
    const outcome = claimAchievement(entry.AchievementId, quarantined);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    setTimeout(() => client.invalidateQueries({ queryKey: queryKeys.achievements }), 800);
  }

  const claimable = $derived(
    (achievements.data ?? []).filter((a) => !a.IsClaimed && a.CompletedTier > 0),
  );

  function duration(seconds: number): string {
    const hours = Math.floor(seconds / 3600);
    if (hours < 1) return `${Math.floor(seconds / 60)}m`;
    return `${hours}h`;
  }
</script>

<div class="grid">
  <section class="panel">
    <div class="head">
      <h2>Achievements</h2>
      <span class="dim tiny">{claimable.length} ready to claim</span>
    </div>

    {#if quarantined}
      <p class="warn">Your account is restricted, so rewards cannot be claimed.</p>
    {/if}

    {#if achievements.isPending}
      <p class="dim">Loading...</p>
    {:else if achievements.isError}
      <p class="err">{achievements.error?.message}</p>
    {:else if (achievements.data ?? []).length === 0}
      <p class="dim">No achievements tracked yet.</p>
    {:else}
      <ul class="rows">
        {#each achievements.data ?? [] as entry (entry.AchievementId)}
          {@const ready = !entry.IsClaimed && entry.CompletedTier > 0}
          <li class:ready>
            <div class="line">
              <strong>Achievement #{entry.AchievementId}</strong>
              {#if entry.IsClaimed}
                <span class="dim tiny">claimed</span>
              {:else if ready}
                <!-- Modul: the button deliberately does NOT show a number.
                     NextTierReward is the reward for the tier NOT yet reached,
                     while claiming pays out the tiers already earned - a
                     Treasury claim at tier 2 paid 60 while the field read 250.
                     The client cannot compute the real total (the reward
                     tables are server-side), so promising a figure here would
                     be guessing at the player's payout. -->
                <button class="tiny-btn" disabled={quarantined} onclick={() => claim(entry)}>
                  Claim tier {entry.CompletedTier}
                </button>
              {/if}
            </div>
            <Bar
              value={entry.CurrentProgress}
              max={Math.max(1, entry.NextTierTarget)}
              color={ready ? 'var(--good)' : 'var(--accent)'}
              label={`${entry.CurrentProgress.toLocaleString()} / ${entry.NextTierTarget.toLocaleString()}`}
            />
            <span class="dim tiny">
              tier {entry.CompletedTier}
              {#if !entry.IsClaimed}&middot; next tier pays {entry.NextTierReward}{/if}
            </span>
          </li>
        {/each}
      </ul>
    {/if}
  </section>

  <section class="panel">
    <h2>Daily login</h2>
    {#if loginBonus.data}
      <p class="dim small">
        Streak day {loginBonus.data.CurrentStreakDay}.
        {loginBonus.data.CreditedToday ? "Today's reward is already credited." : 'Today is not credited yet.'}
      </p>
      <ol class="week">
        {#each loginBonus.data.WeeklyGoldSchedule as gold, index}
          <li class:current={index + 1 === loginBonus.data.CurrentStreakDay}>
            <span class="dim tiny">Day {index + 1}</span>
            <strong>{gold.toLocaleString()}g</strong>
          </li>
        {/each}
      </ol>
      {#if loginBonus.data.Day7DiamondBonus > 0}
        <p class="dim tiny">Day 7 also grants {loginBonus.data.Day7DiamondBonus} diamonds.</p>
      {/if}
    {:else}
      <p class="dim">Loading...</p>
    {/if}

    <h3>Race mastery</h3>
    {#if (raceMastery.data ?? []).length === 0}
      <p class="dim tiny">No race mastery yet.</p>
    {:else}
      {#each raceMastery.data ?? [] as race (race.RaceId)}
        <div class="mastery">
          <span class="dim tiny">{RACE_NAMES[race.RaceId] ?? `Race ${race.RaceId}`} &middot; level {race.Level}</span>
          <Bar
            value={race.Experience}
            max={Math.max(1, race.NextLevelExperience)}
            color="var(--rarity-6)"
            label={`${race.Experience.toLocaleString()} / ${race.NextLevelExperience.toLocaleString()}`}
          />
        </div>
      {/each}
    {/if}
  </section>

  <section class="panel">
    <h2>Statistics</h2>
    {#if statistics.data}
      {@const st = statistics.data}
      <dl class="stats">
        <div><dt>Level</dt><dd>{st.Level}</dd></div>
        <div><dt>Gold</dt><dd>{st.Gold.toLocaleString()}</dd></div>
        <div><dt>Diamonds</dt><dd>{st.PremiumDiamonds.toLocaleString()}</dd></div>
        <div><dt>Login streak</dt><dd>{st.LoginStreakDays}</dd></div>
        <div><dt>Kills</dt><dd>{st.TotalKills.toLocaleString()}</dd></div>
        <div><dt>Bosses</dt><dd>{st.BossesSlain.toLocaleString()}</dd></div>
        <div><dt>Crafted</dt><dd>{st.TotalItemsCrafted.toLocaleString()}</dd></div>
        <div><dt>Deaths</dt><dd>{st.TotalDeaths.toLocaleString()}</dd></div>
        <div><dt>Regions done</dt><dd>{st.RegionsCompletedCount}</dd></div>
        <div><dt>Achievements</dt><dd>{st.AchievementsClaimedCount}</dd></div>
        <div><dt>Characters</dt><dd>{st.CharacterCount}</dd></div>
        <div><dt>Played</dt><dd>{duration(st.TotalPlayTimeSeconds)}</dd></div>
      </dl>
      {#if st.GuildName}
        <p class="dim tiny">Guild: {st.GuildName}</p>
      {/if}
    {:else}
      <p class="dim">Loading...</p>
    {/if}
  </section>

  <section class="panel">
    <h2>Leaderboard</h2>
    {#if leaderboard.isPending}
      <p class="dim">Loading...</p>
    {:else if (leaderboard.data ?? []).length === 0}
      <p class="dim">No ranked players yet.</p>
    {:else}
      <ol class="board">
        {#each leaderboard.data ?? [] as row (row.PlayerId)}
          <li class:self={row.PlayerId === connection.currentPlayerId}>
            <span class="rank dim">#{row.Rank}</span>
            <span class="who">{row.DisplayName}</span>
            <span class="dim tiny">lv {row.Level}</span>
            <span class="xp">{row.Xp.toLocaleString()}</span>
          </li>
        {/each}
      </ol>
    {/if}

    <h3>Guilds</h3>
    <!-- Modul: /api/v1/leaderboard/guilds is implemented server-side but no
         Unity screen has ever called it - one of the nine endpoints the port
         plan lists as capability the old client never used. -->
    {#if (guildBoard.data ?? []).length === 0}
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

  .head {
    display: flex;
    justify-content: space-between;
    align-items: baseline;
    gap: 1rem;
  }

  h2 {
    margin: 0 0 0.5rem;
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
  .small {
    font-size: 0.8rem;
    margin: 0 0 0.7rem;
  }
  .tiny {
    font-size: 0.72rem;
  }
  .err {
    color: var(--danger);
  }

  .warn {
    padding: 0.5rem 0.65rem;
    background: rgba(224, 85, 63, 0.12);
    border-left: 3px solid var(--danger);
    border-radius: 4px;
    font-size: 0.82rem;
    margin: 0 0 0.7rem;
  }

  .rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.5rem;
    max-height: 28rem;
    overflow-y: auto;
  }

  .rows li {
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.45rem 0.55rem;
    opacity: 0.75;
  }

  .rows li.ready {
    opacity: 1;
    border-color: var(--good);
  }

  .line {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 0.5rem;
    font-size: 0.85rem;
    margin-bottom: 0.25rem;
  }

  .week {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(7, 1fr);
    gap: 0.25rem;
    text-align: center;
  }

  .week li {
    border: 1px solid var(--border);
    border-radius: 4px;
    padding: 0.3rem 0.15rem;
    font-size: 0.7rem;
  }

  .week li.current {
    border-color: var(--good);
    background: rgba(123, 201, 111, 0.1);
  }

  .week strong {
    display: block;
    font-size: 0.72rem;
  }

  .mastery {
    display: grid;
    gap: 0.15rem;
    margin-bottom: 0.45rem;
  }

  .stats {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 0.5rem;
    margin: 0;
  }

  .stats div {
    display: grid;
    gap: 0.1rem;
  }

  dt {
    font-size: 0.7rem;
    color: var(--text-dim);
  }

  dd {
    margin: 0;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
    font-size: 0.9rem;
  }

  .board {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.2rem;
    max-height: 20rem;
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
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.45rem;
  }
</style>
