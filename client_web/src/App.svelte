<script lang="ts">
  import { QueryClientProvider } from '@tanstack/svelte-query';
  import Login from './routes/Login.svelte';
  import Combat from './routes/Combat.svelte';
  import Gathering from './routes/Gathering.svelte';
  import Character from './routes/Character.svelte';
  import Inventory from './routes/Inventory.svelte';
  import Larder from './routes/Larder.svelte';
  import Market from './routes/Market.svelte';
  import Bank from './routes/Bank.svelte';
  import Crafting from './routes/Crafting.svelte';
  import Forge from './routes/Forge.svelte';
  import Chat from './routes/Chat.svelte';
  import Social from './routes/Social.svelte';
  import GuildOps from './routes/GuildOps.svelte';
  import Progression from './routes/Progression.svelte';
  import Village from './routes/Village.svelte';
  import Codex from './routes/Codex.svelte';
  import OfflineSummary from './lib/ui/OfflineSummary.svelte';
  import Toasts from './lib/ui/Toasts.svelte';
  import { startSession, endSession, connectionStatus, playerState } from './lib/stores/game';
  import { storedToken, clearToken } from './lib/net/auth';
  import { queryClient } from './lib/net/queryClient';
  import { HALT_REASON_SHORT } from './lib/ui/slots';

  // 49 screens are modal panels, not URLs, so this is a screen store rather
  // than a router - closer to the existing design and one dependency fewer.
  let token = $state<string | null>(storedToken());

  const SCREENS = [
    { key: 'combat', label: 'Combat' },
    { key: 'gathering', label: 'Gathering' },
    { key: 'character', label: 'Character' },
    { key: 'inventory', label: 'Inventory' },
    { key: 'larder', label: 'Larder' },
    { key: 'crafting', label: 'Crafting' },
    { key: 'forge', label: 'Forge' },
    { key: 'market', label: 'Market' },
    { key: 'bank', label: 'Bank' },
    { key: 'chat', label: 'Chat' },
    { key: 'social', label: 'Social' },
    { key: 'guildops', label: 'War & Raid' },
    { key: 'village', label: 'Village' },
    { key: 'progression', label: 'Progress' },
    { key: 'codex', label: 'Codex' },
  ] as const;

  type ScreenKey = (typeof SCREENS)[number]['key'];
  let screen = $state<ScreenKey>('combat');

  $effect(() => {
    if (token) {
      startSession(token);
      return () => endSession();
    }
  });

  function signOut() {
    endSession();
    clearToken();
    // The cache is per-account. Leaving it populated would show the previous
    // player's inventory to the next one for as long as it stayed fresh.
    queryClient.clear();
    token = null;
  }

  const snap = $derived($playerState);

  // Surfaced in the header rather than only on the screen that caused it: a
  // halted character earns nothing, and the player may well be looking at the
  // inventory when it happens.
  const haltBadge = $derived(snap ? (HALT_REASON_SHORT[snap.ActivityHaltReason] ?? '') : '');
</script>

<svelte:head>
  <title>FolkIdle</title>
</svelte:head>

<QueryClientProvider client={queryClient}>
  {#if token}
    <header>
      <strong>FolkIdle</strong>

      <nav>
        {#each SCREENS as item}
          <button class:active={screen === item.key} onclick={() => (screen = item.key)}>
            {item.label}
          </button>
        {/each}
      </nav>

      {#if haltBadge}
        <span class="halt" title="This character is not earning">{haltBadge}</span>
      {/if}

      {#if snap}
        <span class="money" title="Gold">{Number(snap.Gold).toLocaleString()}g</span>
      {/if}

      <span class="phase" data-phase={$connectionStatus.phase}>
        {$connectionStatus.phase}{$connectionStatus.attempt > 0
          ? ` (retry ${$connectionStatus.attempt})`
          : ''}
      </span>
      <button onclick={signOut}>Sign out</button>
    </header>

    <!-- Modul: offline/reconnect UI (port plan 4d). Unity showed connection
         state weakly, and a browser tab can be frozen by the OS mid-session,
         so the player is told plainly rather than left watching a frozen
         screen. -->
    {#if $connectionStatus.phase === 'reconnecting'}
      <div class="banner">
        Connection lost - reconnecting (attempt {$connectionStatus.attempt}).
        {#if $connectionStatus.detail}<br /><span class="detail">{$connectionStatus.detail}</span>{/if}
      </div>
    {/if}

    {#if screen === 'combat'}
      <Combat />
    {:else if screen === 'gathering'}
      <Gathering />
    {:else if screen === 'character'}
      <Character />
    {:else if screen === 'inventory'}
      <Inventory />
    {:else if screen === 'larder'}
      <Larder />
    {:else if screen === 'crafting'}
      <Crafting />
    {:else if screen === 'forge'}
      <Forge />
    {:else if screen === 'market'}
      <Market />
    {:else if screen === 'bank'}
      <Bank />
    {:else if screen === 'chat'}
      <Chat />
    {:else if screen === 'social'}
      <Social />
    {:else if screen === 'guildops'}
      <GuildOps />
    {:else if screen === 'village'}
      <Village />
    {:else if screen === 'progression'}
      <Progression />
    {:else if screen === 'codex'}
      <Codex />
    {/if}

    <OfflineSummary />
    <Toasts />
  {:else}
    <Login onAuthenticated={(newToken) => (token = newToken)} />
  {/if}
</QueryClientProvider>

<style>
  header {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.5rem 1rem;
    background: var(--bg-panel);
    border-bottom: 1px solid var(--border);
    flex-wrap: wrap;
  }

  header strong {
    letter-spacing: 0.04em;
  }

  nav {
    display: flex;
    gap: 0.25rem;
  }

  nav button {
    background: transparent;
    border-color: transparent;
    padding: 0.35rem 0.7rem;
    font-size: 0.85rem;
    color: var(--text-dim);
  }

  nav button.active {
    background: var(--bg-raised);
    border-color: var(--border);
    color: var(--text);
  }

  .halt {
    font-size: 0.75rem;
    color: var(--danger);
    border: 1px solid var(--danger);
    border-radius: 999px;
    padding: 0.1rem 0.5rem;
  }

  .money {
    margin-left: auto;
    font-size: 0.85rem;
    font-variant-numeric: tabular-nums;
    color: var(--text-dim);
  }

  .phase {
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
