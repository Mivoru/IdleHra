<script lang="ts">
  // The Rebirth Trials and the Ancestral Boons, in the Wiki, from the server's
  // own rebirth preview (TrialView / BoonView). Written out by hand they would
  // be one more copy of numbers that already live in RebirthTrials and
  // LegacyPerkResolver - the drift this codebase keeps paying for.
  import { createQuery } from '@tanstack/svelte-query';
  import { queryKeys, fetchRebirthPreview } from '../net/rest';
  import Skeleton from './Skeleton.svelte';

  const preview = createQuery(() => ({ queryKey: queryKeys.rebirthPreview, queryFn: fetchRebirthPreview }));
  const trials = $derived(preview.data?.Trials ?? []);
  const boons = $derived(preview.data?.Boons ?? []);
</script>

{#if preview.isPending}
  <Skeleton rows={4} />
{:else if preview.isError || !preview.data}
  <p class="dim small">Sign in to see the trials and boons.</p>
{:else}
  <h4>Rebirth Trials</h4>
  <p class="dim small">
    Chosen on the Rebirth screen when you are reborn. The rule holds for that whole life; the rebirth that ends the
    life finishes the trial and pays its reward for good, once, with a title.
  </p>
  <div class="scroll">
    <table data-testid="wiki-trials">
      <thead><tr><th>Trial</th><th>The rule</th><th>The reward</th></tr></thead>
      <tbody>
        {#each trials as t (t.Id)}
          <tr><td><strong>{t.Name}</strong></td><td>{t.Rule}</td><td>{t.Reward}</td></tr>
        {/each}
      </tbody>
    </table>
  </div>

  <h4>Ancestral Boons</h4>
  <p class="dim small">
    Bought with Legacy Shards on the Rebirth screen and kept through every rebirth. A second button buys as many
    ranks as your shards cover at once.
  </p>
  <div class="scroll">
    <table data-testid="wiki-boons">
      <thead><tr><th>Boon</th><th>Each rank</th><th class="num">Ranks</th></tr></thead>
      <tbody>
        {#each boons as b (b.UnlockId)}
          <tr><td><strong>{b.Name}</strong></td><td>{b.Effect}</td><td class="num">{b.MaxRank}</td></tr>
        {/each}
      </tbody>
    </table>
  </div>
{/if}
