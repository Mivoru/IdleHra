<script lang="ts">
  import { QueryClientProvider } from '@tanstack/svelte-query';
  import Login from './routes/Login.svelte';
  import ChatButton from './lib/ui/ChatButton.svelte';
  import { screenRequest, signOutRequest, publishCurrentScreen } from './lib/stores/navigation';
  import TabBar from './lib/ui/TabBar.svelte';
  import { hotkeyTab, MAIN_TABS } from './lib/ui/tabs';
  import { connectionChip } from './lib/ui/connectionMessage';
  import { refreshUnopenedChests, unopenedChests } from './lib/stores/cosmeticChests';
  import { PREF_LAST_SCREEN, readPrefAs, writePref } from './lib/net/prefs';
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
  import { chatDockOpen, setChatOpen } from './lib/stores/chatDock';
  import { topOverlay } from './lib/stores/sheet';
  import {
    resolveBackPress,
    watchHardwareBack,
    exitApp,
    isCloseOutcome,
    LAYER_Z,
    type BackOutcome,
  } from './lib/net/backButton';
  import Modal from './lib/ui/Modal.svelte';
  import {
    storedToken,
    clearToken,
    storedRefreshToken,
    refreshSession,
    revokeRefreshToken,
  } from './lib/net/auth';
  import { queryClient } from './lib/net/queryClient';
  import { HALT_REASON_SHORT, isAutomationNote } from './lib/ui/slots';
  import { initLanguage, loadTranslations } from './lib/ui/i18n';
  import { unlockAudio, play } from './lib/ui/audio';
  import { startMusic, stopMusic } from './lib/ui/music';
  import { closeProfiles } from './lib/stores/profile';
  import { resolveNotesOnStartup, startUpdatePolling } from './lib/stores/version';
  import { coachTargetScreen, screenLocks } from './lib/stores/tutorial';
  import { bootStage, bootStep, bootDone } from './lib/ui/boot';
  import { tick, untrack, type Component } from 'svelte';

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
  // and the groups are how the game already thinks about itself.
  //
  // Task 95: ONE LIST, TWO FACES. A desktop shows each group as a header
  // dropdown; a phone shows the same groups as tiles in the More sheet, minus
  // the four the tab bar already holds. Regrouped at the same time - "You"
  // had mixed Skill Tree and Progress with Settings and the Wiki, and the
  // Market was reachable only as a tab inside another entry:
  //   Play             - what the character is doing right now
  //   Hero             - the character and what it carries
  //   Make             - what the village builds and breeds
  //   Community        - other players, and the post
  //   Game             - your record, the rules, the settings
  //
  // ONE NAME PER CONCEPT (task 95, owner-approved 2026-10-02). An entry, its
  // tab, its heading, the Wiki and the tutorial say the same word: Home (not
  // Map), Auto-Eat (not Supplies - the first guided step already said "Go to
  // Auto-Eat" and pointed at an entry called something else), Bloodline >
  // Breeding / Ancestors / Inheritance. Community keeps its name (owner, 2026-10-03)
  // - it opens the Friends tab.
  const GROUPS = [
    {
      name: 'Play',
      screens: [
        { key: 'hub', label: 'Home' },
        { key: 'combat', label: 'Combat' },
        { key: 'gathering', label: 'Gathering' },
        { key: 'worldboss', label: 'World Boss' },
        { key: 'delve', label: 'The Delve' },
      ],
    },
    {
      name: 'Hero',
      screens: [
        { key: 'character', label: 'Character' },
        { key: 'skills', label: 'Skill Tree' },
        // Task 54: cosmetic chests, avatars and frames.
        { key: 'wardrobe', label: 'Wardrobe' },
        { key: 'chest', label: 'Chest' },
        // Task 95 renamed the entry from "Supplies" to the word the tutorial
        // uses. It had a Boosts tab until 2026-10-07, when the five
        // consumables it listed were deleted - nothing ever produced one.
        { key: 'larder', label: 'Auto-Eat' },
      ],
    },
    {
      name: 'Make',
      screens: [
        { key: 'village', label: 'Village' },
        { key: 'crafting', label: 'Crafting' },
        { key: 'forge', label: 'Forge' },
        // Task 59: Breeding, the Hall of Ancestors and Inheritance are one
        // family's story - who is born, who is kept through the season, what
        // the line has bought - so they are one "Bloodline" entry, three tabs.
        { key: 'breeding', label: 'Bloodline' },
        { key: 'codex', label: 'Codex' },
      ],
    },
    {
      name: 'Community',
      screens: [
        // Task 76 made Friends, Market, Guild and Leaderboards one family with
        // four tabs. Task 95 gives Guild and Market an entry of their own as
        // well: a player looking for the Market finds the word Market in the
        // menu, not inside an entry with another name.
        { key: 'social', label: 'Community' },
        { key: 'guildops', label: 'Guild' },
        { key: 'market', label: 'Market' },
        // Modul: Mail has its own entry - world boss rewards land there, and
        // a badge on a tab inside another entry is a badge nobody sees. It
        // once belonged NOWHERE: moving it out of Items dropped the entry
        // without adding it back, and the route was reachable only by a
        // cross-screen request.
        { key: 'mailbox', label: 'Mail' },
      ],
    },
    {
      name: 'Game',
      screens: [
        { key: 'progression', label: 'Progress' },
        { key: 'wiki', label: 'Wiki' },
        { key: 'settings', label: 'Settings' },
        { key: 'store', label: 'Store' },
      ],
    },
  ] as const;

  /** The group whose sheet section carries the chat entry (task 95). */
  const CHAT_GROUP = 'Community';

  /** The tab bar's own screens: a phone's sheet does not list them twice. */
  const TAB_BAR_KEYS: ReadonlySet<string> = new Set(MAIN_TABS.map((t) => t.key));

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
  //
  // Task 95: a family has a NAME, shown as the breadcrumb above its tabs
  // ("Bloodline > Ancestors"). Before it, the word Bloodline was on the menu
  // entry and nowhere on the page it opened.
  const TAB_FAMILIES: Record<string, { name: string; tabs: readonly { key: string; label: string }[] }> = {
    breeding: {
      name: 'Bloodline',
      tabs: [
        { key: 'breeding', label: 'Breeding' },
        { key: 'ancestors', label: 'Ancestors' },
        { key: 'inheritance', label: 'Inheritance' },
      ],
    },
    social: {
      name: 'Community',
      tabs: [
        { key: 'social', label: 'Friends' },
        { key: 'market', label: 'Market' },
        { key: 'guildops', label: 'Guild' },
        { key: 'leaderboards', label: 'Leaderboards' },
      ],
    },
  };
  const TAB_ONLY_KEYS = ['ancestors', 'inheritance', 'market', 'guildops', 'leaderboards'] as const;

  type ScreenKey =
    | (typeof GROUPS)[number]['screens'][number]['key']
    | (typeof TAB_ONLY_KEYS)[number];

  /** The family owner a screen's tabs belong to - itself, unless it is a tab. */
  function menuKeyOf(key: string | null): string | null {
    if (key === null) return null;
    for (const [owner, family] of Object.entries(TAB_FAMILIES)) {
      if (family.tabs.some((t) => t.key === key)) return owner;
    }
    return key;
  }

  const MENU_KEYS: ReadonlySet<string> = new Set(GROUPS.flatMap((g) => g.screens.map((s) => s.key)));

  /**
   * The menu ENTRY that stands for a screen: its own entry when it has one
   * (Market and Guild are tabs AND entries since task 95), otherwise its
   * family's. So the menu marks Market, not Friends, while you are trading.
   */
  function entryKeyOf(key: string | null): string | null {
    if (key === null) return null;
    return MENU_KEYS.has(key) ? key : menuKeyOf(key);
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
  // asked to see the map. Login stays static because it is the first thing a
  // stranger is drawn; everything else is a dynamic import() and its own chunk.
  //
  // Modul: HUB IS LAZY TOO (2026-10-09). Static, it dragged HomeCards, the
  // crafting and gathering cards and the 22 KB wiki data into the bundle a
  // stranger downloads to see a login form - PageSpeed counted 80 KiB of that
  // bundle unused on the landing page. It is requested the moment a session
  // exists (below), in parallel with the socket, and the loading screen stays
  // up until the first snapshot - so a signed-in launch still opens on a
  // drawn Hub, not on "Loading...".
  //
  // Resolved components are kept in a $state map rather than behind an
  // {#await}: a second visit then renders synchronously, with no one-frame
  // "Loading" flash between two screens the player has already seen.
  //
  // A failed load is almost always a deploy: the hashed chunk this tab's
  // bundle names no longer exists on the server. That is answered with a
  // reload button, not a blank screen - and not an automatic reload, which
  // would loop if the network, rather than the deploy, is the cause.
  type LazyScreen = ScreenKey;
  const SCREEN_LOADERS: Record<LazyScreen, () => Promise<{ default: Component<any> }>> = {
    hub: () => import('./routes/Hub.svelte'),
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
    ensureScreenLoaded(screen);
  });

  const ActiveScreen = $derived(loadedScreens[screen] ?? null);

  // Modul: THE SIGNED-IN OVERLAYS, ONE CHUNK. Chat, the profile modal, What's
  // New, the loot/victory/death cards and the tutorial surfaces only ever
  // render behind a session, so a stranger on the login form has no use for
  // any of them. All of them read state stores rather than one-shot events,
  // so mounting them a moment after the session starts loses nothing: what
  // arrived in between is still in the store when they subscribe.
  let SignedInOverlays = $state<Component | null>(null);
  let overlaysRequested = false;
  $effect(() => {
    if (!token) return;
    ensureScreenLoaded('hub');
    if (overlaysRequested) return;
    overlaysRequested = true;
    import('./lib/ui/SignedInOverlays.svelte')
      .then((mod) => (SignedInOverlays = mod.default))
      .catch((err) => {
        // A deploy replaced the chunk under this tab. The game still plays;
        // the next navigation's screen load offers the reload.
        console.warn('signed-in overlays failed to load', err);
        overlaysRequested = false;
      });
  });

  let navOpen = $state(false);

  // Modul: TASK 82 - ON A DESKTOP THE HEADER IS FIVE DROPDOWNS, not 24 buttons.
  // The entries stay in the DOM (a phone shows them as the flat menu, and the
  // scripts navigate by data-nav / data-label); CSS hides a closed group's
  // panel above the phone breakpoint. Only one group is open at a time.
  let openGroup = $state('');
  function closeGroups(refocus = false): void {
    const was = openGroup;
    openGroup = '';
    if (refocus && was) {
      document.querySelector<HTMLElement>(`[data-group-toggle="${was}"]`)?.focus();
    }
  }
  function onWindowKey(event: KeyboardEvent): void {
    if (event.key !== 'Escape' || event.defaultPrevented) return;
    if (openGroup) {
      event.preventDefault();
      closeGroups(true);
      return;
    }
    // Modul: ESCAPE IS THE BACK BUTTON'S "CLOSE" HALF. Most modals had no key
    // handler at all - the death and victory cards, What's new, the exit
    // prompt, the profile, the wheel - and the few that had one each decided
    // for themselves which layer Escape meant. It now asks the same resolver
    // the hardware button does, so both close the same (topmost) layer; but
    // only ever to close something - Escape never changes the screen and
    // never asks to quit.
    const outcome = resolveBack();
    if (!isCloseOutcome(outcome)) return;
    event.preventDefault();
    applyBack(outcome);
  }
  function onWindowPointer(event: Event): void {
    if (!openGroup) return;
    const target = event.target as Element | null;
    if (!target?.closest?.('.group')) closeGroups();
  }
  function onGroupFocusOut(event: FocusEvent): void {
    const next = event.relatedTarget as Node | null;
    if (next && !(event.currentTarget as HTMLElement).contains(next)) closeGroups();
  }
  function onToggleKey(event: KeyboardEvent, name: string): void {
    if (event.key !== 'ArrowDown') return;
    event.preventDefault();
    openGroup = name;
    queueMicrotask(() => {
      document
        .querySelector<HTMLElement>(`.group[aria-label="${name}"] .group-buttons button:not(:disabled)`)
        ?.focus();
    });
  }
  function groupHolds(group: { screens: readonly { key: string }[] }, key: string | null): boolean {
    return key !== null && group.screens.some((s) => s.key === key);
  }

  // Task 95: the phone's More sheet is `navOpen` - the same flag the old
  // in-header menu used, so the back button's 'close-nav' still closes it.
  // Opening it closes the chat window: two full-screen layers at once make a
  // back press that seems to do nothing.
  function setNavOpen(open: boolean): void {
    navOpen = open;
    if (open) setChatOpen(false);
  }

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

  // Modul: WHERE EACH SCREEN WAS SCROLLED TO, for back.
  //
  // Changing screens never touched the scroll position, so the document kept
  // the previous screen's scrollY, clamped to the new one's height: a player
  // deep in the Chest who tapped Village landed mid-page, with the header and
  // the sub-tabs above the fold - and back then put them at the top of the
  // Chest rather than where they had been. A first visit hid it, because the
  // "Loading..." stub is short enough to clamp the scroll to 0.
  //
  // Forward is the top of the new screen; back is where you were. Plain Map,
  // not $state, for the same reason as the history beside it. The Wiki's own
  // anchor jumps are inside one screen and never pass through here.
  const scrollPositions = new Map<ScreenKey, number>();

  async function scrollAfterRender(y: number): Promise<void> {
    await tick();
    // A frame later as well: a revisited screen renders synchronously from
    // loadedScreens, but its cached queries can still be filling in its
    // height, and scrollTo clamps to whatever height exists when it runs.
    window.scrollTo(0, y);
    if (y > 0) requestAnimationFrame(() => window.scrollTo(0, y));
  }

  function goTo(next: ScreenKey): void {
    // Modul: the same screen twice is not a step. Both the nav and a
    // cross-screen request can ask for where the player already is, and
    // recording those would make back a no-op the player has to press
    // repeatedly - the single most common way a back stack goes wrong.
    if (next === screen) return;
    scrollPositions.set(screen, window.scrollY);
    screenHistory.push(screen);
    if (screenHistory.length > MAX_SCREEN_HISTORY) screenHistory.shift();
    screen = next;
    void scrollAfterRender(0);
  }
  const activeFamily = $derived(TAB_FAMILIES[menuKeyOf(screen) ?? ''] ?? null);
  const activeTabs = $derived(activeFamily?.tabs ?? null);
  const activeTabLabel = $derived(activeTabs?.find((t) => t.key === screen)?.label ?? '');

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
    // A profile left open would reappear over the next account's first screen.
    closeProfiles();
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
  // WHY THE BIG LAYERS ARE READ INDIVIDUALLY RATHER THAN FROM A REGISTRY.
  // The App-level layers already keep their open state somewhere durable -
  // three stores in stores/game.ts, one in stores/chatDock.ts, and the nav
  // menu which App.svelte owns outright. Registering them as well would be a
  // second copy of state that already exists, kept in step by hand, and this
  // codebase has shipped its worst defects to exactly that shape of
  // duplication.
  //
  // The overlays whose open state lives INSIDE a component - the picker
  // sheet, PlayerProfileModal, the name menu, What's new, the shield wheel -
  // have no durable store to read, so they register a closer on the stack in
  // stores/sheet.ts while they are mounted. That is not a copy: the stack IS
  // the only place App can learn they exist. The profile used to be excluded
  // for exactly that reason (its state is local to the screens that open it),
  // and back walked off the screen under it.
  //
  // Still NOT covered, deliberately: the inline equipment picker on
  // Character. It is not an overlay - it is a disclosure panel in the page
  // flow with its own visible Close button, and consuming a back press for
  // something that obscures nothing would make back feel like it had missed.
  let exitPromptOpen = $state(false);

  function resolveBack(): BackOutcome {
    return resolveBackPress({
      overlayZ: $topOverlay?.z ?? null,
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
  }

  function handleBack(): void {
    applyBack(resolveBack());
  }

  function applyBack(outcome: BackOutcome): void {
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
      case 'close-overlay':
        $topOverlay?.close();
        break;
      case 'close-nav':
        navOpen = false;
        break;
      case 'previous-screen': {
        const previous = screenHistory.pop() ?? 'hub';
        screen = previous;
        void scrollAfterRender(scrollPositions.get(previous) ?? 0);
        break;
      }
      case 'root-screen':
        screen = 'hub';
        void scrollAfterRender(0);
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

  // Modul: THE BOTTOM CHROME STEPS ASIDE WHILE THE PLAYER TYPES.
  //
  // With the soft keyboard up, the APK's viewport shrinks to roughly half a
  // phone, and the 64px tab bar and the onboarding coach stayed pinned above
  // the keyboard inside it. The browser scrolls a focused field into view
  // without knowing about fixed overlays, so a Market price or a guild
  // donation near the bottom of a screen could land exactly behind the tab
  // bar. A class on <html> rather than state: what it hides lives in other
  // components, and the CSS below (scoped :global, phone-only) is the whole
  // effect. focusout always clears it - moving between two fields removes and
  // re-adds it in the same task, so nothing flickers.
  const EDITABLE =
    'input:not([type=checkbox]):not([type=radio]):not([type=range]):not([type=button])' +
    ':not([type=submit]):not([type=reset]):not([type=color]):not([type=file]):not([type=image]),' +
    ' textarea, [contenteditable]:not([contenteditable="false"])';
  $effect(() => {
    const root = document.documentElement;
    const onFocusIn = (event: FocusEvent) => {
      const target = event.target as Element | null;
      if (target?.matches?.(EDITABLE)) root.classList.add('typing');
    };
    const onFocusOut = () => root.classList.remove('typing');
    document.addEventListener('focusin', onFocusIn);
    document.addEventListener('focusout', onFocusOut);
    return () => {
      document.removeEventListener('focusin', onFocusIn);
      document.removeEventListener('focusout', onFocusOut);
      root.classList.remove('typing');
    };
  });

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

  // Modul: the loading screen (index.html #boot) stays up until the first
  // snapshot, so a signed-in launch goes straight from the picture to a
  // populated Hub instead of through an empty one. See lib/ui/boot.ts.
  $effect(() => {
    const stage = bootStage({
      restoring,
      signedIn: token !== null,
      phase: $connectionStatus.phase,
      hasState: snap !== null,
    });
    if (stage === 'done') bootDone();
    else bootStep(stage.percent, stage.message, stage.next);
  });
  // Task 74: the same rule TabBar dots Character with - unspent attribute
  // points or an unopened cosmetic chest.
  const characterWants = $derived((snap ? Number(snap.UnspentAttributePoints) > 0 : false) || $unopenedChests > 0);

  // Surfaced in the header rather than only on the screen that caused it: a
  // halted character earns nothing, and the player may well be looking at the
  // inventory when it happens.
  const haltBadge = $derived(snap ? (HALT_REASON_SHORT[snap.ActivityHaltReason] ?? '') : '');
  // Task 95: the Skill Tree lives in the sheet now, so it carries its own dot.
  const skillsWant = $derived(snap ? Number(snap.AvailableSkillPoints) > 0 : false);

  // Task 95: nothing while connected, one plain word otherwise.
  const phaseChip = $derived(connectionChip($connectionStatus.phase));

  // Modul: THE HEADER'S HEIGHT, PUBLISHED. The header is sticky now (task 95),
  // so anything else that sticks - Combat's status strip, Character's person
  // switcher, the connection notice - has to sit UNDER it, not behind it. The
  // height is measured rather than declared because it is not fixed: an event
  // chip or a halt badge can wrap a desktop header onto a second line.
  let headerEl = $state<HTMLElement | null>(null);
  $effect(() => {
    const el = headerEl;
    if (!el) return;
    const root = document.documentElement;
    const publish = () =>
      root.style.setProperty('--sticky-header-h', `${Math.ceil(el.getBoundingClientRect().height)}px`);
    publish();
    const observer = new ResizeObserver(publish);
    observer.observe(el);
    return () => {
      observer.disconnect();
      root.style.removeProperty('--sticky-header-h');
    };
  });
</script>

<svelte:head>
  <title>FolkIdle</title>
</svelte:head>

<svelte:window onkeydown={onWindowKey} onpointerdown={onWindowPointer} />

<QueryClientProvider client={queryClient}>
  {#if token}
    <!-- Modul: ONE NAV ENTRY, two places (task 95): the desktop header's
         dropdowns and the phone's More sheet both render this, so a lock, a
         badge or a coach-mark cannot be added to one and forgotten in the
         other. -->
    {#snippet navEntry(item: { key: string; label: string }, inSheet: boolean)}
      <!-- Task 60: a screen that is not useful yet is greyed, with what opens
           it beside the label, rather than hidden - the menu says what is
           coming. A link from another screen still opens it. -->
      {@const locked = $screenLocks(item.key)}
      <!-- Modul: THE COACH-MARK. The onboarding panel does not float a bubble
           next to this button, it makes the button itself pulse - same "look
           here", none of the positioning maths that clips at a narrow width. -->
      <button
        class:active={entryKeyOf(screen) === item.key}
        class:coachmark={entryKeyOf($coachTargetScreen) === item.key}
        class:locked={locked !== null}
        class:tile={inSheet}
        disabled={locked !== null}
        aria-current={entryKeyOf(screen) === item.key ? 'page' : undefined}
        title={locked ? `Opens at: ${locked}` : item.key === 'character' && characterWants ? 'Points to spend or a chest to open' : undefined}
        data-nav={item.key}
        data-label={item.label}
        data-locked={locked ?? undefined}
        onclick={() => {
          goTo(item.key as ScreenKey);
          navOpen = false;
          openGroup = '';
        }}
      >
        <span class="entry-label">{item.label}</span>
        {#if locked}<span class="lock-req">{locked}</span>{/if}
        <!-- Only the header's copy chimes when mail arrives; see MailBadge. -->
        {#if item.key === 'mailbox'}<MailBadge quiet={inSheet} />{/if}
        {#if item.key === 'character' && characterWants}<span class="navdot" aria-hidden="true"></span>{/if}
        {#if item.key === 'skills' && skillsWant}<span class="navdot" aria-hidden="true"></span>{/if}
      </button>
    {/snippet}

    <!-- Modul: TASK 95 - A SLIM HEADER THAT STAYS. It was in flow and about
         230px tall on a 390px phone (title, event, wallet, "Live", Sign out,
         and a "Menu · Map" button on a row of its own), and it scrolled away
         with the page - so the menu, the only way to 21 of 26 screens, was at
         the top of whatever the player had scrolled down. Now one row, sticky
         under the status bar: the name, the purse, and a menu button that
         opens the same sheet as the More tab. On a desktop the same row
         carries the grouped dropdowns, the chat entry and Sign out. -->
    <header bind:this={headerEl}>
      <strong class="brand">FolkIdle</strong>

      <nav aria-label="Main menu">
        {#each GROUPS as group}
          <div
            class="group"
            class:open={openGroup === group.name}
            role="group"
            aria-label={group.name}
            onfocusout={onGroupFocusOut}
          >
            <!-- Task 82: the desktop face of the group. aria-label says "menu"
                 so a button named exactly "Village" still means the ENTRY
                 inside it. Task 95: the group you are in is underlined and
                 bold, and a coach-mark is a dot on the toggle - the toggle used
                 to wear the tutorial's pulsing ring while the "you are here"
                 state was a background a shade off the header's own. -->
            <button
              class="group-toggle"
              class:active={groupHolds(group, entryKeyOf(screen))}
              class:coachmark={groupHolds(group, entryKeyOf($coachTargetScreen))}
              aria-haspopup="true"
              aria-expanded={openGroup === group.name}
              aria-label={`${group.name} menu`}
              data-group-toggle={group.name}
              onclick={() => (openGroup = openGroup === group.name ? '' : group.name)}
              onkeydown={(e) => onToggleKey(e, group.name)}
            >
              {group.name}
              <span class="caret" aria-hidden="true">&#9662;</span>
            </button>
            <div class="group-buttons">
              {#each group.screens as item}
                {#if !MENU_HIDDEN.has(item.key)}
                  {@render navEntry(item, false)}
                {/if}
              {/each}
            </div>
          </div>
        {/each}
      </nav>

      <span class="event-slot"><EventBanner /></span>

      {#if haltBadge}
        <!-- Task 85: an order acting (6, 7) still earns; only a stop says it does not. -->
        <span class="halt" title={snap && isAutomationNote(Number(snap.ActivityHaltReason)) ? 'An order you set acted for this character' : 'This character is not earning'}>{haltBadge}</span>
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

      <!-- Task 95: the one chat entry on a desktop. A phone's is in the sheet. -->
      <ChatButton class="chat-head" />

      {#if phaseChip}
        <span class="phase" data-phase={$connectionStatus.phase}>{phaseChip}</span>
      {/if}
      <button class="signout" onclick={signOut}>Sign out</button>

      <!-- Phone only: the same sheet as the More tab, from the top of the
           screen for a player whose thumb is up there. -->
      <button
        class="navtoggle"
        aria-label="Menu"
        aria-expanded={navOpen}
        aria-controls="more-sheet"
        onclick={() => setNavOpen(!navOpen)}
      >
        <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
          <path d="M4 7h16M4 12h16M4 17h16" />
        </svg>
      </button>
    </header>

    <!-- Modul: offline/reconnect UI. This was a one-line banner that printed
         "reconnecting (attempt 4)" and nothing else - proportionate to a
         browser tab, useless to a phone that loses signal several times an
         hour. ConnectionNotice says whose fault it is, that the character is
         still earning, and what the player can do about it, and it clears
         itself when the phase goes back to live. Presentation only: the
         reconnect loop it reports on is untouched. -->
    <ConnectionNotice />

    {#if activeFamily && activeTabs}
      <!-- Modul: TASK 95 - "WHERE AM I" ABOVE EVERY TAB ROW. The family's name
           ("Bloodline") was on the menu entry and nowhere on the page it
           opened, so the page read as "Breeding" and the menu as something
           else. And the tabs are ONE row that scrolls sideways: a guest's
           lock suffixes ("Market Level 10") wrapped them onto a second line. -->
      <div class="family">
        <!-- The family's first tab can share its name (Auto-Eat), and
             "Auto-Eat > Auto-Eat" says nothing twice. -->
        <p class="crumbs">
          <span>{activeFamily.name}</span>
          {#if activeTabLabel && activeTabLabel !== activeFamily.name}
            <span class="crumb-sep" aria-hidden="true">&rsaquo;</span>
            <span aria-current="page">{activeTabLabel}</span>
          {/if}
        </p>
        <div class="screen-tabs" role="tablist" aria-label={activeFamily.name}>
          {#each activeTabs as tab (tab.key)}
            {@const tabLock = $screenLocks(tab.key)}
            <!-- Task 76: a tab carries the lock its screen would have had as a
                 menu entry (Market and Guild at level 10), so the entry itself
                 stays open for the tabs that are. -->
            <button
              role="tab"
              class:active={screen === tab.key}
              class:locked={tabLock !== null}
              aria-selected={screen === tab.key}
              disabled={tabLock !== null && screen !== tab.key}
              title={tabLock ? `Opens at: ${tabLock}` : undefined}
              data-subtab={tab.key}
              data-locked={tabLock ?? undefined}
              onclick={() => goTo(tab.key as ScreenKey)}>{tab.label}{#if tabLock}<span class="lock-req">{tabLock}</span>{/if}</button
            >
          {/each}
        </div>
      </div>
    {/if}

    <!-- Modul: the page's one main landmark, so a screen reader can jump past
         the header to the screen. display: contents keeps it out of the
         layout - every screen was styled as a direct child of this level. -->
    <main class="screen-main">
    {#if screen === 'hub' && ActiveScreen}
      <ActiveScreen onNavigate={(next: ScreenKey) => goTo(next)} />
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
    </main>

    <!-- The overlays only a session can need, loaded with it: see
         SignedInOverlays.svelte and the effect that requests it. -->
    {#if SignedInOverlays}
      <SignedInOverlays />
    {/if}

    {#if navOpen}
      <!-- Modul: TASK 95 - THE MORE SHEET. Every destination the tab bar does
           not hold, grouped, rising from the tab bar the thumb is already on:
           from the bottom of a long Chest, Mail and Settings are two taps away
           and no scrolling. Phone only - a desktop never sets navOpen.
           Task 106: it is a Modal (sheet variant), so Escape, back, the focus
           trap and scroll lock are the shared ones. register={false} because
           resolveBack already reads navOpen ('close-nav'); registering it on
           the overlay stack as well would be a second copy of that truth. The
           app root is inert behind it, tab bar included, so the sheet covers
           the bar and closes from its Close button, the scrim or Escape.
           #more-sheet is the wrapper scripts/screens.mjs looks inside. -->
      <Modal variant="sheet" label="More" register={false} z={LAYER_Z.nav} layout="block" onClose={() => setNavOpen(false)}>
      <div class="more-sheet" id="more-sheet">
        <div class="sheet-head">
          <h2 class="sheet-title">More</h2>
          <button class="sheet-close" onclick={() => setNavOpen(false)}>Close</button>
        </div>
        <div class="sheet-event"><EventBanner /></div>
        {#each GROUPS as group}
          {@const items = group.screens.filter((s) => !MENU_HIDDEN.has(s.key) && !TAB_BAR_KEYS.has(s.key))}
          {#if items.length > 0 || group.name === CHAT_GROUP}
            <section class="sheet-group" aria-label={group.name}>
              <h2 class="group-name">{group.name}</h2>
              <div class="sheet-grid">
                {#each items as item (item.key)}
                  {@render navEntry(item, true)}
                {/each}
                {#if group.name === CHAT_GROUP}
                  <ChatButton class="tile" onactivate={() => (navOpen = false)} />
                {/if}
              </div>
            </section>
          {/if}
        {/each}
      </div>
      </Modal>
    {/if}

    <TabBar
      current={screen}
      moreOpen={navOpen}
      onMore={() => setNavOpen(!navOpen)}
      onNavigate={(next) => {
        // A tab tapped under the open sheet or chat changes the screen, so
        // neither may stay on top of it.
        navOpen = false;
        setChatOpen(false);
        goTo(next as ScreenKey);
      }}
    />
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
    </Modal>
  {/if}
</QueryClientProvider>

<style>
  .screen-main {
    display: contents;
  }

  /* Task 95: the family's name, then the tab you are on, above the tabs. */
  .family {
    padding: 0.6rem 1rem 0;
  }
  .crumbs {
    display: flex;
    align-items: baseline;
    gap: 0.35rem;
    margin: 0 0 0.35rem;
    font-size: var(--fs-xs);
    color: var(--text-dim);
    text-transform: uppercase;
    letter-spacing: 0.06em;
  }
  .crumbs [aria-current='page'] {
    color: var(--text);
  }
  /* Modul: ONE ROW THAT SCROLLS SIDEWAYS (task 95). It wrapped, and a guest's
     lock suffixes ("Market Level 10", "Guild Level 10") pushed Leaderboards
     onto a second row of 44px buttons - 52px of chrome before the screen. A
     tab may not shrink: the touch floor is 44px and a squeezed tab is a
     clipped label. */
  .screen-tabs {
    display: flex;
    flex-wrap: nowrap;
    gap: 0.5rem;
    overflow-x: auto;
    scrollbar-width: none;
  }
  .screen-tabs button {
    flex-shrink: 0;
    min-height: 44px;
    padding: 0.4rem 0.9rem;
    border-radius: var(--radius);
    border: 1px solid var(--border);
    background: var(--bg-panel);
    color: inherit;
    font: inherit;
    white-space: nowrap;
    cursor: pointer;
  }
  .screen-tabs button.active {
    border-color: var(--accent);
    color: var(--accent);
    font-weight: 700;
  }
  nav button.locked,
  .screen-tabs button.locked,
  .more-sheet button.locked {
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

  /* Modul: STICKY, under the status bar (task 95). `top: var(--sa-top)`, not
     0: a sticky offset is measured from the scrollport edge, which on an
     edge-to-edge phone is under the clock - the reason ConnectionNotice gives.
     The shadow paints the header's own colour up over that strip, so a page
     scrolling under it does not show through behind the status icons.
     --z-nav like the tab bar: above page content and the screens' own sticky
     strips (30), below the cards (60). Its height is published as
     --sticky-header-h (script), which those strips add to their own top. */
  header {
    position: sticky;
    top: var(--sa-top);
    z-index: var(--z-nav);
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.5rem 1rem;
    background: var(--bg-panel);
    border-bottom: 1px solid var(--border);
    box-shadow: 0 calc(-1 * var(--sa-top)) 0 var(--bg-panel);
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

  /* Task 95: a label, not a control - but a READABLE one. It was 0.6rem at 65%
     opacity, which is under the size the rest of the client calls small. */
  .group-name {
    margin: 0;
    font-size: var(--fs-xs);
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    color: var(--text-dim);
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

  /* The ≡ and the More sheet are the phone's; a desktop header has room for
     the groups themselves. */
  .navtoggle {
    display: none;
  }

  /* Task 82: above the phone breakpoint a group is a dropdown. */
  @media (min-width: 40.01rem) {
    .group {
      position: relative;
    }
    .group-name {
      display: none;
    }
    .group-toggle {
      position: relative;
      border-color: var(--border);
      color: var(--text);
    }
    /* Modul: "YOU ARE HERE" HAS TO OUTRANK "LOOK HERE" (task 95). The active
       group was a background one shade off the header's own, while the
       tutorial's coach-mark was a pulsing accent ring - so the header pointed
       at Items while the player stood in Community. Now the group you are in
       is bold with an accent underline, and the coach-mark on a toggle is a
       small dot. The ring stays on the ENTRY inside an open dropdown, where it
       is the only thing marked. */
    .group-toggle.active {
      background: var(--bg-raised);
      font-weight: 700;
      box-shadow: inset 0 -2px 0 var(--accent);
    }
    .group-toggle.coachmark {
      outline: none;
      animation: none;
    }
    .group-toggle.coachmark::after {
      content: '';
      position: absolute;
      top: 3px;
      right: 3px;
      width: 0.4rem;
      height: 0.4rem;
      border-radius: 50%;
      background: var(--accent);
      animation: coachdot 1.6s ease-in-out infinite;
    }
    .group-toggle[aria-expanded='true'] {
      border-color: var(--accent);
    }
    .caret {
      font-size: 0.7em;
    }
    .group:not(.open) .group-buttons {
      display: none;
    }
    .group.open .group-buttons {
      position: absolute;
      top: calc(100% + 0.2rem);
      left: 0;
      z-index: 60;
      flex-direction: column;
      min-width: 11rem;
      padding: 0.3rem;
      gap: 0.15rem;
      background: var(--bg-panel);
      border: 1px solid var(--border);
      border-radius: var(--radius);
      box-shadow: 0 6px 18px rgba(0, 0, 0, 0.45);
    }
    .group.open .group-buttons button {
      justify-content: flex-start;
      text-align: left;
    }
  }

  @keyframes coachdot {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.25; }
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
    /* Modul: ONE ROW ON A PHONE (task 95). It measured about 230px of an
       844px phone: the title, an event banner on its own row, the wallet,
       "Live" and Sign out, and the menu on a row of its own. Now the name, the
       purse and ≡. The event chip moved into the More sheet, the chat entry
       too, Sign out lives in Settings, and the connection says something only
       when it is not connected (ConnectionNotice explains). */
    header {
      flex-wrap: nowrap;
      gap: 0.5rem;
      padding: 0.25rem 0.5rem 0.25rem 0.75rem;
    }

    nav,
    .event-slot,
    .signout,
    header :global(.chat-head) {
      display: none;
    }

    .halt {
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .wallet {
      flex-shrink: 0;
    }

    /* Modul: 44px stated here. This is the single most-tapped control on a
       phone and it was 37px on all twenty-six screens: a scoped component
       class outranks the global touch floor in app.css, so the number has to
       be here. */
    .navtoggle {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      flex-shrink: 0;
      min-width: 44px;
      min-height: 44px;
      padding: 0;
      width: auto;
    }

    .navtoggle[aria-expanded='true'] {
      border-color: var(--accent);
      color: var(--accent);
    }

    /* Modul: THE MORE SHEET (task 95) is a Modal sheet since task 106: the
       scrim, the docking, the safe-area insets and the dvh cap are Modal's.
       What is left is the layout of what is inside. */
    .more-sheet {
      display: grid;
      align-content: start;
      gap: 0.85rem;
    }

    .sheet-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 0.5rem;
    }

    .sheet-title {
      margin: 0;
      font-size: var(--fs-md);
    }

    .sheet-close {
      min-height: 44px;
      min-width: 44px;
    }

    .sheet-event:empty {
      display: none;
    }

    .sheet-group {
      display: grid;
      gap: 0.4rem;
    }

    .sheet-grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 0.4rem;
    }

    .more-sheet button.tile,
    .more-sheet :global(.chat-entry.tile) {
      display: flex;
      align-items: center;
      gap: 0.35rem;
      flex-wrap: wrap;
      width: 100%;
      min-height: 44px;
      padding: 0.45rem 0.7rem;
      text-align: left;
      justify-content: flex-start;
      background: var(--bg-raised);
      border: 1px solid var(--border);
      color: var(--text);
    }

    .more-sheet button.tile.active {
      border-color: var(--accent);
      color: var(--accent);
      font-weight: 700;
    }

    .more-sheet button.tile.coachmark {
      outline: 2px solid var(--accent);
      outline-offset: 1px;
      animation: coachpulse 1.6s ease-in-out infinite;
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

  /* The chat entry is ChatButton's own element, so the scoped class needs
     :global to reach it. */
  header :global(.chat-head) {
    font-size: 0.8rem;
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
    nav button.coachmark,
    .more-sheet button.tile.coachmark,
    .group-toggle.coachmark::after {
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

  /* Task 95: focus-scrolling and anchor jumps stop under the sticky header
     rather than behind it. */
  :global(html) {
    scroll-padding-top: calc(var(--sa-top) + var(--sticky-header-h));
  }

  /* Modul: WHILE TYPING (html.typing, set in the script), on a phone: the tab
     bar and the coach go, and --tabbar-h drops to 0 so everything that stands
     on the bar - the chat dock, its window's height, the toasts - comes down
     with it. The chat window stays: it is often where the player is typing.
     scroll-padding-bottom tells focus-scrolling about whatever fixed chrome
     is left, which it otherwise cannot know about. Here and not in app.css
     because the class belongs to this file's focus listener. */
  @media (max-width: 40rem) {
    :global(html) {
      scroll-padding-bottom: calc(var(--tabbar-h) + var(--sa-bottom) + 1rem);
    }
    :global(html.typing) {
      --tabbar-h: 0px;
    }
    :global(html.typing .tabbar),
    :global(html.typing .coach) {
      display: none;
    }
  }
</style>
