<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { queryKeys, fetchRecipes, type CraftingRecipe } from '../lib/net/rest';
  import { prettifyBaseId } from '../lib/net/content';
  import { assignCharacterActivity, startTreeCraft, MAX_CRAFT_BATCH, EMPTY_GUID } from '../lib/net/commands';
  import { craftingActivityId } from '../lib/ui/slots';
  import { pushLocalNotice, playerState } from '../lib/stores/game';
  import { craftingProfessionName } from '../lib/ui/slots';

  const client = useQueryClient();
  const recipes = createQuery(() => ({ queryKey: queryKeys.recipes, queryFn: fetchRecipes }));

  const snap = $derived($playerState);

  let search = $state('');
  let affordableOnly = $state(false);

  // Modul: CRAFT NOW vs PUT TO WORK are two different acts and the screen only
  // ever offered the second. Assigning a character sets their activity and they
  // craft one unit per interval FOREVER while materials last - right for
  // idling, wrong for "I need a pickaxe", which is why making one tool meant
  // assigning a worker and then remembering to stop them.
  //
  // Ticked, one press crafts ten for ten times the materials. The server
  // clamps the batch either way; this box only decides what to ask for.
  let craftTen = $state(false);
  const batchSize = $derived(craftTen ? MAX_CRAFT_BATCH : 1);

  function affordableUnits(recipe: CraftingRecipe): number {
    const one = recipe.Mat1Id === 0 ? Infinity : Math.floor(recipe.Mat1CurrentStock / Math.max(1, recipe.Mat1Count));
    const two = recipe.Mat2Id === 0 ? Infinity : Math.floor(recipe.Mat2CurrentStock / Math.max(1, recipe.Mat2Count));
    const units = Math.min(one, two);
    return Number.isFinite(units) ? units : MAX_CRAFT_BATCH;
  }

  function craftNow(recipe: CraftingRecipe) {
    // Refuse here rather than letting the server take the materials for a
    // batch it cannot complete. ExecuteCraftingAsync is one transaction and
    // rolls back cleanly, but the player would see a press that did nothing.
    const have = affordableUnits(recipe);
    if (have < batchSize) {
      return pushLocalNotice(
        have < 1
          ? `Not enough materials for ${prettifyBaseId(recipe.ResultBaseItemId)}.`
          : `Enough for ${have}, not ${batchSize}.`,
      );
    }

    const outcome = startTreeCraft(recipe.ResultItemId, batchSize);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);

    pushLocalNotice(
      `Crafting ${batchSize} x ${prettifyBaseId(recipe.ResultBaseItemId)}.`,
      'info',
    );
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.recipes });
      client.invalidateQueries({ queryKey: queryKeys.inventory });
    }, 800);
  }

  const playerLevel = $derived(recipes.data?.PlayerLevel ?? 0);

  // Modul: Mat*CurrentStock is the UNIFIED backpack+stash balance - exactly
  // what InventoryAndStashSystem will spend - so "affordable" here means
  // genuinely affordable. Reporting one tier while spending from two was the
  // shape of an earlier bug, and is why the endpoint returns the unified
  // number rather than the backpack's.
  function isAffordable(recipe: CraftingRecipe): boolean {
    const one = recipe.Mat1Id === 0 || recipe.Mat1CurrentStock >= recipe.Mat1Count;
    const two = recipe.Mat2Id === 0 || recipe.Mat2CurrentStock >= recipe.Mat2Count;
    return one && two;
  }

  function isUnlocked(recipe: CraftingRecipe): boolean {
    return playerLevel >= recipe.RequiredLevel;
  }

  const visible = $derived.by(() => {
    const all = recipes.data?.Recipes ?? [];
    const needle = search.trim().toLowerCase();
    return all
      .filter((r) => (needle === '' ? true : r.ResultBaseItemId.toLowerCase().includes(needle)))
      .filter((r) => (affordableOnly ? isAffordable(r) && isUnlocked(r) : true))
      .sort((a, b) => {
        // Craftable first, then by level - a 104-recipe list is unusable
        // otherwise, and "what can I make right now" is the actual question.
        const aReady = isAffordable(a) && isUnlocked(a);
        const bReady = isAffordable(b) && isUnlocked(b);
        if (aReady !== bReady) return aReady ? -1 : 1;
        return a.RequiredLevel - b.RequiredLevel;
      });
  });

  const readyCount = $derived(
    (recipes.data?.Recipes ?? []).filter((r) => isAffordable(r) && isUnlocked(r)).length,
  );

  // Modul: crafting is a JOB now, not a button.
  //
  // Every recipe has always carried a CraftingTimeMs and nothing read it: a
  // craft consumed its materials and produced its result in the same instant,
  // with no character involved. So a hundred meals was a hundred clicks, and a
  // character could gather or fight but never cook.
  //
  // Assigning replaces the instant craft rather than sitting beside it - an
  // instant button next to a timed job is just the timed job being optional,
  // which is the state this is meant to leave.
  const workers = $derived(
    snap
      ? [
          { slot: 1, id: snap.Slot1_CharacterId, busy: Number(snap.ActiveActivityId) },
          { slot: 2, id: snap.Slot2_CharacterId, busy: snap.Slot2ActivityId },
          { slot: 3, id: snap.Slot3_CharacterId, busy: snap.Slot3ActivityId },
        ].filter((w) => w.id !== EMPTY_GUID)
      : [],
  );

  let worker = $state(1);

  // The activity id is the recipe's INDEX in the server's table, and this list
  // is that same table in that same order - so the index has to come from the
  // unfiltered array, never from the filtered/sorted view on screen.
  function activityIdFor(recipe: CraftingRecipe): number {
    const index = (recipes.data?.Recipes ?? []).findIndex(
      (r) => r.ResultItemId === recipe.ResultItemId,
    );
    return index < 0 ? -1 : craftingActivityId(index);
  }

  function putToWork(recipe: CraftingRecipe) {
    const chosen = workers.find((w) => w.slot === worker);
    if (!chosen) return pushLocalNotice('No character to assign.');

    const activityId = activityIdFor(recipe);
    if (activityId < 0) return pushLocalNotice('That recipe is not on the server list.');

    const clash = workers.find((w) => w.slot !== worker && w.busy === activityId);
    const outcome = assignCharacterActivity(chosen.id, activityId, {
      takenBy: clash ? `Slot ${clash.slot}` : null,
    });
    if (!outcome.ok) return pushLocalNotice(outcome.reason);

    pushLocalNotice(`Slot ${worker} is now making ${prettifyBaseId(recipe.ResultBaseItemId)}.`, 'info');
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.recipes });
      client.invalidateQueries({ queryKey: queryKeys.inventory });
    }, 800);
  }
</script>

<div class="wrap">
  <section class="panel">
    <div class="head">
      <h2>Crafting</h2>
      <span class="dim tiny">
        {readyCount} of {(recipes.data?.Recipes ?? []).length} craftable now
        {#if snap}&middot; Workshop {snap.CraftingWorkshopLevel}{/if}
      </span>
    </div>

    <p class="dim small">
      Crafting takes time and needs a character. Assign one and they will keep
      making it until you give them something else to do.
    </p>

    {#if workers.length === 0}
      <p class="dim tiny">No character available to assign.</p>
    {:else}
      <label class="worker">
        Character
        <select bind:value={worker}>
          {#each workers as w (w.slot)}
            <option value={w.slot}>Slot {w.slot}</option>
          {/each}
        </select>
      </label>
    {/if}

    <div class="filters">
      <input placeholder="Filter by name..." bind:value={search} />
      <label class="check">
        <input type="checkbox" bind:checked={affordableOnly} />
        Craftable only
      </label>
      <label class="check">
        <input type="checkbox" bind:checked={craftTen} />
        Craft x{MAX_CRAFT_BATCH}
      </label>
    </div>
    <p class="dim tiny">
      <strong>Craft</strong> makes them now and stops.
      <strong>Put to work</strong> assigns a character to keep making them while
      materials last.
    </p>

    {#if recipes.isPending}
      <p class="dim">Loading recipes...</p>
    {:else if recipes.isError}
      <p class="err">{recipes.error?.message}</p>
    {:else}
      <ul class="recipes">
        {#each visible as recipe (recipe.ResultItemId)}
          {@const ready = isAffordable(recipe) && isUnlocked(recipe)}
          <li class:ready>
            <div class="line">
              <strong>{prettifyBaseId(recipe.ResultBaseItemId)}</strong>
              <!-- Craft now sits FIRST because it is the one a player reaches
                   for. Put to work is the idle assignment and keeps its place
                   beside it rather than being replaced - they answer different
                   questions and both are wanted. -->
              <button
                class="tiny-btn primary"
                disabled={!isUnlocked(recipe) || affordableUnits(recipe) < batchSize}
                onclick={() => craftNow(recipe)}
              >
                Craft{craftTen ? ` x${MAX_CRAFT_BATCH}` : ''}
              </button>
              <button
                class="tiny-btn"
                disabled={!ready || workers.length === 0}
                onclick={() => putToWork(recipe)}
              >
                {workers.some((w) => w.busy === activityIdFor(recipe)) ? 'Working' : 'Put to work'}
              </button>
            </div>

            <div class="dim tiny meta">
              <!-- A colour chip on the profession, because this list is 103
                   rows long and the profession is what a player scans it by.
                   The name is still written out - the colour narrows the
                   search, it does not replace the label. -->
              <span class="prof" data-prof={recipe.ProfessionType}>
                {craftingProfessionName(recipe.ProfessionType)}
              </span>
              &middot; level {recipe.RequiredLevel}
              {#if !isUnlocked(recipe)}<span class="blocked">(locked)</span>{/if}
              &middot; {(recipe.CraftingTimeMs / 1000).toFixed(1)}s
            </div>

            <div class="mats">
              {#if recipe.Mat1Id !== 0}
                <span class:short={recipe.Mat1CurrentStock < recipe.Mat1Count}>
                  {prettifyBaseId(recipe.Mat1BaseItemId)}
                  {recipe.Mat1CurrentStock.toLocaleString()}/{recipe.Mat1Count.toLocaleString()}
                </span>
              {/if}
              {#if recipe.Mat2Id !== 0}
                <span class:short={recipe.Mat2CurrentStock < recipe.Mat2Count}>
                  {prettifyBaseId(recipe.Mat2BaseItemId)}
                  {recipe.Mat2CurrentStock.toLocaleString()}/{recipe.Mat2Count.toLocaleString()}
                </span>
              {/if}
            </div>
          </li>
        {/each}
      </ul>

      {#if visible.length === 0}
        <p class="dim">No recipes match.</p>
      {/if}
    {/if}
  </section>
</div>

<style>
  .worker {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    margin: 0 0 0.6rem;
    font-size: 0.85rem;
  }

  .worker select {
    width: auto;
  }

  .wrap {
    padding: 1rem;
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
    justify-content: space-between;
    gap: 1rem;
  }

  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
  }

  .dim {
    color: var(--text-dim);
  }
  .tiny {
    font-size: 0.72rem;
  }
  .err {
    color: var(--danger);
  }

  .filters {
    display: grid;
    grid-template-columns: 1fr auto;
    gap: 0.6rem;
    align-items: center;
    margin: 0.5rem 0 0.8rem;
  }

  .check {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    font-size: 0.8rem;
    color: var(--text-dim);
    white-space: nowrap;
  }

  .check input {
    width: auto;
  }

  .recipes {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(17rem, 1fr));
    gap: 0.5rem;
  }

  .recipes li {
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.5rem 0.6rem;
    opacity: 0.55;
  }

  .recipes li.ready {
    opacity: 1;
    border-color: var(--good);
  }

  .line {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
    font-size: 0.85rem;
  }

  .meta {
    margin-top: 0.15rem;
  }

  .blocked {
    color: var(--danger);
  }

  /* CRAFTING_PROFESSIONS ids 2-5. Hues are picked to be distinguishable from
     each other rather than to mean anything - there is no natural ordering
     between Smelting and Alchemy. */
  .prof {
    font-weight: 600;
  }

  .prof[data-prof='2'] {
    color: var(--rarity-11);
  }
  .prof[data-prof='3'] {
    color: var(--accent);
  }
  .prof[data-prof='4'] {
    color: var(--good);
  }
  .prof[data-prof='5'] {
    color: var(--rarity-7);
  }

  .mats {
    display: grid;
    gap: 0.1rem;
    margin-top: 0.3rem;
    font-size: 0.75rem;
    font-variant-numeric: tabular-nums;
    color: var(--text-dim);
  }

  .mats .short {
    color: var(--danger);
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.45rem;
    flex: none;
  }

  /* Craft now is the primary act on this row; Put to work sits beside it as
     the idle alternative. Only the emphasis differs - both stay legible when
     disabled, because "I cannot afford this" has to read as clearly as
     "I can". */
  .tiny-btn.primary {
    border-color: currentColor;
    font-weight: 600;
  }
</style>
