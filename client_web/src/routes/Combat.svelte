<script lang="ts">
  import { locationName } from '../lib/ui/locations';
  import { assignCharacterActivity, EMPTY_GUID } from '../lib/net/commands';
  import { locationBackground } from '../lib/ui/sprites';
  import { onMount } from 'svelte';
  import { playerState, visualState, connectionStatus, observedMaxPlayerHp, damageEvents, pushLocalNotice } from '../lib/stores/game';
  import {
    loadContent,
    itemName,
    monsterName,
    prettifyBaseId,
    type ContentRegistry,
    type MonsterDefinition,
    type MonsterLootEntry,
  } from '../lib/net/content';
  import { authedGet } from '../lib/net/auth';
  import { HALT_REASONS } from '../lib/ui/slots';
  import Bar from '../lib/ui/Bar.svelte';
  import FloatingDamage from '../lib/ui/FloatingDamage.svelte';
  import MonsterPortrait from '../lib/ui/MonsterPortrait.svelte';
  import SessionLoot from '../lib/ui/SessionLoot.svelte';

  // Modul: region progression. The server refuses a target in a region whose
  // predecessor's boss is still standing (CommandResultCode.RegionLocked), so
  // the list has to say which those are. Offering a Fight button that is
  // guaranteed to be rejected is how a rule reads as a bug.
  //
  // Falls back to region 1 rather than to "everything unlocked" when no state
  // has arrived yet: showing a locked region as open and having the click fail
  // is worse than showing an open one as locked for the moment before the
  // first packet lands.
  const unlockedRegion = $derived($playerState?.HighestUnlockedRegion || 1);

  let registry = $state<ContentRegistry | null>(null);
  let contentError = $state('');
  let selectedMonsterId = $state(0);
  let dropPreview = $state<MonsterLootEntry[]>([]);
  let dropPreviewFor = $state(0);

  onMount(async () => {
    try {
      registry = await loadContent();
    } catch (err) {
      contentError = err instanceof Error ? err.message : String(err);
    }
  });

  // Modul: this screen used to carry its OWN copy of the halt-reason strings,
  // which had already drifted from lib/ui/slots.ts by two entries. One table,
  // imported - the header badge and this panel must never disagree about why a
  // character stopped.

  const snap = $derived($playerState);
  const visual = $derived($visualState);
  const activeMonster = $derived(
    snap && snap.CurrentMonsterId > 0 ? (registry?.monsters.get(snap.CurrentMonsterId) ?? null) : null,
  );
  const haltMessage = $derived(snap ? (HALT_REASONS[snap.ActivityHaltReason] ?? '') : '');

  // Modul: DEPLOYED IS NOT THE SAME AS FIGHTING, and conflating them made a
  // real fault look like a no-op button.
  //
  // ActiveActivityId is what the player asked for; CurrentMonsterId is what
  // the simulation is actually doing. They diverge whenever a tick cannot run
  // - a full backpack returns from ProcessSubTick before anything spawns - and
  // this screen only ever read the second one. So clicking Fight set the
  // activity server-side, no monster appeared, and the screen said "Not in
  // combat", which reads as "the button did nothing".
  //
  // Named as its own state so the player is told they ARE deployed and what is
  // blocking them, instead of being shown the idle screen.
  const deployedTo = $derived(
    snap && snap.ActiveActivityId > 0 ? (registry?.monsters.get(Number(snap.ActiveActivityId)) ?? null) : null,
  );
  const stalled = $derived(deployedTo !== null && activeMonster === null);

  // Modul: a brief shake on the monster portrait when a hit lands, and a flare
  // on the panel when a level is gained.
  //
  // Driven off the damage feed and the level number rather than off a timer,
  // so they fire exactly when the thing they describe happened. Both are keyed
  // to a counter that the CSS animation restarts from, which is how you replay
  // an animation in Svelte without removing and re-adding the node.
  let hitPulse = $state(0);
  let levelPulse = $state(0);
  let lastSeenLevel = 0;

  // Modul: keyed on the newest event ID, not on the array being non-empty.
  //
  // `damageEvents` is rewritten by the render loop every time it prunes an
  // expired number - roughly sixty times a second - so "the array has items"
  // fires continuously. An earlier version incremented on that, which
  // re-created the portrait node sixty times a second and starved the main
  // thread badly enough that the rest of the app stopped responding: the
  // health bar froze and every other screen failed to load. The symptom
  // looked nothing like an animation bug.
  //
  // The highest id only moves when a hit actually lands, which is the event
  // this is meant to reflect.
  let lastHitId = 0;

  $effect(() => {
    const events = $damageEvents;
    const newest = events.length > 0 ? events[events.length - 1].id : 0;
    if (newest > lastHitId) {
      lastHitId = newest;
      hitPulse++;
    }
  });

  $effect(() => {
    const level = snap?.CurrentLevel ?? 0;
    if (lastSeenLevel > 0 && level > lastSeenLevel) levelPulse++;
    lastSeenLevel = level;
  });

  async function selectMonster(monster: MonsterDefinition) {
    selectedMonsterId = monster.Id;
    if (dropPreviewFor !== monster.Id) {
      try {
        dropPreview = await authedGet<MonsterLootEntry[]>(
          `/api/v1/monsters/loot?monsterId=${monster.Id}`,
        );
        dropPreviewFor = monster.Id;
      } catch {
        dropPreview = [];
      }
    }
  }

  const activeCharacterId = $derived(snap?.Slot1_CharacterId ?? EMPTY_GUID);

  function fight(monster: MonsterDefinition) {
    selectMonster(monster);
    // See Gathering.svelte: a bare TargetId does not persist.
    const outcome = assignCharacterActivity(activeCharacterId, monster.Id);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }

  function stop() {
    const outcome = assignCharacterActivity(activeCharacterId, 0);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }

  // BaseItemId is the reliable identifier on a drop-preview row; ItemId is 0
  // for every equipment entry. Falls back to the registry only when the row
  // carries no BaseItemId at all.
  function dropEntryName(entry: MonsterLootEntry): string {
    return entry.BaseItemId ? prettifyBaseId(entry.BaseItemId) : itemName(registry, entry.ItemId);
  }
</script>

<div class="layout">
  <!-- The level-up flare goes on the whole panel rather than on the number,
       because the number is small and the moment is not. -->
  {#key levelPulse}
    <section class="panel" class:level-flare={levelPulse > 0}>
      <h2>Combat</h2>

    {#if contentError}
      <p class="error">Content failed to load: {contentError}</p>
    {/if}

    {#if $connectionStatus.phase !== 'live'}
      <p class="status">
        {$connectionStatus.phase}
        {#if $connectionStatus.detail}- {$connectionStatus.detail}{/if}
      </p>
    {/if}

    {#if snap}
      <div class="stats">
        <div>
          <span class="dim">Level</span>
          <strong>{snap.CurrentLevel}</strong>
        </div>
        <div>
          <span class="dim">XP</span>
          <strong>{Math.floor(visual?.CurrentXp ?? snap.CurrentXp).toLocaleString()}</strong>
        </div>
        <div>
          <span class="dim">Gold</span>
          <strong>{Math.floor(visual?.Gold ?? snap.Gold).toLocaleString()}</strong>
        </div>

      </div>

      <div class="hpblock">
        <span class="dim">Your health</span>
        <Bar
          value={visual?.PlayerHp ?? snap.PlayerHp}
          max={$observedMaxPlayerHp}
          color="var(--good)"
          label={`${Math.round(visual?.PlayerHp ?? snap.PlayerHp)} / ${$observedMaxPlayerHp}`}
        />
      </div>

      {#if activeMonster}
        <FloatingDamage />
        <div class="fighting">
          <!-- Keyed on the pulse counter so the animation restarts on every
               hit; without the key Svelte reuses the node and the animation
               only ever plays once. -->
          {#key hitPulse}
            <span class="hit-shake">
              <MonsterPortrait monsterId={activeMonster.Id} name={activeMonster.Name} size="lg" />
            </span>
          {/key}
          <div class="hpblock grow">
            <span class="dim">Fighting {activeMonster.Name}</span>
            <Bar
              value={visual?.CurrentMonsterHp ?? snap.CurrentMonsterHp}
              max={activeMonster.MaxHp}
              color="var(--danger)"
              label={`${Math.round(visual?.CurrentMonsterHp ?? snap.CurrentMonsterHp).toLocaleString()} / ${activeMonster.MaxHp.toLocaleString()}`}
            />
          </div>
        </div>
        <button onclick={stop}>Stop fighting</button>
      {:else if stalled}
        <!-- Deployed, but the simulation is not running. Saying "not in
             combat" here is what made the Fight button look broken. -->
        <p class="stalled">
          Deployed to {deployedTo?.Name ?? `activity ${snap.ActiveActivityId}`}, but nothing is
          happening. See below for why.
        </p>
        <button onclick={stop}>Stand down</button>
      {:else}
        <p class="dim">Not in combat.</p>
      {/if}

      {#if haltMessage}
        <p class="halt">{haltMessage}</p>
      {/if}
    {:else}
      <p class="dim">Waiting for the first state snapshot...</p>
    {/if}
    </section>
  {/key}

  <section class="panel">
    <h2>Monsters</h2>
    {#if registry}
      {#each registry.regions as region, index}
        <!-- Modul: each location gets its painted scene as a banner. The art
             existed and nothing referenced it; a list of five identical
             headings is a much weaker sense of place than the thing the
             painting is of. -->
        <h3
          class="place"
          style={locationBackground(index + 1)
            ? `background-image: linear-gradient(rgba(0,0,0,0.45), rgba(0,0,0,0.75)), url('${locationBackground(index + 1)}')`
            : ''}
        >
          {locationName(index + 1)}
          {#if index + 1 > unlockedRegion}
            <span class="locked-tag"
              >Locked — defeat the {locationName(index)} boss</span
            >
          {/if}
        </h3>
        <ul class="monsters" class:locked={index + 1 > unlockedRegion}>
          {#each region as monster}
            <li class:selected={selectedMonsterId === monster.Id}>
              <button class="row" onclick={() => selectMonster(monster)}>
                <MonsterPortrait monsterId={monster.Id} name={monster.Name} size="sm" />
                <span class="name">{monster.Name}</span>
                <span class="dim">{monster.MaxHp.toLocaleString()} HP</span>
                <span class="dim">{monster.BaseXpReward.toLocaleString()} XP</span>
              </button>
              <button
                class="fight"
                disabled={$connectionStatus.phase !== 'live' || index + 1 > unlockedRegion}
                onclick={() => fight(monster)}
              >
                Fight
              </button>
            </li>
          {/each}
        </ul>
      {/each}
    {:else if !contentError}
      <p class="dim">Loading content...</p>
    {/if}
  </section>

  <section class="panel">
    <h2>Drops</h2>
    {#if dropPreviewFor > 0}
      <h3>{monsterName(registry, dropPreviewFor)} drop table</h3>
      {#if dropPreview.length === 0}
        <p class="dim">No drop data.</p>
      {:else}
        <ul class="drops">
          {#each dropPreview as entry}
            <li>
              <!-- Equipment rows come back with ItemId = 0 and are identified
                   by BaseItemId alone, because equipment is generated per
                   slot rather than being a numbered ContentRegistry item.
                   Looking up 0 rendered four of five rows as "Item #0". -->
              <span>{dropEntryName(entry)}</span>
              <span class="dim">
                {entry.ChancePct.toFixed(2)}% &middot; {entry.MinQuantity}-{entry.MaxQuantity}
              </span>
            </li>
          {/each}
        </ul>
      {/if}
    {/if}

    <SessionLoot {registry} />

  </section>
</div>

<style>
  h3.place {
    background-size: cover;
    background-position: center;
    border-radius: var(--radius, 6px);
    padding: 0.7rem 0.9rem;
    margin: 1rem 0 0.5rem;
    text-shadow: 0 1px 3px rgba(0, 0, 0, 0.9);
    letter-spacing: 0.04em;
  }

  /* Modul: THE PAGE JITTERED THROUGHOUT EVERY FIGHT.
     `auto-fit` sizes the tracks from their content, and this screen's content
     is gold and XP counting up ten times a second. Every digit that changed
     width re-measured the whole grid and slid the monster list - and its Fight
     buttons - sideways. It reads as a wobble, and it is why an automated click
     on a Fight button could never land: the element genuinely never stopped
     moving.
     Fixed fractions, and tabular numerals so a digit is always the same
     width. */
  .layout {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(20rem, 1fr));
    grid-auto-columns: 1fr;
    gap: 1rem;
    padding: 1rem;
    align-items: start;
  }

  .layout strong {
    font-variant-numeric: tabular-nums;
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
  }

  h2 {
    margin: 0 0 0.75rem;
    font-size: 1.05rem;
  }

  h3 {
    margin: 1rem 0 0.35rem;
    font-size: 0.8rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  .dim {
    color: var(--text-dim);
  }

  .stats {
    display: grid;
    grid-template-columns: repeat(4, 1fr);
    gap: 0.5rem;
    margin-bottom: 0.85rem;
  }

  .stats div {
    display: grid;
    gap: 0.1rem;
    font-size: 0.8rem;
  }

  .hpblock {
    display: grid;
    gap: 0.25rem;
    margin-bottom: 0.75rem;
    font-size: 0.8rem;
  }

  .monsters,
  .drops {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  /* Modul: a locked region stays READABLE. Dimmed, not hidden - knowing what
     is behind the boss is the reason to go and fight it, and a region that
     simply is not drawn reads as content that does not exist yet. */
  .monsters.locked {
    opacity: 0.45;
  }

  .locked-tag {
    display: block;
    font-size: 0.75rem;
    font-weight: 400;
    letter-spacing: 0.02em;
    opacity: 0.9;
  }

  .monsters li {
    display: grid;
    grid-template-columns: 1fr auto;
    gap: 0.35rem;
  }

  .monsters li.selected .row {
    border-color: var(--accent);
  }

  .row {
    display: grid;
    grid-template-columns: auto 1fr auto auto;
    gap: 0.6rem;
    align-items: center;
    text-align: left;
    font-size: 0.85rem;
  }

  /* The portrait sits beside the health bar rather than above it, so the
     fight reads as one thing at a glance. */
  .fighting {
    display: flex;
    align-items: center;
    gap: 0.8rem;
  }

  .grow {
    flex: 1;
    min-width: 0;
  }

  .name {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .drops li {
    display: flex;
    justify-content: space-between;
    gap: 0.75rem;
    font-size: 0.85rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.25rem;
  }

  .halt {
    margin: 0.5rem 0 0;
    padding: 0.5rem 0.65rem;
    background: rgba(224, 85, 63, 0.12);
    border-left: 3px solid var(--danger);
    border-radius: 4px;
  }

  /* Warn, not danger: the player did the right thing and something is in the
     way, which is a different message from "this failed". */
  .stalled {
    margin: 0 0 0.6rem;
    color: var(--warn);
    font-size: 0.88rem;
  }

  .error {
    color: var(--danger);
  }

  .status {
    color: var(--text-dim);
    font-size: 0.85rem;
  }
</style>
