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
  import Leaderboards from './routes/Leaderboards.svelte';
  import Village from './routes/Village.svelte';
  import Codex from './routes/Codex.svelte';
  import Breeding from './routes/Breeding.svelte';
  import Ancestors from './routes/Ancestors.svelte';
  import Store from './routes/Store.svelte';
  import Delve from './routes/Delve.svelte';
  import SkillsPanel from './lib/ui/SkillsPanel.svelte';
  import Inheritance from './routes/Inheritance.svelte';
  import Settings from './routes/Settings.svelte';
  import Mailbox from './routes/Mailbox.svelte';
  import Chest from './routes/Chest.svelte';
  import WorldBoss from './routes/WorldBoss.svelte';
  import Boosts from './routes/Boosts.svelte';
  import Wiki from './routes/Wiki.svelte';
  import OfflineSummary from './lib/ui/OfflineSummary.svelte';
  import VictoryCard from './lib/ui/VictoryCard.svelte';
  import DeathCard from './lib/ui/DeathCard.svelte';
  import Toasts from './lib/ui/Toasts.svelte';
  import AchievementToast from './lib/ui/AchievementToast.svelte';
  import MailBadge from './lib/ui/MailBadge.svelte';
  import EventBanner from './lib/ui/EventBanner.svelte';
  import Money from './lib/ui/Money.svelte';
  import ConnectionNotice from './lib/ui/ConnectionNotice.svelte';
  import {
    startSession,
    endSession,
    connectionStatus,
    playerState,
    offlineSummary,
    dismissOfflineSummary,
    victorySummary,
    dismissVictory,
    deathSummary,
    dismissDeath,
  } from './lib/stores/game';
  import { chatDockOpen } from './lib/stores/chatDock';
  import { resolveBackPress, watchHardwareBack, exitApp } from './lib/net/backButton';
  import {
    storedToken,
    clearToken,
    storedRefreshToken,
    refreshSession,
    revokeRefreshToken,
  } from './lib/net/auth';
  import { queryClient } from './lib/net/queryClient';
  import { HALT_REASON_SHORT } from './lib/ui/slots';
  import { initLanguage, loadTranslations } from './lib/ui/i18n';
  import { unlockAudio, play } from './lib/ui/audio';
  import OnboardingCoach from './lib/ui/OnboardingCoach.svelte';
  import WhatsNew from './lib/ui/WhatsNew.svelte';
  import { resolveNotesOnStartup, startUpdatePolling } from './lib/stores/version';
  import { coachTargetScreen } from './lib/stores/tutorial';
  import { untrack } from 'svelte';

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
        { key: 'delve', label: 'The Delve' },
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
      name: 'Community',
      screens: [
        { key: 'market', label: 'Market' },
        { key: 'social', label: 'Friends' },
        { key: 'guildops', label: 'Guild' },
        // Modul: Mail belongs here, not under Items - and for a while it
        // belonged NOWHERE. Moving it out of Items dropped the entry without
        // adding it back, which left the route and its unread badge reachable
        // only by a cross-screen navigation request. There was no button.
        { key: 'mailbox', label: 'Mail' },
        { key: 'leaderboards', label: 'Leaderboards' },
      ],
    },
    {
      name: 'Genetics',
      screens: [
        { key: 'breeding', label: 'Breeding' },
        { key: 'ancestors', label: 'Ancestors' },
        { key: 'inheritance', label: 'Inheritance' },
      ],
    },
    {
      name: 'You',
      screens: [
        { key: 'village', label: 'Village' },
        { key: 'skills', label: 'Skill Tree' },
        { key: 'progression', label: 'Progress' },
        { key: 'codex', label: 'Codex' },
        { key: 'store', label: 'Store' },
        { key: 'settings', label: 'Settings' },
      ],
    },
    {
      name: 'Others',
      screens: [
        { key: 'wiki', label: 'Wiki' },
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

  // Modul: THE ROUTE THE PLAYER TOOK, kept so the Android back button has
  // something to walk. See lib/net/backButton.ts for why back needed to stop
  // meaning "quit".
  //
  // A plain array rather than $state: nothing in the markup renders it, and a
  // reactive proxy for a value only one function reads would put a dependency
  // on every screen change for no benefit. Bounded because an idle session is
  // measured in hours and a player who taps between two screens for an
  // afternoon should not grow an unbounded list to prove it.
  const MAX_SCREEN_HISTORY = 24;
  let screenHistory: ScreenKey[] = [];

  function goTo(next: ScreenKey): void {
    // Modul: the same screen twice is not a step. Both the nav and a
    // cross-screen request can ask for where the player already is, and
    // recording those would make back a no-op the player has to press
    // repeatedly - the single most common way a back stack goes wrong.
    if (next === screen) return;
    screenHistory.push(screen);
    if (screenHistory.length > MAX_SCREEN_HISTORY) screenHistory.shift();
    screen = next;
  }
  // Modul: flattened through an explicit type. `GROUPS` is a readonly tuple OF
  // readonly tuples, and flatMap over that infers the union of the tuples
  // themselves rather than of their elements - so `item` came out as unknown
  // and `item.label` did not typecheck. Naming the element type is the whole
  // fix; the runtime behaviour never changed.
  const ALL_SCREENS: readonly { key: ScreenKey; label: string }[] = GROUPS.flatMap(
    (group) => group.screens as readonly { key: ScreenKey; label: string }[],
  );
  const currentScreenLabel = $derived(
    ALL_SCREENS.find((item) => item.key === screen)?.label ?? 'Menu',
  );

  $effect(() => {
    const request = $screenRequest;
    if (request && ALL_SCREEN_KEYS.has(request.screen)) {
      // untrack because goTo READS `screen` to record it. Without this the
      // effect would take a dependency on the very value it writes and re-run
      // itself on every navigation.
      untrack(() => goTo(request.screen as ScreenKey));
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
    // Modul: the refresh token is worth sixty days, so signing out has to end
    // it on the SERVER as well as here. Deliberately not awaited - the local
    // half above is what the player asked for and it has already happened.
    revokeRefreshToken();
    // The next session starts at the map, and it starts with nothing behind
    // it - a back stack left over from the previous account would walk the new
    // player through screens they never opened.
    screen = 'hub';
    screenHistory = [];
    navOpen = false;
    // The cache is per-account. Leaving it populated would show the previous
    // player's inventory to the next one for as long as it stayed fresh.
    queryClient.clear();
    token = null;
  }

  // Modul: THE ANDROID BACK BUTTON USED TO QUIT THE GAME FROM ANY SCREEN.
  //
  // Registering a listener for it is what disables Capacitor's own default, so
  // from here on this handler owes the player an exit - which is what the
  // confirmation below is for. The ORDER of the checks is the paint order of
  // the layers and lives in backButton.ts, as a pure function, so it can be
  // tested without a device; this is only the half that has to touch component
  // state.
  //
  // WHY THE LAYERS ARE READ INDIVIDUALLY RATHER THAN FROM A REGISTRY. Every
  // dismissable layer in this client already keeps its open state somewhere
  // durable - three stores in stores/game.ts, one in stores/chatDock.ts, and
  // the nav menu which App.svelte owns outright. A registry would be a fourth
  // copy of state that already exists, kept in step by hand, and this codebase
  // has shipped its worst defects to exactly that shape of duplication.
  //
  // NOT covered, deliberately: PlayerProfileModal (opened from Friends and
  // chat) and the inline equipment picker on Character. The first is a real
  // overlay whose open state is local to two routes; the second is not an
  // overlay at all - it is a disclosure panel in the page flow with its own
  // visible Close button, and consuming a back press for something that
  // obscures nothing would make back feel like it had missed.
  let exitPromptOpen = $state(false);

  function handleBack(): void {
    const outcome = resolveBackPress({
      exitPromptOpen,
      deathCardOpen: $deathSummary !== null,
      victoryCardOpen: $victorySummary !== null,
      offlineSummaryOpen: $offlineSummary !== null,
      chatDockOpen: $chatDockOpen,
      navOpen,
      historyDepth: token ? screenHistory.length : 0,
      // The login form is a root too: there is nothing behind it to go back to.
      atRoot: !token || screen === 'hub',
    });

    switch (outcome) {
      case 'close-exit-prompt':
        exitPromptOpen = false;
        break;
      case 'close-death-card':
        dismissDeath();
        break;
      case 'close-victory-card':
        dismissVictory();
        break;
      case 'close-offline-summary':
        dismissOfflineSummary();
        break;
      case 'close-chat-dock':
        chatDockOpen.set(false);
        break;
      case 'close-nav':
        navOpen = false;
        break;
      case 'previous-screen':
        screen = screenHistory.pop() ?? 'hub';
        break;
      case 'root-screen':
        screen = 'hub';
        break;
      case 'confirm-exit':
        exitPromptOpen = true;
        break;
    }
  }

  // No dependencies: the handler reads current state when it fires rather than
  // closing over a snapshot, so this attaches once and stays attached. A
  // no-op in a browser, where there is no such button.
  $effect(() => watchHardwareBack(handleBack));

  // Modul: what build this is, and what the player has not seen yet. Runs once
  // and needs no dependencies - the version is a compile-time constant and the
  // stored one is read at the moment this fires. A brand-new player is recorded
  // silently and shown nothing; see resolveNotesOnStartup.
  $effect(() => {
    resolveNotesOnStartup();
    startUpdatePolling();
  });

  // Modul: A REJECTED TOKEN HAS TO REACH THE LOGIN FORM.
  //
  // The socket used to retry an expired JWT forever behind "reconnecting
  // (attempt 5)", which can never succeed and hides the only action that
  // would work. connection.ts reports 'signedout' for exactly that case;
  // this is what turns it into the login screen.
  // Modul: ONE REFRESH ATTEMPT BEFORE THE LOGIN FORM, and only one.
  //
  // 'signedout' means the server rejected this JWT, which after a long
  // suspend usually means nothing worse than "it expired while the phone was
  // in a pocket" - the exact case the refresh token exists for. Retrying the
  // refresh would be a loop, because a refused refresh token is discarded by
  // `refreshSession` and the second attempt has nothing to send; the guard
  // below is what keeps one rejection from starting a second attempt while
  // the first is still in the air.
  let refreshInFlight = false;

  $effect(() => {
    if ($connectionStatus.phase !== 'signedout' || !token || refreshInFlight) return;

    refreshInFlight = true;
    void refreshSession()
      .then((session) => {
        if (session) {
          // Restarts the socket: the session effect above depends on `token`.
          token = session.token;
        } else {
          signOut();
        }
      })
      .finally(() => {
        refreshInFlight = false;
      });
  });

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
                <!-- Modul: THE COACH-MARK. The onboarding panel does not float a
                     bubble next to this button, it makes the button itself
                     pulse - same "look here", none of the positioning maths
                     that clips at a narrow width. -->
                <button
                  class:active={screen === item.key}
                  class:coachmark={$coachTargetScreen === item.key}
                  data-nav={item.key}
                  onclick={() => {
                    goTo(item.key);
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

    <!-- Modul: offline/reconnect UI. This was a one-line banner that printed
         "reconnecting (attempt 4)" and nothing else - proportionate to a
         browser tab, useless to a phone that loses signal several times an
         hour. ConnectionNotice says whose fault it is, that the character is
         still earning, and what the player can do about it, and it clears
         itself when the phase goes back to live. Presentation only: the
         reconnect loop it reports on is untouched. -->
    <ConnectionNotice />

    {#if screen === 'hub'}
      <Hub onNavigate={(next) => goTo(next)} />
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
    {:else if screen === 'ancestors'}
      <Ancestors />
    {:else if screen === 'inheritance'}
      <Inheritance />
    {:else if screen === 'delve'}
      <Delve />
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
    {:else if screen === 'leaderboards'}
      <Leaderboards />
    {:else if screen === 'wiki'}
      <Wiki />
    {/if}

    <!-- Modul: A BANNER THAT DOES SOMETHING.
         The old one printed "Step 1 of 3" and a sentence, and its only button
         went to Settings to turn itself off - so the one action it offered was
         to make it go away. It names the step, says WHY the step matters, and
         its main button takes the player to the screen where the thing is
         done. Pointing is the whole job.
         It is now ONE surface for both onboarding tiers - the three
         first-session steps and the seventeen discovery moments - because a
         second, differently-shaped hint box would teach the player that hints
         come in kinds. -->
    <OnboardingCoach />

    <!-- Modul: what changed since the player was last here, and whether the
         bundle this tab is running has been replaced since it loaded. Both
         live in one component - see WhatsNew.svelte for why the second waits
         for the first. -->
    <WhatsNew />

    <OfflineSummary />
    <!-- Modul: the two moments the game never marked - a first boss
         clear and a death. Both are modal because both are things the
         player must not miss while looking at another screen. -->
    <VictoryCard />
    <DeathCard />
    <ChatDock />
    <Toasts />
    <AchievementToast />
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
       Rendered outside the signed-in branch for that reason. -->
  {#if exitPromptOpen}
    <div class="exitbackdrop" role="dialog" aria-modal="true" aria-label="Leave FolkIdle">
      <div class="exitcard">
        <h2>Leave FolkIdle?</h2>
        <p>
          Your character keeps playing while the app is closed, and you are paid
          for the time when you come back.
        </p>
        <div class="exitrow">
          <button class="stay" onclick={() => (exitPromptOpen = false)}>Stay</button>
          <button
            class="leave"
            onclick={() => {
              exitPromptOpen = false;
              exitApp();
            }}>Leave</button
          >
        </div>
      </div>
    </div>
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
      /* Modul: 44px, not 2.2rem. This is the single most-tapped control on a
         phone - it is how every screen is reached - and it was 37px on all
         twenty-six of them. A scoped component class outranks the global touch
         floor in app.css, so the floor could not reach it; the number has to
         be here. */
      min-height: 44px;
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

  /* Modul: the coach-mark on the real control. Outline rather than a border
     or a size change, so nothing in the nav reflows while it pulses - a
     tutorial that moves the button it is pointing at is worse than none.
     Reduced-motion drops the animation and keeps the ring, matching how the
     rest of the client treats that setting. */
  nav button.coachmark {
    outline: 2px solid var(--accent);
    outline-offset: 1px;
    color: var(--text);
    animation: coachpulse 1.6s ease-in-out infinite;
  }

  @keyframes coachpulse {
    0%, 100% { outline-color: var(--accent); }
    50% { outline-color: transparent; }
  }

  @media (prefers-reduced-motion: reduce) {
    nav button.coachmark {
      animation: none;
    }
  }

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

  .exitbackdrop {
    position: fixed;
    inset: 0;
    z-index: 1100;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 1rem;
    background: rgba(0, 0, 0, 0.62);
  }

  .exitcard {
    width: min(22rem, 100%);
    padding: 1rem;
    border: 1px solid var(--border);
    border-radius: var(--radius, 8px);
    background: var(--bg-panel);
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
