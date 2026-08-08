<script lang="ts">
  // Modul: THE VILLAGE GENE POOL, shown.
  //
  // Breeding takes each aptitude from one parent, so a child can never exceed
  // the best value already in the pair. Outside blood is the only thing that
  // actually moves a bloodline, and this is where it comes from - so the
  // village needed a face, not just a list of buildings.
  //
  // The cap is shown as "11 / 14" because that fraction is the whole decision:
  // somebody arrives at 4/3/9/2, and a full village means keeping them or
  // turning them away for a better roll later.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { queryKeys, fetchVillageNewcomers, type VillageNewcomer } from '../net/rest';
  import { APTITUDE_VILLAGE_CEILING, recruitVillager, dismissNewcomer } from '../net/commands';
  import { pushLocalNotice } from '../stores/game';
  import RaceIcon from './RaceIcon.svelte';
  import { raceName } from './races';
  import Skeleton from './Skeleton.svelte';

  const client = useQueryClient();
  const folk = createQuery(() => ({
    queryKey: queryKeys.villageNewcomers,
    queryFn: fetchVillageNewcomers,
  }));

  const data = $derived(folk.data);

  function hours(seconds: number): string {
    return `${Math.round(seconds / 3600)}h`;
  }

  // Modul: the two decisions the population cap exists to pose, and neither
  // had a button. A full village STOPS the arrival clock, so somebody who
  // turned up at 4/3/9/2 is occupying the slot a twenty would have walked
  // into - "keep them or send them on" is the whole game of the gene pool,
  // and it was unplayable.
  function refresh() {
    setTimeout(() => client.invalidateQueries({ queryKey: queryKeys.villageNewcomers }), 900);
  }

  function feast() {
    const outcome = recruitVillager();
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    refresh();
  }

  function sendAway(person: VillageNewcomer) {
    const outcome = dismissNewcomer(person.Id);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    refresh();
  }
</script>

<section class="panel folk">
  <header>
    <div>
      <h3>The village</h3>
      <p class="dim small">
        Outside blood. Breeding never exceeds the best value already in a pair,
        so a line only climbs by marrying somebody new in.
      </p>
    </div>
    {#if data}
      <span class="tally" class:full={data.Newcomers.length >= data.PopulationCap}>
        {data.Newcomers.length} / {data.PopulationCap}
      </span>
    {/if}
  </header>

  {#if folk.isPending}
    <Skeleton rows={3} />
  {:else if folk.isError}
    <p class="warn-line">The village roster could not be loaded.</p>
  {:else if data}
    <p class="dim tiny">
      Someone new every {hours(data.IntervalSeconds)} while there is room — the
      Inn (level {data.InnLevel}) sets both how often they come and how good
      they are, up to {APTITUDE_VILLAGE_CEILING}.
    </p>

    {#if data.Newcomers.length === 0}
      <p class="dim small">Nobody has settled here yet.</p>
    {:else}
      <ul>
        {#each data.Newcomers as person (person.Id)}
          <li class:elder={person.IsElder}>
            <RaceIcon raceId={person.RaceId} />
            <div class="who">
              <strong>{raceName(person.RaceId)}</strong>
              <span class="dim tiny">
                {person.IsFemale ? 'woman' : 'man'}{#if person.IsElder} · has married in{/if}
              </span>
            </div>
            <span class="apts">
              <span title="Strength">{person.AptitudeStrength}</span>
              <span title="Skill">{person.AptitudeSkill}</span>
              <span title="Endurance">{person.AptitudeEndurance}</span>
              <span title="Fortune">{person.AptitudeFortune}</span>
            </span>
            <!-- An elder married into the line. They are a record of the blood
                 that came in, not a resident, and the server refuses to dismiss
                 them - so no button rather than a button that fails. -->
            {#if !person.IsElder}
              <button
                class="send"
                title="Send them on their way and free the slot"
                onclick={() => sendAway(person)}
              >
                Send on
              </button>
            {/if}
          </li>
        {/each}
      </ul>
      <p class="dim tiny key">Strength &middot; Skill &middot; Endurance &middot; Fortune</p>
    {/if}

    <div class="feast">
      <button disabled={data.RecruitBlockedReason !== ''} onclick={feast}>
        Throw a feast &middot; {data.RecruitCostGold.toLocaleString()}g
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
</section>

<style>
  .folk {
    display: grid;
    gap: 0.5rem;
  }

  header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 0.6rem;
  }

  h3 {
    margin: 0 0 0.1rem;
  }

  header p {
    margin: 0;
    max-width: 40ch;
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

  /* A full village stops the clock, so the number is the warning. */
  .tally.full {
    border-color: var(--warn);
    color: var(--warn);
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
    opacity: 0.6;
    border-style: dashed;
  }

  .who {
    display: grid;
    gap: 0.05rem;
    min-width: 0;
  }

  .apts {
    display: flex;
    gap: 0.3rem;
    margin-left: auto;
    font-variant-numeric: tabular-nums;
    font-size: 0.85rem;
  }

  .apts span {
    min-width: 1.5rem;
    padding: 0.05rem 0.2rem;
    text-align: center;
    border-radius: 3px;
    background: var(--bg);
    color: var(--brass-lit);
  }

  .send {
    flex: none;
    font: inherit;
    font-size: 0.72rem;
    padding: 0.15rem 0.4rem;
    color: var(--text-dim);
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    cursor: pointer;
  }

  .send:hover {
    color: var(--warn);
    border-color: var(--warn);
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

  .key,
  p {
    margin: 0;
  }

  .warn-line {
    color: var(--warn);
    font-size: 0.85rem;
  }
</style>
