<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { playerState, visualState, observedMaxPlayerHp, pushLocalNotice } from '../lib/stores/game';
  import { connection } from '../lib/net/connection';
  import { CommandType } from '../lib/net/protocol.generated';
  import { queryKeys, fetchInventory, type InventoryEquipment } from '../lib/net/rest';
  import { loadContent, prettifyBaseId, monsterName, type ContentRegistry } from '../lib/net/content';
  import { EQUIPMENT_SLOTS, equippedIdFor, isAffixLocked, agePhaseName, HALT_REASON_SHORT, isGatheringActivity, professionName, resolveSlotIndex, isCraftingActivity, craftingActivityId } from '../lib/ui/slots';
  import { craftingProfessionName } from '../lib/ui/slots';
  import { queryKeys as qk, fetchRecipes } from '../lib/net/rest';
  import { rarityColor, rarityName, shouldGlow } from '../lib/ui/rarity';
  import Affixes from '../lib/ui/Affixes.svelte';
  import Bar from '../lib/ui/Bar.svelte';
  import RaceIcon from '../lib/ui/RaceIcon.svelte';
  import { assignCharacterActivity, EMPTY_GUID } from '../lib/net/commands';
  // EMPTY_GUID is the sentinel the roster filter below tests against.
  import { raceName } from '../lib/ui/races';
  import { onMount } from 'svelte';

  const inventory = createQuery(() => ({ queryKey: queryKeys.inventory, queryFn: fetchInventory }));
  // Recipes carry no id of their own on the wire - the crafting activity id is
  // the recipe's INDEX in this list, which is the same order the server holds.
  const recipeList = createQuery(() => ({ queryKey: qk.recipes, queryFn: fetchRecipes }));

  let registry = $state<ContentRegistry | null>(null);
  onMount(async () => {
    registry = await loadContent().catch(() => null);
  });

  const snap = $derived($playerState);
  const visual = $derived($visualState);

  // Equipment ids arrive on StateUpdate but their names, rarities and affixes
  // do not - the hot-path packet carries only the instance id, deliberately.
  // The inventory snapshot is where the rest lives.
  const byId = $derived(
    new Map<number, InventoryEquipment>((inventory.data?.Equipment ?? []).map((e) => [e.Id, e])),
  );

  // Modul: you could see the seven slots here but only ever empty them. The
  // only place that could EQUIP anything was the chest screen, so gearing up
  // meant leaving the character you were gearing. Candidates are resolved with
  // the same slot rule the server uses, so the list for a slot is exactly what
  // that slot will accept.
  const equippedIds = $derived(
    snap
      ? new Set(EQUIPMENT_SLOTS.map((slot) => equippedIdFor(snap, slot)).filter((id) => id > 0))
      : new Set<number>(),
  );

  const candidatesBySlot = $derived.by(() => {
    const bySlot = new Map<number, InventoryEquipment[]>();
    for (const item of inventory.data?.Equipment ?? []) {
      if (equippedIds.has(item.Id)) continue;
      const index = resolveSlotIndex(item.BaseItemId);
      if (index < 0) continue;
      const list = bySlot.get(index) ?? [];
      list.push(item);
      bySlot.set(index, list);
    }
    for (const list of bySlot.values()) {
      list.sort((a, b) => b.QualityTier - a.QualityTier);
    }
    return bySlot;
  });

  let picked = $state<Record<number, number>>({});

  function equip(slotIndex: number) {
    const instanceId = picked[slotIndex];
    if (!instanceId) return;
    connection.send({ Command: CommandType.EquipItem, TargetId: instanceId });
    picked = { ...picked, [slotIndex]: 0 };
    setTimeout(() => inventory.refetch(), 700);
  }

  // Modul: nothing could tell a character what to do.
  //
  // SimulationEngine has routed ChangeActivity by TargetGuid since the
  // multi-slot overhaul, and CharacterSlotEngine unlocks slot 2 at Town Hall 3
  // and slot 3 at Town Hall 5 - but no screen ever named a character, so
  // characters 2 and 3 sat Idle permanently. Every other screen assigns work
  // to "you", which is always slot 1.
  const SLOT_UNLOCK_TOWN_HALL = [0, 3, 5];
  const townHall = $derived(snap?.TownHallLevel ?? 0);

  const jobChoices = $derived.by(() => {
    if (!registry) return [] as { id: number; label: string; group: string }[];
    const out: { id: number; label: string; group: string }[] = [];
    for (const node of registry.gatheringNodes) {
      out.push({
        id: node.ActivityId,
        label: `${professionName(node.ProfessionType)} tier ${node.ActivityId % 1000}`,
        group: 'Gathering',
      });
    }
    for (const region of registry.regions) {
      for (const monster of region) {
        out.push({ id: monster.Id, label: monster.Name, group: 'Combat' });
      }
    }
    (recipeList.data?.Recipes ?? []).forEach((recipe, index) => {
      out.push({
        id: craftingActivityId(index),
        label: `${craftingProfessionName(recipe.ProfessionType)}: ${prettifyBaseId(recipe.ResultBaseItemId)}`,
        group: 'Crafting',
      });
    });
    return out;
  });

  const gatheringJobs = $derived(jobChoices.filter((j) => j.group === 'Gathering'));
  const combatJobs = $derived(jobChoices.filter((j) => j.group === 'Combat'));
  const craftingJobs = $derived(jobChoices.filter((j) => j.group === 'Crafting'));

  // CharacterSlotEngine.IsActivityOccupiedByAnotherSlot: two of your own
  // characters may not work the same activity. The server answers NodeOccupied;
  // showing it here means the player never has to find out that way.
  function occupiedBy(activityId: number, bySlot: number): string | null {
    if (activityId <= 0 || !snap) return null;
    if (bySlot !== 1 && Number(snap.ActiveActivityId) === activityId) return 'Slot 1';
    if (bySlot !== 2 && snap.Slot2ActivityId === activityId) return 'Slot 2';
    if (bySlot !== 3 && snap.Slot3ActivityId === activityId) return 'Slot 3';
    return null;
  }

  let jobPick = $state<Record<number, number>>({});

  function assign(slot: number, characterId: string) {
    const activityId = jobPick[slot] ?? 0;
    const outcome = assignCharacterActivity(characterId, activityId, {
      unlocked: townHall >= SLOT_UNLOCK_TOWN_HALL[slot - 1],
      takenBy: occupiedBy(activityId, slot),
    });
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
  }

  function stopWork(slot: number, characterId: string) {
    const outcome = assignCharacterActivity(characterId, 0);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    jobPick = { ...jobPick, [slot]: 0 };
  }

  function activityLabel(activityId: number, haltReason: number): string {
    if (activityId === 0) {
      const halt = HALT_REASON_SHORT[haltReason];
      return halt ? `Idle - ${halt}` : 'Idle';
    }
    if (isGatheringActivity(activityId)) {
      const profession = professionName(Math.floor(activityId / 1000) - 1);
      return `${profession} tier ${activityId % 1000}`;
    }
    if (isCraftingActivity(activityId)) {
      const job = craftingJobs.find((j) => j.id === activityId);
      return job ? job.label : `Crafting #${activityId}`;
    }
    return monsterName(registry, activityId);
  }

  const roster = $derived(
    snap
      ? [
          {
            slot: 1,
            id: snap.Slot1_CharacterId,
            ageTicks: snap.Slot1_AgeTicks,
            agePhase: snap.Slot1_AgePhase,
            raceId: snap.Slot1_RaceId,
            activity: Number(snap.ActiveActivityId),
            halt: snap.ActivityHaltReason,
          },
          {
            slot: 2,
            id: snap.Slot2_CharacterId,
            ageTicks: snap.Slot2_AgeTicks,
            agePhase: snap.Slot2_AgePhase,
            raceId: snap.Slot2_RaceId,
            activity: snap.Slot2ActivityId,
            halt: snap.Slot2ActivityHaltReason,
          },
          {
            slot: 3,
            id: snap.Slot3_CharacterId,
            ageTicks: snap.Slot3_AgeTicks,
            agePhase: snap.Slot3_AgePhase,
            raceId: snap.Slot3_RaceId,
            activity: snap.Slot3ActivityId,
            halt: snap.Slot3ActivityHaltReason,
          },
        ].filter((c) => c.id !== EMPTY_GUID)
      : [],
  );

  // Modul: UnequipItem's TargetId is a SLOT INDEX (0 Weapon, 1 Helmet,
  // 2 Chest, 3 Gloves, 4 Leggings, 5 Boots, 6 Offhand) - NOT an item instance
  // id, which is what EquipItem takes. Sending the instance id addresses slot
  // 8, or 42, or whatever the id happened to be: out of range, silently
  // ignored, no error anywhere.
  //
  // The asymmetry is real and easy to miss: equip names the ITEM, unequip
  // names the SLOT, because a slot is what you are emptying.
  function unequip(slotIndex: number) {
    connection.send({ Command: CommandType.UnequipItem, TargetId: slotIndex });
    // The command resolves on the tick thread, so the REST snapshot is stale
    // for a moment. Refetching immediately would race it and read the old
    // rows back; one tick of slack is enough and is still imperceptible.
    setTimeout(() => inventory.refetch(), 400);
  }
</script>

{#if !snap}
  <p class="dim pad">Waiting for the first state snapshot...</p>
{:else}
  <div class="grid">
    <section class="panel">
      <h2>Character</h2>

      <div class="vitals">
        <div class="hpblock">
          <span class="dim">Health</span>
          <Bar
            value={visual?.PlayerHp ?? snap.PlayerHp}
            max={$observedMaxPlayerHp}
            color="var(--good)"
            label={`${Math.round(visual?.PlayerHp ?? snap.PlayerHp).toLocaleString()} / ${$observedMaxPlayerHp.toLocaleString()}`}
          />
        </div>
        {#if snap.MaxMana > 0}
          <div class="hpblock">
            <span class="dim">Mana</span>
            <Bar
              value={visual?.CurrentMana ?? snap.CurrentMana}
              max={snap.MaxMana}
              color="var(--accent)"
              label={`${Math.round(visual?.CurrentMana ?? snap.CurrentMana)} / ${snap.MaxMana}`}
            />
          </div>
        {/if}
      </div>

      <h3>Attributes</h3>
      <dl class="stats">
        <div><dt>STR</dt><dd>{snap.STR}</dd></div>
        <div><dt>DEX</dt><dd>{snap.DEX}</dd></div>
        <div><dt>CON</dt><dd>{snap.CON}</dd></div>
        <div><dt>LCK</dt><dd>{snap.LCK}</dd></div>
      </dl>

      <!-- Modul: these three are the server-COMPUTED values actually used in
           that tick's combat resolution, not a client reconstruction from raw
           DEX/CON - so what is shown can never drift from what the server
           rolled against. -->
      <h3>Combat rating</h3>
      <dl class="stats">
        <div><dt>Accuracy</dt><dd>{snap.PlayerAccuracyRating.toLocaleString()}</dd></div>
        <div><dt>Armor</dt><dd>{snap.PlayerArmorRating.toLocaleString()}</dd></div>
        <div>
          <dt>Block</dt>
          <dd>
            {typeof snap.PlayerBlockStrengthPct === 'number'
              ? `${snap.PlayerBlockStrengthPct.toFixed(1)}%`
              : String(snap.PlayerBlockStrengthPct)}
          </dd>
        </div>
        <div><dt>Skill pts</dt><dd>{snap.AvailableSkillPoints}</dd></div>
      </dl>
    </section>

    <section class="panel">
      <h2>Equipment</h2>
      <p class="dim small">The active character's seven slots.</p>

      <ul class="slots">
        {#each EQUIPMENT_SLOTS as slot}
          {@const instanceId = equippedIdFor(snap, slot)}
          {@const item = byId.get(instanceId)}
          {@const candidates = candidatesBySlot.get(slot.index) ?? []}
          <li>
            <div class="slotname dim">{slot.label}</div>
            {#if instanceId > 0}
              <div class="item">
                <span
                  style="color: {rarityColor(item?.QualityTier ?? 0)}"
                  class:rarity-glow={shouldGlow(item?.QualityTier ?? 0)}
                >
                  {item ? prettifyBaseId(item.BaseItemId) : `Item #${instanceId}`}
                </span>
                <span class="dim tiny">
                  {#if item}[{rarityName(item.QualityTier)}]{/if}
                  {#if isAffixLocked(snap, slot) || item?.IsAffixLocked}· locked{/if}
                </span>
                {#if item}<Affixes affixes={item.Affixes} />{/if}
              </div>
              <button class="tiny-btn" onclick={() => unequip(slot.index)}>Unequip</button>
            {:else}
              <div class="item empty dim">empty</div>
            {/if}

            {#if candidates.length > 0}
              <div class="equiprow">
                <select bind:value={picked[slot.index]}>
                  <option value={0}>Choose...</option>
                  {#each candidates as candidate (candidate.Id)}
                    <option value={candidate.Id}>
                      {prettifyBaseId(candidate.BaseItemId)} [{rarityName(candidate.QualityTier)}]
                    </option>
                  {/each}
                </select>
                <button
                  class="tiny-btn"
                  disabled={!picked[slot.index]}
                  onclick={() => equip(slot.index)}
                >
                  Equip
                </button>
              </div>
            {:else if instanceId === 0}
              <span class="dim tiny">Nothing in the chest fits this slot.</span>
            {/if}
          </li>
        {/each}
      </ul>
    </section>

    <section class="panel">
      <h2>Roster</h2>
      <p class="dim small">
        Every character in your village. Equipment is shown for the active
        character only.
      </p>

      {#each roster as character}
        <div class="rostercard">
          <div class="rosterrow">
            <strong>Slot {character.slot}</strong>
            <span class="race">
              <RaceIcon raceId={character.raceId} />
              {raceName(character.raceId)}
            </span>
            <span class="dim">{agePhaseName(character.agePhase)}</span>
            <span class:idle={character.activity === 0}>
              {activityLabel(character.activity, character.halt)}
            </span>
          </div>

          <div class="assign">
            <select bind:value={jobPick[character.slot]}>
              <option value={0}>Choose a job...</option>
              <optgroup label="Gathering">
                {#each gatheringJobs as job (job.id)}
                  <option value={job.id}>
                    {job.label}{occupiedBy(job.id, character.slot) ? ' (taken)' : ''}
                  </option>
                {/each}
              </optgroup>
              <optgroup label="Combat">
                {#each combatJobs as job (job.id)}
                  <option value={job.id}>
                    {job.label}{occupiedBy(job.id, character.slot) ? ' (taken)' : ''}
                  </option>
                {/each}
              </optgroup>
              <optgroup label="Crafting &amp; cooking">
                {#each craftingJobs as job (job.id)}
                  <option value={job.id}>
                    {job.label}{occupiedBy(job.id, character.slot) ? ' (taken)' : ''}
                  </option>
                {/each}
              </optgroup>
            </select>
            <button
              class="tiny-btn"
              disabled={!jobPick[character.slot]}
              onclick={() => assign(character.slot, character.id)}
            >
              Assign
            </button>
            {#if character.activity !== 0}
              <button class="tiny-btn" onclick={() => stopWork(character.slot, character.id)}>
                Stop
              </button>
            {/if}
          </div>
        </div>
      {/each}

      {#each [2, 3] as lockedSlot}
        {#if roster.length < lockedSlot && townHall < SLOT_UNLOCK_TOWN_HALL[lockedSlot - 1]}
          <p class="dim tiny">
            Slot {lockedSlot} unlocks at Town Hall {SLOT_UNLOCK_TOWN_HALL[lockedSlot - 1]} (you
            are at {townHall}).
          </p>
        {/if}
      {/each}

      {#if roster.length === 0}
        <p class="dim">No characters yet.</p>
      {/if}

      <h3>Village</h3>
      <dl class="stats">
        <div><dt>Population</dt><dd>{snap.CurrentPopulationCount}/{snap.CachedMaxPopulationCapacity}</dd></div>
        <div><dt>Town Hall</dt><dd>{snap.TownHallLevel}</dd></div>
        <div><dt>Forge</dt><dd>{snap.ForgeLevel}</dd></div>
        <div><dt>Workshop</dt><dd>{snap.CraftingWorkshopLevel}</dd></div>
      </dl>
    </section>
  </div>
{/if}

<style>
  .rostercard {
    padding: 0.5rem 0;
    border-bottom: 1px solid rgba(255, 255, 255, 0.07);
  }

  .rostercard:last-of-type {
    border-bottom: none;
  }

  .assign {
    display: flex;
    gap: 0.4rem;
    align-items: center;
    margin-top: 0.35rem;
  }

  .assign select {
    flex: 1;
    min-width: 0;
  }

  .equiprow {
    display: flex;
    gap: 0.4rem;
    align-items: center;
    margin-top: 0.3rem;
  }

  .equiprow select {
    flex: 1;
    min-width: 0;
  }

  .race {
    display: inline-flex;
    align-items: center;
    gap: 0.3rem;
  }

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

  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
  }

  h3 {
    margin: 1rem 0 0.35rem;
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
    margin: 0 0 0.5rem;
  }
  .tiny {
    font-size: 0.72rem;
  }
  .pad {
    padding: 1rem;
  }

  .vitals {
    display: grid;
    gap: 0.5rem;
  }

  .hpblock {
    display: grid;
    gap: 0.2rem;
    font-size: 0.78rem;
  }

  .stats {
    display: grid;
    grid-template-columns: repeat(4, 1fr);
    gap: 0.4rem;
    margin: 0;
  }

  .stats div {
    display: grid;
    gap: 0.1rem;
  }

  dt {
    font-size: 0.72rem;
    color: var(--text-dim);
  }

  dd {
    margin: 0;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
  }

  .slots {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.45rem;
  }

  .slots li {
    display: grid;
    grid-template-columns: 5rem 1fr auto;
    gap: 0.5rem;
    align-items: start;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.35rem;
  }

  .slotname {
    font-size: 0.75rem;
    padding-top: 0.1rem;
  }

  .item {
    font-size: 0.85rem;
    min-width: 0;
  }

  .item.empty {
    font-style: italic;
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.45rem;
  }

  .rosterrow {
    display: grid;
    grid-template-columns: 4rem auto 1fr;
    gap: 0.4rem;
    align-items: baseline;
    font-size: 0.82rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.3rem;
    margin-bottom: 0.3rem;
  }

  .rosterrow span:last-child {
    grid-column: 1 / -1;
    color: var(--good);
  }

  .rosterrow span.idle {
    color: var(--text-dim);
  }
</style>
