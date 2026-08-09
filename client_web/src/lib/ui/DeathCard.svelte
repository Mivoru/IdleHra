<script lang="ts">
  // Modul: A DEATH SAID ALMOST NOTHING.
  //
  // The whole report was a small badge in the corner reading "Died" - the same
  // shape as every other halt reason - and the killer's identity was already
  // gone by then, because the respawn clears CurrentMonsterId before any
  // broadcast runs. A player came back to an idle character at full health
  // with no idea what had happened or why they had stopped earning.
  //
  // WHAT KILLED YOU IS THE WHOLE POINT. Dying in this game is almost always
  // one of two fixable things - an empty larder, or a monster out of the
  // character's league - so the card names the cause and points at the fix
  // rather than just reporting the fact.
  import { deathSummary, dismissDeath, playerState } from '../stores/game';
  import { onMount } from 'svelte';
  import { loadContent, monsterName, type ContentRegistry } from '../net/content';
  import { requestScreen } from '../stores/navigation';

  // The monster table is content, not state - loaded once, the same way
  // Combat loads it. A card that says "monster 105" is not a card.
  let registry = $state<ContentRegistry | null>(null);
  onMount(async () => {
    try {
      registry = await loadContent();
    } catch {
      // A missing name is survivable here; the numbers still say what
      // happened, and blocking the card on a content fetch would mean
      // the player misses the moment entirely.
    }
  });

  const death = $derived($deathSummary);
  const snap = $derived($playerState);

  // The larder is the usual culprit, and it is checkable right here. Auto-eat
  // is what keeps a character alive mid-fight; with nothing loaded, the fourth
  // monster of a region kills them every time.
  const larderBites = $derived(
    snap
      ? Number(snap.Food1_Count ?? 0) + Number(snap.Food2_Count ?? 0) + Number(snap.Food3_Count ?? 0)
      : 0,
  );
</script>

{#if death}
  <div class="backdrop" role="dialog" aria-modal="true" aria-label="Your character died">
    <div class="card">
      <p class="kicker">Down</p>
      <h2>
        {#if death.monsterId > 0}
          {monsterName(registry, death.monsterId)} killed you
        {:else}
          Your character died
        {/if}
      </h2>

      <p class="dim small">
        You revived where you fell, at full health — but the fight stopped, and
        a stopped character earns nothing until you send it back.
      </p>

      {#if larderBites === 0}
        <!-- The cause, when it is knowable. An empty larder is the single most
             common way a character dies in this game, and it is the one the
             player can fix in ten seconds. -->
        <p class="cause">
          <strong>Your larder is empty.</strong> Auto-eat is what heals you mid-fight;
          without it the deeper monsters of a region will keep doing this.
        </p>
      {:else}
        <p class="cause soft">
          You still have {larderBites} bite{larderBites === 1 ? '' : 's'} of food. If
          this keeps happening, the monster is simply out of your league — better
          gear or an easier target.
        </p>
      {/if}

      <div class="row">
        {#if larderBites === 0}
          <button
            class="primary"
            onclick={() => {
              dismissDeath();
              requestScreen('larder');
            }}
          >
            Fill the larder
          </button>
        {:else}
          <button
            class="primary"
            onclick={() => {
              dismissDeath();
              requestScreen('combat');
            }}
          >
            Back to the fight
          </button>
        {/if}
        <button onclick={dismissDeath}>Close</button>
      </div>
    </div>
  </div>
{/if}

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.55);
    display: grid;
    place-items: center;
    z-index: 60;
    padding: 1rem;
  }

  .card {
    width: min(26rem, 100%);
    background: var(--bg-panel);
    border: 1px solid var(--danger);
    border-radius: var(--radius);
    padding: 1.2rem;
    display: grid;
    gap: 0.5rem;
  }

  .kicker {
    margin: 0;
    font-size: 0.7rem;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--danger);
  }

  h2 {
    margin: 0;
    font-size: 1.2rem;
  }

  .cause {
    margin: 0.3rem 0 0;
    padding: 0.5rem 0.65rem;
    font-size: 0.85rem;
    background: rgba(224, 85, 63, 0.12);
    border-left: 3px solid var(--danger);
    border-radius: 4px;
  }

  .cause.soft {
    background: none;
    border-left-color: var(--border);
    color: var(--text-dim);
  }

  .row {
    display: flex;
    gap: 0.4rem;
    margin-top: 0.5rem;
  }

  button {
    font: inherit;
    padding: 0.4rem 0.7rem;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    cursor: pointer;
  }

  .primary {
    border-color: var(--brass);
    color: var(--brass-lit);
  }

  .dim {
    color: var(--text-dim);
  }

  .small {
    font-size: 0.82rem;
    margin: 0;
  }
</style>
