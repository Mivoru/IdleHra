<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { authedGet } from '../net/auth';
  import ItemIcon from './ItemIcon.svelte';
  import { prettifyBaseId } from '../net/content';

  let { monsterId }: { monsterId: number } = $props();

  interface MonsterLootEntry {
    ItemId: number;
    BaseItemId: string;
    ChancePct: number;
    MinQuantity: number;
    MaxQuantity: number;
    IsEquipment: boolean;
  }

  const loot = createQuery(() => ({
    queryKey: ['monsterLoot', monsterId],
    queryFn: () => authedGet<MonsterLootEntry[]>(`/api/v1/monsters/loot?monsterId=${monsterId}`),
    staleTime: 60000 * 60, // 1 hour
  }));
</script>

<div class="loot-container">
  {#if loot.isPending}
    <p class="dim tiny">Loading drops...</p>
  {:else if loot.isError}
    <p class="dim tiny" style="color: var(--danger)">Failed to load drops.</p>
  {:else if loot.data && loot.data.length > 0}
    <ul class="drop-list">
      {#each loot.data as drop (drop.ItemId)}
        <li>
          <ItemIcon baseItemId={drop.BaseItemId} name={prettifyBaseId(drop.BaseItemId)} size="sm" />
          <div class="drop-info">
            <span class="name">{prettifyBaseId(drop.BaseItemId)}</span>
            <span class="chance dim tiny">
              {drop.ChancePct.toFixed(2)}%
              {#if drop.MinQuantity !== drop.MaxQuantity}
                (x{drop.MinQuantity}-{drop.MaxQuantity})
              {:else if drop.MinQuantity > 1}
                (x{drop.MinQuantity})
              {/if}
              {#if drop.IsEquipment}
                <span class="eq-badge">Equipment</span>
              {/if}
            </span>
          </div>
        </li>
      {/each}
    </ul>
  {:else}
    <p class="dim tiny">No drops.</p>
  {/if}
</div>

<style>
  .loot-container {
    margin-top: 0.5rem;
    padding-top: 0.5rem;
    border-top: 1px solid rgba(255,255,255,0.05);
  }
  .drop-list {
    list-style: none;
    padding: 0;
    margin: 0;
    display: grid;
    gap: 0.5rem;
  }
  .drop-list li {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }
  .drop-info {
    display: flex;
    flex-direction: column;
  }
  .name {
    font-size: 0.85rem;
  }
  .chance {
    font-variant-numeric: tabular-nums;
  }
  .eq-badge {
    color: var(--good);
    border: 1px solid currentColor;
    border-radius: 3px;
    padding: 0 0.2rem;
    margin-left: 0.3rem;
  }
</style>
