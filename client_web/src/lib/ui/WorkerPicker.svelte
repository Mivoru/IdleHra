<script lang="ts">
  // Modul: WHO DOES THE JOB, picked by name (task 101) - shared by Gathering
  // and Crafting so the two cannot drift.
  //
  // Buttons, NOT a <select>: the job line under each name changes while the
  // player looks at it (a halt, a craft finishing), and a native select whose
  // options re-render while open is broken on Android (client_web/CLAUDE.md,
  // the breeding pickers). With one person there is nothing to choose, so it
  // is a line of text saying who and what.
  import { workerName, type Worker } from './workers';
  import { raceName } from './races';

  interface Props {
    workers: Worker[];
    names: ReadonlyMap<string, string>;
    selected: number;
    describe: (worker: Worker) => string;
    onpick: (slot: number) => void;
    label?: string;
  }

  const { workers, names, selected, describe, onpick, label = 'Who' }: Props = $props();

  const nameOf = (w: Worker) => {
    const name = workerName(w, names);
    const race = w.raceId > 0 ? raceName(w.raceId) : '';
    return race && race !== name ? `${name} (${race})` : name;
  };
</script>

{#if workers.length === 1}
  <p class="solo" data-testid="worker-solo">
    <span class="who">{nameOf(workers[0])}</span>
    <span class="job dim">{describe(workers[0])}</span>
  </p>
{:else if workers.length > 1}
  <div class="picker" role="radiogroup" aria-label={label} data-testid="worker-picker">
    {#each workers as w (w.slot)}
      <button
        type="button"
        role="radio"
        aria-checked={w.slot === selected}
        class="person"
        class:on={w.slot === selected}
        onclick={() => onpick(w.slot)}
      >
        <span class="who">{nameOf(w)}</span>
        <span class="job">{describe(w)}</span>
      </button>
    {/each}
  </div>
{/if}

<style>
  .solo {
    display: flex;
    flex-wrap: wrap;
    gap: 0.2rem 0.5rem;
    margin: 0 0 0.5rem;
    font-size: 0.82rem;
  }

  .picker {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(9rem, 1fr));
    gap: 0.35rem;
    margin: 0 0 0.6rem;
  }

  .person {
    display: grid;
    gap: 0.1rem;
    min-width: 0;
    text-align: left;
    padding: 0.35rem 0.55rem;
    background-image: none;
    background: var(--bg);
    box-shadow: none;
    border: 1px solid var(--border);
  }

  .person.on {
    border-color: var(--brass-lit);
    background: color-mix(in srgb, var(--brass) 18%, var(--bg));
  }

  .who {
    font-weight: 600;
    font-size: 0.82rem;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .job {
    font-size: 0.72rem;
    color: var(--text-dim);
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
</style>
