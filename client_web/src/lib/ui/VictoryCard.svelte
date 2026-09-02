<script lang="ts">
  // Modul: THE FIRST CLEAR, ACKNOWLEDGED.
  //
  // Until a boss is put down once it carries FIVE times its health and TWICE
  // its attack - the hardest single fight in the game. It produced no
  // acknowledgement of any kind: no card, no summary, nothing. A player found
  // out they had unlocked a race by noticing a new option on a different
  // screen, possibly days later, and never learned that a whole region had
  // opened or what lived in it.
  //
  // The rewards come off the wire; the UNLOCKS are derived from the boss id
  // against content the client already has - see victories.ts for why that is
  // not a second copy of anything.
  import { victorySummary, dismissVictory } from '../stores/game';
  import { onMount } from 'svelte';
  import { loadContent, monsterName, type ContentRegistry } from '../net/content';
  import {
    unlocksFor,
    formatFightDuration,
    FIRST_CLEAR_HP_MULTIPLIER,
    FIRST_CLEAR_ATTACK_MULTIPLIER,
  } from './victories';
  import { requestScreen } from '../stores/navigation';
  import Burst from './Burst.svelte';

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

  const victory = $derived($victorySummary);
  const unlocks = $derived(victory ? unlocksFor(victory.monsterId) : null);
</script>

{#if victory && unlocks}
  <div class="backdrop" role="dialog" aria-modal="true" aria-label="First victory">
    <!-- Modul: the shared flourish, on the loudest moment in the game. A
         sweep as it is presented and a burst behind the heading - the same
         vocabulary the achievement toast uses, so a big moment looks like a
         bigger version of a small one rather than like a different game. -->
    <div class="card folk-sweep folk-glow">
      <span class="crown"><Burst count={16} reach={4.5} /></span>
      <p class="kicker">First clear</p>
      <h2>{monsterName(registry, victory.monsterId)} is down</h2>

      <!-- Saying WHICH monster they fought. The farmed version is a different
           creature from the one they just beat, and that is worth knowing
           before they go back expecting the same fight. -->
      <p class="dim small">
        That was the {FIRST_CLEAR_HP_MULTIPLIER}x health and
        {FIRST_CLEAR_ATTACK_MULTIPLIER}x attack version. Every time from now on
        it fights at its ordinary strength.
      </p>

      <dl class="stats">
        <div><dt>Fight lasted</dt><dd>{formatFightDuration(victory.durationSeconds)}</dd></div>
        <div><dt>Gold</dt><dd>{victory.gold.toLocaleString()}</dd></div>
        <div><dt>Experience</dt><dd>{victory.xp.toLocaleString()}</dd></div>
      </dl>

      <h3>What it opened</h3>
      <ul class="unlocks">
        {#if unlocks.raceUnlocked}
          <li>
            <strong>{unlocks.raceUnlocked}</strong> is now playable — a male and a
            female arrive on your roster, so the bloodline can be bred forward.
          </li>
        {/if}

        {#if unlocks.openedRegion !== null}
          <li>
            <strong>Region {unlocks.openedRegion}</strong> is open, and so is its
            gear:
            <span class="dim">
              {unlocks.openedMonsterIds.map((id) => monsterName(registry, id)).join(', ')}
            </span>
          </li>
        {:else}
          <li>
            That was the last boss in the game. Nothing further opens — what is
            left is the chase for rarity and affixes.
          </li>
        {/if}

        <li class="dim">
          Anything it dropped is in your Chest, and counts toward the Book of
          Deeds.
        </li>
      </ul>

      <div class="row">
        <button
          class="primary"
          onclick={() => {
            dismissVictory();
            requestScreen('combat');
          }}
        >
          {unlocks.openedRegion !== null ? 'Take me there' : 'Back to it'}
        </button>
        <button onclick={dismissVictory}>Close</button>
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
    width: min(30rem, 100%);
    max-height: 86vh;
    overflow-y: auto;
    background: var(--bg-panel);
    border: 1px solid var(--brass);
    border-radius: var(--radius);
    padding: 1.2rem;
    display: grid;
    gap: 0.5rem;
  }

  .crown {
    position: absolute;
    left: 50%;
    top: 2.2rem;
    width: 0;
    height: 0;
  }

  .kicker {
    margin: 0;
    font-size: 0.7rem;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--brass-lit);
  }

  h2 {
    margin: 0;
    font-size: 1.3rem;
  }

  h3 {
    margin: 0.6rem 0 0.1rem;
    font-size: 0.72rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  .stats {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 0.5rem;
    margin: 0.5rem 0 0;
  }

  .stats div {
    display: grid;
    gap: 0.1rem;
  }

  dt {
    font-size: 0.7rem;
    color: var(--text-dim);
  }

  dd {
    margin: 0;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
  }

  .unlocks {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.35rem;
    font-size: 0.85rem;
  }

  .unlocks li {
    padding-left: 0.9rem;
    position: relative;
  }

  .unlocks li::before {
    content: '◆';
    position: absolute;
    left: 0;
    color: var(--brass);
    font-size: 0.6rem;
    top: 0.25rem;
  }

  .row {
    display: flex;
    gap: 0.4rem;
    margin-top: 0.6rem;
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
