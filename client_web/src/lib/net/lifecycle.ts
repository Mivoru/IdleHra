// Modul: THE APP COMING BACK IS AN EVENT, AND NOTHING WAS LISTENING FOR IT.
//
// The reconnect loop in connection.ts is good at what it was written for - a
// server that went away - and blind to the case that dominates on a phone: the
// OS froze the WebView, the socket died in the player's pocket, and the player
// is now looking at the screen expecting the game to be there.
//
// Left alone, that resolves in up to fifteen seconds of exponential backoff,
// or never at all if the socket is a zombie that still reports OPEN. Both read
// as a broken app, and both are invisible in a browser tab, which is where all
// the testing happens.
//
// WHY TWO SOURCES OF THE SAME EVENT.
//
// `visibilitychange` is the web answer and fires in a Capacitor WebView too,
// but it is not reliable on iOS when the app is suspended rather than merely
// hidden - the page can be frozen before the event dispatches, and what the
// player experiences on return is the app never having noticed. Capacitor's
// own `appStateChange` is dispatched by the native layer and does not depend
// on the JavaScript thread having been alive. Neither alone covers both
// platforms, so both are wired and the handler is made idempotent instead.
//
// `pageshow` catches the third case: a bfcache restore, where the page is
// resurrected wholesale and neither of the other two need have fired.
import { connection } from './connection';

type Unsubscribe = () => void;

/** Guards against the three listeners all firing for one resume. */
let lastResumeAt = 0;
const RESUME_DEBOUNCE_MS = 400;

function onResume(): void {
  const now = Date.now();
  if (now - lastResumeAt < RESUME_DEBOUNCE_MS) return;
  lastResumeAt = now;
  connection.resumeFromBackground();
}

/**
 * Whether this shell is one whose socket is worth putting down.
 *
 * Modul: NATIVE ONLY, AND THAT IS THE WHOLE CARE TAKEN HERE.
 *
 * On a phone, backgrounding means the OS is about to freeze the WebView anyway
 * and the minutes before it does are a 10 Hz stream into somebody's pocket. On
 * a DESKTOP, a hidden tab is a legitimate way to leave an idle game running -
 * the player switched tabs, they did not leave - and closing the socket there
 * would cost them the live view and any chat arriving in it for no benefit at
 * all. Reading the platform rather than the visibility state is what keeps
 * those two cases apart.
 */
function isNativeShell(): boolean {
  const capacitor = (globalThis as { Capacitor?: { isNativePlatform?: () => boolean } }).Capacitor;
  return typeof capacitor?.isNativePlatform === 'function' ? capacitor.isNativePlatform() : false;
}

function onBackground(): void {
  if (!isNativeShell()) return;
  // Any resume debounce in flight is meaningless now.
  lastResumeAt = 0;
  connection.suspendForBackground();
}

interface CapacitorAppPlugin {
  addListener?: (
    event: 'appStateChange',
    handler: (state: { isActive: boolean }) => void,
  ) => { remove?: () => void } | Promise<{ remove?: () => void }>;
}

/**
 * Starts listening. Returns an unsubscribe for symmetry and for tests; the app
 * itself never stops listening, because there is no point in the session
 * during which a resume stops mattering.
 */
export function watchAppLifecycle(): Unsubscribe {
  if (typeof document === 'undefined') return () => {};

  const cleanups: Unsubscribe[] = [];

  const visibility = () => {
    if (document.visibilityState === 'visible') onResume();
    else onBackground();
  };
  document.addEventListener('visibilitychange', visibility);
  cleanups.push(() => document.removeEventListener('visibilitychange', visibility));

  if (typeof window !== 'undefined') {
    const shown = () => onResume();
    window.addEventListener('pageshow', shown);
    cleanups.push(() => window.removeEventListener('pageshow', shown));

    // Modul: `focus` is deliberately NOT listened for. It fires on every
    // click back into the window from a devtools panel or another pane, which
    // on a desktop is constant - and each one would ask the connection
    // whether it is stale. The debounce would absorb the noise, but the
    // honest fix is not to generate it.
  }

  // Native shell, if there is one. Read defensively: the plugin is only
  // present in a Capacitor build, and `@capacitor/app` may not be installed at
  // all - this must not become a hard dependency of the web build.
  const capacitor = (globalThis as { Capacitor?: { Plugins?: { App?: CapacitorAppPlugin } } }).Capacitor;
  const nativeApp = capacitor?.Plugins?.App;

  if (typeof nativeApp?.addListener === 'function') {
    try {
      const handle = nativeApp.addListener('appStateChange', ({ isActive }) => {
        if (isActive) onResume();
        else onBackground();
      });
      Promise.resolve(handle)
        .then((h) => {
          if (typeof h?.remove === 'function') cleanups.push(() => h.remove?.());
        })
        .catch(() => {
          // A listener that could not be attached is a degraded resume, not a
          // broken app: visibilitychange still covers the common case.
        });
    } catch {
      // Same reasoning. Never let the native path take the web build down.
    }
  }

  return () => {
    for (const off of cleanups) off();
    cleanups.length = 0;
  };
}
