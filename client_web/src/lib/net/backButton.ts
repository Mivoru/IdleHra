// Modul: THE HARDWARE BACK BUTTON QUIT THE GAME FROM EVERY SCREEN.
//
// Android's back button is not a browser control - it is the OS-level "undo
// the last thing that happened to my screen", and every app on the platform
// answers it. This client answered it with the Capacitor default, which is to
// leave: a player two taps deep in the Chest, or looking at a death card,
// pressed the button their thumb has pressed a thousand times and the game
// closed. An idle game survives that (the simulation is on the server), but it
// reads as a crash, and a store reviewer reads it as a bug.
//
// WHY A LISTENER AT ALL, AND WHAT REGISTERING ONE COSTS.
//
// Capacitor's App plugin dispatches `backButton` to JavaScript and DISABLES
// its own default handling for as long as a listener exists. That is the whole
// mechanism: from the moment `watchHardwareBack` attaches, nothing else will
// ever close the app, so this module owes the player an exit path. That is
// what `exitApp` is for, and why the resolver below ends at `confirm-exit`
// rather than at nothing.
//
// WHY THE PLUGIN IS READ THE WAY lifecycle.ts READS IT.
//
// `@capacitor/app` is not a dependency of the web build and the plugin object
// is whatever the native shell injected at startup. Importing it would make
// every browser build carry - and fail on - a native-only module. So it is
// read off the global, every access is optional, and every call is wrapped:
// a missing or throwing bridge degrades to "the back button does what it did
// before", never to a blank page. `npm run check` cannot see any of this, so
// the guard has to be written rather than typed.

type Unsubscribe = () => void;

interface CapacitorAppPlugin {
  addListener?: (
    event: 'backButton',
    handler: (state: { canGoBack: boolean }) => void,
  ) => { remove?: () => void } | Promise<{ remove?: () => void }>;
  exitApp?: () => void | Promise<void>;
}

function nativeApp(): CapacitorAppPlugin | undefined {
  const capacitor = (
    globalThis as { Capacitor?: { Plugins?: { App?: CapacitorAppPlugin } } }
  ).Capacitor;
  return capacitor?.Plugins?.App;
}

/**
 * What one press of the back button should do, given what is on screen.
 *
 * Modul: A PURE FUNCTION, so the decision can be tested without a device.
 *
 * The ordering below is not arbitrary and it is not alphabetical - it is the
 * PAINT ORDER of the layers, topmost first, because "back" means "close the
 * thing I am looking at" and the thing a player is looking at is whichever
 * layer is on top. The z-indexes it mirrors live in the components: the exit
 * prompt (1100) over the death and victory cards (60, death last in the DOM
 * and therefore above victory), over the offline summary (50), over the chat
 * dock (40), over the collapsed nav menu.
 *
 * Get this order wrong and back appears to skip a layer: it would close
 * something behind whatever is covering the screen, and the player would see
 * nothing happen at all.
 */
export interface BackPressState {
  /** The "leave the game?" dialog this module's own last press opened. */
  exitPromptOpen: boolean;
  deathCardOpen: boolean;
  victoryCardOpen: boolean;
  offlineSummaryOpen: boolean;
  chatDockOpen: boolean;
  /** The phone's collapsed navigation menu, which covers the screen it is on. */
  navOpen: boolean;
  /** How many screens are behind this one. */
  historyDepth: number;
  /** True on the map, and true on the login screen - both are "the bottom". */
  atRoot: boolean;
}

export type BackOutcome =
  | 'close-exit-prompt'
  | 'close-death-card'
  | 'close-victory-card'
  | 'close-offline-summary'
  | 'close-chat-dock'
  | 'close-nav'
  | 'previous-screen'
  | 'root-screen'
  | 'confirm-exit';

export function resolveBackPress(state: BackPressState): BackOutcome {
  if (state.exitPromptOpen) return 'close-exit-prompt';
  if (state.deathCardOpen) return 'close-death-card';
  if (state.victoryCardOpen) return 'close-victory-card';
  if (state.offlineSummaryOpen) return 'close-offline-summary';
  if (state.chatDockOpen) return 'close-chat-dock';
  if (state.navOpen) return 'close-nav';

  // Modul: the history is walked before the "go to the map" fallback, and both
  // exist. History is what a player means by back - it retraces the route they
  // took. But the route can be empty while the screen is not the map: a
  // cross-screen request that arrived before any navigation, or a session
  // restored onto a screen nobody walked to. Falling through to the map keeps
  // the promise that back never exits from a nested screen, which is the whole
  // acceptance criterion.
  if (state.historyDepth > 0) return 'previous-screen';
  if (!state.atRoot) return 'root-screen';

  return 'confirm-exit';
}

/**
 * Attaches to the native back button. Returns an unsubscribe.
 *
 * Does nothing at all in a browser, where there is no such button - and
 * deliberately does NOT fall back to `popstate`. This client has no router and
 * pushes no history entries, so a browser back press means "leave the site",
 * which is a decision the browser already handles correctly.
 */
export function watchHardwareBack(onBack: () => void): Unsubscribe {
  const app = nativeApp();
  if (typeof app?.addListener !== 'function') return () => {};

  let handle: { remove?: () => void } | null = null;
  let stopped = false;

  try {
    const result = app.addListener('backButton', () => onBack());
    Promise.resolve(result)
      .then((h) => {
        // Modul: the unsubscribe can lose the race with the attach, because
        // addListener may resolve asynchronously. Without this flag a
        // component that mounted and unmounted quickly would leave a live
        // listener behind - and a stale listener holding a stale closure is
        // exactly how back starts closing a card that is no longer there.
        if (stopped) h?.remove?.();
        else handle = h;
      })
      .catch(() => {
        // A listener that could not be attached leaves Capacitor's default
        // behaviour in place. That is the bug this module fixes, not a new
        // one, and it is far better than a client that will not start.
      });
  } catch {
    // Same reasoning. Never let the native path take the web build down.
  }

  return () => {
    stopped = true;
    try {
      handle?.remove?.();
    } catch {
      // Nothing useful to do; the shell is going away with the listener.
    }
    handle = null;
  };
}

/**
 * Closes the app.
 *
 * Only reachable from the exit confirmation, and only meaningful on native -
 * a browser tab is closed by the browser. Silent where there is no bridge,
 * because the alternative is an error dialog on a button whose entire job is
 * to make something go away.
 */
export function exitApp(): void {
  try {
    void nativeApp()?.exitApp?.();
  } catch {
    // See above.
  }
}
