<script lang="ts">
  import type { AffixMap } from '../net/rest';
  import { toDisplayAffixes } from './affixes';
  import { loadContent, type ContentRegistry } from '../net/content';
  import { onMount } from 'svelte';
  import { pieceTotals } from './lootCompare';
  import { familiesQuery, indexFamilies, boostedValue, FAMILY_COLOR } from './affixFamilies';

  interface Props {
    affixes: AffixMap;
    baseItemId?: string;
    /** The piece's own quality tier; the base lines are scaled by it. */
    qualityTier?: number;
    /**
     * Show each affix's family and the viewer's tree boost (phase 5.1). Off
     * for another player's gear: the boost is the viewer's own tree, not theirs.
     */
    families?: boolean;
    /** Only the item's own base lines - for a screen that lists the affixes itself (the Forge). */
    baseOnly?: boolean;
  }

  let { affixes, baseItemId, qualityTier = 1, families = true, baseOnly = false }: Props = $props();

  // Modul: THE TREE'S MARK ON THE GEAR (phase 5.1, 2026-11-03). A family chip
  // in its column's colour on every affix, and - when the tree boosts that
  // family - the value it actually fights with, beside the rolled one.
  const familyData = familiesQuery();
  const familyIndex = $derived(indexFamilies(familyData.data));

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
  const rows = $derived(baseOnly ? [] : toDisplayAffixes(affixes));
  const baseStats = $derived.by(() => {
    if (!registry || !baseItemId) return [];
    // Modul: scaled by the piece's quality exactly as EquipmentSlotEngine
    // scales it. The authored number is the same at every rarity, so these
    // lines used to read a Legendary sword and a Normal one as equal.
    const totals = pieceTotals(registry, baseItemId, qualityTier);
    const stats = [];
    if (totals.attack > 0) stats.push({ label: 'Attack', value: totals.attack });
    if (totals.defense > 0) stats.push({ label: 'Defense', value: totals.defense });
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
      {@const fam = familyIndex.byAffix.get(row.id)}
      {@const boosted = families ? boostedValue(row.id, row.magnitude, fam) : null}
      <li>
        <span class="name" title={`${row.rarityName} affix`}>
          <i class="pip" style="background: {RARITY_COLOR[row.rarity] ?? 'var(--text-dim)'}"></i>
          {row.label}
          {#if fam}
            <span
              class="fam"
              style="--fam: {FAMILY_COLOR[fam.Key] ?? 'var(--text-dim)'}"
              title={families && fam.BoostTenths > 0
                ? `${fam.Name} affix: your ${fam.Column} tree makes it ${fam.BoostTenths / 10}% stronger`
                : `${fam.Name} affix: the ${fam.Column} column's Affinity node would make it stronger`}
              data-testid="affix-family"
            >{fam.Name}</span>
          {/if}
        </span>
        {#if boosted}
          <span class="val">
            <s class="dim">{row.value}</s>
            <b class="boosted" data-testid="affix-boosted">{boosted}</b>
          </span>
        {:else}
          <b>{row.value}</b>
        {/if}
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

  /* A small tag in the family's tree-column colour. */
  .fam {
    font-size: 0.62rem;
    font-weight: 700;
    letter-spacing: 0.03em;
    text-transform: uppercase;
    padding: 0 0.35rem;
    border-radius: 999px;
    border: 1px solid color-mix(in srgb, var(--fam) 70%, transparent);
    color: var(--fam);
    background: color-mix(in srgb, var(--fam) 12%, transparent);
    line-height: 1.4;
    white-space: nowrap;
  }

  .val {
    display: inline-flex;
    align-items: baseline;
    gap: 0.35rem;
    white-space: nowrap;
  }
  .val s {
    font-size: 0.7rem;
  }
  .boosted {
    color: var(--good) !important;
  }
</style>
