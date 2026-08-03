<script lang="ts">
  import { onMount } from 'svelte';
  import { playerState, visualState } from '../lib/stores/game';
  import { connection } from '../lib/net/connection';
  import { CommandType } from '../lib/net/protocol.generated';
  import { loadContent, type ContentRegistry, type GatheringNodeDefinition } from '../lib/net/content';
  import { PROFESSIONS, isGatheringActivity, HALT_REASONS } from '../lib/ui/slots';
  import { locationName, nodeLocation } from '../lib/ui/locations';
  import Bar from '../lib/ui/Bar.svelte';
  import SessionLoot from '../lib/ui/SessionLoot.svelte';

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

  // All four professions now carry their own mastery. Until this pass the
  // wire had two tracks and the server routed Fishing and Herbalism into
  // Mining, so fishing raised your mining level and Fishing could not be shown
  // at all.
  const MASTERY_TRACKS = [
    { id: 0, name: 'Woodcutting' },
    { id: 1, name: 'Mining' },
    { id: 2, name: 'Fishing' },
    { id: 3, name: 'Herbalism' },
  ];

  function masteryLevelOf(professionId: number): number {
    if (!snap) return 0;
    if (professionId === 0) return snap.WoodcuttingMasteryLevel;
    if (professionId === 1) return snap.MiningMasteryLevel;
    if (professionId === 2) return snap.FishingMasteryLevel;
    return snap.HerbalismMasteryLevel;
  }

  function masteryXpOf(professionId: number): number {
    if (!snap) return 0;
    if (professionId === 0) return snap.WoodcuttingMasteryXp;
    if (professionId === 1) return snap.MiningMasteryXp;
    if (professionId === 2) return snap.FishingMasteryXp;
    return snap.HerbalismMasteryXp;
  }

  const masteryFor = $derived((professionId: number) => {
    if (!snap) return null;
    const track = MASTERY_TRACKS.find((t) => t.id === professionId) ?? MASTERY_TRACKS[3];
    return { name: track.name, level: masteryLevelOf(professionId), xp: masteryXpOf(professionId) };
  });

  function deploy(node: GatheringNodeDefinition) {
    connection.send({ Command: CommandType.ChangeActivity, TargetId: node.ActivityId });
  }

  function stop() {
    connection.send({ Command: CommandType.ChangeActivity, TargetId: 0 });
  }

  // Modul: BaseTickThreshold is NOT what a gather actually costs.
  //
  // SimulationEngine computes the real threshold as
  //   BaseTickThreshold - masteryLevel*2 - CachedCurrentToolTier
  // with a hard floor of 2 ticks. This screen used to print the base value, so
  // a player with mastery 20 and a tier 5 tool was told a node took 12 seconds
  // when it took 7.5 - the rate was wrong for everyone except a brand new
  // account, and wrong in the direction that hides progress.
  //
  // The logistics achievement's percentage reduction is applied server-side
  // after these and is not carried on any packet, so the number below is a
  // floor on the real speed rather than an exact figure - stated in the UI
  // rather than quietly presented as exact.
  const MIN_GATHER_TICKS = 2;

  function effectiveTicks(node: GatheringNodeDefinition): number {
    if (!snap) return node.BaseTickThreshold;
    const mastery = masteryLevelOf(node.ProfessionType);
    const reduced = node.BaseTickThreshold - mastery * 2 - snap.CachedCurrentToolTier;
    return Math.max(MIN_GATHER_TICKS, reduced);
  }

  function secondsPerUnit(node: GatheringNodeDefinition): string {
    return (effectiveTicks(node) / 10).toFixed(1);
  }

  function isFloored(node: GatheringNodeDefinition): boolean {
    return effectiveTicks(node) === MIN_GATHER_TICKS;
  }

  const toolTier = $derived(snap?.CachedCurrentToolTier ?? 0);

  // Modul: a node belongs to a PLACE, and you can only work places you have
  // been. Gathering used to be completely open, so a brand new character could
  // work the Abyssal Breach on their first minute and the five locations were
  // decoration. One kill in a location is what opens it.
  const reached = $derived(snap?.HighestLocationReached ?? 1);

  function isLocked(node: GatheringNodeDefinition): boolean {
    return nodeLocation(node.ActivityId) > reached;
  }

  // Monoliths are a GUILD upgrade, so they can move without the player doing
  // anything. The yield bonus is a flat percent, capped at 50 server-side.
  const MONOLITH_CAP_PCT = 50;
  const woodMonolith = $derived(snap?.CachedWoodcuttingMonolithLevel ?? 0);
  const mineMonolith = $derived(snap?.CachedMiningMonolithLevel ?? 0);

  function monolithFor(professionId: number): number {
    return professionId === 0 ? woodMonolith : mineMonolith;
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
            Working {locationName(nodeLocation(activeActivity))}
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
        Each profession levels on its own. Higher mastery cuts two ticks per
        gather off that profession's nodes.
      </p>
      <dl class="mastery">
        {#each MASTERY_TRACKS as track (track.id)}
          <div>
            <dt>{track.name}</dt>
            <dd>
              level {masteryLevelOf(track.id)} &middot;
              {masteryXpOf(track.id).toLocaleString()} xp
            </dd>
          </div>
        {/each}
      </dl>

      <h3>Speed and yield</h3>
      <dl class="mastery">
        <div>
          <dt>Tool tier</dt>
          <dd class="bonus">-{toolTier} ticks per gather</dd>
        </div>
        <div>
          <dt>Mastery</dt>
          <dd class="bonus">
            -{masteryLevelOf(0) * 2} wood, -{masteryLevelOf(1) * 2} mining,
            -{masteryLevelOf(2) * 2} fish, -{masteryLevelOf(3) * 2} herb
          </dd>
        </div>
        <div>
          <dt>Woodcutting monolith</dt>
          <dd class="bonus">
            +{Math.min(woodMonolith, MONOLITH_CAP_PCT)}% yield
            {#if woodMonolith > MONOLITH_CAP_PCT}<span class="dim tiny">(capped)</span>{/if}
          </dd>
        </div>
        <div>
          <dt>Mining monolith</dt>
          <dd class="bonus">
            +{Math.min(mineMonolith, MONOLITH_CAP_PCT)}% yield
            {#if mineMonolith > MONOLITH_CAP_PCT}<span class="dim tiny">(capped)</span>{/if}
          </dd>
        </div>
      </dl>
      <p class="dim small">
        Monoliths are a guild upgrade, so these move without you doing anything.
        The rates in the node lists below already include your tool and mastery.
      </p>
    </section>
  {/if}

  <div class="professions">
    <section class="panel">
      <h2>Hauled this session</h2>
      <p class="dim small">
        What this character has actually pulled out of the ground and the water.
        Combat has had this feed since the loot events landed; gathering showed
        nothing, so a working node and a broken one looked identical.
      </p>
      <SessionLoot {registry} />
    </section>

    {#each byProfession as profession}
      <section class="panel">
        <h2>{profession.name}</h2>
        <p class="dim small">
          {#if masteryFor(profession.id)}
            &middot; {masteryFor(profession.id)?.name} mastery {masteryFor(profession.id)?.level}
          {/if}
          {#if monolithFor(profession.id) > 0}
            &middot; <span class="bonus">+{Math.min(monolithFor(profession.id), MONOLITH_CAP_PCT)}% yield</span>
          {/if}
        </p>

        <ul class="nodes">
          {#each profession.nodes as node (node.ActivityId)}
            {@const locked = isLocked(node)}
            <li class:current={activeActivity === node.ActivityId} class:locked>
              <span class="place">{locationName(nodeLocation(node.ActivityId))}</span>
              <span class="dim tiny" title={`Base ${(node.BaseTickThreshold / 10).toFixed(1)}s, reduced by mastery and tool tier`}>
                {secondsPerUnit(node)}s / unit{#if isFloored(node)}<span class="floored"> (floor)</span>{/if}
              </span>
              <span class="dim tiny">{node.BaseMasteryXpReward} xp</span>
              {#if locked}
                <span class="dim tiny lock">Fight here first</span>
              {:else}
                <button
                  class="tiny-btn"
                  disabled={activeActivity === node.ActivityId}
                  onclick={() => deploy(node)}
                >
                  {activeActivity === node.ActivityId ? 'Working' : 'Gather'}
                </button>
              {/if}
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

  .place {
    font-weight: 600;
  }

  li.locked {
    opacity: 0.5;
  }

  .lock {
    font-style: italic;
  }

  .bonus {
    color: var(--good);
    font-variant-numeric: tabular-nums;
  }

  /* A node already at the two-tick floor gains nothing from more mastery, and
     that is worth knowing before spending on it. */
  .floored {
    color: var(--rarity-12);
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.45rem;
  }
</style>
