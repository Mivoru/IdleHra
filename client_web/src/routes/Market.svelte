<script lang="ts">
  import { formatNumber, formatGold } from '../lib/ui/format';
  import Money from '../lib/ui/Money.svelte';
  import CosmeticMarket from '../lib/ui/CosmeticMarket.svelte';
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { invalidateOwnedItems } from '../lib/net/queryClient';
  import {
    queryKeys,
    fetchInventory,
    fetchMarketListings,
    fetchMarketPriceHistory,
    fetchStatistics,
    fetchMyMarketOrders,
    cancelMyMarketOrder,
    MARKET_CANCEL_SENTENCES,
    type InventoryEquipment,
    type MarketOwnOrder,
  } from '../lib/net/rest';
  import DetailSheet from '../lib/ui/DetailSheet.svelte';
  import RarityPip from '../lib/ui/RarityPip.svelte';
  import { summarizeAffixes } from '../lib/ui/itemRow';
  import { isWide } from '../lib/ui/media';
  import { prettifyBaseId } from '../lib/net/content';
  import { listItemOnMarket, buyMarketListing, placeLimitOrder, MAX_MARKET_PRICE } from '../lib/net/commands';
  import { contentQuery } from '../lib/net/registry.svelte';
  import QueryError from '../lib/ui/QueryError.svelte';
  import ItemBrowser from '../lib/ui/ItemBrowser.svelte';
  import Tabs from '../lib/ui/Tabs.svelte';
  import { rarityColor, rarityName, MAX_QUALITY_TIER } from '../lib/ui/rarity';
  import { EQUIPMENT_SLOTS, resolveSlotIndex } from '../lib/ui/slots';
  import { locationName } from '../lib/ui/locations';
  import ItemIcon from '../lib/ui/ItemIcon.svelte';
  import PriceChart from '../lib/ui/PriceChart.svelte';
  import { pushLocalNotice, playerState } from '../lib/stores/game';
  import ConfirmButton from '../lib/ui/ConfirmButton.svelte';
  import { commandInFlight } from '../lib/ui/commandInFlight';
  import { requestScreen } from '../lib/stores/navigation';

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
  // Modul: CHECKBOXES, not a dropdown. "Show me helmets, chests and leggings"
  // is one question a player shopping for armour asks once - a single-value
  // filter made it three passes through the book. Weapons are split into the
  // three archetypes here even though they share one equip slot, because
  // "melee weapon" is what a player is shopping for; the split is presentation
  // over the same slot index, resolved by the id marker.
  const TYPE_FILTERS = EQUIPMENT_SLOTS.map((slot) => ({ index: slot.index, label: slot.label }));

  // The five locations. This is RegionTier - which set the gear belongs to -
  // and NOT the 14-step rarity below it. Both get called "tier" in
  // conversation, so they are labelled apart in the UI.
  const TIER_FILTERS = [1, 2, 3, 4, 5];

  const SORTS = [
    { key: 'price', label: 'Price' },
    { key: 'rarity', label: 'Rarity' },
    { key: 'name', label: 'Name' },
  ] as const;

  let filterText = $state('');
  let filterSlots = $state<number[]>([]);
  let filterTiers = $state<number[]>([]);
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
      filterSlots.join(','),
      filterTiers.join(','),
      filterMinRarity,
      filterMaxRarity,
      sortBy,
      descending,
      pageIndex,
    ],
    queryFn: () =>
      fetchMarketListings({
        baseItemId: debouncedText,
        slotIndexes: filterSlots,
        tiers: filterTiers,
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
    filterSlots = [];
    filterTiers = [];
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

  // What KIND of thing this is, for the card. Resolved from the id by the same
  // rule the equip path uses, so a card can never claim a slot the item would
  // not go into.
  function slotLabel(baseItemId: string): string {
    const index = resolveSlotIndex(baseItemId);
    return EQUIPMENT_SLOTS.find((s) => s.index === index)?.label ?? 'Item';
  }

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
      pushLocalNotice(outcome.reason, 'error');
      return;
    }
    sellInstanceId = 0;
  }

  // Modul: ONE BUY PER TAP. Buy sent its command and stayed live, so a double
  // tap bought (or tried to buy) twice: a success toast, then "Target not
  // found." for the same press. The listing is held until the server answers.
  // A purchase that takes a quarter of the purse or more also asks first -
  // gold is the one thing a mis-tap here cannot get back, and below that
  // threshold a confirm on every cheap buy would just be friction.
  const buyKey = (orderId: number) => `market:${orderId}`;
  const purse = $derived(Number($playerState?.Gold ?? 0));
  const isBigBuy = (price: number) => purse > 0 && price * 4 >= purse;

  function buy(orderId: number) {
    const outcome = commandInFlight.run(buyKey(orderId), () => buyMarketListing(orderId));
    if (outcome === null) return;
    if (!outcome.ok) {
      pushLocalNotice(outcome.reason, 'error');
      return;
    }
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

  // Modul: a query, not `loadContent().then(...)` with no catch - a failed
  // content fetch left "Item wanted" an empty dropdown with nothing saying why.
  const content = contentQuery;
  const registry = $derived(content.data ?? null);

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
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');

    pushLocalNotice('Order placed. It rests until something matches it.', 'info');
  }

  // Task 54: equipment and cosmetics are two markets with different rules
  // (a price corridor against the seller's own price), so two tabs.
  let marketTab = $state<'equipment' | 'cosmetics'>('equipment');

  // --- task 102: Buy / Sell / My orders --------------------------------------
  //
  // Modul: ONE JOB AT A TIME ON A PHONE. The browse panel opened with about
  // 650 px of filters (eleven slot checkboxes, five region checkboxes, two
  // selects and a sort), so the first listing sat near y 1060; the sell picker
  // and the limit-order form stacked below that. A segmented Buy | Sell | My
  // orders puts the one being done on screen, and the filters fold into a
  // sheet behind "Filters (n)". On a wide screen the three are side by side -
  // the results wide, Sell and the orders beside them.
  let seg = $state<'buy' | 'sell' | 'orders'>('buy');
  let filtersOpen = $state(false);

  const activeFilterCount = $derived(
    filterSlots.length +
      filterTiers.length +
      (filterMinRarity > 0 ? 1 : 0) +
      (filterMaxRarity < MAX_QUALITY_TIER ? 1 : 0),
  );
  const anyFilter = $derived(activeFilterCount > 0 || debouncedText.trim() !== '');

  function toggleSlot(index: number) {
    filterSlots = filterSlots.includes(index) ? filterSlots.filter((i) => i !== index) : [...filterSlots, index];
    pageIndex = 0;
  }

  function toggleTier(tier: number) {
    filterTiers = filterTiers.includes(tier) ? filterTiers.filter((t) => t !== tier) : [...filterTiers, tier];
    pageIndex = 0;
  }

  const myOrders = createQuery(() => ({
    queryKey: queryKeys.marketMine,
    queryFn: fetchMyMarketOrders,
    enabled: seg === 'orders' || $isWide,
  }));

  // Modul: CANCEL, two taps (ConfirmButton) because it is the one control on
  // this panel that undoes something. The row leaves the list only when the
  // server says Ok - an optimistic removal would hide a refusal ("it had just
  // sold") behind a row that silently came back on the next refetch. A
  // refusal names its reason; Sold/Gone also drop the row, since it is no
  // longer open either way.
  const queryClient = useQueryClient();
  let cancelling = $state<number | null>(null);

  async function cancelOrder(orderId: number) {
    if (cancelling !== null) return;
    cancelling = orderId;
    try {
      const response = await cancelMyMarketOrder(orderId);
      const result = response?.Result;
      if (!result) {
        pushLocalNotice('The market did not answer. Try again.', 'error');
        return;
      }
      if (result === 'Ok' || result === 'Sold' || result === 'Gone') {
        queryClient.setQueryData<MarketOwnOrder[]>(queryKeys.marketMine, (rows) =>
          (rows ?? []).filter((o) => o.OrderId !== orderId),
        );
      }
      if (result === 'Ok') {
        pushLocalNotice(
          response.RefundedGold > 0
            ? `Order cancelled. ${formatGold(response.RefundedGold)} returned.`
            : 'Listing cancelled. The piece is back in your chest.',
          'info',
        );
      } else {
        pushLocalNotice(MARKET_CANCEL_SENTENCES[result] ?? result, 'error');
      }
      invalidateOwnedItems(queryClient);
      queryClient.invalidateQueries({ queryKey: queryKeys.marketMine });
      queryClient.invalidateQueries({ queryKey: ['market'] });
    } catch {
      pushLocalNotice('Could not cancel that order. Try again.', 'error');
    } finally {
      cancelling = null;
    }
  }

  function ago(epochMs: number): string {
    const minutes = Math.max(0, Math.floor((Date.now() - epochMs) / 60_000));
    if (minutes < 60) return `${minutes} min ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 48) return `${hours} h ago`;
    return `${Math.floor(hours / 24)} days ago`;
  }
</script>

<!-- Modul: UNDERLINED, because these are sub-tabs of the Market and the
     filled buttons made them look like the screen's own top tabs. -->
<div class="market-tabs">
  <Tabs
    bind:value={marketTab}
    label="Market"
    variant="underline"
    tabs={[
      { value: 'equipment', label: 'Equipment' },
      { value: 'cosmetics', label: 'Cosmetics', testid: 'market-tab-cosmetics' },
    ]}
  />
</div>

{#snippet licenceWarning()}
  {#if statistics.isError && statistics.data === undefined}
    <!-- Modul: a failed membership check is not "you have no guild". The
         buttons stay disabled (hasGuildLicense is false) but the reason given
         is the true one. -->
    <QueryError query={statistics} what="your guild membership" />
  {:else if statistics.data !== undefined && !hasGuildLicense}
    <p class="warn">
      Trading needs an active guild membership - the server treats it as a
      trade licence and rejects listings and purchases without one.
    </p>
  {/if}
{/snippet}

{#snippet buyPanel()}
  <section class="panel browse" data-testid="market-buy">
    <header class="head">
      <h2>Market</h2>
      <span class="dim tiny">
        {formatNumber(totalCount)} listing{totalCount === 1 ? '' : 's'}
      </span>
    </header>

    <div class="searchrow">
      <input type="search" placeholder="Search by name..." bind:value={filterText} aria-label="Search the market" />
      <button class="filtersbtn" class:on={activeFilterCount > 0} onclick={() => (filtersOpen = true)} data-testid="market-filters">
        Filters{activeFilterCount > 0 ? ` (${activeFilterCount})` : ''}
      </button>
    </div>
    <div class="sortrow">
      <label class="sortlabel">
        <span>Sort:</span>
        <select bind:value={sortBy} onchange={() => (pageIndex = 0)} aria-label="Sort by">
          {#each SORTS as option (option.key)}
            <option value={option.key}>{option.label}</option>
          {/each}
        </select>
      </label>
      <button class="tiny-btn" onclick={() => (descending = !descending)}>
        {descending ? 'High to low' : 'Low to high'}
      </button>
    </div>

    {#if listings.isPending}
      <p class="dim">Loading the market...</p>
    {:else if listings.isError}
      <p class="err">{listings.error?.message}</p>
    {:else if rows.length === 0}
      <!-- Modul: TWO EMPTY STATES. "Nothing matches those filters" was said
           even with no filter set, blaming the player for an empty book. -->
      {#if anyFilter}
        <p class="dim" data-testid="market-empty-filtered">
          Nothing matches those filters.
          <button class="linkish" onclick={resetFilters}>Clear filters</button>
        </p>
      {:else}
        <p class="dim" data-testid="market-empty">The market is empty - nobody has listed anything yet.</p>
      {/if}
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
              <span class="dim tiny metaline">
                <RarityPip tier={listing.QualityTier} />
                {rarityName(listing.QualityTier)} &middot; {slotLabel(listing.BaseItemId)}
              </span>
            </div>
            <span class="price"><Money amount={listing.Price} /></span>
            {#if isBigBuy(listing.Price)}
              <ConfirmButton
                small
                danger={false}
                label="Buy"
                confirmLabel="Really buy?"
                disabled={!hasGuildLicense || $commandInFlight.has(buyKey(listing.OrderId))}
                onConfirm={() => buy(listing.OrderId)}
              />
            {:else}
              <button
                class="tiny-btn"
                disabled={!hasGuildLicense || $commandInFlight.has(buyKey(listing.OrderId))}
                onclick={() => buy(listing.OrderId)}
              >
                Buy
              </button>
            {/if}
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
{/snippet}

{#snippet sellPanel()}
  <section class="panel" data-testid="market-sell">
    <h2>Sell</h2>
    {@render licenceWarning()}

    {#if !sellItem}
      <p class="dim small">
        Pick the piece to list. Only carried equipment can be sold - take a
        piece off first, in
        <button class="linkish" onclick={() => requestScreen('chest')}>the chest</button>.
      </p>
      <!-- Modul: A BROWSER, NOT A DROPDOWN - the same component the Forge uses,
           so the two cannot drift. It is the whole tab until something is
           picked, which is as close to a full-screen picker as a phone needs;
           then it gives way to a card that names the piece. -->
      <ItemBrowser
        items={sellable}
        regionOf={(baseId) => registry?.itemsByBaseId.get(baseId)?.RegionTier ?? 0}
        selectedId={sellInstanceId}
        compact
        emptyText="Nothing carried. Take a piece off in the chest to sell it."
        onselect={(item) => (sellInstanceId = item.Id)}
      />
      {#if inventory.isError && inventory.data === undefined}
        <QueryError query={inventory} what="your equipment" />
      {/if}
    {:else}
      <!-- Modul: THE ITEM, ECHOED. "List for 1000g" did not say what was being
           listed; the card and the button both name it now. -->
      <div class="sellcard" data-testid="market-sell-card">
        <ItemIcon baseItemId={sellItem.BaseItemId} name={prettifyBaseId(sellItem.BaseItemId)} qualityTier={sellItem.QualityTier} size="md" />
        <div class="what">
          <span class="name" style="color: {rarityColor(sellItem.QualityTier)}">{prettifyBaseId(sellItem.BaseItemId)}</span>
          <span class="dim tiny metaline">
            <RarityPip tier={sellItem.QualityTier} />
            {rarityName(sellItem.QualityTier)} &middot; {slotLabel(sellItem.BaseItemId)}
          </span>
          {#if summarizeAffixes(sellItem.Affixes)}
            <span class="dim tiny">{summarizeAffixes(sellItem.Affixes)}</span>
          {/if}
        </div>
        <button class="tiny-btn" onclick={() => (sellInstanceId = 0)}>Change</button>
      </div>

      <!-- Modul: WHAT IS IT WORTH. A price box with nothing beside it asks the
           player to invent a number, and the market has been answering that
           question in the trade archive since it shipped. -->
      <div class="quote">
        {#if history.isPending}
          <p class="dim tiny">Checking what these go for...</p>
        {:else if history.isError && history.data === undefined}
          <QueryError query={history} what="the price history" />
        {:else if history.data && history.data.TradeCount > 0}
          {@const h = history.data}
          <div class="quote-head">
            <div>
              <span class="dim tiny">Last sold</span>
              <strong><Money amount={h.LastPrice} /></strong>
            </div>
            <div>
              <span class="dim tiny">Average</span>
              <strong><Money amount={h.AveragePrice} /></strong>
            </div>
            <div>
              <span class="dim tiny">Range</span>
              <strong><Money amount={h.LowPrice} /> - <Money amount={h.HighPrice} /></strong>
            </div>
          </div>

          <PriceChart points={h.Points} />

          <!-- "-" where nothing traded before that window opened. -->
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

          <p class="dim tiny">{formatNumber(h.TradeCount)} trades in the last 30 days.</p>

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
      <input type="number" min="1" max={MAX_MARKET_PRICE} step="1" bind:value={sellPrice} />
    </label>

    <!-- Modul: the cut, BEFORE confirming. Both figures come from the server
         with the history, so a client copy cannot misquote them. -->
    {#if sellItem && history.data && sellPrice > 0}
      {@const fee = Math.floor((sellPrice * history.data.FeePct) / 100)}
      {@const guildCut = Math.floor((sellPrice * history.data.GuildTaxPct) / 100)}
      <dl class="payout">
        <div><dt>Asking</dt><dd><Money amount={sellPrice} /></dd></div>
        <div><dt>Market fee ({history.data.FeePct}%)</dt><dd class="minus">-{formatGold(fee)}</dd></div>
        {#if history.data.GuildTaxPct > 0}
          <div>
            <dt>Guild cut ({history.data.GuildTaxPct}%)</dt>
            <dd class="minus">-{formatGold(guildCut)}</dd>
          </div>
        {/if}
        <div class="total">
          <dt>You receive</dt>
          <dd><Money amount={Math.max(0, sellPrice - fee - guildCut)} /></dd>
        </div>
      </dl>
    {/if}

    <!-- Disabled without a licence: not offering a choice the server will
         refuse beats offering it and explaining afterwards. -->
    <button
      class="primary listbtn"
      onclick={sell}
      disabled={!hasGuildLicense || !sellItem || sellPrice < 1 || sellPrice > MAX_MARKET_PRICE}
      data-testid="market-list"
      data-guide="market-list"
    >
      {sellItem
        ? `List ${prettifyBaseId(sellItem.BaseItemId)} for ${formatGold(Math.max(1, sellPrice))}`
        : 'Choose an item to list'}
    </button>
  </section>
{/snippet}

{#snippet ordersPanel()}
  <section class="panel" data-testid="market-orders">
    <h2>My orders</h2>
    {#if myOrders.isPending}
      <p class="dim tiny">Reading your orders...</p>
    {:else if myOrders.isError && myOrders.data === undefined}
      <QueryError query={myOrders} what="your orders" />
    {:else if (myOrders.data ?? []).length === 0}
      <p class="dim small">You have nothing on the market. A listing or a standing order shows up here until it fills.</p>
    {:else}
      <ul class="cards mine">
        {#each myOrders.data ?? [] as order (order.OrderId)}
          <li data-testid="market-order" data-order-id={order.OrderId}>
            <ItemIcon baseItemId={order.BaseItemId} name={prettifyBaseId(order.BaseItemId)} qualityTier={order.QualityTier} size="sm" />
            <div class="what">
              <span class="name" style={order.QualityTier > 0 ? `color: ${rarityColor(order.QualityTier)}` : undefined}>
                {prettifyBaseId(order.BaseItemId)}
              </span>
              <span class="dim tiny">
                <span class="side" class:buy={order.OrderType === 'BUY'}>{order.OrderType === 'BUY' ? 'Buying' : 'Selling'}</span>
                {order.QualityTier > 0 ? rarityName(order.QualityTier) : 'any quality'} &middot; {ago(order.CreatedAtEpoch)}
              </span>
            </div>
            <span class="price"><Money amount={order.Price} /></span>
            <ConfirmButton
              small
              label="Cancel"
              confirmLabel="Really cancel?"
              disabled={cancelling !== null}
              onConfirm={() => cancelOrder(order.OrderId)}
            />
          </li>
        {/each}
      </ul>
      <p class="dim tiny">
        An order stays until it fills or you cancel it. Cancelling returns the
        piece to your chest, or a buy order's gold to your purse.
      </p>
    {/if}

    <h3>Place a standing order</h3>
    <p class="dim small">
      A standing order rests in the book until something matches it, rather
      than trading immediately. A buy order names the item you WANT; a sell
      order names a specific piece you already hold.
    </p>

    {@render licenceWarning()}

    <div class="sides">
      <button class:active={orderSide === 'buy'} onclick={() => (orderSide = 'buy')}>Buy</button>
      <button class:active={orderSide === 'sell'} onclick={() => (orderSide = 'sell')}>Sell</button>
    </div>

    {#if orderSide === 'buy'}
      {#if content.isError && !registry}
        <QueryError query={content} what="the item list" />
      {/if}
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
      <input type="number" min="1" max={MAX_MARKET_PRICE} step="1" bind:value={orderPrice} />
    </label>

    <button
      onclick={placeOrder}
      disabled={!hasGuildLicense ||
        orderPrice < 1 ||
        (orderSide === 'buy' ? orderDefinitionId === 0 : orderInstanceId === 0)}
    >
      Place {orderSide} order at {formatGold(Math.max(1, orderPrice))}
    </button>

    <p class="dim tiny">Placing an order takes a moment.</p>
  </section>
{/snippet}

{#if marketTab === 'cosmetics'}
  <CosmeticMarket {hasGuildLicense} />
{:else if $isWide}
  <!-- Desktop: the results wide, Sell and the orders beside them. -->
  <div class="wide">
    {@render buyPanel()}
    <div class="side-col">
      {@render sellPanel()}
      {@render ordersPanel()}
    </div>
  </div>
{:else}
  <div class="phone">
    <div class="seg" role="tablist" aria-label="Market">
      <button role="tab" aria-selected={seg === 'buy'} class:on={seg === 'buy'} onclick={() => (seg = 'buy')} data-testid="market-seg-buy">Buy</button>
      <button role="tab" aria-selected={seg === 'sell'} class:on={seg === 'sell'} onclick={() => (seg = 'sell')} data-testid="market-seg-sell">Sell</button>
      <button role="tab" aria-selected={seg === 'orders'} class:on={seg === 'orders'} onclick={() => (seg = 'orders')} data-testid="market-seg-orders">My orders</button>
    </div>
    {#if seg === 'buy'}
      {@render buyPanel()}
    {:else if seg === 'sell'}
      {@render sellPanel()}
    {:else}
      {@render ordersPanel()}
    {/if}
  </div>
{/if}

{#if filtersOpen}
  <!-- Modul: THE FILTERS ARE CHIPS IN A SHEET - the same idiom as the
       Cosmetics tab - and the results never wait below them. -->
  <DetailSheet title="Filters" onClose={() => (filtersOpen = false)} testid="market-filter-sheet">
    <div class="filters">
      <p class="chiphead">Type</p>
      <div class="chips" role="group" aria-label="Type">
        {#each TYPE_FILTERS as option (option.index)}
          <button
            class="chip"
            class:active={filterSlots.includes(option.index)}
            aria-pressed={filterSlots.includes(option.index)}
            onclick={() => toggleSlot(option.index)}
          >{option.label}</button>
        {/each}
      </div>

      <p class="chiphead">Region</p>
      <div class="chips" role="group" aria-label="Region">
        {#each TIER_FILTERS as tier (tier)}
          <button
            class="chip"
            class:active={filterTiers.includes(tier)}
            aria-pressed={filterTiers.includes(tier)}
            onclick={() => toggleTier(tier)}
          >{locationName(tier)}</button>
        {/each}
      </div>

      <p class="chiphead">Rarity</p>
      <div class="range">
        <select bind:value={filterMinRarity} onchange={() => (pageIndex = 0)} aria-label="Lowest rarity">
          {#each Array.from({ length: MAX_QUALITY_TIER + 1 }, (_, i) => i) as tier}
            <option value={tier}>{tier === 0 ? 'Any' : rarityName(tier)}</option>
          {/each}
        </select>
        <span class="dim">to</span>
        <select bind:value={filterMaxRarity} onchange={() => (pageIndex = 0)} aria-label="Highest rarity">
          {#each Array.from({ length: MAX_QUALITY_TIER + 1 }, (_, i) => i) as tier}
            <option value={tier}>{tier === 0 ? 'Any' : rarityName(tier)}</option>
          {/each}
        </select>
      </div>

      <div class="sheetacts">
        <button class="tiny-btn" onclick={resetFilters}>Clear all</button>
        <button class="primary" onclick={() => (filtersOpen = false)}>
          Show {formatNumber(totalCount)} listing{totalCount === 1 ? '' : 's'}
        </button>
      </div>
    </div>
  </DetailSheet>
{/if}

<style>
  .market-tabs {
    margin: 1rem 1rem 0;
  }

  .phone {
    display: grid;
    gap: 0.75rem;
    padding: 0.75rem 1rem 1rem;
  }

  .wide {
    display: grid;
    grid-template-columns: minmax(0, 2fr) minmax(20rem, 1fr);
    gap: 1rem;
    padding: 1rem;
    align-items: start;
  }

  .side-col {
    display: grid;
    gap: 1rem;
  }

  /* The segmented control: one bordered strip, the chosen segment filled. */
  .seg {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    border: 1px solid var(--border);
    border-radius: var(--radius);
    overflow: hidden;
  }

  .seg button {
    border: 0;
    border-radius: 0;
    background: var(--bg);
    background-image: none;
    box-shadow: none;
    color: var(--text-dim);
    padding: 0.45rem 0.3rem;
  }

  .seg button + button {
    border-left: 1px solid var(--border);
  }

  .seg button.on {
    background: color-mix(in srgb, var(--brass) 25%, var(--bg));
    color: var(--text);
    font-weight: 700;
  }

  .browse .head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 0.5rem;
  }

  .searchrow {
    display: flex;
    gap: 0.4rem;
    margin: 0.5rem 0 0.4rem;
  }

  .searchrow input {
    flex: 1 1 auto;
    min-width: 0;
  }

  .filtersbtn {
    flex-shrink: 0;
  }

  .filtersbtn.on {
    border-color: var(--brass-lit);
    font-weight: 700;
  }

  .sortrow {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    margin: 0 0 0.7rem;
  }

  .sortlabel {
    display: flex;
    align-items: center;
    gap: 0.35rem;
    margin: 0;
    flex: 1 1 auto;
    min-width: 0;
  }

  /* Modul: the label is a flex item with the default shrink, so on a narrow
     phone it was squeezed to the width of "Sor" and wrapped "t:" under it.
     It never shrinks; the select beside it absorbs the squeeze (min-width: 0). */
  .sortlabel span {
    flex: 0 0 auto;
    white-space: nowrap;
  }

  .sortlabel select {
    flex: 1 1 auto;
    min-width: 0;
  }

  .filters {
    display: grid;
    gap: 0.4rem;
  }

  .chiphead {
    margin: 0.3rem 0 0;
    font-size: 0.72rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  .chips {
    display: flex;
    flex-wrap: wrap;
    gap: 0.35rem;
  }

  .chip {
    flex-shrink: 0;
    background-image: none;
    box-shadow: none;
    background: var(--bg);
    font-size: 0.8rem;
    padding: 0.3rem 0.65rem;
    border-radius: 999px;
  }

  .chip.active {
    border-color: var(--brass-lit);
    background: color-mix(in srgb, var(--brass) 25%, var(--bg));
    font-weight: 700;
  }

  .range {
    display: flex;
    align-items: center;
    gap: 0.4rem;
  }

  .range select {
    flex: 1 1 0;
    min-width: 0;
  }

  .sheetacts {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 0.5rem;
    margin-top: 0.6rem;
  }

  .metaline {
    display: inline-flex;
    align-items: center;
    gap: 0.3rem;
  }

  .sellcard {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    padding: 0.6rem;
    margin: 0 0 0.7rem;
    border: 1px solid var(--brass);
    border-radius: var(--radius);
  }

  .sellcard .what {
    display: grid;
    gap: 0.1rem;
    flex: 1 1 auto;
    min-width: 0;
  }

  .sellcard .name {
    font-weight: 700;
  }

  .sellcard button {
    flex-shrink: 0;
  }

  .listbtn {
    width: 100%;
    margin-top: 0.4rem;
  }

  .side {
    font-weight: 700;
    color: var(--good);
    margin-right: 0.2rem;
  }

  .side.buy {
    color: var(--accent);
  }

  h3 {
    margin: 1.1rem 0 0.4rem;
    font-size: 0.85rem;
  }

  .cards {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(17rem, 1fr));
    gap: 0.4rem;
  }

  .cards.mine {
    grid-template-columns: minmax(0, 1fr);
    margin-bottom: 0.4rem;
  }

  .cards li {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.4rem 0.5rem;
    border: 1px solid var(--edge-soft);
    border-radius: var(--radius-sm);
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

  .panel {
    min-width: 0;
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

  /* Modul: an inline button styled as a link - screens are modal panels, not
     URLs, so there is no href to give it. */
  .linkish {
    background: none;
    background-image: none;
    box-shadow: none;
    border: none;
    padding: 0;
    font: inherit;
    color: var(--accent);
    text-decoration: underline;
    cursor: pointer;
  }

  /* --- what it is worth, and what you keep ------------------------------- */

  .quote {
    display: grid;
    gap: 0.5rem;
    padding: 0.6rem;
    margin: 0 0 0.7rem;
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
    color: var(--good);
  }

  .change.down strong {
    color: var(--danger);
  }

  /* The payout breakdown, with the total ruled off so the number the player
     actually receives is not just another row. */
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
    color: var(--danger);
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
