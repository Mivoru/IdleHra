<script lang="ts">
  import { formatGold, numberTitle } from './format';
  // Modul: THE VILLAGE GENE POOL, shown.
  //
  // Breeding takes each aptitude from one parent, so a child can never exceed
  // the best value already in the pair by much. Outside blood is the only thing
  // that actually moves a bloodline, and this is where it comes from.
  //
  // Modul: A SHORTLIST, NOT A ROSTER (task 103). This was a 28rem inner
  // scroller cut mid-row, with married-in elders mixed into it, the aptitude
  // key UNDER the list and two paragraphs of rules above it - all of it ahead
  // of the buildings on the page. The decision here is "is anybody worth
  // marrying", so the strongest few by aptitude sum come first, the rest are
  // one tap away, the elders (a record, not a choice) are collapsed, and the
  // rules sit behind a disclosure. No inner scroller: it caught the thumb on
  // Android and hid where it was cut.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { queryKeys, fetchVillageNewcomers, fetchTraits, type VillageNewcomer } from '../net/rest';
  import { APTITUDE_VILLAGE_CEILING, recruitVillager, dismissNewcomer } from '../net/commands';
  import { pushLocalNotice } from '../stores/game';
  import RaceIcon from './RaceIcon.svelte';
  import TraitBadge from './TraitBadge.svelte';
  import { raceName } from './races';
  import { traitsOf } from './traits';
  import Skeleton from './Skeleton.svelte';
  import ConfirmButton from './ConfirmButton.svelte';
  import { commandInFlight } from './commandInFlight';
  import { APTITUDES } from './aptitudes';

  const client = useQueryClient();
  const folk = createQuery(() => ({
    queryKey: queryKeys.villageNewcomers,
    queryFn: fetchVillageNewcomers,
  }));
  const traitCatalogue = createQuery(() => ({ queryKey: queryKeys.traits, queryFn: fetchTraits, staleTime: Infinity }));

  const data = $derived(folk.data);

  /** How many of the unmarried show before "Show all". */
  const SHORTLIST = 5;

  function sum(p: VillageNewcomer): number {
    return p.AptitudeStrength + p.AptitudeSkill + p.AptitudeEndurance + p.AptitudeFortune;
  }

  const unmarried = $derived(
    (data?.Newcomers ?? []).filter((p) => !p.IsElder).sort((a, b) => sum(b) - sum(a) || b.ArrivedAtEpoch - a.ArrivedAtEpoch),
  );
  const elders = $derived((data?.Newcomers ?? []).filter((p) => p.IsElder));

  // Modul: THE SERVER COUNTS ELDERS AGAINST THE CAP. VillageArrivalEngine and
  // RecruitBlockedReason both count every VillageNewcomers row, married in or
  // not, so this fraction is all of them - the same number the feast refusal
  // quotes. A fraction of only the unmarried would disagree with that sentence.
  const placesUsed = $derived(data?.Newcomers.length ?? 0);

  let showAll = $state(false);
  let showElders = $state(false);
  let showRules = $state(false);
  let openAnyway = $state(false);

  // Collapsed to one line until there is something to look at: no Inn and
  // nobody arrived means a new player would read two paragraphs about a pool
  // that is empty and cannot yet fill.
  const dormant = $derived(data !== undefined && data.InnLevel < 1 && data.Newcomers.length === 0);
  const expanded = $derived(!dormant || openAnyway);

  const shown = $derived(showAll ? unmarried : unmarried.slice(0, SHORTLIST));

  function hours(seconds: number): string {
    return `${Math.round(seconds / 3600)}h`;
  }

  // Modul: the two decisions the population cap exists to pose. A full village
  // STOPS the arrival clock, so somebody who turned up at 4/3/9/2 is occupying
  // the slot a twenty would have walked into.
  function refresh() {
    setTimeout(() => client.invalidateQueries({ queryKey: queryKeys.villageNewcomers }), 900);
  }

  function feast() {
    const outcome = recruitVillager();
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    refresh();
  }

  // Modul: held pending from the tap until the server answers. The row stayed
  // live for the 900ms before refresh() refetched, and a second tap sent a
  // second dismissal for somebody already gone.
  const sendKey = (person: VillageNewcomer) => `newcomer:${person.Id}`;

  function sendAway(person: VillageNewcomer) {
    const outcome = commandInFlight.run(sendKey(person), () => dismissNewcomer(person.Id));
    if (outcome === null) return;
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    refresh();
  }
</script>

{#snippet person(p: VillageNewcomer)}
  <li class:elder={p.IsElder}>
    <RaceIcon raceId={p.RaceId} />
    <!-- Modul: a person, with a name. This read "Human / woman" - the one
         screen that asks you to marry somebody did not say who. -->
    <div class="who">
      <strong>{p.Name || raceName(p.RaceId)}</strong>
      <span class="dim tiny">
        {raceName(p.RaceId)} {p.IsFemale ? 'woman' : 'man'}{#if p.IsElder} · has married in{/if}
      </span>
      {#if p.TraitMask > 0 && traitCatalogue.data}
        <span class="traits">
          {#each traitsOf(p.TraitMask, traitCatalogue.data) as trait (trait.Id)}<TraitBadge {trait} />{/each}
        </span>
      {/if}
    </div>
    <span class="apts">
      {#each APTITUDES as apt (apt.short)}
        <span aria-label="{apt.name} {apt.of(p)}">{apt.of(p)}</span>
      {/each}
    </span>
    <!-- An elder married into the line. They are a record of the blood that
         came in, not a resident, and the server refuses to dismiss them - so
         no button rather than a button that fails.
         Modul: two taps - sending somebody on is permanent, and the next
         newcomer is a day or two away. -->
    <span class="act">
      {#if !p.IsElder}
        <ConfirmButton
          small
          label="Send on"
          confirmLabel="Really send?"
          title="Send them on their way and free the place"
          disabled={$commandInFlight.has(sendKey(p))}
          onConfirm={() => sendAway(p)}
        />
      {/if}
    </span>
  </li>
{/snippet}

{#snippet columns()}
  <!-- The key ABOVE the numbers it labels, aligned with them - it used to sit
       under the whole list as "Strength · Skill · Endurance · Fortune". -->
  <div class="cols" aria-hidden="true">
    <span class="cols-who"></span>
    <span class="apts">
      {#each APTITUDES as apt (apt.short)}<span title={apt.name}>{apt.short[0]}</span>{/each}
    </span>
    <span class="act"></span>
  </div>
{/snippet}

<section class="panel folk" data-testid="gene-pool">
  <header>
    <h3>The gene pool</h3>
    {#if data}
      <!-- Modul: LABELLED. "101 / 11" sat here with no word beside it, next to
           the Village's own unlabelled "184/35" - two fractions of two
           different things. -->
      <span
        class="tally"
        class:full={placesUsed >= data.PopulationCap}
        data-testid="gene-pool-count"
        data-count={placesUsed}
        title="Everybody in the village, married in or not, against the places the Inn provides"
      >
        Newcomers {placesUsed} / {data.PopulationCap}
        {#if placesUsed > data.PopulationCap}
          <span class="over">over the cap</span>
        {:else if placesUsed >= data.PopulationCap}
          <span class="over">full</span>
        {/if}
      </span>
    {/if}
  </header>

  {#if folk.isPending}
    <Skeleton rows={3} />
  {:else if folk.isError}
    <p class="warn-line">The village roster could not be loaded.</p>
  {:else if data}
    {#if !expanded}
      <p class="dim small one-line">
        Nobody has arrived yet. Build the <strong>Inn</strong> and outside blood
        starts coming to marry into your line.
        <button type="button" class="link" onclick={() => (openAnyway = true)}>Show</button>
      </p>
    {:else}
      <p class="dim small">
        Outside blood: marrying somebody new in is what raises a line.
        {#if placesUsed >= data.PopulationCap}
          <strong class="warn-text">Full - arrivals have stopped.</strong>
        {:else}
          Next arrival within {hours(data.IntervalSeconds)}.
        {/if}
      </p>

      <button type="button" class="disclose" aria-expanded={showRules} onclick={() => (showRules = !showRules)}>
        <span class="caret" aria-hidden="true">{showRules ? '▾' : '▸'}</span> How the gene pool works
      </button>
      {#if showRules}
        <div class="rules">
          <!-- Modul: THIS SENTENCE WAS WRONG once, and wrong in the direction
               that makes the mechanic look absolute. A child CAN beat the
               better parent - by one, on a 25% drift roll or a 5% epic - it
               just cannot do more than that. -->
          <p class="dim tiny">
            A child copies each aptitude whole from one parent and can only beat
            the better of them by one, so a closed line barely climbs &mdash;
            marrying somebody new in is what raises it.
          </p>
          <p class="dim tiny">
            A <strong>newcomer</strong> arrives every {hours(data.IntervalSeconds)} while
            there is room &mdash; the Inn (level {data.InnLevel}) sets how often they
            come, how many fit, and how high their aptitudes roll, up to
            {APTITUDE_VILLAGE_CEILING}. Everybody who has married in still takes a
            place. A full village stops the clock entirely.
          </p>
          <!-- Modul: the interlock, said where it bites. -->
          <p class="dim tiny">
            Marry one in on the Breeding screen: any grown adult of the same race
            and the opposite sex &mdash; there is no level requirement. Everybody
            marries <strong>once</strong>. None of them survives the season, so
            blood you do not marry in is blood you lose.
          </p>
        </div>
      {/if}

      {#if unmarried.length === 0}
        <p class="dim small">Nobody is waiting to marry in.</p>
      {:else}
        {@render columns()}
        <ul data-testid="gene-pool-list">
          {#each shown as p (p.Id)}{@render person(p)}{/each}
        </ul>
        {#if unmarried.length > SHORTLIST}
          <button type="button" class="more" data-testid="gene-pool-show-all" onclick={() => (showAll = !showAll)}>
            {showAll ? `Show the best ${SHORTLIST}` : `Show all ${unmarried.length}`}
          </button>
        {/if}
      {/if}

      {#if elders.length > 0}
        <button
          type="button"
          class="disclose"
          aria-expanded={showElders}
          data-testid="gene-pool-elders-toggle"
          onclick={() => (showElders = !showElders)}
        >
          <span class="caret" aria-hidden="true">{showElders ? '▾' : '▸'}</span> Married in ({elders.length})
        </button>
        {#if showElders}
          {@render columns()}
          <ul class="elders">
            {#each elders as p (p.Id)}{@render person(p)}{/each}
          </ul>
        {/if}
      {/if}

      <div class="feast">
        <button disabled={data.RecruitBlockedReason !== ''} onclick={feast} data-exact={data.RecruitCostGold} title={numberTitle(data.RecruitCostGold)}>
          Throw a feast &middot; {formatGold(data.RecruitCostGold)}
        </button>
        <p class="dim tiny">
          {#if data.RecruitBlockedReason}
            {data.RecruitBlockedReason}
          {:else}
            Attracts somebody today instead of in {hours(data.IntervalSeconds)}. Each
            feast this season costs more than the last.
          {/if}
        </p>
      </div>
    {/if}
  {/if}
</section>

<style>
  /* Modul: THIS PANEL HAD NO PADDING OF ITS OWN. Village.svelte's `.panel`
     rule is scoped to Village, so it never reached a child component's
     section, and the footer line ran onto the corner bracket app.css draws
     4px in from each corner. Its own surface, and a bottom gap past the
     bracket's 22px - on a phone too, where app.css forces every panel's
     padding to 0.7rem. */
  .folk {
    display: grid;
    gap: 0.5rem;
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem 1rem 1.5rem;
  }

  @media (max-width: 40rem) {
    .folk > :last-child {
      margin-bottom: 0.75rem;
    }

    /* A phone row is a name, four numbers and a button. The portrait goes
       first, so the name never crushes to zero width (client_web/CLAUDE.md). */
    li :global(.race) {
      display: none;
    }
  }

  header {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    justify-content: space-between;
    gap: 0.4rem 0.6rem;
  }

  h3 {
    margin: 0;
  }

  .tally {
    flex: none;
    padding: 0.15rem 0.5rem;
    border: 1px solid var(--brass);
    border-radius: var(--radius);
    color: var(--brass-lit);
    font-variant-numeric: tabular-nums;
    font-size: 0.85rem;
  }

  /* A full village stops the clock, so the number is the warning - in words
     as well as colour. */
  .tally.full {
    border-color: var(--warn);
    color: var(--warn);
  }

  .over {
    font-size: 0.72rem;
    font-weight: 700;
    margin-left: 0.25rem;
  }

  .warn-text {
    color: var(--warn);
  }

  .one-line {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.4rem;
  }

  .link {
    flex-shrink: 0;
    font-size: 0.8rem;
    padding: 0.15rem 0.6rem;
  }

  .disclose,
  .more {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    width: 100%;
    padding: 0.3rem 0.5rem;
    font: inherit;
    font-size: 0.82rem;
    text-align: left;
    color: inherit;
    background: var(--bg);
    background-image: none;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    box-shadow: none;
  }

  .more {
    justify-content: center;
  }

  .caret {
    width: 0.8rem;
    color: var(--text-dim);
  }

  .rules {
    display: grid;
    gap: 0.3rem;
    padding: 0.4rem 0.6rem;
    background: var(--bg);
    border-radius: var(--radius);
  }

  ul {
    display: grid;
    gap: 0.3rem;
    margin: 0;
    padding: 0;
    list-style: none;
  }

  li {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.35rem 0.5rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    min-width: 0;
  }

  li.elder {
    opacity: 0.7;
    border-style: dashed;
  }

  .who {
    display: grid;
    gap: 0.05rem;
    min-width: 0;
    flex: 1 1 auto;
  }

  .traits {
    display: flex;
    flex-wrap: wrap;
    gap: 0.2rem;
    margin-top: 0.15rem;
  }

  .apts {
    display: flex;
    gap: 0.2rem;
    flex: none;
    font-variant-numeric: tabular-nums;
    font-size: 0.85rem;
  }

  .apts > span {
    width: 1.5rem;
    text-align: center;
    border-radius: 3px;
    background: var(--bg);
    color: var(--brass-lit);
  }

  /* The column key, laid out like a row so each letter sits over its number:
     the row's padding and border, then the same fixed-width action column
     every row has (empty on an elder), so the letters line up whatever the
     button says - "Send on" and "Really send?" are different widths. */
  .cols {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0 calc(0.5rem + 1px);
    margin-bottom: -0.2rem;
    font-size: 0.66rem;
    letter-spacing: 0.05em;
    color: var(--text-dim);
  }

  .cols-who {
    flex: 1 1 auto;
  }

  .cols .apts {
    font-size: inherit;
  }

  .act {
    flex: none;
    width: 6rem;
    display: flex;
    justify-content: flex-end;
  }

  .act :global(button) {
    flex-shrink: 0;
    width: 100%;
  }

  .cols .apts > span {
    background: none;
    color: var(--text-dim);
  }

  .feast {
    display: grid;
    gap: 0.2rem;
    margin-top: 0.2rem;
  }

  .feast button {
    font: inherit;
    padding: 0.35rem 0.5rem;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--brass);
    border-radius: var(--radius);
    cursor: pointer;
  }

  .feast button:disabled {
    opacity: 0.5;
    border-color: var(--border);
    cursor: default;
  }

  p {
    margin: 0;
  }

  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.85rem;
  }
  .tiny {
    font-size: 0.72rem;
  }

  .warn-line {
    color: var(--warn);
    font-size: 0.85rem;
  }
</style>
