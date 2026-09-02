<script lang="ts">
  // Modul: the recipe list, read from the server rather than restated.
  //
  // Same endpoint the Crafting screen uses, so the wiki cannot describe a
  // recipe tree the bench does not have. The player's own stock comes back in
  // the same payload and is deliberately ignored here: a wiki page is a
  // reference, not a shopping list, and rendering "you have 4 of 100" would
  // make the page mean different things to different readers.

  import { createQuery } from '@tanstack/svelte-query';
  import { authedGet } from '../net/auth';
  import ItemIcon from './ItemIcon.svelte';
  import { prettifyBaseId } from '../net/content';
  import { craftingProfessionName } from './slots';

  interface CraftingRecipe {
    ResultItemId: number;
    ResultBaseItemId: string;
    ProfessionType: number;
    RequiredLevel: number;
    CraftingTimeMs: number;
    Mat1BaseItemId: string;
    Mat1Count: number;
    Mat2BaseItemId: string;
    Mat2Count: number;
  }

  interface RecipeSnapshot {
    PlayerLevel: number;
    Recipes: CraftingRecipe[];
  }

  const snapshot = createQuery(() => ({
    queryKey: ['wikiRecipes'],
    queryFn: () => authedGet<RecipeSnapshot>('/api/v1/crafting/recipes'),
    staleTime: 60_000 * 60,
  }));

  let search = $state('');
  let profession = $state(-1);

  const all = $derived(snapshot.data?.Recipes ?? []);

  const professions = $derived(
    Array.from(new Set(all.map((r) => r.ProfessionType))).sort((a, b) => a - b),
  );

  const shown = $derived.by(() => {
    const needle = search.trim().toLowerCase();
    return all
      .filter((r) => {
        if (profession >= 0 && r.ProfessionType !== profession) return false;
        if (!needle) return true;
        const haystack = `${prettifyBaseId(r.ResultBaseItemId)} ${prettifyBaseId(r.Mat1BaseItemId)} ${prettifyBaseId(r.Mat2BaseItemId)}`;
        return haystack.toLowerCase().includes(needle);
      })
      .slice()
      .sort(
        (a, b) =>
          a.ProfessionType - b.ProfessionType ||
          a.RequiredLevel - b.RequiredLevel ||
          prettifyBaseId(a.ResultBaseItemId).localeCompare(prettifyBaseId(b.ResultBaseItemId)),
      );
  });

  function seconds(ms: number): string {
    if (ms < 1000) return `${ms}ms`;
    return `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)}s`;
  }
</script>

<div class="recipes">
  <div class="controls">
    <input type="search" bind:value={search} placeholder="Search recipes…" />
    <select bind:value={profession}>
      <option value={-1}>Every profession</option>
      {#each professions as p}
        <option value={p}>{craftingProfessionName(p)}</option>
      {/each}
    </select>
  </div>

  {#if snapshot.isPending}
    <p class="dim tiny">Reading the recipe tree…</p>
  {:else if snapshot.isError}
    <p class="dim tiny err">
      Could not read the recipe tree. It comes from the server, so it needs you
      to be signed in.
    </p>
  {:else if shown.length === 0}
    <p class="dim tiny">No recipe matches that.</p>
  {:else}
    <p class="dim tiny count">{shown.length} of {all.length} recipes</p>
    <div class="scroll">
      <table>
        <thead>
          <tr>
            <th>Makes</th>
            <th>Profession</th>
            <th class="num">Level</th>
            <th>Costs</th>
            <th class="num">Time</th>
          </tr>
        </thead>
        <tbody>
          {#each shown as recipe (recipe.ResultItemId)}
            <tr>
              <td>
                <span class="mat">
                  <ItemIcon
                    baseItemId={recipe.ResultBaseItemId}
                    name={prettifyBaseId(recipe.ResultBaseItemId)}
                    size="sm"
                  />
                  {prettifyBaseId(recipe.ResultBaseItemId)}
                </span>
              </td>
              <td class="dim">{craftingProfessionName(recipe.ProfessionType)}</td>
              <td class="num">{recipe.RequiredLevel}</td>
              <td>
                <span class="costs">
                  {#if recipe.Mat1BaseItemId}
                    <span class="mat">
                      <ItemIcon
                        baseItemId={recipe.Mat1BaseItemId}
                        name={prettifyBaseId(recipe.Mat1BaseItemId)}
                        size="sm"
                      />
                      {recipe.Mat1Count} {prettifyBaseId(recipe.Mat1BaseItemId)}
                    </span>
                  {/if}
                  {#if recipe.Mat2BaseItemId}
                    <span class="mat">
                      <ItemIcon
                        baseItemId={recipe.Mat2BaseItemId}
                        name={prettifyBaseId(recipe.Mat2BaseItemId)}
                        size="sm"
                      />
                      {recipe.Mat2Count} {prettifyBaseId(recipe.Mat2BaseItemId)}
                    </span>
                  {/if}
                </span>
              </td>
              <td class="num">{seconds(recipe.CraftingTimeMs)}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  {/if}
</div>

<style>
  .recipes {
    display: flex;
    flex-direction: column;
    gap: 0.6rem;
    min-width: 0;
  }

  .controls {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
  }

  .controls input {
    flex: 1 1 10rem;
    min-width: 0;
  }

  .scroll {
    overflow-x: auto;
    border: 1px solid var(--border);
    border-radius: var(--radius, 8px);
    background: rgba(0, 0, 0, 0.12);
  }

  table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.85rem;
    min-width: 30rem;
  }

  th {
    text-align: left;
    padding: 0.5rem 0.6rem;
    border-bottom: 1px solid var(--border);
    color: var(--text-dim);
    font-weight: 600;
    white-space: nowrap;
  }

  td {
    padding: 0.4rem 0.6rem;
    border-bottom: 1px solid rgba(128, 128, 128, 0.12);
  }

  tbody tr:last-child td {
    border-bottom: none;
  }

  .num {
    text-align: right;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }

  .mat {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
  }

  .costs {
    display: flex;
    flex-wrap: wrap;
    gap: 0.15rem 0.75rem;
  }

  .count {
    margin: 0;
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
</style>
