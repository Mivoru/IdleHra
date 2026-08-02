<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import { queryKeys, fetchStoreCatalog, fetchStorefront } from '../lib/net/rest';
  import Money from '../lib/ui/Money.svelte';
  import {
    purchaseLegacyUnlock,
    consumeChronoCore,
    toggleChronoAcceleration,
  } from '../lib/net/commands';
  import { prettifyBaseId } from '../lib/net/content';
  import { purchase, purchaseUnavailableReason } from '../lib/net/billing';
  import { play } from '../lib/ui/audio';
  import Skeleton from '../lib/ui/Skeleton.svelte';

  const catalog = createQuery(() => ({ queryKey: queryKeys.storeCatalog, queryFn: fetchStoreCatalog }));

  // Fetching the storefront has a SIDE EFFECT server-side - it upserts this
  // player's segmentation profile - so it is pinned to one fetch per session
  // rather than left on the default refetch behaviour. A cohort that changes
  // because someone alt-tabbed would be a real bug in the pricing data.
  const storefront = createQuery(() => ({
    queryKey: queryKeys.storefront,
    queryFn: fetchStorefront,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  }));

  // --- purchasing -----------------------------------------------------------
  //
  // Computed once, not per-click: a screen that lets you press Buy and then
  // explains why it could not is worse than one that disables the button and
  // says so up front. Same reasoning as the guarded command layer.
  const cannotBuy = purchaseUnavailableReason();

  let buying = $state<string | null>(null);

  async function buy(productIdentifier: string) {
    buying = productIdentifier;
    try {
      const outcome = await purchase(productIdentifier);
      if (outcome.kind === 'granted') {
        play('levelUp');
        pushLocalNotice('Purchase confirmed - your diamonds are on the way.', 'info');
      } else if (outcome.kind === 'cancelled') {
        // Deliberately silent. Changing your mind is not an event worth a
        // notification, and telling someone about it every time reads as a
        // complaint.
      } else {
        pushLocalNotice(outcome.reason);
      }
    } finally {
      buying = null;
    }
  }

  const snap = $derived($playerState);
  const quarantined = $derived(snap ? snap.Quarantine_Active !== 0 : false);

  // --- legacy shop ----------------------------------------------------------
  // Modul: LegacyPerksBitmask packs three prestige perks at byte offsets
  // 0/8/16 (LegacyPerkResolver) - XP multiplier, gold drop rate, combat speed.
  // They gate real combat maths, so the ranks are read off the wire rather
  // than tracked client-side.
  const PERKS = [
    { id: 1, name: 'XP multiplier', shift: 0 },
    { id: 2, name: 'Gold drop rate', shift: 8 },
    { id: 3, name: 'Combat speed', shift: 16 },
  ];

  function perkRank(shift: number): number {
    if (!snap) return 0;
    // The mask is a long; ranks are one byte each.
    return Number((BigInt(snap.LegacyPerksBitmask) >> BigInt(shift)) & 0xffn);
  }

  function buyPerk(unlockId: number) {
    const outcome = purchaseLegacyUnlock(unlockId);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }

  // --- chrono bank ----------------------------------------------------------
  let coreItemId = $state(0);

  function useCore() {
    const outcome = consumeChronoCore(coreItemId, quarantined);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }

  function setSpeed(value: number) {
    const outcome = toggleChronoAcceleration(value);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }
</script>

{#if !snap}
  <p class="dim pad">Waiting for state...</p>
{:else}
  <div class="grid">
    <section class="panel">
      <h2>Diamond packages</h2>
      <p class="dim small">
        You hold {snap.PremiumCurrencyBalance.toLocaleString()} diamonds.
      </p>

      {#if catalog.isPending}
        <Skeleton />
      {:else if catalog.isError}
        <p class="err">{catalog.error?.message}</p>
      {:else}
        <ul class="rows">
          {#each catalog.data ?? [] as entry (entry.ProductId)}
            <li>
              <span class="name">{prettifyBaseId(entry.ProductId)}</span>
              <span class="amount">{entry.DiamondAmount.toLocaleString()} diamonds</span>
            </li>
          {/each}
        </ul>
      {/if}

      <!-- Modul: purchases ARE wired now - see lib/net/billing.ts. They go
           through /api/v1/billing/verify-receipt, which validates the store's
           signature, and never through opcode 39, which grants diamonds on an
           unsigned transaction id. What is still missing is a store adapter
           for a specific vendor, which is why the Buy buttons below disable
           themselves and say so rather than pretending. -->
      <p class="dim tiny">
        The catalogue above carries no price - it only says how many diamonds
        each product grants. Prices are personal and live in your storefront
        below.
      </p>

      <h3>Your storefront</h3>

      <!-- Modul: this list is NOT the same for every player.
           Requesting it runs StorefrontSegmentationEngine, which sorts the
           account into a cohort by lifetime spend, account age and days since
           the last purchase, and returns only that cohort's listings. Two
           players comparing screens will legitimately see different prices, so
           nothing here may be described as "the" price.

           Fetching it also WRITES - it upserts a PlayerSegmentationProfile row
           - so it must never be polled or refetched on window focus. And any
           query string on the URL force-disconnects the player's session. -->
      {#if storefront.isPending}
        <Skeleton rows={2} />
      {:else if storefront.isError}
        <p class="dim tiny">Could not load your storefront.</p>
      {:else if (storefront.data ?? []).length === 0}
        <p class="dim tiny">No listings are offered to your account right now.</p>
      {:else}
        {#if cannotBuy}
          <p class="dim tiny buy-note">{cannotBuy}</p>
        {/if}

        <ul class="rows">
          {#each storefront.data ?? [] as listing (listing.ListingId)}
            <li>
              <span class="name">{prettifyBaseId(listing.ProductIdentifier)}</span>
              <span class="amount">
                <Money amount={listing.DiamondPackageYield} kind="diamond" />
              </span>
              <span class="cash">{(listing.PriceInCents / 100).toFixed(2)}</span>
              <button
                class="tiny-btn"
                disabled={cannotBuy !== null || buying === listing.ProductIdentifier}
                onclick={() => buy(listing.ProductIdentifier)}
              >
                {buying === listing.ProductIdentifier ? 'Buying...' : 'Buy'}
              </button>
            </li>
          {/each}
        </ul>
        <p class="dim tiny">
          Prices are shown without a currency symbol because the server sends
          cents with no currency code - guessing one would be wrong for most
          players. These listings are chosen for your account specifically and
          may differ from another player's.
        </p>
      {/if}
    </section>

    <section class="panel">
      <h2>Legacy shop</h2>
      <p class="dim small">
        {snap.LegacyShardBalance.toLocaleString()} shards
        &middot; {snap.CitizenMultiSlotsUnlocked} citizen slots unlocked
      </p>

      <ul class="rows">
        {#each PERKS as perk}
          <li>
            <span class="name">{perk.name}</span>
            <span class="dim tiny">rank {perkRank(perk.shift)}</span>
            <button class="tiny-btn" onclick={() => buyPerk(perk.id)}>Buy rank</button>
          </li>
        {/each}
      </ul>
      <p class="dim tiny">
        Perks are bought with prestige shards and raise combat maths directly -
        the server prices and applies each rank.
      </p>
    </section>

    <section class="panel">
      <h2>Chrono bank</h2>

      <dl class="stats">
        <div><dt>Banked</dt><dd>{snap.VisualBankedChronoSeconds.toLocaleString()}s</dd></div>
        <div>
          <dt>Accelerating</dt>
          <dd>{snap.IsChronoAccelerating ? `${snap.CurrentSimulationSpeedMultiplier}x` : 'no'}</dd>
        </div>
      </dl>

      <h3>Speed</h3>
      <div class="speeds">
        {#each [1, 2, 3, 4] as value}
          <button
            class:active={snap.CurrentSimulationSpeedMultiplier === value}
            onclick={() => setSpeed(value)}
          >
            {value}x
          </button>
        {/each}
      </div>
      <p class="dim tiny">1x turns acceleration off. Banked seconds pay for the rest.</p>

      <h3>Consume a chrono core</h3>
      <div class="row">
        <input type="number" min="1" placeholder="Item id" bind:value={coreItemId} />
        <button disabled={quarantined || coreItemId < 1} onclick={useCore}>Consume</button>
      </div>
      {#if quarantined}
        <p class="dim tiny">A restricted account cannot consume cores.</p>
      {/if}
    </section>
  </div>
{/if}

<style>
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(19rem, 1fr));
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
    margin: 0.4rem 0 0;
  }
  .pad {
    padding: 1rem;
  }
  .err {
    color: var(--danger);
  }

  .rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  .buy-note {
    margin: 0 0 0.5rem;
    color: var(--warn);
  }

  /* Flex rather than a fixed grid: the catalogue rows carry three children and
     the storefront rows four (they have a Buy button), and a shared
     grid-template would wrap the fourth onto its own line. */
  .rows li {
    display: flex;
    gap: 0.5rem;
    align-items: center;
    font-size: 0.85rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.28rem;
  }

  .rows .name {
    flex: 1;
    min-width: 0;
  }

  /* Real money, so deliberately NOT coloured like an in-game currency - the
     distinction between "spend diamonds" and "spend money" is the one this
     screen must never blur. */
  .cash {
    font-variant-numeric: tabular-nums;
    font-weight: 700;
    color: var(--text);
  }

  .name {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .amount {
    font-variant-numeric: tabular-nums;
    font-weight: 700;
  }

  .stats {
    display: grid;
    grid-template-columns: repeat(2, 1fr);
    gap: 0.5rem;
    margin: 0 0 0.5rem;
  }

  .stats div {
    display: grid;
    gap: 0.1rem;
  }

  dt {
    font-size: 0.7rem;
    color: var(--text-dim);
  }

  dd {
    margin: 0;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
  }

  .speeds {
    display: flex;
    gap: 0.3rem;
  }

  .speeds button.active {
    border-color: var(--accent);
    color: var(--accent);
  }

  .row {
    display: grid;
    grid-template-columns: 1fr auto;
    gap: 0.4rem;
  }

  input {
    font: inherit;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.4rem 0.5rem;
    width: 100%;
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.45rem;
  }
</style>
