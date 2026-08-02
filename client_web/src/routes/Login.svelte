<script lang="ts">
  import { loginWithDevice, loginWithEmail, register, AuthError } from '../lib/net/auth';
  import { configurationProblem } from '../lib/net/config';
  import { isNativePlatform } from '../lib/net/platform';

  // Modul: a misconfigured native build fails as a connection timeout, which
  // reads like the server being down. Said plainly here instead - this is the
  // first screen, and it is the only place the difference can be explained
  // before the player concludes the game is broken.
  const configError = configurationProblem(isNativePlatform());

  interface Props {
    onAuthenticated: (token: string) => void;
  }

  let { onAuthenticated }: Props = $props();

  let mode = $state<'choose' | 'login' | 'register'>('choose');
  let email = $state('');
  let password = $state('');
  let username = $state('');
  let busy = $state(false);
  let error = $state('');

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
  {:else}
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
  {/if}

  {#if error}
    <p class="error">{error}</p>
  {/if}
</div>

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
</style>
