<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import { queryKeys, fetchStatistics } from '../lib/net/rest';
  import {
    BUILDINGS,
    upgradeBuilding,
    evictVillager,
    upgradeTool,
    assignMentor,
    MAX_MENTOR_SLOTS,
  } from '../lib/net/commands';
  import { connection } from '../lib/net/connection';
  import { toolIcon } from '../lib/ui/sprites';
  import type { StateUpdate } from '../lib/net/protocol.generated';

  const client = useQueryClient();
  const statistics = createQuery(() => ({ queryKey: queryKeys.statistics, queryFn: fetchStatistics }));

  const snap = $derived($playerState);

  function levelOf(state: StateUpdate, field: string): number {
    const value = (state as unknown as Record<string, unknown>)[field];
    return typeof value === 'number' ? value : 0;
  }

  // Modul: PendingUpgradeBuildingId == 0 means no upgrade is in flight. Only
  // one can run at a time, so every other button is disabled while one is -
  // otherwise the player queues a second and it silently does nothing.
  const pendingId = $derived(snap ? snap.PendingUpgradeBuildingId : 0);
  const pendingUntil = $derived(snap ? Number(snap.PendingUpgradeCompletesAtEpoch) : 0);

  let nowSeconds = $state(Math.floor(connection.serverNowMs() / 1000));
  $effect(() => {
    // Server-corrected clock, never Date.now() - cooldowns and windows on this
    // wire are epoch-based and a browser clock can be arbitrarily wrong.
    const timer = setInterval(() => {
      nowSeconds = Math.floor(connection.serverNowMs() / 1000);
    }, 1000);
    return () => clearInterval(timer);
  });

  const pendingRemaining = $derived(Math.max(0, pendingUntil - nowSeconds));

  function upgrade(buildingId: number) {
    const outcome = upgradeBuilding(buildingId);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
  }

  function evict(slotIndex: number) {
    const outcome = evictVillager(slotIndex);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    setTimeout(() => client.invalidateQueries({ queryKey: queryKeys.statistics }), 800);
  }

  // Modul: skills moved to the Character screen - they are combat abilities
  // that spend mana and have cooldowns, and they lived here between the
  // building queue and the mentor slots. See lib/ui/SkillsPanel.svelte.

  // --- gathering tool -------------------------------------------------------
  // Modul: CachedCurrentToolTier is subtracted DIRECTLY from a gathering node's
  // required tick count, so each tier is one tick off every gather forever -
  // the most straightforwardly valuable upgrade in the game and the one with
  // no screen until now.
  const toolTier = $derived(snap?.CachedCurrentToolTier ?? 0);
  const TOOL_KINDS = ['axe', 'pickaxe', 'rod'] as const;

  function upgradeGatheringTool() {
    const outcome = upgradeTool();
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    // The command carries nothing and the engine reports nothing, so there is
    // no result to await. The tier below is the only evidence it worked.
    pushLocalNotice('Tool upgrade requested.', 'info');
  }

  // --- mentor slots ---------------------------------------------------------
  // Modul: THE ACADEMY'S LEVEL IS ITS SLOT COUNT. A level 2 Academy has slots
  // 0 and 1, and asking for slot 2 does not fail - it disconnects. So the
  // slots rendered here are bounded by the level, not by MAX_MENTOR_SLOTS.
  const academyLevel = $derived(snap?.AcademyLevel ?? 0);
  const mentorSlots = $derived(Math.min(academyLevel, MAX_MENTOR_SLOTS));

  const ownCharacters = $derived(
    snap
      ? [
          { slot: 1, id: snap.Slot1_CharacterId, phase: snap.Slot1_AgePhase },
          { slot: 2, id: snap.Slot2_CharacterId, phase: snap.Slot2_AgePhase },
          { slot: 3, id: snap.Slot3_CharacterId, phase: snap.Slot3_AgePhase },
        ].filter((c) => c.id && c.id !== '00000000-0000-0000-0000-000000000000')
      : [],
  );

  let mentorPicks = $state<Record<number, string>>({});

  function seat(slotIndex: number) {
    const characterId = mentorPicks[slotIndex] ?? '';
    const outcome = assignMentor(characterId, slotIndex, academyLevel);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    // AssignMentor queues a ReloadState server-side so the mentor count comes
    // back from the database rather than being guessed at here.
    pushLocalNotice(`Seated in slot ${slotIndex}.`, 'info');
  }
</script>

{#if !snap}
  <p class="dim pad">Waiting for state...</p>
{:else}
  <div class="grid">
    <section class="panel">
      <div class="head">
        <h2>Village</h2>
        <span class="dim tiny">
          {snap.CurrentPopulationCount}/{snap.CachedMaxPopulationCapacity} population
        </span>
      </div>

      <dl class="stocks">
        <div><dt>Wood</dt><dd>{Number(snap.CachedWoodStock).toLocaleString()}</dd></div>
        <div><dt>Stone</dt><dd>{Number(snap.CachedStoneStock).toLocaleString()}</dd></div>
        <div><dt>Iron ore</dt><dd>{Number(snap.CachedIronOreStock).toLocaleString()}</dd></div>
      </dl>

      {#if pendingId !== 0}
        <p class="pending">
          Upgrading {BUILDINGS.find((b) => b.id === pendingId)?.name ?? `building ${pendingId}`}
          &middot; {pendingRemaining > 0 ? `${pendingRemaining}s left` : 'finishing...'}
        </p>
      {/if}

      <ul class="buildings">
        {#each BUILDINGS as building}
          <li>
            <span class="name">{building.name}</span>
            <span class="lvl">{levelOf(snap, building.stateField)}</span>
            <button
              class="tiny-btn"
              disabled={pendingId !== 0}
              title={pendingId !== 0 ? 'Another upgrade is already in progress' : ''}
              onclick={() => upgrade(building.id)}
            >
              Upgrade
            </button>
          </li>
        {/each}
      </ul>
      <!-- Town Hall gates every other building's ceiling, which is why it is
           listed first rather than in id order. -->
      <p class="dim tiny">Town Hall level caps every other building.</p>
    </section>

    <section class="panel">
      <h2>Villagers</h2>
      {#if (statistics.data?.Villagers ?? []).length === 0}
        <p class="dim">No villagers yet.</p>
      {:else}
        <ul class="villagers">
          {#each statistics.data?.Villagers ?? [] as villager (villager.SlotIndex)}
            <li>
              <span class="name">Slot {villager.SlotIndex}</span>
              <span class="dim tiny">
                {villager.IsActive ? 'active' : 'idle'} &middot;
                {(villager.EfficiencyModifier * 100).toFixed(0)}% efficiency
              </span>
              <button class="tiny-btn" onclick={() => evict(villager.SlotIndex)}>Evict</button>
            </li>
          {/each}
        </ul>
      {/if}
    </section>


    <section class="panel">
      <h2>Gathering tool</h2>

      <!-- The three tools share one tier, so all three are shown: the ladder
           is a wood type, not a per-profession upgrade, and seeing an axe, a
           pickaxe and a rod change together is what makes that legible. -->
      <div class="tools">
        {#each TOOL_KINDS as kind}
          {@const url = toolIcon(kind, toolTier)}
          <span class="tool" title="{kind} tier {toolTier}">
            {#if url}
              <img src={url} alt="" loading="lazy" decoding="async" />
            {:else}
              <span class="dim tiny">{kind}</span>
            {/if}
          </span>
        {/each}
      </div>

      <dl class="stocks">
        <div><dt>Tier</dt><dd class="tier">{toolTier}</dd></div>
        <div><dt>Ticks saved</dt><dd class="tier">-{toolTier}</dd></div>
      </dl>

      <p class="dim small">
        Each tier removes one tick from every gathering action, permanently and
        on every node. There is no cap shown on the wire and no cost preview -
        a request you cannot afford simply does nothing, so the tier above is
        the only way to see whether it worked.
      </p>

      <button onclick={upgradeGatheringTool}>Upgrade tool</button>
    </section>

    <section class="panel">
      <h2>Mentor slots</h2>

      {#if academyLevel === 0}
        <p class="dim">
          Build a Mentorship Academy above to seat characters as mentors.
        </p>
      {:else}
        <p class="dim small">
          Your Academy is level {academyLevel}, which is exactly how many slots
          it has. Seating a character lends its experience to the others.
        </p>

        <ul class="slots">
          {#each Array(mentorSlots) as _, slotIndex}
            <li>
              <span class="name">Slot {slotIndex}</span>
              <select bind:value={mentorPicks[slotIndex]}>
                <option value="">Choose a character...</option>
                {#each ownCharacters as character (character.id)}
                  <option value={character.id}>
                    Character {character.slot} (phase {character.phase})
                  </option>
                {/each}
              </select>
              <button class="tiny-btn" onclick={() => seat(slotIndex)}>Seat</button>
            </li>
          {/each}
        </ul>

        <p class="dim tiny">
          Mentors held: {snap.CachedMentorCount}.
          {#if academyLevel < MAX_MENTOR_SLOTS}
            Upgrading the Academy adds one more slot, up to {MAX_MENTOR_SLOTS}.
          {/if}
        </p>
      {/if}
    </section>
  </div>
{/if}

<style>
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(19rem, 1fr));
    gap: 1rem;
    padding: 1rem;
    align-items: start;
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
  }

  .head {
    display: flex;
    justify-content: space-between;
    align-items: baseline;
    gap: 1rem;
  }

  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
  }

  .dim {
    color: var(--text-dim);
  }
  .tiny {
    font-size: 0.72rem;
  }
  .pad {
    padding: 1rem;
  }

  .pending {
    padding: 0.45rem 0.6rem;
    background: rgba(74, 163, 223, 0.12);
    border-left: 3px solid var(--accent);
    border-radius: 4px;
    font-size: 0.82rem;
    margin: 0 0 0.6rem;
  }

  .stocks {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 0.5rem;
    margin: 0 0 0.7rem;
  }

  .stocks div {
    display: grid;
    gap: 0.1rem;
  }

  dt {
    font-size: 0.7rem;
    color: var(--text-dim);
  }

  dd {
    margin: 0;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
  }

  .buildings,
  .villagers,
  .slots {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  .buildings li,
  .villagers li,
  .slots li {
    display: grid;
    grid-template-columns: 1fr auto auto;
    gap: 0.5rem;
    align-items: center;
    font-size: 0.85rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.28rem;
  }

  .slots li {
    grid-template-columns: auto 1fr auto;
  }

  .slots select {
    font: inherit;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.25rem 0.4rem;
    font-size: 0.8rem;
    min-width: 0;
  }

  .tier {
    color: var(--accent);
  }

  .tools {
    display: flex;
    gap: 0.5rem;
    margin-bottom: 0.7rem;
  }

  .tool {
    display: grid;
    place-items: center;
    width: 3.4rem;
    height: 3.4rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg);
  }

  .tool img {
    width: 100%;
    height: 100%;
    object-fit: contain;
  }

  .name {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .lvl {
    font-weight: 700;
    font-variant-numeric: tabular-nums;
    min-width: 1.5rem;
    text-align: right;
  }

  .mana {
    display: grid;
    gap: 0.15rem;
    margin-bottom: 0.7rem;
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.45rem;
  }
</style>
