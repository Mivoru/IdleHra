<script lang="ts">
  // Modul: what this session actually produced, best first.
  //
  // The backpack is gone and everything lands in the village chest, which the
  // player is not watching. So this feed is the ONLY place a session's output
  // is visible as it happens, which makes its ordering a real decision rather
  // than a display preference.
  //
  // Sorted by RARITY descending, not by time. A chronological feed of an idle
  // session is a wall of Normal-tier scrap with the one Legendary buried four
  // hundred lines up - the exact thing a player came back to check for. Time
  // ordering is what you want when you are watching; rarity ordering is what
  // you want when you return, and returning is the whole premise of the genre.
  //
  // Aggregated by item and tier, because forty Iron Ore is one fact.

  import { lootLog, type LootEntry } from '../stores/game';
  import { itemName, type ContentRegistry } from '../net/content';
  import { rarityColor, shouldGlow } from './rarity';

  interface Props {
    registry: ContentRegistry | null;
  }

  const { registry }: Props = $props();

  /** ResponseLootDropPacket's DropKind. */
  const KIND_MATERIAL = 0;
  const KIND_EQUIPMENT = 1;

  interface Row {
    key: string;
    itemId: number;
    qualityTier: number;
    dropKind: number;
    quantity: number;
    count: number;
    newest: number;
  }

  const rows = $derived.by((): Row[] => {
    const byKey = new Map<string, Row>();

    for (const entry of $lootLog as LootEntry[]) {
      // Kind is part of the key so a material and an equipment piece that
      // happen to share an id never merge into one row.
      const key = `${entry.dropKind}:${entry.itemId}:${entry.qualityTier}`;
      const existing = byKey.get(key);
      if (existing) {
        existing.quantity += entry.quantity;
        existing.count += 1;
        existing.newest = Math.max(existing.newest, entry.atMs);
      } else {
        byKey.set(key, {
          key,
          itemId: entry.itemId,
          qualityTier: entry.qualityTier,
          dropKind: entry.dropKind,
          quantity: entry.quantity,
          count: 1,
          newest: entry.atMs,
        });
      }
    }

    return [...byKey.values()].sort(
      // Rarity first, then equipment above materials at the same tier, then
      // most recent - so the top of the list is stable and the tail is where
      // churn happens.
      (a, b) =>
        b.qualityTier - a.qualityTier ||
        (b.dropKind === KIND_EQUIPMENT ? 1 : 0) - (a.dropKind === KIND_EQUIPMENT ? 1 : 0) ||
        b.newest - a.newest,
    );
  });

  const equipmentCount = $derived(
    rows.filter((r) => r.dropKind === KIND_EQUIPMENT).reduce((n, r) => n + r.count, 0),
  );
  const materialCount = $derived(
    rows.filter((r) => r.dropKind === KIND_MATERIAL).reduce((n, r) => n + r.quantity, 0),
  );

  function label(row: Row): string {
    return itemName(registry, row.itemId);
  }
</script>

<div class="loot">
  <div class="head">
    <h3>Loot received</h3>
    {#if rows.length > 0}
      <span class="dim tiny">
        {equipmentCount} equipment &middot; {materialCount.toLocaleString()} materials
      </span>
    {/if}
  </div>

  {#if rows.length === 0}
    <p class="dim">Nothing yet.</p>
  {:else}
    <ul>
      {#each rows as row (row.key)}
        <li>
          <span
            class="name"
            style="color: {row.dropKind === KIND_MATERIAL ? 'var(--text)' : rarityColor(row.qualityTier)}"
            class:rarity-glow={row.dropKind === KIND_EQUIPMENT && shouldGlow(row.qualityTier)}
          >
            {label(row)}
          </span>

          {#if row.dropKind === KIND_EQUIPMENT}
            <span class="tag kept-tag">equipment</span>
          {/if}

          <span class="qty">
            {#if row.dropKind === KIND_EQUIPMENT}
              x{row.count}
            {:else}
              {row.quantity.toLocaleString()}
            {/if}
          </span>
        </li>
      {/each}
    </ul>
  {/if}
</div>

<style>
  .head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 0.5rem;
  }

  h3 {
    margin: 1.1rem 0 0.4rem;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  .dim {
    color: var(--text-dim);
  }
  .tiny {
    font-size: 0.72rem;
  }

  ul {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.2rem;
    max-height: 22rem;
    overflow-y: auto;
  }

  li {
    display: flex;
    align-items: baseline;
    gap: 0.45rem;
    font-size: 0.83rem;
  }

  .name {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .tag {
    font-size: 0.62rem;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: var(--text-dim);
    border: 1px solid var(--border);
    border-radius: 999px;
    padding: 0 0.35rem;
  }

  .kept-tag {
    color: var(--good);
    border-color: var(--good);
  }

  .qty {
    font-variant-numeric: tabular-nums;
    color: var(--text-dim);
    font-size: 0.78rem;
  }
</style>
