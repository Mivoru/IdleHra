<script lang="ts">
  import { currencyIcon } from './sprites';
  import { formatCompact, formatExact, isCompacted } from './format';
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

  /*
    Modul: COMPACTED FROM A MILLION, and the exact figure never goes away.

    This is the most-read number in the game - it sits in the header on every
    screen - and a live account's balance is seven digits. `5 042 484` has to be
    counted in groups before it can be compared against a 250,000 gate; `5.04M`
    does not. Below a million the separator is still doing its job, so nothing
    changes there: a 17,000 fee and a 2,000 reroll stay comparable at a glance
    and keep their real precision.

    `data-exact` carries the RAW number, not the formatted one - the attribute
    exists so a machine can read it, and Number("4 950 462") is NaN. The
    grouped, human-readable figure goes in the title, which is the half a
    person actually hovers for.

    exercise.mjs reads numbers out of the DOM in
    four places and one of its regexes already carries a comment about breaking
    "once a number passes a thousand" - so the exact value is PUBLISHED as data
    rather than left to be parsed back out of display text. A format that tests
    parse is a format nobody can change afterwards.
  */
  const compacted = $derived(isCompacted(value));

  const formatted = $derived.by(() => {
    const abs = formatCompact(Math.abs(value));
    if (!signed) return abs;
    return value < 0 ? `-${abs}` : `+${abs}`;
  });

  const exactText = $derived.by(() => {
    const abs = formatExact(Math.abs(value));
    if (!signed) return abs;
    return value < 0 ? `-${abs}` : `+${abs}`;
  });

  /* Modul: the unaffordable warning wins the title, because it is the more
     urgent of the two things a hover could say - and it carries its own exact
     figure, so nothing is lost by preferring it. */
  const hoverTitle = $derived.by(() => {
    if (!affordable) return `You have ${formatExact(available ?? 0)}`;
    return compacted ? `${exactText}${short}` : undefined;
  });
</script>

<span
  class="money"
  data-kind={kind}
  data-exact={compacted ? value : undefined}
  class:short={affordable}
  class:unaffordable={!affordable}
  title={hoverTitle}
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
