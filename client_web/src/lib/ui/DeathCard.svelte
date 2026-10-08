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
  import { workersOf } from './workers';
  import { selectedCharacterSlot } from '../stores/selectedCharacter';
  import { writePref, PREF_LAST_MONSTER } from '../net/prefs';
  import { onMount } from 'svelte';
  import { loadContent, monsterName, type ContentRegistry } from '../net/content';
  import { requestScreen } from '../stores/navigation';
  import Modal from './Modal.svelte';

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
    // The character that died, not always slot 1: a death idles its slot with
    // halt reason Died (2) or Stepped down (7). Falls back to the picked one.
    const people = workersOf(snap);
    const fallen = people.find((w) => w.activity === 0 && (w.halt === 2 || w.halt === 7))
      ?? people.find((w) => w.slot === $selectedCharacterSlot)
      ?? people[0];
    const character = fallen?.id ?? EMPTY_GUID;
    const outcome = assignCharacterActivity(character, monsterId);
    dismissDeath();
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    writePref(PREF_LAST_MONSTER, String(monsterId));
    requestScreen('combat');
  }
</script>

{#if death}
  <!-- Back and Escape reach it through App's deathSummary store, so it does
       not register a closer of its own (see Modal.svelte). -->
  <Modal label="Your character died" tone="danger" register={false}>
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
  </Modal>
{/if}

<style>
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

  .dim {
    color: var(--text-dim);
  }

  .small {
    font-size: 0.82rem;
    margin: 0;
  }
</style>
