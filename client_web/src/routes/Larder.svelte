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

  /** Food carried in the backpack. Only the backpack can be loaded from. */
  const availableFood = $derived(
    (inventory.data?.Stacks ?? [])
      .filter((s) => isFood(s.ItemId) && s.BackpackQuantity > 0)
      .map((s) => ({
        baseId: s.ItemId,
        quantity: s.BackpackQuantity,
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
    // DepositQuantity 0 means "unload this slot back into the backpack".
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
    <h2>Larder</h2>
    <p class="dim small">
      Auto-eat draws from these three slots. An empty larder is what stops a
      combat activity the first time health crosses the threshold.
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

    <h3>Load from backpack</h3>
    {#if availableFood.length === 0}
      <p class="dim">
        No food in the backpack. Cook something, or fish it up - the larder can
        only be loaded from carried stock, not from the village stash.
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
    <h2>Auto-eat</h2>
    {#if snap}
      <p class="dim small">
        Eats when health falls below this. Set it to 0 to never auto-eat - the
        character will fight until it dies instead, which stops the activity
        just the same but costs no food.
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
