<script lang="ts">
  import Money from '../lib/ui/Money.svelte';
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { fly } from 'svelte/transition';
  import { motionMs, MOTION } from '../lib/ui/motion';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import { requestScreen } from '../lib/stores/navigation';
  import {
    queryKeys,
    fetchBreedingRoster,
    fetchBreedingPreview,
    fetchVillagerBreedingPreview,
    fetchVillageNewcomers,
    fetchTraits,
    fetchAncestorsHall,
    fetchRebirthPreview,
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
  import BloodlinePath from '../lib/ui/BloodlinePath.svelte';
  import ChildForecast from '../lib/ui/ChildForecast.svelte';
  import ChildPreview from '../lib/ui/ChildPreview.svelte';
  import PersonPicker from '../lib/ui/PersonPicker.svelte';
  import QueryError from '../lib/ui/QueryError.svelte';
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

  // The loop around this screen (BloodlinePath): who is growing and who
  // carries come from the Hall, the bloodline ceiling from the rebirth preview.
  const hall = createQuery(() => ({ queryKey: queryKeys.ancestorsHall, queryFn: fetchAncestorsHall }));
  const rebirth = createQuery(() => ({ queryKey: queryKeys.rebirthPreview, queryFn: fetchRebirthPreview }));

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
  // Modul: THE ANSWER BEFORE THE RULES (2026-10-14). The owner found the
  // screen hard to follow: the preview opened with a paragraph of odds per
  // aptitude and the button sat about two screens down on a phone. The order
  // is now the decision's own order - the pair, what you breed for, the child
  // drawn on one track per aptitude with a one-line verdict (ChildForecast),
  // then the price and the button - and the mechanics are behind a toggle.

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

  const heroName = $derived(heroGroups[0].people.find((p) => p.key === 'c:' + heroId)?.name ?? 'your hero');
  const partnerName = $derived(
    partnerGroups.flatMap((g) => g.people).find((p) => p.key === partnerKey)?.name ?? 'the partner',
  );

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

  // Modul: WHAT HAPPENED, AND WHAT NEXT. A birth used to clear the partner and
  // say nothing on this screen, so the one step everything else hangs on -
  // field the child - was small print under the button. The Hall is read
  // before the Breed press and again after; whoever is new is the child. That
  // keeps the banner true when the server refuses (no new member, no banner)
  // and names the actual child rather than a guess.
  let hallBefore = $state<Set<string> | null>(null);
  const bornChild = $derived(
    hallBefore === null ? undefined : (hall.data?.Members ?? []).find((m) => !hallBefore!.has(m.CharacterId)),
  );

  function breed() {
    hallBefore = new Set((hall.data?.Members ?? []).map((m) => m.CharacterId));
    const outcome = partnerIsVillager
      ? executeVillagerBreeding(heroId, partnerVillagerId, breedingLevel, effectiveMask)
      : executeBreeding(paternalId, maternalId, breedingLevel, effectiveMask);

    if (!outcome.ok) {
      hallBefore = null;
      return pushLocalNotice(outcome.reason, 'error');
    }

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

<div class="breeding-screen">
  <BloodlinePath
    hall={hall.data}
    newcomers={newcomers.length}
    ceilingNow={rebirth.data?.BloodlineCeilingNow ?? null}
    ceilingAfter={rebirth.data?.BloodlineCeilingAfter ?? null}
    capped={rebirth.data?.Capped ?? false}
  />

  <div class="grid">
    <section class="panel lab">
      <h2>Breeding lab</h2>

      <!-- Modul: WHERE THIS SITS. Task 109 cut five blocks of rules prose
           around the lab to one line per control; the full rules are the
           Wiki's Bloodline page, and BloodlinePath above carries the loop. -->
      <p class="interlocks dim tiny">
        What you breed into the <strong>Hall of Ancestors</strong> is what
        survives a rebirth; levels, gear, gold and the village folk do not.
      </p>

      {#if breedingLevel === 0}
        <p class="warn">
          You have no <strong>Breeding Grounds</strong>. Build it in your village
          and any grown adult can marry &mdash; there is no level requirement.
          <button type="button" class="inline-go" onclick={() => requestScreen('village')}>Village</button>
        </p>
      {/if}

      <!-- Modul: the pickers below say "You have no characters" and "Nobody to
           choose from" when their lists are empty - which a failed request also
           produces. Name the failure above them instead. -->
      {#if roster.isError && roster.data === undefined}
        <QueryError query={roster} what="your characters" />
      {/if}
      {#if village.isError && village.data === undefined}
        <QueryError query={village} what="your village" />
      {/if}

      <!-- The pair, side by side where there is room: the decision is one
           hero against one partner, and it reads as a pair. -->
      <div class="pair">
        <PersonPicker
          testId="hero-picker"
          label="Your hero"
          placeholder="Choose a hero..."
          groups={heroGroups}
          selectedKey={heroId ? 'c:' + heroId : ''}
          onSelect={selectHero}
          emptyText="You have no characters with a bloodline yet."
        />
        <span class="times" aria-hidden="true">&times;</span>
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
      </div>

      <!-- Modul: WHICH LIST TO PICK FROM, stated rather than implied. The Inn
           figure is bestVillagerAptitudeFor, pinned to the server by
           serverMirrors.test.ts. -->
      <p class="dim tiny">
        A <strong>village</strong> newcomer raises a bloodline (Inn level {innLevel}:
        up to <strong>{bestVillagerAptitudeFor(innLevel)}</strong>, never past
        {APTITUDE_VILLAGE_CEILING}); <strong>your own</strong> line refines it.
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
            Breeding Grounds level {SELECTION_UNLOCK_LEVELS[0]} lets you choose an
            aptitude that always keeps the better parent's value.
          </p>
        {:else}
          <!-- One choice is a radio choice; toggleSelection already replaces
               the old pick at capacity 1, and a second tap clears it. -->
          <p class="dim tiny">
            {selectableCount === 1 ? 'Choose one' : `Choose up to ${selectableCount}`}: it keeps the
            better parent's value; the rest roll. Every aptitude has a
            {upMutationPercent(breedingLevel)}% chance of +1.
          </p>

          <div class="apt-choices">
            {#each APTITUDES as aptitude, index (aptitude.field)}
              {@const on = (effectiveMask & (1 << index)) !== 0}
              <label class="apt-choice" class:on>
                <input
                  type={selectableCount === 1 ? 'radio' : 'checkbox'}
                  name="breed-for"
                  checked={on}
                  onclick={() => toggleAptitude(index)}
                />
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
        <ChildForecast preview={p} selectionMask={effectiveMask} {heroName} {partnerName} />

        <!-- The price, the cost that matters most, and the button: together
             and straight under the forecast, so on a phone the action follows
             the answer instead of the rules. -->
        <div class="act">
          {#if !p.IsEligible}
            <p class="warn">{p.IneligibleReason ? refusal(p.IneligibleReason) : 'These two cannot pair.'}</p>
          {:else}
            <p class="cost" class:short={!p.HasSufficientGold}>
              Costs <Money amount={p.BreedingCostGold} />
              {#if !p.HasSufficientGold}&middot; not enough gold{/if}
              {#if p.IsInbredRisk}&middot; <span class="risk">related pair</span>{/if}
            </p>
          {/if}
          {#if partnerIsVillager}
            <p class="spent-note" data-testid="partner-spent-note">
              This villager is <strong>spent for ever</strong> once married.
            </p>
          {/if}
          <button class="breed" data-guide="breeding-start" onclick={breed} disabled={!canBreed}>Breed</button>
        </div>

        <ChildPreview preview={p} mode={partnerIsVillager ? 'village' : 'roster'} {generation} {catalogue} />
      {:else}
        {#if partnerKey !== '' && (partnerIsVillager ? villagePreview : rosterPreview).isError}
          <QueryError query={partnerIsVillager ? villagePreview : rosterPreview} what="the child preview" />
        {:else if heroId === '' || partnerKey === ''}
          <p class="empty-forecast dim tiny" data-testid="forecast-empty">
            Choose a hero and a partner, and the child's four aptitudes are drawn here before you pay anything.
          </p>
        {/if}
        <button class="breed" data-guide="breeding-start" onclick={breed} disabled={!canBreed}>Breed</button>
      {/if}

      {#if bornChild}
        <div class="born" data-testid="born-banner" role="status" transition:fly={{ y: 12, duration: motionMs(MOTION.enter) }}>
          <div>
            <strong>{bornChild.Name || 'A child'} is born</strong>
            <span class="dim tiny">
              Strength {bornChild.AptitudeStrength} &middot; Skill {bornChild.AptitudeSkill} &middot;
              Endurance {bornChild.AptitudeEndurance} &middot; Fortune {bornChild.AptitudeFortune}{#if bornChild.IsEpicMutation}
                &middot; <span class="epic">epic</span>{/if}
            </span>
            <span class="dim tiny">Field them in the Hall of Ancestors - about an hour of play and they are grown.</span>
          </div>
          <button type="button" onclick={() => requestScreen('ancestors')}>Hall of Ancestors</button>
        </div>
      {/if}
    </section>

    <!-- What the bloodline is worth now. Beside the lab on a wide screen,
         under it on a phone - the lab is what you came here to do. -->
    <AptitudePanel />
  </div>
</div>

<style>
  .breeding-screen {
    display: grid;
    gap: 1rem;
    padding: 1rem;
  }

  .grid {
    display: grid;
    grid-template-columns: minmax(0, 3fr) minmax(0, 2fr);
    gap: 1rem;
    align-items: start;
  }

  @media (max-width: 56rem) {
    .grid {
      grid-template-columns: minmax(0, 1fr);
    }
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

  .inline-go {
    margin-left: 0.4rem;
    min-height: 44px;
  }

  .pair {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr);
    align-items: end;
    gap: 0.5rem;
  }

  .times {
    padding-bottom: 0.7rem;
    font-size: 1.2rem;
    color: var(--brass-lit, var(--text-dim));
  }

  @media (max-width: 40rem) {
    .pair {
      grid-template-columns: minmax(0, 1fr);
    }

    .times {
      display: none;
    }
  }

  .act {
    display: grid;
    gap: 0.4rem;
    margin: 0.8rem 0 0.4rem;
    padding: 0.6rem 0.7rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg-raised);
  }

  .act .warn {
    margin: 0;
  }

  .cost {
    font-size: 0.85rem;
    margin: 0;
  }

  .cost.short {
    color: var(--danger);
  }

  .risk {
    color: var(--danger);
  }

  .spent-note {
    margin: 0;
    font-size: 0.8rem;
    color: var(--warn, var(--danger));
  }

  .breed {
    width: 100%;
    min-height: 44px;
    margin-top: 0.2rem;
  }

  .empty-forecast {
    margin: 0.8rem 0 0.6rem;
    padding: 0.9rem;
    border: 1px dashed var(--border);
    border-radius: var(--radius);
    text-align: center;
  }

  .born {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    justify-content: space-between;
    gap: 0.6rem;
    margin-top: 0.8rem;
    padding: 0.7rem 0.8rem;
    border-radius: var(--radius);
    border: 1px solid var(--brass-lit, var(--accent));
    background: color-mix(in srgb, var(--brass-lit, #d8b45a) 12%, var(--bg-panel));
    box-shadow: 0 0 14px var(--glow-soft, transparent);
  }

  .born > div {
    display: grid;
    gap: 0.15rem;
    min-width: 0;
  }

  .born .tiny {
    margin: 0;
  }

  .born button {
    min-height: 44px;
    flex-shrink: 0;
  }

  .epic {
    color: var(--brass-lit, var(--accent));
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
    grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr));
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
    transition:
      border-color 160ms ease,
      background-color 160ms ease;
  }

  .apt-choice.on {
    border-color: var(--brass-lit, var(--border));
    background: color-mix(in srgb, var(--brass, #8a6a34) 16%, transparent);
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

  @media (prefers-reduced-motion: reduce) {
    .apt-choice {
      transition: none;
    }
  }
</style>
