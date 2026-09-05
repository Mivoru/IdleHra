<script lang="ts">
  // Modul: what this session actually produced, in TWO lists.
  //
  // Reported as "in all that time nothing better than Rare dropped from the Ice
  // Bat, and I have 23,804 kills". The database disagreed - that account holds
  // 144 Legendary, 53 Mythic, 13 Relic and 9 Ancient, and had taken a Relic
  // recently. The drops were real. This panel had thrown them away.
  //
  // One shared ring buffer held both kinds. With two characters gathering, a
  // material drop lands every few seconds and the whole buffer turned over in
  // about four minutes, evicting every piece of equipment older than that
  // whatever its rarity. The player's own diagnosis was exactly right: "with 2
  // characters on gathering the equipment probably gets overwritten straight
  // away".
  //
  // Two stores now (lootLogEquipment / lootLogMaterials), so material volume
  // cannot reach the gear, and two lists here so it cannot crowd it out
  // visually either.
  //
  // Within each list, sorted by RARITY descending rather than by time. A
  // chronological feed of an idle session is a wall of Normal-tier scrap with
  // the one Legendary buried four hundred lines up - the exact thing a player
  // came back to check for. Aggregated by item and tier, because forty Iron Ore
  // is one fact.

  import { lootLogEquipment, lootLogMaterials, type LootEntry } from '../stores/game';
  import { itemName, type ContentRegistry } from '../net/content';
  import { rarityColor, shouldGlow, rarityName, killsPerRarity } from './rarity';
  import Burst from './Burst.svelte';

  interface Props {
    registry: ContentRegistry | null;
    /**
     * Modul: GATHERING HAS NO EQUIPMENT SECTION.
     *
     * A node drops materials and nothing else, so on that screen the equipment
     * list is permanently "Nothing yet." under a line quoting the odds of a
     * Legendary per KILL - a panel telling a fisherman his fishing is failing
     * at something fishing does not do. Combat shows both.
     */
    showEquipment?: boolean;
  }

  const { registry, showEquipment = true }: Props = $props();

  interface Row {
    key: string;
    itemId: number;
    qualityTier: number;
    dropKind: number;
    quantity: number;
    count: number;
    newest: number;
  }

  function aggregate(entries: LootEntry[]): Row[] {
    const byKey = new Map<string, Row>();

    for (const entry of entries) {
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
      (a, b) => b.qualityTier - a.qualityTier || b.newest - a.newest,
    );
  }

  const equipmentRows = $derived(aggregate($lootLogEquipment as LootEntry[]));
  const materialRows = $derived(aggregate($lootLogMaterials as LootEntry[]));

  const equipmentCount = $derived(equipmentRows.reduce((n, r) => n + r.count, 0));
  const materialCount = $derived(materialRows.reduce((n, r) => n + r.quantity, 0));

  // Modul: SAY HOW RARE RARE ACTUALLY IS.
  //
  // Asked, in effect, by "it's strange that nothing better than Rare dropped
  // and I have 23,804 kills". Nothing was wrong - gear drops on 15% of kills
  // and Legendary-or-better is 0.85% of those, so it is one kill in about
  // thirteen hundred. Twenty-four thousand kills is a couple of dozen, and the
  // chest sweep deletes everything up to Epic, so what a player SEES at the top
  // is whatever they last fused.
  //
  // The odds were computable all along - rarityOdds() was written, exported and
  // imported by nothing. A player counting kills against a number nobody showed
  // them will conclude the game is broken, and be reasonable about it.
  const odds = [
    { tier: 7, kills: killsPerRarity(7) },
    { tier: 10, kills: killsPerRarity(10) },
  ];
</script>

<div class="loot">
  <h2>Loot drops</h2>

  {#if showEquipment}
  <section class="lootsection">
    <div class="head">
      <h3>Equipment</h3>
      {#if equipmentCount > 0}
        <span class="dim tiny">{equipmentCount} piece{equipmentCount === 1 ? '' : 's'}</span>
      {/if}
    </div>

    <p class="dim tiny odds">
      {#each odds as row, i}{i > 0 ? ' · ' : ''}{rarityName(row.tier)}+ about 1 in {row.kills.toLocaleString()} kills{/each}
    </p>

    {#if equipmentRows.length === 0}
      <p class="dim tiny">Nothing yet.</p>
    {:else}
      <ul>
        {#each equipmentRows as row (row.key)}
          {@const isRare = shouldGlow(row.qualityTier)}
          <li class:rare={isRare} class:folk-sweep={isRare}>
            <!-- Modul: A TOP-TIER DROP LOOKED LIKE EVERY OTHER LINE OF TEXT.
                 Gated on shouldGlow - tier 10 and up - for the reason that
                 function exists: an effect on every drop is an effect on none. -->
            {#if isRare}
              <span class="burstwrap">
                <Burst color={rarityColor(row.qualityTier)} reach={2.4} count={10} />
              </span>
            {/if}

            <!-- Modul: NAME THE RARITY, do not only colour it.
                 The same base item at three different qualities renders as
                 three rows, and without the tier they read as duplicates of
                 one thing - on a panel whose entire purpose is telling the
                 player how good a drop was. Colour alone also fails anyone who
                 cannot separate the fourteen hues. -->
            <span class="name" style="color: {rarityColor(row.qualityTier)}" class:rarity-glow={isRare}>
              {itemName(registry, row.itemId)}
            </span>
            <span class="tier" style="color: {rarityColor(row.qualityTier)}">{rarityName(row.qualityTier)}</span>
            <span class="qty">x{row.count}</span>
          </li>
        {/each}
      </ul>
    {/if}
  </section>
  {/if}

  <section class="lootsection">
    <div class="head">
      <h3>Materials</h3>
      {#if materialCount > 0}
        <span class="dim tiny">{materialCount.toLocaleString()}</span>
      {/if}
    </div>

    {#if materialRows.length === 0}
      <p class="dim tiny">Nothing yet.</p>
    {:else}
      <ul>
        {#each materialRows as row (row.key)}
          <li>
            <span class="name">{itemName(registry, row.itemId)}</span>
            <span class="qty">{row.quantity.toLocaleString()}</span>
          </li>
        {/each}
      </ul>
    {/if}
  </section>
</div>

<style>
  .loot {
    display: flex;
    flex-direction: column;
    gap: 0.9rem;
  }

  .lootsection {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
  }

  .head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 0.5rem;
  }

  h3 {
    margin: 0;
    font-size: 0.9rem;
  }

  ul {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.15rem;
    /* Modul: each list scrolls on its OWN, so a long material run cannot push
       the equipment list off the screen - which is the display half of the
       defect the split buffers fixed. */
    max-height: 16rem;
    overflow-y: auto;
  }

  li {
    position: relative;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
    font-size: 0.82rem;
    padding: 0.1rem 0;
    border-bottom: 1px solid var(--border);
  }

  .name {
    flex: 1 1 auto;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .tier {
    font-size: 0.72rem;
    opacity: 0.85;
    flex: 0 0 auto;
    white-space: nowrap;
  }

  .qty {
    font-variant-numeric: tabular-nums;
    opacity: 0.85;
    flex: 0 0 auto;
  }

  .burstwrap {
    position: absolute;
    inset: 0;
    pointer-events: none;
  }

  .odds {
    margin: 0 0 0.15rem;
    opacity: 0.55;
  }

  .dim {
    opacity: 0.7;
  }

  .tiny {
    font-size: 0.75rem;
  }
</style>
