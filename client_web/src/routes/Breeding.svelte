<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import {
    queryKeys,
    fetchBreedingRoster,
    fetchBreedingPreview,
    fetchVillagerBreedingPreview,
    fetchVillageNewcomers,
    fetchTraits,
  } from '../lib/net/rest';
  import {
    executeBreeding,
    executeVillagerBreeding,
    APTITUDES,
    selectableAptitudeCount,
    clampSelectionMask,
    upMutationPercent,
    bestVillagerAptitudeFor,
    SELECTION_UNLOCK_LEVELS,
    APTITUDE_VILLAGE_CEILING,
  } from '../lib/net/commands';
  import { connection } from '../lib/net/connection';
  import AptitudePanel from '../lib/ui/AptitudePanel.svelte';
  import ChildPreview from '../lib/ui/ChildPreview.svelte';
  import PersonPicker from '../lib/ui/PersonPicker.svelte';
  import {
    heroPerson,
    partnerCharacterPerson,
    villagerPerson,
    sortForPicker,
    villagerBlockedReason,
    characterPartnerBlockedReason,
    toggleSelection,
    selectionMaskOf,
  } from '../lib/ui/breedingPicker';

  const client = useQueryClient();
  const roster = createQuery(() => ({ queryKey: queryKeys.breedingRoster, queryFn: fetchBreedingRoster }));
  const village = createQuery(() => ({
    queryKey: queryKeys.villageNewcomers,
    queryFn: fetchVillageNewcomers,
  }));
  const traitCatalogue = createQuery(() => ({ queryKey: queryKeys.traits, queryFn: fetchTraits, staleTime: Infinity }));
  const catalogue = $derived(traitCatalogue.data ?? []);

  const snap = $derived($playerState);
  const breedingLevel = $derived(snap?.BreedingLevel ?? 0);

  // Modul: ONE QUESTION, NOT TWO TABS.
  //
  // This screen used to open on a tab choice - "Marry the village" against
  // "Cross your own" - which asks a player to understand the difference before
  // they are allowed to begin. It is one hero and one partner, with the partner
  // list grouped. The two engine methods still exist; which one gets called
  // falls out of what was picked.
  //
  // Modul: AND NO <select>. Both pickers were native selects whose options
  // counted down every second, which on Android's WebView closes the open
  // dialog or drops the choice - see PersonPicker.svelte and breedingPicker.ts.
  //
  // The Chronicle pass used to sit at the bottom of this screen. It has nothing
  // to do with breeding and moved to Progress.

  // --- who is breeding ------------------------------------------------------

  let heroId = $state('');

  /** '' when nobody is chosen; 'v:<id>' for a villager, 'c:<guid>' for one of your own. */
  let partnerKey = $state('');

  const candidates = $derived(roster.data ?? []);
  const newcomers = $derived(village.data?.Newcomers ?? []);
  const innLevel = $derived(village.data?.InnLevel ?? 0);

  const hero = $derived(candidates.find((c) => c.CharacterId === heroId));

  const partnerIsVillager = $derived(partnerKey.startsWith('v:'));
  const partnerVillagerId = $derived(partnerIsVillager ? Number(partnerKey.slice(2)) : 0);
  const partnerCharacterId = $derived(partnerKey.startsWith('c:') ? partnerKey.slice(2) : '');
  const partnerCharacter = $derived(candidates.find((c) => c.CharacterId === partnerCharacterId));

  // A rest is shown in whole minutes, so the clock does not need to be finer
  // than this - and nothing the player is touching re-renders every second.
  let nowSeconds = $state(Math.floor(connection.serverNowMs() / 1000));
  $effect(() => {
    const timer = setInterval(() => {
      nowSeconds = Math.floor(connection.serverNowMs() / 1000);
    }, 15_000);
    return () => clearInterval(timer);
  });

  const heroGroups = $derived([
    {
      title: 'Your line',
      hint: 'Any grown adult who is not resting. There is no level requirement.',
      people: sortForPicker(candidates.map((c) => heroPerson(c, nowSeconds, catalogue))),
    },
  ]);

  // THE VILLAGE FIRST, and that order is not cosmetic: a child copies each
  // aptitude whole from one parent, so crossing your own line converges on what
  // you already have. Outside blood is the only thing that puts a new number in.
  const partnerGroups = $derived([
    {
      title: 'From the village - new blood',
      hint: 'Only outside blood brings a number your line does not already have.',
      people: sortForPicker(newcomers.map((p) => villagerPerson(hero, p, catalogue))),
    },
    {
      title: 'Your own line - refines what you have',
      people: sortForPicker(candidates.map((c) => partnerCharacterPerson(hero, c, nowSeconds, catalogue))),
    },
  ]);

  /**
   * Choosing a hero also re-checks the partner. The old screen kept a partner
   * who could no longer pair with the new hero - two men, say - so the Breed
   * button went grey and nothing said why.
   */
  function selectHero(key: string) {
    heroId = key.slice(2);
    const nextHero = candidates.find((c) => c.CharacterId === heroId);
    if (!partnerKey || !nextHero) return;

    let stillValid = false;
    if (partnerKey.startsWith('v:')) {
      const person = newcomers.find((p) => 'v:' + p.Id === partnerKey);
      stillValid = person !== undefined && villagerBlockedReason(nextHero, person) === null;
    } else {
      const other = candidates.find((c) => 'c:' + c.CharacterId === partnerKey);
      stillValid = other !== undefined && characterPartnerBlockedReason(nextHero, other, nowSeconds) === null;
    }
    if (!stillValid) partnerKey = '';
  }

  // --- what you are breeding FOR --------------------------------------------

  /**
   * Modul: THE BREEDING GROUNDS FINALLY DOES SOMETHING.
   *
   * Its level was read in four places on the server and every one of them
   * tested `<= 0`, so every upgrade past the first changed no number anywhere
   * in the game. It buys SELECTION now: a chosen aptitude takes the better
   * parent's value outright instead of the weighted coin.
   */
  const selectableCount = $derived(selectableAptitudeCount(breedingLevel));

  /** The order the player ticked them in, so the OLDEST is the one dropped. */
  let selectionOrder = $state<number[]>([]);

  /** Trimmed the way the server trims it, so the screen cannot promise more than it sends. */
  const effectiveMask = $derived(
    clampSelectionMask(selectionMaskOf(selectionOrder.slice(-Math.max(0, selectableCount))), breedingLevel),
  );

  function toggleAptitude(index: number) {
    selectionOrder = toggleSelection(selectionOrder, index, selectableCount);
  }

  const nextSelectionLevel = $derived(
    SELECTION_UNLOCK_LEVELS.find((level) => level > breedingLevel) ?? null,
  );

  // --- the preview -----------------------------------------------------------

  const villagePreview = createQuery(() => ({
    queryKey: queryKeys.villagerBreedingPreview(heroId, partnerVillagerId),
    queryFn: () => fetchVillagerBreedingPreview(heroId, partnerVillagerId),
    enabled: heroId !== '' && partnerVillagerId > 0,
  }));

  /**
   * Modul: the roster pairing is ORDERED - the paternal side has to be the man -
   * and the player is not asked which is which. Whichever of the two is male
   * goes in first, exactly what the engine already does for a village pairing.
   */
  const paternalId = $derived(
    hero && partnerCharacter ? (hero.IsFemale ? partnerCharacter.CharacterId : hero.CharacterId) : '',
  );
  const maternalId = $derived(
    hero && partnerCharacter ? (hero.IsFemale ? hero.CharacterId : partnerCharacter.CharacterId) : '',
  );

  const rosterPreview = createQuery(() => ({
    queryKey: queryKeys.breedingPreview(paternalId, maternalId),
    queryFn: () => fetchBreedingPreview(paternalId, maternalId),
    enabled: paternalId !== '' && maternalId !== '' && paternalId !== maternalId,
  }));

  const preview = $derived(
    partnerKey === '' ? undefined : partnerIsVillager ? villagePreview.data : rosterPreview.data,
  );

  const generation = $derived(
    !hero
      ? null
      : partnerIsVillager
        ? hero.GenerationIndex
        : partnerCharacter
          ? Math.max(hero.GenerationIndex, partnerCharacter.GenerationIndex)
          : null,
  );

  /**
   * Modul: the preview answers in SERVER CODES - "parent_on_cooldown" was
   * rendered to the player verbatim once. An unknown code falls through to the
   * raw string rather than a shrug, so a new one is visible rather than swallowed.
   */
  function refusal(code: string): string {
    switch (code) {
      case 'parent_not_adult':
        return 'A parent has to be a grown adult. A child matures an hour after you field it.';
      case 'parent_locked_in_escrow':
        return 'That character is locked in a trade.';
      case 'parent_on_cooldown':
        return 'That character is still resting after the last child.';
      case 'villager_already_married':
        return 'They have already married into your line. Everyone marries once.';
      case 'same_sex':
        return 'A pair needs one of each.';
      case 'sex_roles_swapped':
        return 'Swap them over - the paternal side has to be the man.';
      case 'race_mismatch':
        return 'The two are of different races.';
      default:
        return code;
    }
  }

  function breed() {
    const outcome = partnerIsVillager
      ? executeVillagerBreeding(heroId, partnerVillagerId, breedingLevel, effectiveMask)
      : executeBreeding(paternalId, maternalId, breedingLevel, effectiveMask);

    if (!outcome.ok) return pushLocalNotice(outcome.reason);

    // A villager marries once, so the choice is spent the moment this lands;
    // a partner from your own line is now resting. Either way the old choice
    // cannot be pressed again, so it is cleared rather than left greyed.
    partnerKey = '';

    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.breedingRoster });
      client.invalidateQueries({ queryKey: queryKeys.villageNewcomers });
      client.invalidateQueries({ queryKey: queryKeys.ancestorsHall });
    }, 900);
  }

  const canBreed = $derived(
    breedingLevel > 0 &&
      heroId !== '' &&
      partnerKey !== '' &&
      (preview ? preview.IsEligible && preview.HasSufficientGold : false),
  );
</script>

<div class="grid">
  <!-- Modul: what the bloodline is worth, first. It is the reason
       breeding exists, and it was invisible until now. -->
  <AptitudePanel />

  <section class="panel">
    <h2>Breeding lab</h2>

    <!-- Modul: WHERE THIS SITS. Breeding interlocks with four other systems and
         none of them were named here. -->
    <p class="interlocks dim tiny">
      The <strong>Inn</strong> stocks your village with newcomers to marry, and
      sets how good they are. The <strong>Breeding Grounds</strong> lets you
      choose which aptitude to breed for. A child joins the
      <strong>Hall of Ancestors</strong>, where you field it and mark whether it
      carries. When the season turns, levels, gear, gold and the whole village
      are taken back &mdash; the Hall and the <strong>aptitudes</strong> bred
      into it are what survive.
    </p>

    {#if breedingLevel === 0}
      <p class="warn">
        You have no <strong>Breeding Grounds</strong>. Build it in your village
        and any grown adult can marry &mdash; there is no level requirement.
      </p>
    {/if}

    <PersonPicker
      testId="hero-picker"
      label="Your hero"
      placeholder="Choose a hero..."
      groups={heroGroups}
      selectedKey={heroId ? 'c:' + heroId : ''}
      onSelect={selectHero}
      emptyText="You have no characters with a bloodline yet."
    />

    <PersonPicker
      testId="partner-picker"
      label="Partner"
      placeholder={heroId ? 'Choose a partner...' : 'Choose your hero first'}
      groups={partnerGroups}
      selectedKey={partnerKey}
      onSelect={(key) => (partnerKey = key)}
      disabled={heroId === ''}
      emptyText="Nobody to choose from here."
    />

    <!-- Modul: WHICH LIST TO PICK FROM, stated rather than implied. -->
    <p class="dim tiny">
      Marrying the <strong>village</strong> is what raises a bloodline &mdash;
      only outside blood brings a number you do not already have. Your Inn is
      level {innLevel}, so a newcomer can roll up to
      <strong>{bestVillagerAptitudeFor(innLevel)}</strong> in an aptitude
      (the village can never exceed {APTITUDE_VILLAGE_CEILING}; past that it is
      selection and luck alone). Crossing <strong>your own</strong> refines what
      you have and never exceeds it by more than a lucky point.
    </p>

    {#if newcomers.length === 0 && village.data}
      <p class="dim tiny">
        Nobody has settled in your village yet. Somebody turns up every
        {Math.round(village.data.IntervalSeconds / 3600)}h while there is room.
      </p>
    {/if}

    <!-- Modul: BREED FOR SOMETHING - what the Breeding Grounds buys. -->
    <fieldset class="selection">
      <legend>Breed for</legend>

      {#if selectableCount === 0}
        <p class="dim tiny">
          Your <strong>Breeding Grounds</strong> is level {breedingLevel}. At
          level {SELECTION_UNLOCK_LEVELS[0]} you can choose one aptitude to breed
          for, and a chosen one always keeps the better parent's value instead of
          leaving it to chance.
        </p>
      {:else}
        <p class="dim tiny">
          Choose up to <strong>{selectableCount}</strong>. A chosen aptitude
          takes the <strong>better parent's value outright</strong>; the rest are
          a weighted roll, so a 4 against a 6 keeps the 6 only about 60% of the
          time. Your Grounds also gives every aptitude a
          <strong>{upMutationPercent(breedingLevel)}%</strong> chance of +1.
        </p>

        <div class="apt-choices">
          {#each APTITUDES as aptitude, index (aptitude.field)}
            {@const on = (effectiveMask & (1 << index)) !== 0}
            <label class="apt-choice" class:on>
              <input type="checkbox" checked={on} onchange={() => toggleAptitude(index)} />
              <span>
                <strong>{aptitude.name}</strong>
                <span class="dim tiny">{aptitude.blurb}</span>
              </span>
            </label>
          {/each}
        </div>

        {#if nextSelectionLevel !== null}
          <p class="dim tiny">
            Breeding Grounds level {nextSelectionLevel} buys another choice.
          </p>
        {/if}
      {/if}
    </fieldset>

    {#if preview}
      {@const p = preview}
      {#if !p.IsEligible}
        <p class="warn">{p.IneligibleReason ? refusal(p.IneligibleReason) : 'These two cannot pair.'}</p>
      {:else}
        <p class="cost" class:short={!p.HasSufficientGold}>
          Costs {p.BreedingCostGold.toLocaleString()}g
          {#if !p.HasSufficientGold}&middot; not enough gold{/if}
          {#if p.IsInbredRisk}&middot; <span class="risk">related pair</span>{/if}
        </p>
      {/if}

      <ChildPreview preview={p} mode={partnerIsVillager ? 'village' : 'roster'} {generation} {catalogue} />
    {/if}

    <button class="breed" onclick={breed} disabled={!canBreed}>Breed</button>

    <!-- Modul: WHAT HAPPENS NEXT, which the screen never said. -->
    <p class="dim tiny">
      The child is born into the <strong>Hall of Ancestors</strong>. Field it
      into one of your slots and it grows from a child into an adult after an
      hour. A villager who marries in is <strong>spent for ever</strong> &mdash;
      everybody marries once &mdash; so spend a good one deliberately.
    </p>
  </section>
</div>

<style>
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(21rem, 1fr));
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

  .dim {
    color: var(--text-dim);
  }
  .tiny {
    font-size: 0.72rem;
    margin: 0.35rem 0 0;
  }

  .warn {
    padding: 0.5rem 0.65rem;
    background: rgba(224, 85, 63, 0.12);
    border-left: 3px solid var(--danger);
    border-radius: 4px;
    font-size: 0.82rem;
    margin: 0 0 0.7rem;
  }

  .cost {
    font-size: 0.85rem;
    margin: 0 0 0.5rem;
  }

  .cost.short {
    color: var(--danger);
  }

  .risk {
    color: var(--danger);
  }

  .breed {
    width: 100%;
    min-height: 44px;
    margin-top: 0.6rem;
  }

  .selection {
    margin: 0.6rem 0;
    padding: 0.5rem 0.6rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
  }

  .selection legend {
    padding: 0 0.35rem;
    font-size: 0.72rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  .apt-choices {
    display: grid;
    gap: 0.3rem;
    margin-top: 0.4rem;
  }

  /* Modul: the whole row is the target, not the box. Padding cannot enlarge a
     checkbox - the browser hit-tests its border box - so the LABEL carries the
     44px floor and the input rides inside it. */
  .apt-choice {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    min-height: 44px;
    padding: 0.3rem 0.45rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    cursor: pointer;
    font-size: 0.8rem;
  }

  .apt-choice.on {
    border-color: var(--brass, var(--border));
  }

  .apt-choice input {
    flex-shrink: 0;
    width: 20px;
    height: 20px;
  }

  .apt-choice span {
    display: grid;
    gap: 0.1rem;
    min-width: 0;
  }

  .apt-choice .tiny {
    margin: 0;
    display: block;
  }

  .interlocks {
    margin: 0 0 0.7rem;
    padding: 0.5rem 0.6rem;
    background: var(--bg);
    border-radius: var(--radius);
    line-height: 1.4;
  }
</style>
