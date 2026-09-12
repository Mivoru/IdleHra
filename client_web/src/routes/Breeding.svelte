<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import {
    queryKeys,
    fetchBreedingRoster,
    fetchBreedingPreview,
    fetchVillagerBreedingPreview,
    fetchVillageNewcomers,
    fetchMetadata,
    type BreedingCandidate,
    type VillageNewcomer,
  } from '../lib/net/rest';
  import {
    executeBreeding,
    executeVillagerBreeding,
    claimBattlePassMilestone,
    purchaseBattlePass,
    APTITUDES,
    selectableAptitudeCount,
    clampSelectionMask,
    upMutationPercent,
    bestVillagerAptitudeFor,
    SELECTION_UNLOCK_LEVELS,
    APTITUDE_VILLAGE_CEILING,
  } from '../lib/net/commands';
  import { connection } from '../lib/net/connection';
  import { agePhaseName } from '../lib/ui/slots';
  import { raceName } from '../lib/ui/races';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import AptitudePanel from '../lib/ui/AptitudePanel.svelte';
  import ChildPreview from '../lib/ui/ChildPreview.svelte';

  const client = useQueryClient();
  const roster = createQuery(() => ({ queryKey: queryKeys.breedingRoster, queryFn: fetchBreedingRoster }));
  const metadata = createQuery(() => ({ queryKey: queryKeys.metadata, queryFn: fetchMetadata }));
  const village = createQuery(() => ({
    queryKey: queryKeys.villageNewcomers,
    queryFn: fetchVillageNewcomers,
  }));

  const snap = $derived($playerState);
  const breedingLevel = $derived(snap?.BreedingLevel ?? 0);
  const quarantined = $derived(snap ? snap.Quarantine_Active !== 0 : false);

  // Modul: ONE QUESTION, NOT TWO TABS.
  //
  // This screen used to open on a tab choice - "Marry the village" against
  // "Cross your own" - which asks a player to understand the difference before
  // they are allowed to begin. The player who reported this could not work out
  // which one he wanted, and he was right that nothing on the screen told him.
  //
  // It is one hero and one partner now, with the partner list grouped. The two
  // engine methods still exist; which one gets called falls out of what was
  // picked, and a player never has to learn there were two systems.
  //
  // The grouping is not cosmetic: a child copies each aptitude whole from one
  // parent, so crossing your own line converges on what you already have. The
  // village is the only thing that puts a genuinely new number into a
  // bloodline, which is why it is listed first.

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

  let nowSeconds = $state(Math.floor(connection.serverNowMs() / 1000));
  $effect(() => {
    const timer = setInterval(() => {
      nowSeconds = Math.floor(connection.serverNowMs() / 1000);
    }, 1000);
    return () => clearInterval(timer);
  });

  const aptitudesOf = (c: BreedingCandidate) =>
    c.AptitudeStrength + '/' + c.AptitudeSkill + '/' + c.AptitudeEndurance + '/' + c.AptitudeFortune;

  const villagerAptitudes = (p: VillageNewcomer) =>
    p.AptitudeStrength + '/' + p.AptitudeSkill + '/' + p.AptitudeEndurance + '/' + p.AptitudeFortune;

  /**
   * Modul: A NAME FIRST. This label used to lead with eight hex digits of the
   * character's Guid - "Human man b6b704ca - lv 1, Elder, gen 0, needs 50" -
   * and the player who reported this could not find his own main character in
   * a list of ten. Correctly: nothing in the list referred to it.
   *
   * There is no level here any more either. It quoted a characters.Level column
   * that nothing in the server ever wrote, so every row read "lv 1, needs 50"
   * and the screen was telling every player to go and find a character that
   * could not exist.
   */
  function heroLabel(candidate: BreedingCandidate): string {
    const marks = [
      raceName(candidate.LocusRaceDominant) + ' ' + (candidate.IsFemale ? 'woman' : 'man'),
      agePhaseName(candidate.AgePhase),
      aptitudesOf(candidate),
    ];
    if (candidate.AgePhase < 1) marks.push('still a child');
    if (candidate.IsEpicMutation) marks.push('epic');
    if (candidate.IsInbred) marks.push('inbred');
    if (candidate.BreedingCooldownEndEpoch > nowSeconds) {
      marks.push('resting ' + (candidate.BreedingCooldownEndEpoch - nowSeconds) + 's');
    }
    const who = candidate.Name || candidate.CharacterId.slice(0, 8);
    return who + ' - ' + marks.join(', ');
  }

  /**
   * Why a villager cannot pair with the chosen hero, or null.
   *
   * Mirrors BreedingGateRules.CheckVillagerPair. The server answers every
   * refusal with a command result now rather than rolling back in silence, but
   * a reason shown BEFORE the button is pressed is worth more than one after.
   *
   * "Has already married" is IsElder on the server - a word that means SPENT,
   * not old, and which collided on this very screen with the Elder age phase.
   * That collision was the first thing the reporting player got wrong, so the
   * word does not appear here at all.
   */
  function villagerBlockedReason(person: VillageNewcomer): string | null {
    if (person.IsElder) return 'has already married in';
    if (!hero) return null;
    if (hero.IsFemale === person.IsFemale) return 'both ' + (person.IsFemale ? 'women' : 'men');
    if (hero.LocusRaceDominant !== person.RaceId) return 'not ' + raceName(hero.LocusRaceDominant);
    return null;
  }

  /** The same question asked of one of your own characters. */
  function characterBlockedReason(candidate: BreedingCandidate): string | null {
    if (!hero) return null;
    if (candidate.CharacterId === hero.CharacterId) return 'that is the hero';
    if (hero.IsFemale === candidate.IsFemale) return 'both ' + (candidate.IsFemale ? 'women' : 'men');
    if (hero.LocusRaceDominant !== candidate.LocusRaceDominant) {
      return 'not ' + raceName(hero.LocusRaceDominant);
    }
    if (candidate.AgePhase < 1) return 'still a child';
    if (candidate.BreedingCooldownEndEpoch > nowSeconds) return 'resting';
    return null;
  }

  function partnerLabel(candidate: BreedingCandidate): string {
    const blocked = characterBlockedReason(candidate);
    const who = candidate.Name || candidate.CharacterId.slice(0, 8);
    const base = who + ' - ' + raceName(candidate.LocusRaceDominant) + ' ' +
      (candidate.IsFemale ? 'woman' : 'man') + ', ' + aptitudesOf(candidate);
    return blocked ? base + ' (' + blocked + ')' : base;
  }

  function villagerLabel(person: VillageNewcomer): string {
    const blocked = villagerBlockedReason(person);
    const base = raceName(person.RaceId) + ' ' + (person.IsFemale ? 'woman' : 'man') +
      ' - ' + villagerAptitudes(person);
    return blocked ? base + ' (' + blocked + ')' : base;
  }

  // --- what you are breeding FOR --------------------------------------------

  /**
   * Modul: THE BREEDING GROUNDS FINALLY DOES SOMETHING.
   *
   * Its level was read in four places on the server and every one of them
   * tested `<= 0`, so every upgrade past the first changed no number anywhere
   * in the game. It buys SELECTION now: a chosen aptitude takes the better
   * parent's value outright instead of the weighted coin, where a 4 against a 6
   * takes the 6 only 60% of the time - which is why a bloodline kept losing
   * ground on the exact stat the player was trying to raise.
   */
  const selectableCount = $derived(selectableAptitudeCount(breedingLevel));
  let selectionMask = $state(0);

  /** Trimmed the way the server trims it, so the screen cannot promise more than it sends. */
  const effectiveMask = $derived(clampSelectionMask(selectionMask, breedingLevel));
  const selectedCount = $derived(APTITUDES.filter((_, i) => (effectiveMask & (1 << i)) !== 0).length);

  function toggleAptitude(index: number) {
    const bit = 1 << index;
    if ((selectionMask & bit) !== 0) {
      selectionMask &= ~bit;
      return;
    }
    // Drop the oldest choice rather than refusing the new one - a checkbox that
    // silently does nothing is worse than one that swaps.
    if (selectedCount >= selectableCount) {
      for (let i = 0; i < APTITUDES.length; i++) {
        if ((selectionMask & (1 << i)) !== 0) {
          selectionMask &= ~(1 << i);
          break;
        }
      }
    }
    selectionMask |= bit;
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
   * and the player is no longer asked which is which. Whichever of the two is
   * male goes in first, which is exactly what the engine already does for a
   * village pairing. The old screen made the player get this right and refused
   * them in silence when they did not.
   */
  const paternalId = $derived(
    hero && partnerCharacter
      ? hero.IsFemale
        ? partnerCharacter.CharacterId
        : hero.CharacterId
      : '',
  );
  const maternalId = $derived(
    hero && partnerCharacter
      ? hero.IsFemale
        ? hero.CharacterId
        : partnerCharacter.CharacterId
      : '',
  );

  const rosterPreview = createQuery(() => ({
    queryKey: queryKeys.breedingPreview(paternalId, maternalId),
    queryFn: () => fetchBreedingPreview(paternalId, maternalId),
    enabled: paternalId !== '' && maternalId !== '' && paternalId !== maternalId,
  }));

  const preview = $derived(partnerIsVillager ? villagePreview.data : rosterPreview.data);

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
   * Modul: the preview answered in SERVER CODES - "parent_on_cooldown" was
   * rendered to the player verbatim. A refusal nobody can read is a refusal
   * that teaches nothing.
   *
   * An unknown code falls through to the raw string rather than a shrug, so a
   * new one is visible rather than swallowed.
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

    // The villager is spent and the child joins the roster, so both lists are
    // stale the moment this lands.
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.breedingRoster });
      client.invalidateQueries({ queryKey: queryKeys.villageNewcomers });
    }, 900);
  }

  const canBreed = $derived(
    breedingLevel > 0 &&
      heroId !== '' &&
      partnerKey !== '' &&
      (preview ? preview.IsEligible && preview.HasSufficientGold : false),
  );

  // --- season pass ----------------------------------------------------------
  // Modul: ClaimedMilestonesBitmask was REMOVED from StateUpdatePacket along
  // with the pass level and seasonal XP, so which milestones are already
  // claimed is not readable anywhere this client can reach. Milestones are
  // therefore offered without a claimed/unclaimed mark, and a repeat claim is
  // the server's to reject - stating that rather than inventing a checkmark
  // that would be a guess.
  const passLevel = $derived(metadata.data?.ChroniclePassLevel ?? 0);
  const seasonalXp = $derived(metadata.data?.AccumulatedSeasonalXp ?? 0);

  let milestone = $state(0);

  function claimMilestone() {
    const outcome = claimBattlePassMilestone(milestone, quarantined);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    setTimeout(() => client.invalidateQueries({ queryKey: queryKeys.metadata }), 900);
  }

  function buyPass() {
    purchaseBattlePass();
    setTimeout(() => client.invalidateQueries({ queryKey: queryKeys.metadata }), 900);
  }
</script>

<div class="grid">
  <!-- Modul: what the bloodline is worth, first. It is the reason
       breeding exists, and it was invisible until now. -->
  <AptitudePanel />

  <section class="panel">
    <h2>Breeding lab</h2>

    <!-- Modul: WHERE THIS SITS. Breeding interlocks with four other systems and
         none of them were named here, so a player could work the screen without
         ever learning that the Inn stocks the partner list or that a child has
         to be fielded from the Hall before it can grow up. -->
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

    <!-- Modul: ONE QUESTION. A hero and a partner. The two tabs this replaced
         asked the player to understand the difference between crossing their
         own line and marrying the village before they were allowed to begin. -->
    <label>
      Your hero
      <select bind:value={heroId}>
        <option value="">Choose...</option>
        {#each candidates as candidate (candidate.CharacterId)}
          <option value={candidate.CharacterId}>{heroLabel(candidate)}</option>
        {/each}
      </select>
    </label>

    <label>
      Partner
      <select bind:value={partnerKey}>
        <option value="">Choose...</option>
        <optgroup label="From the village - new blood">
          {#each newcomers as person (person.Id)}
            <option value={'v:' + person.Id} disabled={villagerBlockedReason(person) !== null}>
              {villagerLabel(person)}
            </option>
          {/each}
        </optgroup>
        <optgroup label="Your own line - refines what you have">
          {#each candidates as candidate (candidate.CharacterId)}
            <option
              value={'c:' + candidate.CharacterId}
              disabled={characterBlockedReason(candidate) !== null}
            >
              {partnerLabel(candidate)}
            </option>
          {/each}
        </optgroup>
      </select>
    </label>

    <!-- Modul: WHICH LIST TO PICK FROM, stated rather than implied. A child
         copies each aptitude whole from one parent, so crossing your own line
         converges on what you already have; the village is the only thing that
         puts a number into a bloodline that was not already in it. -->
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

    <!-- Modul: BREED FOR SOMETHING. The Breeding Grounds level was read in four
         places on the server and every one tested `<= 0`, so every upgrade past
         the first changed no number in the game. This is what it buys. -->
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
              <input
                type="checkbox"
                checked={on}
                onchange={() => toggleAptitude(index)}
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
      {#if !p.IsEligible}
        <p class="warn">{p.IneligibleReason ? refusal(p.IneligibleReason) : 'These two cannot pair.'}</p>
      {:else}
        <p class="cost" class:short={!p.HasSufficientGold}>
          Costs {p.BreedingCostGold.toLocaleString()}g
          {#if !p.HasSufficientGold}&middot; not enough gold{/if}
          {#if p.IsInbredRisk}&middot; <span class="risk">related pair</span>{/if}
        </p>
      {/if}

      <ChildPreview
        preview={p}
        mode={partnerIsVillager ? 'village' : 'roster'}
        {generation}
      />
    {/if}

    <button onclick={breed} disabled={!canBreed}>Breed</button>

    <!-- Modul: WHAT HAPPENS NEXT, which the screen never said. A child is not
         playable where it lands, and a villager is spent for ever - two facts a
         player could only discover by doing it. -->
    <p class="dim tiny">
      The child is born into the <strong>Hall of Ancestors</strong>. Field it
      into one of your slots and it grows from a child into an adult after an
      hour. A villager who marries in is <strong>spent for ever</strong> &mdash;
      everybody marries once &mdash; so spend a good one deliberately.
    </p>
  </section>

  <section class="panel">
    <h2>Chronicle pass</h2>

    {#if metadata.isPending}
      <Skeleton />
    {:else}
      <dl class="stats">
        <div><dt>Pass level</dt><dd>{passLevel}</dd></div>
        <div><dt>Seasonal XP</dt><dd>{seasonalXp.toLocaleString()}</dd></div>
        <div><dt>Transactions</dt><dd>{metadata.data?.EventHorizonTransactionCount ?? 0}</dd></div>
      </dl>

      <button onclick={buyPass}>Unlock premium track</button>
      <p class="dim tiny">
        Spends PremiumDiamonds server-side - no real-money purchase is involved
        in unlocking the track.
      </p>

      <h3>Claim a milestone</h3>
      <div class="row">
        <input type="number" min="0" max="49" bind:value={milestone} />
        <button disabled={quarantined} onclick={claimMilestone}>Claim</button>
      </div>
      <!-- Modul: ClaimedMilestonesBitmask was removed from StateUpdatePacket
           along with the pass level, and no endpoint replaced it - so which
           milestones are already claimed is not readable by this client at
           all. Milestones are entered by index rather than shown as a checked
           list, because a list would have to invent the checkmarks. -->
      <p class="dim tiny">
        Which milestones you have already claimed is not exposed by any endpoint,
        so they are claimed by index and a repeat is the server's to refuse.
        Indices run 0-49.
      </p>
    {/if}
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

  h3 {
    margin: 1.1rem 0 0.4rem;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
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

  label {
    display: grid;
    gap: 0.25rem;
    font-size: 0.8rem;
    color: var(--text-dim);
    margin-bottom: 0.6rem;
  }

  select,
  input {
    font: inherit;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.4rem 0.5rem;
    width: 100%;
  }

  .row {
    display: grid;
    grid-template-columns: 1fr auto;
    gap: 0.4rem;
  }

  /* Modul: the aptitude and gene lists moved into ui/ChildPreview.svelte with
     their styles, so the preview is explained from one place. What is left
     here is the frame around it, and the .tabs rules that used to sit here
     went with the tabs themselves - one hero, one partner, no mode to pick. */

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
     44px floor and the input rides inside it. That is the lesson check:touch
     was written to record. */
  .apt-choice {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    min-height: 44px;
    padding: 0.3rem 0.45rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    cursor: pointer;
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

  .stats {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 0.5rem;
    margin: 0 0 0.7rem;
  }

  .stats div {
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
</style>
