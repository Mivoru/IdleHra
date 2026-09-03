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
  import VirtualList from './VirtualList.svelte';
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

  // Modul: memoised, because the sort comparator calls it.
  //
  // This was a linear `EQUIPMENT_SLOTS.find` on every call, and the "sort by
  // kind" comparator calls it twice per comparison - O(n log n) scans of the
  // slot table. With 17,836 items that is roughly half a million array walks
  // for one sort. The answer depends only on the base id, of which there are
  // about 75 in the whole catalogue, so the cache cannot grow unbounded.
  const slotLabels = new Map<string, string>();

  const slotLabel = (baseId: string) => {
    const memo = slotLabels.get(baseId);
    if (memo !== undefined) return memo;

    const index = resolveSlotIndex(baseId);
    const label = EQUIPMENT_SLOTS.find((s) => s.index === index)?.label ?? 'Other';
    slotLabels.set(baseId, label);
    return label;
  };

  const shown = $derived.by(() => {
    const needle = search.trim().toLowerCase();
    const list = items.filter((item) => {
      // Integer compares first, string work last - the search allocates a
      // haystack per row and most rows are rejected before it is asked.
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

  // Modul: the virtual list needs one number in pixels, and the rows are two
  // sizes. Kept next to the CSS that produces them - a mismatch here does not
  // error, it silently overlaps or gaps the rows, which is the kind of bug
  // that gets reported as "the list looks weird sometimes".
  const rowHeight = $derived(compact ? 34 : 44);
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
    <!-- Modul: WINDOWED. This was a plain {#each} over everything the player
         carries, in a box 22rem tall. The Forge's "show all" and the market's
         sell form both feed it the whole chest, which reached 17,836 pieces on
         one live account. See VirtualList. -->
    <VirtualList
      items={shown}
      {rowHeight}
      maxHeight={compact ? '14rem' : '22rem'}
      label="Your items"
    >
      {#snippet row(item: Item)}
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
      {/snippet}
    </VirtualList>
  {/if}
</div>

<style>
  .browser {
    display: grid;
    gap: 0.4rem;
    min-width: 0;
  }

  /* Modul: WRAPS ON THE PANEL'S WIDTH, NOT THE WINDOW'S.
     This was `minmax(8rem, 2fr) repeat(3, minmax(6rem, 1fr))` - a 26rem
     minimum - with a `@media (max-width: 560px)` rule underneath to stack it
     on a phone. The media query asks the wrong question: every screen lays its
     panels out with `repeat(auto-fit, minmax(19..21rem, 1fr))`, so this
     browser sits in a ~19rem column on a 1440px desktop as readily as on a
     handset. On the Forge at a two-column width the four controls, the count
     and the whole item list ran 116px past the panel's right edge and were cut
     off at the window.

     Flex with a basis instead: the row breaks when the CONTAINER runs out,
     whatever the viewport is doing. Same fix as the guild buff tiers.
     min-width: 0 is load-bearing - a flex item defaults to min-content and a
     <select> insists on fitting its longest option ("Ultra Rare+"). */
  .controls {
    display: flex;
    flex-wrap: wrap;
    gap: 0.35rem;
  }

  .controls input {
    flex: 1 1 12rem;
    min-width: 0;
  }

  .controls select {
    flex: 1 1 6.5rem;
    min-width: 0;
  }

  .count {
    margin: 0;
  }

  /* Modul: these two heights ARE the `rowHeight` the virtual list is given -
     44px normally, 34px compact - because it positions rows arithmetically
     rather than by measuring them. Change one and change the other, or the
     rows overlap. box-sizing is load-bearing for the same reason: the padding
     has to be inside the number. */
  .row {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    width: 100%;
    height: 44px;
    box-sizing: border-box;
    padding: 0.3rem 0.4rem;
    background: transparent;
    border: 1px solid transparent;
    border-radius: 0.35rem;
    text-align: left;
    cursor: pointer;
    color: inherit;
    font: inherit;
  }

  .compact .row {
    height: 34px;
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
</style>
