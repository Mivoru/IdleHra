<script lang="ts">
  // Modul: THE LOOP, AS ONE PATH (owner 2026-10-14: "it is not clear how
  // breeding connects to rebirth").
  //
  // Breeding, the Hall of Ancestors and Rebirth are three screens, and the loop
  // that makes any of them worth doing - marry, raise the child, carry it
  // through a rebirth - was written down on none of them. This strip sits on
  // top of the Breeding screen and names the three steps with live counts, so
  // "what do I do next" has an answer and a button.
  //
  // Every count is the server's: Hall members' AgePhase, PlayableSlot and
  // WouldCarry (the cull's own verdict, never recomputed here) and the rebirth
  // preview's bloodline ceiling.
  import type { HallSnapshot } from '../net/rest';
  import { requestScreen } from '../stores/navigation';

  interface Props {
    hall: HallSnapshot | undefined;
    newcomers: number;
    ceilingNow: number | null;
    ceilingAfter: number | null;
    capped: boolean;
  }

  const { hall, newcomers, ceilingNow, ceilingAfter, capped }: Props = $props();

  /** AGE_PHASES in ui/slots.ts: 0 is Child. */
  const CHILD = 0;

  const members = $derived(hall?.Members ?? []);
  const growing = $derived(members.filter((m) => m.AgePhase === CHILD));
  const benchedChildren = $derived(growing.filter((m) => m.PlayableSlot < 0).length);
  const carried = $derived(members.filter((m) => m.WouldCarry).length);
</script>

<nav class="path panel" aria-label="The bloodline, step by step" data-testid="bloodline-path">
  <ol>
    <li class="step now">
      <span class="num" aria-hidden="true">1</span>
      <div class="body">
        <strong>Marry</strong>
        <span class="dim tiny">
          {newcomers > 0 ? `${newcomers} in the village to choose from` : 'Pick a hero and a partner below'}
        </span>
      </div>
    </li>
    <li class="step" class:todo={benchedChildren > 0}>
      <span class="num" aria-hidden="true">2</span>
      <div class="body">
        <strong>Raise</strong>
        <span class="dim tiny">
          {#if benchedChildren > 0}
            {benchedChildren} {benchedChildren === 1 ? 'child waits' : 'children wait'} to be fielded
          {:else if growing.length > 0}
            {growing.length} growing up in play
          {:else}
            Field a child and give it work; it is grown in an hour
          {/if}
        </span>
      </div>
      <button type="button" class="go" onclick={() => requestScreen('ancestors')}>Hall</button>
    </li>
    <li class="step">
      <span class="num" aria-hidden="true">3</span>
      <div class="body">
        <strong>Carry</strong>
        <span class="dim tiny">
          {#if hall}{carried} of {hall.Cap} carry through a rebirth{:else}Mark Keep, then be reborn{/if}
        </span>
      </div>
      <button type="button" class="go" onclick={() => requestScreen('rebirth')}>Rebirth</button>
    </li>
  </ol>

  {#if ceilingNow !== null}
    <p class="ceiling" data-testid="bloodline-ceiling">
      <span class="chip">Bloodline ceiling <strong>{ceilingNow}</strong></span>
      <span class="dim tiny">
        {#if capped || ceilingAfter === null || ceilingAfter <= ceilingNow}
          the highest it goes
        {:else}
          the next rebirth raises it to {ceilingAfter}
        {/if}
      </span>
    </p>
  {/if}
</nav>

<style>
  .path {
    display: grid;
    gap: 0.6rem;
  }

  ol {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: 0.5rem;
    counter-reset: none;
  }

  .step {
    position: relative;
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    grid-template-rows: auto auto;
    align-items: center;
    gap: 0.35rem 0.5rem;
    padding: 0.55rem 0.6rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg);
    min-width: 0;
  }

  .step.now {
    border-color: var(--brass-lit, var(--accent));
    box-shadow: 0 0 0 1px color-mix(in srgb, var(--brass-lit, #d8b45a) 35%, transparent) inset;
  }

  .step.todo {
    border-color: var(--warn);
  }

  .num {
    display: grid;
    place-items: center;
    width: 1.6rem;
    height: 1.6rem;
    border-radius: 50%;
    font-size: 0.8rem;
    font-weight: 700;
    background: var(--bg-raised);
    border: 1px solid var(--border);
  }

  .step.now .num {
    background: var(--brass, #8a6a34);
    color: var(--text);
  }

  .body {
    display: grid;
    gap: 0.1rem;
    min-width: 0;
    font-size: 0.85rem;
  }

  .go {
    grid-column: 1 / -1;
    min-height: 44px;
    font-size: 0.8rem;
  }

  .ceiling {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.4rem 0.6rem;
    margin: 0;
  }

  .chip {
    padding: 0.2rem 0.6rem;
    border-radius: 999px;
    border: 1px solid var(--brass, #8a6a34);
    background: color-mix(in srgb, var(--brass, #8a6a34) 18%, transparent);
    font-size: 0.8rem;
  }

  /* A phone: the three steps stack, each a single row with its button. */
  @media (max-width: 40rem) {
    ol {
      grid-template-columns: minmax(0, 1fr);
    }

    .step {
      grid-template-columns: auto minmax(0, 1fr) auto;
      grid-template-rows: auto;
    }

    .go {
      grid-column: auto;
      flex-shrink: 0;
      min-width: 5.5rem;
    }
  }
</style>
