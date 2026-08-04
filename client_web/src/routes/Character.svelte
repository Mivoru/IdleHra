<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { playerState, visualState, observedMaxPlayerHp, pushLocalNotice } from '../lib/stores/game';
  import { connection } from '../lib/net/connection';
  import { CommandType } from '../lib/net/protocol.generated';
  import { queryKeys, fetchInventory, type InventoryEquipment } from '../lib/net/rest';
  import { loadContent, prettifyBaseId, monsterName, type ContentRegistry } from '../lib/net/content';
  import { EQUIPMENT_SLOTS, agePhaseName, HALT_REASON_SHORT, isGatheringActivity, professionName, resolveSlotIndex, isCraftingActivity, craftingActivityId } from '../lib/ui/slots';
  import { craftingProfessionName } from '../lib/ui/slots';
  import { queryKeys as qk, fetchRecipes } from '../lib/net/rest';
  import { rarityColor, rarityName, shouldGlow } from '../lib/ui/rarity';
  import Affixes from '../lib/ui/Affixes.svelte';
  import Bar from '../lib/ui/Bar.svelte';
  import RaceIcon from '../lib/ui/RaceIcon.svelte';
  import ItemIcon from '../lib/ui/ItemIcon.svelte';
  import { assignCharacterActivity, EMPTY_GUID } from '../lib/net/commands';
  import { locationName, nodeLocation } from '../lib/ui/locations';
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
  // Modul: you could see the seven slots here but only ever empty them. The
  // only place that could EQUIP anything was the chest screen, so gearing up
  // meant leaving the character you were gearing. Candidates are resolved with
  // the same slot rule the server uses, so the list for a slot is exactly what
  // that slot will accept.
  // Modul: worn by ANYONE, not just by the character on screen. This used to
  // read the wire's equipped ids, which are the ACTIVE character's only - so a
  // sword on character 2 was offered to character 1 as free, and the server
  // refused it with nothing visible happening.
  const equippedIds = $derived(
    new Set(
      (inventory.data?.Equipment ?? [])
        .filter((item) => item.EquippedByCharacterSlot >= 0)
        .map((item) => item.Id),
    ),
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
        label: `${professionName(node.ProfessionType)} - ${locationName(nodeLocation(node.ActivityId))}`,
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
      return `${profession} - ${locationName(nodeLocation(activityId))}`;
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
        ].map((c) => ({
          ...c,
          unlocked: townHall >= SLOT_UNLOCK_TOWN_HALL[c.slot - 1],
          occupied: c.id !== EMPTY_GUID,
        }))
      : [],
  );

  // Modul: slot 2 used to appear the moment a character existed for it, with
  // no way to give it a job and nothing saying why - the screen showed a
  // worker who could not work. All three slots are always listed now, and a
  // locked one says which Town Hall level opens it, the way slot 3 already
  // did.
  let selectedSlot = $state(1);
  const selected = $derived(roster.find((c) => c.slot === selectedSlot) ?? roster[0] ?? null);

  // Modul: gear per character, from the inventory snapshot rather than the
  // wire. The hot-path packet deliberately carries only the ACTIVE
  // character's equipment - gear changes on a button press, not ten times a
  // second - so /api/v1/player/inventory's EquippedByCharacterSlot is the only
  // place characters 2 and 3's loadouts exist at all.
  //
  // That field is a ZERO-BASED character slot; this screen numbers slots from
  // one, which is exactly the kind of off-by-one that silently dresses the
  // wrong character.
  function wornBy(slotOneBased: number, equipSlotIndex: number): InventoryEquipment | null {
    for (const item of inventory.data?.Equipment ?? []) {
      if (item.EquippedByCharacterSlot !== slotOneBased - 1) continue;
      if (item.EquippedInSlotIndex === equipSlotIndex) return item;
    }
    return null;
  }

  // Which slot the picker is open on, or -1 for closed.
  let pickerSlot = $state(-1);

  // Split so the figure stands between two even columns. Weapon/offhand are
  // hands and sit lowest on their side; the rest run head to foot.
  const LEFT_SLOTS = EQUIPMENT_SLOTS.filter((sl) => [1, 3, 0].includes(sl.index));
  const RIGHT_SLOTS = EQUIPMENT_SLOTS.filter((sl) => [2, 4, 5, 6].includes(sl.index));
  // Modul: the three tool slots, in their own row under the figure. They are
  // gear like any other piece - rolled rarity, rolled affixes - but they are
  // what a character WORKS with rather than what they fight in, and mixing
  // them into the armour columns loses that distinction.
  const TOOL_SLOTS = EQUIPMENT_SLOTS.filter((sl) => [7, 8, 9].includes(sl.index));

  // Modul: BOTH COMMANDS NAME THE CHARACTER. EquipItem and UnequipItem have
  // carried a TargetGuid since per-character equipment landed, and Guid.Empty
  // resolves to the main character - so sending it without one silently
  // dressed slot 1 no matter which character was on screen. Caught by the
  // paper doll's own test: wearing an item filled no slot.
  function equipInstance(instanceId: number) {
    if (!selected) return;
    connection.send({
      Command: CommandType.EquipItem,
      TargetId: instanceId,
      TargetGuid: selected.id,
    });
    pickerSlot = -1;
    setTimeout(() => inventory.refetch(), 700);
  }

  // Modul: UnequipItem's TargetId is a SLOT INDEX (0 Weapon, 1 Helmet,
  // 2 Chest, 3 Gloves, 4 Leggings, 5 Boots, 6 Offhand) - NOT an item instance
  // id, which is what EquipItem takes. Sending the instance id addresses slot
  // 8, or 42, or whatever the id happened to be: out of range, silently
  // ignored, no error anywhere.
  //
  // The asymmetry is real and easy to miss: equip names the ITEM, unequip
  // names the SLOT, because a slot is what you are emptying.
  function unequip(slotIndex: number) {
    connection.send({
      Command: CommandType.UnequipItem,
      TargetId: slotIndex,
      TargetGuid: selected?.id ?? EMPTY_GUID,
    });
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

    <section class="panel doll">
      <h2>Equipment</h2>

      <!-- Modul: the seven slots were a LIST, with a dropdown and an Equip
           button stapled under each row, sitting in the same panel that gave
           characters their jobs. Dressing someone and telling them what to do
           are different acts and looked identical.

           This is a paper doll: the character in the middle, their gear either
           side of them, and a slot is a thing you click. -->
      <div class="who">
        {#each roster as character (character.slot)}
          <button
            class="slottab"
            class:on={selectedSlot === character.slot}
            disabled={!character.unlocked || !character.occupied}
            onclick={() => { selectedSlot = character.slot; pickerSlot = -1; }}
          >
            Slot {character.slot}
            {#if !character.unlocked}
              <span class="dim tiny">Town Hall {SLOT_UNLOCK_TOWN_HALL[character.slot - 1]}</span>
            {:else if !character.occupied}
              <span class="dim tiny">empty</span>
            {:else}
              <span class="dim tiny">{raceName(character.raceId)}</span>
            {/if}
          </button>
        {/each}
      </div>

      {#if !selected || !selected.occupied}
        <p class="dim small">No character in this slot.</p>
      {:else}
        <div class="rig">
          <div class="column">
            {#each LEFT_SLOTS as slot (slot.index)}
              {@const item = wornBy(selected.slot, slot.index)}
              <button
                class="gearslot"
                class:filled={item !== null}
                class:open={pickerSlot === slot.index}
                onclick={() => (pickerSlot = pickerSlot === slot.index ? -1 : slot.index)}
              >
                <span class="slotname dim tiny">{slot.label}</span>
                {#if item}
                  <ItemIcon baseItemId={item.BaseItemId} name={prettifyBaseId(item.BaseItemId)} qualityTier={item.QualityTier} size="md" />
                  <span
                    class="gearname"
                    style="color: {rarityColor(item.QualityTier)}"
                    class:rarity-glow={shouldGlow(item.QualityTier)}
                  >{prettifyBaseId(item.BaseItemId)}</span>
                {:else}
                  <span class="dim tiny">empty</span>
                {/if}
              </button>
            {/each}
          </div>

          <div class="figure">
            <RaceIcon raceId={selected.raceId} size="xl" />
            <strong>{raceName(selected.raceId)}</strong>
            <span class="dim tiny">{agePhaseName(selected.agePhase)}</span>
            <span class="dim tiny" class:idle={selected.activity === 0}>
              {activityLabel(selected.activity, selected.halt)}
            </span>
          </div>

          <div class="column">
            {#each RIGHT_SLOTS as slot (slot.index)}
              {@const item = wornBy(selected.slot, slot.index)}
              <button
                class="gearslot"
                class:filled={item !== null}
                class:open={pickerSlot === slot.index}
                onclick={() => (pickerSlot = pickerSlot === slot.index ? -1 : slot.index)}
              >
                <span class="slotname dim tiny">{slot.label}</span>
                {#if item}
                  <ItemIcon baseItemId={item.BaseItemId} name={prettifyBaseId(item.BaseItemId)} qualityTier={item.QualityTier} size="md" />
                  <span
                    class="gearname"
                    style="color: {rarityColor(item.QualityTier)}"
                    class:rarity-glow={shouldGlow(item.QualityTier)}
                  >{prettifyBaseId(item.BaseItemId)}</span>
                {:else}
                  <span class="dim tiny">empty</span>
                {/if}
              </button>
            {/each}
          </div>
        </div>

        <div class="tools">
          {#each TOOL_SLOTS as slot (slot.index)}
            {@const item = wornBy(selected.slot, slot.index)}
            <button
              class="gearslot"
              class:filled={item !== null}
              class:open={pickerSlot === slot.index}
              onclick={() => (pickerSlot = pickerSlot === slot.index ? -1 : slot.index)}
            >
              <span class="slotname dim tiny">{slot.label}</span>
              {#if item}
                <ItemIcon
                  baseItemId={item.BaseItemId}
                  name={prettifyBaseId(item.BaseItemId)}
                  qualityTier={item.QualityTier}
                  size="md"
                />
                <span
                  class="gearname"
                  style="color: {rarityColor(item.QualityTier)}"
                  class:rarity-glow={shouldGlow(item.QualityTier)}
                >{prettifyBaseId(item.BaseItemId)}</span>
              {:else}
                <span class="dim tiny">empty</span>
              {/if}
            </button>
          {/each}
        </div>

        {#if pickerSlot >= 0}
          {@const slot = EQUIPMENT_SLOTS.find((sl) => sl.index === pickerSlot)}
          {@const worn = wornBy(selected.slot, pickerSlot)}
          {@const candidates = candidatesBySlot.get(pickerSlot) ?? []}
          <div class="picker">
            <header>
              <strong>{slot?.label}</strong>
              <button class="tiny-btn" onclick={() => (pickerSlot = -1)}>Close</button>
            </header>

            {#if worn}
              <div class="wornrow">
                <span>Wearing {prettifyBaseId(worn.BaseItemId)} [{rarityName(worn.QualityTier)}]</span>
                <button class="tiny-btn" onclick={() => unequip(pickerSlot)}>Take off</button>
              </div>
              <Affixes affixes={worn.Affixes} />
            {/if}

            {#if candidates.length === 0}
              <p class="dim tiny">Nothing in the chest fits this slot.</p>
            {:else}
              <ul class="choices">
                {#each candidates as candidate (candidate.Id)}
                  <li>
                    <ItemIcon baseItemId={candidate.BaseItemId} name={prettifyBaseId(candidate.BaseItemId)} qualityTier={candidate.QualityTier} size="sm" />
                    <span
                      style="color: {rarityColor(candidate.QualityTier)}"
                      class:rarity-glow={shouldGlow(candidate.QualityTier)}
                    >{prettifyBaseId(candidate.BaseItemId)}</span>
                    <span class="dim tiny">[{rarityName(candidate.QualityTier)}]</span>
                    <button class="tiny-btn" onclick={() => equipInstance(candidate.Id)}>Wear</button>
                  </li>
                {/each}
              </ul>
            {/if}
          </div>
        {/if}
      {/if}
    </section>

    <section class="panel">
      <h2>Work</h2>
      <p class="dim small">
        Who is doing what. Gear lives on the Equipment panel - these are two
        different jobs and used to share one row.
      </p>

      {#each roster as character}
        <div class="rostercard" class:locked={!character.unlocked}>
          <div class="rosterrow">
            <strong>Slot {character.slot}</strong>
            {#if character.unlocked && character.occupied}
              <span class="race">
                <RaceIcon raceId={character.raceId} />
                {raceName(character.raceId)}
              </span>
              <span class="dim">{agePhaseName(character.agePhase)}</span>
              <span class:idle={character.activity === 0}>
                {activityLabel(character.activity, character.halt)}
              </span>
            {:else if !character.unlocked}
              <span class="dim">
                Locked - opens at Town Hall {SLOT_UNLOCK_TOWN_HALL[character.slot - 1]}
                (you are at {townHall})
              </span>
            {:else}
              <span class="dim">Empty - breed a character to fill it</span>
            {/if}
          </div>

          {#if character.unlocked && character.occupied}
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
          {/if}
        </div>
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
  .tools {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 0.4rem;
    margin-top: 0.6rem;
  }

  .doll .who {
    display: flex;
    gap: 0.4rem;
    margin-bottom: 0.8rem;
    flex-wrap: wrap;
  }

  .slottab {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 0.1rem;
    padding: 0.35rem 0.6rem;
    border-radius: var(--radius, 6px);
    border: 1px solid rgba(255, 255, 255, 0.14);
    background: rgba(255, 255, 255, 0.03);
    cursor: pointer;
    width: auto;
    font-size: 0.82rem;
  }

  .slottab.on {
    border-color: var(--accent, #7aa2f7);
    background: rgba(122, 162, 247, 0.12);
  }

  .slottab:disabled {
    cursor: not-allowed;
    opacity: 0.5;
  }

  .rig {
    display: grid;
    grid-template-columns: 1fr auto 1fr;
    gap: 0.8rem;
    align-items: start;
  }

  .rig .column {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }

  .figure {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.25rem;
    text-align: center;
    padding-top: 0.6rem;
  }

  .gearslot {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.15rem;
    padding: 0.4rem;
    border-radius: var(--radius, 6px);
    border: 1px dashed rgba(255, 255, 255, 0.16);
    background: rgba(255, 255, 255, 0.02);
    cursor: pointer;
    width: 100%;
    min-height: 4.4rem;
  }

  .gearslot.filled {
    border-style: solid;
  }

  .gearslot.open {
    border-color: var(--accent, #7aa2f7);
  }

  .gearname {
    font-size: 0.72rem;
    line-height: 1.15;
    word-break: break-word;
  }

  .picker {
    margin-top: 0.9rem;
    padding: 0.6rem;
    border-radius: var(--radius, 6px);
    border: 1px solid rgba(255, 255, 255, 0.14);
    background: rgba(255, 255, 255, 0.03);
  }

  .picker header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: 0.4rem;
  }

  .wornrow {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
    font-size: 0.85rem;
    margin-bottom: 0.3rem;
  }

  .choices {
    list-style: none;
    margin: 0.4rem 0 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
    max-height: 16rem;
    overflow-y: auto;
  }

  .choices li {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    font-size: 0.85rem;
  }

  .choices li button {
    margin-left: auto;
  }

  .rostercard.locked {
    opacity: 0.55;
  }

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

  .slotname {
    font-size: 0.75rem;
    padding-top: 0.1rem;
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
