<script lang="ts">
  import type { AffixMap } from '../net/rest';
  import { toDisplayAffixes } from './affixes';
  import { loadContent, type ContentRegistry } from '../net/content';
  import { onMount } from 'svelte';

  interface Props {
    affixes: AffixMap;
    baseItemId?: string;
  }

  let { affixes, baseItemId }: Props = $props();

  let registry = $state<ContentRegistry | null>(null);
  onMount(async () => {
    registry = await loadContent().catch(() => null);
  });

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
  const baseStats = $derived.by(() => {
    if (!registry || !baseItemId) return [];
    const item = registry.itemsByBaseId.get(baseItemId);
    if (!item) return [];
    const stats = [];
    if (item.FlatAttackPower > 0) stats.push({ label: 'Attack', value: item.FlatAttackPower });
    if (item.FlatDefenseRating > 0) stats.push({ label: 'Defense', value: item.FlatDefenseRating });
    return stats;
  });
</script>

{#if baseStats.length > 0 || rows.length > 0}
  <ul class="affixes">
    {#each baseStats as stat}
      <li>
        <span class="name" title="Base statistic">
          <i class="pip" style="background: var(--text-dim)"></i>
          {stat.label}
        </span>
        <b style="color: var(--text)">{stat.value}</b>
      </li>
    {/each}
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
