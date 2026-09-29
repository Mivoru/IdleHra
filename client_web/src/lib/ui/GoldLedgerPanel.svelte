<script lang="ts">
  // Modul: WHERE THE GOLD WENT (task 79). Every sink debits one gold row and
  // nothing used to record which sink did, so a player could see a balance
  // fall and never learn what it had bought. The numbers are the server's
  // (GoldLedger, gold_spend_daily). This only names the categories and draws
  // the split.
  import { createQuery } from '@tanstack/svelte-query';
  import { queryKeys, fetchGoldLedger } from '../net/rest';
  import Money from './Money.svelte';

  const ledger = createQuery(() => ({ queryKey: queryKeys.goldLedger, queryFn: fetchGoldLedger }));

  // Names only. An unknown category (a sink added on the server first) shows
  // its raw name rather than disappearing.
  const LABELS: Record<string, string> = {
    Reroll: 'Affix rerolls',
    Fusion: 'Forge fusions',
    Village: 'Village upgrades',
    Recruit: 'Recruiting villagers',
    Breeding: 'Breeding',
    Delve: 'The Delve',
    Deep: 'The Deep',
    Market: 'Market purchases',
    Cosmetics: 'Cosmetics',
    Guild: 'Guild donations',
    GuildRaid: 'Guild raids',
  };

  type Window = 'Last7Days' | 'Last30Days' | 'SinceRecorded';
  const WINDOWS: { key: Window; label: string }[] = [
    { key: 'Last7Days', label: '7 days' },
    { key: 'Last30Days', label: '30 days' },
    { key: 'SinceRecorded', label: 'All' },
  ];
  let period = $state<Window>('Last30Days');

  const rows = $derived(
    (ledger.data?.Categories ?? [])
      .map((c) => ({ label: LABELS[c.Category] ?? c.Category, amount: c[period] }))
      .filter((r) => r.amount > 0)
      .sort((a, b) => b.amount - a.amount),
  );
  const total = $derived(rows.reduce((sum, r) => sum + r.amount, 0));
</script>

<h3>Where your gold went</h3>
{#if ledger.data}
  <div class="windows" role="group" aria-label="Period">
    {#each WINDOWS as w (w.key)}
      <button class:active={period === w.key} onclick={() => (period = w.key)}>{w.label}</button>
    {/each}
  </div>
  {#if rows.length === 0}
    <p class="dim tiny">
      Nothing spent {period === 'SinceRecorded' ? 'yet' : 'in this period'}. Rerolls, the Forge, the
      village, the Delve and the market all count.
    </p>
  {:else}
    <ul class="split" data-gold-ledger>
      {#each rows as r (r.label)}
        <li>
          <span class="label">{r.label}</span>
          <span class="bar" aria-hidden="true"><span style="width: {Math.max(2, (r.amount / total) * 100)}%"></span></span>
          <span class="amount"><Money amount={r.amount} /></span>
        </li>
      {/each}
    </ul>
  {/if}
  <p class="dim tiny">
    Spent in total: <Money amount={ledger.data.LifetimeSpent} /> - this is what the Treasury deed counts.
    {#if ledger.data.RecordedSince}Recorded since {ledger.data.RecordedSince}.{/if}
  </p>
{:else if ledger.isError}
  <p class="dim tiny">The ledger could not be read.</p>
{/if}

<style>
  .windows {
    display: flex;
    gap: 0.35rem;
    margin-bottom: 0.5rem;
  }

  .windows button {
    font-size: 0.78rem;
  }

  .windows button.active {
    border-color: var(--accent);
    color: var(--accent);
  }

  .split {
    list-style: none;
    margin: 0 0 0.5rem;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  .split li {
    display: grid;
    grid-template-columns: minmax(0, 9rem) minmax(0, 1fr) auto;
    gap: 0.5rem;
    align-items: center;
    font-size: 0.82rem;
  }

  .label {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .bar {
    height: 0.5rem;
    background: var(--bg-raised);
    border-radius: 999px;
    overflow: hidden;
  }

  .bar span {
    display: block;
    height: 100%;
    background: var(--accent);
  }

  .amount {
    font-variant-numeric: tabular-nums;
  }

  .dim {
    color: var(--text-dim);
  }

  .tiny {
    font-size: 0.72rem;
  }
</style>
