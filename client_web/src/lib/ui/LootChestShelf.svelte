<script lang="ts">
  // Modul: LOOT CHESTS ON THE CHEST SCREEN (CHESTS_DUNGEONS_DIAMONDS.md
  // section 1). Earned only - the Deep's new weekly floors from 10 and a
  // regional boss's first clear - and they stack, so the shelf shows a count
  // per tier and opens one or five. The server rolls and grants; this shows
  // what came out. The odds are the server's own lines, published here as the
  // cosmetic chests publish theirs.
  //
  // The opening is ChestStage: three automatic shakes while the request is in
  // flight, then the burst once the answer is back - the chest never opens on
  // a result the server has not given.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { queryKeys, fetchLootChests, openLootChests, type OpenedLootChest } from '../net/rest';
  import { prettifyBaseId } from '../net/content';
  import { formatGold, formatNumber } from './format';
  import ChestStage from './ChestStage.svelte';

  const client = useQueryClient();
  const shelf = createQuery(() => ({ queryKey: queryKeys.lootChests, queryFn: fetchLootChests }));

  let stageTier = $state(0);
  let taps = $state(0);
  let opened = $state(false);
  let results = $state<OpenedLootChest[]>([]);
  let error = $state<string | null>(null);
  let showOdds = $state<number | null>(null);

  const owned = $derived((shelf.data?.Chests ?? []).filter((c) => c.Count > 0));

  async function open(tier: number, count: number): Promise<void> {
    error = null;
    results = [];
    opened = false;
    taps = 0;
    stageTier = tier;
    const shakes = (async () => {
      for (let i = 1; i <= 3; i++) {
        taps = i;
        await new Promise((r) => setTimeout(r, 520));
      }
    })();
    try {
      const [res] = await Promise.all([openLootChests(tier, count), shakes]);
      if (res.Result !== 'Ok') {
        error = 'There was no chest of that kind left to open.';
        stageTier = 0;
      } else {
        results = res.Opened;
        opened = true;
      }
      client.setQueryData(queryKeys.lootChests, (old: typeof shelf.data) => (old ? { ...old, Chests: res.Chests } : old));
      void client.invalidateQueries({ queryKey: queryKeys.inventory });
    } catch {
      error = 'The chest would not open. Try again in a moment.';
      stageTier = 0;
    }
  }

  function close(): void {
    stageTier = 0;
    results = [];
  }

  const totalGold = $derived(results.reduce((a, r) => a + r.Gold, 0));
  const totalDiamonds = $derived(results.reduce((a, r) => a + r.Diamonds, 0));
  const materials = $derived.by(() => {
    const sum = new Map<string, number>();
    for (const r of results) for (const m of r.Materials) sum.set(m.ItemId, (sum.get(m.ItemId) ?? 0) + m.Quantity);
    return [...sum.entries()];
  });
</script>

{#if owned.length > 0 || stageTier > 0}
  <section class="shelf panel" data-testid="loot-chest-shelf" aria-label="Loot chests">
    <h2>Loot chests</h2>
    <ul class="tiers">
      {#each owned as chest (chest.Tier)}
        <li class="tier" data-testid={`loot-chest-${chest.Tier}`}>
          <img src={`/chest/stage/loot-${['', 'common', 'rare', 'epic', 'legendary'][chest.Tier]}-closed.webp`} alt="" />
          <div class="meta">
            <strong>{chest.Name}</strong>
            <span class="dim">×{chest.Count}</span>
          </div>
          <div class="actions">
            <button class="primary" disabled={stageTier > 0} onclick={() => open(chest.Tier, 1)} data-testid={`loot-chest-open-${chest.Tier}`}>Open</button>
            {#if chest.Count > 1}
              <button disabled={stageTier > 0} onclick={() => open(chest.Tier, Math.min(5, chest.Count))}>Open ×{Math.min(5, chest.Count)}</button>
            {/if}
            <button class="link" onclick={() => (showOdds = showOdds === chest.Tier ? null : chest.Tier)}>Odds</button>
          </div>
          {#if showOdds === chest.Tier}
            <ul class="odds dim tiny">
              {#each chest.Odds as line}<li>{line}</li>{/each}
            </ul>
          {/if}
        </li>
      {/each}
    </ul>
    {#if error}<p class="error" role="alert">{error}</p>{/if}
  </section>
{/if}

{#if stageTier > 0}
  <div class="overlay" role="dialog" aria-modal="true" aria-label="Opening a loot chest">
    <div class="stage-wrap">
      <ChestStage art="loot" rarity={stageTier} {taps} {opened} />
    </div>
    {#if opened}
      <div class="reveal panel" data-testid="loot-chest-reveal">
        <h3>{results.length > 1 ? `${results.length} chests opened` : 'Chest opened'}</h3>
        <ul>
          {#if totalGold > 0}<li>{formatGold(totalGold)} gold</li>{/if}
          {#if totalDiamonds > 0}<li>{formatNumber(totalDiamonds)} diamonds</li>{/if}
          {#each materials as [item, qty]}<li>{formatNumber(qty)} × {prettifyBaseId(item)}</li>{/each}
          {#each results.filter((r) => r.Piece) as r}
            <li class="piece">{r.Piece!.RarityName} {prettifyBaseId(r.Piece!.BaseItemId)}</li>
          {/each}
        </ul>
        <button class="primary" onclick={close} data-testid="loot-chest-collect">Collect</button>
      </div>
    {/if}
  </div>
{/if}

<style>
  .shelf h2 { margin: 0 0 8px; font-size: 1rem; }
  .tiers { list-style: none; margin: 0; padding: 0; display: grid; gap: 8px; }
  .tier { display: grid; grid-template-columns: 56px 1fr auto; align-items: center; gap: 10px; }
  .tier img { width: 56px; height: 56px; object-fit: contain; }
  .meta { display: flex; flex-direction: column; }
  .actions { display: flex; gap: 6px; flex-wrap: wrap; justify-content: flex-end; }
  .actions button { min-height: 44px; }
  .odds { grid-column: 1 / -1; margin: 0; padding-left: 18px; }
  .overlay {
    position: fixed;
    inset: 0;
    z-index: 60;
    background: rgba(8, 8, 12, 0.82);
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 16px;
    padding: 16px;
  }
  .stage-wrap { width: min(80vw, 380px); }
  .reveal { width: min(92vw, 420px); text-align: center; }
  .reveal ul { list-style: none; padding: 0; margin: 8px 0 12px; display: grid; gap: 4px; }
  .reveal .piece { font-weight: 600; }
  .reveal button { min-height: 44px; min-width: 140px; }
</style>
