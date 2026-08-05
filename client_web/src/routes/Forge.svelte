<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { queryKeys, fetchForge, type ForgeEquipment } from '../lib/net/rest';
  import { prettifyBaseId } from '../lib/net/content';
  import { executeForgeFusion, rerollAffix, craftForgeRecipe, REROLL_OPERATIONS } from '../lib/net/commands';
  import { pushLocalNotice, playerState } from '../lib/stores/game';
  import { rarityColor, rarityName, shouldGlow, MAX_QUALITY_TIER } from '../lib/ui/rarity';
  import { toDisplayAffixes, AFFIX_RARITY_NAMES, KNOWN_AFFIX_IDS } from '../lib/ui/affixes';
  import Affixes from '../lib/ui/Affixes.svelte';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import { takePendingFocusEquipment } from '../lib/stores/navigation';

  const client = useQueryClient();
  const forge = createQuery(() => ({ queryKey: queryKeys.forge, queryFn: fetchForge }));

  const snap = $derived($playerState);
  const forgeLevel = $derived(snap?.ForgeLevel ?? 0);
  const owned = $derived(forge.data?.OwnedEquipment ?? []);

  function refresh() {
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.forge });
      client.invalidateQueries({ queryKey: queryKeys.inventory });
    }, 800);
  }

  function label(item: ForgeEquipment): string {
    return `${prettifyBaseId(item.BaseItemId)} [${rarityName(item.QualityTier)}] #${item.Id}`;
  }

  // --- forge crafting -------------------------------------------------------
  // These ids come from /api/v1/forge/inventory, which returns real
  // CraftingReceptuary ids - the only source this client has. An invented one
  // disconnects the session rather than being rejected.
  function craftRecipe(recipeId: number) {
    const outcome = craftForgeRecipe(recipeId);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    refresh();
  }

  // --- fusion ---------------------------------------------------------------
  let fusionTarget = $state(0);
  let fusionSacOne = $state(0);
  let fusionSacTwo = $state(0);

  const fusionTargetItem = $derived(owned.find((i) => i.Id === fusionTarget) ?? null);
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
  const FORGE_BASE_FEE = 1000;
  const fusionFee = $derived(
    fusionTargetItem ? Math.ceil(FORGE_BASE_FEE * Math.pow(1.5, fusionTargetItem.QualityTier)) : 0,
  );
  const gold = $derived(Number($playerState?.Gold ?? 0));

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
          }
        : undefined;

    const outcome = executeForgeFusion(fusionTarget, fusionSacOne, fusionSacTwo, forgeLevel, match);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    fusionSacOne = 0;
    fusionSacTwo = 0;
    refresh();
  }

  // --- reroll ---------------------------------------------------------------
  let rerollItemId = $state(0);
  let rerollAffixIndex = $state(0);
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

  const rerollItem = $derived(owned.find((i) => i.Id === rerollItemId) ?? null);
  const rerollAffixRows = $derived(rerollItem ? toDisplayAffixes(rerollItem.Affixes) : []);
  const selectedOperation = $derived(REROLL_OPERATIONS[rerollOperation] ?? REROLL_OPERATIONS[0]);

  function doReroll() {
    const outcome = rerollAffix(rerollItemId, rerollAffixIndex, rerollOperation, {
      maxAttempts: autoReroll ? autoAttempts : 0,
      stopMinRarity,
      stopAffixIndex,
    });
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
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
  </section>

  <section class="panel">
    <h2>Affix reroll</h2>
    <p class="dim small">
      Rerolls one affix on one item. Value and stat rerolls cost gold; a rarity
      upgrade is the only operation priced in Diamonds.
    </p>

    <label>
      Item
      <select bind:value={rerollItemId} onchange={() => (rerollAffixIndex = 0)}>
        <option value={0}>Choose...</option>
        {#each owned as item (item.Id)}<option value={item.Id}>{label(item)}</option>{/each}
      </select>
    </label>

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
        <Affixes affixes={rerollItem.Affixes} />
      </div>

      {#if rerollAffixRows.length === 0}
        <p class="dim">This item has no affixes to reroll.</p>
      {:else}
        <label>
          Affix
          <select bind:value={rerollAffixIndex}>
            {#each rerollAffixRows as row, index}
              <option value={index}>{row.label} {row.value} ({row.rarityName})</option>
            {/each}
          </select>
        </label>
      {/if}

      <!-- Modul: the operation picker is gone. There is one reroll and it
           costs gold, so a dropdown with a single entry would be asking the
           player to choose between one thing - see REROLL_OPERATIONS. The hint
           stays, because what the reroll actually does to the affix is the part
           worth saying. -->
      <p class="dim tiny hint">{selectedOperation.hint}</p>

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

      <button onclick={doReroll} disabled={rerollAffixRows.length === 0 || rerollItem.IsAffixLocked}>
        {autoReroll ? `Auto-reroll up to ${autoAttempts}x` : 'Reroll once'}
      </button>
    {/if}
  </section>

  <section class="panel">
    <h2>Forge stock</h2>
    {#if forge.isPending}
      <Skeleton />
    {:else if forge.isError}
      <p class="err">{forge.error?.message}</p>
    {:else}
      <p class="dim small">{owned.length} items available to the Forge.</p>
      <ul class="stock">
        {#each (forge.data?.Recipes ?? []) as recipe (recipe.RecipeId)}
          {@const affordable = recipe.CurrentMaterialStock >= recipe.MaterialCost}
          <li>
            <span>{prettifyBaseId(recipe.ResultBaseItemId)}</span>
            <span class="dim tiny" class:short={!affordable}>
              {recipe.MaterialName}
              {recipe.CurrentMaterialStock.toLocaleString()}/{recipe.MaterialCost.toLocaleString()}
            </span>
            <button class="tiny-btn" disabled={!affordable} onclick={() => craftRecipe(recipe.RecipeId)}>
              Forge
            </button>
          </li>
        {/each}
      </ul>
    {/if}
  </section>
</div>

<style>
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

  .stock {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.25rem;
    max-height: 28rem;
    overflow-y: auto;
  }

  .stock li {
    display: grid;
    grid-template-columns: 1fr auto auto;
    align-items: center;
    gap: 0.6rem;
    font-size: 0.82rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.22rem;
  }

  .stock .short {
    color: var(--danger);
  }
</style>
