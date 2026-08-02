<script lang="ts">
  import Login from './routes/Login.svelte';
  import Combat from './routes/Combat.svelte';
  import { startSession, endSession, connectionStatus } from './lib/stores/game';
  import { storedToken, clearToken } from './lib/net/auth';

  // 49 screens are modal panels, not URLs, so this is a screen store rather
  // than a router - closer to the existing design and one dependency fewer.
  let token = $state<string | null>(storedToken());

  $effect(() => {
    if (token) {
      startSession(token);
      return () => endSession();
    }
  });

  function signOut() {
    endSession();
    clearToken();
    token = null;
  }
</script>

<svelte:head>
  <title>FolkIdle</title>
</svelte:head>

{#if token}
  <header>
    <strong>FolkIdle</strong>
    <span class="phase" data-phase={$connectionStatus.phase}>
      {$connectionStatus.phase}{$connectionStatus.attempt > 0 ? ` (retry ${$connectionStatus.attempt})` : ''}
    </span>
    <button onclick={signOut}>Sign out</button>
  </header>

  <!-- Modul: offline/reconnect UI (port plan 4d). Unity showed connection
       state weakly, and a browser tab can be frozen by the OS mid-session, so
       the player is told plainly rather than left watching a frozen screen. -->
  {#if $connectionStatus.phase === 'reconnecting'}
    <div class="banner">
      Connection lost - reconnecting (attempt {$connectionStatus.attempt}).
      {#if $connectionStatus.detail}<br /><span class="detail">{$connectionStatus.detail}</span>{/if}
    </div>
  {/if}

  <Combat />
{:else}
  <Login onAuthenticated={(newToken) => (token = newToken)} />
{/if}

<style>
  header {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.6rem 1rem;
    background: var(--bg-panel);
    border-bottom: 1px solid var(--border);
  }

  header strong {
    letter-spacing: 0.04em;
  }

  .phase {
    margin-left: auto;
    font-size: 0.8rem;
    color: var(--text-dim);
    text-transform: capitalize;
  }

  .phase[data-phase='live'] {
    color: var(--good);
  }

  .phase[data-phase='reconnecting'],
  .phase[data-phase='failed'] {
    color: var(--danger);
  }

  .banner {
    padding: 0.6rem 1rem;
    background: rgba(224, 85, 63, 0.15);
    border-bottom: 1px solid var(--danger);
    font-size: 0.85rem;
  }

  .detail {
    color: var(--text-dim);
  }
</style>
