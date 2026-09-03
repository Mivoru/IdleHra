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
    bulkClearChest,
    fetchChestSettings,
    type InventoryEquipment,
    type InventoryStack,
  } from '../lib/net/rest';
  import { invalidateOwnedItems } from '../lib/net/queryClient';
  import VirtualList from '../lib/ui/VirtualList.svelte';
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

  // Modul: the needle is lowercased ONCE, not once per item.
  //
  // This was `search.trim().toLowerCase()` inside the predicate, so it ran per
  // row - 17,836 times per keystroke on the worst-affected account, to produce
  // the same string every time. Same for the haystack: it was being built with
  // a template literal and two function calls per row, per pass, and there are
  // two passes (filter, then sort).
  const needle = $derived(search.trim().toLowerCase());

  const visibleEquipment = $derived.by(() => {
    if (filter === 'materials' || filter === 'food') return [];

    const wantWeapons = filter === 'weapons';
    const byKind = filter === 'all';

    return equipment.filter((e) => {
      // Cheapest tests first. The rarity floor is an integer compare and
      // rejects most of a chest at any setting above Normal; the search, which
      // allocates, is asked last and only of what survives.
      if (e.QualityTier < minRarity) return false;
      if (!byKind && isWeapon(e.BaseItemId) !== wantWeapons) return false;
      if (needle === '') return true;

      return `${prettifyBaseId(e.BaseItemId)} ${rarityName(e.QualityTier)}`
        .toLowerCase()
        .includes(needle);
    });
  });

  const visibleMaterials = $derived(
    filter === 'equipment' || filter === 'weapons'
      ? []
      : materials.filter((m) => {
          // Modul: a rarity floor above Normal hides materials entirely rather
          // than showing every stack unfiltered - they have no rarity, so
          // "Rare and up" cannot honestly include them.
          if (minRarity > 0) return false;
          if (needle !== '' && !prettifyBaseId(m.ItemId).toLowerCase().includes(needle)) return false;
          const food = isFood(m.ItemId) || consumableKind(m.ItemId) !== null;
          if (filter === 'food') return food;
          if (filter === 'materials') return !food;
          return true;
        }),
  );

  // Best first. The chest is where a player looks after being away, and a list
  // sorted by anything else buries the one Legendary under four hundred
  // Normals - the same reasoning the session loot feed uses.
  //
  // Modul: the tie-break compares BASE IDS, not prettified names. It used to
  // call prettifyBaseId inside the comparator, which is O(n log n) calls -
  // about 240,000 on a 17,836-item chest, for an ordering the player cannot
  // tell apart from this one, because prettifying only strips a structural
  // suffix and title-cases what is left.
  const sortedEquipment = $derived(
    [...visibleEquipment].sort(
      (a, b) => b.QualityTier - a.QualityTier || a.BaseItemId.localeCompare(b.BaseItemId),
    ),
  );

  // Modul: ONE PASS, not four. This was four separate `.filter().length`
  // calls over the same two arrays - and two of them ran isFood and
  // consumableKind on every material, twice. Small in absolute terms next to
  // the equipment list, but it is in the same reactive statement, so it ran on
  // every keystroke alongside everything else.
  const counts = $derived.by(() => {
    let weapons = 0;
    for (const e of equipment) if (isWeapon(e.BaseItemId)) weapons++;

    let food = 0;
    for (const m of materials) {
      if (isFood(m.ItemId) || consumableKind(m.ItemId) !== null) food++;
    }

    return {
      equipment: equipment.length,
      weapons,
      materials: materials.length - food,
      food,
    };
  });

  // ---------------------------------------------------------------------------
  // Bulk cleanup
  // ---------------------------------------------------------------------------

  // Modul: THE ONLY DRAIN THIS CHEST HAS EVER HAD.
  //
  // Equipment lands on 15% of kills and nothing removed it but the per-item
  // Sell button below. One live account reached 17,836 pieces - about fifty
  // hours of play - at which point this screen was too slow to open, so the
  // cleanup tool and the thing that needed cleaning were the same screen. A
  // player could not dig their way out one click at a time, and nothing in the
  // game suggested they would ever need to.
  //
  // Modul: THE CEILING COMES FROM THE SERVER, and this was a hardcoded 6.
  //
  // The server refuses anything above VillageChestEngine.MaxSweepableQualityTier
  // - Legendary and above is never clearable in bulk, because there is no undo
  // and those are the drops the whole loop is for. A constant here would be a
  // second copy of that rule, and two copies of one truth is this codebase's
  // dominant bug class: raise the server's cap and this dropdown silently keeps
  // offering the old range, lower it and every option past the new cap becomes
  // a button that 400s with nothing on screen saying why.
  //
  // The fallback is the SAFE direction. If the fetch fails the dropdown offers
  // Normal only, so the worst outcome of not knowing the ceiling is a sweep
  // that takes too little.
  const chestSettings = createQuery(() => ({
    queryKey: queryKeys.chestSettings,
    queryFn: fetchChestSettings,
    staleTime: Infinity,
  }));

  const maxSweepTier = $derived(chestSettings.data?.MaxSweepableQualityTier ?? 1);

  // Collapsed by default: a chest that is not yet full does not need this, and
  // it sits above the list everyone came here to read.
  let sweepOpen = $state(false);
  let sweepTier = $state(1);
  let sweeping = $state(false);
  let confirmingSweep = $state<'sell' | 'bin' | null>(null);

  // What the sweep would actually take, counted from the same list on screen -
  // so the number in the button is the number that disappears. Excludes worn
  // pieces for the same reason the server does.
  const sweepCount = $derived(
    equipment.filter((e) => e.QualityTier <= sweepTier && !e.IsEquipped).length,
  );

  async function sweep(sell: boolean) {
    confirmingSweep = null;
    sweeping = true;
    try {
      const result = await bulkClearChest(sweepTier, sell);
      if (!result || result.Success === false) {
        pushLocalNotice('Could not clear the chest.');
        return;
      }

      const kept =
        result.SkippedWornCount > 0
          ? ` ${result.SkippedWornCount} kept - they are being worn.`
          : '';

      if (sell) {
        play('lootDropped');
        pushLocalNotice(
          `Sold ${result.RemovedCount.toLocaleString()} pieces for ${result.GoldGained.toLocaleString()}g.${kept}`,
          'info',
        );
      } else {
        pushLocalNotice(`Binned ${result.RemovedCount.toLocaleString()} pieces.${kept}`, 'info');
      }

      refresh();
    } catch {
      pushLocalNotice('Could not reach the server.');
    } finally {
      sweeping = false;
    }
  }

  function refresh() {
    // Both, always - see invalidateOwnedItems. Selling from the chest changes
    // the material stacks as well as the equipment list, and the two now come
    // from two routes.
    invalidateOwnedItems(client);
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

    <!-- Modul: THE DRAIN. Loot lands on 15% of kills and, until this, the only
         way anything left the chest was one click on one item - so the table
         grew forever and the screen that would have cleared it became the
         screen too slow to open. A cleanup tool that cannot keep up with the
         mess is not a cleanup tool. -->
    <!-- Modul: A PLAIN {#if}, NOT A <details>, AND THAT IS THE FIX FOR A REAL
         BUG rather than a style preference.
         This was a <details>/<summary>, relying on the browser to hide the
         content while closed. It did not: `npm run check:overlap` at 390px
         reported "Bin them all is covered by Unequip", and measuring it showed
         the collapsed panel's buttons still had live 93x35 boxes sitting on top
         of the equipment list - so a player tapping a list row could hit
         "Bin them all" instead. The details element measured 37px tall while
         its own content measured 126px and overflowed it.
         Wrapping the content in a div did not help either: the wrapper still
         computed to `display: block`, because the hiding rule this depends on
         is a UA detail that varies by engine (older builds use `display: none`
         on the children, newer ones a `::details-content` pseudo) and an author
         rule on a child can defeat the first form entirely.
         An {#if} does not depend on any of that. When collapsed the controls
         are NOT IN THE DOM, so they cannot be measured, hit, tabbed to or
         reported by the overlap audit - which is the actual requirement. -->
    <section class="sweep">
      <button
        class="sweeptoggle"
        aria-expanded={sweepOpen}
        onclick={() => (sweepOpen = !sweepOpen)}
      >
        {sweepOpen ? '▾' : '▸'} Clear out the junk
      </button>

      {#if sweepOpen}
      <div class="sweepbody">
        <div class="sweeprow">
          <label>
            Everything up to
            <select bind:value={sweepTier} aria-label="Clear pieces up to this rarity">
              {#each Array(maxSweepTier) as _, i}
                <option value={i + 1}>{rarityName(i + 1)}</option>
              {/each}
            </select>
          </label>

          <span class="dim tiny">
            {sweepCount.toLocaleString()}
            {sweepCount === 1 ? 'piece' : 'pieces'}
          </span>
        </div>

        {#if confirmingSweep === null}
          <div class="sweepbtns">
            <button disabled={sweeping || sweepCount === 0} onclick={() => (confirmingSweep = 'sell')}>
              Sell them all
            </button>
            <button
              class="danger"
              disabled={sweeping || sweepCount === 0}
              onclick={() => (confirmingSweep = 'bin')}
            >
              Bin them all
            </button>
          </div>
        {:else}
          <!-- Modul: both halves confirm, unlike the per-item buttons where only
               Bin does. One click here moves thousands of items at once and
               there is no undo for either - a mis-clicked Sell is not recoverable
               just because it paid. -->
          <p class="confirm">
            {confirmingSweep === 'sell' ? 'Sell' : 'Permanently bin'}
            {sweepCount.toLocaleString()}
            {sweepCount === 1 ? 'piece' : 'pieces'} up to {rarityName(sweepTier)}? Worn gear is kept.
          </p>
          <div class="sweepbtns">
            <button
              class:danger={confirmingSweep === 'bin'}
              disabled={sweeping}
              onclick={() => sweep(confirmingSweep === 'sell')}
            >
              {sweeping ? 'Working...' : 'Yes, do it'}
            </button>
            <button disabled={sweeping} onclick={() => (confirmingSweep = null)}>Cancel</button>
          </div>
        {/if}

        <p class="dim tiny">
          Legendary and above is never cleared this way - use the per-item
          buttons for those. Set an automatic floor in Settings to stop the junk
          arriving in the first place.
        </p>
      </div>
      {/if}
    </section>

    {#if inventory.isPending}
      <Skeleton rows={5} variant="row" />
    {:else if sortedEquipment.length === 0 && visibleMaterials.length === 0}
      <p class="dim">Nothing here.</p>
    {:else}
      {#if sortedEquipment.length > 0}
        <h3>
          Equipment
          <span class="dim tiny">
            {sortedEquipment.length.toLocaleString()} shown
          </span>
        </h3>

        <!-- Modul: WINDOWED. This was a plain {#each} over every piece the
             player owns, inside a box 26rem tall - 17,836 rows on the
             worst-affected account, each one an icon and six buttons, roughly
             180,000 DOM nodes to display about twenty. See ui/VirtualList. -->
        <VirtualList items={sortedEquipment} rowHeight={34} label="Equipment in the chest">
          {#snippet row(item: InventoryEquipment)}
            <div class="row">
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
            </div>
          {/snippet}
        </VirtualList>
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

  /* Still the MATERIALS list. Materials are one row per item id - 63 of them
     on the live database, bounded by the catalogue rather than by playtime -
     so there is nothing to window and a plain list is the right shape. The
     equipment list is the unbounded one, and it is a VirtualList now. */
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
  .rows li,
  .row {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.3rem 0.45rem;
    background: var(--bg-raised);
    border-radius: var(--radius);
    font-size: 0.85rem;
  }

  /* Modul: the virtual list positions rows by arithmetic, so this has to be
     exactly the rowHeight passed to it (34px) - box-sizing included, since the
     padding above is inside it. A row that renders taller overlaps its
     neighbour instead of pushing it down. */
  .row {
    box-sizing: border-box;
    height: 100%;
  }

  .sweep {
    margin: 0.5rem 0 0.9rem;
    padding: 0.5rem 0.6rem;
    background: var(--bg-raised);
    border: 1px solid var(--border);
    border-radius: var(--radius);
  }

  .sweeptoggle {
    display: block;
    width: 100%;
    text-align: left;
    background: transparent;
    border: 0;
    padding: 0;
    cursor: pointer;
    font: inherit;
    font-size: 0.8rem;
    color: var(--text-dim);
  }

  .sweeprow {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    flex-wrap: wrap;
    margin: 0.6rem 0 0.5rem;
    font-size: 0.82rem;
  }

  .sweeprow label {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
  }

  .sweepbtns {
    display: flex;
    gap: 0.4rem;
    flex-wrap: wrap;
  }

  .sweepbtns button {
    font-size: 0.78rem;
  }

  .confirm {
    margin: 0 0 0.5rem;
    font-size: 0.8rem;
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
