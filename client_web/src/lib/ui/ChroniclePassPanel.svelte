<script lang="ts">
  // Modul: THE CHRONICLE TRACK (owner, 2026-11-15: "lay it out like Clash
  // Royale's pass"). One vertical road, level 1 at the top: a numbered node on
  // a centre line for each level, the line filled as far as the player has
  // come, and each level's reward beside it with its own Claim. The three
  // keepsakes - avatar (25), frame (40), pet (50) - are drawn larger, because
  // they are the reason to finish. Past level 50 the road ends in a bonus
  // chest that refills every 100 XP.
  //
  // Everything is the server's: what each level pays (gold already scaled to
  // this player's income), what is ready, the XP. The client only draws it.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { tick } from 'svelte';
  import { claimChronicle, fetchChronicle, queryKeys, type ChronicleClaim, type ChronicleLevel, type ChronicleReward } from '../net/rest';
  import { petKeys } from '../net/pets';
  import { cosmeticKeys } from '../net/cosmetics';
  import { dungeonKeys } from '../net/dungeon';
  import { play } from './audio';
  import { spriteUrl } from './spriteUrl';
  import { currencyIcon } from './sprites';
  import { TICKET_ICON } from './dungeonTickets';
  import CosmeticFrame from './CosmeticFrame.svelte';
  import Money from './Money.svelte';

  const client = useQueryClient();
  // Fresh on every open, like the missions tab: a cached pass can show a level
  // the missions have already moved past.
  const pass = createQuery(() => ({
    queryKey: queryKeys.chronicle,
    queryFn: fetchChronicle,
    staleTime: 0,
    refetchOnMount: 'always' as const,
    refetchInterval: 15_000,
  }));

  let busy = $state(false);
  let got = $state<ChronicleClaim | null>(null);
  let failed = $state(false);
  let road = $state<HTMLOListElement | null>(null);
  let scrolled = false;

  const LOOT_STILL: Record<string, string> = {
    Bronze: '/chest/stage/loot-common-closed.webp',
    Silver: '/chest/stage/loot-rare-closed.webp',
    Gold: '/chest/stage/loot-epic-closed.webp',
    Ancient: '/chest/stage/loot-legendary-closed.webp',
  };

  const KEEPSAKE_LEVELS = new Set([25, 40, 50]);

  async function claim(level?: number) {
    if (busy) return;
    busy = true;
    failed = false;
    try {
      const result = await claimChronicle(level);
      client.setQueryData(queryKeys.chronicle, result.Pass);
      if (result.Outcome === 'Ok') {
        got = result;
        play(result.Keepsakes.length > 0 || result.Chests.length > 0 ? 'chestShine' : 'itemSold');
        // What a claim can touch: chests, the wardrobe, pets, the gates.
        void client.invalidateQueries({ queryKey: queryKeys.lootChests });
        void client.invalidateQueries({ queryKey: cosmeticKeys.mine });
        void client.invalidateQueries({ queryKey: petKeys.all });
        void client.invalidateQueries({ queryKey: dungeonKeys.view });
      }
    } catch {
      failed = true;
    } finally {
      busy = false;
    }
  }

  function daysLeft(seconds: number): string {
    const d = Math.floor(seconds / 86400);
    const h = Math.floor((seconds % 86400) / 3600);
    return d > 0 ? `${d} d ${h} h` : `${h} h`;
  }

  // Open on the player's place on the road, once.
  $effect(() => {
    const data = pass.data;
    if (!data || !road || scrolled) return;
    scrolled = true;
    const target = Math.max(1, Math.min(data.Levels, data.Level + 1));
    void tick().then(() => road?.querySelector(`[data-level="${target}"]`)?.scrollIntoView({ block: 'center' }));
  });

  function isKeepsake(r: ChronicleReward): boolean {
    return r.Kind === 'Avatar' || r.Kind === 'Frame' || r.Kind === 'Pet';
  }

  function main(level: ChronicleLevel): ChronicleReward {
    return level.Rewards.find(isKeepsake) ?? level.Rewards.find((r) => r.Kind !== 'Diamonds') ?? level.Rewards[0];
  }

  function extras(level: ChronicleLevel): ChronicleReward[] {
    const m = main(level);
    return level.Rewards.filter((r) => r !== m);
  }

  const view = $derived(pass.data);
  const fill = $derived(view ? Math.min(100, ((view.Level + (view.Level < view.Levels ? view.XpIntoLevel / view.XpPerLevel : 0)) / view.Levels) * 100) : 0);
</script>

{#snippet rewardArt(r: ChronicleReward, big: boolean)}
  {#if r.Kind === 'Avatar' && r.Art}
    <img class="portrait" class:big src={spriteUrl(r.Art)} alt="" loading="lazy" decoding="async" />
  {:else if r.Kind === 'Pet' && r.Art}
    <img class="pet" class:big src={spriteUrl(r.Art)} alt="" loading="lazy" decoding="async" />
  {:else if r.Kind === 'Frame'}
    <span class="frame-preview" class:big><CosmeticFrame frameId={r.Art} animate /></span>
  {:else if r.Kind === 'LootChest' && r.Chest}
    <img class="chest" src={LOOT_STILL[r.Chest]} alt="" loading="lazy" decoding="async" />
  {:else if r.Kind === 'WardrobeChest'}
    <img class="chest" src="/chest/stage/cosmetic-closed.webp" alt="" loading="lazy" decoding="async" />
  {:else if r.Kind === 'Tickets'}
    <span class="tickets">
      <img src={TICKET_ICON.Xp} alt="" width="15" height="15" />
      <img src={TICKET_ICON.Gold} alt="" width="15" height="15" />
      <img src={TICKET_ICON.Equipment} alt="" width="15" height="15" />
    </span>
  {:else}
    <img class="coin" src={currencyIcon(r.Kind === 'Diamonds' ? 'diamond' : 'gold') ?? ''} alt="" width="34" height="34" decoding="async" />
  {/if}
{/snippet}

{#snippet rewardText(r: ChronicleReward)}
  {#if r.Kind === 'Gold'}<Money amount={r.Gold} icon={false} /> <span class="dim tiny">({r.Amount} min)</span>
  {:else if r.Kind === 'Diamonds'}<Money amount={r.Amount} kind="diamond" icon={false} />
  {:else}{r.Label}{/if}
{/snippet}

{#if pass.isError}
  <p class="error small">The Chronicle could not be loaded. Try again in a moment.</p>
{:else if !view}
  <p class="dim small">Loading...</p>
{:else}
  <header class="season" data-testid="chronicle-season">
    <div class="season-name">
      <h3>{view.SeasonName}</h3>
      <span class="dim tiny">Season {view.Season} &middot; ends in {daysLeft(view.SecondsLeft)}</span>
    </div>
    <div class="level-badge" aria-label="Chronicle level {view.Level}">
      <span class="lv">{view.Level}</span>
      <span class="tiny">/ {view.Levels}</span>
    </div>
  </header>
  <div class="xp" role="progressbar" aria-valuemin="0" aria-valuemax={view.XpPerLevel} aria-valuenow={view.XpIntoLevel}>
    <span style:width="{(view.XpIntoLevel / view.XpPerLevel) * 100}%"></span>
    <em>{view.Level >= view.Levels ? `${view.OverflowProgress}/100 XP to a bonus chest` : `${view.XpIntoLevel}/${view.XpPerLevel} XP to level ${view.Level + 1}`}</em>
  </div>
  <p class="dim tiny how">Daily missions fill the Chronicle: 30 XP each, 20 more for all six. 25 full days reach level 50.</p>

  {#if got}
    <div class="got" data-testid="chronicle-got">
      <strong>You received:</strong>
      <ul>
        {#if got.Gold > 0}<li><Money amount={got.Gold} icon /></li>{/if}
        {#if got.Diamonds > 0}<li><Money amount={got.Diamonds} kind="diamond" icon /></li>{/if}
        {#if got.Tickets > 0}<li>{got.Tickets} dungeon tickets</li>{/if}
        {#each got.Chests as c}<li>{c}</li>{/each}
        {#each got.Keepsakes as k}<li class="keep">{k}</li>{/each}
      </ul>
      <button class="link" onclick={() => (got = null)}>OK</button>
    </div>
  {/if}
  {#if failed}<p class="error small">The claim did not go through. Try again in a moment.</p>{/if}

  <div class="road-wrap">
    <ol class="road" bind:this={road} style:--fill="{fill}%" data-testid="chronicle-track">
      {#each view.Track as level (level.Level)}
        {@const m = main(level)}
        {@const keep = KEEPSAKE_LEVELS.has(level.Level) && isKeepsake(m)}
        <li class="step {level.State.toLowerCase()}" class:keep data-level={level.Level} data-state={level.State}>
          <span class="node" aria-hidden="true">{level.Level}</span>
          <div class="card">
            <div class="art">{@render rewardArt(m, keep)}</div>
            <div class="what">
              <span class="name">{@render rewardText(m)}</span>
              {#each extras(level) as x}<span class="extra">+ {@render rewardText(x)}</span>{/each}
              {#if keep && m.Kind === 'Pet' && view.PetBonus}<span class="dim tiny">{view.PetBonus}</span>{/if}
            </div>
            {#if level.State === 'Ready'}
              <button class="primary claim" onclick={() => claim(level.Level)} disabled={busy} data-testid="chronicle-claim-level">Claim</button>
            {:else if level.State === 'Claimed'}
              <span class="tick" aria-label="Claimed">&#10003;</span>
            {:else}
              <span class="lock dim tiny">Lv {level.Level}</span>
            {/if}
          </div>
        </li>
      {/each}
      <li class="step overflow" class:ready={view.OverflowReady > 0} data-testid="chronicle-overflow">
        <span class="node" aria-hidden="true">+</span>
        <div class="card">
          <div class="art"><img class="chest" src={LOOT_STILL.Gold} alt="" loading="lazy" decoding="async" /></div>
          <div class="what">
            <span class="name">Bonus chest</span>
            <span class="dim tiny">Past level 50, every 100 XP earns a Gold loot chest. {view.OverflowClaimed} taken this season.</span>
          </div>
          {#if view.OverflowReady > 0}
            <span class="count-pill">{view.OverflowReady}</span>
          {/if}
        </div>
      </li>
    </ol>
  </div>

  <div class="foot">
    <button class="primary" onclick={() => claim()} disabled={busy || view.Claimable === 0} data-testid="chronicle-claim-all">
      {view.Claimable > 0 ? `Claim all (${view.Claimable})` : 'Nothing to claim'}
    </button>
  </div>
{/if}

<style>
  .season {
    display: flex;
    align-items: center;
    gap: 0.75rem;
  }

  .season-name {
    flex: 1;
    min-width: 0;
    display: grid;
  }

  .season h3 {
    margin: 0;
  }

  .level-badge {
    flex-shrink: 0;
    width: 3.4rem;
    height: 3.4rem;
    border-radius: 50%;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    line-height: 1;
    color: var(--chr-ink);
    background: radial-gradient(circle at 35% 30%, var(--chr-hi), var(--chr-lo));
    border: 2px solid var(--chr-rim);
  }

  .level-badge .lv {
    font-size: 1.3rem;
    font-weight: 800;
  }

  .xp {
    position: relative;
    height: 1.25rem;
    margin: 0.55rem 0 0.25rem;
    border-radius: 0.65rem;
    background: var(--border);
    overflow: hidden;
  }

  .xp span {
    display: block;
    height: 100%;
    background: linear-gradient(90deg, var(--chr-lo), var(--chr-hi));
  }

  .xp em {
    position: absolute;
    inset: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    font-style: normal;
    font-size: 0.75rem;
    font-weight: 700;
    color: var(--chr-ink);
  }

  .how {
    margin: 0 0 0.5rem;
  }

  .got {
    border: 1px solid var(--accent);
    border-radius: 8px;
    padding: 0.5rem 0.7rem;
    margin-bottom: 0.5rem;
  }

  .got ul {
    margin: 0.3rem 0;
    padding-left: 1.1rem;
  }

  .got .keep {
    font-weight: 700;
    color: var(--accent);
  }

  .road-wrap {
    max-height: min(55vh, 34rem);
    overflow-y: auto;
    overscroll-behavior: contain;
    border: 1px solid var(--border);
    border-radius: 10px;
    padding: 0.4rem 0.4rem 0.4rem 0;
  }

  .road {
    position: relative;
    list-style: none;
    margin: 0;
    padding: 0;
  }

  /* The centre line: grey for the whole road, gold as far as the player has come. */
  .road::before,
  .road::after {
    content: '';
    position: absolute;
    left: 1.6rem;
    top: 0.8rem;
    width: 6px;
    margin-left: -3px;
    border-radius: 3px;
  }

  .road::before {
    bottom: 0.8rem;
    background: var(--border);
  }

  .road::after {
    height: calc((100% - 1.6rem) * var(--fill) / 100);
    background: linear-gradient(180deg, var(--chr-hi), var(--chr-lo));
  }

  .step {
    position: relative;
    display: flex;
    align-items: center;
    gap: 0.6rem;
    padding: 0.3rem 0;
  }

  .node {
    position: relative;
    z-index: 1;
    flex-shrink: 0;
    width: 2.2rem;
    height: 2.2rem;
    margin-left: 0.5rem;
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    font-weight: 800;
    font-size: 0.85rem;
    background: var(--bg-raised);
    border: 2px solid var(--border);
  }

  .step.ready .node,
  .step.claimed .node {
    color: var(--chr-ink);
    background: radial-gradient(circle at 35% 30%, var(--chr-hi), var(--chr-lo));
    border-color: var(--chr-rim);
  }

  .card {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: center;
    gap: 0.6rem;
    padding: 0.35rem 0.55rem;
    border: 1px solid var(--border);
    border-radius: 8px;
  }

  .step.ready .card {
    border-color: var(--accent);
    box-shadow: 0 0 0 1px var(--accent) inset;
  }

  .step.claimed .card {
    opacity: 0.55;
  }

  .step.locked .card {
    opacity: 0.85;
  }

  .step.keep .card {
    padding: 0.55rem;
    border-width: 2px;
    border-color: var(--chr-rim);
    background: linear-gradient(90deg, color-mix(in srgb, var(--chr-hi) 18%, transparent), transparent);
  }

  .art {
    flex-shrink: 0;
    width: 3rem;
    display: flex;
    justify-content: center;
  }

  .step.keep .art {
    width: 4.6rem;
  }

  .chest {
    width: 2.8rem;
    height: 2.8rem;
    object-fit: contain;
  }

  .portrait {
    width: 2.8rem;
    height: 2.8rem;
    border-radius: 50%;
    object-fit: cover;
  }

  .pet {
    width: 2.8rem;
    height: 2.8rem;
    object-fit: contain;
  }

  .portrait.big,
  .pet.big {
    width: 4.4rem;
    height: 4.4rem;
  }

  .frame-preview {
    position: relative;
    display: block;
    width: 2.8rem;
    height: 2.8rem;
    border-radius: 50%;
    background: var(--border);
  }

  .frame-preview.big {
    width: 4rem;
    height: 4rem;
  }

  .tickets {
    display: flex;
    gap: 1px;
  }

  .what {
    flex: 1;
    min-width: 0;
    display: grid;
    gap: 0.1rem;
  }

  .name {
    font-weight: 600;
  }

  .step.keep .name {
    font-size: 1.02rem;
  }

  .extra {
    font-size: 0.8rem;
  }

  .claim {
    flex-shrink: 0;
    min-height: 44px;
    min-width: 44px;
  }

  .tick {
    flex-shrink: 0;
    font-size: 1.2rem;
    color: var(--good);
    min-width: 2.4rem;
    text-align: center;
  }

  .lock {
    flex-shrink: 0;
    min-width: 2.4rem;
    text-align: center;
  }

  .count-pill {
    flex-shrink: 0;
    min-width: 1.6rem;
    padding: 0 0.4rem;
    border-radius: 0.8rem;
    text-align: center;
    font-weight: 700;
    color: #fff;
    background: #d63a2f;
  }

  .foot {
    display: flex;
    justify-content: flex-end;
    margin-top: 0.6rem;
  }

  .error {
    color: var(--danger);
  }

  .link {
    background: none;
    border: none;
    padding: 0;
    min-height: 44px;
    text-decoration: underline;
    cursor: pointer;
  }

  :global(:root) {
    --chr-ink: #3a2410;
    --chr-hi: #f6d98a;
    --chr-lo: #c8902f;
    --chr-rim: #7a5418;
  }
</style>
