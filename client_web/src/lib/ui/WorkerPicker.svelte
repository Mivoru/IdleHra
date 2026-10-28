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
  import { kindOfWork } from './slots';

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

  // Modul: WHAT EACH PERSON IS DOING, AS A COLOUR (owner, 2026-10-28). The
  // three cards were grey boxes of grey text; the dot beside the job now
  // carries the section colour of the work (sections.ts): ember for a fight,
  // moss for gathering, river for the workbench, dim for idle. A halted job
  // keeps its colour - the job line already says why it stopped.
  function workTone(w: Worker): string {
    const kind = kindOfWork(w.activity);
    if (kind === 0) return 'idle';
    if (kind === 1 || kind === 7) return 'combat';
    if (kind === 6) return 'make';
    return 'gather';
  }
</script>

{#if workers.length === 1}
  <p class="solo" data-testid="worker-solo">
    <span class="who">{nameOf(workers[0])}</span>
    <span class="job dim">{describe(workers[0])}</span>
  </p>
{:else if workers.length > 1}
  <div class="picker" role="radiogroup" aria-label={label} data-testid="worker-picker" style="--n: {Math.min(3, workers.length)}">
    {#each workers as w (w.slot)}
      <button
        type="button"
        role="radio"
        aria-checked={w.slot === selected}
        class="person"
        class:on={w.slot === selected}
        title={nameOf(w)}
        onclick={() => onpick(w.slot)}
      >
        <!-- Name only on a card: three across a phone left "Cadoc (Hum...".
             The race is in the title, and on the Character screen. -->
        <span class="who">{workerName(w, names)}</span>
        <span class="job"><span class="dot tone-{workTone(w)}" aria-hidden="true"></span>{describe(w)}</span>
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

  /* Always one row of equal cards (owner: two-and-one on a phone looked
     unfinished); the name and job shorten with an ellipsis instead. */
  .picker {
    display: grid;
    grid-template-columns: repeat(var(--n, 3), minmax(0, 1fr));
    gap: 0.35rem;
    margin: 0 0 0.6rem;
  }

  .person {
    display: grid;
    gap: 0.15rem;
    min-width: 0;
    text-align: left;
    padding: 0.4rem 0.5rem;
    background-color: var(--bg);
    background-image: linear-gradient(180deg, rgba(255, 255, 255, 0.04), rgba(0, 0, 0, 0.12));
    box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.04);
    border: 1px solid var(--border);
  }

  .person.on {
    border-color: var(--section);
    background-color: var(--bg-raised);
    background-image: linear-gradient(
      180deg,
      color-mix(in srgb, var(--section) 26%, transparent),
      color-mix(in srgb, var(--section) 6%, transparent)
    );
    box-shadow:
      inset 0 1px 0 rgba(255, 255, 255, 0.08),
      0 0 0 1px color-mix(in srgb, var(--section) 45%, transparent),
      0 2px 8px color-mix(in srgb, var(--section) 25%, transparent);
  }

  .who {
    font-weight: 700;
    font-size: 0.82rem;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .job {
    display: flex;
    align-items: center;
    gap: 0.3rem;
    font-size: 0.72rem;
    color: var(--text-dim);
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .dot {
    flex: none;
    width: 0.5rem;
    height: 0.5rem;
    border-radius: 50%;
    background: var(--text-dim);
    opacity: 0.6;
  }

  .dot.tone-combat {
    background: var(--sec-combat);
    opacity: 1;
    box-shadow: 0 0 5px var(--sec-combat);
  }

  .dot.tone-gather {
    background: var(--sec-gather);
    opacity: 1;
    box-shadow: 0 0 5px var(--sec-gather);
  }

  .dot.tone-make {
    background: var(--sec-make);
    opacity: 1;
    box-shadow: 0 0 5px var(--sec-make);
  }
</style>
