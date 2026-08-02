// Modul: which shell this client is running inside, and the two decisions that
// depend on the answer.
//
// A Capacitor build is the SAME web application in a native WebView, so almost
// nothing should care. Two things genuinely do, and both are silent failures
// if got wrong:
//
//   1. Token lifetime. The browser build stores the JWT in sessionStorage
//      deliberately (see auth.ts), because a tab closing is a reasonable place
//      for a credential to die. A phone does not work that way: the OS
//      suspends and kills apps on its own schedule, so the same rule would log
//      the player out at unpredictable moments with no explanation. Native
//      builds therefore persist through Capacitor Preferences instead.
//
//   2. The server address. `localhost` means the phone itself, not the
//      development machine, so a native build pointed at the browser default
//      cannot reach anything at all - and it fails as a connection timeout,
//      which reads like a server problem rather than a configuration one.

/**
 * True when running inside a Capacitor native shell.
 *
 * Read from the injected global rather than the user agent: the shell defines
 * `window.Capacitor` and sets `isNativePlatform`, which is authoritative,
 * whereas a user agent string is a guess that a WebView update can invalidate.
 */
export function isNativePlatform(): boolean {
  const capacitor = (globalThis as { Capacitor?: { isNativePlatform?: () => boolean } }).Capacitor;
  return typeof capacitor?.isNativePlatform === 'function' ? capacitor.isNativePlatform() : false;
}

/** "android", "ios", or "web". */
export function platformName(): string {
  const capacitor = (globalThis as { Capacitor?: { getPlatform?: () => string } }).Capacitor;
  return typeof capacitor?.getPlatform === 'function' ? capacitor.getPlatform() : 'web';
}

/**
 * Origins a Capacitor build sends. The server's CORS allow-list
 * (FOLKIDLE_WEB_ORIGINS) must contain the one for the platform being built, or
 * every request fails before the player sees anything.
 *
 * Android uses `https://localhost` because capacitor.config.json sets
 * androidScheme to https; iOS uses `capacitor://localhost`. Exported so the
 * value lives beside its explanation rather than only in a deployment note.
 */
export const CAPACITOR_ORIGINS = ['https://localhost', 'capacitor://localhost'] as const;
