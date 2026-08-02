<script lang="ts">
  import { onMount } from 'svelte';
  import { createQuery } from '@tanstack/svelte-query';
  import { queryKeys, fetchCodex, fetchCodexRegions } from '../lib/net/rest';
  import { loadContent, REGION_COUNT, type ContentRegistry } from '../lib/net/content';
  import { playerState } from '../lib/stores/game';
  import Bar from '../lib/ui/Bar.svelte';

  const codex = createQuery(() => ({ queryKey: queryKeys.codex, queryFn: fetchCodex }));

  let registry = $state<ContentRegistry | null>(null);
  onMount(async () => {
    registry = await loadContent().catch(() => null);
  });

  const snap = $derived($playerState);
  const byMonster = $derived(new Map((codex.data ?? []).map((e) => [e.MonsterId, e])));

  // Modul: region completion is a BITMASK on the hot path - bit (region - 1)
  // per completed region - not a count, so it is read per bit rather than
  // compared against a total.
  function regionComplete(regionIndex: number): boolean {
    return snap ? (snap.CompletedAreaFlags & (1 << regionIndex)) !== 0 : false;
  }

  const discovered = $derived((codex.data ?? []).filter((e) => e.Kills > 0).length);

  // Modul: region progress toward the PERMANENT loot-luck bonus a completed
  // region grants. The bitmask above says whether a region is done; this says
  // how far off the unfinished ones are, which is what makes it a goal rather
  // than a status light.
  const regionsQuery = createQuery(() => ({
    queryKey: queryKeys.codexRegions,
    queryFn: fetchCodexRegions,
  }));

  // THE ENDPOINT REPORTS TEN REGIONS. THIS GAME HAS FIVE.
  //
  // HandleCodexRegionsSnapshot groups monsters by ContentRegistry.
  // GetMonsterRegionTier, and RegionTier is NOT the canonical region - the
  // five real regions are the 25 canonical monsters, ids 91-115, five each.
  // Deriving regions from RegionTier is a mistake this project has made
  // before; content.ts records the canonical grouping for exactly this reason.
  //
  // So the server emits phantom regions 6-10, each showing 0/1000 kills that
  // can never be earned because no canonical monster belongs to them. Filtered
  // here rather than displayed, because ten bars of which five are unreachable
  // is worse than five that are all real.
  const regions = $derived((regionsQuery.data ?? []).filter((r) => r.RegionId <= REGION_COUNT));

  // The server writes `LootLuckBonusPct = isCompleted ? 1 : 0`, so the field
  // is zero for every region a player has not finished and cannot be used to
  // say what finishing one is worth. The reward is a flat 1% and that number
  // is named here, from that line, rather than read from a field that will
  // always be 0 at the moment the question is being asked.
  const LOOT_LUCK_PER_REGION_PCT = 1;

  const totalLootLuck = $derived(regions.filter((r) => r.IsCompleted).length * LOOT_LUCK_PER_REGION_PCT);
</script>

<div class="wrap">
  <section class="panel">
    <div class="head">
      <h2>Monster codex</h2>
      <span class="dim tiny">{discovered} of 25 encountered</span>
    </div>
    <p class="dim small">
      Kill counts level each entry. Only the 25 canonical monsters appear -
      the content file holds more ids, but they are not part of any region.
    </p>

    <h3>
      Region completion
      {#if totalLootLuck > 0}
        <span class="earned">+{totalLootLuck}% loot luck earned</span>
      {/if}
    </h3>

    {#if regionsQuery.isPending}
      <p class="dim tiny">Loading...</p>
    {:else if regions.length === 0}
      <p class="dim tiny">No region requirements are defined.</p>
    {:else}
      <p class="dim tiny reg-note">
        Each region needs {regions[0].RequiredKills.toLocaleString()} kills of its
        <em>least</em>-killed monster, so the bar tracks your weakest entry, not
        your total. Finishing one grants +{LOOT_LUCK_PER_REGION_PCT}% loot luck
        permanently.
      </p>
      <ul class="regions">
        {#each regions as region (region.RegionId)}
          <li>
            <span class="region-name">
              Region {region.RegionId}
              <!-- The word as well as the colour, so a completed region reads
                   as completed without relying on the green. -->
              {#if region.IsCompleted}<span class="done">complete</span>{/if}
            </span>
            <Bar
              value={Math.min(region.CurrentKills, region.RequiredKills)}
              max={Math.max(1, region.RequiredKills)}
              color={region.IsCompleted ? 'var(--good)' : 'var(--rarity-6)'}
              label={`${region.CurrentKills.toLocaleString()} / ${region.RequiredKills.toLocaleString()}`}
            />
          </li>
        {/each}
      </ul>
    {/if}

    {#if codex.isPending}
      <p class="dim">Loading...</p>
    {:else if codex.isError}
      <p class="err">{codex.error?.message}</p>
    {:else if !registry}
      <p class="dim">Loading content...</p>
    {:else}
      {#each registry.regions as region, regionIndex}
        <h3>
          Region {regionIndex + 1}
          {#if regionComplete(regionIndex)}<span class="done">complete</span>{/if}
        </h3>
        <ul class="entries">
          {#each region as monster (monster.Id)}
            {@const entry = byMonster.get(monster.Id)}
            <li class:unseen={!entry || entry.Kills === 0}>
              <div class="line">
                <span class="name">{monster.Name}</span>
                <span class="dim tiny">
                  {#if entry && entry.Kills > 0}
                    lv {entry.Level} &middot; {entry.Kills.toLocaleString()} kills
                  {:else}
                    never encountered
                  {/if}
                </span>
              </div>
              {#if entry && entry.Kills > 0}
                <Bar
                  value={entry.Kills}
                  max={Math.max(1, entry.NextLevelKills)}
                  color="var(--rarity-6)"
                  label={`${entry.Kills.toLocaleString()} / ${entry.NextLevelKills.toLocaleString()}`}
                />
              {/if}
              <div class="dim tiny stats">
                {monster.MaxHp.toLocaleString()} HP &middot;
                {monster.AttackPower.toLocaleString()} atk &middot;
                {monster.BaseXpReward.toLocaleString()} xp
              </div>
            </li>
          {/each}
        </ul>
      {/each}
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
    justify-content: space-between;
    align-items: baseline;
    gap: 1rem;
  }

  h2 {
    margin: 0 0 0.4rem;
    font-size: 1.05rem;
  }

  h3 {
    margin: 1.1rem 0 0.4rem;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
    display: flex;
    align-items: baseline;
    gap: 0.5rem;
  }

  .done {
    color: var(--good);
    text-transform: none;
    letter-spacing: 0;
  }

  .earned {
    color: var(--gold);
    text-transform: none;
    letter-spacing: 0;
    font-weight: 700;
  }

  /* Five short columns rather than five full-width bars: the region strip is
     context for the codex below it, not the subject of the screen. */
  .regions {
    list-style: none;
    margin: 0 0 0.5rem;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(11rem, 1fr));
    gap: 0.55rem;
  }

  .reg-note {
    margin: 0 0 0.5rem;
  }

  .regions li {
    display: grid;
    gap: 0.15rem;
  }

  .region-name {
    font-size: 0.8rem;
    display: flex;
    align-items: baseline;
    gap: 0.4rem;
  }

  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.8rem;
    margin: 0 0 0.4rem;
  }
  .tiny {
    font-size: 0.72rem;
  }
  .err {
    color: var(--danger);
  }

  .entries {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(15rem, 1fr));
    gap: 0.5rem;
  }

  .entries li {
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.45rem 0.55rem;
  }

  .entries li.unseen {
    opacity: 0.5;
  }

  .line {
    display: flex;
    justify-content: space-between;
    align-items: baseline;
    gap: 0.5rem;
    margin-bottom: 0.2rem;
  }

  .name {
    font-weight: 600;
    font-size: 0.85rem;
  }

  .stats {
    margin-top: 0.2rem;
    font-variant-numeric: tabular-nums;
  }
</style>
