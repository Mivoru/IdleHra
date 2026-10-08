<script lang="ts" module>
  import type { PaceSample as Sample } from './pace';
  const firstReadings = new Map<string, Sample>();
</script>

<script lang="ts">
  import { formatNumber } from './format';
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
  import { playerState, pushLocalNotice } from '../stores/game';
  import { requestScreen } from '../stores/navigation';
  import { queryKeys, fetchDeeds, fetchWorn, fetchBreedingRoster } from '../net/rest';
  import { isSameKindOfWork } from './slots';
  import { assignCharacterActivity } from '../net/commands';
  import { writePref } from '../net/prefs';
  import { bossRegionOf } from './victories';
  import {
    formatOfflineCap,
    lastActivityKey,
    nearestScreenUnlock,
    nextUnlockLine,
    rememberedActivity,
  } from './homeNow';
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
  import { screenLocks } from '../stores/tutorial';
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

  // Modul: TASK 73 - CHARACTERS HAVE NAMES. The card said "Human" three times;
  // the breeding roster is where the server publishes a character's name.
  const roster = createQuery(() => ({ queryKey: queryKeys.breedingRoster, queryFn: fetchBreedingRoster, staleTime: 60_000 }));
  const nameById = $derived(new Map((roster.data ?? []).map((c) => [c.CharacterId, c.Name])));
  function who(worker: { id: string; raceId: number }): string {
    return nameById.get(worker.id) || raceName(worker.raceId);
  }

  // Every job a character can be given from here. Crafting stays on the
  // Character screen (its list needs the recipe query); "More jobs" goes there.
  const jobGroups = $derived.by(() => {
    if (!registry) return [] as { title: string; jobs: { id: number; label: string }[] }[];
    const open = snap?.HighestUnlockedRegion || 1;
    return [
      {
        title: 'Fight',
        jobs: registry.regions.slice(0, open).flat().map((m) => ({ id: m.Id, label: m.Name })),
      },
      {
        title: 'Gather',
        jobs: registry.gatheringNodes.map((node) => ({
          id: node.ActivityId,
          label: `${professionName(node.ProfessionType)} - ${locationName(nodeLocation(node.ActivityId))}`,
        })),
      },
    ];
  });

  function activityLabelFor(id: number): string {
    return jobLabel(id);
  }

  function takenBy(activityId: number, bySlot: number): string | null {
    if (!snap) return null;
    // One character per KIND of work (CharacterSlotEngine, 2026-10-08).
    if (bySlot !== 1 && isSameKindOfWork(Number(snap.ActiveActivityId), activityId)) return 'Slot 1';
    if (bySlot !== 2 && isSameKindOfWork(snap.Slot2ActivityId, activityId)) return 'Slot 2';
    if (bySlot !== 3 && isSameKindOfWork(snap.Slot3ActivityId, activityId)) return 'Slot 3';
    return null;
  }

  let pickerSlot = $state(0);
  let pickerGroup = $state(0);

  function assign(worker: { slot: number; id: string }, activityId: number) {
    const outcome = assignCharacterActivity(worker.id, activityId, { takenBy: takenBy(activityId, worker.slot) });
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    writePref(lastActivityKey(worker.id), String(activityId));
    pickerSlot = 0;
  }

  // Task 73: the offline limit is the server's own effective figure
  // (OfflineCapSeconds). How long the player has already been away is not on
  // the wire, so the line says what the limit IS and never a countdown.
  const offlineCap = $derived(formatOfflineCap(Number(snap?.OfflineCapSeconds ?? 0)));

  const worn = createQuery(() => ({ queryKey: queryKeys.worn, queryFn: fetchWorn, refetchInterval: 60_000 }));
  const unlock = $derived.by((): string | null => {
    if (!registry || !snap) return null;
    const content = registry;
    const region = snap.HighestUnlockedRegion || 1;
    const boss = content.regions[region - 1]?.find((m) => bossRegionOf(m.Id) === region)?.Name ?? null;
    // Task 109: below the boss's gear threshold the boss line was endgame
    // jargon on day one ("0 of 8 pieces at region 1 Rare"); the nearest screen
    // that is still locked is the unlock a new player can actually work on.
    const nearer = nearestScreenUnlock(Number(snap.CurrentLevel), $screenLocks);
    return nextUnlockLine(region, content.regions.length, boss, worn.data?.Pieces ?? null, (id) => content.itemsByBaseId.get(id)?.RegionTier ?? 1, nearer);
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
              <!-- Task 109: slot 1 is the character Combat fights with - the
                   player's own person. A bare "Brennus" left a new player
                   asking who that was. -->
              <strong data-testid="home-worker-name">{who(worker)}{#if worker.slot === 1}<span class="you"> (you)</span>{/if}</strong>
              <span class:idle={worker.activity === 0}>{jobLabel(worker.activity)}</span>
              {#if halt}
                <span class="halt">{halt}</span>
              {/if}
            </div>
            {#if fix}
              <button onclick={() => requestScreen(fix.screen)}>{fix.label}</button>
            {/if}
            {#if worker.activity === 0}
              {@const last = rememberedActivity(worker.id, worker.slot)}
              {#if last > 0 && !takenBy(last, worker.slot)}
                <button data-testid="home-continue" onclick={() => assign(worker, last)}>
                  Continue: {activityLabelFor(last)}
                </button>
              {/if}
              <button data-testid="home-give-job" onclick={() => (pickerSlot = pickerSlot === worker.slot ? 0 : worker.slot)}>
                {last > 0 ? 'Other job' : 'Give a job'}
              </button>
            {/if}
            {#if pickerSlot === worker.slot}
              <div class="picker" data-testid="home-job-picker">
                <div class="tabs">
                  {#each jobGroups as group, index (group.title)}
                    <button class:on={pickerGroup === index} onclick={() => (pickerGroup = index)}>{group.title}</button>
                  {/each}
                  <button onclick={() => requestScreen('character')}>Crafting...</button>
                </div>
                <div class="jobs">
                  {#each jobGroups[pickerGroup]?.jobs ?? [] as job (job.id)}
                    {@const taken = takenBy(job.id, worker.slot)}
                    <button disabled={!!taken} onclick={() => assign(worker, job.id)}>
                      {job.label}{taken ? ` (${taken})` : ''}
                    </button>
                  {/each}
                </div>
              </div>
            {/if}
          </li>
        {/each}
      </ul>
      {#if offlineCap}
        <p class="dim" data-testid="home-offline-cap">Offline progress is kept for up to {offlineCap}.</p>
      {/if}
      {#if unlock}
        <p class="dim" data-testid="home-next-unlock"><strong>Next unlock:</strong> {unlock}</p>
      {/if}
    </section>

    {#if goal}
      <section class="card">
        <h2>Closest goal</h2>
        <p class="goal-title"><strong>{goal.Title}</strong></p>
        <Bar
          value={Math.min(goal.Current, goal.Target)}
          max={Math.max(1, goal.Target)}
          label={`${formatNumber(Math.min(goal.Current, goal.Target))} / ${formatNumber(goal.Target)}`}
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
    margin-bottom: 0;
    /* Desktop: two cards side by side kept their own heights - a short goal
       card stretched to the height of a long roster was a box of nothing. */
    align-items: start;
  }

  .you {
    font-weight: 400;
    color: var(--text-dim);
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

  .picker {
    flex: 1 1 100%;
    display: grid;
    gap: 0.4rem;
  }

  .tabs,
  .jobs {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
  }

  .jobs {
    max-height: 14rem;
    overflow-y: auto;
  }

  .tabs .on {
    border-color: var(--accent, currentColor);
  }

  .card p {
    margin: 0;
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
