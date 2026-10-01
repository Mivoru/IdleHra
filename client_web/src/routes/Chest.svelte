<script lang="ts">
  import { PREF_CHEST_FILTER, PREF_CHEST_MIN_RARITY, readPrefAs, writePref } from '../lib/net/prefs';
  import { formatGold, formatNumber } from '../lib/ui/format';
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
    toggleChestLock,
    bulkClearChest,
    fetchChestSettings,
    saveChestSettings,
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
  import QueryError from '../lib/ui/QueryError.svelte';
  import { isNarrow, isWide } from '../lib/ui/media';
  import ItemRow from '../lib/ui/ItemRow.svelte';
  import Affixes from '../lib/ui/Affixes.svelte';
  import { isChestMaterial, splitWorn, itemMetaLine } from '../lib/ui/itemRow';
  import { contentRegistry } from '../lib/net/registry.svelte';
  import ContextMenu, { type MenuItem } from '../lib/ui/ContextMenu.svelte';
  import Hint from '../lib/ui/Hint.svelte';
  import { onDestroy } from 'svelte';
  import { SvelteMap } from 'svelte/reactivity';

  // Modul: TWO NUMBERS FOR ONE CONTRACT, because the row has two shapes.
  //
  // VirtualList positions rows by arithmetic, so whatever `.row` actually
  // renders as, this has to match it. The row is ItemRow now (task 99): two
  // single-line text lines at every width - the name, then tier, rarity and
  // the top affixes - so on a wide screen it is two lines of text plus padding
  // (46; it was 34 while it was one line), and below 40rem it is still the
  // 44px buttons plus padding, which the two lines fit beside. It was 78 while
  // the row carried five buttons on a second line of their own.
  //
  // Modul: do NOT write a literal style or script tag in a comment here. The
  // Svelte parser scans this block as raw text looking for its closing tag,
  // and an angle-bracketed one in a comment made it report
  // "`<script>` was left open" pointing at the LAST line of the file, which
  // says nothing about where the problem is.
  //
  // Kept beside each other rather than derived from a CSS variable: the CSS
  // and this number have to agree, and two literals a reader can compare are
  // easier to keep honest than one indirection they have to resolve.
  const ROW_H_WIDE = 46;
  const ROW_H_NARROW = 56;
  const equipmentRowHeight = $derived($isNarrow ? ROW_H_NARROW : ROW_H_WIDE);

  const client = useQueryClient();
  const inventory = createQuery(() => ({ queryKey: queryKeys.inventory, queryFn: fetchInventory }));

  type Filter = 'all' | 'equipment' | 'weapons' | 'materials' | 'food';

  // Task 52: the tab and the rarity floor are remembered on this device, so a
  // player who always sorts through weapons does not re-pick it every visit.
  // The search box is not - it is a question asked once.
  const FILTERS: readonly Filter[] = ['all', 'equipment', 'weapons', 'materials', 'food'];
  let filter = $state<Filter>(
    readPrefAs(PREF_CHEST_FILTER, (v): v is Filter => (FILTERS as readonly string[]).includes(v), 'all'),
  );
  $effect(() => writePref(PREF_CHEST_FILTER, filter));
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
  // Modul: GOLD IS NOT A MATERIAL (task 99) - see isChestMaterial. The
  // server would answer Sell all and Bin on it by deleting the gold for 0.
  const materials = $derived(((inventory.data?.Stacks ?? []) as InventoryStack[]).filter(isChestMaterial));

  // Modul: SEARCH AND RARITY, alongside the category tabs.
  //
  // The tabs answer "what kind of thing", which stops helping the moment a
  // player owns ninety pieces of equipment - the reason the market grew a
  // search box and a rarity floor, and the reason the chest needed the same
  // two. Deliberately additive: the tabs stay, and these narrow whatever the
  // tab already selected.
  let search = $state('');
  let minRarity = $state(
    Number(readPrefAs(PREF_CHEST_MIN_RARITY, (v) => /^(?:[0-9]|1[0-4])$/.test(v), '0')),
  );
  $effect(() => writePref(PREF_CHEST_MIN_RARITY, String(minRarity)));

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

  // Modul: WORN PIECES ARE THEIR OWN GROUP, ON TOP (task 99). A worn piece
  // used to differ from a loose one only by its button saying Unequip, so the
  // gear a character has on was scattered through five thousand rows. The
  // group is bounded by the roster's slots, so it is a plain list; the loose
  // pieces are the unbounded half and stay in the VirtualList.
  const groups = $derived(splitWorn(sortedEquipment));

  // What the loose list is called. "Equipment" meant two things - the tab
  // (everything but weapons) and the heading (everything) - so each says what
  // it holds.
  const looseTitle = $derived(
    filter === 'weapons' ? 'Weapons' : filter === 'equipment' ? 'Armour & tools' : 'Gear',
  );

  // ---------------------------------------------------------------------------
  // Inspect (task 99)
  // ---------------------------------------------------------------------------

  // Modul: BY ID, NOT BY OBJECT. The inventory refetches after every action,
  // and a held object would keep showing the affixes from before a reroll or
  // a lock. Looking the id up again also closes the pane by itself when the
  // piece is sold or binned.
  let inspectedId = $state<number | null>(null);
  const inspected = $derived(
    inspectedId === null ? null : (equipment.find((e) => e.Id === inspectedId) ?? null),
  );
  let inlineDetail = $state<HTMLElement | null>(null);

  function inspect(id: number) {
    inspectedId = id;
    // Below the wide breakpoint the detail opens above the list, which may be
    // a long way up from the row that asked for it.
    if (!$isWide) requestAnimationFrame(() => inlineDetail?.scrollIntoView({ block: 'nearest', behavior: 'smooth' }));
  }

  function inspectedMeta(item: InventoryEquipment): string {
    const regionTier = contentRegistry.current?.itemsByBaseId.get(item.BaseItemId)?.RegionTier ?? 0;
    return itemMetaLine({ regionTier, qualityTier: item.QualityTier });
  }

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
        pushLocalNotice('Could not clear the chest.', 'error');
        return;
      }

      const kept =
        result.SkippedWornCount > 0
          ? ` ${result.SkippedWornCount} kept - they are being worn.`
          : '';

      if (sell) {
        play('itemSold');
        pushLocalNotice(
          `Sold ${formatNumber(result.RemovedCount)} pieces for ${formatGold(result.GoldGained)}.${kept}`,
          'info',
        );
      } else {
        pushLocalNotice(`Binned ${formatNumber(result.RemovedCount)} pieces.${kept}`, 'info');
      }

      refresh();
    } catch {
      pushLocalNotice('Could not reach the server.', 'error');
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
        pushLocalNotice(`Could not ${sell ? 'sell' : 'bin'} ${label}.`, 'error');
      } else if (sell) {
        play('itemSold');
        pushLocalNotice(`Sold ${label} for ${formatGold(result.GoldGained)}.`, 'info');
      } else {
        pushLocalNotice(`Binned ${label}.`, 'info');
      }
      refresh();
    } catch {
      pushLocalNotice('Could not reach the server.', 'error');
    } finally {
      busy = false;
    }
  }

  // Modul: THE LOCK, which the server has always understood and nothing could
  // ever set.
  //
  // A locked piece cannot be rerolled, fused, sold, binned, or taken by the
  // sweep. That last one is why it matters: "Sell them all" clears a whole
  // rarity band in one call, and its ceiling of Epic was the only way to say
  // "not that one" - which cannot express "keep THIS Epic sword".
  //
  // The server TOGGLES and reports the state it ended in, so this never has to
  // guess: two clicks racing cannot leave the screen showing whichever lost.
  async function toggleLock(equipmentId: number, label: string) {
    busy = true;
    try {
      const result = await toggleChestLock(equipmentId);
      if (!result || result.Success === false) {
        pushLocalNotice(`Could not change the lock on ${label}.`, 'error');
      } else {
        pushLocalNotice(
          result.Locked
            ? `${label} is locked - it cannot be sold, binned, swept, rerolled or fused.`
            : `${label} is unlocked.`,
          'info',
        );
      }
      refresh();
    } catch {
      pushLocalNotice('Could not reach the server.', 'error');
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
    // On send, like every command: a refusal answers with the error tone.
    play('itemEquipped');
    setTimeout(refresh, 700);
  }

  function unequip(baseItemId: string) {
    const slotIndex = resolveSlotIndex(baseItemId);
    if (slotIndex < 0) return pushLocalNotice('That piece has no equipment slot.', 'error');
    connection.send({ Command: CommandType.UnequipItem, TargetId: slotIndex });
    setTimeout(refresh, 700);
  }

  function openRerollInForge(instanceId: number) {
    setPendingFocusEquipment(instanceId);
    requestScreen('forge');
  }

  // Binning is irreversible and sits next to a button that is not, so it asks
  // once. Selling does not ask, because it has an undo (below).
  let confirming = $state<string | null>(null);

  // ---------------------------------------------------------------------------
  // The row menu (task 81)
  // ---------------------------------------------------------------------------

  // Modul: ONE ACTION ON THE ROW, THE REST IN A MENU. A row carried five
  // buttons (Equip/Unequip, Reroll, Lock, Sell, Bin), and on a phone they took
  // a second line of their own. The row keeps the action a player takes most,
  // wearing the piece, and the others open from the "More" button in the
  // shared ContextMenu. The lock state could only be read off its button
  // before, so it is a badge on the row now. A lock you cannot see is a lock
  // you have to click to check.
  //
  // Task 99: the same menu serves a material row, which keeps Sell all on the
  // row and moved Bin in here - it weighed the same as Sell all, 6px away.
  type MenuTarget =
    | { x: number; y: number; kind: 'eq'; item: InventoryEquipment }
    | { x: number; y: number; kind: 'mat'; stack: InventoryStack };
  let menu = $state<MenuTarget | null>(null);

  function openMenu(e: MouseEvent, item: InventoryEquipment) {
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
    confirming = null;
    menu = { x: rect.left, y: rect.bottom + 4, kind: 'eq', item };
  }

  function openMaterialMenu(e: MouseEvent, stack: InventoryStack) {
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
    confirming = null;
    menu = { x: rect.left, y: rect.bottom + 4, kind: 'mat', stack };
  }

  const menuTitle = $derived(
    menu === null
      ? ''
      : menu.kind === 'eq'
        ? `${prettifyBaseId(menu.item.BaseItemId)} - ${rarityName(menu.item.QualityTier)}`
        : prettifyBaseId(menu.stack.ItemId),
  );

  function materialMenuItems(stack: InventoryStack): MenuItem[] {
    const label = prettifyBaseId(stack.ItemId);
    const binKey = `mat:${stack.ItemId}`;
    return [
      confirming === binKey
        ? {
            label: `Really bin ${formatNumber(stack.Quantity)}`,
            danger: true,
            disabled: busy,
            onSelect: () => {
              confirming = null;
              act({ itemId: stack.ItemId, quantity: stack.Quantity }, false, label);
            },
          }
        : {
            label: 'Bin',
            disabled: busy,
            title: 'Destroy the whole stack for nothing',
            keepOpen: true,
            onSelect: () => (confirming = binKey),
          },
    ];
  }

  const menuItems = $derived.by((): MenuItem[] => {
    if (!menu) return [];
    if (menu.kind === 'mat') return materialMenuItems(menu.stack);
    const item = menu.item;
    const label = prettifyBaseId(item.BaseItemId);
    const blocked = item.IsEquipped ? 'Worn - take it off first' : item.IsAffixLocked ? 'Locked - unlock it first' : '';
    const binKey = `eq:${item.Id}`;
    return [
      // Modul: INSPECT FIRST (task 99). The affixes are what makes this piece
      // different from the next one with the same name, and they were nowhere
      // on this screen. It opens the Forge's own Affixes panel.
      { label: 'Inspect', title: 'Show every affix on this piece', onSelect: () => inspect(item.Id) },
      { label: 'Reroll in Forge', title: "Reroll this piece's affixes in the Forge", onSelect: () => openRerollInForge(item.Id) },
      {
        label: item.IsAffixLocked ? 'Unlock' : 'Lock',
        title: item.IsAffixLocked
          ? 'Locked - cannot be sold, binned, swept, rerolled or fused. Unlock it.'
          : 'Lock this piece so nothing can sell, bin, sweep, reroll or fuse it',
        onSelect: () => toggleLock(item.Id, label),
      },
      {
        // The price is the server's (InventoryEquipment.SellValueGold), so the
        // menu says what the tap is worth before it is made.
        label: `Sell · ${formatGold(item.SellValueGold)}`,
        disabled: busy || blocked !== '',
        title: blocked,
        note: blocked,
        onSelect: () => queueSale({ equipmentId: item.Id }, label, `eq:${item.Id}`),
      },
      confirming === binKey
        ? {
            label: 'Really bin',
            danger: true,
            separated: true,
            disabled: busy,
            onSelect: () => {
              confirming = null;
              act({ equipmentId: item.Id }, false, label);
            },
          }
        : {
            label: 'Bin',
            separated: true,
            disabled: busy || item.IsEquipped,
            title: item.IsEquipped ? 'Worn - take it off first' : '',
            note: item.IsEquipped ? 'Worn - take it off first' : '',
            keepOpen: true,
            onSelect: () => (confirming = binKey),
          },
    ];
  });

  // ---------------------------------------------------------------------------
  // Undo on a sale (task 81)
  // ---------------------------------------------------------------------------

  // Modul: THE CLIENT HOLDS THE SALE FOR FIVE SECONDS, AND THE SERVER NEVER
  // KNOWS. A sale is final on the server and a mis-tap on a crowded row sold
  // the wrong piece. Instead of a server-side undo (a second write path for
  // gold, see server/CLAUDE.md "Two gold paths"), the request is not sent until
  // the window closes. Undo just forgets it.
  //
  // Leaving the screen SENDS what is pending (onDestroy), because navigating
  // away is not changing your mind. Closing the tab inside the window sends
  // nothing, which keeps the item. That is the safe direction to fail in.
  const UNDO_MS = 5000;
  type SaleTarget = { equipmentId: number } | { itemId: string; quantity: number };
  const pendingSales = new SvelteMap<string, { target: SaleTarget; label: string; deadline: number; timer: ReturnType<typeof setTimeout> }>();
  let now = $state(Date.now());

  function queueSale(target: SaleTarget, label: string, key: string) {
    if (pendingSales.has(key)) return;
    const timer = setTimeout(() => commitSale(key), UNDO_MS);
    pendingSales.set(key, { target, label, deadline: Date.now() + UNDO_MS, timer });
    now = Date.now();
  }

  function commitSale(key: string) {
    const pending = pendingSales.get(key);
    if (!pending) return;
    clearTimeout(pending.timer);
    pendingSales.delete(key);
    void act(pending.target, true, pending.label);
  }

  function undoSale(key: string) {
    const pending = pendingSales.get(key);
    if (!pending) return;
    clearTimeout(pending.timer);
    pendingSales.delete(key);
    pushLocalNotice(`Kept ${pending.label}.`, 'info');
  }

  function secondsLeft(key: string): number {
    const pending = pendingSales.get(key);
    return pending ? Math.max(0, Math.ceil((pending.deadline - now) / 1000)) : 0;
  }

  // The countdown's clock, ticking only while something is pending.
  $effect(() => {
    if (pendingSales.size === 0) return;
    const id = setInterval(() => (now = Date.now()), 250);
    return () => clearInterval(id);
  });

  onDestroy(() => {
    for (const key of [...pendingSales.keys()]) commitSale(key);
  });

  // ---------------------------------------------------------------------------
  // Auto-sell rules (task 81)
  // ---------------------------------------------------------------------------

  // Modul: THE RULES ARE THE AUTO-SALVAGE FLOOR, PER REGION. They moved here
  // from Settings, because this is where the junk they stop is seen. The server
  // takes the larger of the all-regions floor and a region's own
  // (ChestSalvageRules), so a region rule can only sell MORE there. That is
  // why its options at or below the all-regions floor are disabled. They would
  // be rules that do nothing.
  //
  // A rule runs as a piece DROPS, before it is a row, so nothing already in
  // the chest changes and a locked piece is never touched. "Clear out the
  // junk" is still the tool for what has piled up.
  const REGIONS = [1, 2, 3, 4, 5] as const;
  let rulesOpen = $state(false);
  let rulesSaving = $state(false);
  let draft = $state<{ global: number; regions: number[] } | null>(null);

  $effect(() => {
    const data = chestSettings.data;
    if (data && draft === null) {
      draft = { global: data.AutoSalvageBelowTier, regions: [...(data.AutoSalvageRegionTiers ?? [0, 0, 0, 0, 0])] };
    }
  });

  const rulesDirty = $derived(
    draft !== null &&
      chestSettings.data !== undefined &&
      (draft.global !== chestSettings.data.AutoSalvageBelowTier ||
        draft.regions.some((t, i) => t !== (chestSettings.data?.AutoSalvageRegionTiers?.[i] ?? 0))),
  );

  const rulesSummary = $derived.by(() => {
    const data = chestSettings.data;
    if (!data) return '';
    const regionRules = (data.AutoSalvageRegionTiers ?? []).filter((t) => t > data.AutoSalvageBelowTier).length;
    if (data.AutoSalvageBelowTier === 0 && regionRules === 0) return 'off - every drop is kept';
    const parts: string[] = [];
    if (data.AutoSalvageBelowTier > 0) parts.push(`${rarityName(data.AutoSalvageBelowTier)} and worse everywhere`);
    if (regionRules > 0) parts.push(`${regionRules} region ${regionRules === 1 ? 'rule' : 'rules'}`);
    return parts.join(', ');
  });

  async function saveRules() {
    if (!draft) return;
    rulesSaving = true;
    try {
      const saved = await saveChestSettings(draft.global, draft.regions);
      if (!saved) {
        pushLocalNotice('The rules did not save. Try again in a moment.', 'error');
        return;
      }
      client.setQueryData(queryKeys.chestSettings, saved);
      draft = { global: saved.AutoSalvageBelowTier, regions: [...saved.AutoSalvageRegionTiers] };
      pushLocalNotice('Auto-sell rules saved. They apply to the next drop.', 'info');
    } catch {
      pushLocalNotice('Could not reach the server.', 'error');
    } finally {
      rulesSaving = false;
    }
  }
</script>

{#snippet dots()}
  <svg viewBox="0 0 16 16" aria-hidden="true">
    <circle cx="3" cy="8" r="1.6" fill="currentColor" />
    <circle cx="8" cy="8" r="1.6" fill="currentColor" />
    <circle cx="13" cy="8" r="1.6" fill="currentColor" />
  </svg>
{/snippet}

<!-- One equipment row, shared by the Worn group and the VirtualList so the
     two cannot drift apart. `.row[data-equipment-id]` is what exercise.mjs
     addresses a piece by. -->
{#snippet equipmentRow(item: InventoryEquipment)}
  {@const saleKey = `eq:${item.Id}`}
  <div class="row" class:pending={pendingSales.has(saleKey)} data-equipment-id={item.Id}>
    <ItemRow
      baseItemId={item.BaseItemId}
      name={prettifyBaseId(item.BaseItemId)}
      qualityTier={item.QualityTier}
      affixes={item.Affixes}
      selected={$isWide && inspectedId === item.Id}
      onSelect={$isWide ? () => inspect(item.Id) : undefined}
    >
      {#snippet chips()}
        {#if item.IsEquipped}<span class="chip worn">Worn</span>{/if}
        {#if item.IsAffixLocked}
          <!-- Modul: a Hint, because what a lock protects against was
               only this badge's title - nothing on a phone. -->
          <Hint text="Locked - cannot be sold, binned, swept, rerolled or fused. Unlock it from the row's menu."
            ><span class="lockbadge">Locked</span></Hint
          >
        {/if}
      {/snippet}
      {#snippet actions()}
        <!-- Modul: ONE PRIMARY ACTION AND A MENU (task 81). A pending sale
             takes the group's place with its countdown and Undo, so nothing
             else can be pressed on a piece that is about to go. -->
        {#if pendingSales.has(saleKey)}
          <span class="dim tiny">Selling in {secondsLeft(saleKey)}s</span>
          <button class="tiny-btn" onclick={() => undoSale(saleKey)}>Undo</button>
        {:else}
          {#if item.IsEquipped}
            <button class="tiny-btn" onclick={() => unequip(item.BaseItemId)}>Unequip</button>
          {:else}
            <button class="tiny-btn" onclick={() => equip(item.Id)}>Equip</button>
          {/if}
          <button
            class="tiny-btn more"
            aria-label="More"
            aria-haspopup="menu"
            title="Inspect, reroll, lock, sell or bin"
            onclick={(e) => openMenu(e, item)}
          >
            {@render dots()}
          </button>
        {/if}
      {/snippet}
    </ItemRow>
  </div>
{/snippet}

<!-- Modul: THE DETAIL (task 99) - the pane beside the list on a wide screen,
     a block above the list below that. The affixes are the Forge's own
     Affixes component, so this and the reroll panel cannot describe one roll
     two ways. The actions are real buttons here because there is room.
     Deliberately no "Cancel" label in it: exercise.mjs finds the sweep's
     confirm by that name, page-wide. -->
{#snippet detail(item: InventoryEquipment)}
  {@const label = prettifyBaseId(item.BaseItemId)}
  {@const saleKey = `eq:${item.Id}`}
  {@const blocked = item.IsEquipped ? 'Worn - take it off first' : item.IsAffixLocked ? 'Locked - unlock it first' : ''}
  <div class="detail-body" data-inspected-id={item.Id}>
    <div class="detail-head">
      <ItemIcon baseItemId={item.BaseItemId} name={label} qualityTier={item.QualityTier} size="md" />
      <div class="detail-title">
        <strong style="color: {rarityColor(item.QualityTier)}" class:rarity-glow={shouldGlow(item.QualityTier)}>{label}</strong>
        <span class="dim tiny">
          {inspectedMeta(item)}
          {#if item.IsEquipped}<span class="chip worn">Worn</span>{/if}
          {#if item.IsAffixLocked}<span class="lockstate">Locked</span>{/if}
        </span>
      </div>
      <button class="tiny-btn close" aria-label="Close" title="Close" onclick={() => (inspectedId = null)}>×</button>
    </div>

    <Affixes affixes={item.Affixes} baseItemId={item.BaseItemId} qualityTier={item.QualityTier} />

    <!-- Modul: THE SERVER'S PRICE. VillageChestEngine.ValueEquipment rides on
         the inventory row as SellValueGold, so this is the number the sale
         pays - not a client copy of BaseValueGold x (1 + tier x 0.5) x 0.40. -->
    <p class="dim tiny">Sells for {formatGold(item.SellValueGold)} - 40% of its market value.</p>

    <div class="detail-actions">
      {#if pendingSales.has(saleKey)}
        <span class="dim tiny">Selling in {secondsLeft(saleKey)}s</span>
        <button class="tiny-btn" onclick={() => undoSale(saleKey)}>Undo</button>
      {:else}
        {#if item.IsEquipped}
          <button class="tiny-btn" onclick={() => unequip(item.BaseItemId)}>Take off</button>
        {:else}
          <button class="tiny-btn" onclick={() => equip(item.Id)}>Wear</button>
        {/if}
        <button class="tiny-btn" onclick={() => openRerollInForge(item.Id)}>Reroll in Forge</button>
        <button class="tiny-btn" disabled={busy} onclick={() => toggleLock(item.Id, label)}>
          {item.IsAffixLocked ? 'Unlock' : 'Lock'}
        </button>
        <button
          class="tiny-btn"
          disabled={busy || blocked !== ''}
          title={blocked}
          onclick={() => queueSale({ equipmentId: item.Id }, label, saleKey)}
        >
          Sell · {formatGold(item.SellValueGold)}
        </button>
        {#if confirming === `detail:${item.Id}`}
          <button
            class="tiny-btn danger"
            disabled={busy}
            onclick={() => {
              confirming = null;
              act({ equipmentId: item.Id }, false, label);
            }}
          >
            Really bin
          </button>
        {:else}
          <button
            class="tiny-btn"
            disabled={busy || item.IsEquipped}
            title={item.IsEquipped ? 'Worn - take it off first' : ''}
            onclick={() => (confirming = `detail:${item.Id}`)}
          >
            Bin
          </button>
        {/if}
      {/if}
    </div>
    {#if blocked !== ''}<p class="dim tiny">{blocked}.</p>{/if}
  </div>
{/snippet}

<div class="wrap" class:split={$isWide}>
  <section class="panel">
    <header class="head">
      <h2>Village chest</h2>
      <span class="dim tiny">
        Unlimited. Crafting, the forge and the market all draw from here.
      </span>
    </header>

    <div class="filters" role="group" aria-label="Filter">
      {#each [['all', 'All', equipment.length + materials.length], ['equipment', 'Armour & tools', counts.equipment - counts.weapons], ['weapons', 'Weapons', counts.weapons], ['materials', 'Materials', counts.materials], ['food', 'Food', counts.food]] as [key, label, count]}
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
        <svg class="caret" class:right={!sweepOpen} viewBox="0 0 12 12" aria-hidden="true">
          <path d="M2 4.5 L6 8.5 L10 4.5" fill="none" stroke="currentColor" stroke-width="1.8"
                stroke-linecap="round" stroke-linejoin="round" />
        </svg>
        Clear out the junk
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
            {formatNumber(sweepCount)}
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
            {formatNumber(sweepCount)}
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
          menu for those. An auto-sell rule below stops the junk arriving in the
          first place.
        </p>
      </div>
      {/if}
    </section>

    <section class="rules">
      <button
        class="sweeptoggle"
        aria-expanded={rulesOpen}
        onclick={() => (rulesOpen = !rulesOpen)}
      >
        <svg class="caret" class:right={!rulesOpen} viewBox="0 0 12 12" aria-hidden="true">
          <path d="M2 4.5 L6 8.5 L10 4.5" fill="none" stroke="currentColor" stroke-width="1.8"
                stroke-linecap="round" stroke-linejoin="round" />
        </svg>
        Auto-sell rules
        {#if rulesSummary}<span class="dim">- {rulesSummary}</span>{/if}
      </button>

      {#if rulesOpen && draft}
      <div class="sweepbody">
        <p class="dim tiny">
          A drop at or below the rarity you pick is sold the moment it lands, for
          the same gold this chest pays. Pieces already here are not touched,
          and locked pieces never are.
        </p>
        <div class="rulegrid">
          <label>
            <span>All regions</span>
            <select bind:value={draft.global} disabled={rulesSaving} aria-label="Auto-sell in every region up to">
              <option value={0}>Off - keep everything</option>
              {#each Array(maxSweepTier) as _, i}
                <option value={i + 1}>{rarityName(i + 1)} and worse</option>
              {/each}
            </select>
          </label>
          {#each REGIONS as region, index (region)}
            <label>
              <span>Region {region}</span>
              <select bind:value={draft.regions[index]} disabled={rulesSaving} aria-label={`Auto-sell in region ${region} up to`}>
                <option value={0}>Same as all</option>
                {#each Array(maxSweepTier) as _, i}
                  <option value={i + 1} disabled={i + 1 <= draft.global}>{rarityName(i + 1)} and worse</option>
                {/each}
              </select>
            </label>
          {/each}
        </div>
        <div class="sweepbtns">
          <button disabled={!rulesDirty || rulesSaving} onclick={saveRules}>
            {rulesSaving ? 'Saving...' : 'Save rules'}
          </button>
        </div>
        <p class="dim tiny">
          A region rule can only sell more there, never less. Legendary and above
          is never sold automatically.
        </p>
      </div>
      {:else if rulesOpen && chestSettings.isError}
        <QueryError query={chestSettings} what="your auto-sell rules" />
      {/if}
    </section>

    {#if inventory.isPending}
      <Skeleton rows={5} variant="row" />
    {:else if inventory.isError && inventory.data === undefined}
      <!-- Modul: an error is not an empty chest. This fell through to
           "Nothing here." - read by players who lived through the 17,836-row
           incident as "my items are gone". See QueryError. -->
      <QueryError query={inventory} what="your chest" />
    {:else if sortedEquipment.length === 0 && visibleMaterials.length === 0}
      <p class="dim">Nothing here.</p>
    {:else}
      {#if inventory.isError}
        <QueryError query={inventory} what="your chest" stale />
      {/if}
      {#if !$isWide && inspected}
        <div class="inline-detail" bind:this={inlineDetail}>
          {@render detail(inspected)}
        </div>
      {/if}

      {#if groups.worn.length > 0}
        <h3>
          Worn ({formatNumber(groups.worn.length)})
        </h3>
        <!-- Bounded by the roster's slots, so a plain list. Each item keeps
             the VirtualList's row height so the two groups line up. -->
        <ul class="plain" aria-label="Worn pieces">
          {#each groups.worn as item (item.Id)}
            <li style="height: {equipmentRowHeight}px">{@render equipmentRow(item)}</li>
          {/each}
        </ul>
      {/if}

      {#if groups.loose.length > 0}
        <h3>
          {looseTitle}
          <span class="dim tiny">
            {formatNumber(groups.loose.length)} shown
          </span>
        </h3>

        <!-- Modul: WINDOWED. This was a plain {#each} over every piece the
             player owns, inside a box 26rem tall - 17,836 rows on the
             worst-affected account, each one an icon and six buttons, roughly
             180,000 DOM nodes to display about twenty. See ui/VirtualList. -->
        <VirtualList items={groups.loose} rowHeight={equipmentRowHeight} label="Equipment in the chest">
          {#snippet row(item: InventoryEquipment)}
            {@render equipmentRow(item)}
          {/snippet}
        </VirtualList>
      {/if}

      {#if visibleMaterials.length > 0}
        <h3>Materials</h3>
        <!-- Modul: IN THE PAGE, NOT IN A BOX (task 99). This list sat in a
             26rem scroller inside the scrolling page, cut mid-row. It is one
             row per item id - bounded by the catalogue, 63 rows on the live
             database - so it needs no window and no scroller of its own. -->
        <ul class="plain materials">
          {#each visibleMaterials as stack (stack.ItemId)}
            {@const saleKey = `mat:${stack.ItemId}`}
            {@const label = prettifyBaseId(stack.ItemId)}
            <li class:pending={pendingSales.has(saleKey)}>
              <ItemRow
                baseItemId={stack.ItemId}
                name={label}
                quantity={stack.Quantity}
                extra={isFood(stack.ItemId) || consumableKind(stack.ItemId) !== null ? 'Food' : 'Material'}
              >
                {#snippet actions()}
                  {#if pendingSales.has(saleKey)}
                    <span class="dim tiny">Selling in {secondsLeft(saleKey)}s</span>
                    <button class="tiny-btn" onclick={() => undoSale(saleKey)}>Undo</button>
                  {:else}
                    <button
                      class="tiny-btn"
                      disabled={busy}
                      onclick={() => queueSale({ itemId: stack.ItemId, quantity: stack.Quantity }, label, saleKey)}
                    >
                      Sell all · {formatGold(stack.UnitSellValueGold * stack.Quantity)}
                    </button>
                    <button
                      class="tiny-btn more"
                      aria-label="More"
                      aria-haspopup="menu"
                      title="Bin"
                      onclick={(e) => openMaterialMenu(e, stack)}
                    >
                      {@render dots()}
                    </button>
                  {/if}
                {/snippet}
              </ItemRow>
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

  <!-- Modul: LIST | DETAIL on a wide screen (task 99). The card used to stop
       at about 880px of a 1440px window with the rest empty; the pane uses it
       for the one thing a row cannot hold, every affix on the piece. -->
  {#if $isWide}
    <aside class="panel detail" aria-label="Selected piece">
      {#if inspected}
        {@render detail(inspected)}
      {:else}
        <p class="dim tiny">Pick a piece to see every affix on it and what you can do with it.</p>
      {/if}
    </aside>
  {/if}
</div>

{#if menu}
  <ContextMenu
    x={menu.x}
    y={menu.y}
    title={menuTitle}
    items={menuItems}
    onClose={() => { menu = null; confirming = null; }}
  />
{/if}

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

  @media (max-width: 40rem) {
    .finders {
      grid-template-columns: 1fr;
    }
  }

  .wrap {
    padding: 1rem;
    max-width: 56rem;
  }

  /* Wide: the list keeps its old width and the detail pane takes the room
     that used to sit empty to the right of it. The pane sticks so a piece
     picked far down the list still has its detail in view. */
  .wrap.split {
    max-width: 88rem;
    display: grid;
    grid-template-columns: minmax(0, 56rem) minmax(18rem, 26rem);
    gap: 1rem;
    align-items: start;
  }

  .detail {
    position: sticky;
    top: 1rem;
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

  /* Modul: THE MATERIALS AND THE WORN GROUP FLOW IN THE PAGE (task 99). The
     materials sat in a 26rem scroller here, cut mid-row inside a page that
     scrolls anyway. Both lists are bounded - materials by the catalogue (63
     rows live), worn pieces by the roster's slots - so neither needs a box of
     its own. The loose equipment is the unbounded one, and it is a
     VirtualList. */
  .plain {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 4px;
  }

  /* Modul: the virtual list positions rows by arithmetic, so this has to be
     exactly the rowHeight passed to it. ItemRow fills it with `height: 100%`
     and keeps both of its lines to one line each. */
  .row {
    height: 100%;
  }

  /* Modul: a locked piece has to READ as locked at a glance, or the player
     has to open each menu to find out - the opposite of what a lock is for
     when there are thousands of rows. */
  .lockbadge,
  .lockstate {
    color: var(--warn);
    font-weight: 600;
  }

  /* Worn is a fact about the piece, not an action, so it is a chip rather
     than the Equip/Unequip label being the only difference. */
  .chip {
    display: inline-block;
    padding: 0 0.3rem;
    border: 1px solid currentColor;
    border-radius: var(--radius-pill);
    font-size: 0.68rem;
    line-height: 1.25;
  }

  .chip.worn {
    color: var(--good);
  }

  .more svg {
    width: 14px;
    height: 14px;
    display: block;
  }

  .row.pending,
  .materials li.pending {
    opacity: 0.6;
  }

  /* The detail, in the side pane or inline above the list. */
  .inline-detail {
    margin: 0.6rem 0;
    padding: 0.6rem;
    background: var(--bg-raised);
    border: 1px solid var(--accent);
    border-radius: var(--radius);
  }

  .detail-head {
    display: flex;
    align-items: flex-start;
    gap: 0.6rem;
  }

  .detail-title {
    flex: 1 1 auto;
    min-width: 0;
    display: grid;
    gap: 0.15rem;
  }

  .detail-title strong {
    overflow-wrap: anywhere;
  }

  .detail-title .chip,
  .detail-title .lockstate {
    margin-left: 0.3rem;
  }

  .close {
    flex-shrink: 0;
  }

  .detail-actions {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
    margin-top: 0.5rem;
  }

  .detail-body p {
    margin: 0.5rem 0 0;
  }

  .rulegrid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(12rem, 1fr));
    gap: 0.4rem 0.8rem;
    margin: 0.5rem 0;
    font-size: 0.82rem;
  }

  .rulegrid label {
    display: grid;
    grid-template-columns: 5.5rem 1fr;
    align-items: center;
    gap: 0.35rem;
  }

  .rulegrid select {
    min-width: 0;
  }

  .sweep,
  .rules {
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

  /* Modul: a caret drawn rather than typed. A glyph is a font's opinion about
     a shape - it differs by family, is not guaranteed to be present, and a
     screen reader announces it as "black down-pointing small triangle" in the
     middle of a label. `rotate` rather than `transform`, for the reason
     app.css records on button:active: transform is one property holding a
     whole list, so setting it here would replace whatever else used it. */
  .caret {
    width: 11px;
    height: 11px;
    flex: none;
    transition: rotate 140ms ease;
  }

  .caret.right {
    rotate: -90deg;
  }
</style>
