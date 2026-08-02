<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import {
    queryKeys,
    fetchInventory,
    fetchMarketListings,
    fetchStatistics,
    type InventoryEquipment,
  } from '../lib/net/rest';
  import { prettifyBaseId } from '../lib/net/content';
  import { listItemOnMarket, buyMarketListing, placeLimitOrder } from '../lib/net/commands';
  import { loadContent, type ContentRegistry } from '../lib/net/content';
  import { rarityColor, rarityName, MAX_QUALITY_TIER } from '../lib/ui/rarity';
  import { pushLocalNotice } from '../lib/stores/game';

  const client = useQueryClient();
  const inventory = createQuery(() => ({ queryKey: queryKeys.inventory, queryFn: fetchInventory }));

  // Modul: trading requires an active guild membership - MarketEscrowEngine
  // answers NoGuildLicense otherwise. Surfaced up front rather than letting
  // the player price an item and only then be told, because a guild is not
  // something they can fix from this screen.
  //
  // Read from the statistics snapshot because GUILD MEMBERSHIP IS NOT ON THE
  // WIRE: StateUpdate carries guild war and logistics numbers but no GuildId,
  // so there is nothing on the hot path to test.
  const statistics = createQuery(() => ({
    queryKey: queryKeys.statistics,
    queryFn: fetchStatistics,
  }));
  const hasGuildLicense = $derived((statistics.data?.GuildName ?? '') !== '');

  // --- browse ---------------------------------------------------------------
  let searchBaseItemId = $state('');
  let searchQuality = $state(1);
  let submittedSearch = $state<{ baseItemId: string; quality: number } | null>(null);

  const listings = createQuery(() => ({
    queryKey: queryKeys.market(
      submittedSearch?.baseItemId ?? '',
      submittedSearch?.quality ?? 0,
      0,
    ),
    queryFn: () => fetchMarketListings(submittedSearch!.baseItemId, submittedSearch!.quality),
    // The endpoint 400s without a baseItemId, so this is a search, not a
    // browse - there is deliberately no "everything" query to fire on mount.
    enabled: submittedSearch !== null,
  }));

  // Carried equipment is the only sellable stock; anything worn has to be
  // taken off first.
  const sellable = $derived(
    (inventory.data?.Equipment ?? []).filter((e: InventoryEquipment) => !e.IsEquipped),
  );

  const distinctBaseIds = $derived(
    [...new Set((inventory.data?.Equipment ?? []).map((e: InventoryEquipment) => e.BaseItemId))].sort(),
  );

  function search() {
    if (!searchBaseItemId) return;
    submittedSearch = { baseItemId: searchBaseItemId, quality: searchQuality };
  }

  // --- sell -----------------------------------------------------------------
  let sellInstanceId = $state(0);
  let sellPrice = $state(1000);

  function sell() {
    const outcome = listItemOnMarket(sellInstanceId, sellPrice);
    if (!outcome.ok) {
      pushLocalNotice(outcome.reason);
      return;
    }
    sellInstanceId = 0;
    setTimeout(() => client.invalidateQueries({ queryKey: queryKeys.inventory }), 600);
  }

  function buy(orderId: number) {
    const outcome = buyMarketListing(orderId);
    if (!outcome.ok) {
      pushLocalNotice(outcome.reason);
      return;
    }
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.inventory });
      listings.refetch();
    }, 600);
  }

  // --- limit orders ---------------------------------------------------------
  //
  // Modul: the resting side of the book, as opposed to the instant list/buy
  // above. THE TWO SIDES ADDRESS DIFFERENT THINGS THROUGH THE SAME FIELD:
  //
  //   sell - TargetId is an equipment INSTANCE you own
  //   buy  - TargetId is an item DEFINITION id, and QualityTier then says
  //          which quality the order will fill against
  //
  // So the buy form below picks from the CONTENT TABLE and the sell form from
  // the backpack. Building the buy side by copying the sell side would post an
  // order against whichever item happens to share that instance's number -
  // accepted by the server, wrong for the player, and silent.

  let registry = $state<ContentRegistry | null>(null);
  $effect(() => {
    void loadContent().then((loaded) => (registry = loaded));
  });

  const itemDefinitionCount = $derived(registry?.items.size ?? 0);

  const definitionOptions = $derived(
    registry
      ? [...registry.items.values()].sort((a, b) => a.BaseId.localeCompare(b.BaseId))
      : [],
  );

  let orderSide = $state<'buy' | 'sell'>('buy');
  let orderDefinitionId = $state(0);
  let orderInstanceId = $state(0);
  let orderPrice = $state(1000);
  let orderQuality = $state(0);

  function placeOrder() {
    const outcome = placeLimitOrder(
      orderSide === 'buy'
        ? {
            isBuy: true,
            targetId: orderDefinitionId,
            price: orderPrice,
            qualityTier: orderQuality,
            itemDefinitionCount,
          }
        : { isBuy: false, targetId: orderInstanceId, price: orderPrice },
    );
    if (!outcome.ok) return pushLocalNotice(outcome.reason);

    pushLocalNotice('Order placed. It rests until something matches it.', 'info');
    setTimeout(() => client.invalidateQueries({ queryKey: queryKeys.inventory }), 700);
  }
</script>

<div class="grid">
  <section class="panel">
    <h2>Browse</h2>
    <p class="dim small">
      The market is searched by item, not browsed - the server requires a base
      item id, so there is no "show everything" query.
    </p>

    <div class="search">
      <select bind:value={searchBaseItemId}>
        <option value="">Choose an item...</option>
        {#each distinctBaseIds as baseId}
          <option value={baseId}>{prettifyBaseId(baseId)}</option>
        {/each}
      </select>
      <select bind:value={searchQuality}>
        {#each Array.from({ length: MAX_QUALITY_TIER }, (_, i) => i + 1) as tier}
          <option value={tier}>{rarityName(tier)}</option>
        {/each}
      </select>
      <button onclick={search} disabled={!searchBaseItemId}>Search</button>
    </div>
    <p class="dim tiny">Only items you already own can be picked here - the wire has no item catalogue endpoint.</p>

    {#if submittedSearch}
      {#if listings.isPending}
        <p class="dim">Searching...</p>
      {:else if listings.isError}
        <p class="err">{listings.error?.message}</p>
      {:else if (listings.data ?? []).length === 0}
        <p class="dim">No listings for that item and tier.</p>
      {:else}
        <ul class="listings">
          {#each listings.data ?? [] as listing (listing.OrderId)}
            <li>
              <span style="color: {rarityColor(listing.QualityTier)}">
                {prettifyBaseId(listing.BaseItemId)}
              </span>
              <span class="dim tiny">[{rarityName(listing.QualityTier)}]</span>
              <span class="price">{listing.Price.toLocaleString()}g</span>
              <button class="tiny-btn" disabled={!hasGuildLicense} onclick={() => buy(listing.OrderId)}>
                Buy
              </button>
            </li>
          {/each}
        </ul>
      {/if}
    {/if}
  </section>

  <section class="panel">
    <h2>Sell</h2>

    {#if !hasGuildLicense}
      <p class="warn">
        Trading needs an active guild membership - the server treats it as a
        trade licence and rejects listings and purchases without one.
      </p>
    {/if}

    <p class="dim small">Only carried equipment can be listed. Take a piece off first to sell it.</p>

    <label>
      Item
      <select bind:value={sellInstanceId}>
        <option value={0}>Choose an item...</option>
        {#each sellable as item (item.Id)}
          <option value={item.Id}>
            {prettifyBaseId(item.BaseItemId)} [{rarityName(item.QualityTier)}]
          </option>
        {/each}
      </select>
    </label>

    <label>
      Price
      <!-- The server DISCONNECTS on a price of zero or less rather than
           rejecting it, so this is bounded at the input as well as guarded in
           the command layer. -->
      <input type="number" min="1" step="1" bind:value={sellPrice} />
    </label>

    <!-- Disabled without a licence for the same reason the fusion dropdowns
         exclude each other: not offering a choice the server will refuse beats
         offering it and explaining afterwards. NoGuildLicense is a rejection
         code rather than a disconnect, so this is UX rather than safety. -->
    <button onclick={sell} disabled={!hasGuildLicense || sellInstanceId === 0 || sellPrice < 1}>
      List for {Math.max(1, sellPrice).toLocaleString()}g
    </button>

    {#if sellable.length === 0}
      <p class="dim tiny">Nothing carried to sell.</p>
    {/if}
  </section>

  <section class="panel">
    <h2>Limit order</h2>

    <p class="dim small">
      A standing order that rests in the book until something matches it, rather
      than trading immediately. A buy order names the item you WANT; a sell
      order names a specific piece you already hold.
    </p>

    {#if !hasGuildLicense}
      <p class="warn">
        Trading needs an active guild membership.
      </p>
    {/if}

    <div class="sides">
      <button class:active={orderSide === 'buy'} onclick={() => (orderSide = 'buy')}>Buy</button>
      <button class:active={orderSide === 'sell'} onclick={() => (orderSide = 'sell')}>Sell</button>
    </div>

    {#if orderSide === 'buy'}
      <label>
        Item wanted
        <select bind:value={orderDefinitionId}>
          <option value={0}>Choose an item...</option>
          {#each definitionOptions as definition (definition.Id)}
            <option value={definition.Id}>{prettifyBaseId(definition.BaseId)}</option>
          {/each}
        </select>
      </label>

      <label>
        Quality
        <select bind:value={orderQuality}>
          <!-- Zero is a real, useful value here and not "unset": it means the
               order fills against any quality. -->
          <option value={0}>Any quality</option>
          {#each Array.from({ length: MAX_QUALITY_TIER }, (_, i) => i + 1) as tier}
            <option value={tier}>{rarityName(tier)}</option>
          {/each}
        </select>
      </label>
    {:else}
      <label>
        Item held
        <select bind:value={orderInstanceId}>
          <option value={0}>Choose an item...</option>
          {#each sellable as item (item.Id)}
            <option value={item.Id}>
              {prettifyBaseId(item.BaseItemId)} [{rarityName(item.QualityTier)}]
            </option>
          {/each}
        </select>
      </label>
      <p class="dim tiny">
        A sell order carries the quality of the piece itself - there is nothing
        to choose.
      </p>
    {/if}

    <label>
      Price
      <input type="number" min="1" step="1" bind:value={orderPrice} />
    </label>

    <button
      onclick={placeOrder}
      disabled={!hasGuildLicense ||
        orderPrice < 1 ||
        (orderSide === 'buy' ? orderDefinitionId === 0 : orderInstanceId === 0)}
    >
      Place {orderSide} order at {Math.max(1, orderPrice).toLocaleString()}g
    </button>

    <p class="dim tiny">
      Placing an order flushes your state to the database first, so there is a
      brief pause before the rest of the game resumes.
    </p>
  </section>
</div>

<style>
  .sides {
    display: flex;
    gap: 0.3rem;
    margin-bottom: 0.7rem;
  }

  .sides button {
    flex: 1;
    font-size: 0.82rem;
    color: var(--text-dim);
  }

  /* Buy and sell are coloured because getting the side wrong is the expensive
     mistake on this panel, and the label alone is easy to skim past. */
  .sides button.active {
    border-color: var(--accent);
    color: var(--accent);
  }

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

  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
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
  }
  .err {
    color: var(--danger);
  }

  .warn {
    padding: 0.5rem 0.65rem;
    background: rgba(224, 85, 63, 0.12);
    border-left: 3px solid var(--danger);
    border-radius: 4px;
    font-size: 0.82rem;
    margin: 0 0 0.7rem;
  }

  .search {
    display: grid;
    grid-template-columns: 1fr auto auto;
    gap: 0.4rem;
  }

  select,
  input {
    font: inherit;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.4rem 0.5rem;
    width: 100%;
  }

  label {
    display: grid;
    gap: 0.25rem;
    font-size: 0.8rem;
    color: var(--text-dim);
    margin-bottom: 0.6rem;
  }

  .listings {
    list-style: none;
    margin: 0.8rem 0 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  .listings li {
    display: grid;
    grid-template-columns: 1fr auto auto auto;
    gap: 0.5rem;
    align-items: center;
    font-size: 0.85rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.3rem;
  }

  .price {
    font-variant-numeric: tabular-nums;
    font-weight: 700;
    color: var(--gold);
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.45rem;
  }
</style>
