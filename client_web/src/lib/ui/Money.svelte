<script lang="ts">
  import { currencyIcon } from './sprites';
  // Modul: one way to render a currency amount.
  //
  // Gold and diamonds appear on nine screens and were formatted nine different
  // ways - `1500g`, `1 500 gold`, a bare number next to the word "Diamonds",
  // and in the header a dim grey that read as disabled. A player scanning for
  // "can I afford this" should find the same shape and the same colour every
  // time, and an amount they cannot afford should be visibly so without
  // reading it.
  //
  // Colour is never the only signal: the suffix (g / diamonds) is always
  // present, and "cannot afford" also gets a strikethrough-free but explicit
  // title. Someone who cannot distinguish the two hues still reads the word.

  interface Props {
    amount: number | bigint | string;
    kind?: 'gold' | 'diamond';
    /** When given, the amount renders as unaffordable if it exceeds this. */
    available?: number;
    /** Prefix a sign, for deltas rather than totals. */
    signed?: boolean;
    /**
     * Draw the coin or gem alongside the number.
     *
     * Off by default on purpose. A ledger of thirty rows with thirty tiny
     * images is slower and busier than the same ledger with a colour and a
     * suffix, and the colour already distinguishes the two currencies. It
     * earns its place where the amount is the SUBJECT - the header wallet, a
     * store listing - rather than one column among many.
     */
    icon?: boolean;
  }

  const { amount, kind = 'gold', available, signed = false, icon = false }: Props = $props();

  const iconUrl = $derived(icon ? currencyIcon(kind) : null);

  const value = $derived(Number(amount));
  const short = $derived(kind === 'gold' ? 'g' : '');
  const affordable = $derived(available === undefined || value <= available);

  const formatted = $derived.by(() => {
    const abs = Math.abs(value).toLocaleString();
    if (!signed) return abs;
    return value < 0 ? `-${abs}` : `+${abs}`;
  });
</script>

<span
  class="money"
  data-kind={kind}
  class:short={affordable}
  class:unaffordable={!affordable}
  title={affordable ? undefined : `You have ${(available ?? 0).toLocaleString()}`}
>
  {#if iconUrl}
    <img src={iconUrl} alt="" loading="lazy" decoding="async" />
  {/if}
  {formatted}{short}
  {#if kind === 'diamond'}<span class="unit">diamonds</span>{/if}
</span>

<style>
  .money {
    display: inline-flex;
    align-items: center;
    gap: 0.25em;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }

  /* Sized in em so the coin tracks whatever type size it sits in, rather than
     needing a variant per place it appears. */
  img {
    width: 1.15em;
    height: 1.15em;
    object-fit: contain;
    flex: none;
  }

  .money[data-kind='gold'] {
    color: var(--gold);
  }

  .money[data-kind='diamond'] {
    color: var(--diamond);
  }

  /* Dimmed and struck rather than recoloured to red: red already means
     "something went wrong" everywhere else in this UI, and not having enough
     gold is not an error. */
  .unaffordable {
    opacity: 0.55;
    text-decoration: line-through;
  }

  .unit {
    font-size: 0.85em;
    opacity: 0.85;
    margin-left: 0.15em;
  }
</style>
