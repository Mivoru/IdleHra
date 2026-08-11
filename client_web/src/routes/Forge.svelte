<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { queryKeys, fetchForge, type ForgeEquipment } from '../lib/net/rest';
  import { prettifyBaseId, loadContent, type ContentRegistry } from '../lib/net/content';
  import { executeForgeFusion, rerollAffix, REROLL_OPERATIONS } from '../lib/net/commands';
  import Burst from '../lib/ui/Burst.svelte';
  import { pushLocalNotice, playerState } from '../lib/stores/game';
  import ItemBrowser from '../lib/ui/ItemBrowser.svelte';

  import { rarityColor, rarityName, shouldGlow, MAX_QUALITY_TIER } from '../lib/ui/rarity';
  import { toDisplayAffixes, AFFIX_RARITY_NAMES, KNOWN_AFFIX_IDS } from '../lib/ui/affixes';
  import Affixes from '../lib/ui/Affixes.svelte';

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
    client.invalidateQueries({ queryKey: queryKeys.inventory });
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
  const atMaxTier = $derived((fusionTargetItem?.QualityTier ?? 0) >= MAX_QUALITY_TIER);

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

  const targetChoices = $derived(owned.filter((i) => i.Id !== fusionSacOne && i.Id !== fusionSacTwo));
  const sacOneChoices = $derived(twins.filter((i) => i.Id !== fusionSacTwo));
  const sacTwoChoices = $derived(twins.filter((i) => i.Id !== fusionSacOne));

  // Sets the player can actually fuse right now: three or more of the same
  // item at the same rarity. Without this the screen is a puzzle - three
  // dropdowns and no way to tell whether any legal combination exists.
  const fusableSets = $derived.by(() => {
    const groups = new Map<string, { base: string; tier: number; count: number }>();
    for (const item of owned) {
      if (item.QualityTier >= MAX_QUALITY_TIER) continue;
      const key = `${item.BaseItemId}#${item.QualityTier}`;
      const seen = groups.get(key) ?? { base: item.BaseItemId, tier: item.QualityTier, count: 0 };
      seen.count++;
      groups.set(key, seen);
    }
    return [...groups.values()]
      .filter((g) => g.count >= 3)
      .sort((a, b) => b.tier - a.tier || a.base.localeCompare(b.base));
  });

  // ForgeSplicingEngine: BaseGoldCost * 1.5^currentTier, rounded up. Luck and
  // the Diamond Star event take up to 25% off server-side, so this is the
  // ceiling rather than the exact charge - stated in the UI as such rather
  // than quietly presented as final.
  const FORGE_BASE_FEE = 200;
  const FORGE_FEE_GROWTH = 1.35;
  const fusionFee = $derived(
    fusionTargetItem ? Math.ceil(FORGE_BASE_FEE * Math.pow(FORGE_FEE_GROWTH, fusionTargetItem.QualityTier)) : 0,
  );
  const gold = $derived(Number($playerState?.Gold ?? 0));

  // Modul: THE REROLL PRICE, SHOWN. Mirrors
  // AffixRegistry.CalculateRerollGoldCost - 100 * 1.35^(itemTier-1).
  //
  // Reported from play alongside the price itself being too high: "it does not
  // even say what it costs". It did not. A player pressed a button, gold left,
  // and the only way to learn the rate was to watch the balance - which is how
  // someone spends a night's income on five rolls without noticing until it is
  // gone. A charge you cannot see before you agree to it is not a price.

  function pickSet(base: string, tier: number) {
    const trio = owned.filter((i) => i.BaseItemId === base && i.QualityTier === tier).slice(0, 3);
    if (trio.length < 3) return;
    fusionTarget = trio[0].Id;
    fusionSacOne = trio[1].Id;
    fusionSacTwo = trio[2].Id;
  }

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

    const outcome = executeForgeFusion(fusionTarget, fusionSacOne, fusionSacTwo, forgeLevel, match);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
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

  function doReroll() {
    // The affix ABOUT TO BE DESTROYED, not the one that will replace it.
    const current = rerollAffixRows[rerollAffixIndex];
    if (current && current.rarity >= guardRarity) {
      const what = `${current.rarityName} ${current.label} ${current.value}`;
      const scope = autoReroll
        ? `Auto-reroll will keep rolling this slot up to ${autoAttempts} times, so it is gone on the first attempt.`
        : 'A reroll replaces it outright - it can come out worse.';
      if (!confirm(`Reroll ${what}?

${scope}`)) return;
    }

    const outcome = rerollAffix(rerollItemId, rerollAffixIndex, rerollOperation, {
      maxAttempts: autoReroll ? autoAttempts : 0,
      stopMinRarity,
      stopAffixIndex,
    });
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
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
    {:else}
      <p class="dim small">
        Three identical items of the same rarity become one of the next rarity,
        for a gold fee. It always works - nothing is lost to chance.
      </p>

      {#if fusableSets.length > 0}
        <div class="sets">
          <span class="dim tiny">Ready to fuse:</span>
          {#each fusableSets as set (set.base + set.tier)}
            <button class="settag" onclick={() => pickSet(set.base, set.tier)}>
              <span style="color: {rarityColor(set.tier)}">{prettifyBaseId(set.base)}</span>
              <span class="dim tiny">{rarityName(set.tier)} &times;{set.count}</span>
            </button>
          {/each}
        </div>
      {:else}
        <p class="dim tiny">
          Nothing to fuse yet - you need three of the same item at the same
          rarity.
        </p>
      {/if}
    {/if}

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
          &middot; <span class="blocked">already at the maximum tier</span>
        {/if}
      </p>

      {#if !atMaxTier}
        <p class="dim small">
          Fee up to <b class:blocked={gold < fusionFee}>{fusionFee.toLocaleString()}g</b>.
          Luck and the Diamond Star event take up to 25% off.
        </p>
      {/if}
    {/if}

    <button
      onclick={fuse}
      disabled={forgeLevel === 0 ||
        fusionTarget === 0 ||
        fusionSacOne === 0 ||
        fusionSacTwo === 0 ||
        atMaxTier ||
        gold < fusionFee}
    >
      Fuse
    </button>

    {#if fusionFlash > 0}
      {#key fusionFlash}
        <span class="forgefx folk-sweep"></span>
        <span class="forgeburst"><Burst count={14} reach={3.6} /></span>
      {/key}
    {/if}
  </section>

  <section class="panel">
    <h2>Affix reroll</h2>
    <!-- Modul: SAY WHAT THE MACHINE DOES.
         Both halves of this screen were controls with no explanation, in a
         game where the two of them are the entire gear progression. A player
         who does not know that fusion needs three IDENTICAL pieces at the same
         rarity will try it with three different ones and conclude it is
         broken - which is exactly what happened. -->
    <p class="explainer dim small">
      Fusion takes <strong>three identical pieces at the same rarity</strong>
      and returns one at the next rarity up. Two ceilings apply: your Forge's
      level (currently {forgeLevel}) is the highest rarity it can produce, and
      rarity {MAX_QUALITY_TIER} is the top of the ladder.
    </p>
    <p class="dim small">
      Rerolls one affix on one item, for gold. Its stat, its rarity and its
      value are all rolled fresh together - so it can come out worse. The other
      affixes on the item are untouched.
    </p>

    {#if rerollItem}
      <p class="price">
        This reroll costs
        <b class:blocked={gold < rerollFee}>{rerollFee.toLocaleString()}g</b>.
        You have {gold.toLocaleString()}g.
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
        <Affixes affixes={rerollItem.Affixes} baseItemId={rerollItem.BaseItemId} />
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
                     same authority on both sides. -->
                <option value={index + 1}>{id}</option>
              {/each}
            </select>
          </label>
        </div>
        <p class="dim tiny hint">
          The attempt count is a request, not a limit - the server clamps it, so
          a large number here cannot drain more than it allows in one go.
        </p>
      {/if}

      <!-- Modul: THE PRICE IS ON THE BUTTON.
           It was stated in a paragraph above, which is where a player does not
           look at the moment they commit. A charge belongs on the thing that
           charges - and this is the button that quietly took a night's income
           over five presses. -->
      <button
        onclick={doReroll}
        disabled={rerollAffixRows.length === 0 || rerollItem.IsAffixLocked || gold < rerollFee}
      >
        {autoReroll ? `Auto-reroll up to ${autoAttempts}x` : 'Reroll once'}
        &middot; {rerollFee.toLocaleString()}g{autoReroll ? ' each' : ''}
      </button>

      {#if rerollFlash > 0}
        {#key rerollFlash}
          <span class="forgefx folk-sweep"></span>
          <span class="forgeburst"><Burst count={12} reach={3} color="var(--brass-lit)" /></span>
        {/key}
      {/if}
      {#if gold < rerollFee}
        <p class="dim tiny">
          You have {gold.toLocaleString()}g and this costs {rerollFee.toLocaleString()}g.
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

  .slot:hover {
    border-color: currentColor;
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

  .sets {
    display: flex;
    flex-wrap: wrap;
    gap: 0.35rem;
    align-items: center;
    margin: 0 0 0.6rem;
  }

  .settag {
    display: inline-flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 0.1rem;
    padding: 0.3rem 0.5rem;
    border-radius: var(--radius, 6px);
    border: 1px solid rgba(255, 255, 255, 0.14);
    background: rgba(255, 255, 255, 0.04);
    cursor: pointer;
    font-size: 0.8rem;
    width: auto;
  }

  .settag:hover {
    border-color: rgba(255, 255, 255, 0.32);
  }

  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(20rem, 1fr));
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

</style>
