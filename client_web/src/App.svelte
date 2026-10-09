<script lang="ts">
  import Login from './routes/Login.svelte';
  import Modal from './lib/ui/Modal.svelte';
  import { storedToken, storedRefreshToken, refreshSession } from './lib/net/auth';
  import { resolveBackPress, watchHardwareBack, exitApp, isCloseOutcome, LAYER_Z, type BackOutcome } from './lib/net/backButton';
  import { topOverlay } from './lib/stores/sheet';
  import { exitPromptOpen } from './lib/stores/exitPrompt';
  import { initLanguage, loadTranslations } from './lib/ui/i18n';
  import { unlockAudio, play } from './lib/ui/audio';
  import { startMusic } from './lib/ui/music';
  import { bootStage, bootStep, bootDone } from './lib/ui/boot';
  import type { Component } from 'svelte';

  // Modul: THE LANDING PAGE IS THIS FILE. A stranger's first download is the
  // bundle this component pulls in, and PageSpeed measured the login painting
  // (the page's LCP) waiting on that bundle to arrive and mount. Everything a
  // session needs - the header, the screens, the socket, the query cache and
  // the stores behind them - is Game.svelte, requested the moment a token
  // exists. Main chunk 88 KiB -> about 25 KiB gzip; measured on a throttled
  // phone, LCP 2.9 s -> 2.4 s. Anything added here is added to that download,
  // so a signed-in feature belongs in Game.svelte.
  initLanguage();
  void loadTranslations();

  // 49 screens are modal panels, not URLs, so this is a screen store rather
  // than a router - closer to the existing design and one dependency fewer.
  let token = $state<string | null>(storedToken());

  // Modul: OPENING THE APP TOMORROW USED TO MEAN THE LOGIN FORM.
  //
  // The JWT lasts a day. In a browser tab that is a mild annoyance; on a phone
  // it is a password prompt every morning, in a game whose entire proposition
  // is that it runs while you are gone. The server issues a refresh token
  // beside the JWT now, and this is where it is spent.
  //
  // `restoring` exists so the login form does not FLASH. Without it, a launch
  // with an expired JWT paints the sign-in screen, then replaces it half a
  // second later - which reads as having been signed out and then rescued, and
  // is the sort of thing a player reports as a bug.
  let restoring = $state(token === null && storedRefreshToken() !== null);

  if (restoring) {
    void refreshSession().then((session) => {
      if (session) token = session.token;
      restoring = false;
    });
  }

  // Modul: REQUESTED AS SOON AS THERE IS A TOKEN, not when it is first drawn.
  // A returning player has one before anything mounts, so the chunk downloads
  // alongside the rest of the start-up rather than after it. A failed load is
  // almost always a deploy that replaced the chunk under this tab, answered
  // with a reload button rather than an automatic reload that could loop.
  let Game = $state<Component<any> | null>(null);
  let gameFailed = $state(false);
  let gameRequested = false;
  function loadGame(): void {
    if (gameRequested) return;
    gameRequested = true;
    gameFailed = false;
    import('./Game.svelte')
      .then((mod) => (Game = mod.default))
      .catch((err) => {
        console.warn('the game failed to load', err);
        gameRequested = false;
        gameFailed = true;
        bootDone();
      });
  }
  $effect(() => {
    if (token) loadGame();
  });

  // Browsers refuse to start an AudioContext before a user gesture, so a
  // gesture arms it. EVERY gesture, not just the first: unlockAudio also
  // resumes a context the browser suspended while the app was backgrounded.
  //
  // Modul: THE CLICK WAS HEARD ONCE PER SESSION. It lived only in the
  // first-gesture handler above, which removed itself, so every button after
  // the first was silent and the owner read the UI as having no sound at all.
  // One delegated listener covers every button on every screen, including
  // ones added later; a disabled button fires no click, so it stays silent.
  function armAudio() {
    unlockAudio();
    // Background music starts on the same gesture - browsers refuse sound
    // before one - and only in the game, not on the sign-in screen.
    if (token) void startMusic();
  }
  function clickSound(event: MouseEvent) {
    const target = event.target;
    if (!(target instanceof Element)) return;
    const control = target.closest('button, [role="button"], a[href], summary');
    if (!control || control.closest('[data-no-click-sound]')) return;
    play('buttonClick');
  }
  $effect(() => {
    window.addEventListener('pointerdown', armAudio, true);
    window.addEventListener('click', clickSound, true);
    return () => {
      window.removeEventListener('pointerdown', armAudio, true);
      window.removeEventListener('click', clickSound, true);
    };
  });

  // Modul: BACK BEFORE THE GAME EXISTS. Game.svelte answers the Android back
  // button and Escape once it is mounted; until then - the login form, the
  // restore, the moment the game chunk is still downloading - this does, and
  // the only layers there are the exit prompt and a Modal the login form
  // opened. Exactly one of the two answers at a time, or one press would be
  // handled twice.
  const gameAnswersBack = $derived(token !== null && Game !== null);

  function resolveBack(): BackOutcome {
    return resolveBackPress({
      overlayZ: $topOverlay?.z ?? null,
      exitPromptOpen: $exitPromptOpen,
      deathCardOpen: false,
      victoryCardOpen: false,
      offlineSummaryOpen: false,
      chatDockOpen: false,
      navOpen: false,
      historyDepth: 0,
      // The login form is a root: there is nothing behind it to go back to.
      atRoot: true,
    });
  }

  function applyBack(outcome: BackOutcome): void {
    if (outcome === 'close-exit-prompt') exitPromptOpen.set(false);
    else if (outcome === 'close-overlay') $topOverlay?.close();
    else if (outcome === 'confirm-exit') exitPromptOpen.set(true);
  }

  $effect(() =>
    watchHardwareBack(() => {
      if (!gameAnswersBack) applyBack(resolveBack());
    }),
  );

  function onWindowKey(event: KeyboardEvent): void {
    if (gameAnswersBack || event.key !== 'Escape' || event.defaultPrevented) return;
    const outcome = resolveBack();
    if (!isCloseOutcome(outcome)) return;
    event.preventDefault();
    applyBack(outcome);
  }

  // Modul: the loading screen (index.html #boot) - see lib/ui/boot.ts. Signed
  // out, it ends when the login form is the screen; signed in, Game.svelte
  // carries it to the first snapshot, and until Game has loaded it sits at the
  // first connecting step.
  $effect(() => {
    if (gameAnswersBack) return;
    if (gameFailed) return;
    const stage = bootStage({ restoring, signedIn: token !== null, phase: 'idle', hasState: false });
    if (stage === 'done') bootDone();
    else bootStep(stage.percent, stage.message, stage.next);
  });
</script>

<svelte:head>
  <title>FolkIdle</title>
</svelte:head>

<svelte:window onkeydown={onWindowKey} />

{#if token}
  {#if Game}
    <Game {token} onToken={(next: string) => (token = next)} onSignedOut={() => (token = null)} />
  {:else if gameFailed}
    <section class="panel restoring">
      <p>The game could not be loaded - it has probably been updated since this tab opened.</p>
      <button onclick={() => location.reload()}>Reload</button>
    </section>
  {/if}
{:else if restoring}
  <!-- Modul: NOT A SPINNER PRETENDING TO BE THE GAME. This is the half
       second in which a stored refresh token is exchanged for a session, and
       it exists so the login form does not appear and then vanish - which
       reads as having been signed out and rescued, and gets reported as a
       bug. If the exchange fails, the form below arrives instead. -->
  <div class="restoring">
    <strong>FolkIdle</strong>
    <p>Signing you back in&hellip;</p>
  </div>
{:else}
  <Login onAuthenticated={(newToken) => (token = newToken)} />
{/if}

<!-- Modul: ASKING BEFORE LEAVING, because back no longer leaves on its own.
     Attaching a backButton listener disables Capacitor's default exit, so
     this is the only way out of the app that is left - it has to exist, and
     it has to work on the login screen as well as in the game.
     Rendered here, outside Game, for that reason. -->
{#if $exitPromptOpen}
  <!-- Back and Escape close it through exitPromptOpen (resolveBack), so the
       Modal registers nothing; a tap outside does not answer the question. -->
  <Modal label="Leave FolkIdle" z={LAYER_Z.exitPrompt} width="22rem" layout="block" register={false}>
    <div class="exitcard">
      <h2>Leave FolkIdle?</h2>
      <p>
        Your character keeps playing while the app is closed, and you are paid
        for the time when you come back.
      </p>
      <div class="exitrow">
        <button class="stay" onclick={() => exitPromptOpen.set(false)}>Stay</button>
        <button
          class="leave"
          onclick={() => {
            exitPromptOpen.set(false);
            exitApp();
          }}>Leave</button
        >
      </div>
    </div>
  </Modal>
{/if}

<style>
  /* Modul: THE EXIT CONFIRMATION IS THE TOPMOST THING IN THE APP.
     Above the death and victory cards (60) and above PlayerProfileModal
     (1000), because it is the answer to a press the player made while looking
     at one of them - a dialog that asks "leave?" from behind another panel is
     a dialog nobody can answer. */
  .restoring {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 0.4rem;
    min-height: 60vh;
    color: var(--text-dim);
  }

  .restoring strong {
    font-size: 1.3rem;
    color: var(--text);
  }

  .exitcard h2 {
    margin: 0 0 0.4rem;
    font-size: 1.1rem;
  }

  .exitcard p {
    margin: 0 0 0.9rem;
    color: var(--text-dim);
    font-size: 0.88rem;
    line-height: 1.35;
  }

  .exitrow {
    display: flex;
    gap: 0.6rem;
  }

  /* Modul: 44px stated here rather than inherited from app.css. A scoped
     component selector outranks that file's `button:not(.touch-exempt)` rule,
     which is exactly how the header's menu button ended up 37px tall on all
     twenty-six screens. Equal widths so neither answer is the accidental
     default. */
  .exitrow button {
    flex: 1 1 0;
    min-height: 44px;
    min-width: 44px;
  }

  .exitrow .leave {
    border-color: var(--danger);
    color: var(--danger);
  }
</style>
