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
      .map((s) => ({ ...s, total: s.Quantity }))
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
  // Mirrors Network.LarderLimits.SlotCapacity. Raised from 999 with it: a
  // thousand fish is about forty minutes of the larder bill in a late region.
  const SLOT_CAPACITY = 9999;

  function foodName(itemId: number): string {
    if (itemId === 0) return '';
    const item = registry?.items.get(itemId);
    return item ? prettifyBaseId(item.BaseId) : `Item #${itemId}`;
  }

  function refetchSoon() {
    setTimeout(() => inventory.refetch(), 400);
  }


  // Modul: ADD to a slot rather than only filling an empty one.
  //
  // The server has always summed into an occupied slot when the food matches -
  // `newCount = existingCount + toMove` - and this screen never offered it, so
  // the only route from 100 fish to 200 was Unload, then Load 200. Sending the
  // slot's OWN food id is what makes it an addition rather than a swap.
  function add(slotIndex: number, itemId: number) {
    const food = itemId > 0
      ? availableFood.find((f) => f.numericId === itemId)
      : availableFood.find((f) => f.baseId === selectedFood);
    if (!food) return;

    connection.send({
      Command: CommandType.StockFoodSlot,
      ConsumableItemId: food.numericId,
      TargetSlotIndex: slotIndex,
      DepositQuantity: Math.min(amount, food.quantity, SLOT_CAPACITY),
    });
    refetchSoon();
  }

  // Modul: take SOME back out, which had no expression at all.
  //
  // Food id 0 with a positive quantity - an impossible combination before, so
  // it needed no new wire field. Id 0 with quantity 0 remains "empty the slot".
  function remove(slotIndex: number) {
    connection.send({
      Command: CommandType.StockFoodSlot,
      ConsumableItemId: 0,
      TargetSlotIndex: slotIndex,
      DepositQuantity: Math.max(1, Math.min(amount, SLOT_CAPACITY)),
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
            <span class="pm">
              <button
                class="tiny-btn"
                title="Add {amount} more"
                onclick={() => add(slot.index, slot.itemId)}
              >+</button>
              <button
                class="tiny-btn"
                title="Take {amount} back to the chest"
                onclick={() => remove(slot.index)}
              >&minus;</button>
              <button
                class="tiny-btn ghost"
                title="Empty the slot"
                onclick={() => unload(slot.index)}
              >all</button>
            </span>
          {:else}
            <span class="name dim empty">empty</span>
            <span class="count dim">0</span>
            <span class="pm">
              <button
                class="tiny-btn"
                disabled={!selectedFood}
                onclick={() => add(slot.index, 0)}
              >+</button>
            </span>
          {/if}
        </li>
      {/each}
    </ul>

    <h3>From the village chest</h3>
    <p class="dim tiny">
      Choose a food and an amount, then use + on a slot. &minus; takes that
      same amount back out; "all" empties the slot.
    </p>
    <!-- Modul: "there is none" is a claim, and it needs the answer to have
         arrived first. This read `availableFood.length === 0`, which is also
         true while the inventory request is in flight and while the content
         registry is still loading (the list needs numeric ids from it) - so a
         player opening this screen was told flatly that their chest held no
         food, a moment before their fish appeared. An empty state that lies
         during loading is the reason someone goes and fishes for an hour they
         did not need to.

         Two conditions because there are two sources: the query, and the
         registry the ids are resolved through. Either one missing means the
         answer is not known yet. -->
    {#if inventory.isPending || !registry}
      <p class="dim">Checking the chest...</p>
    {:else if availableFood.length === 0}
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
      <p class="dim tiny">Slots hold at most {SLOT_CAPACITY.toLocaleString()}; larger requests are clamped.</p>
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

  .pm {
    display: inline-flex;
    gap: 0.25rem;
  }

  .pm .ghost {
    opacity: 0.7;
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
