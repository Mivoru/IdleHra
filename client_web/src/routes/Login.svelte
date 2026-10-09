<script lang="ts">
  import {
    loginWithDevice,
    loginWithEmail,
    register,
    requestPasswordReset,
    completePasswordReset,
    takeResetTokenFromUrl,
    AuthError,
  } from '../lib/net/auth';
  import { configurationProblem } from '../lib/net/config';
  import Modal from '../lib/ui/Modal.svelte';
  import { isNativePlatform } from '../lib/net/platform';
  import {
    APP_DOWNLOAD_PATH,
    appDownloadUrl,
    markPlayed,
    markPromoSeen,
    playedBefore,
    promoSeen,
    shouldOfferApp,
  } from '../lib/net/appDownload';
  import { backgroundUrl } from '../lib/ui/sprites';

  // Modul: a misconfigured native build fails as a connection timeout, which
  // reads like the server being down. Said plainly here instead - this is the
  // first screen, and it is the only place the difference can be explained
  // before the player concludes the game is broken.
  const configError = configurationProblem(isNativePlatform());

  interface Props {
    onAuthenticated: (token: string) => void;
  }

  let { onAuthenticated }: Props = $props();

  // Modul: PASSWORD RESET. Registration used to be the only place a password
  // was ever set, so forgetting one meant losing the account for good.
  //
  // 'reset' is entered from the emailed link rather than from a button: the
  // token arrives in the URL fragment and is read once at startup, which is
  // also what clears it out of the address bar.
  // Read ONCE, at module scope: takeResetTokenFromUrl also clears the fragment
  // from the address bar, so calling it twice would return null the second
  // time and lose the token.
  const linkedResetToken = takeResetTokenFromUrl();

  let mode = $state<'choose' | 'login' | 'register' | 'forgot' | 'reset'>(
    linkedResetToken === null ? 'choose' : 'reset',
  );
  let resetToken = $state<string>(linkedResetToken ?? '');

  // Modul: the Android app, offered in the browser only - see appDownload.ts.
  // The link below the form is permanent; the popup shows once per browser and
  // is marked seen the moment it opens, so closing the tab does not bring it
  // back. Not during a password reset: that player came from an email link to
  // do one thing.
  //
  // Task 109: NOT ON THE VERY FIRST VISIT. A stranger who had not played yet
  // was asked to install an app before seeing a single screen of the game, on
  // top of the pitch. It waits until this browser has been through one
  // session (markPlayed, on every successful sign-in below).
  const offerApp = shouldOfferApp();
  let promoOpen = $state(offerApp && linkedResetToken === null && !promoSeen() && playedBefore());
  if (promoOpen) markPromoSeen();
  // Already on the phone: the address to type elsewhere is noise.
  const onAndroid = /Android/i.test(globalThis.navigator?.userAgent ?? '');

  let notice = $state('');
  let email = $state('');
  let password = $state('');
  let username = $state('');
  let busy = $state(false);
  let error = $state('');
  let showPassword = $state(false);

  // Modul: A STRIP CUT FOR THIS BOX, not main_hub itself. The painting is
  // the largest thing on the first screen a stranger sees, so it IS the page's
  // LCP - and main_hub is 1920x1072, 446 KiB, for a box 300 px wide. On a
  // throttled phone it finished 4 s after the form. login_valley.webp is the
  // same crop .art used to take (center 60%), 800x317, 64 KiB, and index.html
  // preloads it so the download starts with the bundle instead of after it.
  // Re-cut it (tools/prepare_backgrounds.py, STRIPS) if the painting or the
  // box's aspect-ratio changes - under a NEW name: /sprites/* is served
  // immutable, and the preload in index.html names the file too.
  const scene = backgroundUrl('login_valley');

  // Modul: ALWAYS THE SAME MESSAGE, whether or not that address has an
  // account. Anything else would rebuild the enumeration oracle that
  // /api/v1/auth/check-email was deleted for - and the server answers 200
  // regardless, so the client could not tell the difference anyway.
  async function askForLink() {
    busy = true;
    error = '';
    const canSend = await requestPasswordReset(email);
    busy = false;
    // The server says whether it can send mail at ALL - the same answer for
    // every address - so telling the truth here reveals nothing about accounts.
    notice = canSend
      ? 'If that address has an account, a reset link is on its way. It is good for one hour.'
      : 'Password reset by email is not available on this server yet, so no link was sent. Contact the game admin to recover your account.';
  }

  async function applyNewPassword() {
    busy = true;
    error = '';
    notice = '';
    try {
      await completePasswordReset(resetToken, password);
      notice = 'Your password is set. Sign in with it.';
      password = '';
      mode = 'login';
    } catch (err) {
      error = err instanceof AuthError ? err.message : 'Could not reach the server.';
    } finally {
      busy = false;
    }
  }

  async function run(action: () => Promise<{ token: string }>) {
    busy = true;
    error = '';
    try {
      const session = await action();
      markPlayed();
      onAuthenticated(session.token);
    } catch (err) {
      // Registration failures carry a Reason; a dead backend does not, and
      // "Failed to fetch" told a player nothing in the Unity client either.
      // Naming the likely cause is cheap and this is exactly the moment the
      // server is most likely to be down.
      error =
        err instanceof AuthError
          ? err.message
          : `Could not reach the server. Is it running on the configured address?`;
    } finally {
      busy = false;
    }
  }
</script>

<!-- Modul: THE PAGE'S MAIN LANDMARK, and the 12vh above the panel. That gap
     was the panel's own margin, which collapsed through #app and body - so
     when the form mounted it pushed BODY 99 px down, measured as the page's
     whole layout shift (CLS 0.12 on a phone). Padding does not collapse. -->
<main class="page">
<div class="shell">
  <h1>FolkIdle</h1>

  {#if configError}
    <p class="config" role="alert">{configError}</p>
  {/if}

  {#if mode === 'choose'}
    <!-- Task 109: THE FIRST IMPRESSION SAYS WHAT THE GAME IS. Three identical
         buttons and no picture was the whole pitch. The painting is the Home
         valley the player lands in, and "Play now" is the one filled action. -->
    <div class="art" style="background-image: url('{scene}')" role="img" aria-label="The valley at Home"></div>
    <p class="pitch">
      Raise a family of fighters, send them into the wilds, and let the village
      keep working while you are away.
    </p>
    <button class="primary" disabled={busy} onclick={() => run(loginWithDevice)}>Play now</button>
    <!-- Modul: THE GUEST NOTE IS THE TRUTH ABOUT GUESTS. A guest account is
         keyed to a device id held in this browser (auth.ts deviceId), and
         RegisterWithEmailAsync creates a NEW account rather than claiming the
         guest's - "the guest keeps the device". Nothing in the client links an
         email to a guest, so "sign up later and keep your progress" would be a
         lie. This says what actually happens. -->
    <p class="hint guest">
      Play now starts a guest game kept in this browser. It cannot be turned
      into an account later - to play on another device too, create an account
      first.
    </p>
    <div class="choose-row">
      <button disabled={busy} onclick={() => (mode = 'login')}>Sign in</button>
      <button disabled={busy} onclick={() => (mode = 'register')}>Create an account</button>
    </div>
  {:else if mode === 'login' || mode === 'register'}
    <label>
      Email
      <input type="email" bind:value={email} autocomplete="email" />
    </label>

    {#if mode === 'register'}
      <label>
        Username
        <input bind:value={username} autocomplete="username" maxlength="20" />
        <!-- RegisterWithEmailAsync: 3 to 20 characters after trimming. It is
             the DisplayName other players see. -->
        <span class="hint-small">3 to 20 characters. Other players see this name.</span>
      </label>
    {/if}

    <label>
      Password
      <span class="pw-row">
        <input
          type={showPassword ? 'text' : 'password'}
          bind:value={password}
          autocomplete={mode === 'register' ? 'new-password' : 'current-password'}
        />
        <button
          type="button"
          class="pw-toggle"
          aria-pressed={showPassword}
          onclick={() => (showPassword = !showPassword)}>{showPassword ? 'Hide' : 'Show'}</button
        >
      </span>
      <!-- Modul: SAY THE RULE BEFORE IT IS BROKEN. The minimum moved from six
           to eight and the form said nothing either way, so the only way to
           discover it was to be refused. Length only - there is no required
           digit or symbol, deliberately; see PasswordPolicy. -->
      {#if mode === 'register'}
        <span class="hint">Eight characters or more. Length is all that is asked for.</span>
      {/if}
    </label>

    <button
      class="primary"
      disabled={busy || !email || !password || (mode === 'register' && !username)}
      onclick={() =>
        run(() =>
          mode === 'register'
            ? register(email, password, username)
            : loginWithEmail(email, password),
        )}
    >
      {mode === 'register' ? 'Create account' : 'Sign in'}
    </button>
    <!-- Back is a way out, not a choice of equal weight to the form's action. -->
    <button class="link back" disabled={busy} onclick={() => (mode = 'choose')}>Back</button>

    {#if mode === 'login'}
      <button class="link" disabled={busy} onclick={() => (mode = 'forgot')}>
        Forgot your password?
      </button>
    {/if}
  {/if}

  {#if mode === 'forgot'}
    <p class="hint">
      Tell us the address on the account and we will send a link to set a new
      password.
    </p>
    <label>
      Email
      <input type="email" bind:value={email} autocomplete="email" />
    </label>
    <button disabled={busy || !email} onclick={askForLink}>Send the link</button>
    <button disabled={busy} onclick={() => (mode = 'login')}>Back</button>
  {/if}

  {#if mode === 'reset'}
    <p class="hint">Choose a new password for your account.</p>
    <label>
      New password
      <input type="password" bind:value={password} autocomplete="new-password" />
      <span class="hint-small">Eight characters or more. Length is all that is asked for.</span>
    </label>
    <button disabled={busy || !password} onclick={applyNewPassword}>Set it</button>
  {/if}

  {#if notice}
    <p class="notice">{notice}</p>
  {/if}

  {#if error}
    <p class="error">{error}</p>
  {/if}

  {#if offerApp}
    <a class="applink" href={APP_DOWNLOAD_PATH} download>Get the Android app</a>
  {/if}
</div>
</main>

{#if promoOpen}
  <!-- Task 106: the shared Modal - scrim, safe-area insets, the inert app
       behind it, a focus trap, and Escape/back closing it like any other. -->
  <Modal label="FolkIdle for Android" onClose={() => (promoOpen = false)} width="22rem" layout="block">
    <div class="promo">
      <h2>FolkIdle is on Android</h2>
      <p>
        The app keeps you signed in, and updates itself. Your account and your
        characters are the same in both.
      </p>
      {#if !onAndroid}
        <p class="promourl">On your phone, open <strong>{appDownloadUrl()}</strong></p>
      {/if}
      <div class="promorow">
        <button class="quiet" onclick={() => (promoOpen = false)}>Not now</button>
        <a
          class="promoget"
          href={APP_DOWNLOAD_PATH}
          download
          onclick={() => (promoOpen = false)}>Download</a
        >
      </div>
      <p class="promonote">The link stays at the bottom of this screen.</p>
    </div>
  </Modal>
{/if}

<style>
  .page {
    padding-block: 12vh;
  }

  .shell {
    max-width: 22rem;
    margin: 0 auto;
    display: grid;
    gap: 0.65rem;
    padding: 1.5rem;
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
  }

  h1 {
    margin: 0 0 0.25rem;
    font-size: 1.5rem;
    letter-spacing: 0.02em;
  }

  .notice {
    margin: 0.25rem 0 0;
    color: var(--brass-lit, inherit);
  }

  .link {
    background: none;
    border: none;
    padding: 0.2rem;
    font: inherit;
    font-size: 0.8rem;
    color: var(--text-dim);
    text-decoration: underline;
    cursor: pointer;
  }

  .hint-small {
    font-size: 0.75rem;
    color: var(--text-dim);
  }

  .hint {
    margin: 0 0 0.5rem;
    color: var(--text-dim);
  }

  /* A build problem, not a gameplay one - phrased and styled as something the
     player cannot fix, so they stop trying to. */
  .config {
    margin: 0 0 0.8rem;
    padding: 0.6rem 0.7rem;
    font-size: 0.82rem;
    color: var(--warn);
    border: 1px solid var(--warn);
    border-radius: var(--radius);
    text-align: left;
  }

  label {
    display: grid;
    gap: 0.25rem;
    color: var(--text-dim);
    font-size: 0.85rem;
  }

  .error {
    margin: 0.25rem 0 0;
    color: var(--danger);
  }

  .hint {
    font-size: 0.75rem;
    color: var(--text-dim);
  }

  .applink {
    justify-self: center;
    display: inline-flex;
    align-items: center;
    min-height: 44px;
    margin-top: 0.4rem;
    font-size: 0.85rem;
    color: var(--brass-lit, var(--accent));
  }

  .promo h2 {
    margin: 0 0 0.4rem;
    font-size: 1.1rem;
  }

  .promo p {
    margin: 0 0 0.7rem;
    color: var(--text-dim);
    font-size: 0.88rem;
    line-height: 1.35;
  }

  .promo .promourl {
    overflow-wrap: anywhere;
  }

  .promo .promonote {
    margin: 0.7rem 0 0;
    font-size: 0.75rem;
  }

  .promorow {
    display: flex;
    gap: 0.6rem;
  }

  /* 44px stated here: a scoped selector outranks app.css's touch floor. */
  .promorow > * {
    flex: 1 1 0;
    min-height: 44px;
    min-width: 44px;
  }

  /* Download is what the card is for, so it is the filled one - the same
     treatment as app.css's button.primary. */
  .promoget {
    display: flex;
    align-items: center;
    justify-content: center;
    border: 1px solid var(--brass-lit, var(--accent));
    border-radius: var(--radius);
    background-color: var(--brass, var(--accent));
    background-image: linear-gradient(180deg, rgba(255, 234, 190, 0.28), rgba(0, 0, 0, 0.12));
    color: #1d1408;
    font-weight: 700;
    text-decoration: none;
  }

  .quiet {
    background: transparent;
    border-color: transparent;
    color: var(--text-dim);
  }

  .art {
    /* login_valley is pre-cut to this aspect; the strip keeps the buttons in the
       first viewport on a phone. */
    aspect-ratio: 1920 / 760;
    margin: -0.25rem 0 0.1rem;
    background-size: cover;
    background-position: center 60%;
    border-radius: var(--radius);
    border: 1px solid var(--border);
  }

  .pitch {
    margin: 0;
    line-height: 1.4;
  }

  .hint.guest {
    margin: 0;
  }

  .choose-row {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.5rem;
  }

  .pw-row {
    display: flex;
    gap: 0.4rem;
  }

  .pw-row input {
    flex: 1 1 auto;
    min-width: 0;
  }

  .pw-toggle {
    flex-shrink: 0;
    min-width: 44px;
    min-height: 44px;
    font-size: 0.8rem;
  }

  .link.back {
    justify-self: center;
    min-height: 44px;
    font-size: 0.9rem;
  }
</style>
