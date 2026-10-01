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
  import { deathSummary, dismissDeath, playerState, pushLocalNotice } from '../stores/game';
  import { assignCharacterActivity, EMPTY_GUID } from '../net/commands';
  import { writePref, PREF_LAST_MONSTER } from '../net/prefs';
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

  // Modul: TASK 72 - THE WAY BACK IS ONE PRESS. "Back to the fight" only
  // opened the Combat screen, where the player then had to find the monster
  // again. With food in the larder the two real choices are the same fight
  // or an easier one, so the card offers both and sends the main character
  // straight there. "One easier" is the monster before it in the same region
  // (the canon runs 91-115, five a region, boss last); the first of a region
  // has none, and a region the player can see is a region they have opened.
  const easierId = $derived.by(() => {
    const id = death?.monsterId ?? 0;
    const offset = id - 91;
    if (offset < 0 || offset >= 25 || offset % 5 === 0) return 0;
    return id - 1;
  });

  function refight(monsterId: number) {
    const character = snap?.Slot1_CharacterId ?? EMPTY_GUID;
    const outcome = assignCharacterActivity(character, monsterId);
    dismissDeath();
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    writePref(PREF_LAST_MONSTER, String(monsterId));
    requestScreen('combat');
  }
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
        {:else if death.monsterId > 0}
          <button class="primary" data-testid="death-again" onclick={() => refight(death.monsterId)}>
            Again
          </button>
          {#if easierId > 0}
            <button data-testid="death-easier" onclick={() => refight(easierId)}>
              One easier: {monsterName(registry, easierId)}
            </button>
          {/if}
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
    /* Fixed, so body's safe-area padding does not reach it - its own inset. */
    padding: calc(1rem + var(--sa-top)) calc(1rem + var(--sa-right)) calc(1rem + var(--sa-bottom))
      calc(1rem + var(--sa-left));
  }

  /* The card scrolls rather than running off a short screen (a landscape
     phone, a large font setting): a fixed backdrop cannot scroll for it. The
     vh line is the fallback for an engine without dvh. */
  .card {
    width: min(26rem, 100%);
    max-height: calc(100vh - 2rem);
    max-height: calc(100dvh - 2rem - var(--sa-top) - var(--sa-bottom));
    overflow-y: auto;
    overscroll-behavior: contain;
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
    flex-wrap: wrap;
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
