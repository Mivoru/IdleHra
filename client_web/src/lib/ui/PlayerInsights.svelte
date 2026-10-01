<script lang="ts">
  // Task 56: STATISTICS THAT SAY HOW YOU PLAY. Rates over ten minutes, an hour
  // and a day, the split of your characters' time, and a timeline - all from
  // the server's ten-minute samples (StatSampler). Nothing here is computed
  // from the live packet: a rate needs history, and the server keeps it.
  import { createQuery } from '@tanstack/svelte-query';
  import { queryKeys, fetchInsights, type InsightRate, type InsightStyle } from '../net/rest';
  import { formatNumber, formatDecimal } from './format';
  import Skeleton from './Skeleton.svelte';

  const insights = createQuery(() => ({
    queryKey: queryKeys.insights,
    queryFn: fetchInsights,
    staleTime: 60_000,
    refetchInterval: 5 * 60_000,
  }));
  const view = $derived(insights.data);

  const ROWS: { label: string; pick: (r: InsightRate) => number }[] = [
    { label: 'Kills', pick: (r) => r.KillsPerHour },
    { label: 'Experience', pick: (r) => r.XpPerHour },
    { label: 'Gold earned', pick: (r) => r.GoldEarnedPerHour },
    { label: 'Harvests', pick: (r) => r.HarvestsPerHour },
    { label: 'Crafts', pick: (r) => r.CraftsPerHour },
  ];

  function cell(rate: InsightRate, value: number): string {
    if (rate.CoveredSeconds === 0) return '-';
    return value >= 1000 ? formatNumber(Math.round(value)) : formatDecimal(value, 1);
  }

  function minutes(seconds: number): string {
    return seconds >= 3600 ? `${Math.round(seconds / 3600)} h` : `${Math.round(seconds / 60)} min`;
  }

  const SLICES: { key: keyof InsightStyle; label: string; color: string }[] = [
    { key: 'FightingPct', label: 'Fighting', color: 'var(--danger)' },
    { key: 'GatheringPct', label: 'Gathering', color: 'var(--good)' },
    { key: 'CraftingPct', label: 'Crafting', color: 'var(--accent)' },
    { key: 'IdlePct', label: 'Idle', color: 'var(--border)' },
  ];

  const day = $derived(view?.Rates.find((r) => r.WindowSeconds === 86400) ?? null);

  function when(iso: string): string {
    return new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
  }
</script>

<section class="panel insights" data-testid="player-insights">
  <h3>How you play</h3>

  {#if insights.isPending}
    <Skeleton rows={6} />
  {:else if insights.isError}
    <p class="warn-line">Statistics could not be read.</p>
  {:else if view}
    <h4>Per hour</h4>
    <div class="scroll">
      <table class="rates">
        <thead>
          <tr>
            <th></th>
            {#each view.Rates as r (r.Window)}<th class="num">{r.Window}</th>{/each}
          </tr>
        </thead>
        <tbody>
          {#each ROWS as row (row.label)}
            <tr>
              <th scope="row">{row.label}</th>
              {#each view.Rates as r (r.Window)}<td class="num">{cell(r, row.pick(r))}</td>{/each}
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
    <p class="dim tiny">
      Measured every {view.SampleIntervalMinutes} minutes while you are online; what you earn away counts
      in the window you come back in.
      {#if view.Rates.every((r) => r.CoveredSeconds === 0)}
        The first reading arrives within {view.SampleIntervalMinutes} minutes of playing.
      {/if}
      {#each view.Rates.filter((r) => r.CoveredSeconds > 0 && r.CoveredSeconds < r.WindowSeconds) as r (r.Window)}
        The {r.Window} column only has {minutes(r.CoveredSeconds)} of readings so far.
      {/each}
    </p>

    <h4>Your style</h4>
    {#each view.Style as s (s.Window)}
      <div class="style">
        <span class="dim tiny">Last {s.Window}</span>
        {#if s.Samples === 0}
          <span class="dim tiny">no readings yet</span>
        {:else}
          <div class="stack" role="img" aria-label={SLICES.map((sl) => `${sl.label} ${s[sl.key]}%`).join(', ')}>
            {#each SLICES as sl (sl.key)}
              {#if Number(s[sl.key]) > 0}
                <span style={`width: ${s[sl.key]}%; background: ${sl.color}`}></span>
              {/if}
            {/each}
          </div>
          <ul class="legend">
            {#each SLICES as sl (sl.key)}
              <li><i style={`background: ${sl.color}`}></i>{sl.label} {s[sl.key]}%</li>
            {/each}
          </ul>
        {/if}
      </div>
    {/each}
    {#if day && day.CoveredSeconds > 0}
      <p class="dim small gold">
        Gold in the last day: <strong>+{formatNumber(day.GoldEarned)}</strong> earned,
        <strong>-{formatNumber(day.GoldSpent)}</strong> spent.
        <span class="tiny">(From the rises and falls of your purse between readings.)</span>
      </p>
    {/if}

    <h4>Timeline</h4>
    {#if view.Timeline.length === 0}
      <p class="dim tiny">Nothing recorded yet.</p>
    {:else}
      <ol class="timeline">
        {#each view.Timeline as t, i (i)}
          <li class={t.Kind}>
            <span class="date dim tiny">{when(t.AtUtc)}</span>
            <span>{t.Text}</span>
          </li>
        {/each}
      </ol>
    {/if}
  {/if}
</section>

<style>
  .insights {
    display: grid;
    gap: 0.4rem;
  }

  h3 {
    margin: 0;
  }

  h4 {
    margin: 0.4rem 0 0;
    font-size: 0.9rem;
  }

  .scroll {
    overflow-x: auto;
  }

  .rates {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.85rem;
  }

  .rates th,
  .rates td {
    padding: 0.2rem 0.4rem;
    border-bottom: 1px solid var(--border);
    text-align: left;
  }

  .rates .num {
    text-align: right;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }

  .style {
    display: grid;
    gap: 0.2rem;
  }

  .stack {
    display: flex;
    height: 0.7rem;
    border-radius: 999px;
    overflow: hidden;
    background: var(--bg);
  }

  .stack span {
    display: block;
    height: 100%;
  }

  .legend {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-wrap: wrap;
    gap: 0.2rem 0.75rem;
    font-size: 0.75rem;
  }

  .legend i {
    display: inline-block;
    width: 0.6rem;
    height: 0.6rem;
    border-radius: 2px;
    margin-right: 0.25rem;
    vertical-align: middle;
  }

  .timeline {
    list-style: none;
    margin: 0;
    padding: 0 0 0 0.8rem;
    border-left: 2px solid var(--border);
    display: grid;
    gap: 0.35rem;
  }

  .timeline li {
    position: relative;
    display: grid;
    font-size: 0.85rem;
  }

  .timeline li::before {
    content: '';
    position: absolute;
    left: -1.15rem;
    top: 0.35rem;
    width: 0.5rem;
    height: 0.5rem;
    border-radius: 50%;
    background: var(--brass);
  }

  .timeline li.drop::before {
    background: var(--accent);
  }

  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.85rem;
  }
  .tiny {
    font-size: 0.72rem;
  }
  .warn-line {
    color: var(--warn);
    font-size: 0.85rem;
  }
</style>
