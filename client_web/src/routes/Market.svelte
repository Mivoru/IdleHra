<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import {
    queryKeys,
    fetchInventory,
    fetchMarketListings,
    fetchMarketPriceHistory,
    fetchStatistics,
    type InventoryEquipment,
  } from '../lib/net/rest';
  import { prettifyBaseId } from '../lib/net/content';
  import { listItemOnMarket, buyMarketListing, placeLimitOrder } from '../lib/net/commands';
  import { loadContent, type ContentRegistry } from '../lib/net/content';
  import { rarityColor, rarityName, MAX_QUALITY_TIER } from '../lib/ui/rarity';
  import { EQUIPMENT_SLOTS } from '../lib/ui/slots';
  import ItemIcon from '../lib/ui/ItemIcon.svelte';
  import PriceChart from '../lib/ui/PriceChart.svelte';
  import { pushLocalNotice } from '../lib/stores/game';
  import { requestScreen } from '../lib/stores/navigation';

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
  //
  // Modul: THE MARKET WAS A LOOKUP, NOT A SHOP. It required an exact
  // BaseItemId and an exact rarity and returned nothing without them, so the
  // only question a player could ask was "is this precise item at this precise
  // tier for sale" - a question nobody can ask about a marketplace they have
  // never seen. This is a shop front: filter by what kind of thing it is and
  // how rare, sort it, and page through the rest.
  const SLOT_FILTERS = [
    { index: -1, label: 'Everything' },
    ...EQUIPMENT_SLOTS.map((slot) => ({ index: slot.index, label: slot.label })),
  ];

  const SORTS = [
    { key: 'price', label: 'Price' },
    { key: 'rarity', label: 'Rarity' },
    { key: 'name', label: 'Name' },
  ] as const;

  let filterText = $state('');
  let filterSlot = $state(-1);
  let filterMinRarity = $state(0);
  let filterMaxRarity = $state(MAX_QUALITY_TIER);
  let sortBy = $state<'price' | 'rarity' | 'name'>('price');
  let descending = $state(false);
  let pageIndex = $state(0);

  const PAGE_SIZE = 24;

  // Debounced so typing does not fire a request per keystroke.
  let debouncedText = $state('');
  let debounceHandle: ReturnType<typeof setTimeout> | undefined;
  $effect(() => {
    const next = filterText;
    clearTimeout(debounceHandle);
    debounceHandle = setTimeout(() => {
      debouncedText = next;
      pageIndex = 0;
    }, 300);
    return () => clearTimeout(debounceHandle);
  });

  const listings = createQuery(() => ({
    queryKey: [
      'market',
      debouncedText,
      filterSlot,
      filterMinRarity,
      filterMaxRarity,
      sortBy,
      descending,
      pageIndex,
    ],
    queryFn: () =>
      fetchMarketListings({
        baseItemId: debouncedText,
        slotIndex: filterSlot,
        minQualityTier: filterMinRarity,
        maxQualityTier: filterMaxRarity,
        sortBy,
        descending,
        pageIndex,
        pageSize: PAGE_SIZE,
      }),
  }));

  const rows = $derived(listings.data?.Listings ?? []);
  const totalCount = $derived(listings.data?.TotalCount ?? 0);
  const pageCount = $derived(Math.max(1, Math.ceil(totalCount / PAGE_SIZE)));

  function resetFilters() {
    filterText = '';
    filterSlot = -1;
    filterMinRarity = 0;
    filterMaxRarity = MAX_QUALITY_TIER;
    sortBy = 'price';
    descending = false;
    pageIndex = 0;
  }

  // Carried equipment is the only sellable stock; anything worn has to be
  // taken off first.
  const sellable = $derived(
    (inventory.data?.Equipment ?? []).filter((e: InventoryEquipment) => !e.IsEquipped),
  );

  // --- sell -----------------------------------------------------------------
  let sellInstanceId = $state(0);
  let sellPrice = $state(1000);

  const sellItem = $derived(sellable.find((e: InventoryEquipment) => e.Id === sellInstanceId) ?? null);

  // Modul: what this piece is actually worth. Keyed on the base id AND the
  // rarity, because those are the two things the archive matches on - a
  // Legendary and a Common of the same item are different goods and averaging
  // them would quote a price neither has ever fetched.
  //
  // `enabled` rather than a guard inside the fetcher: with nothing selected
  // there is no item to ask about, and firing a request for the empty string
  // would 400 on every render.
  const history = createQuery(() => ({
    queryKey: ['market', 'history', sellItem?.BaseItemId ?? '', sellItem?.QualityTier ?? -1] as const,
    queryFn: () => fetchMarketPriceHistory(sellItem!.BaseItemId, sellItem!.QualityTier),
    enabled: sellItem !== null,
    staleTime: 60_000,
  }));

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
  <section class="panel browse">
    <header class="head">
      <h2>Market</h2>
      <span class="dim tiny">
        {totalCount.toLocaleString()} listing{totalCount === 1 ? '' : 's'}
      </span>
    </header>

    <div class="filters">
      <input placeholder="Search by name..." bind:value={filterText} />

      <select bind:value={filterSlot} onchange={() => (pageIndex = 0)}>
        {#each SLOT_FILTERS as option (option.index)}
          <option value={option.index}>{option.label}</option>
        {/each}
      </select>

      <label class="range">
        Rarity
        <select bind:value={filterMinRarity} onchange={() => (pageIndex = 0)}>
          {#each Array.from({ length: MAX_QUALITY_TIER + 1 }, (_, i) => i) as tier}
            <option value={tier}>{rarityName(tier)}</option>
          {/each}
        </select>
        to
        <select bind:value={filterMaxRarity} onchange={() => (pageIndex = 0)}>
          {#each Array.from({ length: MAX_QUALITY_TIER + 1 }, (_, i) => i) as tier}
            <option value={tier}>{rarityName(tier)}</option>
          {/each}
        </select>
      </label>

      <label class="range">
        Sort
        <select bind:value={sortBy} onchange={() => (pageIndex = 0)}>
          {#each SORTS as option (option.key)}
            <option value={option.key}>{option.label}</option>
          {/each}
        </select>
        <button class="tiny-btn" onclick={() => (descending = !descending)}>
          {descending ? 'High to low' : 'Low to high'}
        </button>
      </label>

      <button class="tiny-btn" onclick={resetFilters}>Clear</button>
    </div>

    {#if listings.isPending}
      <p class="dim">Loading the market...</p>
    {:else if listings.isError}
      <p class="err">{listings.error?.message}</p>
    {:else if rows.length === 0}
      <p class="dim">
        Nothing matches those filters.
        {#if totalCount === 0 && !debouncedText && filterSlot === -1}
          The market is empty - nobody has listed anything yet.
        {/if}
      </p>
    {:else}
      <ul class="cards">
        {#each rows as listing (listing.OrderId)}
          <li>
            <ItemIcon
              baseItemId={listing.BaseItemId}
              name={prettifyBaseId(listing.BaseItemId)}
              qualityTier={listing.QualityTier}
              size="md"
            />
            <div class="what">
              <span class="name" style="color: {rarityColor(listing.QualityTier)}">
                {prettifyBaseId(listing.BaseItemId)}
              </span>
              <span class="dim tiny">{rarityName(listing.QualityTier)}</span>
            </div>
            <span class="price">{listing.Price.toLocaleString()}g</span>
            <button class="tiny-btn" disabled={!hasGuildLicense} onclick={() => buy(listing.OrderId)}>
              Buy
            </button>
          </li>
        {/each}
      </ul>

      <!-- Pages rather than one endless scroll: the book is meant to get
           large, and "page 4 of 60" is a fact a scrollbar cannot state. -->
      <div class="pager">
        <button class="tiny-btn" disabled={pageIndex === 0} onclick={() => (pageIndex -= 1)}>
          Previous
        </button>
        <span class="dim tiny">Page {pageIndex + 1} of {pageCount}</span>
        <button
          class="tiny-btn"
          disabled={pageIndex + 1 >= pageCount}
          onclick={() => (pageIndex += 1)}
        >
          Next
        </button>
      </div>
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

    <p class="dim small">
      Only carried equipment can be listed. Take a piece off first to sell it -
      <button class="linkish" onclick={() => requestScreen('chest')}>open the chest</button>.
    </p>

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

    <!-- Modul: WHAT IS IT WORTH. A price box with nothing beside it asks the
         player to invent a number, and the market has been answering that
         question in the trade archive since it shipped - every completed sale,
         with its price and timestamp. This is that answer, for the exact piece
         they picked. -->
    {#if sellInstanceId > 0}
      <div class="quote">
        {#if history.isPending}
          <p class="dim tiny">Checking what these go for...</p>
        {:else if history.data && history.data.TradeCount > 0}
          {@const h = history.data}
          <div class="quote-head">
            <div>
              <span class="dim tiny">Last sold</span>
              <strong>{h.LastPrice.toLocaleString()}g</strong>
            </div>
            <div>
              <span class="dim tiny">Average</span>
              <strong>{h.AveragePrice.toLocaleString()}g</strong>
            </div>
            <div>
              <span class="dim tiny">Range</span>
              <strong>{h.LowPrice.toLocaleString()} - {h.HighPrice.toLocaleString()}g</strong>
            </div>
          </div>

          <PriceChart points={h.Points} />

          <!-- "-" where nothing traded before that window opened. A three-day
               market has no honest month-over-month figure and 0% would claim
               it does. -->
          <div class="changes">
            {#each [['Day', h.ChangeDayPct], ['Week', h.ChangeWeekPct], ['Month', h.ChangeMonthPct]] as [label, pct]}
              <span class="change" class:up={typeof pct === 'number' && pct > 0} class:down={typeof pct === 'number' && pct < 0}>
                {label}
                <strong>
                  {typeof pct === 'number' ? `${pct > 0 ? '+' : ''}${pct.toFixed(1)}%` : '-'}
                </strong>
              </span>
            {/each}
          </div>

          <p class="dim tiny">{h.TradeCount.toLocaleString()} trades in the last 30 days.</p>

          <button class="tiny-btn" onclick={() => (sellPrice = Math.max(1, h.LastPrice))}>
            Use last price
          </button>
        {:else}
          <p class="dim tiny">
            Nothing like this has sold in the last 30 days - you are setting the
            first price.
          </p>
        {/if}
      </div>
    {/if}

    <label>
      Price
      <!-- The server DISCONNECTS on a price of zero or less rather than
           rejecting it, so this is bounded at the input as well as guarded in
           the command layer. -->
      <input type="number" min="1" step="1" bind:value={sellPrice} />
    </label>

    <!-- Modul: the cut, BEFORE confirming. Both figures come from the server
         with the history - the burn bracket depends on the seller's own wealth
         and the guild rate on their guild's setting, so a client that computed
         either would be a second source of truth about what a player is paid. -->
    {#if history.data && sellPrice > 0}
      {@const fee = Math.floor((sellPrice * history.data.FeePct) / 100)}
      {@const guildCut = Math.floor((sellPrice * history.data.GuildTaxPct) / 100)}
      <dl class="payout">
        <div><dt>Asking</dt><dd>{sellPrice.toLocaleString()}g</dd></div>
        <div><dt>Market fee ({history.data.FeePct}%)</dt><dd class="minus">-{fee.toLocaleString()}g</dd></div>
        {#if history.data.GuildTaxPct > 0}
          <div>
            <dt>Guild cut ({history.data.GuildTaxPct}%)</dt>
            <dd class="minus">-{guildCut.toLocaleString()}g</dd>
          </div>
        {/if}
        <div class="total">
          <dt>You receive</dt>
          <dd>{Math.max(0, sellPrice - fee - guildCut).toLocaleString()}g</dd>
        </div>
      </dl>
    {/if}

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
  .browse .head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 0.5rem;
  }

  .filters {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
    align-items: center;
    margin: 0.6rem 0 0.8rem;
  }

  .filters input {
    flex: 1 1 12rem;
    min-width: 0;
  }

  .filters select {
    width: auto;
  }

  .range {
    display: inline-flex;
    align-items: center;
    gap: 0.3rem;
    font-size: 0.82rem;
  }

  .cards {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(17rem, 1fr));
    gap: 0.4rem;
  }

  .cards li {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.4rem 0.5rem;
    border: 1px solid rgba(255, 255, 255, 0.09);
    border-radius: var(--radius, 6px);
    background: rgba(255, 255, 255, 0.02);
  }

  .cards .what {
    display: flex;
    flex-direction: column;
    min-width: 0;
  }

  .cards .name {
    font-size: 0.85rem;
    line-height: 1.15;
    overflow-wrap: break-word;
  }

  .cards .price {
    margin-left: auto;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }

  .pager {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.6rem;
    margin-top: 0.8rem;
  }

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

  .price {
    font-variant-numeric: tabular-nums;
    font-weight: 700;
    color: var(--gold);
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.45rem;
  }

  /* A button, because it navigates rather than addressing anything - the
     screens are modal panels, not URLs, so there is no href to give it. Styled
     as a link because that is what it does. */
  .linkish {
    background: none;
    border: none;
    padding: 0;
    font: inherit;
    color: var(--accent, #7dd3fc);
    text-decoration: underline;
    cursor: pointer;
  }

  /* --- what it is worth, and what you keep ------------------------------- */

  .quote {
    display: grid;
    gap: 0.5rem;
    padding: 0.6rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: rgba(127, 127, 127, 0.06);
  }

  .quote-head {
    display: flex;
    flex-wrap: wrap;
    gap: 0.9rem;
  }

  .quote-head div {
    display: grid;
    gap: 0.1rem;
  }

  .changes {
    display: flex;
    flex-wrap: wrap;
    gap: 0.75rem;
    font-size: 0.8rem;
  }

  .change {
    display: flex;
    gap: 0.3rem;
    align-items: baseline;
    opacity: 0.85;
  }

  .change.up strong {
    color: var(--good, #4ade80);
  }

  .change.down strong {
    color: var(--bad, #f87171);
  }

  /* The payout breakdown. Laid out as a definition list because that is what
     it is - each line names a deduction and gives its amount - and the total
     is separated by a rule so the number the player actually receives is not
     just another row. */
  .payout {
    display: grid;
    gap: 0.15rem;
    margin: 0;
    font-size: 0.85rem;
  }

  .payout div {
    display: flex;
    justify-content: space-between;
    gap: 1rem;
  }

  .payout dt,
  .payout dd {
    margin: 0;
  }

  .payout .minus {
    color: var(--bad, #f87171);
  }

  .payout .total {
    margin-top: 0.25rem;
    padding-top: 0.25rem;
    border-top: 1px solid var(--border);
    font-weight: 700;
  }

  .payout .total dd {
    color: var(--gold);
  }
</style>
