<script lang="ts">
  import { onMount } from 'svelte';
  import { playerState, visualState } from '../lib/stores/game';
  import { connection } from '../lib/net/connection';
  import { CommandType } from '../lib/net/protocol.generated';
  import { loadContent, type ContentRegistry, type GatheringNodeDefinition } from '../lib/net/content';
  import { PROFESSIONS, isGatheringActivity, HALT_REASONS } from '../lib/ui/slots';
  import Bar from '../lib/ui/Bar.svelte';

  let registry = $state<ContentRegistry | null>(null);
  let contentError = $state('');

  onMount(async () => {
    try {
      registry = await loadContent();
    } catch (err) {
      contentError = err instanceof Error ? err.message : String(err);
    }
  });

  const snap = $derived($playerState);
  const visual = $derived($visualState);
  const activeActivity = $derived(snap ? Number(snap.ActiveActivityId) : 0);
  const isGathering = $derived(isGatheringActivity(activeActivity));

  const byProfession = $derived.by(() => {
    const nodes = registry?.gatheringNodes ?? [];
    return PROFESSIONS.map((profession) => ({
      ...profession,
      nodes: nodes
        .filter((n) => n.ProfessionType === profession.id)
        .sort((a, b) => a.ActivityId - b.ActivityId),
    }));
  });

  // Modul: only Woodcutting and Mining have mastery on the wire.
  // SimulationEngine reads `ProfessionType == 0 ? Woodcutting : Mining`, so
  // Fishing and Herbalism nodes accrue their XP into the Mining track. That is
  // the server's behaviour, not an oversight in this screen - showing a
  // Fishing mastery level the wire does not carry would be inventing one.
  const masteryFor = $derived((professionId: number) => {
    if (!snap) return null;
    if (professionId === 0) {
      return { name: 'Woodcutting', level: snap.WoodcuttingMasteryLevel, xp: snap.WoodcuttingMasteryXp };
    }
    return { name: 'Mining', level: snap.MiningMasteryLevel, xp: snap.MiningMasteryXp };
  });

  function deploy(node: GatheringNodeDefinition) {
    connection.send({ Command: CommandType.ChangeActivity, TargetId: node.ActivityId });
  }

  function stop() {
    connection.send({ Command: CommandType.ChangeActivity, TargetId: 0 });
  }

  /** BaseTickThreshold is in 10 Hz ticks. */
  function secondsPerUnit(node: GatheringNodeDefinition): string {
    return (node.BaseTickThreshold / 10).toFixed(1);
  }
</script>

<div class="wrap">
  {#if contentError}
    <p class="err pad">Content failed to load: {contentError}</p>
  {/if}

  {#if snap}
    <section class="panel status">
      <div>
        <h2>Gathering</h2>
        {#if isGathering}
          <p class="active">
            Working node {activeActivity}
            {#if snap.RequiredProgressTicks > 0}
              &middot; {Math.floor(
                ((visual?.CurrentProgressTicks ?? snap.CurrentProgressTicks) /
                  snap.RequiredProgressTicks) *
                  100,
              )}%
            {/if}
          </p>
        {:else if activeActivity > 0}
          <p class="dim">Currently in combat. Deploying to a node will move this character.</p>
        {:else}
          <p class="dim">Idle.</p>
        {/if}
        {#if snap.ActivityHaltReason !== 0}
          <p class="halt">{HALT_REASONS[snap.ActivityHaltReason]}</p>
        {/if}
      </div>

      {#if snap.RequiredProgressTicks > 0 && isGathering}
        <div class="progress">
          <Bar
            value={visual?.CurrentProgressTicks ?? snap.CurrentProgressTicks}
            max={snap.RequiredProgressTicks}
            color="var(--accent)"
          />
        </div>
      {/if}

      {#if isGathering}
        <button onclick={stop}>Stop gathering</button>
      {/if}
    </section>

    <section class="panel">
      <h3>Mastery</h3>
      <p class="dim small">
        Only two mastery tracks exist on the wire. Fishing and Herbalism accrue
        into Mining server-side, so their levels are not shown separately.
      </p>
      <dl class="mastery">
        <div>
          <dt>Woodcutting</dt>
          <dd>level {snap.WoodcuttingMasteryLevel} &middot; {snap.WoodcuttingMasteryXp.toLocaleString()} xp</dd>
        </div>
        <div>
          <dt>Mining</dt>
          <dd>level {snap.MiningMasteryLevel} &middot; {snap.MiningMasteryXp.toLocaleString()} xp</dd>
        </div>
      </dl>
    </section>
  {/if}

  <div class="professions">
    {#each byProfession as profession}
      <section class="panel">
        <h2>{profession.name}</h2>
        <p class="dim small">
          Activity ids {profession.band}-{profession.band + 999}
          {#if masteryFor(profession.id)}
            &middot; {masteryFor(profession.id)?.name} mastery {masteryFor(profession.id)?.level}
          {/if}
        </p>

        <ul class="nodes">
          {#each profession.nodes as node (node.ActivityId)}
            <li class:current={activeActivity === node.ActivityId}>
              <span class="tier">T{node.ActivityId % 1000}</span>
              <span class="dim tiny">{secondsPerUnit(node)}s / unit</span>
              <span class="dim tiny">{node.BaseMasteryXpReward} xp</span>
              <button
                class="tiny-btn"
                disabled={activeActivity === node.ActivityId}
                onclick={() => deploy(node)}
              >
                {activeActivity === node.ActivityId ? 'Working' : 'Gather'}
              </button>
            </li>
          {/each}
        </ul>
      </section>
    {/each}
  </div>

  {#if registry && registry.gatheringNodes.length === 0}
    <p class="dim pad">No gathering nodes in the content files.</p>
  {/if}
</div>

<style>
  .wrap {
    padding: 1rem;
    display: grid;
    gap: 1rem;
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
  }

  .status {
    display: grid;
    gap: 0.7rem;
  }

  .professions {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(16rem, 1fr));
    gap: 1rem;
    align-items: start;
  }

  h2 {
    margin: 0 0 0.35rem;
    font-size: 1.05rem;
  }

  h3 {
    margin: 0 0 0.35rem;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.8rem;
    margin: 0 0 0.6rem;
  }
  .tiny {
    font-size: 0.72rem;
  }
  .pad {
    padding: 1rem;
  }
  .err {
    color: var(--danger);
  }

  .active {
    margin: 0;
    color: var(--good);
  }

  .halt {
    margin: 0.4rem 0 0;
    padding: 0.4rem 0.6rem;
    background: rgba(224, 85, 63, 0.12);
    border-left: 3px solid var(--danger);
    border-radius: 4px;
    font-size: 0.85rem;
  }

  .mastery {
    display: grid;
    gap: 0.3rem;
    margin: 0;
  }

  .mastery div {
    display: flex;
    justify-content: space-between;
    gap: 1rem;
    font-size: 0.85rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.25rem;
  }

  dt {
    color: var(--text-dim);
  }

  dd {
    margin: 0;
    font-variant-numeric: tabular-nums;
  }

  .nodes {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  .nodes li {
    display: grid;
    grid-template-columns: 2.4rem 1fr auto auto;
    gap: 0.5rem;
    align-items: center;
    font-size: 0.82rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.25rem;
  }

  .nodes li.current {
    background: rgba(74, 163, 223, 0.08);
  }

  .tier {
    font-weight: 700;
    font-variant-numeric: tabular-nums;
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.45rem;
  }
</style>
