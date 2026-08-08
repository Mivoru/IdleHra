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
  import ChatDock from './lib/ui/ChatDock.svelte';
  import { screenRequest } from './lib/stores/navigation';
  import Hub from './routes/Hub.svelte';
  import Social from './routes/Social.svelte';
  import GuildOps from './routes/GuildOps.svelte';
  import Progression from './routes/Progression.svelte';
  import Village from './routes/Village.svelte';
  import Codex from './routes/Codex.svelte';
  import Breeding from './routes/Breeding.svelte';
  import Store from './routes/Store.svelte';
  import SkillsPanel from './lib/ui/SkillsPanel.svelte';
  import Inheritance from './routes/Inheritance.svelte';
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
  import { tutorialPrompt, skipTutorial } from './lib/stores/tutorial';

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
        { key: 'hub', label: 'Map' },
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
      ],
    },
    {
      name: 'Others',
      screens: [
        { key: 'market', label: 'Market' },
        { key: 'social', label: 'Social' },
        { key: 'guildops', label: 'Guild' },
      ],
    },
    {
      name: 'You',
      screens: [
        { key: 'village', label: 'Village' },
        { key: 'skills', label: 'Skill Tree' },
        { key: 'progression', label: 'Progress' },
        { key: 'inheritance', label: 'Inheritance' },
        { key: 'codex', label: 'Codex' },
        { key: 'breeding', label: 'Breeding' },
        { key: 'store', label: 'Store' },
        { key: 'settings', label: 'Settings' },
      ],
    },
  ] as const;

  type ScreenKey = (typeof GROUPS)[number]['screens'][number]['key'];
  // Modul: the map is where a session starts. Signing in used to drop the
  // player straight onto Combat with a wall of nav words above it; the painted
  // valley is both prettier and a better answer to "where am I".
  let screen = $state<ScreenKey>('hub');

  // Modul: cross-screen links. A screen that is not Hub has no way to change
  // `screen` - it is local state and only Hub is handed a setter - so the
  // Chest's "Reroll" button publishes a request instead. See
  // stores/navigation.ts for why it carries a nonce.
  const ALL_SCREEN_KEYS = new Set<string>(GROUPS.flatMap((group) => group.screens.map((s) => s.key)));

  let navOpen = $state(false);
  const currentScreenLabel = $derived(
    GROUPS.flatMap((group) => group.screens).find((item) => item.key === screen)?.label ?? 'Menu',
  );

  $effect(() => {
    const request = $screenRequest;
    if (request && ALL_SCREEN_KEYS.has(request.screen)) {
      screen = request.screen as ScreenKey;
    }
  });

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

      <!-- Modul: ON A PHONE THE NAV IS A MENU, not a wall.
           Twenty-two destinations in four labelled groups is a good desktop
           header and it filled the whole first screen of a 360px phone - the
           map, which is the screen it was sitting on top of, started below the
           fold. A player opening the game saw a list of links and had to
           scroll to reach the game.
           Collapsed by default on narrow screens, showing where you are; the
           full grouping is intact once opened, and choosing anything closes
           it again. -->
      <button
        class="navtoggle"
        aria-expanded={navOpen}
        onclick={() => (navOpen = !navOpen)}
      >
        {navOpen ? 'Close' : 'Menu'} &middot; {currentScreenLabel}
      </button>

      <nav class:open={navOpen}>
        {#each GROUPS as group}
          <div class="group" role="group" aria-label={group.name}>
            <span class="group-name">{group.name}</span>
            <div class="group-buttons">
              {#each group.screens as item}
                <button
                  class:active={screen === item.key}
                  onclick={() => {
                    screen = item.key;
                    navOpen = false;
                  }}
                >
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
          <!-- Modul: shown at zero too. It used to be hidden below one, which
               is precisely when a player goes looking for it - an empty purse
               that renders as nothing reads as a missing feature. -->
          <Money amount={snap.PremiumCurrencyBalance} kind="diamond" icon />
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

    {#if screen === 'hub'}
      <Hub onNavigate={(next) => (screen = next)} />
    {:else if screen === 'combat'}
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
    {:else if screen === 'social'}
      <Social />
    {:else if screen === 'guildops'}
      <GuildOps />
    {:else if screen === 'village'}
      <Village />
    {:else if screen === 'skills'}
      <!-- Modul: the skill tree has its own screen now. It lived inside the
           character sheet, wedged between the paper doll and the stat block,
           where it was both cramped and in the way of the thing that screen is
           actually for. -->
      <SkillsPanel />
    {:else if screen === 'progression'}
      <Progression />
    {:else if screen === 'codex'}
      <Codex />
    {:else if screen === 'breeding'}
      <Breeding />
    {:else if screen === 'inheritance'}
      <Inheritance />
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

    <!-- Modul: A BANNER THAT DOES SOMETHING.
         The old one printed "Step 1 of 3" and a sentence, and its only button
         went to Settings to turn itself off - so the one action it offered was
         to make it go away. It now names the step, says WHY the step matters,
         and its main button takes the player to the screen where the thing is
         done. Pointing is the whole job. -->
    {#if $tutorialPrompt}
      <div class="tutorial" role="status">
        <strong>{$tutorialPrompt.index} / {$tutorialPrompt.total} &middot; {$tutorialPrompt.title}</strong>
        <span>{$tutorialPrompt.body}</span>
        <button class="gilded" onclick={() => (screen = $tutorialPrompt.screen)}>Take me there</button>
        <button onclick={skipTutorial}>Skip</button>
      </div>
    {/if}

    <OfflineSummary />
    <ChatDock />
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

  /* The toggle only exists on narrow screens - a desktop header has room for
     the whole nav and hiding it there would be a step backwards. */
  .navtoggle {
    display: none;
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

  @media (max-width: 40rem) {
    /* Modul: pushed to its own line. Inline beside the title it drew over
       "FolkIdle" once the label grew - a header that overlaps itself. */
    .navtoggle {
      display: inline-flex;
      align-items: center;
      gap: 0.3rem;
      min-height: 2.2rem;
      order: 1;
      margin-left: auto;
    }

    nav {
      display: none;
    }

    nav.open {
      display: flex;
      flex-direction: column;
      gap: 0.4rem;
    }

    nav button {
      min-height: 2.2rem;
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
