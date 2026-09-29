<script lang="ts">
  // Task 57: THE COLLECTION LOG. Every catalogued piece - fifteen per region -
  // at the best rarity this account has EVER owned, beside the region's codex.
  // "Ever", because the Chest is sold and fused away all the time and a log
  // that forgot a Mythic the moment it was melted down would be a list of the
  // Chest, not a collection. The server keeps the high-water mark; this draws it.
  import { createQuery } from '@tanstack/svelte-query';
  import { queryKeys, fetchCollection } from '../net/rest';
  import { prettifyBaseId } from '../net/content';
  import { rarityColor, rarityName } from './rarity';
  import { locationName } from './locations';
  import ItemIcon from './ItemIcon.svelte';
  import Skeleton from './Skeleton.svelte';

  const collection = createQuery(() => ({ queryKey: queryKeys.collection, queryFn: fetchCollection, staleTime: 30_000 }));
  const view = $derived(collection.data);
</script>

<section class="panel collection" data-testid="collection-log">
  <header>
    <h3>Collection</h3>
    {#if view}
      <p class="dim small">
        <strong class="headline">{view.Percent}%</strong>
        &middot; {view.PiecesOwned} / {view.PiecesTotal} pieces found &middot;
        {view.MonstersRecorded} / {view.MonstersTotal} monsters in the codex
      </p>
    {/if}
  </header>

  {#if collection.isPending}
    <Skeleton rows={5} />
  {:else if collection.isError}
    <p class="warn-line">The collection could not be read.</p>
  {:else if view}
    {#each view.Regions as region (region.Region)}
      <div class="region" data-region={region.Region}>
        <div class="rhead">
          <strong>{locationName(region.Region)}</strong>
          <span class="dim tiny">
            {region.PiecesOwned} / {region.PiecesTotal} pieces &middot;
            codex {region.MonstersRecorded} / {region.MonstersTotal}
          </span>
        </div>
        <div class="meter" role="img" aria-label={`Rarity collected ${region.RarityPercent}%`}>
          <span style={`width: ${region.RarityPercent}%`}></span>
        </div>
        <ul class="pieces">
          {#each region.Pieces as piece (piece.BaseItemId)}
            <li
              class:missing={piece.BestTier === 0}
              title={piece.BestTier > 0
                ? `${prettifyBaseId(piece.BaseItemId)} - best ever: ${rarityName(piece.BestTier)}`
                : `${prettifyBaseId(piece.BaseItemId)} - not found yet`}
            >
              <ItemIcon
                baseItemId={piece.BaseItemId}
                name={prettifyBaseId(piece.BaseItemId)}
                qualityTier={piece.BestTier > 0 ? piece.BestTier : undefined}
                size="sm"
              />
              {#if piece.BestTier > 0}
                <span class="tier" style={`color: ${rarityColor(piece.BestTier)}`}>{piece.BestTier}</span>
              {/if}
            </li>
          {/each}
        </ul>
      </div>
    {/each}
    <p class="dim tiny">
      The number on a piece is the best rarity you have ever owned of it (1-14). The bar is how far up the
      rarity ladder the region's fifteen pieces are, together.
    </p>
  {/if}
</section>

<style>
  .collection {
    display: grid;
    gap: 0.6rem;
  }

  header h3 {
    margin: 0 0 0.1rem;
  }

  header p {
    margin: 0;
  }

  .headline {
    font-size: 1.1rem;
    color: var(--brass-lit);
  }

  .region {
    border-top: 1px solid var(--border);
    padding-top: 0.5rem;
    display: grid;
    gap: 0.3rem;
  }

  .rhead {
    display: flex;
    flex-wrap: wrap;
    justify-content: space-between;
    align-items: baseline;
    gap: 0.2rem 0.6rem;
  }

  .meter {
    height: 4px;
    background: var(--bg);
    border-radius: 2px;
    overflow: hidden;
  }

  .meter span {
    display: block;
    height: 100%;
    background: var(--brass);
  }

  .pieces {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(2.6rem, 1fr));
    gap: 0.3rem;
  }

  .pieces li {
    position: relative;
    display: flex;
    justify-content: center;
  }

  /* Not found yet: the silhouette is kept - knowing what exists is half of a
     collection - but it is plainly not yours. */
  .pieces li.missing {
    opacity: 0.3;
    filter: grayscale(1);
  }

  .tier {
    position: absolute;
    right: 0;
    bottom: -0.1rem;
    font-size: 0.65rem;
    font-weight: 700;
    text-shadow: 0 0 2px var(--bg-panel), 0 0 2px var(--bg-panel);
  }

  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.85rem;
  }
  .tiny {
    font-size: 0.72rem;
  }
  .warn-line {
    color: var(--warn);
    font-size: 0.85rem;
  }
</style>
