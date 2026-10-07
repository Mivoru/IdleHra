<script lang="ts">
  /*
    THE DELVE.

    Modul: A GOLD SINK THAT IS A GAME, because the arithmetic said one was
    needed and a fee would not have been played.

    Measured before any of this was written (GoldSinkAffordabilityTests): a
    player keeping up in region 5 earns 369,000 gold an hour, and every
    RECURRING sink in the game costs under 3% of that. Gold at the top had
    stopped being a currency.

    What this screen has to show, in the order a player asks:

      1. what a run costs, and whether they can afford it   (the gate)
      2. what is behind each door, and the odds              (the decision)
      3. what walking out is worth RIGHT NOW                 (the tension)
      4. what pushing on would be worth                      (the temptation)
      5. how much of the weekly ceiling is left              (the honesty)

    Point 3 is the one that makes it a game rather than a slot machine, and it
    is why the odds are printed rather than implied: a bank-or-push decision is
    only a decision if the numbers are on screen.

    NOTHING HERE DECIDES ANYTHING. Every outcome is rolled by DelveEngine; this
    sends "start", "door N" and "bank", and draws what comes back.

    THE DEEP (task 37). With the server's flag on, the bottom of floor 8 is a
    landing, not an end: "Descend into the Deep" banks floors 1-8 exactly as
    walking out would, then pays a toll priced on what the player HOLDS. The
    only number this screen sends is the stake it was shown (QuotedStake), and
    the server charges its own - never more than that. The Deep pays records and
    titles, plus one diamond per new weekly-deepest floor (inside the weekly
    ceiling); it pays no other diamonds, and the screen says so.
  */
  import { onMount } from 'svelte';
  import { play } from '../lib/ui/audio';
  import Money from '../lib/ui/Money.svelte';
  import { formatNumber } from '../lib/ui/format';
  import { formatWhen, minuteClock } from '../lib/ui/when';

  /*
    Modul: SVG, NOT A GLYPH. This screen shipped with ◆ and ● in its markup,
    which is a font's opinion rather than a drawing: the shapes differ by
    family, are not guaranteed to exist, and a screen reader announces "black
    diamond suit" in the middle of a diamond count. A path is the same
    everywhere and can be hidden from the accessibility tree outright.
  */
  import {
    fetchDelve,
    startDelve,
    chooseDelveDoor,
    bankDelve,
    descendDeep,
    buyDeepLantern,
    fetchTitles,
    setActiveTitle,
    type TitlesResponse,
    type DelveRunView,
    type DelveActionResponse,
  } from '../lib/net/rest';

  let view = $state<DelveRunView | null>(null);
  let busy = $state(false);
  let notice = $state('');
  let loadError = $state('');

  /** Mirrors AttributeRegistry - 0 Might, 1 Finesse, 2 Vigour, 3 Fortune. */
  const ATTRIBUTE_NAMES = ['Might', 'Finesse', 'Vigour', 'Fortune'];

  /*
    Modul: the flavour is per ATTRIBUTE, not per door, so a door that shows
    what it wants reads as a place rather than a stat check with a label. A
    hidden door gets the same line every time on purpose - the player is meant
    to learn nothing from it.
  */
  const DOOR_FLAVOUR = [
    'A jammed slab, sunk into its frame',
    'A narrow crack between two stones',
    'A low arch, weeping cold water',
    'A door with no handle at all',
  ];
  const HIDDEN_FLAVOUR = 'A cold draught from somewhere below';

  let titles = $state<TitlesResponse | null>(null);
  let titleNotice = $state('');

  async function loadTitles() {
    try {
      titles = await fetchTitles();
    } catch {
      titles = null;
    }
  }

  /*
    Modul: A TITLE PICKER OF BUTTONS, NOT A <select>. A native select whose
    options change while it is open is broken on Android's WebView (the
    Breeding pickers, 2026-09-13), and a list of names is not a select anyway.
    The names are the server's; this file keeps no list of titles.
  */
  async function wear(slug: string | null) {
    if (busy) return;
    busy = true;
    titleNotice = '';
    try {
      const answer = await setActiveTitle(slug);
      if (answer) {
        titles = answer;
        titleNotice =
          answer.Result === 'Ok'
            ? slug
              ? `You now go by ${answer.Active?.Name ?? 'that title'}.`
              : 'You go by your name alone.'
            : answer.Result === 'NotEarned'
              ? 'That title has not been earned yet.'
              : 'That title does not exist.';
      }
      await load();
    } catch (err) {
      titleNotice = err instanceof Error ? err.message : 'the request failed';
    } finally {
      busy = false;
    }
  }

  async function load() {
    try {
      view = await fetchDelve();
      loadError = '';
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'could not reach the Delve';
    }
  }

  onMount(() => {
    load();
    loadTitles();
  });

  function describe(outcome: DelveActionResponse): string {
    switch (outcome.Result) {
      case 'NotEnoughGold':
        return outcome.View.AtLanding
          ? `Not enough gold for the toll of ${formatNumber(outcome.View.DescendQuote)}. Nothing was charged.`
          : 'Not enough gold for the gate.';
      case 'RunAlreadyInProgress':
        return 'You are already down there.';
      case 'NoRunInProgress':
        return 'There is no run to do that to.';
      case 'InvalidDoor':
        return 'That door is not on this floor.';
      case 'RunLost':
        return 'The last lantern charge goes out. You come up with nothing.';
      case 'ChargeLost':
        return outcome.View.IsDeep && outcome.View.ChargesRemaining === 0
          ? 'The way is barred, and your lantern goes out.'
          : 'The way is barred. A lantern charge burns out, and the floor offers new doors.';
      case 'FloorCleared':
        return outcome.View.IsDeep
          ? `Floor ${outcome.View.CurrentFloor} is yours` +
            (outcome.DiamondsGranted > 0 ? ` - a new deepest this week: ${outcome.DiamondsGranted} diamond.` : '.') +
            ' The dark goes on below.'
          : 'Through. The stair keeps going down.';
      case 'AtTheBottom':
        return outcome.View.CanDescend
          ? 'The bottom of the Delve. Climb out with what you have, or pay to go deeper.'
          : 'The bottom. There is nothing below this - take what you have and climb.';
      case 'PriceChanged':
        return `Your purse grew since the price was shown. The stake is now ${formatNumber(outcome.View.StakeGold)} gold. Nothing was charged.`;
      case 'NotAtTheBottom':
        return 'There are still doors in front of you. Descending waits until the floor is clear.';
      case 'DeepDisabled':
        return 'The way into the Deep is closed for now.';
      case 'NoMoreLanterns':
        return 'There are no more lanterns to buy on this run.';
      case 'ChargesRemain':
        return 'Your lantern is still lit. A new one is only for when it goes out.';
      case 'LanternOut':
        return 'Your lantern is out. Light another, or walk out.';
      default:
        return '';
    }
  }

  async function act(fn: () => Promise<DelveActionResponse | null>) {
    if (busy) return;
    busy = true;
    notice = '';
    try {
      const outcome = await fn();
      if (outcome) {
        view = outcome.View;
        notice = describe(outcome);
        if (outcome.Result === 'Ok' && outcome.GoldCharged > 0) {
          // A descent: say what was banked on the way down and what the toll took.
          const banked: string[] = [];
          if (outcome.DiamondsGranted > 0) banked.push(`${outcome.DiamondsGranted} diamonds`);
          if (outcome.GoldReturned > 0) banked.push(`${formatNumber(outcome.GoldReturned)} gold`);
          notice =
            fn === lightLantern
              ? `You pay ${formatNumber(outcome.GoldCharged)} gold and the lantern burns again.`
              : (banked.length ? `Floors 1-8 banked: ${banked.join(' and ')}. ` : '') +
                `You pay ${formatNumber(outcome.GoldCharged)} gold and go down to floor ${outcome.View.CurrentFloor}.`;
        } else if (outcome.Result === 'Ok' && (outcome.DiamondsGranted > 0 || outcome.GoldReturned > 0)) {
          const parts: string[] = [];
          if (outcome.DiamondsGranted > 0) parts.push(`${outcome.DiamondsGranted} diamonds`);
          if (outcome.GoldReturned > 0) parts.push(`${formatNumber(outcome.GoldReturned)} gold`);
          notice = `You climb out with ${parts.join(' and ')}.`;
        }
      } else {
        await load();
      }
    } catch (err) {
      // Modul: SAID OUT LOUD. A refusal the player cannot see is this
      // server's favourite way to lie, and a minigame whose buttons silently
      // do nothing is indistinguishable from a broken one.
      notice = err instanceof Error ? err.message : 'the request failed';
    } finally {
      busy = false;
    }
  }

  const lightLantern = () => buyDeepLantern();
  const lanternOut = $derived(!!view?.IsDeep && view.ChargesRemaining === 0 && !view.AtLanding);

  const canAfford = $derived(!!view && view.CurrentGold >= view.EntryFeeForNextRun);

  // Modul: TASK 52 - a player who cannot pay the gate met the whole screen:
  // ledger, rules, odds, the Deep's titles - and one disabled button at the
  // end of it. One card says the same thing. The full screen stays one press
  // away for anyone who wants to read the rules before they can play.
  let showFullWhilePoor = $state(false);
  const teaser = $derived(!!view && !view.Active && !canAfford && !showFullWhilePoor);
  const saved = $derived(
    view && view.EntryFeeForNextRun > 0 ? Math.min(1, view.CurrentGold / view.EntryFeeForNextRun) : 0,
  );
  const ceilingLeft = $derived(view ? Math.max(0, view.WeeklyDiamondCeiling - view.DiamondsEarnedThisWeek) : 0);
  const atBottom = $derived(!!view?.Active && view.AtLanding);
  const isDeep = $derived(!!view?.IsDeep);

  // Modul: RESET TIMES, IN ONE PLACE (task 105). The Deep's course, its
  // records and the weekly diamond ceiling all turn over together - they share
  // DelveWeekKey - so one instant answers "when does it reset" for all three.
  // Task 95: through the shared formatter, so it reads like every other reset
  // ("Mon 02:00 - in 3 d").
  const resetsAt = $derived(
    view?.DeepWeekEndsUtc ? formatWhen(new Date(view.DeepWeekEndsUtc), $minuteClock) : 'Monday'
  );

  // Modul: THE UNDERGROUND MOOD (owner, 2026-10-02). The page itself goes
  // dark while this screen is open, in both themes; the palette is the --ug-*
  // block in app.css. Set on <html> because the page background is body's, and
  // removed on leave so no other screen inherits the cave.
  $effect(() => {
    const root = document.documentElement;
    root.dataset.mood = 'underground';
    return () => {
      if (root.dataset.mood === 'underground') delete root.dataset.mood;
    };
  });

  // The records fold to one line; the detail is one press away.
  let recordsOpen = $state(false);

  // Modul: "CLEAR FLOOR 10 TO EARN LAMPLIGHTER" ABOVE AN OWNED LAMPLIGHTER.
  // The server's NextTitle is priced on the deepest floor reached, and a title
  // can be held without that floor (granted, or carried from before a reset).
  // A goal the player already holds is not a goal, so it is not shown as one.
  const ownedSlugs = $derived(new Set((titles?.Titles ?? []).map((t) => t.Slug)));
  const nextTitle = $derived(view?.NextTitle && !ownedSlugs.has(view.NextTitle.Slug) ? view.NextTitle : null);

  function oddsLabel(odds: number): string {
    return odds < 0 ? '???' : `${Math.round(odds * 100)}%`;
  }
</script>

<!-- Modul: RENDERED WITH {@render}, NOT AS A COMPONENT TAG.
     The first version of this wrote <Diamond /> - component syntax for a
     SNIPPET - and svelte-check passed it while the whole screen threw at
     runtime, so the Delve rendered nothing at all and exercise.mjs timed out
     hunting for a door. Same shape as the `derived` shadowing trap in
     CLAUDE.md: legal-looking, type-checked, and only the browser knows. -->
{#snippet Diamond()}
  <svg class="gem" viewBox="0 0 12 12" aria-hidden="true">
    <path d="M6 1 L11 6 L6 11 L1 6 Z" fill="currentColor" />
  </svg>
{/snippet}

<div class="delve underground">
  <header class="intro">
    <h1>The Delve</h1>
    <p class="blurb">
      Pay at the gate, go down, and choose a door on every floor. Climb out to bank diamonds; lose the
      last lantern charge and you carry nothing out.
    </p>
  </header>

  {#if loadError}
    <p class="error">{loadError}</p>
  {:else if !view}
    <p class="muted">Reading the gate&hellip;</p>
  {:else if teaser}
    <section class="gate teaser" data-testid="delve-teaser">
      <h2>The gate is shut to you, for now</h2>
      <p>
        Opens at <strong><Money amount={view.EntryFeeForNextRun} /></strong> &mdash; you have
        <strong><Money amount={view.CurrentGold} /></strong>.
      </p>
      <div class="saving" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow={Math.round(saved * 100)}>
        <span style="width: {saved * 100}%"></span>
      </div>
      <p class="muted small">
        The price is about forty minutes of what you earn in region {view.HighestRegionReached}, so it
        grows as you do. A run pays back in diamonds.
      </p>
      <button class="secondary" onclick={() => (showFullWhilePoor = true)}>How the Delve works</button>
    </section>
  {:else}
    <!-- Modul: THE GATE FIRST (task 105). "Pay and descend" was the seventh
         block, about 865px down a phone, under a six-card ledger and two
         banners. The decision a player came here to make leads; the numbers
         that inform it sit inside it, and the records fold to one line below. -->
    {#if !view.Active}
      <section class="gate">
        <div class="gate-head">
          <h2>The gate</h2>
          <span class="gate-cost">A run costs <strong><Money amount={view.EntryFeeForNextRun} /></strong></span>
        </div>
        <button class="primary" data-guide="delve-start" disabled={busy || !canAfford} onclick={() => act(startDelve)}>
          {canAfford ? 'Pay and descend' : 'Not enough gold'}
        </button>
        <p class="muted small">
          You have <Money amount={view.CurrentGold} />. The price is about forty minutes of what you
          earn in region {view.HighestRegionReached}. Eight floors, three doors on each, and each door
          wants one attribute; Fortune decides how often a door tells you which.
        </p>
      </section>
    {:else}
      <section class="run">
        <div class="runhead">
          <div class="floor">
            <span class="k">Floor</span>
            <span class="v">{isDeep ? `${view.CurrentFloor} - the Deep` : `${Math.min(view.CurrentFloor, 8)} / 8`}</span>
          </div>
          <div class="charges" aria-label="{view.ChargesRemaining} lantern charges left">
            {#each Array(3) as _, i}
              <span class="charge" class:spent={i >= view.ChargesRemaining} aria-hidden="true">
                <svg viewBox="0 0 12 12"><circle cx="6" cy="6" r="4.5" fill="currentColor" /></svg>
              </span>
            {/each}
          </div>
          {#if isDeep}
            <div class="banked">
              <span class="k">Stake</span>
              <span class="v"><Money amount={view.StakeGold} /></span>
            </div>
          {:else}
            <div class="banked">
              <span class="k">Banked</span>
              <span class="v nowrap">{view.DiamondsAfterCeiling}{@render Diamond()}</span>
            </div>
          {/if}
        </div>

        {#if lanternOut}
          <!-- Modul: the light going out in the Deep is an offer, not an end:
               the run waits here until the player pays or walks out. -->
          {#if view.LanternPrice > 0}
            <p class="bottom">Your lantern is out. Light another: <Money amount={view.LanternPrice} />.</p>
            <button
              class="primary"
              disabled={busy || view.CurrentGold < view.LanternPrice}
              onclick={() => act(lightLantern)}
            >
              {view.CurrentGold < view.LanternPrice
                ? `Not enough gold for a lantern: ${formatNumber(view.LanternPrice)}`
                : `Light another lantern: ${formatNumber(view.LanternPrice)}`}
            </button>
            <p class="muted small">
              Each lantern costs twice the last. {view.LanternRefillsLeft} left on this run.
            </p>
          {:else}
            <p class="bottom">Your lantern is out, and there are none left to buy. Walk out.</p>
          {/if}
        {:else if atBottom && isDeep}
          <p class="bottom">Floor {view.CurrentFloor} is behind you. The water below is darker.</p>
        {:else if atBottom && view.CanDescend}
          <p class="bottom">The floor of the Delve. Below floor 8 lies the Deep: one diamond for each floor that is your new deepest of the week, and nothing else but how far you went.</p>
        {:else if atBottom}
          <p class="bottom">You are standing on the floor of the world. There is nothing below.</p>
        {:else}
          <div class="doors">
            {#each view.DoorDemands as demand, i}
              <button
                class="door"
                class:hidden={demand < 0}
                disabled={busy}
                onclick={() => {
                  play('delveDoor');
                  void act(() => chooseDelveDoor(i));
                }}
              >
                <span class="flavour">{demand < 0 ? HIDDEN_FLAVOUR : DOOR_FLAVOUR[demand]}</span>
                <span class="demand">{demand < 0 ? 'Unknown' : ATTRIBUTE_NAMES[demand]}</span>
                <span class="odds">{oddsLabel(view.DoorOdds[i])}</span>
              </button>
            {/each}
          </div>
        {/if}

        <div class="decision">
          {#if atBottom && view.CanDescend}
            <!-- Modul: the toll is IN the label because it is the decision; the
                 stake it is priced from is stated beside it. Nothing that ticks
                 sits inside this button - the quote only changes when the view
                 is re-read, never on a timer. -->
            <button
              class="primary descend"
              disabled={busy || view.CurrentGold + view.ConsolationGoldIfCapped < view.DescendQuote}
              onclick={() => act(() => descendDeep(view!.StakeGold))}
            >
              {isDeep ? `Descend to floor ${view.NextDeepFloor}` : 'Descend into the Deep'}: toll {formatNumber(view.DescendQuote)}
            </button>
            <p class="muted small">
              {#if isDeep}
                Each floor down tolls a quarter more than the last. Your stake of
                <Money amount={view.StakeGold} /> was fixed when you entered the Deep.
              {:else}
                Descending banks floors 1-8 first, exactly as climbing out would. The stake is half a
                percent of the most gold you have held this week (at least the gate's price), and it is
                fixed for the rest of the run.
              {/if}
            </p>
          {/if}
          <button class="secondary" disabled={busy} onclick={() => act(bankDelve)}>
            {#if isDeep}
              Walk out
            {:else}
              Climb out with {view.DiamondsAfterCeiling}{@render Diamond()}{view.ConsolationGoldIfCapped > 0
                ? ` + ${formatNumber(view.ConsolationGoldIfCapped)} gold`
                : ''}
            {/if}
          </button>
          {#if isDeep && !atBottom}
            <p class="muted small">
              Three failures put your lantern out. You can light another, at twice the last price,
              or walk out. Your record stands; only the gold is spent.
            </p>
          {:else if !atBottom}
            <p class="muted small">
              Clearing floor {Math.min(view.CurrentFloor, 8)} would make it
              <strong class="nowrap">{view.DiamondsIfNextFloorCleared}{@render Diamond()}</strong>. Three failures and you
              carry nothing out.
            </p>
          {/if}
        </div>
      </section>
    {/if}

    {#if notice}
      <p class="notice" role="status">{notice}</p>
    {/if}

    <!-- The week, in one place: the diamond ceiling and when it lifts. The
         ceiling is STATED, not discovered - a player who earns nothing and is
         not told why concludes the feature is broken. -->
    <p class="week" class:capped={ceilingLeft === 0}>
      <span class="nowrap">Diamonds this week <strong>{view.DiamondsEarnedThisWeek}/{view.WeeklyDiamondCeiling}</strong></span>
      {#if ceilingLeft === 0}
        <span>- cap reached; a run still pays gold back from the gate.</span>
      {/if}
      <span class="nowrap muted">Resets {resetsAt}.</span>
    </p>

    {#if view.DeepEnabled}
      <!-- Task 61: the Deep's floors come from a seed per ISO week, the same
           for everyone - so the number to beat is your own last week. Naming
           (task 105): "the Delve" is the place, and the Deep is introduced
           here, once, as what lies below floor 8. -->
      <p class="deep-line muted small" data-testid="deep-weekly-course">
        Below floor 8 lies the Deep. This week it is the same course for everyone until {resetsAt};
        whether a door opens is still up to you.
      </p>

      <section class="records">
        <button
          type="button"
          class="records-toggle"
          aria-expanded={recordsOpen}
          onclick={() => (recordsOpen = !recordsOpen)}
        >
          <span class="k">Records</span>
          <span class="records-line">
            best floor <strong>{view.DeepestFloor}</strong> · this week <strong>{view.DeepestThisWeek}</strong>
            · last week <strong>{view.DeepestLastWeek > 0 ? view.DeepestLastWeek : 'none'}</strong>
          </span>
          <span class="chev" aria-hidden="true">{recordsOpen ? '▴' : '▾'}</span>
        </button>
        {#if recordsOpen}
          <p class="muted small records-more">
            {#if view.DeepestLastWeek > 0}
              {view.DeepestThisWeek > view.DeepestLastWeek
                ? `You are past last week's floor ${view.DeepestLastWeek}.`
                : `Last week you reached floor ${view.DeepestLastWeek}; that is the number to beat.`}
            {:else}
              No record last week, so anything this week is a first.
            {/if}
            The deepest region you have reached is {view.HighestRegionReached}, which is what prices the gate.
          </p>
        {/if}
      </section>

      <section class="sheet titles">
        <h2>Titles</h2>
        {#if titles && titles.Titles.length > 0}
          <p class="picker-label" id="wear-title-label">Wear a title</p>
          <div class="picker" role="group" aria-labelledby="wear-title-label">
            {#each titles.Titles as t (t.Slug)}
              {@const isWorn = titles.Active?.Slug === t.Slug}
              <button
                class="secondary"
                class:worn={isWorn}
                aria-pressed={isWorn}
                disabled={busy}
                onclick={() => wear(t.Slug)}
              >
                {t.Name}{#if isWorn}<span class="worn-mark"> · Worn</span>{/if}
              </button>
            {/each}
            {#if titles.Active}
              <button class="secondary" disabled={busy} onclick={() => wear(null)}>No title</button>
            {/if}
          </div>
        {:else}
          <p class="muted small">No titles yet. The Deep pays in these, and in one diamond for each new weekly-deepest floor.</p>
        {/if}
        {#if nextTitle}
          <p class="muted small">
            Next: clear floor {nextTitle.Floor} of the Deep to earn <strong>{nextTitle.Name}</strong>.
          </p>
        {/if}
        {#if titleNotice}
          <p class="notice" role="status">{titleNotice}</p>
        {/if}
      </section>
    {/if}

    <section class="sheet">
      <h2>What you are taking down there</h2>
      <ul class="stats">
        {#each view.Attributes as value, i}
          <li><span class="k">{ATTRIBUTE_NAMES[i]}</span><span class="v">{value}</span></li>
        {/each}
      </ul>
      <p class="muted small">
        Each floor asks more than the last. Depth is bought with the breadth of your sheet: a door
        you cannot answer is still a gamble, never a wall.
      </p>
    </section>
  {/if}
</div>

<style>
  /* Every colour here is a token: the --ug-* palette in app.css, re-pointed
     for this subtree by `.underground`. No literals (tests/worldBossDelveUi.test.ts). */
  .delve {
    padding: 16px;
    max-width: 720px;
    margin: 0 auto;
    color: var(--ug-text);
  }

  h1 {
    margin: 0 0 4px;
    font-size: 1.4rem;
    letter-spacing: 0.02em;
    color: var(--ug-text);
  }

  h2 {
    margin: 0;
    font-size: 1rem;
    color: var(--ug-lamp);
  }

  /* The global `header` rule colours a header's text for the app bar; this
     one is a page intro on stone, so it says its own colours. */
  .intro {
    background: none;
    border: 0;
    padding: 0;
    color: var(--ug-text);
  }

  .blurb {
    margin: 0 0 14px;
    line-height: 1.45;
    color: var(--ug-text-soft);
  }

  .muted {
    color: var(--ug-text-dim);
  }

  .small {
    font-size: 0.85rem;
    line-height: 1.4;
  }

  .nowrap {
    white-space: nowrap;
  }

  .error {
    color: var(--ug-error);
  }

  .k {
    color: var(--ug-text-dim);
  }

  .v {
    font-variant-numeric: tabular-nums;
    font-weight: 600;
  }

  .gate,
  .run,
  .sheet,
  .records {
    background: var(--ug-panel);
    border: 1px solid var(--ug-border);
    border-radius: 8px;
    padding: 14px;
    margin-bottom: 14px;
  }

  .gate {
    display: grid;
    gap: 10px;
  }

  .gate p {
    margin: 0;
  }

  .gate-head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 8px;
    flex-wrap: wrap;
  }

  .gate-cost {
    color: var(--ug-text-soft);
  }

  .saving {
    height: 8px;
    border-radius: 4px;
    background: var(--ug-track);
    overflow: hidden;
    margin: 8px 0 10px;
  }

  .saving span {
    display: block;
    height: 100%;
    background: var(--ug-flame);
  }

  .week {
    display: flex;
    flex-wrap: wrap;
    gap: 4px 8px;
    align-items: baseline;
    margin: 0 0 10px;
    padding: 10px 12px;
    background: var(--ug-sunken);
    border: 1px solid var(--ug-border);
    border-radius: 6px;
    line-height: 1.45;
  }

  .week.capped {
    background: var(--ug-callout);
    border-color: var(--ug-callout-edge);
  }

  .deep-line {
    margin: 0 0 10px;
  }

  .records {
    padding: 0;
  }

  .records-toggle {
    display: flex;
    align-items: center;
    gap: 10px;
    width: 100%;
    min-height: 44px;
    padding: 10px 14px;
    background: none;
    border: 0;
    box-shadow: none;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
  }

  .records-line {
    flex: 1 1 auto;
    min-width: 0;
  }

  .chev {
    color: var(--ug-text-dim);
  }

  .records-more {
    margin: 0;
    padding: 0 14px 12px;
  }

  .runhead {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    flex-wrap: wrap;
    margin-bottom: 12px;
  }

  .floor,
  .banked {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .charge {
    color: var(--ug-flame);
    display: inline-flex;
  }

  .charge svg {
    width: 14px;
    height: 14px;
  }

  .charge.spent {
    color: var(--ug-flame-out);
  }

  .gem {
    width: 0.72em;
    height: 0.72em;
    margin-left: 0.15em;
    vertical-align: -0.02em;
  }

  /* Modul: a COLUMN at narrow widths. Three doors side by side is the shape
     check:clipping catches at 390px - the odds are the first thing to fall off
     the edge, and they are the whole decision. */
  .doors {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    gap: 10px;
  }

  .door {
    display: flex;
    flex-direction: column;
    gap: 6px;
    text-align: left;
    background: var(--ug-raised);
    border: 1px solid var(--ug-border-strong);
    border-radius: 8px;
    padding: 12px;
    color: inherit;
    cursor: pointer;
    font: inherit;
  }

  @media (hover: hover) and (pointer: fine) {
    .door:hover:not(:disabled) {
      border-color: var(--ug-lamp);
    }
  }

  .door:disabled {
    opacity: 0.6;
    cursor: default;
  }

  .door.hidden {
    border-style: dashed;
  }

  .flavour {
    color: var(--ug-text-soft);
    line-height: 1.35;
  }

  .demand {
    color: var(--ug-lamp);
    font-weight: 600;
  }

  .odds {
    font-variant-numeric: tabular-nums;
    font-size: 1.2rem;
    font-weight: 700;
  }

  .decision {
    margin-top: 14px;
    display: flex;
    flex-direction: column;
    gap: 8px;
  }

  .bottom {
    color: var(--ug-lamp);
    line-height: 1.45;
  }

  button.primary,
  button.secondary {
    font: inherit;
    padding: 10px 14px;
    border-radius: 6px;
    cursor: pointer;
    min-height: 44px;
    flex-shrink: 0;
  }

  /* `background`, not `background-color`: the shorthand also clears the
     global button gradient, which on the parchment theme is a white sheen. */
  button.primary {
    background: var(--ug-lamp);
    border: 1px solid var(--ug-lamp);
    color: var(--ug-on-lamp);
    font-weight: 700;
  }

  button.secondary {
    background: var(--ug-raised);
    border: 1px solid var(--ug-edge);
    color: var(--ug-text);
  }

  button:disabled {
    opacity: 0.55;
    cursor: default;
  }

  .notice {
    background: var(--ug-sunken);
    border-left: 3px solid var(--ug-lamp);
    padding: 10px 12px;
    margin: 0 0 14px;
    line-height: 1.45;
  }

  .titles {
    display: grid;
    gap: 8px;
  }

  .titles p {
    margin: 0;
  }

  .picker-label {
    font-size: 0.8rem;
    color: var(--ug-text-soft);
  }

  .picker {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }

  button.worn {
    border-color: var(--ug-lamp);
    color: var(--ug-lamp);
    font-weight: 700;
  }

  .worn-mark {
    font-weight: 400;
    font-size: 0.85em;
  }

  /* Four attributes: 2x2 on a phone, one row of four when there is room -
     never the 4+2 ledger grid the old auto-fit produced. */
  .stats {
    list-style: none;
    margin: 10px 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 8px;
  }

  @media (min-width: 40rem) {
    .stats {
      grid-template-columns: repeat(4, minmax(0, 1fr));
    }
  }

  .stats li {
    display: flex;
    justify-content: space-between;
    gap: 8px;
    background: var(--ug-sunken);
    border: 1px solid var(--ug-border);
    border-radius: 6px;
    padding: 8px 10px;
  }

  .sheet p {
    margin: 0;
  }
</style>
