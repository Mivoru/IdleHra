<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import {
    queryKeys,
    fetchGuildRoster,
    fetchPlayerNames,
    fetchStatistics,
  } from '../lib/net/rest';
  import {
    contributeToWarSupply,
    launchGuildRaid,
    contributeGuildGold,
    establishMentorship,
    terminateMentorship,
  } from '../lib/net/commands';
  import { connection } from '../lib/net/connection';
  import Bar from '../lib/ui/Bar.svelte';

  const client = useQueryClient();
  const roster = createQuery(() => ({ queryKey: queryKeys.guildRoster, queryFn: fetchGuildRoster }));
  const statistics = createQuery(() => ({ queryKey: queryKeys.statistics, queryFn: fetchStatistics }));

  const snap = $derived($playerState);
  const hasGuild = $derived((statistics.data?.GuildName ?? '') !== '');
  const warId = $derived(snap ? Number(snap.ActiveGuildWarId) : 0);

  const rosterIds = $derived((roster.data ?? []).map((m) => m.PlayerId).sort());
  const names = createQuery(() => ({
    queryKey: queryKeys.playerNames(rosterIds),
    queryFn: () => fetchPlayerNames(rosterIds),
    enabled: rosterIds.length > 0,
    staleTime: 10 * 60_000,
  }));
  const nameById = $derived(new Map((names.data ?? []).map((n) => [n.PlayerId, n.Username])));

  function refresh() {
    setTimeout(() => client.invalidateQueries({ queryKey: queryKeys.guildRoster }), 600);
  }

  // --- war ------------------------------------------------------------------
  // Modul: the three war axes are mirrored for both sides on the hot path, so
  // this is a live scoreboard rather than a REST snapshot.
  const warAxes = $derived(
    snap
      ? [
          { label: 'Combat vanguard', ours: snap.GuildCombatVanguardPoints, theirs: snap.EnemyCombatVanguardPoints },
          { label: 'Production logistics', ours: snap.GuildProductionLogisticsPoints, theirs: snap.EnemyProductionLogisticsPoints },
          { label: 'Gathering supply', ours: snap.GuildGatheringSupplyChainPoints, theirs: snap.EnemyGatheringSupplyChainPoints },
        ]
      : [],
  );

  let warCommodity = $state(1);
  let warQuantity = $state(10);

  function contributeWar() {
    const outcome = contributeToWarSupply(warCommodity, warQuantity, warId);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }

  // --- raid -----------------------------------------------------------------
  function raid() {
    const outcome = launchGuildRaid(hasGuild);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    // Leader-only is enforced server-side against the locked membership row,
    // and a non-leader's request simply rolls back with no message at all -
    // so promising success here would be a lie.
    pushLocalNotice('Raid requested. Only a guild leader can actually start one.', 'info');
  }

  // --- treasury -------------------------------------------------------------
  let treasuryGold = $state(1000);

  function giveGold() {
    const outcome = contributeGuildGold(treasuryGold, hasGuild);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    refresh();
  }

  // --- mentorship -----------------------------------------------------------
  const mentorId = $derived(snap ? Number(snap.ActiveMentorPlayerId) : 0);
  const mentorBonus = $derived(
    snap && typeof snap.MentorshipExpBonusMultiplier === 'number'
      ? snap.MentorshipExpBonusMultiplier
      : 1,
  );

  let mentorTarget = $state(0);

  function establish() {
    const outcome = establishMentorship(mentorTarget);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    pushLocalNotice('Mentorship requested.', 'info');
  }

  function terminate() {
    const outcome = terminateMentorship(mentorId);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    pushLocalNotice('Mentorship ended.', 'info');
  }

  const otherMembers = $derived(
    (roster.data ?? []).filter((m) => m.PlayerId !== connection.currentPlayerId),
  );
</script>

{#if !snap}
  <p class="dim pad">Waiting for state...</p>
{:else}
  <div class="grid">
    <section class="panel">
      <h2>Guild war</h2>

      {#if warId <= 0}
        <p class="dim">
          No war is active. The scoreboard below appears once your guild is
          matched.
        </p>
      {:else}
        <p class="dim small">
          War #{warId} &middot; multiplier
          {typeof snap.CachedWarMultiplier === 'number'
            ? snap.CachedWarMultiplier.toFixed(2)
            : snap.CachedWarMultiplier}x
        </p>

        {#each warAxes as axis}
          {@const total = Math.max(1, axis.ours + axis.theirs)}
          <div class="axis">
            <span class="dim tiny">{axis.label}</span>
            <Bar
              value={axis.ours}
              max={total}
              color={axis.ours >= axis.theirs ? 'var(--good)' : 'var(--danger)'}
              label={`${axis.ours.toLocaleString()} vs ${axis.theirs.toLocaleString()}`}
            />
          </div>
        {/each}

        <h3>Contribute supply</h3>
        <div class="row">
          <input type="number" min="1" bind:value={warCommodity} title="Commodity id" />
          <input type="number" min="1" bind:value={warQuantity} title="Quantity" />
          <button onclick={contributeWar}>Burn</button>
        </div>
        <p class="dim tiny">
          Contributions are burned into the war effort. The wire takes a numeric
          commodity id here - there is no picker endpoint for it.
        </p>
      {/if}
    </section>

    <section class="panel">
      <h2>Raid</h2>

      {#if snap.GuildRaidBossMaxHp > 0}
        <p class="dim small">Tier {snap.GuildRaidTier}</p>
        <Bar
          value={Number(snap.GuildRaidBossCurrentHp)}
          max={Number(snap.GuildRaidBossMaxHp)}
          color="var(--danger)"
          label={`${Number(snap.GuildRaidBossCurrentHp).toLocaleString()} / ${Number(snap.GuildRaidBossMaxHp).toLocaleString()}`}
        />
      {:else}
        <p class="dim">No raid boss active.</p>
      {/if}

      <button disabled={!hasGuild} onclick={raid}>Launch raid</button>

      <h3>Treasury</h3>
      <div class="row">
        <input type="number" min="1" step="100" bind:value={treasuryGold} />
        <button disabled={!hasGuild || treasuryGold < 1} onclick={giveGold}>Contribute gold</button>
      </div>
      <p class="dim tiny">
        Raises the guild's tier and your own contribution ranking on the roster.
      </p>

      <h3>Logistics</h3>
      <div class="axis">
        <span class="dim tiny">Depot level {snap.GuildLogisticsLevel}</span>
        <Bar
          value={Number(snap.GuildLogisticsCurrentStock)}
          max={Math.max(1, Number(snap.GuildLogisticsTargetRequirement))}
          color="var(--accent)"
          label={`${Number(snap.GuildLogisticsCurrentStock).toLocaleString()} / ${Number(snap.GuildLogisticsTargetRequirement).toLocaleString()}`}
        />
      </div>
    </section>

    <section class="panel">
      <h2>Mentorship</h2>

      <dl class="stats">
        <div><dt>Mentors held</dt><dd>{snap.CachedMentorCount}</dd></div>
        <div><dt>XP bonus</dt><dd>{(mentorBonus * 100).toFixed(0)}%</dd></div>
      </dl>

      {#if mentorId > 0}
        <p class="active">
          Mentored by {nameById.get(mentorId) ?? `Player #${mentorId}`}.
        </p>
        <button onclick={terminate}>End mentorship</button>
      {:else}
        <p class="dim small">
          A mentor lends you their character's experience bonus. You cannot
          mentor yourself.
        </p>

        <label>
          Guild member
          <select bind:value={mentorTarget}>
            <option value={0}>Choose...</option>
            {#each otherMembers as member (member.PlayerId)}
              <option value={member.PlayerId}>
                {nameById.get(member.PlayerId) ?? `Player #${member.PlayerId}`}
              </option>
            {/each}
          </select>
        </label>

        <button disabled={mentorTarget === 0} onclick={establish}>Request mentorship</button>

        {#if otherMembers.length === 0}
          <p class="dim tiny">No other guild members to ask.</p>
        {/if}
      {/if}
    </section>
  </div>
{/if}

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
    margin: 0.35rem 0 0;
  }
  .pad {
    padding: 1rem;
  }

  .active {
    color: var(--good);
    font-size: 0.88rem;
    margin: 0 0 0.6rem;
  }

  .axis {
    display: grid;
    gap: 0.2rem;
    margin-bottom: 0.5rem;
  }

  .row {
    display: grid;
    grid-template-columns: 1fr 1fr auto;
    gap: 0.4rem;
  }

  input,
  select {
    font: inherit;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.4rem 0.5rem;
    width: 100%;
  }

  label {
    display: grid;
    gap: 0.25rem;
    font-size: 0.8rem;
    color: var(--text-dim);
    margin-bottom: 0.6rem;
  }

  .stats {
    display: grid;
    grid-template-columns: repeat(2, 1fr);
    gap: 0.5rem;
    margin: 0 0 0.7rem;
  }

  .stats div {
    display: grid;
    gap: 0.1rem;
  }

  dt {
    font-size: 0.72rem;
    color: var(--text-dim);
  }

  dd {
    margin: 0;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
  }
</style>
