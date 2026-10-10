<script lang="ts">
  // Owner, 2026-10-10: every stat of the character - attack, crit, attack
  // speed, lifesteal, block, luck, health, armour, gathering - for the one
  // selected, gear and pet included, and the cap each one runs into, so a
  // player can see that attack speed is maxed and buy something else.
  //
  // Modul: THE SERVER'S NUMBERS (CharacterStatSheet). Each one is asked of the
  // function the live tick uses and each cap is the clamp it applies; this
  // file formats. A client-side stat model is the thing this codebase keeps
  // deleting (huntingEstimate.ts, the damage-delta inference).
  import { createQuery } from '@tanstack/svelte-query';
  import { fetchCharacterStats, queryKeys, type StatSheetRow } from '../net/rest';
  import { connectionStatus } from '../stores/game';
  import { formatDecimal, formatNumber } from './format';
  import QueryError from './QueryError.svelte';
  import Skeleton from './Skeleton.svelte';

  interface Props {
    /** 0-based slot of the character to read. */
    slot: number;
  }

  const { slot }: Props = $props();

  // Re-read while open: an equip or a level changes the sheet, and the
  // request is one snapshot on the server - cheap at this cadence.
  const sheet = createQuery(() => ({
    queryKey: queryKeys.characterStats(slot),
    queryFn: () => fetchCharacterStats(slot),
    enabled: $connectionStatus.phase === 'live',
    refetchInterval: 15_000,
    staleTime: 5_000,
    retry: false,
  }));

  function value(row: StatSheetRow): string {
    switch (row.Unit) {
      case 'pct':
        return `${formatDecimal(row.Value, 1)}%`;
      case 'mult':
        return `x${formatDecimal(row.Value, 2)}`;
      case 'per_s':
        return `${formatDecimal(row.Value, 1)} /s`;
      case 's':
        return `${formatDecimal(row.Value, 2)} s`;
      default:
        return Math.abs(row.Value) >= 1000 ? formatNumber(row.Value) : formatDecimal(row.Value, 1);
    }
  }

  function cap(row: StatSheetRow): string | null {
    if (row.Cap === null) return null;
    return row.Unit === 'mult' ? `cap x${formatDecimal(row.Cap, 2)}` : `cap ${formatDecimal(row.Cap, 1)}%`;
  }
</script>

<section class="panel stats" data-testid="character-stats">
  {#if sheet.isError}
    <QueryError query={sheet} what="the stats" />
  {:else if sheet.isPending}
    <Skeleton rows={8} />
  {:else if sheet.data}
    <p class="dim tiny">Everything this character fights and gathers with right now - gear, pet, attributes, skills and bloodline included. A red cap means more of that stat does nothing.</p>
    {#each sheet.data.Sections as section (section.Title)}
      <h3>{section.Title}</h3>
      <dl>
        {#each section.Rows as row (row.Key)}
          <div class="row" class:capped={row.AtCap} data-stat={row.Key}>
            <dt>{row.Label}</dt>
            <dd class="value">
              {value(row)}
              {#if cap(row)}
                <span class="cap tiny" class:at={row.AtCap} data-testid="stat-cap">{row.AtCap ? 'at ' : ''}{cap(row)}</span>
              {:else if row.AtCap}
                <span class="cap tiny at" data-testid="stat-cap">at limit</span>
              {/if}
            </dd>
            {#if row.Note}
              <dd class="note tiny dim">{row.Note}</dd>
            {/if}
          </div>
        {/each}
      </dl>
    {/each}
  {/if}
</section>

<style>
  .stats {
    display: grid;
    gap: 0.4rem;
  }

  h3 {
    margin: 0.8rem 0 0.2rem;
    font-size: 1rem;
  }

  dl {
    margin: 0;
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(17rem, 1fr));
    gap: 0.5rem;
  }

  .row {
    display: grid;
    grid-template-columns: 1fr auto;
    align-items: baseline;
    gap: 0.15rem 0.6rem;
    padding: 0.45rem 0.6rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-raised);
    min-width: 0;
  }

  .row.capped {
    border-color: var(--danger);
  }

  dt {
    font-weight: 600;
    min-width: 0;
  }

  dd {
    margin: 0;
  }

  .value {
    text-align: right;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }

  .cap {
    display: block;
    color: var(--text-dim);
  }

  .cap.at {
    color: var(--danger);
    font-weight: 600;
  }

  .note {
    grid-column: 1 / -1;
    line-height: 1.35;
  }

  .dim {
    color: var(--text-dim);
  }

  .tiny {
    font-size: 0.72rem;
  }
</style>
