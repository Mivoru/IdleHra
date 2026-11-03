<script lang="ts">
  import { formatNumber, formatGold } from '../lib/ui/format';
  import Money from '../lib/ui/Money.svelte';
  import { play } from '../lib/ui/audio';
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { invalidateOwnedItems } from '../lib/net/queryClient';
  import { queryKeys, fetchForge, fetchForgeStackPreview, fetchBreedingRoster, type ForgeEquipment } from '../lib/net/rest';
  import { workersOf, workerName } from '../lib/ui/workers';
  import { raceName } from '../lib/ui/races';
  import { EQUIPMENT_SLOTS } from '../lib/ui/slots';
  import { summarizeAffixes } from '../lib/ui/itemRow';
  import { prettifyBaseId } from '../lib/net/content';
  import { executeForgeFusion, fuseStack, rerollAffix, refineCost, REFINE_OPERATION, MAX_FORGE_LEVEL } from '../lib/net/commands';
  import Burst from '../lib/ui/Burst.svelte';
  import ConfirmButton from '../lib/ui/ConfirmButton.svelte';
  import DisabledReason from '../lib/ui/DisabledReason.svelte';
  import { commandInFlight } from '../lib/ui/commandInFlight';
  import { pushLocalNotice, playerState } from '../lib/stores/game';
  import ItemBrowser from '../lib/ui/ItemBrowser.svelte';
  import ItemIcon from '../lib/ui/ItemIcon.svelte';
  import RarityPip from '../lib/ui/RarityPip.svelte';
  import {
    buildFusionRows,
    fusionFeeCeiling,
    hiddenByPending,
    settlePending,
    PENDING_FUSION_TTL_MS,
    type FusionRow,
    type PendingFusion,
  } from '../lib/ui/fusionRows';

  import { rarityColor, rarityName, shouldGlow, MAX_QUALITY_TIER } from '../lib/ui/rarity';
  import {
    toDisplayAffixes,
    AFFIX_RARITY_NAMES,
    KNOWN_AFFIX_IDS,
    RETIRED_AFFIX_IDS,
    affixLabel,
    describeStopCondition,
  } from '../lib/ui/affixes';
  import Affixes from '../lib/ui/Affixes.svelte';
  import QueryError from '../lib/ui/QueryError.svelte';

  import { takePendingFocusEquipment } from '../lib/stores/navigation';
  import { questSpotlight } from '../lib/stores/questSpotlight';
  import { commandResults } from '../lib/stores/game';


  const client = useQueryClient();
  const forge = createQuery(() => ({ queryKey: queryKeys.forge, queryFn: fetchForge }));

  const snap = $derived($playerState);
  const forgeLevel = $derived(snap?.ForgeLevel ?? 0);
  const owned = $derived(forge.data?.OwnedEquipment ?? []);

  // Modul: REFRESH WHEN THE SERVER ANSWERS, not 800ms after we asked.
  //
  // A fixed timer is a guess about how long a Serializable transaction and a
  // state reload take, and it is wrong in both directions: too early on a
  // loaded server, so the screen re-reads the OLD rows and looks unchanged,
  // and needlessly late otherwise. Reported as "I have to press F5 to see it
  // and the gold does not come off straight away".
  //
  // The command-result feed is the server saying "I am done with that", which
  // is the actual event worth reacting to. The timer stays as a backstop for
  // paths that report nothing.
  function refresh() {
    invalidate();
    setTimeout(invalidate, 800);
  }

  function invalidate() {
    client.invalidateQueries({ queryKey: queryKeys.forge });
    invalidateOwnedItems(client);
  }

  let lastSeenResultId = 0;
  $effect(() => {
    const latest = $commandResults[0];
    if (latest && latest.id !== lastSeenResultId) {
      lastSeenResultId = latest.id;
      invalidate();
    }
  });

  function label(item: ForgeEquipment): string {
    return `${prettifyBaseId(item.BaseItemId)} [${rarityName(item.QualityTier)}] #${item.Id}`;
  }

  // --- fusion ---------------------------------------------------------------
  let fusionTarget = $state(0);
  let fusionSacOne = $state(0);
  let fusionSacTwo = $state(0);

  const fusionTargetItem = $derived(owned.find((i) => i.Id === fusionTarget) ?? null);
  // Modul: ONE CEILING, and it is the top of the rarity ladder.
  //
  // This mirrored a per-gear-band cap - region 1-2 gear stopping at rarity 5 -
  // which was the likeliest cause of fusion appearing broken on ordinary
  // starter gear. That rule is gone server-side: fusion already costs three
  // identical pieces at the same rarity, and a second invisible ceiling on top
  // of that only stopped people using the gear they had.
  //
  // 14, not 13. The server's old constant read the fourteen tiers as "0-13"
  // while every item in the game is 1-based, which quietly made Transcendent
  // the one rarity that exists and cannot be reached.
  //
  // Modul: BUT THE FORGE STOPS AT 12. A fusion needs Forge level >= the rarity it
  // produces, and the Forge tops out at 12 (Town Hall 5 -> ceiling 2 + 5*2), so
  // Godly (13) and Transcendent (14) are unreachable by fusion - they were
  // offered, with a "needs Forge 13" no player can ever meet. Every fusion
  // target above FUSION_CEILING is hidden here.
  const FUSION_CEILING = Math.min(MAX_QUALITY_TIER, MAX_FORGE_LEVEL);
  const atMaxTier = $derived((fusionTargetItem?.QualityTier ?? 0) >= FUSION_CEILING);

  // Modul: fusion now takes THREE IDENTICAL items of the SAME RARITY. Once a
  // target is picked, the only legal partners are its exact twins, so the two
  // other dropdowns offer nothing else - a mismatch is not something the
  // player should be able to select and then be told about.
  //
  // ValidateFusionCommand also DISCONNECTS if any two of the three ids match,
  // so each list still excludes what the others already hold.
  const twins = $derived(
    fusionTargetItem
      ? owned.filter(
          (i) =>
            i.Id !== fusionTarget &&
            i.BaseItemId === fusionTargetItem.BaseItemId &&
            i.QualityTier === fusionTargetItem.QualityTier,
        )
      : [],
  );

  // Sets the player can actually fuse right now: three or more of the same
  // item at the same rarity. Without this the screen is a puzzle - three
  // dropdowns and no way to tell whether any legal combination exists.
  //
  // Modul: DECLARED BEFORE targetChoices, and that ordering is deliberate -
  // the dropdown below is built from this, so moving it back down would leave
  // a $derived reading a const declared after it.
  const fusableSets = $derived.by(() => {
    const groups = new Map<string, { base: string; tier: number; count: number }>();
    for (const item of owned) {
      if (item.QualityTier >= FUSION_CEILING) continue;
      const key = `${item.BaseItemId}#${item.QualityTier}`;
      const seen = groups.get(key) ?? { base: item.BaseItemId, tier: item.QualityTier, count: 0 };
      seen.count++;
      groups.set(key, seen);
    }
    return [...groups.values()]
      .filter((g) => g.count >= 3)
      .sort((a, b) => b.tier - a.tier || a.base.localeCompare(b.base));
  });

  // Modul: ONLY ITEMS THAT CAN ACTUALLY BE FUSED, which is both the fix for
  // the lag and the fix for a dropdown that was lying.
  //
  // This was `owned.filter(...)` - EVERY owned item, as an <option>. A live
  // account holds 12,791 of them, and this screen has three selects, so the
  // Forge built tens of thousands of DOM nodes and rebuilt them whenever the
  // state changed. Reported from a phone as "forge is lagging a lot because of
  // many items"; that is exactly what it was. Same rule the Chest already
  // learned the hard way - a list of owned items must be windowed - except a
  // <select> cannot be windowed, so the list has to be SHORTER instead.
  //
  // And it can be, because fusion needs THREE of the same item at the same
  // rarity. `fusableSets` already computes precisely which (base, tier) groups
  // qualify, and the panel above already shows the player that list. Any other
  // item in this dropdown was an option that could never complete: the screen
  // offered twelve thousand choices of which a handful worked, and answered
  // the rest with "You only have 1 of this item".
  //
  // So the long list was not merely slow - it was why the screen read as a
  // puzzle.
  const fusableKeys = $derived(new Set(fusableSets.map((g) => `${g.base}#${g.tier}`)));
  const targetChoices = $derived(
    owned.filter(
      (i) =>
        i.Id !== fusionSacOne &&
        i.Id !== fusionSacTwo &&
        fusableKeys.has(`${i.BaseItemId}#${i.QualityTier}`),
    ),
  );
  const sacOneChoices = $derived(twins.filter((i) => i.Id !== fusionSacTwo));
  const sacTwoChoices = $derived(twins.filter((i) => i.Id !== fusionSacOne));

  // ForgeSplicingEngine.FusionFee, mirrored in fusionRows.ts (pinned by
  // serverMirrors.test.ts). Luck and the Diamond Star event take up to 25% off
  // server-side, so this is the ceiling rather than the exact charge - stated
  // in the UI as such rather than quietly presented as final.
  const fusionFee = $derived(fusionTargetItem ? fusionFeeCeiling(fusionTargetItem.QualityTier) : 0);
  const gold = $derived(Number($playerState?.Gold ?? 0));

  // Modul: SIX THINGS GREY THE FUSE BUTTON and only the gold one was said
  // anywhere near it, so "no Forge yet" looked like a broken button. The
  // first unmet one is printed under it, in the order a player meets them.
  const fuseBlocked = $derived.by((): string | null => {
    if (forgeLevel === 0) return 'Build a Forge in your village first.';
    if (fusionTarget === 0) return 'Choose the item to upgrade.';
    if (fusionSacOne === 0 || fusionSacTwo === 0) return 'Choose two matching items to fuse into it.';
    if (atMaxTier) return 'That item is already at the highest rarity the Forge can make.';
    if (gold < fusionFee) return `Not enough gold - the fee is up to ${formatGold(fusionFee)}.`;
    return null;
  });

  // Modul: THE REROLL PRICE, SHOWN. Mirrors
  // AffixRegistry.CalculateRerollGoldCost - a flat per-REGION table, see
  // getRerollCost below. This comment used to describe a `100 * 1.35^(itemTier
  // - 1)` curve the server had already dropped, which is how the mirror test
  // ended up guarding constants nobody used.
  //
  // Reported from play alongside the price itself being too high: "it does not
  // even say what it costs". It did not. A player pressed a button, gold left,
  // and the only way to learn the rate was to watch the balance - which is how
  // someone spends a night's income on five rolls without noticing until it is
  // gone. A charge you cannot see before you agree to it is not a price.


  function fuse() {
    const one = owned.find((i) => i.Id === fusionSacOne) ?? null;
    const two = owned.find((i) => i.Id === fusionSacTwo) ?? null;
    const match =
      fusionTargetItem && one && two
        ? {
            sameBase:
              one.BaseItemId === fusionTargetItem.BaseItemId &&
              two.BaseItemId === fusionTargetItem.BaseItemId,
            sameRarity:
              one.QualityTier === fusionTargetItem.QualityTier &&
              two.QualityTier === fusionTargetItem.QualityTier,
            // What the fusion would PRODUCE - the Forge's level has to reach it.
            resultTier: fusionTargetItem.QualityTier + 1,
          }
        : undefined;

    const outcome = commandInFlight.run(`fuse:${fusionTarget}`, () =>
      executeForgeFusion(fusionTarget, fusionSacOne, fusionSacTwo, forgeLevel, match),
    );
    if (outcome === null) return;
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    claim(fusionTarget, [fusionSacOne, fusionSacTwo]);
    fusionSacOne = 0;
    fusionSacTwo = 0;
    fusionFlash++;
    play('forgeReroll');
    refresh();
  }

  // --- whole stack (task 69) -------------------------------------------------
  //
  // Fusion is deterministic 3:1, so a pile of identical pieces holds one
  // decision: how far up. The dev fixture carries 7,550 Normal Birch Axes -
  // about 2,500 presses of the single fusion above for no choice at all. The
  // plan and its price come from the server (the same planner the fusion
  // runs), never from a copy of the fee curve here.
  const stackMaxReach = $derived(Math.min(forgeLevel, FUSION_CEILING));
  const stackTiers = $derived(
    fusionTargetItem
      ? Array.from(
          { length: Math.max(0, stackMaxReach - fusionTargetItem.QualityTier) },
          (_, i) => fusionTargetItem.QualityTier + 1 + i,
        )
      : [],
  );
  let stackTo = $state(0);
  let stackFor = 0;
  $effect(() => {
    const id = fusionTargetItem?.Id ?? 0;
    if (id !== stackFor) {
      stackFor = id;
      stackTo = stackTiers.length > 0 ? stackTiers[stackTiers.length - 1] : 0;
    }
  });

  const stackPreview = createQuery(() => ({
    queryKey: ['forge', 'stack-preview', fusionTarget, stackTo] as const,
    queryFn: () => fetchForgeStackPreview(fusionTarget, stackTo),
    enabled: fusionTarget > 0 && stackTo > 0,
  }));

  function fuseWholeStack() {
    if (!fusionTargetItem) return;
    const outcome = fuseStack(fusionTargetItem.Id, fusionTargetItem.QualityTier, stackTo, forgeLevel);
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    fusionTarget = 0;
    fusionSacOne = 0;
    fusionSacTwo = 0;
    fusionFlash++;
    play('forgeReroll');
    refresh();
  }

  // Modul: WHAT YOU ARE WEARING, first.
  //
  // Reported from play: "I would rather it showed only the items the
  // characters have equipped, and let me click them, than a Choose dropdown."
  // That is right - rerolling is something you do to the gear you are
  // fighting in, and the dropdown made you find it by name among everything
  // you own.
  //
  // Tools are the exception and the reason "everything" is still reachable:
  // the wire carries a tool's TIER but not its instance id, so an equipped
  // axe cannot be identified here. Hiding the full list would make tools
  // unrerollable, which is a worse answer than one extra toggle.
  const equippedIds = $derived.by(() => {
    if (!snap) return new Set<number>();
    return new Set<number>(
      [
        snap.EquippedWeaponId,
        snap.EquippedHelmetId,
        snap.EquippedChestId,
        snap.EquippedGlovesId,
        snap.EquippedLeggingsId,
        snap.EquippedBootsId,
        snap.EquippedAmuletId,
        snap.EquippedRingId,
      ]
        .map(Number)
        .filter((id) => id > 0),
    );
  });

  let showAllForReroll = $state(false);
  const rerollChoices = $derived(
    showAllForReroll ? owned : owned.filter((i: { Id: number }) => equippedIds.has(Number(i.Id))),
  );

  // Modul: ONE COLUMN PER CHARACTER (owner, 2026-11-03). "In the Forge I only
  // ever see the gear on one character; with several I want to reroll theirs
  // without hunting through everything, and see which piece is on whom." The
  // server now says who wears a piece and in which slot
  // (EquippedByCharacterSlot / EquippedInSlotIndex), so worn gear - tools
  // included, which the wire cannot name - is grouped by character and
  // ordered like the paper doll. "Show all" still lists everything.
  const roster = createQuery(() => ({ queryKey: queryKeys.breedingRoster, queryFn: fetchBreedingRoster, staleTime: 60_000 }));
  const nameById = $derived(new Map((roster.data ?? []).map((c) => [c.CharacterId, c.Name])));
  const wornByCharacter = $derived.by(() => {
    const groups = workersOf(snap).map((worker) => {
      const name = workerName(worker, nameById);
      const race = worker.raceId > 0 ? raceName(worker.raceId) : '';
      return {
        slot: worker.slot,
        title: race && race !== name ? `${name} (${race})` : name,
        items: owned
          .filter((i) => (i.EquippedByCharacterSlot ?? -1) === worker.slot - 1)
          .sort((a, b) => (a.EquippedInSlotIndex ?? 99) - (b.EquippedInSlotIndex ?? 99)),
      };
    });
    return groups;
  });
  const slotNameOf = (index: number | undefined) => EQUIPMENT_SLOTS.find((s) => s.index === index)?.label ?? '';

  // --- one row per item (task 100) ------------------------------------------
  //
  // Fusions sent but not yet visible in the list. See fusionRows.ts: without
  // this, a quick second Fuse re-sent pieces the first one had destroyed.
  let pending = $state<PendingFusion[]>([]);
  let clock = $state(Date.now());

  // Settle against every fresh list, and wake once when the oldest entry
  // expires so a refused fusion's pieces come back without another refetch
  // (`clock` feeds hiddenByPending; an expired entry hides nothing).
  $effect(() => {
    const ids = new Set(owned.map((i) => i.Id));
    const now = Date.now();
    const kept = settlePending(pending, ids, now);
    if (kept.length !== pending.length) pending = kept;
  });
  $effect(() => {
    if (pending.length === 0) return;
    const soonest = Math.min(...pending.map((p) => p.expiresAt));
    const handle = setTimeout(() => (clock = Date.now()), Math.max(0, soonest - Date.now()) + 20);
    return () => clearTimeout(handle);
  });
  const hiddenIds = $derived(hiddenByPending(pending, clock));

  function claim(target: number, sacrifices: number[]) {
    clock = Date.now();
    pending = [...pending, { target, sacrifices, expiresAt: clock + PENDING_FUSION_TTL_MS }];
  }

  let rowSearch = $state('');
  let showAllRows = $state(false);
  const ROWS_SHOWN = 6;

  // Worn by anyone: the active character's gear from the wire (instant after
  // an equip), plus the server's IsEquipped for characters 2 and 3 and tools,
  // which the wire does not name.
  const wornAnywhere = $derived(
    new Set<number>([...equippedIds, ...owned.filter((i) => i.IsEquipped).map((i) => i.Id)]),
  );
  const allRows = $derived(buildFusionRows(owned, wornAnywhere, hiddenIds, FUSION_CEILING));
  const matchedRows = $derived.by(() => {
    const needle = rowSearch.trim().toLowerCase();
    if (!needle) return allRows;
    return allRows.filter((r) => prettifyBaseId(r.baseItemId).toLowerCase().includes(needle));
  });
  const visibleRows = $derived(showAllRows || rowSearch.trim() !== '' ? matchedRows : matchedRows.slice(0, ROWS_SHOWN));

  const rowKey = (row: FusionRow) => `fuse:${row.baseItemId}`;

  function rowBlocked(row: FusionRow): string | null {
    if (forgeLevel === 0) return 'Build a Forge first';
    if (!row.next) return null;
    if (row.next.tier + 1 > forgeLevel) return `Forge level ${row.next.tier + 1} needed`;
    if (gold < fusionFeeCeiling(row.next.tier)) return 'Not enough gold';
    return null;
  }

  function fuseRow(row: FusionRow) {
    const next = row.next;
    if (!next) return;
    const outcome = commandInFlight.run(rowKey(row), () =>
      executeForgeFusion(next.target, next.sacrifices[0], next.sacrifices[1], forgeLevel, {
        sameBase: true,
        sameRarity: true,
        resultTier: next.tier + 1,
      }),
    );
    if (outcome === null) return;
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    claim(next.target, [...next.sacrifices]);
    fusionFlash++;
    play('forgeReroll');
    refresh();
  }

  let stackPanel: HTMLElement | null = $state(null);

  // The whole stack is still the section below the rows (it needs a tier
  // picker and the server's plan); a row's button points it at that row's
  // lowest fusable rarity and brings it into view, rather than filling
  // controls the player cannot see - which is what made the old chips look
  // inert on a phone.
  function openStack(row: FusionRow) {
    if (!row.stack) return;
    manualOpen = false;
    fusionTarget = row.stack.sampleId;
    fusionSacOne = 0;
    fusionSacTwo = 0;
    requestAnimationFrame(() => stackPanel?.scrollIntoView({ block: 'nearest', behavior: 'smooth' }));
  }

  // The three-select form, for a player who wants to choose WHICH pieces.
  let manualOpen = $state(false);

  // --- reroll ---------------------------------------------------------------
  let rerollItemId = $state(0);
  let rerollAffixIndex = $state(0);

  // Modul: A REROLL IS IRREVERSIBLE, so a good affix deserves a question.
  //
  // Asked for after an auto-reroll ate an Epic: "it should ask whether you
  // really want to reroll a Legendary, and let me set the same for Epic so it
  // does not run one over."
  //
  // It matters most for AUTO-reroll, which is the one that destroys a good
  // affix without a second press - it keeps rolling the same slot until a stop
  // condition is met, and the affix sitting there when it starts is gone on
  // the first attempt.
  //
  // Threshold rather than a fixed rule, stored locally: this is a safety rail
  // for one person's habits, not a game rule the server has any business
  // knowing about.
  const GUARD_OFF = 99;
  const GUARD_STORAGE_KEY = 'folkidle.rerollGuardRarity';

  let guardRarity = $state(readGuard());

  function readGuard(): number {
    try {
      const stored = Number(localStorage.getItem(GUARD_STORAGE_KEY));
      // 4 = Epic, 5 = Legendary, 99 = never ask. Legendary by default: the
      // rarity a player is least likely to want to gamble away by accident.
      return stored === 4 || stored === 5 || stored === GUARD_OFF ? stored : 5;
    } catch {
      return 5;
    }
  }

  $effect(() => {
    try {
      localStorage.setItem(GUARD_STORAGE_KEY, String(guardRarity));
    } catch {
      // A browser refusing storage is not a reason to break the forge.
    }
  });
  let rerollOperation = $state(0);
  let autoReroll = $state(false);
  let autoOpen = $state(false);
  let autoAttempts = $state(10);
  let stopMinRarity = $state(4);
  let stopAffixIndex = $state(0);

  // Modul: arriving from the Chest's "Reroll" button with the piece already
  // chosen. Consumed once - a player who opens the Forge by hand later should
  // get an empty selector, not whatever they last clicked in the Chest.
  //
  // Waits for `owned` to arrive: the inventory is fetched, so on a cold
  // navigation this effect runs before there is a list to select from.
  $effect(() => {
    if (owned.length === 0) return;

    const pending = takePendingFocusEquipment();
    if (pending > 0 && owned.some((i) => i.Id === pending)) {
      rerollItemId = pending;
    }
  });

  // Modul: THE PRICE IS THE SERVER'S (2026-11-03). This screen kept its own
  // copy of the region table; the price now also follows the item's rarity
  // (owner: rarity x region), and the inventory says what the engine will
  // charge for each piece - AffixRerollEngine.GoldCostFor - so there is
  // nothing here to drift.
  const rerollItem = $derived(owned.find((i) => i.Id === rerollItemId) ?? null);

  // On a phone the card sits under the whole picker, so a tap on a piece
  // changed nothing visible; bring the card into view when the layout is
  // stacked (the same 760px break as .affixlayout).
  let cardCol: HTMLElement | null = $state(null);
  function pickItem(id: number) {
    rerollItemId = id;
    rerollAffixIndex = 0;
    if (typeof window !== 'undefined' && window.matchMedia('(max-width: 760px)').matches) {
      requestAnimationFrame(() => cardCol?.scrollIntoView({ block: 'start', behavior: 'smooth' }));
    }
  }
  const rerollFee = $derived(rerollItem?.RerollGoldCost ?? 0);
  const refineFee = $derived(rerollItem?.RefineGoldCost ?? refineCost(rerollFee));

  // Modul: TWO TABS, NOT TWO PANELS (owner, 2026-11-03: "the Forge is
  // confusing"). Affixes and Fusion were side by side, each a long column; a
  // tab shows one job at a time. The choice is remembered per browser, and a
  // quest step that lights a control on the other tab switches to it - the
  // spotlight cannot ring a button that is not drawn.
  type ForgeTab = 'affixes' | 'fusion';
  const TAB_STORAGE_KEY = 'folkidle.forgeTab';
  function readTab(): ForgeTab {
    try {
      return localStorage.getItem(TAB_STORAGE_KEY) === 'fusion' ? 'fusion' : 'affixes';
    } catch {
      return 'affixes';
    }
  }
  let tab = $state<ForgeTab>(readTab());
  $effect(() => {
    try {
      localStorage.setItem(TAB_STORAGE_KEY, tab);
    } catch {
      // A browser refusing storage only forgets the tab.
    }
  });
  $effect(() => {
    const targets = $questSpotlight?.targets ?? [];
    if (targets.some((t) => t.startsWith('forge-fuse'))) tab = 'fusion';
    else if (targets.some((t) => t.startsWith('forge-reroll'))) tab = 'affixes';
  });
  const rerollAffixRows = $derived(rerollItem ? toDisplayAffixes(rerollItem.Affixes) : []);

  // Modul: THE FORGE GAVE NO SIGN IT HAD DONE ANYTHING.
  //
  // Both halves of this screen are the entire gear progression, and pressing
  // either button produced a silent list refresh a moment later. A reroll that
  // came back with a worse affix and a reroll that was rejected outright looked
  // identical: nothing moved.
  //
  // The counters key the flourish so it replays on every press - without a key
  // Svelte reuses the node and a CSS animation runs exactly once, ever, which
  // is the same trap the hit spark and the achievement toast both document.
  //
  // Fired on ACCEPTANCE rather than on the server's answer, matching how the
  // rest of this screen already behaves: the round trip is short, and a refusal
  // arrives as a toast.
  let fusionFlash = $state(0);
  let rerollFlash = $state(0);

  // Modul: THE GUARD ASKS INLINE. It was a native confirm(), which the
  // Android WebView draws as an unstyled system dialog; now a guarded affix
  // turns the button into a two-tap ConfirmButton and says, before the first
  // tap, what is at stake. The affix ABOUT TO BE DESTROYED, not the one that
  // will replace it.
  function guardFor(index: number) {
    const current = rerollAffixRows[index];
    if (!current || current.rarity < guardRarity) return null;
    return {
      what: `${current.rarityName} ${current.label} ${current.value}`,
      scope: autoReroll
        ? `Auto-reroll will keep rolling this slot up to ${autoAttempts} times, so it is gone on the first attempt.`
        : 'A reroll replaces it outright - it can come out worse.',
    };
  }

  // Refine (roadmap 3.4): the affix keeps its stat and rarity; only the
  // number moves, up to 20% either way, for a quarter of the price. Nothing
  // is destroyed, so it needs no guard.
  function doRefine(index: number) {
    rerollAffixIndex = index;
    const outcome = rerollAffix(rerollItemId, index, REFINE_OPERATION);
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    rerollFlash++;
    play('forgeReroll');
    refresh();
  }

  function doReroll(index: number) {
    rerollAffixIndex = index;
    const outcome = rerollAffix(rerollItemId, index, rerollOperation, {
      maxAttempts: autoReroll ? autoAttempts : 0,
      stopMinRarity,
      stopAffixIndex,
    });
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    rerollFlash++;
    play('forgeReroll');
    refresh();
  }
</script>

<div class="tabs" role="tablist" aria-label="Forge">
  <button role="tab" class="tab" class:active={tab === 'affixes'} aria-selected={tab === 'affixes'} data-testid="forge-tab-affixes" onclick={() => (tab = 'affixes')}>Affixes</button>
  <button role="tab" class="tab" class:active={tab === 'fusion'} aria-selected={tab === 'fusion'} data-testid="forge-tab-fusion" onclick={() => (tab = 'fusion')}>Fusion</button>
  <span class="purse dim small">You have <Money amount={gold} /></span>
</div>

<div class="grid single">
  {#if tab === 'fusion'}
  <section class="panel">
    <h2>Fusion</h2>

    {#if forgeLevel === 0}
      <p class="warn">
        Fusion needs a Forge in your village. Build one under
        <strong>Village</strong>, then come back.
      </p>
    {:else if forge.isError && forge.data === undefined}
      <!-- Modul: "Nothing to fuse yet" is a claim about the chest; a failed
           request cannot make it. -->
      <QueryError query={forge} what="your equipment" />
    {:else if forge.data !== undefined && allRows.length === 0}
      <p class="dim small">
        Nothing to fuse yet - you need three of the same item at the same
        rarity.
      </p>
    {:else if allRows.length > 0}
      <!-- Modul: ONE ROW PER ITEM, worn lines first (task 100). It replaced a
           chip per (item, rarity) pair - about 75 of them, 1,750 px at 390 px
           before the form they filled - and a chip only set three selects
           further down, so on a phone a tap looked like nothing. The row says
           the decision ("3 Mythic -> 1 Relic, up to 12k gold") and carries its
           own Fuse. -->
      {#if allRows.length > ROWS_SHOWN}
        <input
          class="rowsearch"
          type="search"
          placeholder="Find an item..."
          bind:value={rowSearch}
          aria-label="Find an item to fuse"
        />
      {/if}
      <ul class="fuserows" data-testid="fusion-rows">
        {#each visibleRows as row (row.baseItemId)}
          {@const blocked = rowBlocked(row)}
          {@const best = row.next?.tier ?? row.counts[0]?.tier ?? row.wornTier}
          <li class="fuserow" class:worn={row.worn} data-testid="fusion-row">
            <ItemIcon baseItemId={row.baseItemId} name={prettifyBaseId(row.baseItemId)} qualityTier={best} size="sm" />
            <div class="fusebody">
              <span class="fusename">
                <span class="nm" style="color: {rarityColor(best)}">{prettifyBaseId(row.baseItemId)}</span>
                {#if row.worn}<span class="wornchip">Worn</span>{/if}
              </span>
              <span class="counts">
                {#each row.counts.slice(0, 4) as c (c.tier)}
                  <span class="count"><RarityPip tier={c.tier} />&times;{formatNumber(c.count)}</span>
                {/each}
                {#if row.counts.length > 4}<span class="dim tiny">+{row.counts.length - 4} more</span>{/if}
                {#if row.counts.length === 0}<span class="dim tiny">no spare pieces</span>{/if}
              </span>
              {#if row.next}
                <span class="nextfuse">
                  3 {rarityName(row.next.tier)} &rarr;
                  <b style="color: {rarityColor(row.next.tier + 1)}">1 {rarityName(row.next.tier + 1)}</b>
                  &middot; up to {formatGold(fusionFeeCeiling(row.next.tier))}
                </span>
              {:else if row.worn}
                <span class="dim tiny">
                  Three spare pieces at one rarity make the next one up.
                </span>
              {/if}
              {#if blocked && row.next}<span class="blocked tiny">{blocked}</span>{/if}
            </div>
            <div class="fuseacts">
              {#if row.next}
                <button
                  class="tiny-btn primary"
                  data-testid="fusion-row-fuse"
                  data-guide={blocked === null ? 'forge-fuse' : undefined}
                  disabled={blocked !== null || $commandInFlight.has(rowKey(row))}
                  onclick={() => fuseRow(row)}
                >Fuse</button>
              {/if}
              {#if row.stack && row.total >= 6}
                <button class="tiny-btn" data-testid="fusion-row-stack" onclick={() => openStack(row)}>Stack</button>
              {/if}
            </div>
          </li>
        {/each}
      </ul>
      {#if matchedRows.length === 0}
        <p class="dim tiny">No item by that name has anything to fuse.</p>
      {:else if !showAllRows && rowSearch.trim() === '' && matchedRows.length > ROWS_SHOWN}
        <button class="tiny-btn" onclick={() => (showAllRows = true)}>
          Show all {matchedRows.length}
        </button>
      {/if}
    {/if}

    <!-- Modul: THE EXPLAINER LIVES WITH THE MACHINE IT EXPLAINS. It sat in the
         Affix reroll panel, under a heading about something else. -->
    <p class="explainer dim small">
      Three identical pieces at the same rarity become one of the next rarity,
      for a gold fee. It always works - nothing is lost to chance. The piece
      with the most affixes is kept and gains one more; the other two are used
      up. Your Forge's level (now {forgeLevel}) is the highest rarity it can
      make, and anything a character wears is left alone.
    </p>

    <button class="tiny-btn linkish" onclick={() => (manualOpen = !manualOpen)} aria-expanded={manualOpen}>
      {manualOpen ? 'Hide the manual choice' : 'Choose which ones'}
    </button>

    {#if manualOpen}
    <div class="manual">
    <label>
      Item to upgrade
      <select bind:value={fusionTarget}>
        <option value={0}>Choose...</option>
        {#each targetChoices as item (item.Id)}<option value={item.Id}>{label(item)}</option>{/each}
      </select>
    </label>

    <label>
      Matching item
      <select bind:value={fusionSacOne} disabled={fusionTarget === 0}>
        <option value={0}>Choose...</option>
        {#each sacOneChoices as item (item.Id)}<option value={item.Id}>{label(item)}</option>{/each}
      </select>
    </label>

    <label>
      Matching item
      <select bind:value={fusionSacTwo} disabled={fusionTarget === 0}>
        <option value={0}>Choose...</option>
        {#each sacTwoChoices as item (item.Id)}<option value={item.Id}>{label(item)}</option>{/each}
      </select>
    </label>

    {#if fusionTarget !== 0 && twins.length < 2}
      <p class="blocked small">
        You only have {twins.length + 1} of this item at
        {rarityName(fusionTargetItem?.QualityTier ?? 0)}. Fusion needs three.
      </p>
    {/if}

    {#if fusionTargetItem}
      <p class="preview">
        {rarityName(fusionTargetItem.QualityTier)}
        {#if !atMaxTier}
          &rarr; <b style="color: {rarityColor(fusionTargetItem.QualityTier + 1)}">
            {rarityName(fusionTargetItem.QualityTier + 1)}
          </b>
        {:else}
          &middot; <span class="blocked">already at the highest rarity the Forge can make</span>
        {/if}
      </p>

      {#if !atMaxTier}
        <p class="dim small">
          Fee up to <b class:blocked={gold < fusionFee}><Money amount={fusionFee} available={gold} /></b>.
          Luck and the Diamond Star event take up to 25% off.
        </p>
      {/if}
    {/if}

    <button
      data-guide={fuseBlocked === null ? 'forge-fuse' : undefined}
      onclick={fuse}
      disabled={fuseBlocked !== null || $commandInFlight.has(`fuse:${fusionTarget}`)}
    >
      Fuse
    </button>
    <DisabledReason text={fuseBlocked} />
    </div>
    {/if}

    {#if fusionTargetItem && stackTiers.length > 0}
      {@const plan = stackPreview.data}
      <div class="stack" data-testid="fuse-stack" bind:this={stackPanel}>
        <h3>The whole stack</h3>
        <label>
          Fuse every {prettifyBaseId(fusionTargetItem.BaseItemId)} up to
          <select bind:value={stackTo} data-testid="fuse-stack-to">
            {#each stackTiers as tier (tier)}
              <option value={tier}>{rarityName(tier)}</option>
            {/each}
          </select>
        </label>
        {#if stackPreview.isPending}
          <p class="dim tiny">Working it out...</p>
        {:else if stackPreview.isError && !plan}
          <QueryError query={stackPreview} what="the fusion plan" />
        {:else if plan}
          {#if plan.TotalFusions === 0}
            <p class="blocked small">
              {plan.StoppedByGold ? 'Not enough gold for even one fusion.' : 'Nothing in this stack can be fused that far.'}
            </p>
          {:else}
            <p class="small" data-testid="fuse-stack-plan">
              {formatNumber(plan.TotalFusions)} fusions &middot;
              <b><Money amount={plan.GoldCost} /></b> &rarr;
              {#each plan.Result.filter((r) => r.Count > 0).reverse() as row, i (row.Tier)}
                {i > 0 ? ', ' : ''}<span style="color: {rarityColor(row.Tier)}">{formatNumber(row.Count)}&times; {rarityName(row.Tier)}</span>
              {/each}
            </p>
            {#if plan.StoppedByGold}
              <p class="dim tiny">Your gold runs out part way; this is as far as it reaches.</p>
            {/if}
            {#if plan.StoppedByCap}
              <p class="dim tiny">One press fuses at most 10,000 times; press again for the rest.</p>
            {/if}
            <p class="dim tiny">Locked pieces and anything a character wears are left alone.</p>
          {/if}
        {/if}
        <button
          onclick={fuseWholeStack}
          data-testid="fuse-stack-go"
          disabled={!plan || plan.TotalFusions === 0}
        >
          Fuse the stack{plan && plan.TotalFusions > 0 ? ` · ${formatGold(plan.GoldCost)}` : ''}
        </button>
      </div>
    {/if}

    {#if fusionFlash > 0}
      {#key fusionFlash}
        <span class="forgefx folk-sweep"></span>
        <span class="forgeburst"><Burst count={14} reach={3.6} /></span>
      {/key}
    {/if}
  </section>
  {:else}
  <section class="panel affixpanel">
    <h2>Affixes</h2>
    <p class="dim small">
      <b>Reroll</b> rolls one affix fresh - stat, rarity and value - and it can
      come out worse. <b>Refine</b> keeps the stat and its rarity and moves only
      the value, up to 20% either way, for a quarter of the price. The price
      follows the item's region and rarity, never how many times you tried.
    </p>

    <div class="affixlayout">
    <div class="picker">
    <div class="pickhead">
      <h3>{showAllForReroll ? 'Everything you own' : 'What you are wearing'}</h3>
      <button class="tiny-btn" onclick={() => (showAllForReroll = !showAllForReroll)}>
        {showAllForReroll ? 'Only equipped' : 'Show all'}
      </button>
    </div>
    {#if forge.isError && forge.data === undefined}
      <QueryError query={forge} what="your equipment" />
    {/if}
    <!-- Modul: the quest line's second target (data-guide, server-named in
         QuestLineRegistry): before an item is picked there is no Reroll button
         to light, so the picker is what it points at. -->
    <div data-guide="forge-reroll-item">
      {#if showAllForReroll || wornByCharacter.every((g) => g.items.length === 0)}
        <ItemBrowser
          items={showAllForReroll ? owned : rerollChoices}
          selectedId={rerollItemId}
          compact
          emptyText={showAllForReroll
            ? 'Nothing to reroll.'
            : 'Nothing equipped. Dress a character on the Character screen, or show all.'}
          onselect={(item) => pickItem(item.Id)}
        />
      {:else}
        <div class="wearers" data-testid="forge-wearers">
          {#each wornByCharacter as group (group.slot)}
            <section class="wearer">
              <h4>{group.title}</h4>
              {#if group.items.length === 0}
                <p class="dim tiny">Wears nothing.</p>
              {:else}
                <ul class="worn">
                  {#each group.items as item (item.Id)}
                    <li>
                      <button
                        class="wornrow"
                        class:selected={item.Id === rerollItemId}
                        data-item-id={item.Id}
                        onclick={() => pickItem(item.Id)}
                      >
                        <ItemIcon baseItemId={item.BaseItemId} name={prettifyBaseId(item.BaseItemId)} qualityTier={item.QualityTier} size="sm" />
                        <span class="text">
                          <span class="name">{prettifyBaseId(item.BaseItemId)}</span>
                          <span class="meta dim tiny">
                            <span style="color: {rarityColor(item.QualityTier)}">{rarityName(item.QualityTier)}</span>
                            &middot; {slotNameOf(item.EquippedInSlotIndex)}{#if summarizeAffixes(item.Affixes, 1)}
                              &middot; {summarizeAffixes(item.Affixes, 1)}{/if}
                          </span>
                        </span>
                      </button>
                    </li>
                  {/each}
                </ul>
              {/if}
            </section>
          {/each}
        </div>
      {/if}
    </div>

    </div>

    <div class="cardcol" bind:this={cardCol}>
    {#if !rerollItem}
      <p class="dim small pickfirst">Pick a piece on the left to see its affixes.</p>
    {:else}
      {#if rerollItem.IsAffixLocked}
        <p class="warn">This item's affixes are locked.</p>
      {/if}

      <div class="itemcard">
        <span
          class="cardname"
          style="color: {rarityColor(rerollItem.QualityTier)}"
          class:rarity-glow={shouldGlow(rerollItem.QualityTier)}
        >
          {prettifyBaseId(rerollItem.BaseItemId)}
        </span>
        <span class="dim tiny">{rarityName(rerollItem.QualityTier)}</span>
        <Affixes affixes={rerollItem.Affixes} baseItemId={rerollItem.BaseItemId} qualityTier={rerollItem.QualityTier} />
      </div>

      {#if rerollAffixRows.length === 0}
        <p class="dim">This item has no affixes to reroll.</p>
      {:else}
        <!-- Modul: ONE ROW PER AFFIX, WITH ITS OWN BUTTONS (owner, 2026-11-03).
             It was a list of slots to pick and two look-alike buttons side by
             side at the bottom; Refine sat flush against Reroll and was pressed
             by mistake. Each affix now carries Reroll on the left and Refine on
             the right, apart and styled differently, with the price on each.
             The SLOT still stays where it is - only what is in it changes (the
             "jumping affixes" reports, fixed server-side). -->
        <ul class="affixrows" data-testid="forge-affix-rows">
          {#each rerollAffixRows as row, index (index)}
            {@const guard = guardFor(index)}
            {@const locked = rerollItem.IsAffixLocked}
            <li class="affixrow" class:selected={rerollAffixIndex === index}>
              <span class="what">
                <span class="dim tiny">slot {index + 1}</span>
                <span class="label">{row.label} {row.value}</span>
                <span class="dim tiny">{row.rarityName}</span>
              </span>
              <span class="acts">
                {#if guard}
                  {#key `${rerollItemId}:${index}:${autoReroll}`}
                    <ConfirmButton
                      small
                      label="{autoReroll ? `Auto ×${autoAttempts}` : 'Reroll'} · {formatGold(rerollFee)}"
                      guide={index === 0 ? 'forge-reroll' : undefined}
                      confirmLabel="Destroy {guard.what}?"
                      disabled={locked || gold < rerollFee}
                      onConfirm={() => doReroll(index)}
                    />
                  {/key}
                {:else}
                  <button
                    class="tiny-btn rerollbtn"
                    data-guide={index === 0 ? 'forge-reroll' : undefined}
                    data-testid="forge-reroll"
                    disabled={locked || gold < rerollFee}
                    onclick={() => doReroll(index)}
                  >
                    {autoReroll ? `Auto ×${autoAttempts}` : 'Reroll'} · {formatGold(rerollFee)}
                  </button>
                {/if}
                <span class="gap" aria-hidden="true"></span>
                <button
                  class="tiny-btn refinebtn"
                  data-testid="forge-refine"
                  disabled={locked || autoReroll || gold < refineFee}
                  title="Keeps the stat and its rarity; rerolls only the value, up to 20% up or down."
                  onclick={() => doRefine(index)}
                >
                  Refine ±20% · {formatGold(refineFee)}
                </button>
              </span>
            </li>
          {/each}
        </ul>
        {#if guardRarity !== GUARD_OFF}
          <p class="dim tiny hint">A Reroll on {guardRarity === 5 ? 'a Legendary' : 'an Epic or Legendary'} affix asks first.</p>
        {/if}
      {/if}

      <details class="autopanel" bind:open={autoOpen}>
        <summary>Auto-reroll and safety</summary>
        <label class="guard">
          Ask before rerolling
          <select bind:value={guardRarity}>
            <option value={5}>Legendary affixes</option>
            <option value={4}>Epic and Legendary</option>
            <option value={GUARD_OFF}>Never ask</option>
          </select>
        </label>

        <label class="check">
          <input type="checkbox" bind:checked={autoReroll} />
          Reroll buttons keep rolling until a stop condition is met
        </label>

        {#if autoReroll}
          <div class="auto">
            <label>
              Max attempts
              <input type="number" min="1" max="1000" bind:value={autoAttempts} />
            </label>
            <label>
              Stop at rarity
              <select bind:value={stopMinRarity}>
                {#each [1, 2, 3, 4, 5] as rarity}
                  <option value={rarity}>{AFFIX_RARITY_NAMES[rarity]} or better</option>
                {/each}
              </select>
            </label>
            <label>
              Stop on stat
              <select bind:value={stopAffixIndex}>
                <option value={0}>Any stat</option>
                {#each KNOWN_AFFIX_IDS as id, index}
                  <!-- 1-BASED index into AffixRegistry.Definitions; 0 means
                       "any stat". Sent as an index because the packet is
                       fixed-layout (serverMirrors.test.ts guards the order). -->
                  {#if !RETIRED_AFFIX_IDS.has(id)}
                    <option value={index + 1}>{affixLabel(id)}</option>
                  {/if}
                {/each}
              </select>
            </label>
          </div>
          <p class="dim tiny hint">{describeStopCondition(stopMinRarity, stopAffixIndex)}</p>
          <p class="dim tiny hint">
            You pay per roll actually made: a run that stops on its fifth attempt
            costs five. Refine is off while auto-reroll is on.
          </p>
        {/if}
      </details>

      {#if rerollFlash > 0}
        {#key rerollFlash}
          <span class="forgefx folk-sweep"></span>
          <span class="forgeburst"><Burst count={12} reach={3} color="var(--brass-lit)" /></span>
        {/key}
      {/if}
      {#if gold < rerollFee}
        <p class="dim tiny">
          You have <Money amount={gold} /> and a reroll of this piece costs <Money amount={rerollFee} />.
        </p>
      {/if}
    {/if}
    </div>
    </div>
  </section>
  {/if}

  <!-- Modul: the "Forge stock" list is gone with the recipes behind it.
       Equipment is monster loot and tools are crafted, and nothing is both -
       so a panel that forged armour out of ore had no place left. The Forge
       still does what its name says: it fuses and rerolls what you looted.
       Tools live on the Crafting screen with the rest of the recipe tree. -->
</div>

<style>
  /* The flourish overlays the whole panel rather than one control: a fusion
     changes an item that is listed in several places on this screen, so
     marking the machine reads better than marking one row of it. */
  .forgefx {
    position: absolute;
    inset: 0;
    border-radius: var(--radius);
    pointer-events: none;
  }

  .forgeburst {
    position: absolute;
    left: 50%;
    top: 50%;
    width: 0;
    height: 0;
    pointer-events: none;
  }

  .panel {
    position: relative;
  }

  .guard {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    margin: 0.4rem 0;
  }

  /* One column per character on a wide screen, stacked on a phone. */
  .wearers {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(11rem, 1fr));
    gap: 0.75rem;
  }
  .wearer {
    min-width: 0;
    border: 1px solid var(--border);
    border-radius: 0.4rem;
    padding: 0.4rem 0.5rem;
  }
  .wearer h4 {
    margin: 0 0 0.3rem;
    font-size: 0.9rem;
  }
  .worn {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.15rem;
  }
  .wornrow {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    width: 100%;
    min-height: 44px;
    padding: 0.25rem 0.4rem;
    background: transparent;
    background-image: none;
    box-shadow: none;
    border: 1px solid transparent;
    border-radius: 0.35rem;
    text-align: left;
    color: inherit;
    font: inherit;
  }
  .wornrow:hover {
    border-color: var(--border);
  }
  .wornrow.selected {
    border-color: var(--accent);
  }
  .wornrow .text {
    display: grid;
    min-width: 0;
  }
  .wornrow .name,
  .wornrow .meta {
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }
  .slots {
    list-style: none;
    margin: 0.3rem 0;
    padding: 0;
    display: grid;
    gap: 0.25rem;
  }

  .slot {
    display: grid;
    grid-template-columns: 4rem 1fr auto;
    align-items: center;
    gap: 0.5rem;
    width: 100%;
    text-align: left;
    padding: 0.3rem 0.45rem;
    background: transparent;
    border: 1px solid transparent;
    border-radius: 0.35rem;
    color: inherit;
    font: inherit;
    cursor: pointer;
  }

  @media (hover: hover) and (pointer: fine) {
    .slot:hover {
      border-color: currentColor;
    }
  }

  .slot.selected {
    border-color: currentColor;
    background: rgba(127, 127, 127, 0.18);
  }

  .price {
    margin: 0.3rem 0;
  }

  .price .blocked {
    color: var(--danger);
  }

  .explainer {
    border-left: 2px solid var(--border);
    padding-left: 0.6rem;
    margin: 0.4rem 0;
  }

  /* Modul: "Show all (tools too)" WAS TOUCHING THE FILTERS.
     Measured, not guessed: zero vertical gap between this row's bottom and the
     ItemBrowser's filter selects, and a 6px horizontal overlap with the rarity
     one - so the button's rounded edge crossed into the control below it.
     This row had no bottom margin at all and the browser beneath has no top
     one, which left the two flush. */
  .pickhead {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
    margin-bottom: 0.6rem;
  }

  .pickhead h3 {
    margin: 0;
  }

  .rowsearch {
    width: 100%;
    margin: 0 0 0.5rem;
  }

  .fuserows {
    list-style: none;
    margin: 0 0 0.5rem;
    padding: 0;
    display: grid;
    gap: 0.35rem;
  }

  .fuserow {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.4rem 0.5rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-raised);
  }

  .fuserow.worn {
    border-color: var(--brass);
  }

  /* The one child allowed to shrink; it ellipsises rather than collapsing to
     zero width (client_web/CLAUDE.md, the Chest's 0-wide name). */
  .fusebody {
    flex: 1 1 auto;
    min-width: 0;
    display: grid;
    gap: 0.15rem;
    font-size: 0.82rem;
  }

  .fusename {
    display: flex;
    align-items: center;
    gap: 0.35rem;
    min-width: 0;
  }

  .fusename .nm {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-weight: 600;
  }

  .wornchip {
    flex-shrink: 0;
    font-size: 0.65rem;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    padding: 0 0.3rem;
    border-radius: var(--radius-xs);
    border: 1px solid var(--brass);
    color: var(--brass-lit);
  }

  .counts {
    display: flex;
    flex-wrap: wrap;
    gap: 0.2rem 0.5rem;
    align-items: center;
    font-size: 0.72rem;
    color: var(--text-dim);
  }

  .count {
    display: inline-flex;
    align-items: center;
    gap: 0.15rem;
    font-variant-numeric: tabular-nums;
  }

  .nextfuse {
    font-size: 0.75rem;
  }

  .fuseacts {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
    flex-shrink: 0;
  }

  .fuseacts button {
    flex-shrink: 0;
  }

  .manual {
    margin-top: 0.6rem;
  }

  .linkish {
    margin: 0.2rem 0 0.4rem;
  }

  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(20rem, 1fr));
    gap: 1rem;
    padding: 1rem;
    align-items: start;
  }
  .grid.single {
    grid-template-columns: minmax(0, 1fr);
    padding-top: 0.5rem;
  }

  .tabs {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.5rem;
    padding: 1rem 1rem 0;
  }
  .tab {
    min-height: 44px;
    padding: 0 1.1rem;
    font-weight: 700;
  }
  .tab.active {
    border-color: var(--accent);
    color: var(--accent);
  }
  .purse {
    margin-left: auto;
  }

  /* Picker on the left, the item and its affixes on the right; stacked on a
     phone. The picker keeps its own scroll so a long chest does not push the
     card off the screen. */
  .affixlayout {
    display: grid;
    grid-template-columns: minmax(16rem, 2fr) minmax(0, 3fr);
    gap: 1rem;
    align-items: start;
  }
  @media (max-width: 760px) {
    .affixlayout {
      grid-template-columns: minmax(0, 1fr);
    }
  }
  .picker,
  .cardcol {
    min-width: 0;
  }
  .affixpanel .wearers {
    grid-template-columns: minmax(0, 1fr);
  }
  .pickfirst {
    padding: 1rem 0;
  }
  .cardname {
    font-weight: 700;
    margin-right: 0.4rem;
  }

  .affixrows {
    list-style: none;
    margin: 0.75rem 0 0.25rem;
    padding: 0;
    display: grid;
    gap: 0.4rem;
  }
  .affixrow {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    justify-content: space-between;
    gap: 0.4rem 0.75rem;
    padding: 0.4rem 0.6rem;
    border: 1px solid var(--border);
    border-radius: 0.4rem;
  }
  .affixrow.selected {
    border-color: var(--accent);
  }
  .affixrow .what {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 0.2rem 0.5rem;
    min-width: 0;
  }
  .affixrow .label {
    font-weight: 700;
  }
  .affixrow .acts {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.4rem;
    margin-left: auto;
  }
  /* The two actions are kept APART and look different: Reroll destroys the
     affix, Refine only nudges its number. */
  .affixrow .gap {
    width: 0.75rem;
  }
  .refinebtn {
    background: transparent;
    background-image: none;
    box-shadow: none;
    border-style: dashed;
  }

  .autopanel {
    margin-top: 0.75rem;
    border-top: 1px solid var(--border);
    padding-top: 0.5rem;
  }
  .autopanel summary {
    cursor: pointer;
    min-height: 44px;
    display: flex;
    align-items: center;
    font-weight: 700;
  }

  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
  }

  .small {
    font-size: 0.8rem;
    margin: 0 0 0.7rem;
  }
  .hint {
    margin: -0.3rem 0 0.6rem;
  }
  .err {
    color: var(--danger);
  }
  .blocked {
    color: var(--danger);
  }

  .warn {
    padding: 0.5rem 0.65rem;
    background: rgba(224, 85, 63, 0.12);
    border-left: 3px solid var(--danger);
    border-radius: 4px;
    font-size: 0.82rem;
    margin: 0 0 0.7rem;
  }

  label {
    display: grid;
    gap: 0.25rem;
    font-size: 0.8rem;
    color: var(--text-dim);
    margin-bottom: 0.55rem;
  }

  label.check {
    display: flex;
    align-items: center;
    gap: 0.4rem;
  }

  label.check input {
    width: auto;
  }

  select,
  input {
    font: inherit;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.4rem 0.5rem;
    width: 100%;
  }

  .auto {
    display: grid;
    gap: 0.4rem;
    padding: 0.6rem;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    margin-bottom: 0.6rem;
  }

  .auto label {
    margin-bottom: 0;
  }

  .itemcard {
    padding: 0.5rem 0.6rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    margin-bottom: 0.6rem;
    font-size: 0.85rem;
  }

  .preview {
    font-size: 0.85rem;
    margin: 0 0 0.6rem;
  }

  .stack {
    margin-top: 1rem;
    padding-top: 0.8rem;
    border-top: 1px solid var(--border);
  }

  .stack h3 {
    margin: 0 0 0.4rem;
    font-size: 0.95rem;
  }
</style>
