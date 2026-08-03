<script lang="ts">
  import { offlineSummary, dismissOfflineSummary } from '../stores/game';
  import RaceIcon from './RaceIcon.svelte';
  import { raceName } from './races';

  function duration(seconds: number): string {
    if (seconds < 60) return `${seconds} seconds`;
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    if (hours === 0) return `${minutes} minute${minutes === 1 ? '' : 's'}`;
    if (minutes === 0) return `${hours} hour${hours === 1 ? '' : 's'}`;
    return `${hours}h ${minutes}m`;
  }

  const summary = $derived($offlineSummary);
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

      <button onclick={dismissOfflineSummary}>Continue</button>
    </div>
  </div>
{/if}

<style>
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
