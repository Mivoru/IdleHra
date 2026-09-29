<script lang="ts">
  // Modul: WHERE THE GOLD WENT, WHERE IT CAME FROM, AND WHAT THE MATERIALS
  // DID (task 79). Every number is the server's (GoldLedger: gold_spend_daily
  // and gold_income_daily; MaterialLedger: material_flow_daily). This only
  // names the categories and draws the split.
  //
  // Modul: HONEST ABOUT "SINCE WHEN". Spending, income and the material flow
  // each began recording on a different day, so each section prints its own
  // date. Income from kills, the Town Hall and auto-salvage is written when
  // the game checkpoints (about every five minutes, and at logout), so the
  // last few minutes are missing until then - the screen says so rather than
  // letting a player think a fight paid nothing.
  import { createQuery } from '@tanstack/svelte-query';
  import { queryKeys, fetchGoldLedger, type GoldLedgerCategory } from '../net/rest';
  import { prettifyBaseId } from '../net/content';
  import { formatCompact } from './format';
  import Money from './Money.svelte';

  const ledger = createQuery(() => ({ queryKey: queryKeys.goldLedger, queryFn: fetchGoldLedger }));

  // Names only. An unknown category (one added on the server first) shows its
  // raw name rather than disappearing.
  const SPEND_LABELS: Record<string, string> = {
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

  const INCOME_LABELS: Record<string, string> = {
    Combat: 'Fighting',
    CombatAway: 'Fighting while away',
    TownHall: 'Town Hall',
    AutoSalvage: 'Auto-salvage',
    ChestSale: 'Chest sales',
    Market: 'Market sales',
    LoginReward: 'Daily login',
    Mail: 'Mail',
    GuildPayout: 'Guild payout',
    Delve: 'Delve consolation',
    Starter: 'Starting gold',
    Other: 'Delayed grants',
  };

  type Window = 'Last7Days' | 'Last30Days' | 'SinceRecorded';
  const WINDOWS: { key: Window; label: string }[] = [
    { key: 'Last7Days', label: '7 days' },
    { key: 'Last30Days', label: '30 days' },
    { key: 'SinceRecorded', label: 'All' },
  ];
  let period = $state<Window>('Last30Days');

  function split(categories: GoldLedgerCategory[] | undefined, labels: Record<string, string>) {
    return (categories ?? [])
      .map((c) => ({ label: labels[c.Category] ?? c.Category, amount: c[period] }))
      .filter((r) => r.amount > 0)
      .sort((a, b) => b.amount - a.amount);
  }

  const spent = $derived(split(ledger.data?.Categories, SPEND_LABELS));
  const spentTotal = $derived(spent.reduce((sum, r) => sum + r.amount, 0));
  const earned = $derived(split(ledger.data?.Income, INCOME_LABELS));
  const earnedTotal = $derived(earned.reduce((sum, r) => sum + r.amount, 0));

  // One row per material, one column per direction. Sold and binned share a
  // column: both empty the chest, and a fifth column does not fit a phone.
  interface MaterialRow {
    itemId: string;
    gathered: number;
    spent: number;
    sold: number;
    lost: number;
  }
  const materials = $derived.by(() => {
    const byItem = new Map<string, MaterialRow>();
    for (const m of ledger.data?.Materials ?? []) {
      const amount = m[period];
      if (amount <= 0) continue;
      const row = byItem.get(m.ItemId) ?? { itemId: m.ItemId, gathered: 0, spent: 0, sold: 0, lost: 0 };
      if (m.Direction === 'Gathered') row.gathered += amount;
      else if (m.Direction === 'Spent') row.spent += amount;
      else if (m.Direction === 'Sold' || m.Direction === 'Discarded') row.sold += amount;
      else if (m.Direction === 'LostToWarehouseCap') row.lost += amount;
      else continue;
      byItem.set(m.ItemId, row);
    }
    return [...byItem.values()].sort(
      (a, b) => b.gathered + b.spent + b.sold + b.lost - (a.gathered + a.spent + a.sold + a.lost),
    );
  });

  function num(n: number): string {
    return n > 0 ? formatCompact(n) : '-';
  }
</script>

{#if ledger.data}
  <div class="windows" role="group" aria-label="Period">
    {#each WINDOWS as w (w.key)}
      <button class:active={period === w.key} onclick={() => (period = w.key)}>{w.label}</button>
    {/each}
  </div>

  <h3>Where your gold came from</h3>
  {#if earned.length === 0}
    <p class="dim tiny">Nothing earned {period === 'SinceRecorded' ? 'yet' : 'in this period'}.</p>
  {:else}
    <ul class="split" data-gold-income>
      {#each earned as r (r.label)}
        <li>
          <span class="label">{r.label}</span>
          <span class="bar in" aria-hidden="true"><span style="width: {Math.max(2, (r.amount / earnedTotal) * 100)}%"></span></span>
          <span class="amount"><Money amount={r.amount} /></span>
        </li>
      {/each}
    </ul>
  {/if}
  <p class="dim tiny">
    {#if ledger.data.IncomeRecordedSince}Counted since {ledger.data.IncomeRecordedSince}.{:else}Not counted yet.{/if}
    Fighting, the Town Hall and auto-salvage are added when your progress is saved (about every five minutes, and at
    logout), so the last few minutes may be missing.
  </p>

  <h3>Where your gold went</h3>
  {#if spent.length === 0}
    <p class="dim tiny">
      Nothing spent {period === 'SinceRecorded' ? 'yet' : 'in this period'}. Rerolls, the Forge, the
      village, the Delve and the market all count.
    </p>
  {:else}
    <ul class="split" data-gold-ledger>
      {#each spent as r (r.label)}
        <li>
          <span class="label">{r.label}</span>
          <span class="bar" aria-hidden="true"><span style="width: {Math.max(2, (r.amount / spentTotal) * 100)}%"></span></span>
          <span class="amount"><Money amount={r.amount} /></span>
        </li>
      {/each}
    </ul>
  {/if}
  <p class="dim tiny">
    Spent in total: <Money amount={ledger.data.LifetimeSpent} /> - this is what the Treasury deed counts.
    {#if ledger.data.RecordedSince}Recorded since {ledger.data.RecordedSince}.{/if}
  </p>

  <h3>Materials</h3>
  {#if materials.length === 0}
    <p class="dim tiny">No materials moved {period === 'SinceRecorded' ? 'yet' : 'in this period'}.</p>
  {:else}
    <div class="flow" role="table" aria-label="Material flow" data-material-flow>
      <div class="row head" role="row">
        <span role="columnheader">Material</span>
        <span role="columnheader">Gathered</span>
        <span role="columnheader">Spent</span>
        <span role="columnheader">Sold/binned</span>
        <span role="columnheader">Lost</span>
      </div>
      {#each materials as m (m.itemId)}
        <div class="row" role="row">
          <span class="label" role="cell">{prettifyBaseId(m.itemId)}</span>
          <span role="cell">{num(m.gathered)}</span>
          <span role="cell">{num(m.spent)}</span>
          <span role="cell">{num(m.sold)}</span>
          <span role="cell" class:warn={m.lost > 0}>{num(m.lost)}</span>
        </div>
      {/each}
    </div>
  {/if}
  <p class="dim tiny">
    {#if ledger.data.MaterialsRecordedSince}Counted since {ledger.data.MaterialsRecordedSince}.{:else}Not counted yet.{/if}
    Gathered includes what village buildings produce. Spent is crafting, building, the larder and guild donations.
    Lost is production thrown away while you were away because the Warehouse was full; while you play, a full
    Warehouse pauses production instead.
  </p>
{:else if ledger.isError}
  <h3>Gold and materials</h3>
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

  .bar.in span {
    background: var(--good);
  }

  .amount {
    font-variant-numeric: tabular-nums;
  }

  .flow {
    display: grid;
    gap: 0.2rem;
    margin-bottom: 0.5rem;
    font-size: 0.78rem;
  }

  .flow .row {
    display: grid;
    grid-template-columns: minmax(0, 1fr) repeat(4, minmax(0, 3.6rem));
    gap: 0.4rem;
    align-items: center;
  }

  .flow .row > span:not(.label) {
    text-align: right;
    font-variant-numeric: tabular-nums;
  }

  .flow .head {
    color: var(--text-dim);
    font-size: 0.7rem;
  }

  .flow .head > span {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .warn {
    color: var(--warn);
  }

  .dim {
    color: var(--text-dim);
  }

  .tiny {
    font-size: 0.72rem;
  }
</style>
