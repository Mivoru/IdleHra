<script lang="ts">
  import type { AffixMap } from '../net/rest';
  import { toDisplayAffixes } from './affixes';

  interface Props {
    affixes: AffixMap;
  }

  let { affixes }: Props = $props();

  // Affix rarity is a SEPARATE five-tier axis from the item's 14 quality
  // tiers: quality drives how MANY affixes an item has, affix rarity drives
  // how BIG each one is. Conflating them has caused bugs here before, so the
  // colours come from their own scale rather than the item rarity palette.
  const RARITY_COLOR = [
    'var(--text-dim)',
    'var(--text-dim)',
    'var(--good)',
    'var(--accent)',
    'var(--rarity-9)',
    'var(--rarity-12)',
  ];

  const rows = $derived(toDisplayAffixes(affixes));
</script>

{#if rows.length > 0}
  <ul class="affixes">
    {#each rows as row (row.key)}
      <li>
        <span class="name" title={`${row.rarityName} affix`}>
          <i class="pip" style="background: {RARITY_COLOR[row.rarity] ?? 'var(--text-dim)'}"></i>
          {row.label}
        </span>
        <b>{row.value}</b>
      </li>
    {/each}
  </ul>
{/if}

<style>
  .affixes {
    list-style: none;
    margin: 0.3rem 0 0;
    padding: 0;
    display: grid;
    gap: 0.1rem;
    font-size: 0.78rem;
  }

  .affixes li {
    display: flex;
    justify-content: space-between;
    gap: 0.75rem;
    color: var(--text-dim);
  }

  .name {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
  }

  .pip {
    width: 0.45rem;
    height: 0.45rem;
    border-radius: 50%;
    flex: none;
  }

  .affixes b {
    color: var(--accent);
    font-variant-numeric: tabular-nums;
  }
</style>
