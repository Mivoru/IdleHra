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
  } from '../lib/net/commands';
  import { connection } from '../lib/net/connection';
  import { agePhaseName } from '../lib/ui/slots';
  import { raceName } from '../lib/ui/races';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import AptitudePanel from '../lib/ui/AptitudePanel.svelte';

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

  // Modul: TWO PAIRINGS, and the village one first.
  //
  // A child takes each aptitude from ONE parent, so it can never exceed the
  // best value already in the pair - crossing your own characters converges on
  // what you already have. The village is the only thing that puts a number
  // into a bloodline that was not in it, which is why it is the default tab
  // and why the roster pairing is the one that needs explaining.
  let mode = $state<'village' | 'roster'>('village');

  // --- breeding -------------------------------------------------------------
  let paternalId = $state('');
  let maternalId = $state('');

  // Modul: ValidateBreedingRequest DISCONNECTS when both parents are the same
  // character, so each list excludes the other's pick - the same shape as the
  // fusion dropdowns, and for the same reason.
  const paternalChoices = $derived((roster.data ?? []).filter((c) => c.CharacterId !== maternalId));
  const maternalChoices = $derived((roster.data ?? []).filter((c) => c.CharacterId !== paternalId));

  const preview = createQuery(() => ({
    queryKey: queryKeys.breedingPreview(paternalId, maternalId),
    queryFn: () => fetchBreedingPreview(paternalId, maternalId),
    enabled: paternalId !== '' && maternalId !== '' && paternalId !== maternalId,
  }));

  let nowSeconds = $state(Math.floor(connection.serverNowMs() / 1000));
  $effect(() => {
    const timer = setInterval(() => {
      nowSeconds = Math.floor(connection.serverNowMs() / 1000);
    }, 1000);
    return () => clearInterval(timer);
  });

  function label(candidate: BreedingCandidate): string {
    const cooling = candidate.BreedingCooldownEndEpoch > nowSeconds;
    const marks = [
      `lv ${candidate.Level}`,
      agePhaseName(candidate.AgePhase),
      `gen ${candidate.GenerationIndex}`,
    ];
    if (candidate.IsEpicMutation) marks.push('epic');
    if (candidate.IsInbred) marks.push('inbred');
    if (cooling) marks.push(`cooling ${candidate.BreedingCooldownEndEpoch - nowSeconds}s`);
    return `${candidate.CharacterId.slice(0, 8)} - ${marks.join(', ')}`;
  }

  function breed() {
    const outcome = executeBreeding(paternalId, maternalId, breedingLevel);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    setTimeout(() => client.invalidateQueries({ queryKey: queryKeys.breedingRoster }), 900);
  }

  // --- hero x villager: the standard pair -----------------------------------
  let heroId = $state('');
  let villagerId = $state(0);

  const hero = $derived((roster.data ?? []).find((c) => c.CharacterId === heroId));
  const newcomers = $derived(village.data?.Newcomers ?? []);

  /**
   * Why a given villager cannot marry the chosen hero, or null if they can.
   *
   * The server refuses these by rolling the transaction back in silence, so
   * every one of them has to be visible here or a player learns nothing from
   * pressing the button. Mirrors ExecuteHeroVillagerBreedingAsync's own order.
   */
  function villagerBlockedReason(person: VillageNewcomer): string | null {
    if (person.IsElder) return 'has already married in';
    if (!hero) return null;
    if (hero.IsFemale === person.IsFemale) return `both ${person.IsFemale ? 'women' : 'men'}`;
    if (hero.LocusRaceDominant !== person.RaceId) return `not ${raceName(hero.LocusRaceDominant)}`;
    return null;
  }

  function villagerLabel(person: VillageNewcomer): string {
    const apt = [
      person.AptitudeStrength,
      person.AptitudeSkill,
      person.AptitudeEndurance,
      person.AptitudeFortune,
    ].join('/');
    const blocked = villagerBlockedReason(person);
    const who = `${raceName(person.RaceId)} ${person.IsFemale ? 'woman' : 'man'}`;
    return blocked ? `${who} - ${apt} (${blocked})` : `${who} - ${apt}`;
  }

  function heroLabel(candidate: BreedingCandidate): string {
    const apt = [
      candidate.AptitudeStrength,
      candidate.AptitudeSkill,
      candidate.AptitudeEndurance,
      candidate.AptitudeFortune,
    ].join('/');
    const who = `${raceName(candidate.LocusRaceDominant)} ${candidate.IsFemale ? 'woman' : 'man'}`;
    const marks = [`lv ${candidate.Level}`, apt];
    if (candidate.Level < 50) marks.push('needs 50');
    if (candidate.BreedingCooldownEndEpoch > nowSeconds) {
      marks.push(`resting ${candidate.BreedingCooldownEndEpoch - nowSeconds}s`);
    }
    return `${who} - ${marks.join(', ')}`;
  }

  /**
   * Modul: the preview answered in SERVER CODES - "parent_on_cooldown" was
   * rendered to the player verbatim. A refusal nobody can read is a refusal
   * that teaches nothing, which is the same failure as a deed with no counter.
   *
   * Covers both endpoints' reasons; an unknown code falls through to the raw
   * string rather than a shrug, so a new one is visible rather than swallowed.
   */
  function refusal(code: string): string {
    switch (code) {
      case 'hero_not_mature':
        return 'Your hero has to be an adult at level 50.';
      case 'parent_not_mature':
        return 'Both parents have to be adults at level 50.';
      case 'parent_locked_in_escrow':
        return 'That character is locked in a trade.';
      case 'parent_on_cooldown':
        return 'That character is still resting after the last child.';
      case 'villager_already_married':
        return 'They have already married into your line. Everyone marries once.';
      case 'same_sex':
        return 'A pair needs one of each.';
      case 'race_mismatch':
        return 'The two are of different races.';
      default:
        return code;
    }
  }

  const villagePreview = createQuery(() => ({
    queryKey: queryKeys.villagerBreedingPreview(heroId, villagerId),
    queryFn: () => fetchVillagerBreedingPreview(heroId, villagerId),
    enabled: heroId !== '' && villagerId > 0,
  }));

  function marry() {
    const outcome = executeVillagerBreeding(heroId, villagerId, breedingLevel);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    // The villager becomes an elder and the child joins the roster, so both
    // lists are stale the moment this lands.
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.breedingRoster });
      client.invalidateQueries({ queryKey: queryKeys.villageNewcomers });
    }, 900);
  }

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

    {#if breedingLevel === 0}
      <p class="warn">
        You have no Breeding Grounds. The server rejects breeding without them
        by disconnecting, so this screen will not send it.
      </p>
    {/if}

    <div class="tabs" role="tablist">
      <button
        role="tab"
        class:on={mode === 'village'}
        aria-selected={mode === 'village'}
        onclick={() => (mode = 'village')}
      >
        Marry the village
      </button>
      <button
        role="tab"
        class:on={mode === 'roster'}
        aria-selected={mode === 'roster'}
        onclick={() => (mode = 'roster')}
      >
        Cross your own
      </button>
    </div>

    {#if mode === 'village'}
      <!-- Modul: THE STANDARD PAIR. A child takes each aptitude from one
           parent, so it can never beat the best number already in the pair.
           Marrying outside is the only way a bloodline climbs. -->
      <p class="dim small">
        A hero of yours and somebody new. Only the hero needs level 50. A
        villager marries <strong>once</strong> and becomes an elder, so spend
        a good one carefully.
      </p>

      <label>
        Your hero
        <select bind:value={heroId}>
          <option value="">Choose...</option>
          {#each roster.data ?? [] as candidate (candidate.CharacterId)}
            <option value={candidate.CharacterId}>{heroLabel(candidate)}</option>
          {/each}
        </select>
      </label>

      <label>
        From the village
        <select bind:value={villagerId}>
          <option value={0}>Choose...</option>
          {#each newcomers as person (person.Id)}
            <option value={person.Id} disabled={villagerBlockedReason(person) !== null}>
              {villagerLabel(person)}
            </option>
          {/each}
        </select>
      </label>

      {#if village.data && newcomers.length === 0}
        <p class="dim tiny">
          Nobody has settled in your village yet. Somebody turns up every
          {Math.round(village.data.IntervalSeconds / 3600)}h while there is room.
        </p>
      {/if}

      {#if villagePreview.data}
        {@const p = villagePreview.data}
        {#if !p.IsEligible}
          <p class="warn">{p.IneligibleReason ? refusal(p.IneligibleReason) : 'These two cannot marry.'}</p>
        {:else}
          <p class="cost" class:short={!p.HasSufficientGold}>
            Costs {p.BreedingCostGold.toLocaleString()}g
            {#if !p.HasSufficientGold}&middot; not enough gold{/if}
          </p>
        {/if}

        <h3>What the child would inherit</h3>
        <ul class="apts">
          {#each p.Aptitudes as apt (apt.AptitudeName)}
            <li>
              <span class="name">{apt.AptitudeName}</span>
              <span class="band" class:up={apt.PredictedMax > Math.max(apt.ParentHero, apt.ParentPartner)}>
                {apt.PredictedMin}&ndash;{apt.PredictedMax}
              </span>
              <span class="dim tiny">
                you {apt.ParentHero} &middot; them {apt.ParentPartner}
              </span>
            </li>
          {/each}
        </ul>
        <p class="dim tiny">
          Each aptitude comes from one parent, weighted by who is stronger in
          it, then drifts by one. Cross two different specialists and the child
          is good at both.
        </p>
      {/if}

      <button
        onclick={marry}
        disabled={breedingLevel === 0 ||
          heroId === '' ||
          villagerId === 0 ||
          (villagePreview.data
            ? !villagePreview.data.IsEligible || !villagePreview.data.HasSufficientGold
            : false)}
      >
        Marry
      </button>
    {:else}
      <p class="dim small">
        Two of your own characters produce a third. Generation and inbreeding
        carry forward, so pairing close relatives has a real cost - and neither
        child can beat a number the pair does not already have.
      </p>

      <label>
        Paternal
        <select bind:value={paternalId}>
          <option value="">Choose...</option>
          {#each paternalChoices as candidate (candidate.CharacterId)}
            <option value={candidate.CharacterId}>{label(candidate)}</option>
          {/each}
        </select>
      </label>

      <label>
        Maternal
        <select bind:value={maternalId}>
          <option value="">Choose...</option>
          {#each maternalChoices as candidate (candidate.CharacterId)}
            <option value={candidate.CharacterId}>{label(candidate)}</option>
          {/each}
        </select>
      </label>

      {#if preview.data}
        {@const p = preview.data}
        {#if !p.IsEligible}
          <p class="warn">{p.IneligibleReason ? refusal(p.IneligibleReason) : 'These two cannot breed.'}</p>
        {:else}
          <p class="cost" class:short={!p.HasSufficientGold}>
            Costs {p.BreedingCostGold.toLocaleString()}g
            {#if !p.HasSufficientGold}&middot; not enough gold{/if}
            {#if p.IsInbredRisk}&middot; <span class="risk">inbreeding risk</span>{/if}
          </p>
        {/if}

        <h3>What the child would inherit</h3>
        <ul class="apts">
          {#each p.Aptitudes as apt (apt.AptitudeName)}
            <li>
              <span class="name">{apt.AptitudeName}</span>
              <span class="band" class:up={apt.PredictedMax > Math.max(apt.ParentHero, apt.ParentPartner)}>
                {apt.PredictedMin}&ndash;{apt.PredictedMax}
              </span>
              <span class="dim tiny">
                {apt.ParentHero} x {apt.ParentPartner}
              </span>
            </li>
          {/each}
        </ul>

        <h3>Predicted genes</h3>
        <ul class="loci">
          {#each p.Loci as locus (locus.LocusName)}
            <li>
              <span class="name">{locus.LocusName}</span>
              <span class="dim tiny">
                {locus.ParentPaternalDominant} x {locus.ParentMaternalDominant}
                &rarr; {locus.PredictedMinDominant}-{locus.PredictedMaxDominant}
                {#if locus.MutationChancePct > 0}
                  &middot; {locus.MutationChancePct.toFixed(1)}% mutation
                {/if}
              </span>
            </li>
          {/each}
        </ul>
      {/if}

      <button
        onclick={breed}
        disabled={breedingLevel === 0 ||
          paternalId === '' ||
          maternalId === '' ||
          (preview.data ? !preview.data.IsEligible || !preview.data.HasSufficientGold : false)}
      >
        Breed
      </button>
    {/if}
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
  .small {
    font-size: 0.8rem;
    margin: 0 0 0.7rem;
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

  .tabs {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.3rem;
    margin: 0 0 0.7rem;
  }

  .tabs button {
    font: inherit;
    font-size: 0.8rem;
    padding: 0.35rem 0.4rem;
    color: var(--text-dim);
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    cursor: pointer;
  }

  .tabs button.on {
    color: inherit;
    border-color: var(--brass, var(--border));
  }

  .apts {
    list-style: none;
    margin: 0 0 0.7rem;
    padding: 0;
    display: grid;
    gap: 0.25rem;
  }

  .apts li {
    display: grid;
    grid-template-columns: 1fr auto auto;
    align-items: baseline;
    gap: 0.5rem;
    font-size: 0.82rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.22rem;
  }

  .band {
    font-variant-numeric: tabular-nums;
    font-weight: 700;
  }

  /* The only outcome that moves a bloodline: a number neither parent had. */
  .band.up {
    color: var(--brass-lit, inherit);
  }

  .loci {
    list-style: none;
    margin: 0 0 0.7rem;
    padding: 0;
    display: grid;
    gap: 0.25rem;
  }

  .loci li {
    display: grid;
    gap: 0.05rem;
    font-size: 0.82rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.22rem;
  }

  .name {
    font-weight: 600;
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
