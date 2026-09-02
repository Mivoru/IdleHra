<script lang="ts">
  import type { ContentRegistry } from '../net/content';
  import { prettifyBaseId } from '../net/content';
  import ItemIcon from './ItemIcon.svelte';
  import { EQUIPMENT_SLOTS, resolveSlotIndex } from './slots';

  let { registry }: { registry: ContentRegistry } = $props();

  let search = $state('');
  let slotFilter = $state<number | -1>(-1);

  const slotLabel = (baseId: string) => {
    const index = resolveSlotIndex(baseId);
    return EQUIPMENT_SLOTS.find((s) => s.index === index)?.label ?? 'Other';
  };

  const allItems = $derived(Array.from(registry.items.values()));

  const shown = $derived.by(() => {
    const needle = search.trim().toLowerCase();
    const list = allItems.filter((item) => {
      if (slotFilter >= 0 && resolveSlotIndex(item.BaseId) !== slotFilter) return false;
      if (needle) {
        const haystack = `${prettifyBaseId(item.BaseId)} ${slotLabel(item.BaseId)}`;
        if (!haystack.toLowerCase().includes(needle)) return false;
      }
      return true;
    });

    return list.sort((a, b) => {
      return a.RegionTier - b.RegionTier || prettifyBaseId(a.BaseId).localeCompare(prettifyBaseId(b.BaseId));
    });
  });
</script>

<div class="db">
  <div class="controls">
    <input type="search" bind:value={search} placeholder="Search items..." />
    <select bind:value={slotFilter}>
      <option value={-1}>All Types</option>
      {#each EQUIPMENT_SLOTS as slot (slot.index)}
        <option value={slot.index}>{slot.label}</option>
      {/each}
    </select>
  </div>

  <p class="dim tiny count">{shown.length} items found</p>

  <div class="grid">
    {#each shown as item (item.Id)}
      <div class="card">
        <ItemIcon baseItemId={item.BaseId} name={prettifyBaseId(item.BaseId)} qualityTier={1} size="md" />
        <div class="info">
          <strong>{prettifyBaseId(item.BaseId)}</strong>
          <span class="dim tiny">{slotLabel(item.BaseId)} &middot; Tier {item.RegionTier}</span>
          <div class="stats">
            {#if item.FlatAttackPower > 0}
              <span class="stat dmg">+{item.FlatAttackPower} DMG</span>
            {/if}
            {#if item.FlatDefenseRating > 0}
              <span class="stat def">+{item.FlatDefenseRating} DEF</span>
            {/if}
            {#if item.BaseValueGold > 0}
              <span class="stat gold">{item.BaseValueGold}g</span>
            {/if}
          </div>
        </div>
      </div>
    {/each}
  </div>
</div>

<style>
  .db {
    display: flex;
    flex-direction: column;
    gap: 1rem;
    margin-top: 1.5rem;
  }
  .controls {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
  }
  .controls input, .controls select {
    padding: 0.5rem;
    background: rgba(0, 0, 0, 0.12);
    border: 1px solid var(--border);
    color: var(--text);
    border-radius: 4px;
  }
  .controls input {
    flex: 1 1 8rem;
    min-width: 0;
  }
  .grid {
    display: grid;
    /* min() so a card can never be wider than the column it sits in - the
       wiki's content column narrows independently of the viewport. */
    grid-template-columns: repeat(auto-fill, minmax(min(220px, 100%), 1fr));
    gap: 0.75rem;
    max-height: 500px;
    overflow-y: auto;
    padding-right: 0.5rem;
  }
  .card {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.75rem;
    background: rgba(0, 0, 0, 0.12);
    border: 1px solid var(--border);
    border-radius: 6px;
  }
  .info {
    display: flex;
    flex-direction: column;
    min-width: 0;
  }
  /* Modul: this was nowrap + ellipsis, which quietly truncated a third of the
     catalogue's names ("Transcendent Platelegs" became "Transcendent Plat…").
     An item database whose answer is cut off is not an answer; the card is a
     grid cell and there is nothing wrong with two lines. */
  .info strong {
    overflow-wrap: anywhere;
  }
  .stats {
    display: flex;
    gap: 0.5rem;
    margin-top: 0.25rem;
    font-size: 0.75rem;
  }
  .stat {
    padding: 0.1rem 0.3rem;
    border-radius: 3px;
    background: rgba(255,255,255,0.05);
  }
  .dmg { color: #f87171; }
  .def { color: #60a5fa; }
  .gold { color: #fbbf24; }
  .dim {
    color: var(--text-dim);
  }
  .tiny {
    font-size: 0.72rem;
  }
</style>
