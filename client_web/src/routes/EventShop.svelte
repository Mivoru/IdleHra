<script lang="ts">
  // The seasonal event's screen (Samhain first): the shop, and the story.
  // Reached from the header's EventChip, never from the menu.
  //
  // Every word and number about the event is the server's answer (GET
  // /api/v1/event): dates, rates, the cap, the shop and its prices. The live
  // balance is the wire's EventCurrency, so it moves while the screen is open.
  // A purchase is the BuyEventShopItem command; its answer is a command result,
  // which invalidates every query, so Owned flips by itself.
  import { createQuery } from '@tanstack/svelte-query';
  import { buyEventShopItem } from '../lib/net/commands';
  import { EVENT_PHASE, EVENT_SHOP_KIND, fetchSeasonalEvent, seasonalEventKeys, type EventShopEntry } from '../lib/net/seasonalEvent';
  import { connectionStatus, playerState, pushLocalNotice } from '../lib/stores/game';
  import { spriteUrl } from '../lib/ui/spriteUrl';
  import { formatExact } from '../lib/ui/format';
  import ChipGroup from '../lib/ui/ChipGroup.svelte';
  import DisabledReason from '../lib/ui/DisabledReason.svelte';
  import QueryError from '../lib/ui/QueryError.svelte';
  import SeasonalBossPanel from '../lib/ui/SeasonalBossPanel.svelte';

  const event = createQuery(() => ({
    queryKey: seasonalEventKeys.all,
    queryFn: fetchSeasonalEvent,
    refetchInterval: 60_000,
  }));

  type Tab = 'shop' | 'boss' | 'story';
  let tab = $state<Tab>('shop');

  const live = $derived($connectionStatus.phase === 'live');
  const ev = $derived(event.data ?? null);
  const balance = $derived(Number($playerState?.EventCurrency ?? 0));
  const today = $derived(Number($playerState?.EventCurrencyEarnedToday ?? 0));

  // Ticks once a minute: the countdown is in days and hours, and a control
  // that ticks faster has no business being on a phone (client_web/CLAUDE.md).
  let now = $state(Math.floor(Date.now() / 1000));
  $effect(() => {
    const id = setInterval(() => (now = Math.floor(Date.now() / 1000)), 60_000);
    return () => clearInterval(id);
  });

  function countdown(untilUtc: number): string {
    const left = Math.max(0, untilUtc - now);
    const days = Math.floor(left / 86_400);
    const hours = Math.floor((left % 86_400) / 3600);
    if (days > 0) return `${days} d ${hours} h`;
    const minutes = Math.floor((left % 3600) / 60);
    return `${hours} h ${minutes} min`;
  }

  function pct(chance: number): string {
    return `${Math.round(chance * 1000) / 10}%`;
  }

  /** Why Buy is off, in words - never a silent grey. */
  function blockedReason(item: EventShopEntry): string | null {
    if (item.Owned) return null;
    if (!live) return 'Waiting for the connection.';
    if (balance < item.Price) return `You need ${formatExact(item.Price - balance)} more.`;
    return null;
  }

  function buy(item: EventShopEntry) {
    if (!ev) return;
    const outcome = buyEventShopItem(item.Index, ev.Id);
    if (!outcome.ok) pushLocalNotice(outcome.reason, 'error');
  }

  const kindName = (kind: number) => (kind === EVENT_SHOP_KIND.Pet ? 'Pet' : 'Avatar');
</script>

<section class="panel event-screen" data-testid="event-screen">
  {#if event.isError}
    <QueryError query={event} what="the event" />
  {:else if !event.data && event.isPending}
    <p class="dim">Loading...</p>
  {:else if !ev}
    <p class="dim">No event is running right now.</p>
  {:else}
    <header class="event-head">
      <img class="currency" src={spriteUrl('Events/samhain/currency/pumpkin.webp')} alt="" decoding="async" />
      <div>
        <h2>{ev.Name}</h2>
        {#if ev.Phase === EVENT_PHASE.Live}
          <p class="dim tiny">Ends in {countdown(ev.EndUtc)}</p>
        {:else}
          <p class="tiny warn">The event is over - the shop closes in {countdown(ev.ShopCloseUtc)}, and unspent {ev.CurrencyName.toLowerCase()} go with it.</p>
        {/if}
      </div>
      <div class="purse" data-testid="event-balance" data-exact={balance}>
        <strong>{formatExact(balance)}</strong>
        <span class="dim tiny">{ev.CurrencyName}</span>
        {#if ev.Phase === EVENT_PHASE.Live}
          <span class="dim tiny">+{formatExact(today)} today</span>
        {/if}
      </div>
    </header>

    {#if ev.Phase === EVENT_PHASE.Live}
      <p class="earn tiny">
        Every kill has a {pct(ev.KillChance)} chance of a {ev.CurrencyName.toLowerCase().replace(/s$/, '')} and every
        harvest {pct(ev.GatherChance)}{#if ev.OfflineFactor === 1}, the same while you are away{:else}, and time away
        earns at {Math.round(ev.OfflineFactor * 100)}% of that{/if}.
      </p>
    {/if}

    <ChipGroup
      label="Event"
      options={[
        { value: 'shop', label: 'Shop', testid: 'event-tab-shop' },
        { value: 'boss', label: 'Boss', testid: 'event-tab-boss' },
        { value: 'story', label: 'Story', testid: 'event-tab-story' },
      ]}
      bind:value={tab}
    />

    {#if tab === 'shop'}
      <ul class="shop" data-testid="event-shop">
        {#each ev.Shop as item (item.Id)}
          {@const why = blockedReason(item)}
          <li class="item" class:owned={item.Owned} class:pet={item.Kind === EVENT_SHOP_KIND.Pet} data-testid="event-shop-item" data-item={item.Id}>
            <img src={spriteUrl(item.Art)} alt="" loading="lazy" decoding="async" />
            <span class="name">{item.Name}</span>
            <span class="dim tiny">{kindName(item.Kind)}</span>
            {#if item.Bonuses.length > 0}
              <span class="tiny bonus">{item.Bonuses.join(' · ')}</span>
            {/if}
            {#if item.Owned}
              <span class="owned-label tiny" data-testid="event-shop-owned">Owned</span>
            {:else}
              <button type="button" disabled={why !== null} onclick={() => buy(item)} data-testid="event-shop-buy">
                {formatExact(item.Price)}
              </button>
              <DisabledReason text={why} />
            {/if}
          </li>
        {/each}
      </ul>
      <p class="dim tiny">
        Avatars go to your Wardrobe. A pet follows one character - give it one on the Character screen. Pets stay
        yours after the event.
      </p>
    {:else if tab === 'boss'}
      <SeasonalBossPanel />
    {:else}
      <article class="story">
        <img src={spriteUrl('Events/samhain/boss/cailleach.webp')} alt="The Cailleach" loading="lazy" decoding="async" />
        <div>
          <h3>The Cailleach</h3>
          <p>
            The Old Wife. Goddess of the cold and the winds, the Veiled One, Queen of Winter. At Samhain she comes down
            from the mountains, and from then until Beltaine the land is hers to govern - how long the winter lasts,
            and how hard it bites.
          </p>
          <p>
            This year she is angry. The people call her festival <em>Halloween</em> now, and they have forgotten whose
            night it is. So she means to stay: to hold the valley in a winter that never breaks.
          </p>
          <p class="dim">Gather pumpkins while the fires still burn. She is coming.</p>
        </div>
      </article>
    {/if}
  {/if}
</section>

<style>
  .event-screen {
    display: grid;
    gap: 0.9rem;
  }

  .event-head {
    display: flex;
    align-items: center;
    gap: 0.8rem;
    flex-wrap: wrap;
  }

  .event-head h2 {
    margin: 0;
  }

  .event-head p {
    margin: 0.1rem 0 0;
  }

  .currency {
    width: 3rem;
    height: 3rem;
    object-fit: contain;
  }

  .purse {
    margin-left: auto;
    display: grid;
    justify-items: end;
    color: var(--event-accent, var(--gold));
    font-variant-numeric: tabular-nums;
  }

  .purse strong {
    font-size: 1.4rem;
  }

  .warn {
    color: var(--warn);
  }

  .earn {
    margin: 0;
  }

  .shop {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(9.5rem, 1fr));
    gap: 0.7rem;
  }

  .item {
    display: grid;
    justify-items: center;
    gap: 0.25rem;
    padding: 0.6rem;
    border: 1px solid var(--border);
    border-radius: 0.6rem;
    background: var(--bg-raised);
    text-align: center;
  }

  .item img {
    width: 6.5rem;
    height: 6.5rem;
    object-fit: cover;
    border-radius: 50%;
    background: var(--bg);
  }

  .item.pet img {
    object-fit: contain;
    border-radius: 0;
    background: none;
  }

  .bonus {
    color: var(--good);
  }

  .item.owned {
    border-color: var(--event-accent, var(--accent));
  }

  .name {
    font-weight: 600;
  }

  .item button {
    min-width: 6rem;
  }

  .owned-label {
    color: var(--event-accent, var(--accent));
    font-weight: 600;
  }

  .story {
    display: grid;
    grid-template-columns: minmax(8rem, 14rem) 1fr;
    gap: 1rem;
    align-items: start;
  }

  .story img {
    width: 100%;
    height: auto;
  }

  .story h3 {
    margin-top: 0;
  }

  @media (max-width: 40rem) {
    .story {
      grid-template-columns: 1fr;
      justify-items: center;
    }

    .story img {
      max-width: 12rem;
    }
  }
</style>
