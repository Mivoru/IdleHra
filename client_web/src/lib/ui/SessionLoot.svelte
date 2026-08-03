<script lang="ts">
  // Modul: what this session actually produced, best first.
  //
  // The backpack is gone: materials go straight to the village chest and
  // equipment to the bank or to scrap, none of which the player watches. So
  // this feed is now the ONLY place a session's output is visible, which makes
  // its ordering a real decision rather than a display preference.
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
  import { rarityColor, rarityName, shouldGlow } from './rarity';

  interface Props {
    registry: ContentRegistry | null;
  }

  const { registry }: Props = $props();

  /** ResponseLootDropPacket's DropKind. */
  const KIND_MATERIAL = 0;
  const KIND_EQUIPMENT = 1;
  const KIND_SCRAP = 2;

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
      // Kind is part of the key: a Legendary that was KEPT and a Legendary
      // that was SCRAPPED are different outcomes and merging them would hide
      // the one the player would want to change a setting over.
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
      // Rarity first, then equipment above the material it would scrap into,
      // then most recent - so the top of the list is stable and the tail is
      // where churn happens.
      (a, b) =>
        b.qualityTier - a.qualityTier ||
        (b.dropKind === KIND_EQUIPMENT ? 1 : 0) - (a.dropKind === KIND_EQUIPMENT ? 1 : 0) ||
        b.newest - a.newest,
    );
  });

  const kept = $derived(rows.filter((r) => r.dropKind === KIND_EQUIPMENT).length);
  const scrapped = $derived(rows.filter((r) => r.dropKind === KIND_SCRAP).reduce((n, r) => n + r.count, 0));

  function label(row: Row): string {
    if (row.dropKind === KIND_SCRAP) {
      // The scrap event carries the tier of the piece that was broken down,
      // so it can say what was given up rather than reporting anonymous ore.
      return `${rarityName(row.qualityTier)} scrapped`;
    }
    return itemName(registry, row.itemId);
  }
</script>

<div class="loot">
  <div class="head">
    <h3>Loot received</h3>
    {#if rows.length > 0}
      <span class="dim tiny">{kept} kept &middot; {scrapped} scrapped</span>
    {/if}
  </div>

  {#if rows.length === 0}
    <p class="dim">Nothing yet.</p>
  {:else}
    <ul>
      {#each rows as row (row.key)}
        <li class:scrap={row.dropKind === KIND_SCRAP}>
          <span
            class="name"
            style="color: {row.dropKind === KIND_MATERIAL ? 'var(--text)' : rarityColor(row.qualityTier)}"
            class:rarity-glow={row.dropKind === KIND_EQUIPMENT && shouldGlow(row.qualityTier)}
          >
            {label(row)}
          </span>

          {#if row.dropKind === KIND_EQUIPMENT}
            <span class="tag kept-tag">to bank</span>
          {:else if row.dropKind === KIND_SCRAP}
            <span class="tag">to chest</span>
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

  /* Scrap is dimmed rather than hidden: it is most of the volume and none of
     the interest, but a player deciding where to set the keep threshold needs
     to see how much is going that way. */
  li.scrap {
    opacity: 0.62;
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
