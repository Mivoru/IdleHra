<script lang="ts">
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
  }

  const { amount, kind = 'gold', available, signed = false }: Props = $props();

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
  {formatted}{short}
  {#if kind === 'diamond'}<span class="unit">diamonds</span>{/if}
</span>

<style>
  .money {
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
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
