<script lang="ts">
  import { formatNumber, numberTitle } from '../lib/ui/format';
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { invalidateOwnedItems } from '../lib/net/queryClient';
  import { queryKeys, fetchRecipes, fetchBreedingRoster, type CraftingRecipe } from '../lib/net/rest';
  import { prettifyBaseId, loadContent, monsterName, type ContentRegistry } from '../lib/net/content';
  import { assignCharacterActivity, startTreeCraft, MAX_CRAFT_BATCH } from '../lib/net/commands';
  import { craftingActivityId, CRAFTING_BAND, isCraftingActivity } from '../lib/ui/slots';
  import { pushLocalNotice, playerState } from '../lib/stores/game';
  import { craftingProfessionName } from '../lib/ui/slots';
  import WorkshopCommissions from '../lib/ui/WorkshopCommissions.svelte';
  import WorkerPicker from '../lib/ui/WorkerPicker.svelte';
  import ItemIcon from '../lib/ui/ItemIcon.svelte';
  import { commandInFlight } from '../lib/ui/commandInFlight';
  import { requestScreen } from '../lib/stores/navigation';
  import { workersOf, workerName, describeJob, type Worker } from '../lib/ui/workers';
  import { affordableUnits as unitsFor, recipeGroup, toolEffect, firstStepLine } from '../lib/ui/craftingCards';

  const client = useQueryClient();
  const recipes = createQuery(() => ({ queryKey: queryKeys.recipes, queryFn: fetchRecipes }));

  const snap = $derived($playerState);

  let registry = $state<ContentRegistry | null>(null);
  $effect(() => {
    loadContent()
      .then((c) => (registry = c))
      .catch(() => {});
  });

  let search = $state('');

  // Modul: CRAFT NOW vs PUT TO WORK are two different acts and the screen only
  // ever offered the second. Assigning a character sets their activity and they
  // craft one unit per interval FOREVER while materials last - right for
  // idling, wrong for "I need a pickaxe", which is why making one tool meant
  // assigning a worker and then remembering to stop them.
  //
  // The batch is a two-way toggle beside the list now, and the number is on
  // every Craft button: a detached "Craft x10" checkbox changed what a button
  // two screens down did without the button saying so (task 101). The server
  // clamps the batch either way; this only decides what to ask for.
  let batchSize = $state(1);

  function affordableUnits(recipe: CraftingRecipe): number {
    return unitsFor(recipe, MAX_CRAFT_BATCH);
  }

  function craftKey(recipe: CraftingRecipe): string {
    return `craft:${recipe.ResultItemId}`;
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
        'error',
      );
    }

    // Modul: held until the server answers, so a double tap is one batch and
    // not two - the stock shown here is only refreshed after the result.
    const outcome = commandInFlight.run(craftKey(recipe), () => startTreeCraft(recipe.ResultItemId, batchSize));
    if (outcome === null) return;
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');

    pushLocalNotice(
      `Crafting ${batchSize} x ${prettifyBaseId(recipe.ResultBaseItemId)}.`,
      'info',
    );
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.recipes });
      invalidateOwnedItems(client);
    }, 800);
  }

  const playerLevel = $derived(recipes.data?.PlayerLevel ?? 0);
  const allRecipes = $derived(recipes.data?.Recipes ?? []);

  // Modul: Mat*CurrentStock is the UNIFIED backpack+stash balance - exactly
  // what InventoryAndStashSystem will spend - so "affordable" here means
  // genuinely affordable. Reporting one tier while spending from two was the
  // shape of an earlier bug, and is why the endpoint returns the unified
  // number rather than the backpack's.
  const matched = $derived.by(() => {
    const needle = search.trim().toLowerCase();
    return allRecipes
      .filter((r) => (needle === '' ? true : prettifyBaseId(r.ResultBaseItemId).toLowerCase().includes(needle)))
      .sort((a, b) => a.RequiredLevel - b.RequiredLevel);
  });

  // Modul: THREE GROUPS, because "locked" and "missing materials" were the
  // same faded card. A locked recipe waits on a level; a missing one waits on
  // gathering - different answers, so they are said apart. Locked starts
  // folded: it is the longest group and the one nothing can be done about.
  const ready = $derived(matched.filter((r) => recipeGroup(r, playerLevel) === 'ready'));
  const missing = $derived(matched.filter((r) => recipeGroup(r, playerLevel) === 'missing'));
  const locked = $derived(matched.filter((r) => recipeGroup(r, playerLevel) === 'locked'));
  const readyCount = $derived(allRecipes.filter((r) => recipeGroup(r, playerLevel) === 'ready').length);
  let showLocked = $state(false);

  // Modul: crafting is a JOB now, not a button.
  //
  // Every recipe has always carried a CraftingTimeMs and nothing read it: a
  // craft consumed its materials and produced its result in the same instant,
  // with no character involved. So a hundred meals was a hundred clicks, and a
  // character could gather or fight but never cook.
  //
  // The worker is picked by NAME now (task 101), shared with Gathering; it
  // was "Slot 1 / Slot 2" in a native select.
  const names = createQuery(() => ({ queryKey: queryKeys.breedingRoster, queryFn: fetchBreedingRoster, staleTime: 60_000 }));
  const nameById = $derived(new Map((names.data ?? []).map((c) => [c.CharacterId, c.Name])));
  const workers = $derived(workersOf(snap));
  let worker = $state(1);
  const chosen = $derived<Worker | null>(workers.find((w) => w.slot === worker) ?? workers[0] ?? null);

  function recipeName(activityId: number): string | null {
    if (!isCraftingActivity(activityId)) return null;
    const recipe = allRecipes[activityId - CRAFTING_BAND];
    return recipe ? prettifyBaseId(recipe.ResultBaseItemId) : null;
  }

  const jobOf = (w: Worker) => describeJob(w.activity, w.halt, { recipeName, monsterName: (id) => monsterName(registry, id) });

  // The activity id is the recipe's INDEX in the server's table, and this list
  // is that same table in that same order - so the index has to come from the
  // unfiltered array, never from the filtered/sorted view on screen.
  function activityIdFor(recipe: CraftingRecipe): number {
    const index = allRecipes.findIndex((r) => r.ResultItemId === recipe.ResultItemId);
    return index < 0 ? -1 : craftingActivityId(index);
  }

  function putToWork(recipe: CraftingRecipe) {
    if (!chosen) return pushLocalNotice('No character to assign.', 'error');

    const activityId = activityIdFor(recipe);
    if (activityId < 0) return pushLocalNotice('That recipe is not on the server list.', 'error');

    const clash = workers.find((w) => w.slot !== chosen.slot && w.activity === activityId);
    const outcome = assignCharacterActivity(chosen.id, activityId, {
      takenBy: clash ? workerName(clash, nameById) : null,
    });
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');

    pushLocalNotice(`${workerName(chosen, nameById)} is now making ${prettifyBaseId(recipe.ResultBaseItemId)}.`, 'info');
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.recipes });
      invalidateOwnedItems(client);
    }, 800);
  }

  // Task 101: commissions are their own tab. They sat above the recipes - for
  // a guest nine lines of locked prose, for the fixture the recipes started
  // below 850 px.
  let tab = $state<'recipes' | 'commissions'>('recipes');
</script>

{#snippet card(recipe: CraftingRecipe)}
  {@const group = recipeGroup(recipe, playerLevel)}
  {@const tool = toolEffect(recipe.ResultBaseItemId)}
  {@const working = workers.find((w) => w.activity === activityIdFor(recipe))}
  <li class="card" data-group={group}>
    <ItemIcon baseItemId={recipe.ResultBaseItemId} name={prettifyBaseId(recipe.ResultBaseItemId)} size="md" />
    <div class="body">
      <strong class="name">{prettifyBaseId(recipe.ResultBaseItemId)}</strong>
      <span class="dim tiny effect">
        {#if tool}{tool.slot} &middot; {tool.effect}{:else}{craftingProfessionName(recipe.ProfessionType)}{/if}
      </span>
      <div class="mats">
        {#each [[recipe.Mat1Id, recipe.Mat1BaseItemId, recipe.Mat1CurrentStock, recipe.Mat1Count], [recipe.Mat2Id, recipe.Mat2BaseItemId, recipe.Mat2CurrentStock, recipe.Mat2Count]] as [id, base, have, need]}
          {#if id !== 0}
            {@const h = Number(have)}
            {@const n = Number(need) * batchSize}
            <div class="mat" class:short={h < n}>
              <span class="matname">{prettifyBaseId(String(base))}</span>
              <!-- Modul: "have / need" as ONE compact figure with a thin bar.
                   "0/1 226" read as two numbers. -->
              <span class="have" title="You have {numberTitle(h)}, this needs {numberTitle(n)}">
                {formatNumber(h)}<span class="dim">/{formatNumber(n)}</span>
              </span>
              <span class="thin" aria-hidden="true"><span style="width: {Math.min(100, n > 0 ? (h / n) * 100 : 100)}%"></span></span>
            </div>
          {/if}
        {/each}
      </div>
      <span class="dim tiny meta">
        {#if group === 'locked'}<span class="blocked">Level {recipe.RequiredLevel} needed</span>{:else}Level {recipe.RequiredLevel}{/if}
        &middot; {(recipe.CraftingTimeMs / 1000).toFixed(1)}s each
        {#if working}&middot; {workerName(working, nameById)} is making these{/if}
      </span>
    </div>
    <!-- Modul: AN ALIGNED BUTTON GROUP at the card's edge. The buttons used to
         follow the name, so they floated with its length and formed a ragged
         column down the page. Craft is the filled one. -->
    <div class="acts">
      <button
        class="tiny-btn primary"
        disabled={group === 'locked' || affordableUnits(recipe) < batchSize || $commandInFlight.has(craftKey(recipe))}
        onclick={() => craftNow(recipe)}
      >
        Craft{batchSize > 1 ? ` x${batchSize}` : ''}
      </button>
      <button
        class="tiny-btn"
        disabled={group !== 'ready' || !chosen}
        onclick={() => putToWork(recipe)}
      >
        <!-- Modul: the reason in the label, as Village does. -->
        {!chosen ? 'No character fielded' : working ? 'Working' : 'Put to work'}
      </button>
    </div>
  </li>
{/snippet}

<div class="wrap">
  <div class="tabs" role="tablist">
    <button role="tab" class:on={tab === 'recipes'} aria-selected={tab === 'recipes'} onclick={() => (tab = 'recipes')}>
      Recipes
    </button>
    <button
      role="tab"
      class:on={tab === 'commissions'}
      aria-selected={tab === 'commissions'}
      data-testid="crafting-tab-commissions"
      onclick={() => (tab = 'commissions')}
    >
      Workshop commissions
    </button>
  </div>

  {#if tab === 'commissions'}
    <!-- Task 83: the Workshop's commissions - one region piece at a rarity
         floor, for materials and hours. -->
    <WorkshopCommissions />
  {:else}
  <section class="panel">
    <div class="head">
      <h2>Crafting</h2>
      <span class="dim tiny">
        {readyCount} ready
        {#if snap}&middot; Workshop {snap.CraftingWorkshopLevel}{/if}
      </span>
    </div>

    {#if recipes.isPending}
      <p class="dim">Loading recipes...</p>
    {:else if recipes.isError}
      <p class="err">{recipes.error?.message}</p>
    {:else}
      {#if readyCount === 0}
        <!-- Modul: THE NEXT STEP, FIRST. A new player saw "0 of 30 craftable
             now" over thirty faded cards with every button disabled. -->
        <div class="nextstep" data-testid="crafting-next-step">
          <p>{firstStepLine(allRecipes, playerLevel)}</p>
          <button class="tiny-btn primary" onclick={() => requestScreen('gathering')}>Go gather</button>
        </div>
      {/if}

      <p class="dim tiny lead">
        Crafting takes time and needs a character: <strong>Craft</strong> makes
        them now, <strong>Put to work</strong> keeps someone making them while
        materials last.
      </p>

      <WorkerPicker
        {workers}
        names={nameById}
        selected={chosen?.slot ?? 1}
        describe={jobOf}
        onpick={(slot) => (worker = slot)}
        label="Who works"
      />

      <div class="filters">
        <input placeholder="Filter by name..." bind:value={search} aria-label="Filter recipes" />
        <div class="batch" role="radiogroup" aria-label="How many per Craft">
          <button role="radio" aria-checked={batchSize === 1} class:on={batchSize === 1} onclick={() => (batchSize = 1)}>Make 1</button>
          <button
            role="radio"
            aria-checked={batchSize === MAX_CRAFT_BATCH}
            class:on={batchSize === MAX_CRAFT_BATCH}
            onclick={() => (batchSize = MAX_CRAFT_BATCH)}
          >Make {MAX_CRAFT_BATCH}</button>
        </div>
      </div>

      {#if ready.length > 0}
        <h3>Ready <span class="dim tiny">{ready.length}</span></h3>
        <ul class="recipes">
          {#each ready as recipe (recipe.ResultItemId)}{@render card(recipe)}{/each}
        </ul>
      {/if}

      {#if missing.length > 0}
        <h3>Missing materials <span class="dim tiny">{missing.length}</span></h3>
        <ul class="recipes">
          {#each missing as recipe (recipe.ResultItemId)}{@render card(recipe)}{/each}
        </ul>
      {/if}

      {#if locked.length > 0}
        <button class="foldbtn" aria-expanded={showLocked} onclick={() => (showLocked = !showLocked)}>
          {showLocked ? 'Hide' : 'Show'} locked recipes ({locked.length})
        </button>
        {#if showLocked}
          <ul class="recipes">
            {#each locked as recipe (recipe.ResultItemId)}{@render card(recipe)}{/each}
          </ul>
        {/if}
      {/if}

      {#if matched.length === 0}
        <p class="dim">No recipes match.</p>
      {/if}
    {/if}
  </section>
  {/if}
</div>

<style>
  .wrap {
    padding: 1rem;
    display: grid;
    gap: 0.75rem;
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
  }

  /* Underlined sub-tabs, deliberately unlike the filled top tabs. */
  .tabs {
    display: flex;
    gap: 0.25rem;
    border-bottom: 1px solid var(--border);
  }

  .tabs button {
    flex-shrink: 0;
    background: none;
    background-image: none;
    box-shadow: none;
    border: 0;
    border-bottom: 2px solid transparent;
    border-radius: 0;
    padding: 0.45rem 0.7rem;
    color: var(--text-dim);
  }

  .tabs button.on {
    color: var(--text);
    border-bottom-color: var(--brass-lit);
    font-weight: 600;
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

  h3 {
    margin: 0.9rem 0 0.4rem;
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
  .err {
    color: var(--danger);
  }
  .lead {
    margin: 0 0 0.5rem;
  }

  .nextstep {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.6rem;
    padding: 0.6rem 0.7rem;
    margin: 0 0 0.6rem;
    border: 1px solid var(--brass);
    border-radius: var(--radius);
    background: color-mix(in srgb, var(--brass) 10%, transparent);
    font-size: 0.85rem;
  }

  .nextstep p {
    margin: 0;
    min-width: 0;
  }

  .nextstep button {
    flex-shrink: 0;
  }

  .filters {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
    align-items: center;
    margin: 0.2rem 0 0.4rem;
  }

  .filters input {
    flex: 1 1 10rem;
    min-width: 0;
  }

  .batch {
    display: inline-flex;
    flex-shrink: 0;
  }

  .batch button {
    flex-shrink: 0;
    background-image: none;
    box-shadow: none;
    background: var(--bg);
    font-size: 0.78rem;
    padding: 0.3rem 0.6rem;
  }

  .batch button:first-child {
    border-radius: var(--radius) 0 0 var(--radius);
  }

  .batch button:last-child {
    border-radius: 0 var(--radius) var(--radius) 0;
    border-left: 0;
  }

  .batch button.on {
    background: color-mix(in srgb, var(--brass) 25%, var(--bg));
    border-color: var(--brass-lit);
    font-weight: 700;
  }

  .recipes {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(17rem, 1fr));
    gap: 0.5rem;
  }

  .card {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr) auto;
    gap: 0.5rem;
    align-items: start;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.5rem 0.6rem;
  }

  .card[data-group='ready'] {
    border-color: var(--good);
  }

  .card[data-group='locked'] {
    opacity: 0.6;
  }

  .body {
    display: grid;
    gap: 0.15rem;
    min-width: 0;
  }

  .name {
    font-size: 0.86rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .acts {
    display: grid;
    gap: 0.3rem;
    align-content: start;
  }

  .acts button {
    flex-shrink: 0;
  }

  .blocked {
    color: var(--danger);
  }

  .mats {
    display: grid;
    gap: 0.2rem;
    margin-top: 0.2rem;
  }

  .mat {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    gap: 0 0.4rem;
    font-size: 0.74rem;
    font-variant-numeric: tabular-nums;
  }

  .matname {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    color: var(--text-dim);
  }

  .mat.short .have {
    color: var(--danger);
  }

  .thin {
    grid-column: 1 / -1;
    display: block;
    height: 3px;
    border-radius: 2px;
    background: var(--border);
    overflow: hidden;
  }

  .thin > span {
    display: block;
    height: 100%;
    background: var(--good);
  }

  .mat.short .thin > span {
    background: var(--danger);
  }

  .foldbtn {
    width: 100%;
    margin-top: 0.8rem;
    text-align: left;
  }
</style>
