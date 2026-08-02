<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { queryKeys, fetchRecipes, type CraftingRecipe } from '../lib/net/rest';
  import { prettifyBaseId } from '../lib/net/content';
  import { startTreeCraft } from '../lib/net/commands';
  import { pushLocalNotice, playerState } from '../lib/stores/game';
  import { craftingProfessionName } from '../lib/ui/slots';

  const client = useQueryClient();
  const recipes = createQuery(() => ({ queryKey: queryKeys.recipes, queryFn: fetchRecipes }));

  const snap = $derived($playerState);

  let search = $state('');
  let affordableOnly = $state(false);

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

  function craft(recipe: CraftingRecipe) {
    const outcome = startTreeCraft(recipe.ResultItemId);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
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

    <div class="filters">
      <input placeholder="Filter by name..." bind:value={search} />
      <label class="check">
        <input type="checkbox" bind:checked={affordableOnly} />
        Craftable only
      </label>
    </div>

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
              <button class="tiny-btn" disabled={!ready} onclick={() => craft(recipe)}>Craft</button>
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
</style>
