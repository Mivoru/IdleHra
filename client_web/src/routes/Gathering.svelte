<script lang="ts">
  import { onMount } from 'svelte';
  import { playerState, visualState, pushLocalNotice } from '../lib/stores/game';
  import { loadContent, type ContentRegistry, type GatheringNodeDefinition } from '../lib/net/content';
  import { PROFESSIONS, isGatheringActivity, HALT_REASONS } from '../lib/ui/slots';
  import { locationName, nodeLocation } from '../lib/ui/locations';
  import { assignCharacterActivity, EMPTY_GUID } from '../lib/net/commands';
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
    // Herbalism retired with the design list - see slots.ts.
  ];

  function masteryLevelOf(professionId: number): number {
    if (!snap) return 0;
    if (professionId === 0) return snap.WoodcuttingMasteryLevel;
    if (professionId === 1) return snap.MiningMasteryLevel;
    if (professionId === 2) return snap.FishingMasteryLevel;
    return 0;
  }

  function masteryXpOf(professionId: number): number {
    if (!snap) return 0;
    if (professionId === 0) return snap.WoodcuttingMasteryXp;
    if (professionId === 1) return snap.MiningMasteryXp;
    if (professionId === 2) return snap.FishingMasteryXp;
    return 0;
  }

  // Modul: `?? MASTERY_TRACKS[3]` was a crash waiting for a content change.
  // The array holds three entries since Herbalism retired, so index 3 is
  // undefined and `track.name` on the next line throws a TypeError that takes
  // the whole screen down. Nothing reaches it today - no authored node carries
  // profession 3 - which is exactly why it would have survived until the day
  // one did. Returns null instead, which every caller already handles.
  const masteryFor = $derived((professionId: number) => {
    if (!snap) return null;
    const track = MASTERY_TRACKS.find((t) => t.id === professionId);
    if (!track) return null;
    return { name: track.name, level: masteryLevelOf(professionId), xp: masteryXpOf(professionId) };
  });

  // Modul: THE COMMAND HAS TO NAME THE CHARACTER.
  //
  // Sending ChangeActivity with a bare TargetId takes SimulationEngine's
  // legacy single-character branch, which mutates the live payload and never
  // writes the characters row. So a deploy worked, looked right, survived for
  // the rest of the session - and vanished on reload, because hydration reads
  // the row, which still held whatever was persisted last. Assigning fishing
  // and pressing F5 put the character back on mining.
  //
  // Naming the character takes the branch that persists AND applies live.
  const activeCharacterId = $derived(snap?.Slot1_CharacterId ?? EMPTY_GUID);

  function deploy(node: GatheringNodeDefinition) {
    const outcome = assignCharacterActivity(activeCharacterId, node.ActivityId);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }

  function stop() {
    const outcome = assignCharacterActivity(activeCharacterId, 0);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
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

  // Modul: EVERYTHING IS A PERCENTAGE NOW - mirrors
  // GatheringToolEngine.ComputeRequiredTicks.
  //
  // Mastery used to SUBTRACT two ticks a level before any multiplier applied,
  // which on region 1's 30-tick node goes negative at mastery 15 and clamps to
  // the two-tick minimum. That is what put "0.2s / unit (floor)" on this
  // screen: the first two regions gathered instantly, and no tool, building or
  // affix could move a number already pinned to the bottom. A subtraction
  // cannot be balanced against a threshold it does not know about.
  //
  // The tool curve is geometric, 1.35x a tier, so the ladder from Birch to
  // Void Bark is worth about twenty times rather than the old threefold.
  const TOOL_SPEED_PCT = [0, 35, 82, 146, 232, 348, 505, 717, 1003, 1390, 1912];
  const MASTERY_SPEED_PCT_PER_LEVEL = 10;
  const VILLAGE_SPEED_PCT_PER_LEVEL = 5;

  function villageLevelFor(professionId: number): number {
    if (!snap) return 0;
    // Only woodcutting and mining have a production building; fishing gets no
    // acceleration rather than silently borrowing the Mine's.
    if (professionId === 0) return Number(snap.LumberjackLevel ?? 0);
    if (professionId === 1) return Number(snap.MineLevel ?? 0);
    return 0;
  }

  function effectiveTicks(node: GatheringNodeDefinition): number {
    if (!snap) return node.BaseTickThreshold;

    const tier = toolTierFor(node.ProfessionType);
    const speedPct =
      (TOOL_SPEED_PCT[Math.max(0, Math.min(tier, TOOL_SPEED_PCT.length - 1))] ?? 0) +
      masteryLevelOf(node.ProfessionType) * MASTERY_SPEED_PCT_PER_LEVEL +
      villageLevelFor(node.ProfessionType) * VILLAGE_SPEED_PCT_PER_LEVEL +
      Number(snap.ToolGatherSpeedPct ?? 0);

    const ticks = Math.floor((node.BaseTickThreshold * 100) / (100 + speedPct));
    return Math.max(MIN_GATHER_TICKS, ticks);
  }

  function secondsPerUnit(node: GatheringNodeDefinition): string {
    return (effectiveTicks(node) / 10).toFixed(1);
  }

  function isFloored(node: GatheringNodeDefinition): boolean {
    return effectiveTicks(node) === MIN_GATHER_TICKS;
  }

  // Modul: one tool per profession, and it has to be one you OWN. This read
  // CachedCurrentToolTier, which the server set from the forge building's
  // level - so the number moved when you upgraded a building and never when
  // you crafted an axe.
  function toolTierFor(professionId: number): number {
    if (!snap) return 0;
    if (professionId === 0) return snap.AxeToolTier;
    if (professionId === 1) return snap.PickaxeToolTier;
    return snap.RodToolTier;
  }

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
          <dt>Tools</dt>
          <dd class="bonus">
            axe {toolTierFor(0)} &middot; pickaxe {toolTierFor(1)} &middot; rod {toolTierFor(2)}
          </dd>
        </div>
        <div>
          <dt>Mastery</dt>
          <dd class="bonus">
            -{masteryLevelOf(0) * 2} wood, -{masteryLevelOf(1) * 2} mining,
            -{masteryLevelOf(2) * 2} fish
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
                <span
                  title="How long one unit takes at your mastery, tool and village bonuses. The server also applies a logistics bonus this screen cannot see, so the real speed is this or better."
                >{secondsPerUnit(node)}s / unit</span
                >{#if isFloored(node)}<span
                    class="floored"
                    title="This node cannot go any faster - 0.2s is the hard minimum for any gathering action. More mastery or a better tool will not help here; a higher-tier node will."
                  > (as fast as it goes)</span
                >{/if}
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
    /* Modul: the first column was 2.4rem - sized for "T1", not for
       "Whispering Woods". A two-word location name overflowed it and drew on
       top of the rate and the xp beside it. It is the widest column now, and
       the row's own height grows when a name wraps.

       Modul: the floors are minmax(0, ...) rather than 7rem and 5rem, because
       those two minimums plus the three gaps came to more than a narrow panel
       has. At the 900px breakpoint this panel is 245px wide and the row wanted
       254, so it hung 9px past the edge and the Gather button was sliced -
       found by clipping-check.mjs, which is the whole reason that script
       exists. A `fr` track still takes its proportional share of whatever
       space there is, so the name keeps the widest column and keeps wrapping
       inside it; it simply no longer demands a width the panel cannot give.
       The children carry min-width: 0 because a grid item's default
       min-width:auto refuses to shrink below its content and would reinstate
       the floor. */
    grid-template-columns: minmax(0, 1.4fr) auto auto auto;
    gap: 0.5rem;
    align-items: center;
    font-size: 0.82rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.25rem;
  }

  .nodes li > * {
    min-width: 0;
    overflow-wrap: anywhere;
  }

  .nodes li.current {
    background: rgba(74, 163, 223, 0.08);
  }

  /* Modul: the node rows were a flex line, and a two-word location name
     ("Whispering Woods") wrapped underneath the rate and the xp, which then
     overlapped it. A grid gives the name its own column and lets it wrap
     inside it instead of into its neighbours. */
  .place {
    font-weight: 600;
    line-height: 1.15;
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
