<script lang="ts">
  import { onMount } from 'svelte';
  import { createQuery } from '@tanstack/svelte-query';
  import { queryKeys, fetchCodex } from '../lib/net/rest';
  import { loadContent, type ContentRegistry } from '../lib/net/content';
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
