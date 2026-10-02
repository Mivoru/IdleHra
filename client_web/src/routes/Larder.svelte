<script lang="ts">
  import { formatNumber, numberTitle } from '../lib/ui/format';
  import { createQuery } from '@tanstack/svelte-query';
  import { playerState } from '../lib/stores/game';
  import { requestScreen } from '../lib/stores/navigation';
  import { connection } from '../lib/net/connection';
  import { CommandType } from '../lib/net/protocol.generated';
  import { queryKeys, fetchMaterials } from '../lib/net/rest';
  import { prettifyBaseId, isFood, fishHealPercent } from '../lib/net/content';
  import ItemIcon from '../lib/ui/ItemIcon.svelte';
  import { contentQuery } from '../lib/net/registry.svelte';
  import QueryError from '../lib/ui/QueryError.svelte';

  // Modul: MATERIALS ONLY - this screen reads Stacks and nothing else, and was
  // downloading the whole equipment list to count fish. See fetchMaterials.
  const inventory = createQuery(() => ({ queryKey: queryKeys.materials, queryFn: fetchMaterials }));

  // Modul: through contentQuery, not `loadContent().catch(() => null)`. The
  // catch turned a failed fetch into a registry that never arrived, and the
  // list below waits on the registry - so it said "Checking the chest..." for
  // ever. See registry.svelte.ts for why it is not a TanStack query.
  const content = contentQuery;
  const registry = $derived(content.data ?? null);

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
        heal: fishHealPercent(s.ItemId, registry?.itemsByBaseId.get(s.ItemId)?.RegionTier ?? 1),
      }))
      .filter((f) => f.numericId > 0)
      .sort((a, b) => a.baseId.localeCompare(b.baseId)),
  );

  let amount = $state(100);
  // Modul: task 109 - the flow ran bottom to top: pick a food in a <select>
  // under the slots, type an amount, then press + back up on a slot. An empty
  // slot now carries "+ Add food", which opens the chest's foods right under
  // that slot; one tap loads the amount into it.
  let pickingSlot = $state<number | null>(null);

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

  function foodBaseId(itemId: number): string {
    return registry?.items.get(itemId)?.BaseId ?? '';
  }

  function slotHeal(itemId: number): number | null {
    const item = registry?.items.get(itemId);
    return item ? fishHealPercent(item.BaseId, item.RegionTier) : null;
  }

  // Modul: ADD to a slot rather than only filling an empty one.
  //
  // The server has always summed into an occupied slot when the food matches -
  // `newCount = existingCount + toMove` - and this screen never offered it, so
  // the only route from 100 fish to 200 was Unload, then Load 200. Sending the
  // slot's OWN food id is what makes it an addition rather than a swap.
  function add(slotIndex: number, itemId: number) {
    const food = availableFood.find((f) => f.numericId === itemId);
    if (!food) return;
    pickingSlot = null;

    connection.send({
      Command: CommandType.StockFoodSlot,
      ConsumableItemId: food.numericId,
      TargetSlotIndex: slotIndex,
      DepositQuantity: Math.min(amount, food.quantity, SLOT_CAPACITY),
    });
  }

  // Modul: take SOME back out, which had no expression at all.
  //
  // Food id 0 with a positive quantity - an impossible combination before, so
  // it needed no new wire field. Id 0 with quantity 0 remains "empty the slot".
  // Modul: ONE PRESS FROM CHEST TO LARDER. Loading took three controls - a
  // native <select> (which Android's WebView draws as a dialog, see CLAUDE.md),
  // an amount, then + on a slot - for the one thing every fighter needs. This
  // loads the biggest food stack in the chest, all of it up to the slot's
  // capacity, into the slot already holding that food or else the first empty
  // one. ONE command per press on purpose: ValidateCommand's 100 ms rule reads
  // a burst of commands as something other than a person.
  const quickLoad = $derived.by(() => {
    const food = [...availableFood].sort((a, b) => b.quantity - a.quantity)[0];
    if (!food) return null;
    const target = slots.find((sl) => sl.itemId === food.numericId) ?? slots.find((sl) => sl.itemId === 0);
    if (!target) return null;
    const room = SLOT_CAPACITY - (target.itemId === food.numericId ? target.count : 0);
    const quantity = Math.min(food.quantity, room);
    return quantity > 0 ? { food, slotIndex: target.index, quantity } : null;
  });

  function loadAll() {
    const plan = quickLoad;
    if (!plan) return;
    connection.send({
      Command: CommandType.StockFoodSlot,
      ConsumableItemId: plan.food.numericId,
      TargetSlotIndex: plan.slotIndex,
      DepositQuantity: plan.quantity,
    });
  }

  function remove(slotIndex: number) {
    connection.send({
      Command: CommandType.StockFoodSlot,
      ConsumableItemId: 0,
      TargetSlotIndex: slotIndex,
      DepositQuantity: Math.max(1, Math.min(amount, SLOT_CAPACITY)),
    });
  }

  function unload(slotIndex: number) {
    // DepositQuantity 0 means "unload this slot back into the chest".
    connection.send({
      Command: CommandType.StockFoodSlot,
      TargetSlotIndex: slotIndex,
      DepositQuantity: 0,
    });
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
      character eats the one that heals most, automatically. If the larder runs
      out you keep fighting, just without healing.
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
    {#if inventory.isError && inventory.data === undefined}
      <QueryError query={inventory} what="your chest" />
    {:else if content.isError && !registry}
      <QueryError query={content} what="the item list" />
    {:else if inventory.isPending || !registry}
      <p class="dim">Checking the chest...</p>
    {:else if availableFood.length === 0}
      <!-- Modul: an empty state names the next step AND offers it. This said
           "fish it up" and left the player to find where fishing lives. -->
      <div class="empty">
        <p class="dim">No food in the chest yet. Fish some up, then load it here.</p>
        <button onclick={() => requestScreen('gathering')}>Go fishing</button>
      </div>
    {:else}
      {#if quickLoad}
        <button class="quickload" data-guide="larder-load" onclick={loadAll}>
          Load all {prettifyBaseId(quickLoad.food.baseId)} ({formatNumber(quickLoad.quantity)})
          into slot {quickLoad.slotIndex + 1}
        </button>
      {/if}
      <label class="amount">
        <span class="dim">Amount per tap</span>
        <input type="number" min="1" max={SLOT_CAPACITY} bind:value={amount} />
      </label>
    {/if}

    <ul class="slots">
      {#each slots as slot}
        <li>
          <span class="idx dim">Slot {slot.index + 1}</span>
          {#if slot.itemId > 0}
            {@const heal = slotHeal(slot.itemId)}
            <span class="name">
              <ItemIcon baseItemId={foodBaseId(slot.itemId)} name={foodName(slot.itemId)} size="sm" />
              <span>
                {foodName(slot.itemId)}
                {#if heal !== null}<span class="dim heal">heals ~{heal}% each</span>{/if}
              </span>
            </span>
            <span class="count" title={numberTitle(slot.count)}>{formatNumber(slot.count)}</span>
            <span class="pm">
              <button
                class="tiny-btn"
                title="Add {amount} more"
                aria-label="Add {amount} more"
                disabled={!availableFood.some((f) => f.numericId === slot.itemId)}
                onclick={() => add(slot.index, slot.itemId)}
              >+</button>
              <button
                class="tiny-btn"
                title="Take {amount} back to the chest"
                aria-label="Take {amount} back to the chest"
                onclick={() => remove(slot.index)}
              >&minus;</button>
              <button
                class="tiny-btn ghost"
                title="Empty the slot"
                onclick={() => unload(slot.index)}
              >all</button>
            </span>
          {:else}
            <button
              class="add-food"
              aria-expanded={pickingSlot === slot.index}
              disabled={availableFood.length === 0}
              onclick={() => (pickingSlot = pickingSlot === slot.index ? null : slot.index)}
            >+ Add food</button>
          {/if}
        </li>
        {#if pickingSlot === slot.index && slot.itemId === 0}
          <li class="picker">
            {#each availableFood as food (food.baseId)}
              <button class="pick" onclick={() => add(slot.index, food.numericId)}>
                <ItemIcon baseItemId={food.baseId} name={prettifyBaseId(food.baseId)} size="sm" />
                <span class="pick-name">
                  {prettifyBaseId(food.baseId)}
                  {#if food.heal !== null}<span class="dim heal">heals ~{food.heal}%</span>{/if}
                </span>
                <span class="dim">{formatNumber(Math.min(amount, food.quantity, SLOT_CAPACITY))} of {formatNumber(food.quantity)}</span>
              </button>
            {/each}
          </li>
        {/if}
      {/each}
    </ul>
    <p class="dim tiny">
      &minus; takes the amount back to the chest; "all" empties the slot. A slot
      holds at most {formatNumber(SLOT_CAPACITY)}.
    </p>
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
        <output>{threshold}%</output>
      </div>

      <!-- "Applied (50)" was a disabled button, which read as a control that
           could not be used. The saved state is a sentence; the button only
           appears when there is something to save. -->
      {#if threshold === snap.AutoEatThreshold}
        <p class="dim tiny" role="status">
          {snap.AutoEatThreshold === 0 ? 'Auto-eat is off.' : `Eats below ${snap.AutoEatThreshold}% health.`}
        </p>
      {:else}
        <button onclick={applyThreshold}>Eat below {threshold}%</button>
      {/if}
    {:else}
      <p class="dim">Waiting for the first state snapshot...</p>
    {/if}
  </section>
</div>

<style>
  .quickload {
    width: 100%;
    margin: 0 0 0.6rem;
  }

  .empty {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.4rem 0.8rem;
  }

  .empty p {
    margin: 0;
    flex: 1 1 12rem;
  }

  .empty button {
    flex-shrink: 0;
  }

  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(20rem, 1fr));
    gap: 1rem;
    padding: 1rem;
    align-items: start;
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

  .count {
    font-variant-numeric: tabular-nums;
    font-weight: 700;
  }

  .pm {
    display: inline-flex;
    gap: 0.25rem;
  }

  .pm .ghost {
    opacity: 0.7;
  }

  .amount {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: 0.5rem;
    margin: 0 0 0.5rem;
    font-size: 0.8rem;
  }

  .amount input {
    width: 5.5rem;
  }

  .slots .name {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    min-width: 0;
  }

  .heal {
    display: block;
    font-size: 0.72rem;
  }

  .add-food {
    grid-column: 2 / -1;
    justify-self: start;
  }

  .slots li.picker {
    display: grid;
    grid-template-columns: 1fr;
    gap: 0.3rem;
    padding: 0.3rem 0 0.5rem 3.6rem;
  }

  .pick {
    display: grid;
    grid-template-columns: auto 1fr auto;
    align-items: center;
    gap: 0.5rem;
    text-align: left;
  }

  .pick-name {
    min-width: 0;
  }

  /* The phone media query in app.css draws the slider; above it the browser
     default was a bright system blue on parchment. */
  .threshold input[type='range'] {
    accent-color: var(--accent);
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
