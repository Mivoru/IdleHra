<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { playerState } from '../stores/game';
  import { requestScreen } from '../stores/navigation';
  import { queryKeys, fetchMaterials } from '../net/rest';
  import { contentQuery } from '../net/registry.svelte';
  import { stockPotionShelf, unloadPotionShelf, drinkPotion, setPotionAutoDrink } from '../net/commands';
  import { formatNumber, numberTitle } from './format';
  import ItemIcon from './ItemIcon.svelte';
  import QueryError from './QueryError.svelte';
  import { POTION_SLOTS, SHELF_CAPACITY, potionInfo, formatCountdown, type PotionSlotIndex } from './potions';

  // Modul: THE POTION SHELVES (herbs and potions, 2026-10-31). Three, one per
  // potion slot, read from the snapshot's PotionShelf{1,2,3} fields like the
  // larder's Food fields. A potion is drunk from here and nowhere else: the
  // shelf is the inventory it was moved into, so a drink is a decrement on the
  // server's tick and cannot race a save.
  const inventory = createQuery(() => ({ queryKey: queryKeys.materials, queryFn: fetchMaterials }));
  const registry = $derived(contentQuery.data ?? null);
  const snap = $derived($playerState);

  const shelves = $derived(
    snap
      ? POTION_SLOTS.map((s) => {
          const [shelfItem, shelfCount, activeId, activeMs] =
            s.index === 0
              ? [snap.PotionShelf1_ItemId, snap.PotionShelf1_Count, snap.ActiveOffensivePotionId, snap.OffensivePotionDurationMs]
              : s.index === 1
                ? [snap.PotionShelf2_ItemId, snap.PotionShelf2_Count, snap.ActiveDefensivePotionId, snap.DefensivePotionDurationMs]
                : [snap.PotionShelf3_ItemId, snap.PotionShelf3_Count, snap.ActiveGatheringPotionId, snap.GatheringPotionDurationMs];
          return {
            ...s,
            shelfItem,
            shelfCount,
            activeId: activeMs > 0 ? activeId : 0,
            activeMs,
            auto: (snap.PotionAutoDrinkMask & (1 << s.index)) !== 0,
          };
        })
      : [],
  );

  // Potions in the chest, by slot - the stock buttons offer only what fits.
  const chestPotions = $derived(
    (inventory.data?.Stacks ?? [])
      .map((stack) => ({ stack, info: potionInfo(stack.ItemId), id: registry?.itemsByBaseId.get(stack.ItemId)?.Id ?? 0 }))
      .filter((p) => p.info !== null && p.id > 0 && p.stack.Quantity > 0)
      .map((p) => ({ baseId: p.stack.ItemId, quantity: p.stack.Quantity, id: p.id, info: p.info! }))
      .sort((a, b) => a.baseId.localeCompare(b.baseId)),
  );

  function baseIdOf(itemId: number): string {
    return registry?.items.get(itemId)?.BaseId ?? '';
  }

  let error = $state<string | null>(null);
  function run(outcome: { ok: true } | { ok: false; reason: string }) {
    error = outcome.ok ? null : outcome.reason;
  }

  function stock(slot: PotionSlotIndex, itemId: number, have: number, onShelf: number) {
    run(stockPotionShelf(slot, itemId, Math.min(have, SHELF_CAPACITY - onShelf)));
  }
</script>

<section class="panel potions" data-testid="potion-shelf">
  <h2>Potions</h2>
  <p class="dim small">
    Brew potions at Alchemy, then put them on a shelf here. One potion runs in
    each slot at a time, online and offline alike. With auto-drink on, the next
    one is drunk the moment the last runs out.
  </p>

  <ul class="shelves">
    {#each shelves as shelf (shelf.index)}
      {@const active = shelf.activeId > 0 ? potionInfo(baseIdOf(shelf.activeId)) : null}
      {@const held = shelf.shelfCount > 0 ? potionInfo(baseIdOf(shelf.shelfItem)) : null}
      {@const fits = chestPotions.filter((p) => p.info.row.slot === shelf.index && (shelf.shelfCount === 0 || p.id === shelf.shelfItem))}
      <li data-testid="potion-slot-{shelf.index}">
        <div class="head">
          <strong>{shelf.name}</strong>
          <span class="dim tiny">{shelf.hint}</span>
        </div>

        <p class="active" role="status">
          {#if active}
            <span class="running">{active.title}</span>
            <span class="dim">{active.row.effect(active.magnitude)}</span>
            <span class="timer" data-testid="potion-timer-{shelf.index}">{formatCountdown(shelf.activeMs)}</span>
          {:else}
            <span class="dim">Nothing running.</span>
          {/if}
        </p>

        {#if held}
          <div class="held">
            <ItemIcon baseItemId={baseIdOf(shelf.shelfItem)} name={held.title} size="sm" />
            <span class="name">{held.title}<span class="dim tiny block">{held.summary}</span></span>
            <span class="count" title={numberTitle(shelf.shelfCount)} data-testid="potion-count-{shelf.index}">{formatNumber(shelf.shelfCount)}</span>
          </div>
        {/if}
        <div class="actions">
          {#if held}
            <button data-guide="potion-drink" data-testid="potion-drink-{shelf.index}" onclick={() => run(drinkPotion(shelf.index))}>
              {active ? 'Drink now (replaces)' : 'Drink'}
            </button>
          {/if}
          <!-- Always offered: auto-drink can be set before the shelf is stocked. -->
          <label class="auto">
            <input
              type="checkbox"
              checked={shelf.auto}
              data-testid="potion-auto-{shelf.index}"
              onchange={(e) => run(setPotionAutoDrink(shelf.index, (e.currentTarget as HTMLInputElement).checked))}
            />
            Auto-drink
          </label>
          {#if held}
            <button class="ghost" onclick={() => run(unloadPotionShelf(shelf.index))}>Unload</button>
          {:else if shelf.auto}
            <span class="dim tiny">The shelf is empty.</span>
          {/if}
        </div>

        {#each fits as potion (potion.baseId)}
          <button
            class="stock"
            data-guide="potion-stock"
            data-testid="potion-stock-{shelf.index}"
            disabled={shelf.shelfCount >= SHELF_CAPACITY}
            onclick={() => stock(shelf.index as PotionSlotIndex, potion.id, potion.quantity, shelf.shelfCount)}
          >
            <ItemIcon baseItemId={potion.baseId} name={potion.info.title} size="sm" />
            <span>Shelve {potion.info.title}<span class="dim tiny block">{potion.info.summary}</span></span>
            <span class="dim">{formatNumber(potion.quantity)} in chest</span>
          </button>
        {/each}
      </li>
    {/each}
  </ul>

  {#if error}<p class="error tiny" role="alert">{error}</p>{/if}
  {#if inventory.isError && inventory.data === undefined}
    <QueryError query={inventory} what="your chest" />
  {:else if !inventory.isPending && chestPotions.length === 0 && shelves.every((s) => s.shelfCount === 0)}
    <div class="empty">
      <p class="dim">No potions in the chest. Pick herbs, then brew one at Alchemy.</p>
      <button onclick={() => requestScreen('crafting')}>Go to Alchemy</button>
    </div>
  {/if}
</section>

<style>
  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
  }
  .small {
    font-size: 0.8rem;
    margin: 0 0 0.7rem;
  }
  .tiny {
    font-size: 0.72rem;
  }
  .block {
    display: block;
  }
  .shelves {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.6rem;
  }
  .shelves li {
    display: grid;
    gap: 0.35rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.5rem;
  }
  .head {
    display: flex;
    align-items: baseline;
    gap: 0.5rem;
    flex-wrap: wrap;
  }
  .active {
    margin: 0;
    display: flex;
    flex-wrap: wrap;
    gap: 0.2rem 0.5rem;
    align-items: baseline;
    font-size: 0.85rem;
  }
  .running {
    font-weight: 700;
  }
  .timer {
    margin-left: auto;
    font-variant-numeric: tabular-nums;
    font-weight: 700;
    color: var(--accent);
  }
  .held,
  .stock {
    display: grid;
    grid-template-columns: auto 1fr auto;
    gap: 0.5rem;
    align-items: center;
    font-size: 0.85rem;
    text-align: left;
  }
  .count {
    font-variant-numeric: tabular-nums;
    font-weight: 700;
  }
  .name {
    min-width: 0;
  }
  .actions {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
    align-items: center;
  }
  .auto {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    font-size: 0.85rem;
    min-height: 2.75rem;
  }
  .auto input {
    accent-color: var(--accent);
    width: 1.25rem;
    height: 1.25rem;
  }
  .ghost {
    opacity: 0.8;
  }
  .empty {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.4rem 0.8rem;
    margin-top: 0.5rem;
  }
  .empty p {
    margin: 0;
    flex: 1 1 12rem;
  }
  .error {
    color: var(--danger, #b3261e);
  }
</style>
