<script lang="ts">
  // Modul: THE LOGIN WEEK OPENS ITSELF, ONCE A DAY (owner, 2026-11-15). The
  // first session of a UTC day that still has a claim open shows the week and
  // a Claim button; the player takes the reward by hand and sees what came.
  //
  // It used to be credited silently inside the password sign-in, which a
  // refresh-token session never runs - one active player went ten days without
  // a reward and nobody could see why. The server now only answers whether
  // today is claimable; this window asks at session start.
  //
  // "Later" closes it for this UTC day on this device (sessionStorage would
  // forget it on every reload of a PWA). The claim stays open on the Progress
  // screen, so closing never forfeits anything.
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { pendingNotes } from '../stores/version';
  import { offlineSummary, playerState } from '../stores/game';
  import { onboardingDismissed } from '../stores/tutorial';
  import { guidedStage } from '../stores/guided';
  import { EVENT_PHASE, eventThemeKey } from '../net/seasonalEvent';
  import { eventIntroClosed, eventIntroSeen } from './eventIntroSeen';
  import { bigChestPending } from './bigChestPending';
  import { claimLoginBonus, fetchLoginBonus, queryKeys, type LoginBonusClaim } from '../net/rest';
  import { cosmeticKeys } from '../net/cosmetics';
  import { dungeonKeys } from '../net/dungeon';
  import { play } from './audio';
  import LoginWeekGrid from './LoginWeekGrid.svelte';
  import Money from './Money.svelte';
  import Modal from './Modal.svelte';

  const STORAGE_KEY = 'folkidle.loginWeek.laterDay';

  function utcDay(): number {
    return Math.floor(Date.now() / 86_400_000);
  }

  function laterToday(): boolean {
    try {
      return Number(localStorage.getItem(STORAGE_KEY) ?? -1) === utcDay();
    } catch {
      return false;
    }
  }

  let closed = $state(laterToday());
  let claiming = $state(false);
  let claimed = $state<LoginBonusClaim | null>(null);
  let failed = $state(false);

  // After What's new, the offline summary, the guided first minute, the
  // event's introduction and the big-chest prompt - never beside any of them.
  const introKey = $derived(eventThemeKey(Number($playerState?.SeasonalEventId ?? 0)));
  const introPending = $derived.by(() => {
    void $eventIntroClosed;
    return introKey !== null
      && Number($playerState?.SeasonalEventPhase ?? 0) === EVENT_PHASE.Live
      && !eventIntroSeen(introKey);
  });
  const eligible = $derived(
    !closed && !introPending && !$bigChestPending
      && $pendingNotes.length === 0 && $playerState !== null && $offlineSummary === null
      && guidedStage($playerState, $onboardingDismissed) === null,
  );

  const client = useQueryClient();
  const week = createQuery(() => ({
    queryKey: queryKeys.loginBonus,
    queryFn: fetchLoginBonus,
    enabled: eligible,
  }));

  // A failed read opens nothing: the claim is still on the Progress screen,
  // and a daily window that shows an error at sign-in is worse than none.
  const open = $derived(eligible && (claimed !== null || (!week.isError && week.data?.ClaimableToday === true)));

  function later() {
    closed = true;
    try {
      localStorage.setItem(STORAGE_KEY, String(utcDay()));
    } catch {
      // Blocked storage: it simply asks again next session.
    }
  }

  async function claim() {
    if (claiming) return;
    claiming = true;
    failed = false;
    try {
      const result = await claimLoginBonus();
      if (result.Outcome === 'Ok') {
        claimed = result;
        play(result.LootChest || result.WardrobeChest ? 'chestShine' : 'itemSold');
      } else {
        closed = true;
      }
      // What the claim touched: the week itself, the chest shelf, the wardrobe and the gates.
      void client.invalidateQueries({ queryKey: queryKeys.loginBonus });
      void client.invalidateQueries({ queryKey: queryKeys.lootChests });
      void client.invalidateQueries({ queryKey: cosmeticKeys.mine });
      void client.invalidateQueries({ queryKey: dungeonKeys.view });
    } catch {
      failed = true;
    } finally {
      claiming = false;
    }
  }

  function done() {
    closed = true;
  }
</script>

{#if open}
  <Modal label="Daily reward" tone="brass" width="30rem" layout="block" onClose={claimed ? done : later}
    dismissOnScrim={false} testid="daily-login">
    {#snippet header()}
      <div class="head">
        <h2>{claimed ? `Day ${claimed.Day} claimed` : 'Daily reward'}</h2>
        <button class="close" aria-label="Close" onclick={claimed ? done : later}>&times;</button>
      </div>
    {/snippet}
    {#snippet footer()}
      <div class="foot">
        {#if claimed}
          <button class="primary" onclick={done} data-testid="daily-login-done">Great</button>
        {:else}
          <button onclick={later} data-testid="daily-login-close">Later</button>
          <button class="primary" onclick={claim} disabled={claiming} data-testid="daily-login-claim">
            {claiming ? 'Claiming...' : `Claim day ${week.data?.Day ?? ''}`}
          </button>
        {/if}
      </div>
    {/snippet}

    {#if claimed}
      <div class="got" data-testid="daily-login-got">
        <p class="lead">You received:</p>
        <ul>
          {#if claimed.Gold > 0}<li><Money amount={claimed.Gold} icon /></li>{/if}
          {#if claimed.Diamonds > 0}<li><Money amount={claimed.Diamonds} kind="diamond" icon /></li>{/if}
          {#if claimed.LootChest}<li>{claimed.LootChest} loot chest - open it in the Village Chest</li>{/if}
          {#if claimed.WardrobeChest}<li>{claimed.WardrobeChest} wardrobe chest - open it in the Wardrobe</li>{/if}
          {#if claimed.TicketsGranted > 0}<li>{claimed.TicketsGranted} dungeon tickets</li>{/if}
        </ul>
        <p class="dim tiny">Come back tomorrow for day {claimed.Day === 7 ? 1 : claimed.Day + 1}.</p>
      </div>
    {:else if week.data}
      <p class="dim small">
        Seven days, then the week starts again. Miss a day and nothing is lost - you carry on from where you were.
      </p>
      <LoginWeekGrid state={week.data} />
      {#if failed}<p class="error small">The claim did not go through. Try again in a moment.</p>{/if}
    {/if}
  </Modal>
{/if}

<style>
  .head {
    display: flex;
    align-items: center;
    gap: 0.6rem;
  }

  .head h2 {
    margin: 0;
    flex: 1;
  }

  .close {
    min-width: 44px;
    min-height: 44px;
  }

  .foot {
    display: flex;
    gap: 0.5rem;
    justify-content: flex-end;
    flex-wrap: wrap;
  }

  .got ul {
    list-style: none;
    margin: 0.4rem 0 0.6rem;
    padding: 0;
    display: grid;
    gap: 0.35rem;
    font-size: var(--fs-md);
  }

  .lead {
    margin: 0;
  }

  .error {
    color: var(--danger);
  }
</style>
