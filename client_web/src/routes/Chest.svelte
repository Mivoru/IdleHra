<script lang="ts">
  // Modul: the village chest. Everything a character produces ends up here.
  //
  // It replaces the backpack, which capped at twenty shared slots and stopped
  // the whole simulation when it filled - measured at about forty minutes of
  // idle play. There is no capacity here at all: materials stack without limit
  // and equipment accumulates, and nothing is ever destroyed on the way in,
  // because a low-tier piece is fuel for a forge upgrade rather than junk.
  //
  // STORED IN TWO SHAPES, SHOWN AS ONE. Stackable materials carry a quantity
  // per item id; equipment is one row per piece, because each has its own
  // affix roll and that is what makes two identical-looking pieces different
  // objects. Both arrive in the same inventory snapshot and the player should
  // never have to know the difference.
  //
  // The only destruction in this game happens on this screen, deliberately,
  // by the player - sell for gold or bin for nothing.

  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import {
    queryKeys,
    fetchInventory,
    sellFromChest,
    discardFromChest,
    type InventoryEquipment,
    type InventoryStack,
  } from '../lib/net/rest';
  import { prettifyBaseId, isFood, consumableKind } from '../lib/net/content';
  import { rarityColor, rarityName, shouldGlow, MAX_QUALITY_TIER } from '../lib/ui/rarity';
  import { pushLocalNotice } from '../lib/stores/game';
  import { connection } from '../lib/net/connection';
  import { CommandType } from '../lib/net/protocol.generated';
  import { resolveSlotIndex } from '../lib/ui/slots';
  import { play } from '../lib/ui/audio';
  import ItemIcon from '../lib/ui/ItemIcon.svelte';
  import { requestScreen, setPendingFocusEquipment } from '../lib/stores/navigation';
  import Skeleton from '../lib/ui/Skeleton.svelte';

  const client = useQueryClient();
  const inventory = createQuery(() => ({ queryKey: queryKeys.inventory, queryFn: fetchInventory }));

  type Filter = 'all' | 'equipment' | 'weapons' | 'materials' | 'food';

  let filter = $state<Filter>('all');
  let busy = $state(false);

  // Modul: classified by the same BaseId markers the server uses, not by a
  // hand-written id list. A list would go stale the first time content
  // changed, and the failure would be an item silently missing from every
  // filter rather than an error.
  function isWeapon(baseItemId: string): boolean {
    return baseItemId.includes('_weapon_slot_');
  }

  // From the inventory snapshot, which reads EquipmentInstances - the same
  // table equipping, forge fusion, affix reroll and market listing all use. An
  // earlier version read the bank instead, which meant a looted piece could
  // never be worn or upgraded.
  const equipment = $derived(((inventory.data?.Equipment ?? []) as InventoryEquipment[]));
  const materials = $derived(
    ((inventory.data?.Stacks ?? []) as InventoryStack[]).filter(
      (s) => s.Quantity > 0,
    ),
  );

  // Modul: SEARCH AND RARITY, alongside the category tabs.
  //
  // The tabs answer "what kind of thing", which stops helping the moment a
  // player owns ninety pieces of equipment - the reason the market grew a
  // search box and a rarity floor, and the reason the chest needed the same
  // two. Deliberately additive: the tabs stay, and these narrow whatever the
  // tab already selected.
  let search = $state('');
  let minRarity = $state(0);

  const matchesSearch = (label: string) =>
    search.trim() === '' || label.toLowerCase().includes(search.trim().toLowerCase());

  const visibleEquipment = $derived(
    filter === 'materials' || filter === 'food'
      ? []
      : equipment.filter(
          (e) =>
            (filter === 'all' ||
              (filter === 'weapons' ? isWeapon(e.BaseItemId) : !isWeapon(e.BaseItemId))) &&
            e.QualityTier >= minRarity &&
            matchesSearch(`${prettifyBaseId(e.BaseItemId)} ${rarityName(e.QualityTier)}`),
        ),
  );

  const visibleMaterials = $derived(
    filter === 'equipment' || filter === 'weapons'
      ? []
      : materials.filter((m) => {
          // Modul: a rarity floor above Normal hides materials entirely rather
          // than showing every stack unfiltered - they have no rarity, so
          // "Rare and up" cannot honestly include them.
          if (minRarity > 0) return false;
          if (!matchesSearch(prettifyBaseId(m.ItemId))) return false;
          const food = isFood(m.ItemId) || consumableKind(m.ItemId) !== null;
          if (filter === 'food') return food;
          if (filter === 'materials') return !food;
          return true;
        }),
  );

  // Best first. The chest is where a player looks after being away, and a list
  // sorted by anything else buries the one Legendary under four hundred
  // Normals - the same reasoning the session loot feed uses.
  const sortedEquipment = $derived(
    [...visibleEquipment].sort((a, b) => b.QualityTier - a.QualityTier || a.BaseItemId.localeCompare(b.BaseItemId)),
  );

  const counts = $derived({
    equipment: equipment.length,
    weapons: equipment.filter((e) => isWeapon(e.BaseItemId)).length,
    materials: materials.filter((m) => !(isFood(m.ItemId) || consumableKind(m.ItemId) !== null)).length,
    food: materials.filter((m) => isFood(m.ItemId) || consumableKind(m.ItemId) !== null).length,
  });

  function refresh() {
    client.invalidateQueries({ queryKey: queryKeys.inventory });
  }

  async function act(
    target: { equipmentId: number } | { itemId: string; quantity: number },
    sell: boolean,
    label: string,
  ) {
    busy = true;
    try {
      const result = sell ? await sellFromChest(target) : await discardFromChest(target);
      // Success:false arrives with HTTP 200 - the item was already gone, or
      // the quantity was stale. Checking only the status would report a
      // failure as a sale.
      if (!result || result.Success === false) {
        pushLocalNotice(`Could not ${sell ? 'sell' : 'bin'} ${label}.`);
      } else if (sell) {
        play('lootDropped');
        pushLocalNotice(`Sold ${label} for ${result.GoldGained.toLocaleString()}g.`, 'info');
      } else {
        pushLocalNotice(`Binned ${label}.`, 'info');
      }
      refresh();
    } catch {
      pushLocalNotice('Could not reach the server.');
    } finally {
      busy = false;
    }
  }

  // Modul: equipping lives HERE now.
  //
  // It used to be on an Inventory screen that the chest replaced, and removing
  // that screen without moving this left looted gear unwearable - the chest
  // could sell a Legendary but not put it on. Found by the exercise script,
  // which asserts a player can act on what they own rather than that the
  // screen rendered.
  //
  // UnequipItem takes a SLOT INDEX, not an instance id - the same TargetId
  // field carrying two different meanings, which is why the two calls do not
  // share a helper.
  function equip(instanceId: number) {
    connection.send({ Command: CommandType.EquipItem, TargetId: instanceId });
    setTimeout(refresh, 700);
  }

  function unequip(baseItemId: string) {
    const slotIndex = resolveSlotIndex(baseItemId);
    if (slotIndex < 0) return pushLocalNotice('That piece has no equipment slot.');
    connection.send({ Command: CommandType.UnequipItem, TargetId: slotIndex });
    setTimeout(refresh, 700);
  }

  function openRerollInForge(instanceId: number) {
    setPendingFocusEquipment(instanceId);
    requestScreen('forge');
  }

  // Binning is irreversible and sits next to a button that is not, so it asks
  // once. Selling does not - the gold is a receipt and the market still has
  // the item's value written down.
  let confirming = $state<string | null>(null);
</script>

<div class="wrap">
  <section class="panel">
    <header class="head">
      <h2>Village chest</h2>
      <span class="dim tiny">
        Unlimited. Crafting, the forge and the market all draw from here.
      </span>
    </header>

    <div class="filters" role="group" aria-label="Filter">
      {#each [['all', 'All', equipment.length + materials.length], ['equipment', 'Equipment', counts.equipment - counts.weapons], ['weapons', 'Weapons', counts.weapons], ['materials', 'Materials', counts.materials], ['food', 'Food', counts.food]] as [key, label, count]}
        <button class:active={filter === key} onclick={() => (filter = key as Filter)}>
          {label}
          <span class="count">{count}</span>
        </button>
      {/each}
    </div>

    <div class="finders">
      <input
        type="search"
        placeholder="Search the chest..."
        bind:value={search}
        aria-label="Search the chest"
      />
      <select bind:value={minRarity} aria-label="Minimum rarity">
        <option value={0}>Any rarity</option>
        {#each Array(MAX_QUALITY_TIER) as _, i}
          <option value={i + 1}>{rarityName(i + 1)}+</option>
        {/each}
      </select>
    </div>

    {#if inventory.isPending}
      <Skeleton rows={5} variant="row" />
    {:else if sortedEquipment.length === 0 && visibleMaterials.length === 0}
      <p class="dim">Nothing here.</p>
    {:else}
      {#if sortedEquipment.length > 0}
        <h3>Equipment</h3>
        <ul class="rows">
          {#each sortedEquipment as item (item.Id)}
            <li>
              <ItemIcon
                baseItemId={item.BaseItemId}
                name={prettifyBaseId(item.BaseItemId)}
                qualityTier={item.QualityTier}
                size="sm"
              />
              <span
                class="name"
                style="color: {rarityColor(item.QualityTier)}"
                class:rarity-glow={shouldGlow(item.QualityTier)}
              >
                {prettifyBaseId(item.BaseItemId)}
              </span>
              <span class="dim tiny">{rarityName(item.QualityTier)}</span>

              {#if item.IsEquipped}
                <button class="tiny-btn" onclick={() => unequip(item.BaseItemId)}>Unequip</button>
              {:else}
                <button class="tiny-btn" onclick={() => equip(item.Id)}>Equip</button>
              {/if}

              <!-- Modul: the reroll is in the Forge and players did not find
                   it, because the thing being rerolled is an item and items
                   are here. This does not move it - fusion needs the Forge
                   building and the reroll sits beside it - it just puts the
                   door where the player is standing, with the item already
                   chosen when they arrive. -->
              <button
                class="tiny-btn"
                title="Reroll this piece's affixes in the Forge"
                onclick={() => openRerollInForge(item.Id)}
              >
                Reroll
              </button>

              <button
                class="tiny-btn"
                disabled={busy || item.IsEquipped}
                title={item.IsEquipped ? 'Worn - take it off first' : ''}
                onclick={() => act({ equipmentId: item.Id }, true, prettifyBaseId(item.BaseItemId))}
              >
                Sell
              </button>

              {#if confirming === `eq:${item.Id}`}
                <button
                  class="tiny-btn danger"
                  disabled={busy}
                  onclick={() => {
                    confirming = null;
                    act({ equipmentId: item.Id }, false, prettifyBaseId(item.BaseItemId));
                  }}
                >
                  Really bin
                </button>
              {:else}
                <button
                  class="tiny-btn"
                  disabled={busy || item.IsEquipped}
                  title={item.IsEquipped ? 'Worn - take it off first' : ''}
                  onclick={() => (confirming = `eq:${item.Id}`)}
                >
                  Bin
                </button>
              {/if}
            </li>
          {/each}
        </ul>
      {/if}

      {#if visibleMaterials.length > 0}
        <h3>Materials</h3>
        <ul class="rows">
          {#each visibleMaterials as stack (stack.ItemId)}
            {@const total = stack.Quantity}
            <li>
              <ItemIcon baseItemId={stack.ItemId} name={prettifyBaseId(stack.ItemId)} size="sm" />
              <span class="name">{prettifyBaseId(stack.ItemId)}</span>
              <span class="qty">{total.toLocaleString()}</span>

              <button
                class="tiny-btn"
                disabled={busy}
                onclick={() => act({ itemId: stack.ItemId, quantity: total }, true, prettifyBaseId(stack.ItemId))}
              >
                Sell all
              </button>

              {#if confirming === `mat:${stack.ItemId}`}
                <button
                  class="tiny-btn danger"
                  disabled={busy}
                  onclick={() => {
                    confirming = null;
                    act({ itemId: stack.ItemId, quantity: total }, false, prettifyBaseId(stack.ItemId));
                  }}
                >
                  Really bin
                </button>
              {:else}
                <button class="tiny-btn" disabled={busy} onclick={() => (confirming = `mat:${stack.ItemId}`)}>
                  Bin
                </button>
              {/if}
            </li>
          {/each}
        </ul>
      {/if}
    {/if}

    <p class="dim tiny">
      Selling pays 40% of an item's market value - the price of not waiting for
      a buyer. Listing on the market pays the rest.
      <!-- Stated because the difference is otherwise invisible and a player
           who sells everything here would never learn the market exists for a
           reason. -->
    </p>
  </section>
</div>

<style>
  .finders {
    display: grid;
    grid-template-columns: 2fr 1fr;
    gap: 0.35rem;
    margin: 0.4rem 0;
  }

  .finders input,
  .finders select {
    min-width: 0;
    width: 100%;
  }

  @media (max-width: 560px) {
    .finders {
      grid-template-columns: 1fr;
    }
  }

  .wrap {
    padding: 1rem;
    max-width: 56rem;
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
  }

  .head {
    display: flex;
    align-items: baseline;
    gap: 0.7rem;
    flex-wrap: wrap;
  }

  h2 {
    margin: 0 0 0.6rem;
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
  .tiny {
    font-size: 0.72rem;
  }

  .filters {
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
    /* The header rule above ends the title; the tabs are a new thing and were
       sitting hard against it, which read as the rule underlining THEM. */
    margin-top: 0.7rem;
    margin-bottom: 0.8rem;
  }

  .filters button {
    display: inline-flex;
    align-items: baseline;
    gap: 0.3rem;
    font-size: 0.82rem;
    color: var(--text-dim);
  }

  .filters button.active {
    border-color: var(--accent);
    color: var(--accent);
  }

  .filters .count {
    font-size: 0.68rem;
    opacity: 0.75;
    font-variant-numeric: tabular-nums;
  }

  .rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.25rem;
    max-height: 26rem;
    overflow-y: auto;
  }

  /* Flex, not a shared grid template: an equipment row carries six children
     and a material row four, and one template would misalign whichever it was
     not written for. */
  .rows li {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.3rem 0.45rem;
    background: var(--bg-raised);
    border-radius: var(--radius);
    font-size: 0.85rem;
  }

  .name {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .qty {
    font-variant-numeric: tabular-nums;
    color: var(--text-dim);
  }

  .tiny-btn {
    font-size: 0.7rem;
    padding: 0.2rem 0.5rem;
  }

  /* Only after the first press. The unconfirmed button looks like every other
     one, so nothing is destroyed by a mis-click on a crowded row. */
  .danger {
    border-color: var(--danger);
    color: var(--danger);
  }
</style>
