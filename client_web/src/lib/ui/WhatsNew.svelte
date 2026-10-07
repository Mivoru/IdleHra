<script lang="ts">
  import { pendingNotes, acknowledgeNotes, updateAvailable } from '../stores/version';
  import { offlineSummary, playerState } from '../stores/game';
  import { reserveBottom, releaseBottom } from '../stores/bottomInset';
  import Modal from './Modal.svelte';

  const BOTTOM_INSET_KEY = 'update-prompt';

  // Modul: TWO WINDOWS, ONE FILE, and they can both be pending at once.
  //
  // "What's new" looks backwards - you have just loaded a build you had not
  // seen. "Update available" looks forwards - the build you are running has
  // been replaced while this tab was open. A player who leaves a tab open for
  // days across two deploys can genuinely be owed both, so the reload prompt
  // waits for the notes to be dismissed rather than stacking on top of them.
  //
  // Modul: AFTER "WELCOME BACK" (owner, 2026-10-07). The notes used to open the
  // moment the app mounted, on top of (or beside) the offline summary, so a
  // returning player met two windows at once and the one with their earnings
  // was the one pushed behind. They now wait for the first state snapshot - the
  // summary is built from that same packet, so by then we know whether there IS
  // one - and for it to be closed. With no summary they show right after login.
  // `pendingNotes` is untouched, so the seen-version tracking is unchanged.
  const showNotes = $derived($pendingNotes.length > 0 && $playerState !== null && $offlineSummary === null);
  const showReload = $derived(!showNotes && $updateAvailable);

  // Modul: BACK MEANS "GOT IT". The notes are a modal, and after every OTA
  // update an APK player meets them first - but back could not see them, so it
  // changed the screen underneath (or, on the map, opened "Leave FolkIdle?" on
  // top of them). The Modal registers acknowledgeNotes on the overlay stack
  // while the notes show; the reload chip below covers nothing and leaves back
  // alone.

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
  <!-- Modul: THE DISMISS BUTTON DOES NOT SCROLL AWAY. The card used to
       scroll as one piece, so with a release of any length the only way out
       was to scroll to the bottom of it first. Header and footer are pinned
       by the Modal; only the notes move. -->
  <Modal label="What's new" tone="brass" width="32rem" layout="block" onClose={acknowledgeNotes} dismissOnScrim={false}>
      {#snippet header()}
        <div class="head">
          <h2>What&rsquo;s new</h2>
          <button class="close" aria-label="Close" onclick={acknowledgeNotes}>&times;</button>
        </div>
      {/snippet}
      {#snippet footer()}<button class="got-it" onclick={acknowledgeNotes}>Got it</button>{/snippet}

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
  </Modal>
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
  .head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
  }

  .close {
    flex: 0 0 auto;
    min-width: 44px;
    min-height: 44px;
    font-size: 1.4rem;
    line-height: 1;
    padding: 0;
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
    /* A fixed box never sees body's padding, so it carries the side insets
       itself - in landscape the right one is a camera cutout or a gesture
       bar, and the prompt sat 13px into it (check:safearea). */
    right: calc(1rem + var(--sa-right));
    /* Above the chat handle at EVERY width: ChatDock owns the same corner
       (right 1rem, bottom 1rem) at desktop too, and this prompt used to be
       lifted only on phones - so at desktop, whenever an update was out, it
       sat on "Show chat" and swallowed its clicks. Found by a two-browser chat
       check that could not open the dock. */
    bottom: calc(4.25rem + var(--sa-bottom) + var(--tabbar-h));
    z-index: 45;
    width: min(22rem, calc(100vw - 2rem - var(--sa-left) - var(--sa-right)));
    box-sizing: border-box;
    display: grid;
    gap: 0.35rem;
    padding: 0.7rem 0.9rem;
    background: var(--bg-raised);
    border: 1px solid var(--accent);
    border-radius: 0.7rem;
    box-shadow: 0 6px 18px rgba(0, 0, 0, 0.35);
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

  /* The touch floor, and the card's own action is the one control a player
     must always be able to hit. */
  .got-it {
    min-height: 44px;
    width: 100%;
  }
</style>
