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
    markPromoSeen,
    promoSeen,
    shouldOfferApp,
  } from '../lib/net/appDownload';

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
  const offerApp = shouldOfferApp();
  let promoOpen = $state(offerApp && linkedResetToken === null && !promoSeen());
  if (promoOpen) markPromoSeen();
  // Already on the phone: the address to type elsewhere is noise.
  const onAndroid = /Android/i.test(globalThis.navigator?.userAgent ?? '');

  let notice = $state('');
  let email = $state('');
  let password = $state('');
  let username = $state('');
  let busy = $state(false);
  let error = $state('');

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

<div class="shell">
  <h1>FolkIdle</h1>

  {#if configError}
    <p class="config" role="alert">{configError}</p>
  {/if}

  {#if mode === 'choose'}
    <p class="hint">Play instantly, or sign in to an account you can keep.</p>
    <button disabled={busy} onclick={() => run(loginWithDevice)}>Play as guest</button>
    <button disabled={busy} onclick={() => (mode = 'login')}>Sign in</button>
    <button disabled={busy} onclick={() => (mode = 'register')}>Create an account</button>
  {:else if mode === 'login' || mode === 'register'}
    <label>
      Email
      <input type="email" bind:value={email} autocomplete="email" />
    </label>

    {#if mode === 'register'}
      <label>
        Username
        <input bind:value={username} autocomplete="username" />
      </label>
    {/if}

    <label>
      Password
      <input
        type="password"
        bind:value={password}
        autocomplete={mode === 'register' ? 'new-password' : 'current-password'}
      />
      <!-- Modul: SAY THE RULE BEFORE IT IS BROKEN. The minimum moved from six
           to eight and the form said nothing either way, so the only way to
           discover it was to be refused. Length only - there is no required
           digit or symbol, deliberately; see PasswordPolicy. -->
      {#if mode === 'register'}
        <span class="hint">Eight characters or more. Length is all that is asked for.</span>
      {/if}
    </label>

    <button
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
    <button disabled={busy} onclick={() => (mode = 'choose')}>Back</button>

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
        <button onclick={() => (promoOpen = false)}>Not now</button>
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
  .shell {
    max-width: 22rem;
    margin: 12vh auto;
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

  .promoget {
    display: flex;
    align-items: center;
    justify-content: center;
    border: 1px solid var(--brass, var(--accent));
    border-radius: var(--radius);
    background: var(--bg-raised);
    color: inherit;
    text-decoration: none;
  }
</style>
