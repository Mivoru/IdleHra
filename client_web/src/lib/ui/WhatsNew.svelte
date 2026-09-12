<script lang="ts">
  import { pendingNotes, acknowledgeNotes, updateAvailable } from '../stores/version';
  import { reserveBottom, releaseBottom } from '../stores/bottomInset';

  const BOTTOM_INSET_KEY = 'update-prompt';

  // Modul: TWO WINDOWS, ONE FILE, and they can both be pending at once.
  //
  // "What's new" looks backwards - you have just loaded a build you had not
  // seen. "Update available" looks forwards - the build you are running has
  // been replaced while this tab was open. A player who leaves a tab open for
  // days across two deploys can genuinely be owed both, so the reload prompt
  // waits for the notes to be dismissed rather than stacking on top of them.
  const showNotes = $derived($pendingNotes.length > 0);
  const showReload = $derived(!showNotes && $updateAvailable);

  let dismissedReload = $state(false);
  let toastEl = $state<HTMLElement | null>(null);

  // Modul: THE PROMPT MUST NOT SIT ON A CONTROL. Found the honest way - the
  // exercise script could not click "Add" on the Friends screen because this
  // was parked on top of it. Reserves its own footprint (height plus how far it
  // is lifted off the bottom), through the shared inset so it and the
  // onboarding coach cannot clear each other's reservation.
  $effect(() => {
    const showing = showReload && !dismissedReload;
    if (!showing || !toastEl) {
      releaseBottom(BOTTOM_INSET_KEY);
      return;
    }

    const offset = Number.parseFloat(getComputedStyle(toastEl).bottom) || 0;
    reserveBottom(BOTTOM_INSET_KEY, toastEl.offsetHeight + offset + 12);

    return () => releaseBottom(BOTTOM_INSET_KEY);
  });

  function reload() {
    // Bypasses the bfcache, which would otherwise hand back the very bundle
    // being replaced.
    window.location.reload();
  }
</script>

{#if showNotes}
  <div class="backdrop" role="dialog" aria-modal="true" aria-label="What's new">
    <div class="card">
      <h2>What&rsquo;s new</h2>

      <div class="scroll">
      {#each $pendingNotes as release (release.version)}
        <div class="release">
          <p class="version">
            <strong>{release.version}</strong>
            <span class="dim tiny">{release.date}</span>
          </p>

          {#if release.headline}
            <p class="headline">{release.headline}</p>
          {/if}

          {#each release.sections as section (section.title)}
            <h3>{section.title}</h3>
            <ul>
              {#each section.items as item (item)}
                <li>{item}</li>
              {/each}
            </ul>
          {/each}
        </div>
      {/each}
      </div>

      <button onclick={acknowledgeNotes}>Got it</button>
    </div>
  </div>
{:else if showReload && !dismissedReload}
  <!-- Modul: NOT a backdrop. This one interrupts a session already in
       progress, and a modal over a fight the player is watching is worse than
       the staleness it reports. It sits in the corner and waits. -->
  <div class="toast" role="status" bind:this={toastEl}>
    <p class="line"><strong>FolkIdle has been updated.</strong></p>
    <p class="dim tiny">Reload to get the new version. Your progress is on the server &mdash; nothing is lost.</p>
    <div class="row">
      <button onclick={reload}>Reload</button>
      <button class="ghost" onclick={() => (dismissedReload = true)}>Later</button>
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

  /* Modul: THE DISMISS BUTTON DOES NOT SCROLL AWAY.
     The card used to scroll as one piece, so with a release of any length the
     only way out of a modal was to scroll to the bottom of it first - and the
     onboarding coach's own "Got it" showed through the backdrop above it,
     which is two buttons with one name and only one of them reachable.
     Header and action bar are fixed; only the notes move. */
  .card {
    width: min(32rem, 100%);
    max-height: 86vh;
    background: var(--bg-panel);
    border: 1px solid var(--brass);
    border-radius: var(--radius);
    padding: 1.2rem;
    display: grid;
    grid-template-rows: auto minmax(0, 1fr) auto;
    gap: 0.5rem;
  }

  .scroll {
    overflow-y: auto;
    /* Anything arriving at the top of a scroller wants this - see the loot
       list. Harmless here and correct if a release is ever prepended. */
    overflow-anchor: none;
  }

  h2 {
    margin: 0;
    font-size: 1.1rem;
  }

  h3 {
    margin: 0.6rem 0 0.2rem;
    font-size: 0.8rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  .release + .release {
    margin-top: 0.9rem;
    padding-top: 0.9rem;
    border-top: 1px solid var(--border);
  }

  .version {
    display: flex;
    align-items: baseline;
    gap: 0.5rem;
    margin: 0;
  }

  .headline {
    margin: 0.15rem 0 0;
    font-size: 0.9rem;
  }

  ul {
    margin: 0;
    padding-left: 1.1rem;
    display: grid;
    gap: 0.3rem;
  }

  li {
    font-size: 0.85rem;
    line-height: 1.45;
  }

  .dim {
    color: var(--text-dim);
  }

  .tiny {
    font-size: 0.72rem;
  }

  /* Modul: the corner prompt. Lifted clear of the chat handle the same way the
     onboarding coach is - see OnboardingCoach.svelte, which learned it from a
     store screenshot where the handle sat on the end of every sentence. */
  .toast {
    position: fixed;
    right: 1rem;
    bottom: calc(1rem + var(--sa-bottom));
    z-index: 45;
    width: min(22rem, calc(100vw - 2rem));
    box-sizing: border-box;
    display: grid;
    gap: 0.35rem;
    padding: 0.7rem 0.9rem;
    background: var(--bg-raised);
    border: 1px solid var(--accent);
    border-radius: 0.7rem;
    box-shadow: 0 6px 18px rgba(0, 0, 0, 0.35);
  }

  @media (max-width: 40rem) {
    .toast {
      bottom: calc(4.25rem + var(--sa-bottom));
    }
  }

  .line {
    margin: 0;
    font-size: 0.85rem;
  }

  .row {
    display: flex;
    gap: 0.4rem;
    flex-wrap: wrap;
  }

  /* The touch floor is deliberate - see app.css. A control in a flex row also
     needs flex-shrink: 0 or min-width: 0 lets it shrink below its own width. */
  .row button {
    min-height: 44px;
    flex-shrink: 0;
  }

  .ghost {
    background: transparent;
    color: var(--text-dim);
  }

  /* The touch floor, and the card's own action is the one control a player
     must always be able to hit. */
  .card > button {
    min-height: 44px;
  }
</style>
