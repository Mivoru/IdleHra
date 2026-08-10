<script lang="ts">
  /**
   * One way to look through equipment you own.
   *
   * Modul: this exists because the chest and the market's sell form each
   * answered "which of my things is this" differently, and one of them answered
   * it with a `<select>` - a single line of text per item, no picture, no slot,
   * no way to search, in a game whose whole reward loop is items that differ by
   * rarity and affixes. Picking the right piece out of two hundred meant
   * reading two hundred lines of "Iron Greataxe [Rare]".
   *
   * The market's BUY side already had the right shape: search, filter by kind,
   * filter by rarity. This is that shape, over the stock a player is carrying,
   * shared so the two screens cannot drift apart again.
   */
  import ItemIcon from './ItemIcon.svelte';
  import { rarityColor, rarityName, MAX_QUALITY_TIER } from './rarity';
  import { EQUIPMENT_SLOTS, resolveSlotIndex } from './slots';
  import { prettifyBaseId } from '../net/content';

  type Item = {
    Id: number;
    BaseItemId: string;
    QualityTier: number;
  };

  let {
    items = [],
    selectedId = 0,
    onselect,
    emptyText = 'Nothing here.',
    compact = false,
  }: {
    items: Item[];
    selectedId?: number;
    onselect?: (item: Item) => void;
    emptyText?: string;
    compact?: boolean;
  } = $props();

  let search = $state('');
  let slotFilter = $state<number | -1>(-1);
  let minRarity = $state(0);
  let sortBy = $state<'rarity' | 'name' | 'slot'>('rarity');

  const slotLabel = (baseId: string) => {
    const index = resolveSlotIndex(baseId);
    return EQUIPMENT_SLOTS.find((s) => s.index === index)?.label ?? 'Other';
  };

  const shown = $derived.by(() => {
    const needle = search.trim().toLowerCase();
    const list = items.filter((item) => {
      if (minRarity > 0 && item.QualityTier < minRarity) return false;
      if (slotFilter >= 0 && resolveSlotIndex(item.BaseItemId) !== slotFilter) return false;
      if (needle) {
        const haystack = `${prettifyBaseId(item.BaseItemId)} ${slotLabel(item.BaseItemId)} ${rarityName(item.QualityTier)}`;
        if (!haystack.toLowerCase().includes(needle)) return false;
      }
      return true;
    });

    return list.sort((a, b) => {
      if (sortBy === 'rarity') return b.QualityTier - a.QualityTier;
      if (sortBy === 'slot') return slotLabel(a.BaseItemId).localeCompare(slotLabel(b.BaseItemId));
      return prettifyBaseId(a.BaseItemId).localeCompare(prettifyBaseId(b.BaseItemId));
    });
  });

  // Which kinds are actually present. Offering a "Leggings" filter to someone
  // who owns no leggings is a filter that can only ever empty the list.
  const presentSlots = $derived.by(() => {
    const seen = new Set<number>();
    for (const item of items) seen.add(resolveSlotIndex(item.BaseItemId));
    return EQUIPMENT_SLOTS.filter((s) => seen.has(s.index));
  });
</script>

<div class="browser" class:compact>
  <div class="controls">
    <input
      type="search"
      placeholder="Search name, kind or rarity..."
      bind:value={search}
      aria-label="Search your items"
    />

    <select bind:value={slotFilter} aria-label="Filter by kind">
      <option value={-1}>All kinds</option>
      {#each presentSlots as slot (slot.index)}
        <option value={slot.index}>{slot.label}</option>
      {/each}
    </select>

    <select bind:value={minRarity} aria-label="Minimum rarity">
      <option value={0}>Any rarity</option>
      {#each Array(MAX_QUALITY_TIER) as _, i}
        <option value={i + 1}>{rarityName(i + 1)}+</option>
      {/each}
    </select>

    <select bind:value={sortBy} aria-label="Sort by">
      <option value="rarity">Rarity</option>
      <option value="name">Name</option>
      <option value="slot">Kind</option>
    </select>
  </div>

  <p class="dim tiny count">
    {shown.length} of {items.length} shown
  </p>

  {#if items.length === 0}
    <p class="dim">{emptyText}</p>
  {:else if shown.length === 0}
    <p class="dim">Nothing matches that filter.</p>
  {:else}
    <ul class="items">
      {#each shown as item (item.Id)}
        <li>
          <button
            class="row"
            class:selected={item.Id === selectedId}
            onclick={() => onselect?.(item)}
          >
            <ItemIcon
              baseItemId={item.BaseItemId}
              name={prettifyBaseId(item.BaseItemId)}
              qualityTier={item.QualityTier}
              size={compact ? 'sm' : 'md'}
            />
            <span class="text">
              <span class="name">{prettifyBaseId(item.BaseItemId)}</span>
              <span class="meta dim tiny">
                {slotLabel(item.BaseItemId)}
                &middot;
                <span class="rar" style="color: {rarityColor(item.QualityTier)}">
                  {rarityName(item.QualityTier)}
                </span>
              </span>
            </span>
          </button>
        </li>
      {/each}
    </ul>
  {/if}
</div>

<style>
  .browser {
    display: grid;
    gap: 0.4rem;
    min-width: 0;
  }

  .controls {
    display: grid;
    grid-template-columns: minmax(8rem, 2fr) repeat(3, minmax(6rem, 1fr));
    gap: 0.35rem;
  }

  .controls input,
  .controls select {
    min-width: 0;
    width: 100%;
  }

  .count {
    margin: 0;
  }

  .items {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.25rem;
    max-height: 22rem;
    overflow-y: auto;
  }

  .compact .items {
    max-height: 14rem;
  }

  .row {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    width: 100%;
    padding: 0.3rem 0.4rem;
    background: transparent;
    border: 1px solid transparent;
    border-radius: 0.35rem;
    text-align: left;
    cursor: pointer;
    color: inherit;
    font: inherit;
  }

  .row:hover {
    border-color: currentColor;
  }

  .row.selected {
    border-color: currentColor;
    background: rgba(127, 127, 127, 0.18);
  }

  .text {
    display: grid;
    gap: 0.1rem;
    min-width: 0;
  }

  .name {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* Modul: the controls stack on a phone rather than squeezing four inputs
     onto a 360px row, where each ends up too narrow to read its own label. */
  @media (max-width: 560px) {
    .controls {
      grid-template-columns: 1fr 1fr;
    }

    .controls input {
      grid-column: 1 / -1;
    }
  }
</style>
