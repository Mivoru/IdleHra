<script lang="ts">
  // Modul: THE SEASONAL EVENT'S PURSE AND DOOR (owner, 2026-10-09). The
  // currency balance, shown in the header wallet while an event runs, and the
  // only way into the event screen - a menu entry that comes and goes with a
  // calendar would be a second copy of that calendar. Everything shown comes
  // off the wire: SeasonalEventId says whether there is an event at all,
  // EventCurrency the balance.
  import { playerState } from '../stores/game';
  import { spriteUrl } from './spriteUrl';
  import { formatExact, formatNumber } from './format';

  interface Props {
    onOpen: () => void;
    active?: boolean;
    /**
     * Owner, 2026-10-10: a bare count did not say it was a door. With a label
     * the chip reads "Event shop" beside the count - always in the phone's
     * More sheet (`sheet`), and in the header wherever the header has room.
     */
    labelled?: boolean;
    sheet?: boolean;
  }

  const { onOpen, active = false, labelled = false, sheet = false }: Props = $props();

  /** Per event id: its currency's picture and name. Ids are SeasonalEventRegistry's. */
  const CURRENCY: Record<number, { art: string; name: string }> = {
    1: { art: 'Events/samhain/currency/pumpkin.webp', name: 'Pumpkins' },
  };

  const eventId = $derived(Number($playerState?.SeasonalEventId ?? 0));
  const currency = $derived(CURRENCY[eventId] ?? null);
  const balance = $derived(Number($playerState?.EventCurrency ?? 0));
  const today = $derived(Number($playerState?.EventCurrencyEarnedToday ?? 0));
  const label = $derived(
    currency ? `${currency.name}: ${formatExact(balance)} (${formatExact(today)} earned today). Open the event shop.` : '',
  );
</script>

{#if currency}
  <button
    type="button"
    class="event-chip"
    class:active
    class:sheet
    data-testid="event-chip"
    data-exact={balance}
    aria-label={label}
    title={label}
    onclick={onOpen}
  >
    {#if labelled}<span class="label">Event shop</span>{/if}
    <img src={spriteUrl(currency.art)} alt="" decoding="async" />
    <span class="amount">{formatNumber(balance)}</span>
  </button>
{/if}

<style>
  .event-chip {
    display: inline-flex;
    align-items: center;
    gap: 0.3em;
    padding: 0.1rem 0.55rem 0.1rem 0.3rem;
    border-radius: 999px;
    border: 1px solid var(--event-accent, var(--border));
    background: var(--event-chip-bg, transparent);
    color: var(--event-accent, var(--text));
    font: inherit;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
    cursor: pointer;
    flex-shrink: 0;
  }

  .event-chip:hover,
  .event-chip.active {
    background: var(--event-chip-bg-hover, var(--bg-raised));
  }

  .label {
    font-weight: 600;
    padding-right: 0.35em;
    margin-right: 0.1em;
    border-right: 1px solid var(--event-accent, var(--border));
  }

  .sheet {
    width: 100%;
    justify-content: center;
    padding: 0.45rem 0.8rem;
  }

  /* The phone's header row has no room for the word; the More sheet's chip
     carries it there. */
  @media (max-width: 40rem) {
    .event-chip:not(.sheet) .label {
      display: none;
    }
  }

  img {
    width: 1.35em;
    height: 1.35em;
    object-fit: contain;
    flex: none;
  }
</style>
