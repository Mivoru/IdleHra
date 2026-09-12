import { writable, get } from 'svelte/store';
import { notesNewerThan, shouldShowNotes, type Release } from '../ui/releaseNotes';

/**
 * Which build this is, whether the player has seen its notes, and whether the
 * bundle they are running has been superseded.
 *
 * WHY THIS NEEDS NO SERVER. `__BUILD_ID__` is substituted into the bundle at
 * build time, so a tab left open across a deploy keeps yesterday's value;
 * `/version.json` is a static file beside the bundle, so fetching it reaches
 * whatever is deployed NOW. The difference between the two is the entire
 * staleness check - no endpoint, no wire field, no database.
 */

export const APP_VERSION = __APP_VERSION__;
export const BUILD_ID = __BUILD_ID__;

const SEEN_KEY = 'folkidle.seenVersion';

/**
 * Modul: localStorage, and it is the right tool here rather than the player row.
 *
 * Onboarding moved OFF localStorage onto the player record deliberately, because
 * which explanations somebody has been shown has to survive a new device. This
 * does not carry that weight: the worst case is reading the same release notes
 * twice on a second device, which is a shrug rather than a defect - and the
 * alternative costs a migration, a wire field and a hydration path. It also
 * fails safe: a browser that refuses storage shows the notes once per session
 * and never breaks.
 */
function readSeen(): string | null {
  try {
    return localStorage.getItem(SEEN_KEY);
  } catch {
    return null;
  }
}

function writeSeen(version: string) {
  try {
    localStorage.setItem(SEEN_KEY, version);
  } catch {
    // A private window, or storage denied. Nothing here is worth an error.
  }
}

/** The releases to show, or empty. Empty for a brand-new player - see shouldShowNotes. */
export const pendingNotes = writable<readonly Release[]>([]);

/** True once the deployed build differs from the one this tab is running. */
export const updateAvailable = writable(false);

/**
 * Decides what to show on start-up, and records the version either way.
 *
 * A PLAYER WITH NOTHING STORED HAS MISSED NOTHING. They are new, so the current
 * version is recorded silently and no window opens - a changelog for a game
 * somebody has never played is noise in the first thirty seconds, which is the
 * worst place this game can spend a player's attention.
 */
export function resolveNotesOnStartup() {
  const seen = readSeen();

  if (!shouldShowNotes(seen, APP_VERSION)) {
    writeSeen(APP_VERSION);
    return;
  }

  // Everything they missed, not only the newest - somebody back after three
  // updates should see all three.
  pendingNotes.set(notesNewerThan(seen as string));
}

/** Called when the window is closed. Nothing is shown again until the next release. */
export function acknowledgeNotes() {
  writeSeen(APP_VERSION);
  pendingNotes.set([]);
}

let pollTimer: ReturnType<typeof setInterval> | null = null;

/**
 * Asks the deployed build who it is.
 *
 * Silent on every failure. Offline, a 404 on a checkout with no stamped file, a
 * proxy returning HTML - none of them mean "out of date", and an update prompt
 * a player cannot act on is worse than no prompt. Only a genuine, different
 * build id sets the flag.
 */
export async function checkForUpdate(): Promise<void> {
  if (get(updateAvailable)) return;

  try {
    // cache: no-store, or the browser hands back the copy it cached when this
    // tab loaded - which is exactly the stale value being tested for.
    const response = await fetch('/version.json', { cache: 'no-store' });
    if (!response.ok) return;

    const deployed = (await response.json()) as { version?: string; buildId?: string };
    if (!deployed?.buildId) return;

    if (deployed.buildId !== BUILD_ID) updateAvailable.set(true);
  } catch {
    // Offline, or no stamped file. Says nothing.
  }
}

/** Every ten minutes, plus whenever the tab is brought back to the front. */
export function startUpdatePolling(intervalMs = 10 * 60 * 1000) {
  if (pollTimer !== null) return;

  void checkForUpdate();
  pollTimer = setInterval(() => void checkForUpdate(), intervalMs);

  // A phone that has been in a pocket for an hour is the likeliest way to meet
  // a deploy, and it fires no timers while backgrounded.
  if (typeof document !== 'undefined') {
    document.addEventListener('visibilitychange', () => {
      if (!document.hidden) void checkForUpdate();
    });
  }
}

export function stopUpdatePolling() {
  if (pollTimer === null) return;
  clearInterval(pollTimer);
  pollTimer = null;
}
