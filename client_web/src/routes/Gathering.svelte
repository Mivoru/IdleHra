<script lang="ts">
  import { formatNumber, numberTitle } from '../lib/ui/format';
  import { onMount } from 'svelte';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import { loadContent, type ContentRegistry, type GatheringNodeDefinition } from '../lib/net/content';
  import { PROFESSIONS, isGatheringActivity, HALT_REASONS } from '../lib/ui/slots';
  import { locationName, nodeLocation } from '../lib/ui/locations';
  import { assignCharacterActivity, EMPTY_GUID } from '../lib/net/commands';
  import Bar from '../lib/ui/Bar.svelte';
  import SessionLoot from '../lib/ui/SessionLoot.svelte';
  import ItemIcon from '../lib/ui/ItemIcon.svelte';
  import WorkerPicker from '../lib/ui/WorkerPicker.svelte';
  import { createQuery } from '@tanstack/svelte-query';
  import { queryKeys, fetchBreedingRoster, fetchCharacterStats } from '../lib/net/rest';
  import { prettifyBaseId, monsterName } from '../lib/net/content';
  import { workersOf, workerName, describeJob, kindTakenBy, slotBars, type Worker } from '../lib/ui/workers';
  import { selectedCharacterSlot } from '../lib/stores/selectedCharacter';
  import { nodeYieldBaseId, nodeRareBaseId, masteryXpForLevel } from '../lib/ui/gatheringNodes';

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

  // Modul: ANY PERSON CAN GATHER (task 101). This screen always sent slot 1's
  // character. Checked first, as the task asked: SimulationEngine's
  // ChangeActivity takes any of the player's characters by TargetGuid, and
  // ProcessAllSlotSubTicks runs a sub-tick for every unlocked slot that is
  // working - gathering included. So the person is a choice now, by name. Only
  // slot 1's progress ticks are on the wire, which is why the bar is shown for
  // slot 1 alone.
  const names = createQuery(() => ({ queryKey: queryKeys.breedingRoster, queryFn: fetchBreedingRoster, staleTime: 60_000 }));
  const nameById = $derived(new Map((names.data ?? []).map((c) => [c.CharacterId, c.Name])));
  const workers = $derived(workersOf(snap));
  // Shared with Character, Combat and Crafting (2026-10-08): one pick.
  const workerSlot = $derived($selectedCharacterSlot);
  const chosen = $derived<Worker | null>(workers.find((w) => w.slot === workerSlot) ?? workers[0] ?? null);
  // The picked character's job progress. Slot 1's bar used to read
  // CurrentProgressTicks, which nothing on the server advances - gathering
  // counts GatheringProgressTicks - so it sat at 0% forever.
  const bars = $derived(slotBars(snap, chosen?.slot ?? 1));

  const activeActivity = $derived(chosen?.activity ?? 0);
  const isGathering = $derived(isGatheringActivity(activeActivity));

  const jobOf = (w: Worker) => describeJob(w.activity, w.halt, { monsterName: (id) => monsterName(registry, id) });

  /** Another of your people already works this node (NodeOccupied). */
  function takenBy(activityId: number): string | null {
    // One character per KIND of work: a second miner is refused, not just a
    // second miner on the same vein.
    const other = kindTakenBy(workers, chosen?.slot, activityId);
    return other ? workerName(other, nameById) : null;
  }

  let showWhy = $state(false);

  // Modul: EACH TRADE HAS ITS OWN COLOUR (owner, 2026-10-28: the glowing
  // Gather buttons drowned out what is actually being gathered). Wood is
  // moss, ore is copper, fish is river, herbs are meadow - on the mastery bar,
  // the frame round each item's picture and the row's wash.
  const PROFESSION_COLOUR: Record<string, string> = {
    Woodcutting: '#8fbf5c',
    Mining: '#d08a4a',
    Fishing: '#4fb3dc',
    Herbalism: '#b7d65a',
  };

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
    if (professionId === 3) return snap.HerbalismMasteryLevel;
    return 0;
  }

  function masteryXpOf(professionId: number): number {
    if (!snap) return 0;
    if (professionId === 0) return snap.WoodcuttingMasteryXp;
    if (professionId === 1) return snap.MiningMasteryXp;
    if (professionId === 2) return snap.FishingMasteryXp;
    if (professionId === 3) return snap.HerbalismMasteryXp;
    return 0;
  }

  // Modul: `?? MASTERY_TRACKS[3]` was a crash waiting for a content change,
  // back when the array held three entries: `track.name` on an undefined
  // track throws and takes the whole screen down. An unknown profession
  // returns null instead, which every caller already handles.
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
  const activeCharacterId = $derived(chosen?.id ?? EMPTY_GUID);

  function deploy(node: GatheringNodeDefinition) {
    const outcome = assignCharacterActivity(activeCharacterId, node.ActivityId, { takenBy: takenBy(node.ActivityId) });
    if (!outcome.ok) pushLocalNotice(outcome.reason, 'error');
  }

  function stop() {
    const outcome = assignCharacterActivity(activeCharacterId, 0);
    if (!outcome.ok) pushLocalNotice(outcome.reason, 'error');
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

  // Mirrors GatheringToolEngine.GetMasterySpeedBonusPct: 40 * sqrt(level),
  // where it used to be a flat 10% a level with no ceiling. Shown in the
  // mastery line below; the RATES no longer use it (see speedPctFor).
  const MASTERY_SPEED_PCT_AT_LEVEL_ONE = 40;
  function masterySpeedPct(level: number): number {
    return level <= 0 ? 0 : Math.floor(MASTERY_SPEED_PCT_AT_LEVEL_ONE * Math.sqrt(level));
  }

  // Modul: THE RATE IS THE SERVER'S, NOT A COPY OF ITS FORMULA (TASK_BOARD
  // 121, 2026-10-29). This screen used to re-derive the speed from tool tier,
  // mastery, village and ToolGatherSpeedPct. That copy left out the Harvest
  // bough, the Skill aptitude, traits and the pet; it read slot 1's tools
  // whatever character was picked; and it had to change every time the
  // formula did - and the formula changed: extras multiply now, and each tool
  // speeds only its own profession. The stat sheet's gather_speed_N row is
  // GatheringToolEngine.TotalSpeedBonusPct for the PICKED character, the same
  // figure the tick divides by.
  const statSlot = $derived(Math.max(0, (chosen?.slot ?? 1) - 1));
  const speedSheet = createQuery(() => ({
    queryKey: queryKeys.characterStats(statSlot),
    queryFn: () => fetchCharacterStats(statSlot),
    staleTime: 15_000,
  }));
  function speedPctFor(professionId: number): number | null {
    const rows = speedSheet.data?.Sections.flatMap((section) => section.Rows) ?? [];
    const row = rows.find((r) => r.Key === `gather_speed_${professionId}`);
    return row ? Number(row.Value) : null;
  }

  /** Ticks per harvest, or null until the picked character's stat sheet has loaded. */
  function effectiveTicks(node: GatheringNodeDefinition): number | null {
    const speedPct = speedPctFor(node.ProfessionType);
    if (speedPct === null) return null;
    const ticks = Math.floor((node.BaseTickThreshold * 100) / (100 + speedPct));
    return Math.max(MIN_GATHER_TICKS, ticks);
  }

  function secondsPerUnit(node: GatheringNodeDefinition): string {
    const ticks = effectiveTicks(node);
    return ticks === null ? '…' : (ticks / 10).toFixed(1);
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
    if (professionId === 2) return snap.RodToolTier;
    return 0; // herbs are picked by hand
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

  // Modul: only wood and ore have a monolith (SimulationEngine.GatheringYieldFor
  // reads none for 2 or 3). This used to answer the mining monolith for every
  // profession that was not wood, so the fishing card advertised a yield
  // bonus the server never paid.
  function monolithFor(professionId: number): number {
    if (professionId === 0) return woodMonolith;
    if (professionId === 1) return mineMonolith;
    return 0;
  }
</script>

<div class="wrap">
  {#if contentError}
    <p class="err pad">Content failed to load: {contentError}</p>
  {/if}

  {#if snap}
    <!-- Modul: ACTIONS FIRST (task 101). The first Gather button sat at about
         y 990 at 390 px, below the mastery table, the speed table and an empty
         haul. The order is now: who is doing what, the three professions with
         their nodes, what came in, and a closed "How fast and why". -->
    <section class="panel status">
      <h2>Gathering</h2>
      <WorkerPicker
        {workers}
        names={nameById}
        selected={chosen?.slot ?? 1}
        describe={jobOf}
        onpick={(slot) => selectedCharacterSlot.set(slot)}
        label="Who gathers"
      />
      <div class="nowline">
        {#if isGathering && chosen}
          <p class="active" data-testid="gathering-status">
            {jobOf(chosen)}
            {#if bars.requiredTicks > 0}
              &middot; {Math.min(100, Math.floor((bars.progressTicks / bars.requiredTicks) * 100))}%
            {/if}
          </p>
          <button class="stopbtn" onclick={stop}>Stop</button>
        {:else if activeActivity > 0 && chosen}
          <p class="dim" data-testid="gathering-status">{jobOf(chosen)}. Gathering moves them off it.</p>
        {:else}
          <p class="dim" data-testid="gathering-status">
            Idle. Pick a node below - {prettifyBaseId(nodeYieldBaseId(1001))} in
            {locationName(1)} is where everyone starts.
          </p>
        {/if}
      </div>
      {#if bars.requiredTicks > 0 && isGathering}
        <div class="progress">
          <Bar
            value={bars.progressTicks}
            max={bars.requiredTicks}
            color="var(--sec-gather)"
          />
        </div>
      {/if}
      {#if chosen && chosen.halt !== 0 && HALT_REASONS[chosen.halt]}
        <p class="halt">{HALT_REASONS[chosen.halt]}</p>
      {/if}
    </section>
  {/if}

  <div class="professions">
    {#each byProfession as profession}
      {@const track = masteryFor(profession.id)}
      <section class="panel trade" style="--prof: {PROFESSION_COLOUR[profession.name] ?? 'var(--sec-gather)'}">
        <h2>{profession.name}</h2>
        {#if track}
          <!-- Modul: data-mastery + data-exact are what exercise.mjs reads -
               the exact XP, never the compacted display. -->
          <div class="masteryline" data-mastery={track.name}>
            <span class="small-line">
              Mastery level {track.level}
              <span class="dim tiny">
                <span data-exact={track.xp} title={numberTitle(track.xp)}>{formatNumber(track.xp)}</span>
                / {formatNumber(masteryXpForLevel(track.level))} xp
              </span>
              {#if monolithFor(profession.id) > 0}
                <span class="bonus tiny">+{Math.min(monolithFor(profession.id), MONOLITH_CAP_PCT)}% yield</span>
              {/if}
            </span>
            <Bar value={track.xp} max={masteryXpForLevel(track.level)} color="var(--prof)" />
          </div>
        {/if}

        <ul class="nodes">
          {#each profession.nodes as node (node.ActivityId)}
            {@const locked = isLocked(node)}
            {@const yieldId = nodeYieldBaseId(node.ActivityId)}
            {@const working = activeActivity === node.ActivityId}
            <li class:current={working} class:locked>
              <span class="what">
                {#if yieldId}<span class="iconframe"><ItemIcon baseItemId={yieldId} name={prettifyBaseId(yieldId)} size="sm" /></span>{/if}
                <span class="whattext">
                  <span class="yield">{yieldId ? prettifyBaseId(yieldId) : profession.name}</span>
                  <span class="place dim tiny">{locationName(nodeLocation(node.ActivityId))}</span>
                </span>
              </span>
              <!-- Modul: plain text, explained ONCE in "How fast and why".
                   The caveats were tooltips (a phone could never show them),
                   then a Hint per row (fourteen 17px triggers check:touch
                   failed one by one). -->
              <span class="dim tiny rate">
                {secondsPerUnit(node)}s{#if isFloored(node)}<span class="floored"> (max)</span>{/if}
                <br />{node.BaseMasteryXpReward} xp
              </span>
              {#if locked}
                <span class="dim tiny lock">Fight here first</span>
              {:else}
                <button
                  class="tiny-btn"
                  class:primary={!working}
                  disabled={working}
                  title={nodeRareBaseId(node.ActivityId) ? `Sometimes ${prettifyBaseId(nodeRareBaseId(node.ActivityId))}` : undefined}
                  onclick={() => deploy(node)}
                >
                  {working ? 'Working' : 'Gather'}
                </button>
              {/if}
            </li>
          {/each}
        </ul>
      </section>
    {/each}

    <section class="panel">
      <h2>Hauled this session</h2>
      <!-- Modul: this panel exists because gathering used to show nothing
           at all while it ran, so a working node and a broken one looked
           identical. Its own "Loot drops" heading is off here: an h2 inside
           the card outranked the card's title. -->
      <SessionLoot {registry} showEquipment={false} title={false} />
    </section>
  </div>

  {#if snap}
    <section class="panel">
      <button class="whytoggle" aria-expanded={showWhy} onclick={() => (showWhy = !showWhy)}>
        {showWhy ? 'Hide' : 'How fast and why'}
      </button>
      {#if showWhy}
        <dl class="mastery">
          <div>
            <dt>Tools</dt>
            <dd class="bonus">
              axe {toolTierFor(0)} &middot; pickaxe {toolTierFor(1)} &middot; rod {toolTierFor(2)}
              <span class="dim tiny">&middot; herbs by hand</span>
            </dd>
          </div>
          <div>
            <dt>Mastery speed</dt>
            <dd class="bonus">
              <!-- Modul: this printed `-level * 2` ("-112 mining") for weeks
                   after mastery stopped subtracting ticks - the retired rule,
                   shown beside rates that already used the new one. The label
                   reads the same masterySpeedPct the rates do. -->
              +{masterySpeedPct(masteryLevelOf(0))}% wood, +{masterySpeedPct(masteryLevelOf(1))}% mining,
              +{masterySpeedPct(masteryLevelOf(2))}% fish, +{masterySpeedPct(masteryLevelOf(3))}% herbs
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
          The time per unit already counts your tool, mastery and village
          buildings. The server also applies a logistics bonus this screen
          cannot see, so the real speed is this or better. 0.2s is the floor
          for any node - one marked "(max)" will not get faster with mastery or
          a better tool; a higher node will. Each node also has a one-in-ten
          rare find. Monoliths are a guild upgrade, so they move without you
          doing anything.
        </p>
      {/if}
    </section>
  {/if}

  {#if registry && registry.gatheringNodes.length === 0}
    <p class="dim pad">Gathering is unavailable right now - the list of places to gather did not load. Try again in a moment.</p>
  {/if}
</div>

<style>
  .wrap {
    padding: 1rem;
    display: grid;
    gap: 1rem;
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

  .small {
    font-size: 0.8rem;
    margin: 0 0 0.6rem;
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
    grid-template-columns: minmax(0, 1fr) auto auto;
    gap: 0.5rem;
    align-items: center;
    font-size: 0.82rem;
    /* A card per node, washed in its trade's colour from the left. */
    padding: 0.35rem 0.45rem;
    border: 1px solid color-mix(in srgb, var(--prof) 25%, var(--border));
    border-radius: var(--radius);
    background-image: linear-gradient(
      90deg,
      color-mix(in srgb, var(--prof) 14%, transparent),
      transparent 60%
    );
  }

  .nodes li > * {
    min-width: 0;
    overflow-wrap: anywhere;
  }

  .nodes li.current {
    background: var(--tint-selected);
  }

  /* Modul: the node rows were a flex line, and a two-word location name
     ("Whispering Woods") wrapped underneath the rate and the xp, which then
     overlapped it. A grid gives the name its own column and lets it wrap
     inside it instead of into its neighbours. */
  .place {
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

  .nowline {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.6rem;
  }

  .nowline p {
    margin: 0;
    min-width: 0;
  }

  .stopbtn {
    flex-shrink: 0;
  }

  .masteryline {
    display: grid;
    gap: 0.25rem;
    margin: 0 0 0.6rem;
    font-size: 0.82rem;
  }

  .small-line {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 0.2rem 0.5rem;
  }

  .what {
    display: flex;
    align-items: center;
    gap: 0.4rem;
  }

  .whattext {
    display: grid;
    min-width: 0;
    line-height: 1.2;
  }

  /* What you get is the headline of the row. */
  .yield {
    font-weight: 700;
    font-size: 0.92rem;
    color: var(--text);
    letter-spacing: 0.01em;
  }

  /* The icon draws its own border; the trade's colour goes onto it rather
     than around it, so there is one frame, not two. */
  .iconframe {
    display: contents;
  }

  .iconframe :global(.icon) {
    border-color: color-mix(in srgb, var(--prof) 75%, transparent);
    background: radial-gradient(circle, color-mix(in srgb, var(--prof) 30%, var(--bg)), var(--bg) 80%);
    box-shadow: 0 0 6px color-mix(in srgb, var(--prof) 30%, transparent);
  }

  li.locked .iconframe :global(.icon) {
    border-color: var(--border);
    box-shadow: none;
    background: var(--bg);
  }

  .trade > h2 {
    color: color-mix(in srgb, var(--prof) 55%, var(--text));
  }

  .rate {
    text-align: right;
    line-height: 1.25;
  }

  .nodes li.current {
    outline: 1px solid var(--prof);
    border-color: var(--prof);
    box-shadow: 0 0 8px color-mix(in srgb, var(--prof) 35%, transparent);
  }

  .whytoggle {
    width: 100%;
    text-align: left;
  }
</style>
