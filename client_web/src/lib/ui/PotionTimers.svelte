<script lang="ts">
  import { playerState } from '../stores/game';
  import { contentQuery } from '../net/registry.svelte';
  import ItemIcon from './ItemIcon.svelte';
  import { potionInfo, formatCountdown } from './potions';

  // Modul: THE RUNNING POTIONS IN THE HEADER (herbs and potions, 2026-10-31).
  // One small icon and countdown per slot that has a potion running, and
  // nothing at all otherwise - the phone's strip under the header hides
  // itself with :empty, so an idle header stays as it was. Opens the shelves.
  let { onOpen }: { onOpen: () => void } = $props();

  const snap = $derived($playerState);
  const registry = $derived(contentQuery.data ?? null);

  const running = $derived(
    snap
      ? [
          [snap.ActiveOffensivePotionId, snap.OffensivePotionDurationMs],
          [snap.ActiveDefensivePotionId, snap.DefensivePotionDurationMs],
          [snap.ActiveGatheringPotionId, snap.GatheringPotionDurationMs],
        ]
          .filter(([id, ms]) => id > 0 && ms > 0)
          .map(([id, ms]) => {
            const baseId = registry?.items.get(id)?.BaseId ?? '';
            return { id, ms, baseId, info: potionInfo(baseId) };
          })
          .filter((p) => p.info !== null)
      : [],
  );
</script>

{#if running.length > 0}
  <button
    class="potion-timers"
    data-testid="potion-timers"
    title={running.map((p) => `${p.info!.title}: ${p.info!.row.effect(p.info!.magnitude)}`).join('\n')}
    aria-label="Running potions: {running.map((p) => `${p.info!.title}, ${formatCountdown(p.ms)} left`).join('; ')}"
    onclick={onOpen}
  >
    {#each running as potion (potion.id)}
      <span class="one">
        <ItemIcon baseItemId={potion.baseId} name={potion.info!.title} size="sm" />
        <span class="t">{formatCountdown(potion.ms)}</span>
      </span>
    {/each}
  </button>
{/if}

<style>
  /* Three running potions must fit a 320px phone: the countdowns wrap
     inside the chip rather than push the row past the screen edge. */
  .potion-timers {
    display: inline-flex;
    flex-wrap: wrap;
    align-items: center;
    max-width: 100%;
    min-width: 0;
    gap: 0.15rem 0.5rem;
    min-height: 44px;
    padding: 0 0.5rem;
    background: transparent;
    border: 1px solid transparent;
    font-size: 0.78rem;
  }
  .potion-timers:hover {
    border-color: var(--border);
  }
  .one {
    display: inline-flex;
    white-space: nowrap;
    align-items: center;
    gap: 0.2rem;
  }
  .t {
    font-variant-numeric: tabular-nums;
    font-weight: 700;
  }
</style>
