<script lang="ts">
  import { onMount } from 'svelte';
  import { createQuery } from '@tanstack/svelte-query';
  import { playerState } from '../lib/stores/game';
  import { connection } from '../lib/net/connection';
  import { CommandType } from '../lib/net/protocol.generated';
  import { queryKeys, fetchInventory } from '../lib/net/rest';
  import { loadContent, prettifyBaseId, isFood, type ContentRegistry } from '../lib/net/content';

  const inventory = createQuery(() => ({ queryKey: queryKeys.inventory, queryFn: fetchInventory }));

  let registry = $state<ContentRegistry | null>(null);
  onMount(async () => {
    registry = await loadContent().catch(() => null);
  });

  const snap = $derived($playerState);

  // Modul: the larder is three slots, mirrored from TickStatePayload's
  // Food{1,2,3}_ItemId/_Count. Before StockFoodSlot existed nothing anywhere
  // could put food in them - not a command, not a UI, not persistence - so
  // every player's larder was permanently empty and any combat activity halted
  // the first time HP crossed the auto-eat threshold. This screen is the input
  // side of that.
  const slots = $derived(
    snap
      ? [
          { index: 0, itemId: snap.Food1_ItemId, count: snap.Food1_Count },
          { index: 1, itemId: snap.Food2_ItemId, count: snap.Food2_Count },
          { index: 2, itemId: snap.Food3_ItemId, count: snap.Food3_Count },
        ]
      : [],
  );

  // Modul: food anywhere in the village chest, not just the backpack half of
  // it. LarderEngine stocks through InventoryAndStashSystem.TryConsumeUnified,
  // which already draws from both CommodityRecords and the stash - this screen
  // was the only thing still splitting them, so food sitting in the stash was
  // invisible and unloadable even though the server would have taken it.
  const availableFood = $derived(
    (inventory.data?.Stacks ?? [])
      .map((s) => ({ ...s, total: s.BackpackQuantity + s.StashQuantity }))
      .filter((s) => isFood(s.ItemId) && s.total > 0)
      .map((s) => ({
        baseId: s.ItemId,
        quantity: s.total,
        // Commands carry the numeric ContentRegistry id; REST carries BaseIds.
        numericId: registry?.itemsByBaseId.get(s.ItemId)?.Id ?? 0,
      }))
      .filter((f) => f.numericId > 0)
      .sort((a, b) => a.baseId.localeCompare(b.baseId)),
  );

  let selectedFood = $state('');
  let amount = $state(100);

  // LarderLimits.SlotCapacity. A slot cannot hold more, and a request over it
  // is clamped server-side - showing the real ceiling avoids the player
  // wondering why 5000 became 999.
  const SLOT_CAPACITY = 999;

  function foodName(itemId: number): string {
    if (itemId === 0) return '';
    const item = registry?.items.get(itemId);
    return item ? prettifyBaseId(item.BaseId) : `Item #${itemId}`;
  }

  function refetchSoon() {
    setTimeout(() => inventory.refetch(), 400);
  }

  function stock(slotIndex: number) {
    const food = availableFood.find((f) => f.baseId === selectedFood);
    if (!food) return;

    connection.send({
      Command: CommandType.StockFoodSlot,
      ConsumableItemId: food.numericId,
      TargetSlotIndex: slotIndex,
      DepositQuantity: Math.min(amount, food.quantity, SLOT_CAPACITY),
    });
    refetchSoon();
  }

  function unload(slotIndex: number) {
    // DepositQuantity 0 means "unload this slot back into the chest".
    connection.send({
      Command: CommandType.StockFoodSlot,
      TargetSlotIndex: slotIndex,
      DepositQuantity: 0,
    });
    refetchSoon();
  }

  let threshold = $state(0);
  let thresholdTouched = $state(false);

  $effect(() => {
    // Follow the server until the player starts dragging, then stop fighting
    // them for control of the slider.
    if (snap && !thresholdTouched) threshold = snap.AutoEatThreshold;
  });

  // Modul: the threshold rides on LimitPrice, NOT TargetId.
  //
  // LimitPrice is nominally a market-order price field; UpdateAutoEatThreshold
  // reuses it, as does RerollItemAffix for an affix index. This client sent
  // TargetId first and the setting silently did nothing - worse, LimitPrice
  // defaulted to 0, so every attempt to RAISE the threshold actually set it to
  // zero. Nothing reported an error, because 0 is a valid threshold.
  //
  // Range is 0-100 inclusive; ValidateCombatConfiguration DISCONNECTS the
  // session for anything outside it rather than clamping, which is why the
  // slider is bounded rather than free-typed.
  function applyThreshold() {
    connection.send({ Command: CommandType.UpdateAutoEatThreshold, LimitPrice: threshold });
    thresholdTouched = false;
  }
</script>

<div class="grid">
  <section class="panel">
    <h2>Auto-Eat</h2>
    <p class="dim small">
      Load up to three foods. When health drops below the threshold your
      character eats the one that heals most, automatically.
    </p>
    <p class="dim tiny">
      Running out no longer stops you - you simply stop healing, and keep
      fighting until you win or die.
    </p>

    <ul class="slots">
      {#each slots as slot}
        <li>
          <span class="idx dim">Slot {slot.index + 1}</span>
          {#if slot.itemId > 0}
            <span class="name">{foodName(slot.itemId)}</span>
            <span class="count">{slot.count.toLocaleString()}</span>
            <button class="tiny-btn" onclick={() => unload(slot.index)}>Unload</button>
          {:else}
            <span class="name dim empty">empty</span>
            <span class="count dim">0</span>
            <button
              class="tiny-btn"
              disabled={!selectedFood}
              onclick={() => stock(slot.index)}
            >
              Load
            </button>
          {/if}
        </li>
      {/each}
    </ul>

    <h3>Load from the village chest</h3>
    {#if availableFood.length === 0}
      <p class="dim">
        No food in the chest. Cook something, or fish it up.
      </p>
    {:else}
      <div class="loader">
        <select bind:value={selectedFood}>
          <option value="">Choose food...</option>
          {#each availableFood as food}
            <option value={food.baseId}>
              {prettifyBaseId(food.baseId)} ({food.quantity.toLocaleString()})
            </option>
          {/each}
        </select>
        <input type="number" min="1" max={SLOT_CAPACITY} bind:value={amount} />
      </div>
      <p class="dim tiny">Slots hold at most {SLOT_CAPACITY}; larger requests are clamped.</p>
    {/if}
  </section>

  <section class="panel">
    <h2>When to eat</h2>
    {#if snap}
      <p class="dim small">
        Eats as soon as health falls below this share of maximum. Higher wastes
        food on scratches; lower risks dying between bites. Set it to 0 to
        never auto-eat.
      </p>

      <div class="threshold">
        <input
          type="range"
          min="0"
          max="100"
          bind:value={threshold}
          oninput={() => (thresholdTouched = true)}
        />
        <output>{threshold}</output>
      </div>

      <button onclick={applyThreshold} disabled={threshold === snap.AutoEatThreshold}>
        {threshold === snap.AutoEatThreshold ? `Applied (${snap.AutoEatThreshold})` : `Set to ${threshold}`}
      </button>
    {:else}
      <p class="dim">Waiting for state...</p>
    {/if}
  </section>
</div>

<style>
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(20rem, 1fr));
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

  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
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
  .small {
    font-size: 0.8rem;
    margin: 0 0 0.7rem;
  }
  .tiny {
    font-size: 0.72rem;
    margin: 0.35rem 0 0;
  }

  .slots {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.4rem;
  }

  .slots li {
    display: grid;
    grid-template-columns: 3.6rem 1fr auto auto;
    gap: 0.5rem;
    align-items: center;
    font-size: 0.85rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.35rem;
  }

  .idx {
    font-size: 0.75rem;
  }

  .empty {
    font-style: italic;
  }

  .count {
    font-variant-numeric: tabular-nums;
    font-weight: 700;
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.45rem;
  }

  .loader {
    display: grid;
    grid-template-columns: 1fr 5.5rem;
    gap: 0.5rem;
  }

  select {
    font: inherit;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.45rem 0.5rem;
    width: 100%;
  }

  .threshold {
    display: grid;
    grid-template-columns: 1fr 2.5rem;
    gap: 0.6rem;
    align-items: center;
    margin-bottom: 0.7rem;
  }

  output {
    font-variant-numeric: tabular-nums;
    font-weight: 700;
    text-align: right;
  }
</style>
