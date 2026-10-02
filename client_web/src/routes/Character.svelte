<script lang="ts">
  // Modul: TASK 97 - GEAR FIRST ON A PHONE.
  //
  // At 390 px the gear slots sat about 1,500 px down, behind a health bar, a
  // combat rating block and four attribute cards - while the deed a new player
  // is following says "Open Character and tap the weapon slot". The screen also
  // never said WHO it was about: the title was the word "Character", the name
  // appeared only in Orders, and the slot switcher lived inside Equipment even
  // though the rating beside it was per-character too.
  //
  // So: one sticky person switcher at the top that drives every panel, then
  // three tabs - Gear (default; all eleven slots as one icon grid), Attributes
  // and Work & orders. Attributes opens first only when there are points to
  // place AND a weapon to fight with; before the weapon, gear is the thing to
  // do, and the tutorial's weapon step must find the slot without a tab press
  // (the guided layer fences every other control).
  import { formatNumber, numberTitle } from '../lib/ui/format';
  import { requestScreen } from '../lib/stores/navigation';
  import PlayerAvatar from '../lib/ui/PlayerAvatar.svelte';
  import { unopenedChests } from '../lib/stores/cosmeticChests';
  import { createQuery } from '@tanstack/svelte-query';
  import { playerState, visualState, pushLocalNotice, connectionStatus } from '../lib/stores/game';
  import { connection } from '../lib/net/connection';
  import { CommandType } from '../lib/net/protocol.generated';
  import {
    queryKeys,
    fetchInventory,
    fetchRecipes,
    fetchBreedingRoster,
    fetchCombatProjection,
    type InventoryEquipment,
  } from '../lib/net/rest';
  import { loadContent, prettifyBaseId, monsterName, type ContentRegistry } from '../lib/net/content';
  import {
    EQUIPMENT_SLOTS,
    agePhaseName,
    HALT_REASON_SHORT,
    isGatheringActivity,
    isCombatActivity,
    professionName,
    resolveSlotIndex,
    isCraftingActivity,
    craftingActivityId,
    craftingProfessionName,
    SLOT_UNLOCK_TOWN_HALL,
    SLOT_AXE,
    SLOT_PICKAXE,
    SLOT_ROD,
  } from '../lib/ui/slots';
  import { rarityColor, rarityName, shouldGlow } from '../lib/ui/rarity';
  import Affixes from '../lib/ui/Affixes.svelte';
  import Bar from '../lib/ui/Bar.svelte';
  import RaceIcon from '../lib/ui/RaceIcon.svelte';
  import ItemIcon from '../lib/ui/ItemIcon.svelte';
  import Hint from '../lib/ui/Hint.svelte';
  import DisabledReason from '../lib/ui/DisabledReason.svelte';
  import { pickerRows } from '../lib/ui/equipPicker';
  import { assignCharacterActivity, EMPTY_GUID } from '../lib/net/commands';
  import AttributePanel from '../lib/ui/AttributePanel.svelte';
  import AutomationRulesPanel from '../lib/ui/AutomationRulesPanel.svelte';
  import { ATTRIBUTES, equipRequirement } from '../lib/net/commands';
  import { locationName, nodeLocation } from '../lib/ui/locations';
  import { raceName } from '../lib/ui/races';
  import { writePref } from '../lib/net/prefs';
  import { lastActivityKey } from '../lib/ui/homeNow';
  import { onMount } from 'svelte';
  import { play } from '../lib/ui/audio';
  import QueryError from '../lib/ui/QueryError.svelte';

  const inventory = createQuery(() => ({ queryKey: queryKeys.inventory, queryFn: fetchInventory }));
  // Recipes carry no id of their own on the wire - the crafting activity id is
  // the recipe's INDEX in this list, which is the same order the server holds.
  const recipeList = createQuery(() => ({ queryKey: queryKeys.recipes, queryFn: fetchRecipes }));
  // Modul: names come from the breeding roster, the one place the server
  // publishes a character's name (HomeCards reads the same key, task 73).
  const names = createQuery(() => ({ queryKey: queryKeys.breedingRoster, queryFn: fetchBreedingRoster, staleTime: 60_000 }));
  const nameById = $derived(new Map((names.data ?? []).map((c) => [c.CharacterId, c.Name])));

  let registry = $state<ContentRegistry | null>(null);
  onMount(async () => {
    registry = await loadContent().catch(() => null);
  });

  const snap = $derived($playerState);

  // Modul: the attribute pool and the four values, straight off the wire -
  // StateUpdatePacket carries STR/DEX/CON/LCK already and gained
  // UnspentAttributePoints when levelling stopped allocating them. They are the
  // ACCOUNT's (PlayerRecords), not one character's, which is why the
  // Attributes tab does not change with the switcher.
  const attributePoints = $derived(Number(snap?.UnspentAttributePoints ?? 0));

  // Named rather than indexed: the four are real typed fields on the packet,
  // and a string lookup would go stale silently if one were ever renamed.
  function attributeValue(key: string): number {
    if (!snap) return 0;
    if (key === 'STR') return Number(snap.STR ?? 0);
    if (key === 'DEX') return Number(snap.DEX ?? 0);
    if (key === 'CON') return Number(snap.CON ?? 0);
    return Number(snap.LCK ?? 0);
  }

  // Modul: WHAT A PIECE ASKS BEFORE YOU PRESS WEAR.
  //
  // Gear has attribute minimums now (EquipmentAttributeGate). The server
  // refuses correctly either way, but a refusal a player could not see coming
  // is the failure this codebase keeps finding at the bottom of "the button
  // does nothing" - so the requirement is on the row, and the row says whether
  // this character meets it.
  function requirementFor(baseItemId: string): { label: string; minimum: number; met: boolean } | null {
    const slotIndex = resolveSlotIndex(baseItemId);
    if (slotIndex < 0) return null;

    const definition = registry?.itemsByBaseId.get(baseItemId);
    const requirement = equipRequirement(slotIndex, Number(definition?.RegionTier ?? 0));
    if (!requirement) return null;

    const attribute = ATTRIBUTES.find((a) => a.id === requirement.attribute);
    if (!attribute) return null;

    return {
      label: attribute.label,
      minimum: requirement.minimum,
      met: attributeValue(attribute.key) >= requirement.minimum,
    };
  }

  const visual = $derived($visualState);
  // The wire's own maximum, as Combat reads it. Clamped against PlayerHp so a
  // bar can never read over-full while a stat change is still propagating.
  const playerMaxHp = $derived(Math.max(1, $playerState?.PlayerMaxHp ?? 0, $playerState?.PlayerHp ?? 0));

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
  let selectedSlot = $state(1);

  // Modul: StateUpdate's three combat rating fields are the ACTIVE character's
  // only. /api/v1/player/inventory carries each character's own rating,
  // computed from that character's own gear - looked up by slot rather than
  // trusting the wire for anyone but the active character.
  const selectedCombatStats = $derived(
    inventory.data?.RosterCombatStats?.find((s) => s.SlotIndex === selectedSlot - 1) ?? null,
  );

  // Modul: set bonuses are READ from the server (task 63). The numbers below
  // are the server's own SetBonusEngine evaluation of this character's gear.
  const activeSets = $derived(selectedCombatStats?.ActiveSets ?? []);

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

  // SimulationEngine routes ChangeActivity by TargetGuid, and
  // CharacterSlotEngine unlocks slot 2 at Town Hall 3 and slot 3 at Town Hall 5.
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
    const who = (slot: number, id: string) => personName(id, 0) || `Slot ${slot}`;
    if (bySlot !== 1 && Number(snap.ActiveActivityId) === activityId) return who(1, snap.Slot1_CharacterId);
    if (bySlot !== 2 && snap.Slot2ActivityId === activityId) return who(2, snap.Slot2_CharacterId);
    if (bySlot !== 3 && snap.Slot3ActivityId === activityId) return who(3, snap.Slot3_CharacterId);
    return null;
  }

  let jobPick = $state<Record<number, number>>({});

  function assign(slot: number, characterId: string) {
    const activityId = jobPick[slot] ?? 0;
    const outcome = assignCharacterActivity(characterId, activityId, {
      unlocked: townHall >= SLOT_UNLOCK_TOWN_HALL[slot - 1],
      takenBy: occupiedBy(activityId, slot),
    });
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    // Task 73: Home's "Continue" resumes whatever was given last, wherever.
    if (activityId > 0) writePref(lastActivityKey(characterId), String(activityId));
  }

  function stopWork(slot: number, characterId: string) {
    const outcome = assignCharacterActivity(characterId, 0);
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
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
    return `Fighting ${monsterName(registry, activityId)}`;
  }

  /** The person's name, or their race until the roster has answered. */
  function personName(id: string, raceId: number): string {
    return nameById.get(id) || (raceId > 0 ? raceName(raceId) : '');
  }

  const roster = $derived(
    snap
      ? [
          {
            slot: 1,
            id: snap.Slot1_CharacterId,
            agePhase: snap.Slot1_AgePhase,
            raceId: snap.Slot1_RaceId,
            activity: Number(snap.ActiveActivityId),
            halt: snap.ActivityHaltReason,
          },
          {
            slot: 2,
            id: snap.Slot2_CharacterId,
            agePhase: snap.Slot2_AgePhase,
            raceId: snap.Slot2_RaceId,
            activity: snap.Slot2ActivityId,
            halt: snap.Slot2ActivityHaltReason,
          },
          {
            slot: 3,
            id: snap.Slot3_CharacterId,
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

  // Modul: the switcher offers only people who can actually be acted on - a
  // slot behind a Town Hall level, or one nobody stands in, is a line on the
  // Work tab saying why, not a person to select.
  const people = $derived(roster.filter((c) => c.unlocked && c.occupied));
  const idleSlots = $derived(roster.filter((c) => !c.unlocked || !c.occupied));

  const selected = $derived(people.find((c) => c.slot === selectedSlot) ?? people[0] ?? null);

  function choosePerson(slot: number) {
    selectedSlot = slot;
    pickerSlot = -1;
  }

  // ---------------------------------------------------------------- tabs
  type Tab = 'gear' | 'attributes' | 'work';
  let tab = $state<Tab | null>(null);

  // Modul: THE DEFAULT IS DECIDED ONCE, at the first snapshot, and then left
  // alone. Re-deriving it would yank a player off Attributes the moment they
  // placed their last point. Attributes first only when there are points AND
  // the weapon slot is filled: a character with no weapon has a more urgent
  // thing to do, and it is the thing the tutorial's second step asks for.
  // EquippedWeaponId is the wire's, i.e. the active character's, who is also
  // the one the switcher opens on.
  $effect(() => {
    if (tab !== null || !snap) return;
    tab = attributePoints > 0 && Number(snap.EquippedWeaponId) > 0 ? 'attributes' : 'gear';
  });
  const shownTab = $derived<Tab>(tab ?? 'gear');

  function chooseTab(next: Tab) {
    tab = next;
    pickerSlot = -1;
  }

  // ---------------------------------------------------------------- DPS
  // Modul: "Skill pts" sat in the combat rating and linked nowhere - the tree
  // has its own screen. A rating wants a number for how hard this person hits.
  // There is no attack figure on any wire, and a client-side damage model is
  // the thing this codebase keeps deleting (huntingEstimate.ts), so this is the
  // SERVER's projection (HuntingProjection) divided out: a monster's health
  // over the projected time to kill it - misses, crits and its armour
  // included. Against whoever this person is fighting, else the first monster.
  const projection = createQuery(() => ({
    queryKey: queryKeys.combatProjection(Math.max(0, (selected?.slot ?? 1) - 1)),
    queryFn: () => fetchCombatProjection(Math.max(0, (selected?.slot ?? 1) - 1)),
    enabled: $connectionStatus.phase === 'live' && shownTab === 'gear' && selected !== null,
    staleTime: 60_000,
    retry: false,
  }));

  const dps = $derived.by(() => {
    if (!registry || !selected) return null;
    const estimates = projection.data?.Monsters ?? [];
    if (estimates.length === 0) return null;
    const fighting = isCombatActivity(selected.activity) ? selected.activity : 0;
    const firstId = registry.regions[0]?.[0]?.Id ?? 0;
    const estimate =
      estimates.find((e) => e.MonsterId === fighting) ?? estimates.find((e) => e.MonsterId === firstId) ?? null;
    const monster = estimate ? registry.monsters.get(estimate.MonsterId) : undefined;
    if (!estimate || !monster) return null;
    if (!estimate.CanDamage || estimate.SecondsPerKill <= 0) return { value: 0, against: monster.Name };
    return { value: monster.MaxHp / estimate.SecondsPerKill, against: monster.Name };
  });

  // ---------------------------------------------------------------- gear
  // Modul: gear per character, from the inventory snapshot rather than the
  // wire. The hot-path packet carries only the ACTIVE character's equipment,
  // so /api/v1/player/inventory's EquippedByCharacterSlot is the only place
  // characters 2 and 3's loadouts exist at all. That field is ZERO-BASED; this
  // screen numbers slots from one.
  //
  // INDEXED ONCE, not scanned per slot: eleven boxes on each of three
  // characters against 17,836 pieces was 600,000 iterations a render.
  const wornByIndex = $derived.by(() => {
    const index = new Map<string, InventoryEquipment>();
    for (const item of inventory.data?.Equipment ?? []) {
      if (item.EquippedByCharacterSlot < 0) continue;
      index.set(`${item.EquippedByCharacterSlot}:${item.EquippedInSlotIndex}`, item);
    }
    return index;
  });

  function wornBy(slotOneBased: number, equipSlotIndex: number): InventoryEquipment | null {
    return wornByIndex.get(`${slotOneBased - 1}:${equipSlotIndex}`) ?? null;
  }

  // Which slot the picker is open on, or -1 for closed.
  let pickerSlot = $state(-1);

  // Modul: ALL ELEVEN, in slot order - the eight combat slots, then the three
  // tools, which land on the grid's third row on their own. Every list in this
  // codebase that stopped at eight was a bug (root CLAUDE.md).
  const TOOL_INDICES: readonly number[] = [SLOT_AXE, SLOT_PICKAXE, SLOT_ROD];

  // Modul: BOTH COMMANDS NAME THE CHARACTER. EquipItem and UnequipItem carry a
  // TargetGuid, and Guid.Empty resolves to the main character - so sending it
  // without one silently dressed slot 1 no matter who was on screen.
  function equipInstance(instanceId: number) {
    if (!selected) return;
    connection.send({
      Command: CommandType.EquipItem,
      TargetId: instanceId,
      TargetGuid: selected.id,
    });
    // On send, like every command: a refusal answers with the error tone.
    play('itemEquipped');
    pickerSlot = -1;
  }

  // Modul: UnequipItem's TargetId is a SLOT INDEX (0-10) - NOT an item
  // instance id, which is what EquipItem takes. Equip names the ITEM, unequip
  // names the SLOT, because a slot is what you are emptying.
  function unequip(slotIndex: number) {
    connection.send({
      Command: CommandType.UnequipItem,
      TargetId: slotIndex,
      TargetGuid: selected?.id ?? EMPTY_GUID,
    });
  }

</script>

{#if !snap}
  <p class="dim pad">Waiting for the first state snapshot...</p>
{:else}
  <div class="page">
    <!-- The person switcher. Sticky, so whoever the panels below describe is
         never scrolled out of sight. -->
    <section class="switcher" data-testid="person-switcher">
      {#if selected}
        <div class="me" data-testid="person-current" data-character-id={selected.id}>
          <span class="portrait"><RaceIcon raceId={selected.raceId} size="md" /></span>
          <div class="ident">
            <div class="nameline">
              <strong class="name" data-testid="person-name">{personName(selected.id, selected.raceId)}</strong>
              <span class="lv">Lv {formatNumber(Number(snap.CurrentLevel))}</span>
            </div>
            <div class="sub">
              <span>{raceName(selected.raceId)} · {agePhaseName(selected.agePhase)}</span>
              <span class="act" class:idle={selected.activity === 0}>{activityLabel(selected.activity, selected.halt)}</span>
            </div>
          </div>
          <!-- Task 54: the face other players see, and the way to the
               Wardrobe - with a count when a chest is waiting. -->
          <button class="face" onclick={() => requestScreen('wardrobe')} aria-label="Open the Wardrobe" data-testid="character-wardrobe">
            <PlayerAvatar playerId={Number(snap.PlayerId)} size="sm" />
            {#if $unopenedChests > 0}<span class="chest-badge">{$unopenedChests}</span>{/if}
          </button>
        </div>
      {:else}
        <p class="dim small">No character in a slot yet.</p>
      {/if}

      {#if people.length > 1}
        <div class="people" role="group" aria-label="Choose a person">
          {#each people as person (person.slot)}
            <button
              class="person"
              class:on={selected?.slot === person.slot}
              aria-pressed={selected?.slot === person.slot}
              data-person-slot={person.slot}
              data-character-id={person.id}
              onclick={() => choosePerson(person.slot)}
            >
              <RaceIcon raceId={person.raceId} />
              <span class="pname">{personName(person.id, person.raceId)}</span>
            </button>
          {/each}
        </div>
      {/if}
    </section>

    <div class="tabs" role="tablist" aria-label="Character">
      <button role="tab" aria-selected={shownTab === 'gear'} class:on={shownTab === 'gear'} data-character-tab="gear" onclick={() => chooseTab('gear')}>Gear</button>
      <button role="tab" aria-selected={shownTab === 'attributes'} class:on={shownTab === 'attributes'} data-character-tab="attributes" onclick={() => chooseTab('attributes')}>
        Attributes{#if attributePoints > 0}<span class="badge" aria-label="{attributePoints} unspent">{formatNumber(attributePoints)}</span>{/if}
      </button>
      <button role="tab" aria-selected={shownTab === 'work'} class:on={shownTab === 'work'} data-character-tab="work" onclick={() => chooseTab('work')}>Work &amp; orders</button>
    </div>

    {#if shownTab === 'gear'}
      <section class="panel" data-testid="character-gear">
        {#if !selected}
          <p class="dim small">No character in this slot.</p>
        {:else if inventory.isError && inventory.data === undefined}
          <!-- Modul: the grid is drawn from the inventory, so without it every
               slot reads as empty - a character stripped of gear they still own. -->
          <QueryError query={inventory} what="your equipment" />
        {:else}
          <!-- Modul: health is on the wire for the ACTIVE character only
               (slot 1); slots 2 and 3 have no live health anywhere, so they
               get no bar rather than slot 1's. -->
          {#if selected.slot === 1}
            <div class="hpblock">
              <span class="dim">Health</span>
              <Bar
                value={visual?.PlayerHp ?? snap.PlayerHp}
                max={playerMaxHp}
                color="var(--good)"
                label={`${formatNumber(Math.round(visual?.PlayerHp ?? snap.PlayerHp))} / ${formatNumber(playerMaxHp)}`}
              />
            </div>
          {/if}
          <!-- Modul: the server-COMPUTED values used in combat resolution, not
               a client reconstruction. The active slot falls back to the 10 Hz
               wire while /inventory is loading. -->
          <dl class="stats" data-testid="combat-rating">
            <div>
              <dt>
                {#if dps}
                  <Hint text={`Damage a second against ${dps.against}: its health divided by the server's projected time to kill it, misses, crits and its armour included.`}>DPS</Hint>
                {:else}DPS{/if}
              </dt>
              <dd>{dps ? formatNumber(Math.round(dps.value)) : '-'}</dd>
            </div>
            <div>
              <dt>Accuracy</dt>
              <dd title={numberTitle(selectedCombatStats?.Accuracy ?? snap.PlayerAccuracyRating)}>{formatNumber(selectedCombatStats?.Accuracy ?? snap.PlayerAccuracyRating)}</dd>
            </div>
            <div>
              <dt>Armour</dt>
              <dd title={numberTitle(selectedCombatStats?.Armor ?? snap.PlayerArmorRating)}>{formatNumber(selectedCombatStats?.Armor ?? snap.PlayerArmorRating)}</dd>
            </div>
            <div>
              <dt>Block</dt>
              <dd
                >{(selectedCombatStats?.BlockPct ??
                  (typeof snap.PlayerBlockStrengthPct === 'number' ? snap.PlayerBlockStrengthPct : 0)
                ).toFixed(1)}%</dd
              >
            </div>
          </dl>

          <!-- Modul: a slot is a thing you tap. One 4-column grid of all eleven
               - it replaced a paper doll whose figure pushed the slots below
               the first screen on a phone. -->
          <div class="slots">
            {#each EQUIPMENT_SLOTS as slot (slot.index)}
              {@const item = wornBy(selected.slot, slot.index)}
              <button
                class="gearslot"
                class:tool={TOOL_INDICES.includes(slot.index)}
                class:filled={item !== null}
                class:open={pickerSlot === slot.index}
                style={item ? `--slot-rarity: ${rarityColor(item.QualityTier)}` : undefined}
                data-guide="slot-{slot.index}"
                data-slot-index={slot.index}
                data-item-id={item ? item.Id : ''}
                aria-label={item ? `${slot.label}: ${prettifyBaseId(item.BaseItemId)}` : `${slot.label}: empty`}
                aria-expanded={pickerSlot === slot.index}
                onclick={() => (pickerSlot = pickerSlot === slot.index ? -1 : slot.index)}
              >
                <span class="slotname">{slot.label}</span>
                {#if item}
                  <ItemIcon baseItemId={item.BaseItemId} name={prettifyBaseId(item.BaseItemId)} qualityTier={item.QualityTier} size="md" />
                  <span class="gearname" style="color: {rarityColor(item.QualityTier)}">{prettifyBaseId(item.BaseItemId)}</span>
                {:else}
                  <span class="empty" aria-hidden="true">+</span>
                  <span class="gearname dim">empty</span>
                {/if}
              </button>
            {/each}
          </div>

          {#if pickerSlot >= 0}
            {@const slot = EQUIPMENT_SLOTS.find((sl) => sl.index === pickerSlot)}
            {@const worn = wornBy(selected.slot, pickerSlot)}
            {@const candidates = candidatesBySlot.get(pickerSlot) ?? []}
            {@const picked = pickerRows(candidates)}
            <div class="picker" data-testid="equip-picker" data-slot-index={pickerSlot}>
              <header>
                <strong>{slot?.label}</strong>
                <button class="tiny-btn" onclick={() => (pickerSlot = -1)}>Close</button>
              </header>

              {#if worn}
                <div class="wornrow">
                  <span class="wornname">Wearing <span style="color: {rarityColor(worn.QualityTier)}">{prettifyBaseId(worn.BaseItemId)}</span> [{rarityName(worn.QualityTier)}]</span>
                  <button class="tiny-btn" data-testid="take-off" data-piece-id={worn.Id} onclick={() => unequip(pickerSlot)}>Take off</button>
                </div>
                <Affixes affixes={worn.Affixes} baseItemId={worn.BaseItemId} qualityTier={worn.QualityTier} />
              {/if}

              {#if candidates.length === 0}
                <p class="dim tiny">Nothing in the chest fits this slot.</p>
              {:else}
                <ul class="choices">
                  {#each picked.rows as { piece: candidate, count }, candidateIndex (candidate.Id)}
                    {@const req = requirementFor(candidate.BaseItemId)}
                    <li>
                      <ItemIcon baseItemId={candidate.BaseItemId} name={prettifyBaseId(candidate.BaseItemId)} qualityTier={candidate.QualityTier} size="sm" />
                      <span class="cname">
                        <span
                          style="color: {rarityColor(candidate.QualityTier)}"
                          class:rarity-glow={shouldGlow(candidate.QualityTier)}
                        >{prettifyBaseId(candidate.BaseItemId)}</span>
                        <span class="dim tiny">[{rarityName(candidate.QualityTier)}]</span>
                        {#if count > 1}
                          <span class="dim tiny">&times;{formatNumber(count)} identical</span>
                        {/if}
                        {#if req}
                          <span class="req" class:unmet={!req.met}>needs {req.minimum} {req.label}</span>
                        {/if}
                      </span>
                      <button
                        class="tiny-btn"
                        data-guide={candidateIndex === 0 ? 'wear-first' : undefined}
                        data-piece-id={candidate.Id}
                        onclick={() => equipInstance(candidate.Id)}
                      >Wear</button>
                    </li>
                  {/each}
                </ul>
                {#if picked.hiddenRows > 0}
                  <p class="dim tiny">
                    +{formatNumber(picked.hiddenPieces)} more -
                    <button class="tiny-btn" onclick={() => requestScreen('chest')}>open the Chest</button>
                    to filter them.
                  </p>
                {/if}
              {/if}
            </div>
          {/if}

          <!-- The set line: one row per worn set, the detail behind a tap. -->
          {#if activeSets.length > 0}
            <ul class="sets" data-testid="set-line">
              {#each activeSets as set (set.SetId)}
                {@const left = set.NextTierPieces > 0 ? set.NextTierPieces - set.Pieces : 0}
                <li>
                  <Hint
                    text={`${set.Offensive ? 'Offence' : 'Defence'} set. ${set.Burn ? 'Burn: each hit adds a quarter again as fire. ' : ''}${set.Thorns ? 'Thorns: a fifth of each hit taken is returned. ' : ''}${set.DamageCap ? 'Bulwark: no single hit takes more than 20% of your health. ' : ''}${left > 0 ? `${left} more piece${left === 1 ? '' : 's'} for the next tier.` : 'Top tier.'} Rarity scales it: x${set.QualityScale.toFixed(2)}.`}
                  >
                    <span class="set-name">Set: {set.Family} {set.Pieces}/5</span>
                  </Hint>
                  {#if set.DamagePct > 0}<span class="good"> · +{set.DamagePct}% damage</span>{/if}
                  {#if set.ArmorPct > 0}<span class="good"> · +{set.ArmorPct}% armour</span>{/if}
                  {#if set.Burn}<span class="accent"> · Burn</span>{/if}
                  {#if set.Thorns}<span class="accent"> · Thorns</span>{/if}
                  {#if set.DamageCap}<span class="accent"> · Bulwark</span>{/if}
                </li>
              {/each}
            </ul>
          {:else}
            <p class="dim tiny sets-none">No armour set yet - two pieces of one family start a set bonus.</p>
          {/if}

          {#if $unopenedChests > 0}
            <button class="tiny-btn chests" onclick={() => requestScreen('wardrobe')}>
              {$unopenedChests} cosmetic chest{$unopenedChests === 1 ? '' : 's'} to open
            </button>
          {/if}
        {/if}
      </section>
    {:else if shownTab === 'attributes'}
      <section class="panel" data-testid="character-attributes">
        <p class="dim tiny shared">Attributes belong to your whole house - every character fights with them.</p>
        <AttributePanel
          values={{ STR: attributeValue('STR'), DEX: attributeValue('DEX'), CON: attributeValue('CON'), LCK: attributeValue('LCK') }}
          unspent={attributePoints}
          onnotice={(message) => pushLocalNotice(message, 'error')}
        />
      </section>
    {:else}
      <div class="workgrid">
        <section class="panel" data-testid="character-work">
          <h2>Work</h2>
          {#if selected}
            {@const pick = jobPick[selected.slot] ?? 0}
            {@const taken = occupiedBy(pick, selected.slot)}
            <div class="rostercard">
              <p class="small">
                <span class="dim">Now:</span>
                <span class="nowjob" class:idle={selected.activity === 0}>{activityLabel(selected.activity, selected.halt)}</span>
              </p>
              <div class="assign">
                <select bind:value={jobPick[selected.slot]} aria-label="Job for {personName(selected.id, selected.raceId)}">
                  <option value={0}>Choose a job...</option>
                  <optgroup label="Gathering">
                    {#each gatheringJobs as job (job.id)}
                      <option value={job.id}>{job.label}{occupiedBy(job.id, selected.slot) ? ' (taken)' : ''}</option>
                    {/each}
                  </optgroup>
                  <optgroup label="Combat">
                    {#each combatJobs as job (job.id)}
                      <option value={job.id}>{job.label}{occupiedBy(job.id, selected.slot) ? ' (taken)' : ''}</option>
                    {/each}
                  </optgroup>
                  <optgroup label="Crafting &amp; cooking">
                    {#each craftingJobs as job (job.id)}
                      <option value={job.id}>{job.label}{occupiedBy(job.id, selected.slot) ? ' (taken)' : ''}</option>
                    {/each}
                  </optgroup>
                </select>
                <button class="tiny-btn" disabled={!pick} onclick={() => assign(selected.slot, selected.id)}>Assign</button>
                {#if selected.activity !== 0}
                  <button class="tiny-btn" onclick={() => stopWork(selected.slot, selected.id)}>Stop</button>
                {/if}
              </div>
              <DisabledReason text={taken ? `${taken} already works there - two of your people cannot share a job.` : null} />
            </div>
          {/if}

          {#each idleSlots as character (character.slot)}
            <div class="rostercard" class:locked={!character.unlocked}>
              <span class="dim small">
                Slot {character.slot}:
                {#if !character.unlocked}
                  locked - opens at Town Hall {SLOT_UNLOCK_TOWN_HALL[character.slot - 1]} (you are at {townHall})
                {:else}
                  empty - breed a character to fill it
                {/if}
              </span>
            </div>
          {/each}

          <h3>Village</h3>
          <dl class="stats">
            <div>
              <dt>Household</dt>
              <!-- A count, not a fraction: the housing "cap" beside it was
                   enforced by nothing (see Village.svelte). -->
              <dd data-testid="population">{formatNumber(Number(snap.CurrentPopulationCount))}</dd>
            </div>
            <div><dt>Town Hall</dt><dd>{snap.TownHallLevel}</dd></div>
            <div><dt>Forge</dt><dd>{snap.ForgeLevel}</dd></div>
            <div><dt>Workshop</dt><dd>{snap.CraftingWorkshopLevel}</dd></div>
          </dl>
        </section>

        <!-- Task 85: standing orders, beside the jobs they change - for the
             person the switcher is on. -->
        <AutomationRulesPanel characterId={selected?.id} />
      </div>
    {/if}
  </div>
{/if}

<style>
  .page {
    display: grid;
    gap: 0.75rem;
    padding: 1rem;
    max-width: 60rem;
    margin: 0 auto;
    align-content: start;
  }

  /* ------------------------------------------------------------ switcher */
  /* Modul: --sa-top, not 0: a sticky offset is measured from the scrollport
     edge, which on an edge-to-edge phone is under the status bar (the same
     reason ConnectionNotice uses it). One below that banner's 30, so a
     connection notice still wins when both are pinned. */
  .switcher {
    position: sticky;
    top: var(--sa-top, 0px);
    z-index: 29;
    display: grid;
    gap: 0.45rem;
    padding: 0.55rem 0.7rem;
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    box-shadow: 0 2px 8px rgba(0, 0, 0, 0.18);
  }

  .me {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    min-width: 0;
  }

  .portrait {
    flex-shrink: 0;
    display: inline-flex;
  }

  .ident {
    flex: 1 1 auto;
    min-width: 0;
    display: grid;
    gap: 0.1rem;
  }

  /* A 24-character name WRAPS rather than being cut or crushing "Lv": the
     line is allowed to break anywhere, and the level keeps its own width. */
  .nameline {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 0 0.5rem;
  }

  .name {
    font-size: 1rem;
    overflow-wrap: anywhere;
    min-width: 0;
  }

  .lv {
    flex-shrink: 0;
    font-size: 0.8rem;
    font-weight: 700;
    color: var(--accent);
    white-space: nowrap;
  }

  .sub {
    display: flex;
    flex-wrap: wrap;
    gap: 0 0.5rem;
    font-size: 0.75rem;
    color: var(--text-dim);
  }

  .sub .act {
    color: var(--good);
    overflow-wrap: anywhere;
  }

  .sub .act.idle {
    color: var(--text-dim);
  }

  .face {
    position: relative;
    flex-shrink: 0;
    padding: 0;
    border: 0;
    background: transparent;
    min-width: 44px;
    min-height: 44px;
  }

  .chest-badge {
    position: absolute;
    top: -2px;
    right: -4px;
    min-width: 1.1rem;
    padding: 0 0.25rem;
    border-radius: 999px;
    background: var(--accent);
    color: var(--on-accent);
    font-size: 0.7rem;
    line-height: 1.1rem;
  }

  .people {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(0, 1fr));
    gap: 0.35rem;
  }

  .person {
    display: flex;
    align-items: center;
    gap: 0.35rem;
    min-height: 44px;
    min-width: 0;
    padding: 0.25rem 0.45rem;
    border-radius: var(--radius-sm);
    border: 1px solid var(--border);
    background: var(--bg);
    color: inherit;
    font: inherit;
    font-size: 0.8rem;
    cursor: pointer;
  }

  .person.on {
    border-color: var(--accent);
    color: var(--accent);
    font-weight: 700;
  }

  /* Ellipsis is the cut that says so (clipping-check allows it); the full
     name is on the card above once the person is chosen. */
  .pname {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* ---------------------------------------------------------------- tabs */
  .tabs {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: 0;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    overflow: hidden;
    background: var(--bg-panel);
  }

  .tabs button {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 0.35rem;
    min-height: 44px;
    min-width: 0;
    padding: 0.3rem 0.4rem;
    border: 0;
    border-left: 1px solid var(--border);
    border-radius: 0;
    background: transparent;
    color: inherit;
    font: inherit;
    font-size: 0.85rem;
    cursor: pointer;
  }

  .tabs button:first-child {
    border-left: 0;
  }

  .tabs button.on {
    background: var(--accent);
    color: var(--on-accent);
    font-weight: 700;
  }

  .badge {
    min-width: 1.2rem;
    padding: 0 0.3rem;
    border-radius: 999px;
    background: var(--accent);
    color: var(--on-accent);
    font-size: 0.7rem;
    line-height: 1.2rem;
    font-weight: 700;
  }

  .tabs button.on .badge {
    background: var(--on-accent);
    color: var(--accent);
  }

  /* ---------------------------------------------------------------- gear */
  .hpblock {
    display: grid;
    gap: 0.2rem;
    font-size: 0.78rem;
    margin-bottom: 0.5rem;
  }

  .slots {
    display: grid;
    grid-template-columns: repeat(4, minmax(0, 1fr));
    gap: 0.4rem;
    margin-top: 0.7rem;
  }

  .gearslot {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: flex-start;
    gap: 0.15rem;
    min-width: 0;
    min-height: 4.6rem;
    padding: 0.3rem 0.2rem;
    border-radius: var(--radius-sm);
    border: 1px dashed var(--border);
    background: var(--bg);
    color: inherit;
    font: inherit;
    cursor: pointer;
  }

  .gearslot.filled {
    border-style: solid;
    border-color: var(--slot-rarity, var(--border));
  }

  .gearslot.open {
    outline: 2px solid var(--accent);
    outline-offset: 1px;
  }

  .slotname {
    font-size: 0.68rem;
    color: var(--text-dim);
    line-height: 1.1;
  }

  .empty {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 2rem;
    height: 2rem;
    font-size: 1.1rem;
    color: var(--text-dim);
  }

  .gearname {
    max-width: 100%;
    font-size: 0.66rem;
    line-height: 1.15;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .picker {
    margin-top: 0.7rem;
    padding: 0.6rem;
    border-radius: var(--radius-sm);
    border: 1px solid var(--border);
    background: var(--bg);
  }

  .picker header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: 0.4rem;
  }

  .picker header button,
  .wornrow button,
  .choices li button {
    flex-shrink: 0;
  }

  .wornrow {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
    font-size: 0.85rem;
    margin-bottom: 0.3rem;
  }

  .wornname {
    min-width: 0;
    overflow-wrap: anywhere;
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

  .cname {
    flex: 1 1 auto;
    min-width: 0;
    display: flex;
    flex-wrap: wrap;
    gap: 0 0.35rem;
    align-items: baseline;
    overflow-wrap: anywhere;
  }

  .choices li button {
    margin-left: auto;
  }

  /* The requirement on a gear row - dim when met, loud when not, because the
     only time it needs attention is when it is the reason Wear will refuse. */
  .req {
    font-size: 0.7rem;
    opacity: 0.6;
    white-space: nowrap;
  }
  .req.unmet {
    opacity: 1;
    color: var(--danger);
    font-weight: 600;
  }

  .sets {
    list-style: none;
    margin: 0.7rem 0 0;
    padding: 0;
    display: grid;
    gap: 0.25rem;
    font-size: 0.82rem;
  }
  .set-name {
    font-weight: 600;
    text-transform: capitalize;
  }
  .sets .good {
    color: var(--good);
  }
  .sets .accent {
    color: var(--accent);
  }
  .sets-none {
    margin: 0.7rem 0 0;
  }
  .chests {
    margin-top: 0.6rem;
  }

  /* ------------------------------------------------------------ attributes */
  .shared {
    margin: 0 0 -0.4rem;
  }

  /* ---------------------------------------------------------------- work */
  /* Modul: THE WORK TAB MADE THE PAGE "ZOOM OUT" on a phone (owner,
     2026-10-02). Gear and Attributes hold the page at the device width; this
     tab alone has selects whose option text is wider than a phone, two panels
     side by side, and a track floor of 19rem. Chrome on Android sizes the page
     to the widest thing in the document even when html/body clip overflow
     (app.css), so a few pixels of overhang in ONE tab rescaled the whole
     screen and only that tab.
     Three guards, because each alone leaves a way back in: the track floor can
     never exceed the container (min(..., 100%)), the grid items may shrink
     below their content (AutomationRulesPanel is another component, so its
     section carries no scoped class from here - :global), and the grid itself
     clips what still escapes inside its own box instead of handing it to the
     document. */
  .workgrid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(min(19rem, 100%), 1fr));
    gap: 0.75rem;
    align-items: start;
    min-width: 0;
    max-width: 100%;
    overflow-x: clip;
  }

  .workgrid > :global(*) {
    min-width: 0;
    max-width: 100%;
  }

  .rostercard {
    padding: 0.5rem 0;
    border-bottom: 1px solid var(--border);
  }

  .rostercard.locked {
    opacity: 0.75;
  }

  .nowjob {
    color: var(--good);
  }
  .nowjob.idle {
    color: var(--text-dim);
  }

  .assign {
    display: flex;
    gap: 0.4rem;
    align-items: center;
    margin: 0.35rem 0 0.2rem;
  }

  .assign select {
    flex: 1;
    min-width: 0;
  }

  .assign button {
    flex-shrink: 0;
  }

  /* ------------------------------------------------------------ shared */
  .panel {
    padding: 0.9rem;
    min-width: 0;
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
    font-size: 0.82rem;
    margin: 0;
  }
  .tiny {
    font-size: 0.72rem;
  }
  .pad {
    padding: 1rem;
  }

  .stats {
    display: grid;
    grid-template-columns: repeat(4, minmax(0, 1fr));
    gap: 0.4rem;
    margin: 0;
  }

  .stats div {
    display: grid;
    gap: 0.1rem;
    min-width: 0;
  }

  dt {
    font-size: 0.72rem;
    color: var(--text-dim);
  }

  dd {
    margin: 0;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
    overflow-wrap: anywhere;
  }

  /* Wider screens: bigger tiles, the same four columns. */
  @media (min-width: 52rem) {
    .gearslot {
      min-height: 5.6rem;
    }
    .gearname {
      font-size: 0.74rem;
    }
  }
</style>
