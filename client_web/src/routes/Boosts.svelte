<script lang="ts">
  // Modul: temporary power - consumables and the buffs they leave behind.
  //
  // One screen rather than two because they are one decision: everything here
  // is a resource you spend now for an advantage that expires. The chrono bank
  // was the third panel and is gone; what remains is the half that was never
  // about buying time.
  //
  // None of this existed in the web client before the 2026-08-02 audit - eight
  // real consumables, both potion slots, the buff timer and a seven-day time
  // bank were all unreachable.

  import { createQuery } from '@tanstack/svelte-query';
  import { queryKeys, fetchMaterials } from '../lib/net/rest';
  import { loadContent, consumableKind, prettifyBaseId, type ContentRegistry } from '../lib/net/content';
  import {
    consumeConsumable,
    MAX_BUFF_TICKS,
  } from '../lib/net/commands';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import { play } from '../lib/ui/audio';
  import Skeleton from '../lib/ui/Skeleton.svelte';

  const snap = $derived($playerState);
  // Modul: MATERIALS ONLY. This reads `inventory.data.Stacks` and nothing
  // else, but was fetching the full snapshot to get there - and on a
  // long-played account that snapshot carries 17,836 equipment rows and 3.2 MB
  // against the 63 stack rows this screen wants. See fetchMaterials.
  const inventory = createQuery(() => ({ queryKey: queryKeys.materials, queryFn: fetchMaterials }));

  let registry = $state<ContentRegistry | null>(null);
  $effect(() => {
    void loadContent().then((loaded) => (registry = loaded));
  });

  // ---------------------------------------------------------------------------
  // Consumables held
  // ---------------------------------------------------------------------------

  interface HeldConsumable {
    itemId: number;
    baseId: string;
    kind: 'food' | 'offensive' | 'defensive';
    quantity: number;
    attack: number;
    defense: number;
  }

  const held = $derived.by((): HeldConsumable[] => {
    if (!registry) return [];
    const stacks = inventory.data?.Stacks ?? [];
    const rows: HeldConsumable[] = [];

    for (const stack of stacks) {
      const kind = consumableKind(stack.ItemId);
      if (kind === null) continue;
      const definition = registry.itemsByBaseId.get(stack.ItemId);
      if (!definition) continue;

      // Modul: BOTH HALVES. This counted only the backpack, on the reasoning
      // that a stash quantity "is in the bank and cannot be consumed without
      // withdrawing it first". That has not been true since storage became one
      // unbounded village chest: every spend goes through
      // InventoryAndStashSystem.TryConsumeUnifiedAsync, which draws from
      // CommodityRecords AND VillageStashInstances and only refuses when the
      // SUM is short.
      //
      // So the reasoning was inverted - hiding the stash did not avoid a button
      // that does nothing, it hid a potion the server would have been happy to
      // drink. Same defect the larder had, and the chest already reads both.
      const quantity = stack.Quantity;
      if (quantity <= 0) continue;

      rows.push({
        itemId: definition.Id,
        baseId: stack.ItemId,
        kind,
        quantity,
        attack: definition.FlatAttackPower,
        defense: definition.FlatDefenseRating,
      });
    }

    return rows.sort((a, b) => a.kind.localeCompare(b.kind) || a.itemId - b.itemId);
  });

  const buffTicks = $derived(snap?.RemainingBuffDurationTicks ?? 0);
  const saturated = $derived(buffTicks > MAX_BUFF_TICKS);

  function use(row: HeldConsumable) {
    const outcome = consumeConsumable(row.itemId, buffTicks);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    play('windowOpen');
  }

  // ---------------------------------------------------------------------------
  // Active effects
  // ---------------------------------------------------------------------------

  const offensiveId = $derived(snap?.ActiveOffensivePotionId ?? 0);
  const defensiveId = $derived(snap?.ActiveDefensivePotionId ?? 0);

  function itemLabel(itemId: number): string {
    const definition = registry?.items.get(itemId);
    return definition ? prettifyBaseId(definition.BaseId) : `Item #${itemId}`;
  }

  function durationLabel(ms: number): string {
    if (ms <= 0) return 'expired';
    const seconds = Math.round(ms / 1000);
    if (seconds < 60) return `${seconds}s`;
    return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
  }

  // Ticks are 10 Hz, which is the one conversion worth doing in one place -
  // reading this as seconds is how a two-hour cap looks like twelve minutes.
  const buffSeconds = $derived(Math.round(buffTicks / 10));

</script>

<div class="grid">
  <section class="panel">
    <h2>Consumables</h2>
    <p class="dim small">
      Eight exist in the game - four foods and two potions of each kind. Only
      what is in your backpack can be used; anything in the bank has to be
      withdrawn first.
    </p>

    {#if saturated}
      <p class="warn" role="status">
        You are saturated with buff duration. Using anything now would be
        refused by disconnecting you, so the buttons stay off until the timer
        falls below the cap.
      </p>
    {/if}

    {#if inventory.isPending || !registry}
      <Skeleton />
    {:else if held.length === 0}
      <p class="dim">You are not carrying any consumables.</p>
    {:else}
      <ul class="items">
        {#each held as row (row.itemId)}
          <li>
            <span class="kind" data-kind={row.kind}>{row.kind}</span>
            <span class="name">{prettifyBaseId(row.baseId)}</span>
            <span class="stat">
              {#if row.attack > 0}<span class="atk">+{row.attack} atk</span>{/if}
              {#if row.defense > 0}<span class="def">+{row.defense} def</span>{/if}
            </span>
            <span class="qty">x{row.quantity}</span>
            <button class="tiny-btn" disabled={saturated} onclick={() => use(row)}>Use</button>
          </li>
        {/each}
      </ul>
    {/if}
  </section>

  <section class="panel">
    <h2>Active effects</h2>

    {#if offensiveId === 0 && defensiveId === 0 && buffTicks === 0}
      <p class="dim">Nothing active.</p>
    {:else}
      <ul class="effects">
        {#if offensiveId > 0}
          <li>
            <span class="kind" data-kind="offensive">offensive</span>
            <span class="name">{itemLabel(offensiveId)}</span>
            <span class="time">{durationLabel(snap?.OffensivePotionDurationMs ?? 0)}</span>
          </li>
        {/if}
        {#if defensiveId > 0}
          <li>
            <span class="kind" data-kind="defensive">defensive</span>
            <span class="name">{itemLabel(defensiveId)}</span>
            <span class="time">{durationLabel(snap?.DefensivePotionDurationMs ?? 0)}</span>
          </li>
        {/if}
      </ul>

      {#if buffTicks > 0}
        <h3>Saturation</h3>
        <div class="bar">
          <div
            class="bar-fill"
            class:over={saturated}
            style="width: {Math.min(100, (buffTicks / MAX_BUFF_TICKS) * 100)}%"
          ></div>
          <span class="bar-label">{Math.floor(buffSeconds / 60)}m of 120m</span>
        </div>
        <p class="dim tiny">
          Buff duration accumulates rather than replacing. Past two hours the
          server stops accepting new consumables entirely.
        </p>
      {/if}
    {/if}
  </section>

</div>

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
    margin: 0.3rem 0 0;
  }

  .warn {
    font-size: 0.82rem;
    color: var(--danger);
    border-left: 2px solid var(--danger);
    padding-left: 0.55rem;
    margin: 0 0 0.7rem;
  }

  .items,
  .effects {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  .items li {
    display: grid;
    grid-template-columns: auto 1fr auto auto auto;
    align-items: center;
    gap: 0.5rem;
    padding: 0.4rem 0.55rem;
    background: var(--bg-raised);
    border-radius: var(--radius);
  }

  .effects li {
    display: grid;
    grid-template-columns: auto 1fr auto;
    align-items: center;
    gap: 0.5rem;
    padding: 0.4rem 0.55rem;
    background: var(--bg-raised);
    border-radius: var(--radius);
  }

  /* Colour AND a word, never colour alone - the kind is readable without it. */
  .kind {
    font-size: 0.62rem;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    padding: 0.1rem 0.35rem;
    border-radius: 999px;
    border: 1px solid currentColor;
  }

  .kind[data-kind='food'] {
    color: var(--good);
  }
  .kind[data-kind='offensive'] {
    color: var(--rarity-10);
  }
  .kind[data-kind='defensive'] {
    color: var(--accent);
  }

  .name {
    font-size: 0.88rem;
    min-width: 0;
  }

  .stat {
    display: flex;
    gap: 0.35rem;
    font-size: 0.72rem;
    font-variant-numeric: tabular-nums;
  }

  .atk {
    color: var(--rarity-10);
  }
  .def {
    color: var(--accent);
  }

  .qty,
  .time {
    font-size: 0.78rem;
    color: var(--text-dim);
    font-variant-numeric: tabular-nums;
  }

  .bar-fill.over {
    background: var(--danger);
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.55rem;
  }
</style>
