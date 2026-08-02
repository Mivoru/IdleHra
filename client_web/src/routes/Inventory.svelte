<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { connection } from '../lib/net/connection';
  import { CommandType } from '../lib/net/protocol.generated';
  import { playerState } from '../lib/stores/game';
  import { queryKeys, fetchInventory } from '../lib/net/rest';
  import { prettifyBaseId } from '../lib/net/content';
  import { resolveSlotIndex, EQUIPMENT_SLOTS } from '../lib/ui/slots';
  import { rarityColor, rarityName, shouldGlow } from '../lib/ui/rarity';
  import Affixes from '../lib/ui/Affixes.svelte';

  const inventory = createQuery(() => ({ queryKey: queryKeys.inventory, queryFn: fetchInventory }));

  const snap = $derived($playerState);

  const slotLabel = new Map(EQUIPMENT_SLOTS.map((s) => [s.index, s.label]));

  const equipment = $derived([...(inventory.data?.Equipment ?? [])].sort((a, b) => {
    // Carried first - the only rows the player can act on - then by rarity
    // descending, so the best available piece per slot is at the top.
    if (a.IsEquipped !== b.IsEquipped) return a.IsEquipped ? 1 : -1;
    return b.QualityTier - a.QualityTier;
  }));

  const stacks = $derived(
    [...(inventory.data?.Stacks ?? [])]
      .filter((s) => s.BackpackQuantity > 0 || s.StashQuantity > 0)
      .sort((a, b) => a.ItemId.localeCompare(b.ItemId)),
  );

  function refetchSoon() {
    // The command resolves on the tick thread; refetching immediately would
    // race it and read the pre-command rows straight back into the cache.
    setTimeout(() => inventory.refetch(), 400);
  }

  function equip(instanceId: number) {
    connection.send({ Command: CommandType.EquipItem, TargetId: instanceId });
    refetchSoon();
  }

  // Modul: UnequipItem takes a SLOT INDEX, not an instance id - see
  // Character.svelte's fuller note. The slot is derived from the BaseItemId
  // through the same resolver the server uses.
  function unequip(baseItemId: string) {
    const slotIndex = resolveSlotIndex(baseItemId);
    if (slotIndex < 0) return;
    connection.send({ Command: CommandType.UnequipItem, TargetId: slotIndex });
    refetchSoon();
  }
</script>

<div class="grid">
  <section class="panel">
    <div class="head">
      <h2>Equipment</h2>
      <button class="tiny-btn" onclick={() => inventory.refetch()} disabled={inventory.isFetching}>
        {inventory.isFetching ? 'Refreshing...' : 'Refresh'}
      </button>
    </div>

    {#if snap}
      <p class="dim small">
        Backpack {snap.InventoryCapacity - snap.InventorySpaceRemaining}/{snap.InventoryCapacity}
        {#if snap.InventorySpaceRemaining === 0}
          &middot; <span class="warn">full - drops are being discarded</span>
        {/if}
      </p>
    {/if}

    {#if inventory.isPending}
      <p class="dim">Loading...</p>
    {:else if inventory.isError}
      <p class="err">Could not load inventory: {inventory.error?.message}</p>
    {:else if equipment.length === 0}
      <p class="dim">No equipment yet. Kill something.</p>
    {:else}
      <ul class="items">
        {#each equipment as item (item.Id)}
          {@const slot = resolveSlotIndex(item.BaseItemId)}
          <li>
            <div class="line">
              <span
                style="color: {rarityColor(item.QualityTier)}"
                class:rarity-glow={shouldGlow(item.QualityTier)}
              >
                {prettifyBaseId(item.BaseItemId)}
              </span>
              {#if item.IsEquipped}
                <button class="tiny-btn" onclick={() => unequip(item.BaseItemId)}>Unequip</button>
              {:else}
                <button class="tiny-btn" onclick={() => equip(item.Id)}>Equip</button>
              {/if}
            </div>
            <div class="dim tiny">
              [{rarityName(item.QualityTier)}]
              &middot; {slot >= 0 ? (slotLabel.get(slot) ?? 'Unknown slot') : 'Not equippable'}
              {#if item.IsEquipped}
                &middot; worn by character {item.EquippedByCharacterSlot + 1}
              {/if}
              {#if item.IsAffixLocked}&middot; affixes locked{/if}
            </div>
            <Affixes affixes={item.Affixes} />
          </li>
        {/each}
      </ul>
    {/if}
  </section>

  <section class="panel">
    <h2>Materials</h2>
    <p class="dim small">
      Backpack and village stash. Crafting spends the unified balance, so both
      columns count toward a recipe.
    </p>

    {#if inventory.isPending}
      <p class="dim">Loading...</p>
    {:else if stacks.length === 0}
      <p class="dim">Nothing stored.</p>
    {:else}
      <table>
        <thead>
          <tr><th>Item</th><th>Backpack</th><th>Stash</th></tr>
        </thead>
        <tbody>
          {#each stacks as stack (stack.ItemId)}
            <tr>
              <td>{prettifyBaseId(stack.ItemId)}</td>
              <td class="num">{stack.BackpackQuantity.toLocaleString()}</td>
              <td class="num">{stack.StashQuantity.toLocaleString()}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    {/if}
  </section>
</div>

<style>
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(21rem, 1fr));
    gap: 1rem;
    padding: 1rem;
    align-items: start;
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
  }

  .head {
    display: flex;
    align-items: center;
    justify-content: space-between;
  }

  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
  }

  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.8rem;
    margin: 0 0 0.6rem;
  }
  .tiny {
    font-size: 0.72rem;
  }
  .warn {
    color: var(--danger);
  }
  .err {
    color: var(--danger);
  }

  .items {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.5rem;
    max-height: 32rem;
    overflow-y: auto;
  }

  .items li {
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.4rem;
    font-size: 0.85rem;
  }

  .line {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 0.6rem;
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.45rem;
    flex: none;
  }

  table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.85rem;
  }

  th {
    text-align: left;
    font-size: 0.72rem;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: var(--text-dim);
    font-weight: 600;
    padding-bottom: 0.3rem;
  }

  td {
    border-top: 1px solid var(--border);
    padding: 0.25rem 0;
  }

  .num {
    text-align: right;
    font-variant-numeric: tabular-nums;
    width: 6rem;
  }
</style>
