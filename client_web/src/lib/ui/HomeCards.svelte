<script lang="ts" module>
  import type { PaceSample as Sample } from './pace';
  const firstReadings = new Map<string, Sample>();
</script>

<script lang="ts">
  // Modul: THE MAP ANSWERS "WHERE", THESE ANSWER "WHAT NOW".
  //
  // A player coming back to an idle game asks two things: is everybody still
  // working, and what am I working towards. The map is a good way to go
  // somewhere and says nothing about either, so the answers were on four other
  // screens - the halt reason on Combat, the jobs on Character, the goals on
  // Progress. These two cards bring the answers to where a session starts.
  //
  // "What to do next" is deliberately NOT a third card: the onboarding coach
  // already ranks exactly that (tutorialSteps -> discoveries -> objectives),
  // and a second list with its own opinion would be two sources for one truth.
  import { onMount } from 'svelte';
  import { createQuery } from '@tanstack/svelte-query';
  import { playerState } from '../stores/game';
  import { requestScreen } from '../stores/navigation';
  import { queryKeys, fetchDeeds } from '../net/rest';
  import { closestDeed } from './homeGoal';
  import { etaSeconds, formatEta, type PaceSample } from './pace';
  import { loadContent, monsterName, type ContentRegistry } from '../net/content';
  import { EMPTY_GUID } from '../net/commands';
  import {
    HALT_REASON_SHORT,
    SLOT_UNLOCK_TOWN_HALL,
    isGatheringActivity,
    isCraftingActivity,
    professionName,
  } from './slots';
  import { locationName, nodeLocation } from './locations';
  import { raceName } from './races';
  import Bar from './Bar.svelte';

  let registry = $state<ContentRegistry | null>(null);
  onMount(() => {
    void loadContent().then((loaded) => (registry = loaded));
  });

  const snap = $derived($playerState);
  // Refetched while the map is open, so the goal's counter can be watched
  // moving - that movement is the whole of the time estimate below.
  const deeds = createQuery(() => ({ queryKey: queryKeys.deeds, queryFn: fetchDeeds, refetchInterval: 60_000 }));

  // ActivityHaltReason: 1 out of food, 2 died. The only two a player can fix
  // with one press; the rest (quarantine, no character) are explained on the
  // screen the halt badge already points at.
  const HALT_FIX: Record<number, { label: string; screen: string }> = {
    1: { label: 'Stock the larder', screen: 'larder' },
    2: { label: 'Back to the fight', screen: 'combat' },
  };

  function jobLabel(activityId: number): string {
    if (activityId === 0) return 'Idle';
    if (isGatheringActivity(activityId)) {
      return `${professionName(Math.floor(activityId / 1000) - 1)} in ${locationName(nodeLocation(activityId))}`;
    }
    if (isCraftingActivity(activityId)) return 'Crafting';
    return `Fighting ${monsterName(registry, activityId)}`;
  }

  const workers = $derived.by(() => {
    if (!snap) return [];
    const townHall = snap.TownHallLevel ?? 0;
    return [
      { slot: 1, id: snap.Slot1_CharacterId, raceId: snap.Slot1_RaceId, activity: Number(snap.ActiveActivityId), halt: snap.ActivityHaltReason },
      { slot: 2, id: snap.Slot2_CharacterId, raceId: snap.Slot2_RaceId, activity: Number(snap.Slot2ActivityId), halt: snap.Slot2ActivityHaltReason },
      { slot: 3, id: snap.Slot3_CharacterId, raceId: snap.Slot3_RaceId, activity: Number(snap.Slot3ActivityId), halt: snap.Slot3ActivityHaltReason },
    ].filter((w) => w.id !== EMPTY_GUID && townHall >= SLOT_UNLOCK_TOWN_HALL[w.slot - 1]);
  });

  const goal = $derived(closestDeed(deeds.data?.Chapters ?? []));

  // The first reading of each deed this tab has seen, kept at module level so
  // it survives leaving the map and coming back. See lib/ui/pace.ts.
  const eta = $derived.by((): string | null => {
    if (!goal) return null;
    const latest: PaceSample = { at: deeds.dataUpdatedAt, value: goal.Current };
    const first = firstReadings.get(goal.Id);
    if (!first || first.value > latest.value) {
      firstReadings.set(goal.Id, latest);
      return null;
    }
    const seconds = etaSeconds(first, latest, goal.Target);
    return seconds === null ? null : formatEta(seconds);
  });
</script>

{#if snap}
  <div class="cards">
    <section class="card">
      <h2>Right now</h2>
      <ul class="workers">
        {#each workers as worker (worker.slot)}
          {@const halt = HALT_REASON_SHORT[worker.halt] ?? ''}
          {@const fix = HALT_FIX[worker.halt]}
          <li>
            <div class="who">
              <strong>{raceName(worker.raceId)}</strong>
              <span class:idle={worker.activity === 0}>{jobLabel(worker.activity)}</span>
              {#if halt}
                <span class="halt">{halt}</span>
              {/if}
            </div>
            {#if fix}
              <button onclick={() => requestScreen(fix.screen)}>{fix.label}</button>
            {:else if worker.activity === 0}
              <button onclick={() => requestScreen('character')}>Give a job</button>
            {/if}
          </li>
        {/each}
      </ul>
    </section>

    {#if goal}
      <section class="card">
        <h2>Closest goal</h2>
        <p class="goal-title"><strong>{goal.Title}</strong></p>
        <Bar
          value={Math.min(goal.Current, goal.Target)}
          max={Math.max(1, goal.Target)}
          label={`${Math.min(goal.Current, goal.Target).toLocaleString()} / ${goal.Target.toLocaleString()}`}
        />
        <div class="goal-row">
          <span class="dim">
            {eta ? `${eta} at this session's pace` : 'From the Book of Deeds'}
          </span>
          <button onclick={() => requestScreen(goal.Screen)}>Go</button>
        </div>
      </section>
    {/if}
  </div>
{/if}

<style>
  .cards {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(17rem, 1fr));
    gap: 1rem;
    margin-top: 1rem;
  }

  .card {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.9rem 1rem;
    display: grid;
    gap: 0.5rem;
    align-content: start;
  }

  h2 {
    margin: 0;
    font-size: 1.05rem;
  }

  .workers {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.5rem;
  }

  .workers li {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.4rem 0.8rem;
  }

  .who {
    flex: 1 1 12rem;
    min-width: 0;
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 0.2rem 0.5rem;
  }

  .workers button,
  .goal-row button {
    flex-shrink: 0;
  }

  .idle {
    color: var(--text-dim);
  }

  /* The word says it; the colour only draws the eye. */
  .halt {
    font-size: 0.8rem;
    color: var(--danger);
    border: 1px solid var(--danger);
    border-radius: 999px;
    padding: 0 0.5rem;
  }

  .goal-title {
    margin: 0;
  }

  .goal-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.8rem;
  }

  .dim {
    color: var(--text-dim);
    font-size: 0.85rem;
  }
</style>
