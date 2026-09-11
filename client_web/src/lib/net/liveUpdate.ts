// Modul: THE APP UPDATES ITSELF, the way a modern game does.
//
// A store install is a SHELL. The content arrives over the air, so a gameplay
// change reaches a player when they open the app rather than when they
// remember to reinstall it. Here the shell is the APK - Java, the Capacitor
// plugins, the permissions - and the content is the web bundle.
//
// The plugin (@capgo/capacitor-updater) does the download and the swap; the
// configuration lives in capacitor.config.json. This file exists for the one
// thing the plugin CANNOT do on its own, which is decide whether the bundle it
// just installed actually works.
//
// ------------------------------------------------------------------------
// WHY notifyAppReady IS THE MOST IMPORTANT LINE IN THIS FILE
// ------------------------------------------------------------------------
//
// Over-the-air updates introduce a failure mode nothing else in this client
// has: a bundle that cannot boot. A syntax error, a bad import, a Svelte
// snippet rendered with component-tag syntax - this codebase has shipped that
// last one before, and it took the whole screen down at RUNTIME while
// svelte-check passed. Ship that in an APK and the fix is another APK. Ship it
// over the air and, without a rollback, every phone that downloaded it is
// bricked until the player uninstalls and reinstalls - and the update system
// itself is inside the broken bundle, so it cannot heal them.
//
// The plugin's answer is a dead-man's switch: after applying a new bundle it
// waits for `notifyAppReady()`. If the app does not call it before the plugin's
// timeout, the bundle is marked bad and the PREVIOUS one is restored on the
// next launch, permanently. So this call is the assertion "I booted" - and it
// must only be made when that is actually true.
//
// WHICH IS WHY IT IS CALLED AFTER MOUNT AND NOT AT IMPORT TIME. An import-time
// call runs before any component renders, which would confirm a bundle whose
// entire UI throws. Mounting is the cheapest honest evidence that the
// JavaScript parsed, the runes ran and the app produced a screen.
//
// It deliberately does NOT wait for the server. A phone in a tunnel is not a
// broken bundle, and rolling back over a lost connection would replace a
// working app with an older one for a reason that has nothing to do with it.
import { isNativePlatform } from './platform';

interface UpdaterPlugin {
  notifyAppReady?: () => Promise<unknown>;
  current?: () => Promise<{ bundle?: { version?: string } }>;
}

/**
 * Read off the injected global, exactly as push.ts and lifecycle.ts do.
 *
 * Modul: and, exactly as with those two, the PACKAGE still has to be a
 * dependency or `cap sync` links nothing and this is always null on a real
 * phone - see tests/nativeProjects.test.ts, which exists because that happened
 * to both of them at once.
 */
function plugin(): UpdaterPlugin | null {
  const capacitor = (
    globalThis as { Capacitor?: { Plugins?: { CapacitorUpdater?: UpdaterPlugin } } }
  ).Capacitor;
  return capacitor?.Plugins?.CapacitorUpdater ?? null;
}

/**
 * Tells the updater this bundle boots. Call once, after the app has mounted.
 *
 * Safe to call on the web, where it finds no plugin and returns.
 */
export async function confirmBundleBooted(): Promise<void> {
  if (!isNativePlatform()) return;

  const updater = plugin();
  if (updater === null || typeof updater.notifyAppReady !== 'function') return;

  try {
    await updater.notifyAppReady();
  } catch (error) {
    // Modul: swallowed on purpose, and this is the one place where that is
    // right. If the confirmation fails the plugin rolls back - which is the
    // SAFE outcome - and throwing here would take down the app that just
    // proved it works, turning a degraded update into a broken one.
    console.warn('live update: could not confirm this bundle booted', error);
  }
}

/** Which bundle is running, for the Settings screen. Null on the web. */
export async function runningBundleVersion(): Promise<string | null> {
  if (!isNativePlatform()) return null;

  const updater = plugin();
  if (updater === null || typeof updater.current !== 'function') return null;

  try {
    const info = await updater.current();
    return info?.bundle?.version ?? null;
  } catch {
    return null;
  }
}
