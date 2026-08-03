<script lang="ts">
  import { QueryClientProvider } from '@tanstack/svelte-query';
  import Login from './routes/Login.svelte';
  import Combat from './routes/Combat.svelte';
  import Gathering from './routes/Gathering.svelte';
  import Character from './routes/Character.svelte';
  import Larder from './routes/Larder.svelte';
  import Market from './routes/Market.svelte';
  import Crafting from './routes/Crafting.svelte';
  import Forge from './routes/Forge.svelte';
  import Chat from './routes/Chat.svelte';
  import Social from './routes/Social.svelte';
  import GuildOps from './routes/GuildOps.svelte';
  import Progression from './routes/Progression.svelte';
  import Village from './routes/Village.svelte';
  import Codex from './routes/Codex.svelte';
  import Breeding from './routes/Breeding.svelte';
  import Store from './routes/Store.svelte';
  import Settings from './routes/Settings.svelte';
  import Mailbox from './routes/Mailbox.svelte';
  import Chest from './routes/Chest.svelte';
  import WorldBoss from './routes/WorldBoss.svelte';
  import Boosts from './routes/Boosts.svelte';
  import OfflineSummary from './lib/ui/OfflineSummary.svelte';
  import Toasts from './lib/ui/Toasts.svelte';
  import MailBadge from './lib/ui/MailBadge.svelte';
  import EventBanner from './lib/ui/EventBanner.svelte';
  import Money from './lib/ui/Money.svelte';
  import { startSession, endSession, connectionStatus, playerState } from './lib/stores/game';
  import { storedToken, clearToken } from './lib/net/auth';
  import { queryClient } from './lib/net/queryClient';
  import { HALT_REASON_SHORT } from './lib/ui/slots';
  import { initLanguage, loadTranslations } from './lib/ui/i18n';
  import { unlockAudio, play } from './lib/ui/audio';
  import { tutorialStep, currentPrompt, TutorialStep } from './lib/stores/tutorial';

  initLanguage();
  void loadTranslations();

  // 49 screens are modal panels, not URLs, so this is a screen store rather
  // than a router - closer to the existing design and one dependency fewer.
  let token = $state<string | null>(storedToken());

  // Modul: grouped rather than a flat row. Twenty-one destinations in one line
  // wrapped into an unscannable block on any window narrower than a desktop,
  // and the groups are how the game already thinks about itself - what you do,
  // what you own, who you do it with, and what you have achieved.
  const GROUPS = [
    {
      name: 'Play',
      screens: [
        { key: 'combat', label: 'Combat' },
        { key: 'gathering', label: 'Gathering' },
        { key: 'worldboss', label: 'World Boss' },
        { key: 'boosts', label: 'Boosts' },
      ],
    },
    {
      name: 'Items',
      screens: [
        { key: 'character', label: 'Character' },
        { key: 'chest', label: 'Chest' },
        { key: 'larder', label: 'Auto-Eat' },
        { key: 'crafting', label: 'Crafting' },
        { key: 'forge', label: 'Forge' },
        { key: 'mailbox', label: 'Mail' },
      ],
    },
    {
      name: 'Others',
      screens: [
        { key: 'market', label: 'Market' },
        { key: 'chat', label: 'Chat' },
        { key: 'social', label: 'Social' },
        { key: 'guildops', label: 'Guild' },
      ],
    },
    {
      name: 'You',
      screens: [
        { key: 'village', label: 'Village' },
        { key: 'progression', label: 'Progress' },
        { key: 'codex', label: 'Codex' },
        { key: 'breeding', label: 'Breeding' },
        { key: 'store', label: 'Store' },
        { key: 'settings', label: 'Settings' },
      ],
    },
  ] as const;

  type ScreenKey = (typeof GROUPS)[number]['screens'][number]['key'];
  let screen = $state<ScreenKey>('combat');

  $effect(() => {
    if (token) {
      startSession(token);
      return () => endSession();
    }
  });

  // Browsers refuse to start an AudioContext before a user gesture, so the
  // first click anywhere arms it. Registered once and then left alone - a
  // context that never got a gesture plays nothing and says nothing.
  function armAudioOnFirstGesture() {
    unlockAudio();
    play('buttonClick');
    window.removeEventListener('pointerdown', armAudioOnFirstGesture);
  }
  $effect(() => {
    window.addEventListener('pointerdown', armAudioOnFirstGesture);
    return () => window.removeEventListener('pointerdown', armAudioOnFirstGesture);
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
        {#each GROUPS as group}
          <div class="group" role="group" aria-label={group.name}>
            <span class="group-name">{group.name}</span>
            <div class="group-buttons">
              {#each group.screens as item}
                <button class:active={screen === item.key} onclick={() => (screen = item.key)}>
                  {item.label}
                  {#if item.key === 'mailbox'}<MailBadge />{/if}
                </button>
              {/each}
            </div>
          </div>
        {/each}
      </nav>

      <EventBanner />

      {#if haltBadge}
        <span class="halt" title="This character is not earning">{haltBadge}</span>
      {/if}

      {#if snap}
        <!-- Diamonds are PremiumCurrencyBalance on the hot path; the REST
             statistics snapshot calls the same number PremiumDiamonds. Two
             names for one balance, and only this one is live. -->
        <span class="wallet">
          <Money amount={snap.Gold} icon />
          {#if Number(snap.PremiumCurrencyBalance) > 0}
            <Money amount={snap.PremiumCurrencyBalance} kind="diamond" icon />
          {/if}
        </span>
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
    {:else if screen === 'larder'}
      <Larder />
    {:else if screen === 'crafting'}
      <Crafting />
    {:else if screen === 'forge'}
      <Forge />
    {:else if screen === 'market'}
      <Market />
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
    {:else if screen === 'breeding'}
      <Breeding />
    {:else if screen === 'store'}
      <Store />
    {:else if screen === 'settings'}
      <Settings />
    {:else if screen === 'mailbox'}
      <Mailbox />
    {:else if screen === 'chest'}
      <Chest />
    {:else if screen === 'worldboss'}
      <WorldBoss />
    {:else if screen === 'boosts'}
      <Boosts />
    {/if}

    {#if $tutorialStep > TutorialStep.Inactive && $tutorialStep < TutorialStep.Completed}
      <div class="tutorial" role="status">
        <strong>Step {$tutorialStep} of 3</strong>
        <span>{currentPrompt()}</span>
        <button onclick={() => (screen = 'settings')}>Skip</button>
      </div>
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
    gap: 0.9rem;
    flex-wrap: wrap;
  }

  .group {
    display: grid;
    gap: 0.1rem;
  }

  /* The group name is a label, not a control - small, quiet, and skippable
     once the player knows where things live. */
  .group-name {
    font-size: 0.6rem;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    color: var(--text-dim);
    opacity: 0.65;
    padding-left: 0.15rem;
  }

  .group-buttons {
    display: flex;
    gap: 0.2rem;
  }

  nav button {
    display: inline-flex;
    align-items: center;
    gap: 0.3rem;
    background: transparent;
    border-color: transparent;
    padding: 0.3rem 0.6rem;
    font-size: 0.83rem;
    color: var(--text-dim);
  }

  nav button.active {
    background: var(--bg-raised);
    border-color: var(--border);
    color: var(--text);
  }

  @media (max-width: 52rem) {
    nav {
      gap: 0.5rem;
      width: 100%;
    }
    .group-buttons {
      flex-wrap: wrap;
    }
  }

  .halt {
    font-size: 0.75rem;
    color: var(--danger);
    border: 1px solid var(--danger);
    border-radius: 999px;
    padding: 0.1rem 0.5rem;
  }

  .wallet {
    margin-left: auto;
    display: inline-flex;
    align-items: baseline;
    gap: 0.6rem;
    font-size: 0.85rem;
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

  .tutorial {
    position: fixed;
    left: 50%;
    bottom: 1rem;
    transform: translateX(-50%);
    display: flex;
    align-items: center;
    gap: 0.7rem;
    padding: 0.55rem 0.9rem;
    background: var(--bg-raised);
    border: 1px solid var(--accent);
    border-radius: 999px;
    font-size: 0.85rem;
    box-shadow: 0 6px 18px rgba(0, 0, 0, 0.35);
    z-index: 40;
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
