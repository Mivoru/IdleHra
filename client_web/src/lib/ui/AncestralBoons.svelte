<script lang="ts">
  // Modul: THE ANCESTRAL BOONS (2026-11-13) - what Legacy Shards buy.
  //
  // Every rebirth paid shards and nothing in the client could spend them: the
  // server's three perks (XP, gold, damage) were wired into the kill and the
  // offline window but had no button, so 40,000-55,000 shards sat on the three
  // accounts that had rebirthed. Names, effects, ranks, caps and prices all
  // come from the rebirth preview (BoonView on the server) - nothing here
  // knows a price, so this cannot quote one the purchase does not charge.
  import { useQueryClient } from '@tanstack/svelte-query';
  import { queryKeys, type BoonView } from '../net/rest';
  import { purchaseLegacyUnlock } from '../net/commands';
  import { pushLocalNotice } from '../stores/game';
  import { formatNumber } from './format';

  interface Props {
    boons: readonly BoonView[];
    shards: number;
  }

  const { boons, shards }: Props = $props();
  const client = useQueryClient();

  let pending = $state<number | null>(null);

  function buy(boon: BoonView, ranks = 1) {
    if (pending !== null || boon.NextCost < 0 || boon.NextCost > shards) return;
    const outcome = purchaseLegacyUnlock(boon.UnlockId, ranks);
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    pending = boon.UnlockId;
    // The command result (73/74/75) says what happened; the preview re-reads
    // the new rank, price and balance once the purchase has had time to land.
    setTimeout(() => {
      pending = null;
      client.invalidateQueries({ queryKey: queryKeys.rebirthPreview });
    }, 900);
  }
</script>

<section class="boons" data-testid="ancestral-boons">
  <h3>Ancestral Boons</h3>
  <p class="dim small">
    Every rebirth pays <strong>Legacy Shards</strong> - the level the life reached,
    squared and halved. Spend them here on bonuses that last for good, through
    every rebirth. You hold <strong data-testid="boons-shards">{formatNumber(shards)}</strong> shards.
  </p>
  <ul>
    {#each boons as boon (boon.UnlockId)}
      {@const maxed = boon.NextCost < 0}
      {@const affordable = !maxed && boon.NextCost <= shards}
      <li data-testid="boon-row">
        <div class="head">
          <strong>{boon.Name}</strong>
          <span class="rank" class:maxed>+{boon.Rank}% <span class="dim">of {boon.MaxRank}%</span></span>
        </div>
        <div class="bar" aria-hidden="true"><span style:width={`${(boon.Rank / Math.max(1, boon.MaxRank)) * 100}%`}></span></div>
        <div class="foot">
          <span class="dim tiny">{boon.Effect}</span>
          {#if maxed}
            <span class="dim tiny">complete</span>
          {:else}
            <button
              type="button"
              data-testid="boon-buy"
              disabled={!affordable || pending !== null}
              onclick={() => buy(boon)}
            >
              {pending === boon.UnlockId ? 'Buying…' : `+1% for ${formatNumber(boon.NextCost)}`}
            </button>
            <!-- Several ranks in one command (2026-11-13): sixty single taps was
                 the macro detector's rhythm. The count and price are the
                 server's (BoonView.AffordableRanks / AffordableCost). -->
            {#if (boon.AffordableRanks ?? 0) > 1}
              <button
                type="button"
                data-testid="boon-buy-all"
                disabled={pending !== null}
                onclick={() => buy(boon, boon.AffordableRanks ?? 1)}
              >
                +{boon.AffordableRanks}% for {formatNumber(boon.AffordableCost ?? 0)}
              </button>
            {/if}
          {/if}
        </div>
      </li>
    {/each}
  </ul>
</section>

<style>
  .boons {
    display: grid;
    gap: 0.4rem;
  }
  h3 {
    margin: 0.6rem 0 0;
    font-size: 1.05rem;
  }
  ul {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.5rem;
  }
  li {
    border: 1px solid var(--border, rgba(255, 255, 255, 0.12));
    border-radius: 8px;
    padding: 0.5rem 0.75rem;
    display: grid;
    gap: 0.3rem;
  }
  .head,
  .foot {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
  }
  .rank.maxed {
    color: var(--accent, #d4a64a);
  }
  .bar {
    height: 6px;
    border-radius: 3px;
    background: var(--border, rgba(127, 127, 127, 0.25));
    overflow: hidden;
  }
  .bar span {
    display: block;
    height: 100%;
    background: var(--accent, #d4a64a);
  }
  button {
    min-height: 44px;
    padding: 0 0.9rem;
  }
</style>
