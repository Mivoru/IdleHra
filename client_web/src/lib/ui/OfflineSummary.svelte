<script lang="ts">
  import { offlineSummary, dismissOfflineSummary, playerState } from '../stores/game';
  import RaceIcon from './RaceIcon.svelte';
  import { raceName } from './races';
  import { HALT_REASONS } from './slots';

  function duration(seconds: number): string {
    if (seconds < 60) return `${seconds} seconds`;
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    if (hours === 0) return `${minutes} minute${minutes === 1 ? '' : 's'}`;
    if (minutes === 0) return `${hours} hour${hours === 1 ? '' : 's'}`;
    return `${hours}h ${minutes}m`;
  }

  const summary = $derived($offlineSummary);
  const snap = $derived($playerState);

  // Modul: A RATE, not just a total.
  //
  // "You earned 240,000 gold" is a receipt. "That is 15,000 an hour" is the
  // only number that tells a player whether the setup they left running was a
  // good one - and in an idle game, deciding what to leave running IS the game.
  // Both come free from what the card already has.
  const perHour = $derived.by(() => {
    if (!summary || summary.elapsedSeconds < 60) return null;
    const hours = summary.elapsedSeconds / 3600;
    return {
      gold: Math.round(summary.goldEarned / hours),
      xp: Math.round(summary.xpEarned / hours),
    };
  });

  // Modul: AND WHAT IS HAPPENING NOW.
  //
  // The card described the past and said nothing about the present, so a player
  // whose run had stopped an hour in - out of food, or killed - read a healthy
  // total, closed the card and left the same broken setup running again. The
  // most useful thing a morning screen can do is say what is wrong RIGHT NOW.
  const rightNow = $derived.by(() => {
    if (!snap) return null;

    const halt = HALT_REASONS[snap.ActivityHaltReason] ?? '';
    const larderBites =
      Number(snap.Food1_Count ?? 0) + Number(snap.Food2_Count ?? 0) + Number(snap.Food3_Count ?? 0);
    const deployed = Number(snap.ActiveActivityId ?? 0) > 0;

    return { halt, larderBites, deployed };
  });
</script>

{#if summary}
  <div
    class="backdrop"
    role="button"
    tabindex="0"
    onclick={dismissOfflineSummary}
    onkeydown={(e) => (e.key === 'Escape' || e.key === 'Enter') && dismissOfflineSummary()}
  >
    <div class="card" role="dialog" aria-label="Welcome back">
      <h2>Welcome back</h2>
      <p class="lead">You were away for {duration(summary.elapsedSeconds)}.</p>

      {#if summary.earnedNothing}
        <!-- Not a rewards panel. The player earned nothing because no
             character was deployed, and in an idle game that is the single
             most useful thing to tell them - saying it plainly beats showing
             three zeroes dressed up as a reward. -->
        <p class="idle">
          Your character wasn't doing anything, so none of that time earned
          progress. Pick a monster and press <strong>Fight</strong> before you
          go - the fight continues while you are away.
        </p>
      {:else}
        <dl>
          <div><dt>Gold</dt><dd>+{summary.goldEarned.toLocaleString()}</dd></div>
          <div><dt>XP</dt><dd>+{summary.xpEarned.toLocaleString()}</dd></div>
          <div><dt>Materials</dt><dd>+{summary.materialDropsGranted.toLocaleString()}</dd></div>
        </dl>

        {#if perHour}
          <p class="rate dim small">
            That is <strong>{perHour.gold.toLocaleString()} gold</strong> and
            <strong>{perHour.xp.toLocaleString()} XP</strong> an hour. Leave a
            harder monster running and this goes up - leave one that kills you
            and it goes to nothing.
          </p>
        {/if}

        <!-- Per character, because the household has up to three workers and
             one aggregate number cannot tell you that two of them stood still.
             A row of zeroes is the point, not noise. -->
        {#if summary.perCharacter.length > 1}
          <table class="crew">
            <thead>
              <tr><th>Character</th><th>Gold</th><th>XP</th><th>Drops</th></tr>
            </thead>
            <tbody>
              {#each summary.perCharacter as worker (worker.slot)}
                <tr class:idle={worker.gold === 0 && worker.xp === 0 && worker.drops === 0}>
                  <td class="who">
                    <RaceIcon raceId={worker.raceId} />
                    <span>Slot {worker.slot}</span>
                    <span class="dim">{raceName(worker.raceId)}</span>
                  </td>
                  <td>{worker.gold.toLocaleString()}</td>
                  <td>{worker.xp.toLocaleString()}</td>
                  <td>{worker.drops.toLocaleString()}</td>
                </tr>
              {/each}
            </tbody>
          </table>

          {#if summary.perCharacter.some((w) => w.gold === 0 && w.xp === 0 && w.drops === 0)}
            <p class="idle">
              A character on zero was not assigned to anything. Give them a job
              on the <strong>Character</strong> screen.
            </p>
          {/if}
        {/if}
      {/if}

      <!-- Modul: what is true NOW, before the card is dismissed. Everything
           above is history; this is the part that changes what the player does
           in the next ten seconds. -->
      {#if rightNow}
        <div class="briefing">
          {#if rightNow.halt}
            <p class="warn-line">{rightNow.halt}</p>
          {:else if !rightNow.deployed}
            <p class="warn-line">
              Nothing is running now. Whatever you leave deployed keeps going
              while you are away - an empty slot earns nothing.
            </p>
          {/if}

          {#if rightNow.larderBites === 0}
            <p class="warn-line">
              Your larder is empty, so you are fighting without healing. Fish,
              then load food into Auto-Eat before you close the page.
            </p>
          {:else}
            <p class="dim tiny">
              Larder: {rightNow.larderBites.toLocaleString()} bites left.
            </p>
          {/if}
        </div>
      {/if}

      <button onclick={dismissOfflineSummary}>Continue</button>
    </div>
  </div>
{/if}

<style>
  .rate {
    margin: 0.2rem 0 0.4rem;
  }

  .briefing {
    display: grid;
    gap: 0.3rem;
    margin: 0.5rem 0;
    padding: 0.5rem 0.6rem;
    border-left: 2px solid var(--brass);
    background: rgba(201, 162, 39, 0.06);
  }

  .warn-line {
    margin: 0;
    color: var(--warn);
    font-size: 0.85rem;
  }

  .crew {
    width: 100%;
    border-collapse: collapse;
    margin: 0.6rem 0 0.2rem;
    font-size: 0.85rem;
  }

  .crew th {
    text-align: right;
    font-weight: 600;
    opacity: 0.6;
    padding: 0.2rem 0.3rem;
  }

  .crew th:first-child {
    text-align: left;
  }

  .crew td {
    text-align: right;
    padding: 0.25rem 0.3rem;
    border-top: 1px solid rgba(255, 255, 255, 0.08);
    font-variant-numeric: tabular-nums;
  }

  .crew td.who {
    text-align: left;
    display: flex;
    align-items: center;
    gap: 0.3rem;
  }

  .crew tr.idle td {
    opacity: 0.55;
  }

  .backdrop {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.6);
    display: grid;
    place-items: center;
    z-index: 50;
    border: 0;
    padding: 1rem;
  }

  .card {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1.5rem;
    min-width: min(22rem, 90vw);
    text-align: center;
  }

  h2 {
    margin: 0 0 0.25rem;
  }

  .lead {
    margin: 0 0 1rem;
    color: var(--text-dim);
  }

  dl {
    display: grid;
    gap: 0.4rem;
    margin: 0 0 1.25rem;
  }

  dl div {
    display: flex;
    justify-content: space-between;
    gap: 1.5rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.3rem;
  }

  dt {
    color: var(--text-dim);
  }

  .idle {
    margin: 0 0 1.25rem;
    text-align: left;
    line-height: 1.5;
  }

  dd {
    margin: 0;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
    color: var(--good);
  }
</style>
