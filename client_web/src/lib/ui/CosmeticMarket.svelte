<script lang="ts">
  // Task 54 phase 4: the cosmetic market - unopened chests, avatars and frames
  // between players, at whatever price the seller sets (owner, 2026-09-28).
  // Same licence and fees as equipment; the server says so and refuses the
  // rest with a Result this screen turns into a sentence.
  import { onMount } from 'svelte';
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import Avatar from './Avatar.svelte';
  import Money from './Money.svelte';
  import QueryError from './QueryError.svelte';
  import {
    COSMETIC_KIND,
    COSMETIC_MARKET_SENTENCES,
    MAX_COSMETIC_PRICE,
    buyCosmetic,
    cancelCosmeticListing,
    cosmeticKeys,
    cosmeticMarketKeys,
    fetchCosmeticListings,
    fetchCosmetics,
    listCosmetic,
    loadCosmeticCatalogue,
    rarityClass,
    type CosmeticCatalogue,
    type CosmeticMarketResponse,
  } from '../net/cosmetics';
  import { playerState, pushLocalNotice } from '../stores/game';
  import { noteCosmeticsView } from '../stores/cosmeticChests';
  import { requestWorn, wornByPlayer } from '../stores/worn';

  interface Props {
    hasGuildLicense: boolean;
  }

  let { hasGuildLicense }: Props = $props();

  const client = useQueryClient();
  let catalogue = $state<CosmeticCatalogue | null>(null);
  onMount(() => {
    loadCosmeticCatalogue()
      .then((c) => (catalogue = c))
      .catch(() => {});
  });

  let kind = $state<number | null>(null);
  let rarity = $state<number | null>(null);

  const listings = createQuery(() => ({
    queryKey: cosmeticMarketKeys.listings(kind, rarity),
    queryFn: () => fetchCosmeticListings(kind, rarity),
  }));
  const mine = createQuery(() => ({ queryKey: cosmeticKeys.mine, queryFn: fetchCosmetics }));

  const gold = $derived(Number($playerState?.Gold ?? 0));

  // A frame is previewed around the viewer's own face, as in the Wardrobe -
  // an empty ring says nothing about how it would look.
  const selfId = $derived(Number($playerState?.PlayerId ?? 0));
  $effect(() => {
    if (selfId > 0) requestWorn(selfId);
  });
  const selfWorn = $derived(selfId > 0 ? ($wornByPlayer.get(selfId)?.worn ?? null) : null);
  const KIND_WORDS: Record<number, string> = {
    [COSMETIC_KIND.Chest]: 'chest',
    [COSMETIC_KIND.Avatar]: 'avatar',
    [COSMETIC_KIND.Frame]: 'frame',
  };
  const rarityNames = $derived(catalogue?.RarityNames ?? ['', 'Common', 'Rare', 'Epic', 'Legendary']);

  function nameOf(definitionId: string): string {
    return catalogue?.Items.find((d) => d.Id === definitionId)?.Name ?? definitionId;
  }

  const KIND_FILTERS = [
    { label: 'All', value: null },
    { label: 'Chests', value: COSMETIC_KIND.Chest },
    { label: 'Avatars', value: COSMETIC_KIND.Avatar },
    { label: 'Frames', value: COSMETIC_KIND.Frame },
  ] as const;

  // What the player can put up: everything they own that is not listed.
  const sellable = $derived((mine.data?.Owned ?? []).filter((o) => !o.IsListed));
  let sellId = $state(0);
  let sellPrice = $state(1000);
  const sellItem = $derived(sellable.find((o) => o.Id === sellId) ?? null);
  const priceValid = $derived(Number.isInteger(sellPrice) && sellPrice >= 1 && sellPrice <= MAX_COSMETIC_PRICE);

  let busy = $state(false);

  async function settle(response: CosmeticMarketResponse | null, success: string): Promise<void> {
    if (!response) {
      pushLocalNotice('The market did not answer. Try again.', 'error');
      return;
    }
    client.setQueryData(cosmeticKeys.mine, response.Cosmetics);
    noteCosmeticsView(response.Cosmetics);
    await client.invalidateQueries({ queryKey: ['market', 'cosmetics'] });
    if (response.Result === 'Ok') pushLocalNotice(success, 'info');
    else pushLocalNotice(COSMETIC_MARKET_SENTENCES[response.Result] ?? response.Result, 'error');
  }

  async function act(run: () => Promise<void>): Promise<void> {
    if (busy) return;
    busy = true;
    try {
      await run();
    } finally {
      busy = false;
    }
  }

  const sell = () =>
    act(async () => {
      if (!sellItem || !priceValid) return;
      await settle(await listCosmetic(sellItem.Id, sellPrice), `${nameOf(sellItem.DefinitionId)} is on the market.`);
      sellId = 0;
    });

  const buy = (id: number, name: string) => act(async () => settle(await buyCosmetic(id), `You bought ${name}.`));
  const takeDown = (id: number) => act(async () => settle(await cancelCosmeticListing(id), 'Taken off the market.'));
</script>

{#snippet Face(definitionId: string, kindOf: number, rarityOf: number)}
  {#if kindOf === COSMETIC_KIND.Chest}
    <span class="chest-icon {rarityClass(rarityOf)}" aria-hidden="true">
      <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round">
        <path d="M3 10h18v9H3zM3 10l2-5h14l2 5M12 10v4" />
      </svg>
    </span>
  {:else if kindOf === COSMETIC_KIND.Avatar}
    <Avatar avatarId={definitionId} frameId={null} size="md" />
  {:else}
    <Avatar
      avatarId={selfWorn?.AvatarId ?? null}
      frameId={definitionId}
      raceId={selfWorn?.RaceId ?? 0}
      female={selfWorn?.IsFemale ?? false}
      size="md"
    />
  {/if}
{/snippet}

<div class="cosmetic-market">
  <section class="panel">
    <h2>Cosmetics for sale</h2>
    <p class="dim tiny">Chests, avatars and frames, at the price each seller chose.</p>

    <div class="chips" role="group" aria-label="Kind">
      {#each KIND_FILTERS as f (f.label)}
        <button class:active={kind === f.value} onclick={() => (kind = f.value)}>{f.label}</button>
      {/each}
    </div>
    <div class="chips" role="group" aria-label="Rarity">
      <button class:active={rarity === null} onclick={() => (rarity = null)}>Any rarity</button>
      {#each [1, 2, 3, 4] as r}
        <button class="{rarityClass(r)}" class:active={rarity === r} onclick={() => (rarity = r)}>{rarityNames[r]}</button>
      {/each}
    </div>

    {#if listings.isPending}
      <p class="dim">Reading the market&hellip;</p>
    {:else if listings.isError && listings.data === undefined}
      <QueryError query={listings} what="the cosmetic market" />
    {:else if (listings.data ?? []).length === 0}
      <p class="dim">Nothing like that is for sale right now.</p>
    {:else}
      <ul class="listings" data-testid="cosmetic-listings">
        {#each listings.data ?? [] as row (row.Id)}
          <li class="{rarityClass(row.Rarity)}" class:mine={row.IsMine}>
            {@render Face(row.DefinitionId, row.Kind, row.Rarity)}
            <span class="what">
              <strong>{nameOf(row.DefinitionId)}</strong>
              <span class="dim tiny"><span class="rarity">{rarityNames[row.Rarity]}</span> · {row.IsMine ? 'yours' : row.SellerName}</span>
            </span>
            <span class="price"><Money amount={row.Price} available={row.IsMine ? undefined : gold} /></span>
            {#if row.IsMine}
              <button disabled={busy} onclick={() => takeDown(row.Id)} data-testid="cosmetic-take-down">Take down</button>
            {:else}
              <button disabled={busy || !hasGuildLicense || gold < row.Price} onclick={() => buy(row.Id, nameOf(row.DefinitionId))}>Buy</button>
            {/if}
          </li>
        {/each}
      </ul>
    {/if}
  </section>

  <section class="panel">
    <h2>Sell a cosmetic</h2>
    {#if !hasGuildLicense}
      <p class="warn">The market needs a guild. Join one to buy or sell.</p>
    {/if}
    {#if mine.isError && mine.data === undefined}
      <QueryError query={mine} what="your cosmetics" />
    {:else if sellable.length === 0}
      <p class="dim">You have nothing to sell. Chests come every five levels and, rarely, from monsters.</p>
    {:else}
      <div class="sell-pick" role="listbox" aria-label="What to sell">
        {#each sellable as o (o.Id)}
          <button
            class="pick {rarityClass(o.Rarity)}"
            class:chosen={o.Id === sellId}
            role="option"
            aria-selected={o.Id === sellId}
            onclick={() => (sellId = o.Id)}
            data-testid="cosmetic-sell-{o.Id}"
          >
            {@render Face(o.DefinitionId, o.Kind, o.Rarity)}
            <span class="pick-name">{nameOf(o.DefinitionId)}</span>
            <!-- Modul: "Rare avatar" under the name (task 102): two "River
                 Stone"s of different rarity or kind were the same tile. -->
            <span class="pick-meta">{rarityNames[o.Rarity]} {KIND_WORDS[o.Kind] ?? 'cosmetic'}</span>
          </button>
        {/each}
      </div>
      <label class="price-row">
        <span>Price</span>
        <input type="number" min="1" max={MAX_COSMETIC_PRICE} step="1" bind:value={sellPrice} data-testid="cosmetic-sell-price" />
        <span class="dim tiny">gold</span>
      </label>
      <p class="dim tiny">
        You choose the price. When it sells, 5-15% is burned (more the richer you are) and your guild
        takes its tax, like any market sale.
      </p>
      <button
        class="primary"
        disabled={busy || !hasGuildLicense || !sellItem || !priceValid}
        onclick={sell}
        data-testid="cosmetic-sell"
      >{sellItem ? `List ${nameOf(sellItem.DefinitionId)}` : 'Choose something to sell'}</button>
    {/if}
  </section>
</div>

<style>
  .cosmetic-market {
    display: grid;
    gap: 1rem;
    grid-template-columns: minmax(0, 2fr) minmax(0, 1fr);
    padding: 1rem;
  }

  @media (max-width: 52rem) {
    .cosmetic-market {
      grid-template-columns: minmax(0, 1fr);
    }
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
    min-width: 0;
  }

  .panel h2 {
    margin: 0 0 0.3rem;
  }

  .common {
    --c: #a9a9a9;
  }
  .rare {
    --c: #4f9be0;
  }
  .epic {
    --c: #a66be0;
  }
  .legendary {
    --c: #e0a526;
  }

  .chips {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
    margin: 0.5rem 0;
  }

  .chips button {
    min-height: 44px;
    flex-shrink: 0;
  }

  .chips button.active {
    border-color: var(--c, var(--accent));
    color: var(--c, var(--accent));
    font-weight: 700;
  }

  .listings {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.35rem;
    max-height: 32rem;
    overflow-y: auto;
  }

  .listings li {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    padding: 0.35rem 0.5rem;
    border-left: 3px solid var(--c);
    background: color-mix(in srgb, var(--c) 8%, transparent);
  }

  .listings li.mine {
    outline: 1px dashed var(--c);
  }

  .listings button {
    flex-shrink: 0;
  }

  .what {
    display: grid;
    flex: 1;
    min-width: 0;
  }

  .what strong {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .rarity {
    color: var(--c);
  }

  .price {
    flex-shrink: 0;
    font-variant-numeric: tabular-nums;
  }

  .chest-icon {
    display: inline-grid;
    place-items: center;
    width: 44px;
    height: 44px;
    flex-shrink: 0;
    border-radius: 8px;
    color: var(--c);
    border: 1px solid color-mix(in srgb, var(--c) 50%, transparent);
  }

  .sell-pick {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(5.5rem, 1fr));
    gap: 0.4rem;
    max-height: 16rem;
    overflow-y: auto;
    margin: 0.5rem 0;
  }

  .pick {
    display: grid;
    justify-items: center;
    gap: 0.2rem;
    padding: 0.4rem 0.2rem;
    min-height: 44px;
    border: 1px solid color-mix(in srgb, var(--c) 40%, transparent);
    border-radius: 8px;
    background: transparent;
  }

  .pick.chosen {
    box-shadow: 0 0 0 2px var(--c);
  }

  .pick-meta {
    text-align: center;
    font-size: 0.68rem;
    color: var(--text-dim);
  }

  .pick-name {
    font-size: 0.75rem;
    overflow-wrap: anywhere;
    text-align: center;
  }

  .price-row {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    margin: 0.5rem 0;
  }

  .price-row input {
    flex: 1;
    min-width: 0;
    min-height: 44px;
  }

  .warn {
    color: var(--warn);
  }
</style>
