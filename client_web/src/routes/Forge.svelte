<script lang="ts">
  import { formatNumber, formatGold } from '../lib/ui/format';
  import Money from '../lib/ui/Money.svelte';
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { invalidateOwnedItems } from '../lib/net/queryClient';
  import { queryKeys, fetchForge, fetchForgeStackPreview, type ForgeEquipment } from '../lib/net/rest';
  import { prettifyBaseId, loadContent, type ContentRegistry } from '../lib/net/content';
  import { executeForgeFusion, fuseStack, rerollAffix, REROLL_OPERATIONS, MAX_FORGE_LEVEL } from '../lib/net/commands';
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
    affixLabel,
    describeStopCondition,
  } from '../lib/ui/affixes';
  import Affixes from '../lib/ui/Affixes.svelte';
  import QueryError from '../lib/ui/QueryError.svelte';

  import { takePendingFocusEquipment } from '../lib/stores/navigation';
  import { commandResults } from '../lib/stores/game';

  let content: ContentRegistry | null = $state(null);
  $effect(() => {
    loadContent().then((c) => (content = c));
  });

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

  function getRerollCost(regionTier: number): number {
    switch (regionTier) {
      case 1: return 1000;
      case 2: return 2000;
      case 3: return 4000;
      case 4: return 5000;
      case 5: return 10000;
      default: return 10000;
    }
  }

  const rerollItem = $derived(owned.find((i) => i.Id === rerollItemId) ?? null);
  const rerollFee = $derived.by(() => {
    if (!rerollItem || !content) return 0;
    const def = content.itemsByBaseId.get(rerollItem.BaseItemId);
    const regionTier = def?.RegionTier ?? 1;
    return getRerollCost(regionTier);
  });
  const rerollAffixRows = $derived(rerollItem ? toDisplayAffixes(rerollItem.Affixes) : []);
  const selectedOperation = $derived(REROLL_OPERATIONS[rerollOperation] ?? REROLL_OPERATIONS[0]);

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
  const rerollGuard = $derived.by(() => {
    const current = rerollAffixRows[rerollAffixIndex];
    if (!current || current.rarity < guardRarity) return null;
    return {
      what: `${current.rarityName} ${current.label} ${current.value}`,
      scope: autoReroll
        ? `Auto-reroll will keep rolling this slot up to ${autoAttempts} times, so it is gone on the first attempt.`
        : 'A reroll replaces it outright - it can come out worse.',
    };
  });

  function doReroll() {
    const outcome = rerollAffix(rerollItemId, rerollAffixIndex, rerollOperation, {
      maxAttempts: autoReroll ? autoAttempts : 0,
      stopMinRarity,
      stopAffixIndex,
    });
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    rerollFlash++;
    refresh();
  }
</script>

<div class="grid">
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

  <section class="panel">
    <h2>Affix reroll</h2>
    <p class="dim small">
      Rerolls one affix on one item, for gold. Its stat, its rarity and its
      value are all rolled fresh together - so it can come out worse. The other
      affixes on the item are untouched.
    </p>

    {#if rerollItem}
      <p class="price">
        This reroll costs
        <b class:blocked={gold < rerollFee}><Money amount={rerollFee} available={gold} /></b>.
        You have <Money amount={gold} />.
        <span class="dim tiny">
          The price follows the item's rarity, not how many times you have
          tried - a run of poor rolls does not get more expensive.
        </span>
      </p>
    {/if}

    <div class="pickhead">
      <h3>{showAllForReroll ? 'Everything you own' : 'What you are wearing'}</h3>
      <button class="tiny-btn" onclick={() => (showAllForReroll = !showAllForReroll)}>
        {showAllForReroll ? 'Only equipped' : 'Show all (tools too)'}
      </button>
    </div>
    {#if forge.isError && forge.data === undefined}
      <QueryError query={forge} what="your equipment" />
    {/if}
    <!-- Modul: the quest line's second target (data-guide, server-named in
         QuestLineRegistry): before an item is picked there is no Reroll button
         to light, so the picker is what it points at. -->
    <div data-guide="forge-reroll-item">
      <ItemBrowser
        items={rerollChoices}
        selectedId={rerollItemId}
        compact
        emptyText={showAllForReroll
          ? 'Nothing to reroll.'
          : 'Nothing equipped. Dress a character on the Character screen, or show all.'}
        onselect={(item) => {
          rerollItemId = item.Id;
          rerollAffixIndex = 0;
        }}
      />
    </div>

    {#if rerollItem}
      {#if rerollItem.IsAffixLocked}
        <p class="warn">This item's affixes are locked.</p>
      {/if}

      <div class="itemcard">
        <span
          style="color: {rarityColor(rerollItem.QualityTier)}"
          class:rarity-glow={shouldGlow(rerollItem.QualityTier)}
        >
          {prettifyBaseId(rerollItem.BaseItemId)}
        </span>
        <Affixes affixes={rerollItem.Affixes} baseItemId={rerollItem.BaseItemId} qualityTier={rerollItem.QualityTier} />
      </div>

      {#if rerollAffixRows.length === 0}
        <p class="dim">This item has no affixes to reroll.</p>
      {:else}
        <!-- Modul: A LIST OF SLOTS, not a dropdown of affixes.
             Reported twice as affixes "jumping". Two real bugs caused it - the
             server appended the rerolled affix to the end of the item instead
             of substituting it in place, and this screen counted a payload key
             the server skips, so the index the player picked and the index the
             server rerolled were off by one.
             Both are fixed, but a dropdown hides the thing that makes the
             remaining behaviour legible: the SLOT stays and its contents
             change. Shown as numbered slots so a reroll visibly rewrites the
             one that is highlighted and touches nothing else. -->
        <p class="dim small">Pick the slot to reroll. It stays where it is - only what is in it changes.</p>
        <ul class="slots">
          {#each rerollAffixRows as row, index}
            <li>
              <button
                class="slot"
                class:selected={rerollAffixIndex === index}
                onclick={() => (rerollAffixIndex = index)}
              >
                <span class="dim tiny">slot {index + 1}</span>
                <span class="label">{row.label} {row.value}</span>
                <span class="dim tiny">{row.rarityName}</span>
              </button>
            </li>
          {/each}
        </ul>
      {/if}

      <!-- Modul: the operation picker is gone. There is one reroll and it
           costs gold, so a dropdown with a single entry would be asking the
           player to choose between one thing - see REROLL_OPERATIONS. The hint
           stays, because what the reroll actually does to the affix is the part
           worth saying. -->
      <p class="dim tiny hint">{selectedOperation.hint}</p>

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
        Auto-reroll until a stop condition is met
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
                     "any stat". Sent as an index rather than a string because
                     the packet is fixed-layout, and the registry order is the
                     same authority on both sides.

                     The VALUE is the index and the LABEL is a readable name:
                     the list used to render raw ids like "crit_chance_pct",
                     which is the id this file has to send, not a thing to show
                     a player. -->
                <option value={index + 1}>{affixLabel(id)}</option>
              {/each}
            </select>
          </label>
        </div>
        <!-- Modul: SAY WHAT THE RUN WILL DO. The two conditions combine with
             AND and nothing on screen said so, so a player could not tell
             whether "Legendary" plus "Flat HP" meant a Legendary of any stat.
             It never did. The panel simply never claimed it. -->
        <p class="dim tiny hint">{describeStopCondition(stopMinRarity, stopAffixIndex)}</p>
        <p class="dim tiny hint">
          You pay per roll actually made, so a run that stops on its fifth
          attempt costs five. The attempt count is a request, not a limit - the
          server clamps it.
        </p>
      {/if}

      <!-- Modul: THE PRICE IS ON THE BUTTON.
           It was stated in a paragraph above, which is where a player does not
           look at the moment they commit. A charge belongs on the thing that
           charges - and this is the button that quietly took a night's income
           over five presses. -->
      {#if rerollGuard}
        <p class="dim tiny hint">
          Guarded: this destroys your {rerollGuard.what}. {rerollGuard.scope}
        </p>
        {#key `${rerollItemId}:${rerollAffixIndex}`}
          <ConfirmButton
            label="{autoReroll ? `Auto-reroll up to ${autoAttempts}x` : 'Reroll once'} · {formatGold(rerollFee)}{autoReroll ? ' each' : ''}"
            guide="forge-reroll"
            confirmLabel="Really reroll {rerollGuard.what}?"
            disabled={rerollAffixRows.length === 0 || rerollItem.IsAffixLocked || gold < rerollFee}
            onConfirm={doReroll}
          />
        {/key}
      {:else}
        <button
          data-guide="forge-reroll"
          onclick={doReroll}
          disabled={rerollAffixRows.length === 0 || rerollItem.IsAffixLocked || gold < rerollFee}
        >
          {autoReroll ? `Auto-reroll up to ${autoAttempts}x` : 'Reroll once'}
          &middot; {formatGold(rerollFee)}{autoReroll ? ' each' : ''}
        </button>
      {/if}

      {#if rerollFlash > 0}
        {#key rerollFlash}
          <span class="forgefx folk-sweep"></span>
          <span class="forgeburst"><Burst count={12} reach={3} color="var(--brass-lit)" /></span>
        {/key}
      {/if}
      {#if gold < rerollFee}
        <p class="dim tiny">
          You have <Money amount={gold} /> and this costs <Money amount={rerollFee} />.
        </p>
      {/if}
    {/if}
  </section>

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

  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
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
