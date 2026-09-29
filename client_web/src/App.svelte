<script lang="ts">
  import { QueryClientProvider } from '@tanstack/svelte-query';
  import Login from './routes/Login.svelte';
  import ChatDock from './lib/ui/ChatDock.svelte';
  import { chatHandleInHeader } from './lib/stores/chatDock';
  import { screenRequest, signOutRequest, publishCurrentScreen } from './lib/stores/navigation';
  import Hub from './routes/Hub.svelte';
  import OfflineSummary from './lib/ui/OfflineSummary.svelte';
  import VictoryCard from './lib/ui/VictoryCard.svelte';
  import TabBar from './lib/ui/TabBar.svelte';
  import { hotkeyTab } from './lib/ui/tabs';
  import { refreshUnopenedChests, unopenedChests } from './lib/stores/cosmeticChests';
  import { PREF_LAST_SCREEN, readPrefAs, writePref } from './lib/net/prefs';
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
    forgetSession,
    victorySummary,
    dismissVictory,
    deathSummary,
    dismissDeath,
  } from './lib/stores/game';
  import { chatDockOpen } from './lib/stores/chatDock';
  import { openSheetCloser } from './lib/stores/sheet';
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
  import { startMusic, stopMusic } from './lib/ui/music';
  import OnboardingCoach from './lib/ui/OnboardingCoach.svelte';
  import GuidedOverlay from './lib/ui/GuidedOverlay.svelte';
  import LootReveal from './lib/ui/LootReveal.svelte';
  import WhatsNew from './lib/ui/WhatsNew.svelte';
  import { resolveNotesOnStartup, startUpdatePolling } from './lib/stores/version';
  import { coachTargetScreen, screenLocks } from './lib/stores/tutorial';
  import { untrack, type Component } from 'svelte';

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
        { key: 'delve', label: 'The Delve' },
      ],
    },
    {
      name: 'Items',
      screens: [
        { key: 'character', label: 'Character' },
        // Task 54: cosmetic chests, avatars and frames.
        { key: 'wardrobe', label: 'Wardrobe' },
        { key: 'chest', label: 'Chest' },
        // Task 59: Auto-Eat and Boosts are one "Supplies" entry with two tabs
        // - both are what you take into a fight, and Boosts was one small
        // panel once the chrono bank went. See TAB_FAMILIES.
        { key: 'larder', label: 'Supplies' },
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
      name: 'You',
      screens: [
        { key: 'village', label: 'Village' },
        // Task 59: Breeding, the Hall of Ancestors and Inheritance were a
        // three-entry "Genetics" group. They are one family's story - who is
        // born, who is kept through the season, what the line has bought -
        // so they are one "Bloodline" entry with three tabs.
        { key: 'breeding', label: 'Bloodline' },
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

  // Modul: TASK 71 - A MENU ENTRY THAT LEADS TO "NOTHING HERE YET" IS NOT AN
  // ENTRY. The Store has no product (owner, 2026-09-28: simulation speed and
  // the diamond packs removed, Stripe parked), so its button only ever opened
  // an empty page. The screen, its key and its route stay - the purchase path
  // is intact for when payments are set up - and the day it sells something,
  // it comes off this list.
  const MENU_HIDDEN: ReadonlySet<string> = new Set(['store']);

  // Modul: SCREENS REACHED THROUGH A TAB, not a menu entry (task 59). Each
  // keeps its own key, so everything that names a screen - the tutorial's
  // coach-marks, the guided first minute's 'larder' step, requestScreen() from
  // a death card, the back stack, the remembered last screen - still works
  // unchanged. Only the menu folds them together.
  const TAB_FAMILIES: Record<string, readonly { key: string; label: string }[]> = {
    larder: [
      { key: 'larder', label: 'Auto-Eat' },
      { key: 'boosts', label: 'Boosts' },
    ],
    breeding: [
      { key: 'breeding', label: 'Breeding' },
      { key: 'ancestors', label: 'Ancestors' },
      { key: 'inheritance', label: 'Inheritance' },
    ],
  };
  const TAB_ONLY_KEYS = ['boosts', 'ancestors', 'inheritance'] as const;

  type ScreenKey =
    | (typeof GROUPS)[number]['screens'][number]['key']
    | (typeof TAB_ONLY_KEYS)[number];

  /** The menu entry a screen lives under - itself, unless it is a tab. */
  function menuKeyOf(key: string | null): string | null {
    if (key === null) return null;
    for (const [owner, tabs] of Object.entries(TAB_FAMILIES)) {
      if (tabs.some((t) => t.key === key)) return owner;
    }
    return key;
  }
  // Modul: the map is where a session starts. Signing in used to drop the
  // player straight onto Combat with a wall of nav words above it; the painted
  // valley is both prettier and a better answer to "where am I".
  //
  // Task 52: ...unless the player was somewhere else when they left. The last
  // screen is remembered on this device and reopened, because a player who
  // lives on Combat does not want the map between them and it every visit.
  // Signing out writes 'hub', so the next account on the device starts at
  // the map as before.
  let screen = $state<ScreenKey>(
    readPrefAs(
      PREF_LAST_SCREEN,
      (v): v is ScreenKey =>
        GROUPS.some((g) => g.screens.some((s) => s.key === v)) ||
        (TAB_ONLY_KEYS as readonly string[]).includes(v),
      'hub' as ScreenKey,
    ),
  );
  $effect(() => writePref(PREF_LAST_SCREEN, screen));

  // Task 52: desktop hotkeys 1-5 for the five tab-bar screens (see tabs.ts
  // for when a key is left alone).
  function onHotkey(event: KeyboardEvent): void {
    if (!token) return;
    const next = hotkeyTab(event);
    if (next && ALL_SCREEN_KEYS.has(next)) {
      event.preventDefault();
      goTo(next as ScreenKey);
    }
  }
  $effect(() => {
    window.addEventListener('keydown', onHotkey);
    return () => window.removeEventListener('keydown', onHotkey);
  });

  // Modul: cross-screen links. A screen that is not Hub has no way to change
  // `screen` - it is local state and only Hub is handed a setter - so the
  // Chest's "Reroll" button publishes a request instead. See
  // stores/navigation.ts for why it carries a nonce.
  const ALL_SCREEN_KEYS = new Set<string>([
    ...GROUPS.flatMap((group) => group.screens.map((s) => s.key)),
    ...TAB_ONLY_KEYS,
  ]);

  // Modul: EVERY SCREEN BUT THE MAP IS LOADED ON FIRST VISIT.
  //
  // All 26 were static imports, so the first paint waited on one ~660 KB chunk
  // holding the Wiki, the Forge and the guild war UI for a player who had only
  // asked to see the map. Login and Hub stay static because one of them is
  // always the first thing drawn; everything else is a dynamic import() and
  // its own chunk.
  //
  // Resolved components are kept in a $state map rather than behind an
  // {#await}: a second visit then renders synchronously, with no one-frame
  // "Loading" flash between two screens the player has already seen.
  //
  // A failed load is almost always a deploy: the hashed chunk this tab's
  // bundle names no longer exists on the server. That is answered with a
  // reload button, not a blank screen - and not an automatic reload, which
  // would loop if the network, rather than the deploy, is the cause.
  type LazyScreen = Exclude<ScreenKey, 'hub'>;
  const SCREEN_LOADERS: Record<LazyScreen, () => Promise<{ default: Component<any> }>> = {
    combat: () => import('./routes/Combat.svelte'),
    gathering: () => import('./routes/Gathering.svelte'),
    character: () => import('./routes/Character.svelte'),
    wardrobe: () => import('./routes/Wardrobe.svelte'),
    larder: () => import('./routes/Larder.svelte'),
    crafting: () => import('./routes/Crafting.svelte'),
    forge: () => import('./routes/Forge.svelte'),
    market: () => import('./routes/Market.svelte'),
    social: () => import('./routes/Social.svelte'),
    guildops: () => import('./routes/GuildOps.svelte'),
    village: () => import('./routes/Village.svelte'),
    progression: () => import('./routes/Progression.svelte'),
    codex: () => import('./routes/Codex.svelte'),
    breeding: () => import('./routes/Breeding.svelte'),
    ancestors: () => import('./routes/Ancestors.svelte'),
    inheritance: () => import('./routes/Inheritance.svelte'),
    delve: () => import('./routes/Delve.svelte'),
    store: () => import('./routes/Store.svelte'),
    settings: () => import('./routes/Settings.svelte'),
    mailbox: () => import('./routes/Mailbox.svelte'),
    chest: () => import('./routes/Chest.svelte'),
    worldboss: () => import('./routes/WorldBoss.svelte'),
    boosts: () => import('./routes/Boosts.svelte'),
    leaderboards: () => import('./routes/Leaderboards.svelte'),
    wiki: () => import('./routes/Wiki.svelte'),
    // The skill tree has its own screen now. It lived inside the character
    // sheet, wedged between the paper doll and the stat block, where it was
    // both cramped and in the way of the thing that screen is actually for.
    skills: () => import('./lib/ui/SkillsPanel.svelte'),
  };

  let loadedScreens = $state<Partial<Record<LazyScreen, Component<any>>>>({});
  let failedScreen = $state<LazyScreen | null>(null);
  const pendingLoads = new Set<LazyScreen>();

  function ensureScreenLoaded(key: LazyScreen): void {
    if (loadedScreens[key] || pendingLoads.has(key)) return;
    pendingLoads.add(key);
    SCREEN_LOADERS[key]()
      .then((mod) => {
        loadedScreens[key] = mod.default;
        if (failedScreen === key) failedScreen = null;
      })
      .catch((err) => {
        console.warn(`screen ${key} failed to load`, err);
        failedScreen = key;
      })
      .finally(() => pendingLoads.delete(key));
  }

  $effect(() => {
    if (screen !== 'hub') ensureScreenLoaded(screen);
  });

  const ActiveScreen = $derived(screen === 'hub' ? null : (loadedScreens[screen] ?? null));

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
    ALL_SCREENS.find((item) => item.key === menuKeyOf(screen))?.label ?? 'Menu',
  );

  const activeTabs = $derived(TAB_FAMILIES[menuKeyOf(screen) ?? ''] ?? null);

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
    publishCurrentScreen(screen);
  });

  // Settings' Sign out (see requestSignOut). The first value is the store's
  // initial 0, which is not a request.
  $effect(() => {
    if ($signOutRequest > 0) untrack(() => signOut());
  });

  $effect(() => {
    if (token) {
      startSession(token);
      // Task 54: the level-chest backfill lands at login; count what waits.
      refreshUnopenedChests();
      return () => endSession();
    }
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

  function signOut() {
    stopMusic();
    endSession();
    forgetSession();
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
      sheetOpen: $openSheetCloser !== null,
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
      case 'close-sheet':
        $openSheetCloser?.();
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
  // Task 74: the same rule TabBar dots Character with - unspent attribute
  // points or an unopened cosmetic chest.
  const characterWants = $derived((snap ? Number(snap.UnspentAttributePoints) > 0 : false) || $unopenedChests > 0);

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
                <!-- Task 60: a screen that is not useful yet is greyed, with
                     what opens it beside the label, rather than hidden - the
                     menu says what is coming. A link from another screen
                     still opens it; this only declutters the menu. -->
                {@const locked = $screenLocks(item.key)}
                {#if !MENU_HIDDEN.has(item.key)}
                <button
                  class:active={menuKeyOf(screen) === item.key}
                  class:coachmark={menuKeyOf($coachTargetScreen) === item.key}
                  class:locked={locked !== null}
                  disabled={locked !== null}
                  title={locked ? `Opens at: ${locked}` : item.key === 'character' && characterWants ? 'Points to spend or a chest to open' : undefined}
                  data-nav={item.key}
                  data-label={item.label}
                  data-locked={locked ?? undefined}
                  onclick={() => {
                    goTo(item.key);
                    navOpen = false;
                  }}
                >
                  {item.label}
                  {#if locked}<span class="lock-req">{locked}</span>{/if}
                  {#if item.key === 'mailbox'}<MailBadge />{/if}
                  {#if item.key === 'character' && characterWants}<span class="navdot" aria-hidden="true"></span>{/if}
                </button>
                {/if}
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

      {#if $chatHandleInHeader}
        <!-- Task 71: the chat's quiet home. See stores/chatDock.ts. -->
        <button class="chat-head" onclick={() => chatDockOpen.set(true)} aria-label="Show chat, nobody online">
          Chat
        </button>
      {/if}

      <span class="phase" data-phase={$connectionStatus.phase}>
        {$connectionStatus.phase}{$connectionStatus.attempt > 0
          ? ` (retry ${$connectionStatus.attempt})`
          : ''}
      </span>
      <button class="signout" onclick={signOut}>Sign out</button>
    </header>

    <!-- Modul: offline/reconnect UI. This was a one-line banner that printed
         "reconnecting (attempt 4)" and nothing else - proportionate to a
         browser tab, useless to a phone that loses signal several times an
         hour. ConnectionNotice says whose fault it is, that the character is
         still earning, and what the player can do about it, and it clears
         itself when the phase goes back to live. Presentation only: the
         reconnect loop it reports on is untouched. -->
    <ConnectionNotice />

    {#if activeTabs}
      <div class="screen-tabs" role="tablist" aria-label={currentScreenLabel}>
        {#each activeTabs as tab (tab.key)}
          <button
            role="tab"
            class:active={screen === tab.key}
            aria-selected={screen === tab.key}
            data-subtab={tab.key}
            onclick={() => goTo(tab.key as ScreenKey)}>{tab.label}</button
          >
        {/each}
      </div>
    {/if}

    {#if screen === 'hub'}
      <Hub onNavigate={(next) => goTo(next)} />
    {:else if ActiveScreen}
      <ActiveScreen />
    {:else if failedScreen === screen}
      <section class="panel screen-load-failed">
        <p>This screen could not be loaded - the game has probably been updated since this tab opened.</p>
        <button onclick={() => location.reload()}>Reload</button>
      </section>
    {:else}
      <p class="dim screen-loading">Loading...</p>
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
    <GuidedOverlay />

    <!-- Modul: what changed since the player was last here, and whether the
         bundle this tab is running has been replaced since it loaded. Both
         live in one component - see WhatsNew.svelte for why the second waits
         for the first. -->
    <WhatsNew />

    <OfflineSummary />
    <!-- Modul: the two moments the game never marked - a first boss
         clear and a death. Both are modal because both are things the
         player must not miss while looking at another screen. -->
    <!-- Task 50: a Legendary+ drop, shown over any screen for a moment. -->
    <LootReveal />
    <VictoryCard />
    <DeathCard />
    <ChatDock />
    <TabBar current={screen} onNavigate={(next) => goTo(next as ScreenKey)} />
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
  .screen-tabs {
    display: flex;
    gap: 0.5rem;
    padding: 0.75rem 1rem 0;
    flex-wrap: wrap;
  }
  .screen-tabs button {
    min-height: 44px;
    padding: 0.4rem 0.9rem;
    border-radius: var(--radius);
    border: 1px solid var(--border);
    background: var(--bg-panel);
    color: inherit;
    font: inherit;
    cursor: pointer;
  }
  .screen-tabs button.active {
    border-color: var(--accent);
    color: var(--accent);
    font-weight: 700;
  }
  nav button.locked {
    opacity: 0.55;
    cursor: not-allowed;
  }
  /* Task 74: the phone's tab bar already dots Character (TabBar.svelte); the
     desktop header said nothing about 24 unspent points. Same rule, same dot. */
  .navdot {
    display: inline-block;
    width: 0.45rem;
    height: 0.45rem;
    margin-left: 0.3rem;
    border-radius: 50%;
    background: var(--danger);
    vertical-align: 0.15em;
  }

  .lock-req {
    margin-left: 0.35rem;
    font-size: 0.72em;
    opacity: 0.9;
  }

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

    /* Modul: THE HEADER GIVES THE SCREEN BACK. At 390px it measured about
       230px of an 844px phone: the title, an event banner on its own row,
       the wallet, the word "Live" and Sign out on a third, and the menu on a
       fourth. Now: title and wallet on one row, the event chip and the menu
       on the next. Sign out lives in Settings on a phone, and the connection
       state only shows when it is NOT live - "Live" is the normal case and
       ConnectionNotice already speaks up when it is not. */
    .signout,
    .phase[data-phase='live'] {
      display: none;
    }

    .wallet {
      order: 0;
    }

    /* The event chip, the halt badge and the quiet chat button (order 1)
       sit left of the menu. */
    .halt,
    .chat-head {
      order: 1;
    }

    .navtoggle {
      order: 2;
    }

    nav.open {
      order: 3;
      width: 100%;
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

  .chat-head {
    font-size: 0.8rem;
    width: auto;
    opacity: 0.8;
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
