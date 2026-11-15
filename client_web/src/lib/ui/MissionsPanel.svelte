<script lang="ts">
  // Modul: TODAY'S MISSIONS (owner, 2026-11-15; docs/design/CHRONICLE_PASS.md
  // step 2). Six a day: the fixed ones first (dungeons, an expedition, the
  // world boss in its week), then the rotating ones, all drawn by the server
  // from what this player has open - so nothing here can be impossible.
  //
  // One Claim button takes every finished mission and, once all are done, the
  // bonus. Each mission pays Chronicle XP - the pass's only input, so a claim
  // here refreshes the pass tab too. The Missions tab of ChronicleWindow.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { claimMissions, fetchMissions, queryKeys, rerollMission, type MissionDay } from '../net/rest';
  import { requestScreen } from '../stores/navigation';
  import { play } from './audio';
  import { MISSION_SCREEN } from './missions';

  interface Props {
    /** Closes the window before a "Go" changes screen. */
    onLeave: () => void;
  }

  let { onLeave }: Props = $props();

  const client = useQueryClient();
  const day = createQuery(() => ({
    queryKey: queryKeys.missions,
    queryFn: fetchMissions,
    // Modul: FRESH ON EVERY OPEN (owner, 2026-11-15: "opening five wardrobe
    // chests did not register"). It had - the server counted all eleven - but
    // the app-wide 30 s staleTime let the tab show the cached 0/1 it had
    // fetched before the chests were opened. While open it refetches often.
    staleTime: 0,
    refetchOnMount: 'always' as const,
    refetchInterval: 10_000,
  }));

  let busy = $state(false);
  let gained = $state(0);
  let note = $state('');

  function put(view: MissionDay) {
    client.setQueryData(queryKeys.missions, view);
  }

  async function claim() {
    if (busy) return;
    busy = true;
    note = '';
    try {
      const result = await claimMissions();
      put(result.Day);
      if (result.Outcome === 'Ok') {
        gained = result.XpGained;
        play('itemSold');
        void client.invalidateQueries({ queryKey: queryKeys.chronicle });
      } else {
        note = 'Nothing new to claim yet.';
      }
    } catch {
      note = 'The claim did not go through. Try again in a moment.';
    } finally {
      busy = false;
    }
  }

  async function reroll(slot: number) {
    if (busy) return;
    busy = true;
    note = '';
    try {
      const result = await rerollMission(slot);
      put(result.Day);
      if (result.Outcome === 'NothingElseToOffer') note = 'There is no other mission you can do today.';
      else if (result.Outcome === 'NoRerollsLeft') note = 'Today\'s free swap is used.';
      else if (result.Outcome !== 'Ok') note = 'That mission cannot be swapped.';
    } catch {
      note = 'The swap did not go through. Try again in a moment.';
    } finally {
      busy = false;
    }
  }

  function go(action: string) {
    const screen = MISSION_SCREEN[action];
    if (!screen) return;
    onLeave();
    requestScreen(screen);
  }

  function hoursLeft(seconds: number): string {
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    return h > 0 ? `${h} h ${m} min` : `${m} min`;
  }

  const view = $derived(day.data);
  const doneCount = $derived(view ? view.Missions.filter((m) => m.Done).length : 0);
</script>

{#if day.isError}
  <p class="error small">The missions could not be loaded. Try again in a moment.</p>
{:else if !view}
  <p class="dim small">Loading...</p>
{:else}
  <p class="dim small">
    {doneCount} of {view.Missions.length} done &middot; new missions in {hoursLeft(view.SecondsUntilNextDay)}.
    Each pays {view.Missions[0]?.Xp ?? 30} Chronicle XP &middot; {view.RerollsLeft} free swaps left.
  </p>
  <ol class="list" data-testid="missions-list">
    {#each view.Missions as m (m.Slot)}
      <li class="mission" class:done={m.Done} class:claimed={m.Claimed} data-action={m.Action} data-done={m.Done} data-claimed={m.Claimed}>
        <div class="text">
          <span class="title">{m.Title}{#if m.Fixed}<span class="tag">daily</span>{/if}</span>
          <span class="hint dim tiny">{m.Hint}</span>
          <div class="bar" role="progressbar" aria-valuemin="0" aria-valuemax={m.Target} aria-valuenow={m.Progress}>
            <span style:width="{Math.min(100, (m.Progress / m.Target) * 100)}%"></span>
          </div>
        </div>
        <div class="side">
          <span class="count">{m.Claimed ? 'Claimed' : `${m.Progress}/${m.Target}`}</span>
          {#if !m.Done && MISSION_SCREEN[m.Action]}
            <button class="small-btn" onclick={() => go(m.Action)}>Go</button>
          {/if}
          {#if m.Swappable}
            <button class="small-btn" onclick={() => reroll(m.Slot)} disabled={busy}
              title={m.Fixed ? 'This one cannot be done today - replace it (does not use a swap)' : `Swap for another mission (${view.RerollsLeft} free swaps left today)`}
              data-testid="missions-reroll">{m.Fixed ? 'Replace' : 'Swap'}</button>
          {/if}
        </div>
      </li>
    {/each}
  </ol>
  <div class="bonus" class:ready={view.AllDone && !view.BonusClaimed} class:taken={view.BonusClaimed} data-testid="missions-bonus">
    <span>All done bonus</span>
    <span>{view.BonusClaimed ? 'Claimed' : `+${view.BonusXp} XP`}</span>
  </div>
  {#if note}<p class="small dim">{note}</p>{/if}
  <div class="foot">
    {#if gained > 0}<span class="gained" data-testid="missions-gained">+{gained} Chronicle XP</span>{/if}
    <button class="primary" onclick={claim} disabled={busy || view.Claimable === 0} data-testid="missions-claim">
      {view.Claimable > 0 ? `Claim (${view.Claimable})` : 'Claim'}
    </button>
  </div>
{/if}

<style>
  .foot {
    display: flex;
    gap: 0.6rem;
    align-items: center;
    justify-content: flex-end;
    margin-top: 0.6rem;
  }

  .gained {
    color: var(--accent);
    font-weight: 600;
  }

  .list {
    list-style: none;
    margin: 0.5rem 0;
    padding: 0;
    display: grid;
    gap: 0.45rem;
  }

  .mission {
    display: flex;
    gap: 0.6rem;
    align-items: center;
    padding: 0.55rem 0.65rem;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: transparent;
  }

  .mission.done {
    border-color: var(--good);
  }

  .mission.claimed {
    opacity: 0.6;
  }

  .text {
    flex: 1;
    min-width: 0;
    display: grid;
    gap: 0.2rem;
  }

  .title {
    font-weight: 600;
    display: flex;
    gap: 0.4rem;
    align-items: center;
    flex-wrap: wrap;
  }

  .tag {
    font-size: 0.7rem;
    font-weight: 500;
    padding: 0 0.35rem;
    border-radius: 4px;
    border: 1px solid var(--border);
    text-transform: uppercase;
    letter-spacing: 0.03em;
  }

  .bar {
    height: 6px;
    border-radius: 3px;
    background: var(--border);
    overflow: hidden;
  }

  .bar span {
    display: block;
    height: 100%;
    background: var(--accent);
  }

  .mission.done .bar span {
    background: var(--good);
  }

  .side {
    display: flex;
    gap: 0.35rem;
    align-items: center;
    flex-shrink: 0;
  }

  .count {
    font-variant-numeric: tabular-nums;
    min-width: 3.2rem;
    text-align: right;
  }

  .small-btn {
    min-height: 44px;
    min-width: 44px;
    flex-shrink: 0;
  }

  .bonus {
    display: flex;
    justify-content: space-between;
    padding: 0.55rem 0.65rem;
    border: 1px dashed var(--border);
    border-radius: 8px;
  }

  .bonus.ready {
    border-style: solid;
    border-color: var(--accent);
  }

  .bonus.taken {
    opacity: 0.6;
  }

  .error {
    color: var(--danger);
  }

  @media (max-width: 40rem) {
    .mission {
      flex-wrap: wrap;
    }

    .side {
      width: 100%;
      justify-content: flex-end;
    }
  }
</style>
