<script lang="ts">
  // Modul: THE EVENT INTRODUCES ITSELF, ONCE (owner, 2026-10-10). After "What's
  // new" (and "Welcome back") close, a player who has not seen it on this
  // device gets one window saying what the seasonal event is, what it gives
  // and WHERE each part lives - the shop, the pets, the boss on the World Boss
  // screen - because none of it is in the menu. Every number (dates, rates,
  // prices) is the server's answer from GET /api/v1/event; the words about
  // where things are describe this client's own screens.
  //
  // Seen-ness is per device and per event key, in localStorage: a second
  // device shows it once more, which for a one-off explainer is the right
  // side to err on. Blocked storage shows it every session, so it is guarded.
  import { createQuery } from '@tanstack/svelte-query';
  import { pendingNotes } from '../stores/version';
  import { offlineSummary, playerState } from '../stores/game';
  import { requestScreen } from '../stores/navigation';
  import { onboardingDismissed } from '../stores/tutorial';
  import { guidedStage } from '../stores/guided';
  import { fetchSeasonalEvent, seasonalEventKeys, EVENT_PHASE, EVENT_SHOP_KIND } from '../net/seasonalEvent';
  import { chancePct, eventThemeKey } from '../net/seasonalEvent';
  import { formatExact } from './format';
  import { spriteUrl } from './spriteUrl';
  import Modal from './Modal.svelte';

  const STORAGE_PREFIX = 'folkidle.eventIntro.';

  const eventId = $derived(Number($playerState?.SeasonalEventId ?? 0));
  const phase = $derived(Number($playerState?.SeasonalEventPhase ?? 0));
  const key = $derived(eventThemeKey(eventId));

  function seen(k: string): boolean {
    try {
      return localStorage.getItem(STORAGE_PREFIX + k) === '1';
    } catch {
      return false;
    }
  }

  let dismissed = $state(false);

  const wanted = $derived(
    key !== null && phase === EVENT_PHASE.Live && !dismissed && !seen(key)
      && $pendingNotes.length === 0 && $playerState !== null && $offlineSummary === null
      // A brand-new player's guided first minute comes first: the window would
      // sit over its fence and its Skip button (found by the production smoke
      // run, which plays a fresh guest). It shows once the guide is done.
      && guidedStage($playerState, $onboardingDismissed) === null,
  );

  const event = createQuery(() => ({
    queryKey: seasonalEventKeys.all,
    queryFn: fetchSeasonalEvent,
    enabled: key !== null,
  }));
  const ev = $derived(event.data ?? null);
  const avatarPrice = $derived(ev?.Shop.find((i) => i.Kind === EVENT_SHOP_KIND.Avatar)?.Price ?? 0);
  const petPrice = $derived(ev?.Shop.find((i) => i.Kind === EVENT_SHOP_KIND.Pet)?.Price ?? 0);
  const ends = $derived(
    ev ? new Date(ev.EndUtc * 1000).toLocaleDateString(undefined, { day: 'numeric', month: 'long' }) : '',
  );

  function close() {
    dismissed = true;
    if (key) {
      try {
        localStorage.setItem(STORAGE_PREFIX + key, '1');
      } catch {
        // Storage blocked: it will show again next session, nothing worse.
      }
    }
  }

  function openShop() {
    close();
    requestScreen('event');
  }

  const pct = chancePct;
</script>

{#if wanted && ev && !event.isError}
  <Modal label={ev.Name} tone="brass" width="34rem" layout="block" onClose={close} dismissOnScrim={false}>
    {#snippet header()}
      <div class="head">
        <img src={spriteUrl('Events/samhain/currency/pumpkin.webp')} alt="" decoding="async" />
        <h2>{ev.Name} has begun</h2>
        <button class="close" aria-label="Close" onclick={close}>&times;</button>
      </div>
    {/snippet}
    {#snippet footer()}
      <div class="foot">
        <button class="primary" onclick={openShop} data-testid="event-intro-shop">Open the event shop</button>
        <button onclick={close} data-testid="event-intro-close">Got it</button>
      </div>
    {/snippet}

    <div class="body" data-testid="event-intro">
      <p class="lead">
        The old festival of the turning year - and The Cailleach, the Queen of Winter, is angry that the valley now calls
        it Halloween. Until <strong>{ends}</strong>:
      </p>

      <h3>Pumpkins</h3>
      <p>
        Every kill has a {pct(ev.KillChance)} chance of a pumpkin and every harvest {pct(ev.GatherChance)}, while you
        play and while you are away. Your pumpkins are the button beside your gold - <em>Event shop</em> - and on a phone
        the row under the header.
      </p>

      <h3>The event shop</h3>
      <p>
        <strong>Avatars</strong> ({formatExact(avatarPrice)} pumpkins) - a Samhain face for your profile, worn from the
        Wardrobe. <strong>Pets</strong> ({formatExact(petPrice)} pumpkins) - each follows one character and gives it a
        bonus; give it one on the <em>Character</em> screen, under Gear. Pets stay yours after the event.
      </p>

      <h3>A rare companion</h3>
      <p>Any pumpkin you find may bring the Witch with it. You will know when she comes.</p>

      <h3>The Cailleach</h3>
      <p>
        She waits on the <em>World Boss</em> screen (and under the event's Boss tab): six winters, each as hard as a
        region's boss the first time you met it, the sixth harder than anything in the valley. The first fall of each
        winter pays diamonds, gold, pumpkins and titles - and the sixth, the Mini Vampire. Try as often as you like.
      </p>

      <p class="dim tiny">
        When the event ends the shop stays open three more days; pumpkins left after that are gone.
      </p>
    </div>
  </Modal>
{/if}

<style>
  .head {
    display: flex;
    align-items: center;
    gap: 0.6rem;
  }

  .head img {
    width: 2.4rem;
    height: 2.4rem;
    object-fit: contain;
  }

  .head h2 {
    margin: 0;
    flex: 1;
  }

  .close {
    min-width: 44px;
    min-height: 44px;
  }

  .body h3 {
    margin: 0.9rem 0 0.2rem;
    color: var(--event-accent, var(--accent));
  }

  .body p {
    margin: 0.2rem 0;
  }

  .lead {
    margin-top: 0;
  }

  .foot {
    display: flex;
    gap: 0.5rem;
    justify-content: flex-end;
    flex-wrap: wrap;
  }
</style>
