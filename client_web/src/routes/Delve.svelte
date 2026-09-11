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
  */
  import { onMount } from 'svelte';
  import Money from '../lib/ui/Money.svelte';
  import { formatCompact } from '../lib/ui/format';

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

  async function load() {
    try {
      view = await fetchDelve();
      loadError = '';
    } catch (err) {
      loadError = err instanceof Error ? err.message : 'could not reach the Delve';
    }
  }

  onMount(load);

  function describe(outcome: DelveActionResponse): string {
    switch (outcome.Result) {
      case 'NotEnoughGold':
        return 'Not enough gold for the gate.';
      case 'RunAlreadyInProgress':
        return 'You are already down there.';
      case 'NoRunInProgress':
        return 'There is no run to do that to.';
      case 'InvalidDoor':
        return 'That door is not on this floor.';
      case 'RunLost':
        return 'The last lantern charge goes out. You come up with nothing.';
      case 'ChargeLost':
        return 'The way is barred. A lantern charge burns out, and the floor offers new doors.';
      case 'FloorCleared':
        return 'Through. The stair keeps going down.';
      case 'AtTheBottom':
        return 'The bottom. There is nothing below this - take what you have and climb.';
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
        if (outcome.Result === 'Ok' && (outcome.DiamondsGranted > 0 || outcome.GoldReturned > 0)) {
          const parts: string[] = [];
          if (outcome.DiamondsGranted > 0) parts.push(`${outcome.DiamondsGranted} diamonds`);
          if (outcome.GoldReturned > 0) parts.push(`${formatCompact(outcome.GoldReturned)} gold`);
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

  const canAfford = $derived(!!view && view.CurrentGold >= view.EntryFeeForNextRun);
  const ceilingLeft = $derived(view ? Math.max(0, view.WeeklyDiamondCeiling - view.DiamondsEarnedThisWeek) : 0);
  const atBottom = $derived(!!view?.Active && view.FloorsCleared >= 8);

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

<div class="delve">
  <header>
    <h1>The Delve</h1>
    <p class="blurb">
      Pay at the gate, take the stair down, and decide on every floor whether to keep going. What you
      bank converts to diamonds when you climb out. What you are carrying when the last lantern
      charge goes out is lost.
    </p>
  </header>

  {#if loadError}
    <p class="error">{loadError}</p>
  {:else if !view}
    <p class="muted">Reading the gate&hellip;</p>
  {:else}
    <section class="ledger">
      <div><span class="k">Your gold</span><span class="v"><Money amount={view.CurrentGold} /></span></div>
      <div><span class="k">Diamonds this week</span><span class="v">{view.DiamondsEarnedThisWeek} / {view.WeeklyDiamondCeiling}</span></div>
      <div><span class="k">Deepest region reached</span><span class="v">{view.HighestRegionReached}</span></div>
    </section>

    {#if ceilingLeft === 0}
      <!-- Modul: the ceiling is STATED, not discovered. A player who earns
           nothing and is not told why concludes the feature is broken, which
           is exactly the report that produced half this codebase's rules. -->
      <p class="capped">
        You have taken this week's {view.WeeklyDiamondCeiling} diamonds. A run still pays gold back
        from the gate, and the ceiling lifts on Monday.
      </p>
    {/if}

    {#if !view.Active}
      <section class="gate">
        <h2>The gate</h2>
        <p>
          A run costs <strong><Money amount={view.EntryFeeForNextRun} /></strong> &mdash; about
          forty minutes of what you earn in region {view.HighestRegionReached}.
        </p>
        <p class="muted small">
          Eight floors. Three doors on each, and each door wants one attribute. Fortune decides how
          often a door tells you which.
        </p>
        <button class="primary" disabled={busy || !canAfford} onclick={() => act(startDelve)}>
          {canAfford ? 'Pay and descend' : 'Not enough gold'}
        </button>
      </section>
    {:else}
      <section class="run">
        <div class="runhead">
          <div class="floor">
            <span class="k">Floor</span>
            <span class="v">{Math.min(view.CurrentFloor, 8)} / 8</span>
          </div>
          <div class="charges" aria-label="{view.ChargesRemaining} lantern charges left">
            {#each Array(3) as _, i}
              <span class="charge" class:spent={i >= view.ChargesRemaining} aria-hidden="true">
                <svg viewBox="0 0 12 12"><circle cx="6" cy="6" r="4.5" fill="currentColor" /></svg>
              </span>
            {/each}
          </div>
          <div class="banked">
            <span class="k">Banked</span>
            <span class="v">{view.DiamondsAfterCeiling}{@render Diamond()}</span>
          </div>
        </div>

        {#if atBottom}
          <p class="bottom">You are standing on the floor of the world. There is nothing below.</p>
        {:else}
          <div class="doors">
            {#each view.DoorDemands as demand, i}
              <button
                class="door"
                class:hidden={demand < 0}
                disabled={busy}
                onclick={() => act(() => chooseDelveDoor(i))}
              >
                <span class="flavour">{demand < 0 ? HIDDEN_FLAVOUR : DOOR_FLAVOUR[demand]}</span>
                <span class="demand">{demand < 0 ? 'Unknown' : ATTRIBUTE_NAMES[demand]}</span>
                <span class="odds">{oddsLabel(view.DoorOdds[i])}</span>
              </button>
            {/each}
          </div>
        {/if}

        <div class="decision">
          <button class="secondary" disabled={busy} onclick={() => act(bankDelve)}>
            Climb out with {view.DiamondsAfterCeiling}{@render Diamond()}{view.ConsolationGoldIfCapped > 0
              ? ` + ${formatCompact(view.ConsolationGoldIfCapped)} gold`
              : ''}
          </button>
          {#if !atBottom}
            <p class="muted small">
              Clearing floor {Math.min(view.CurrentFloor, 8)} would make it
              <strong>{view.DiamondsIfNextFloorCleared}{@render Diamond()}</strong>. Three failures and you
              carry nothing out.
            </p>
          {/if}
        </div>
      </section>
    {/if}

    {#if notice}
      <p class="notice" role="status">{notice}</p>
    {/if}

    <section class="sheet">
      <h2>What you are taking down there</h2>
      <ul>
        {#each view.Attributes as value, i}
          <li><span class="k">{ATTRIBUTE_NAMES[i]}</span><span class="v">{value}</span></li>
        {/each}
      </ul>
      <p class="muted small">
        Each floor asks more than the last. Depth is bought with the BREADTH of your sheet: a door
        you cannot answer is still a gamble, never a wall.
      </p>
    </section>
  {/if}
</div>

<style>
  .delve {
    padding: 16px;
    max-width: 720px;
    margin: 0 auto;
    color: #e8e2d4;
  }

  h1 {
    margin: 0 0 4px;
    font-size: 1.4rem;
    letter-spacing: 0.02em;
  }

  h2 {
    margin: 0 0 8px;
    font-size: 1rem;
    color: #d9c48b;
  }

  .blurb {
    margin: 0 0 16px;
    line-height: 1.45;
    color: #b6ae9c;
  }

  .muted {
    color: #938b7a;
  }

  .small {
    font-size: 0.85rem;
    line-height: 1.4;
  }

  .error {
    color: #e08a7a;
  }

  .ledger {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
    gap: 8px;
    margin-bottom: 16px;
  }

  .ledger div,
  .sheet li {
    display: flex;
    justify-content: space-between;
    gap: 8px;
    background: #241f1a;
    border: 1px solid #3a322a;
    border-radius: 6px;
    padding: 8px 10px;
  }

  .k {
    color: #938b7a;
  }

  .v {
    font-variant-numeric: tabular-nums;
    font-weight: 600;
  }

  .capped {
    background: #2b2418;
    border: 1px solid #5c4a22;
    border-radius: 6px;
    padding: 10px 12px;
    margin-bottom: 16px;
    line-height: 1.45;
  }

  .gate,
  .run,
  .sheet {
    background: #1e1a16;
    border: 1px solid #3a322a;
    border-radius: 8px;
    padding: 14px;
    margin-bottom: 16px;
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
    color: #e0b74a;
    display: inline-flex;
  }

  .charge svg {
    width: 14px;
    height: 14px;
  }

  .gem {
    width: 0.72em;
    height: 0.72em;
    margin-left: 0.28em;
    vertical-align: -0.02em;
  }

  .charge.spent {
    color: #4a4038;
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
    background: #2a231c;
    border: 1px solid #4a3f33;
    border-radius: 8px;
    padding: 12px;
    color: inherit;
    cursor: pointer;
    font: inherit;
  }

  .door:hover:not(:disabled) {
    border-color: #d9c48b;
  }

  .door:disabled {
    opacity: 0.6;
    cursor: default;
  }

  .door.hidden {
    border-style: dashed;
  }

  .flavour {
    color: #b6ae9c;
    line-height: 1.35;
  }

  .demand {
    color: #d9c48b;
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
    color: #d9c48b;
    line-height: 1.45;
  }

  button.primary,
  button.secondary {
    font: inherit;
    padding: 10px 14px;
    border-radius: 6px;
    cursor: pointer;
  }

  button.primary {
    background: #d9c48b;
    border: 1px solid #d9c48b;
    color: #1b1712;
    font-weight: 700;
  }

  button.secondary {
    background: #2a231c;
    border: 1px solid #6b5a3f;
    color: #e8e2d4;
  }

  button:disabled {
    opacity: 0.55;
    cursor: default;
  }

  .notice {
    background: #241f1a;
    border-left: 3px solid #d9c48b;
    padding: 10px 12px;
    margin: 0 0 16px;
    line-height: 1.45;
  }

  .sheet ul {
    list-style: none;
    margin: 0 0 10px;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
    gap: 8px;
  }
</style>
