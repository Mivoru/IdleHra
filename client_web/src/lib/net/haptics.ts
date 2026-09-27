// Modul: HAPTICS (task 45, plan item 7a). A crit, a kill, a rare drop and the
// shield wheel's big moments are felt on a phone as well as seen.
//
// Read off `Capacitor.Plugins.Haptics` like push.ts reads its plugin, so the
// web bundle carries no native module - BUT THE PLUGIN IS INSTALLED:
// `@capacitor/haptics` is a dependency and nativeProjects.test.ts pins it to
// both native projects. "Not imported" and "not installed" look identical in a
// browser and opposite on a phone (CLAUDE.md).
//
// WHAT FIRES IT. The combat event feed and the loot feed (stores/game.ts) and
// ShieldWheel.svelte - never an inference from a health difference, which is
// the second-source-of-truth shape CLAUDE.md forbids for combat.
//
// THROTTLED to one buzz per 80 ms. A geared character resolves several blows
// between two animation frames; without the throttle a fast fight is one long
// motor rattle rather than distinguishable taps.
import { writable, get } from 'svelte/store';
import { isNativePlatform } from './platform';

export type HapticKind = 'light' | 'medium' | 'heavy' | 'success';

interface HapticsPlugin {
  impact?: (options: { style: 'LIGHT' | 'MEDIUM' | 'HEAVY' }) => Promise<void>;
  notification?: (options: { type: 'SUCCESS' | 'WARNING' | 'ERROR' }) => Promise<void>;
}

export const HAPTICS_THROTTLE_MS = 80;
const ENABLED_KEY = 'folkidle.haptics';

function plugin(): HapticsPlugin | null {
  const capacitor = (globalThis as { Capacitor?: { Plugins?: { Haptics?: HapticsPlugin } } }).Capacitor;
  return capacitor?.Plugins?.Haptics ?? null;
}

// Modul: DEFAULTS TO ON (decision D3 in the audit plan). Only an explicit '0'
// turns it off, so a phone that never opened Settings still feels a crit.
// Storage is wrapped: a private window or a node test has none, and a setting
// is never worth a thrown module load.
function readEnabled(): boolean {
  try {
    return globalThis.localStorage?.getItem(ENABLED_KEY) !== '0';
  } catch {
    return true;
  }
}

export const hapticsEnabled = writable(readEnabled());

hapticsEnabled.subscribe((value) => {
  try {
    globalThis.localStorage?.setItem(ENABLED_KEY, value ? '1' : '0');
  } catch {
    /* the toggle still works for this session */
  }
});

let lastTapAt = Number.NEGATIVE_INFINITY;

/**
 * One haptic, or nothing: nothing on the web, nothing with the toggle off,
 * nothing within 80 ms of the previous one. Returns whether it fired, for tests.
 */
export function tap(kind: HapticKind, nowMs: number = Date.now()): boolean {
  if (!get(hapticsEnabled)) return false;
  if (!isNativePlatform()) return false;
  const haptics = plugin();
  if (haptics === null) return false;
  if (nowMs - lastTapAt < HAPTICS_THROTTLE_MS) return false;
  lastTapAt = nowMs;

  try {
    const pending =
      kind === 'success'
        ? haptics.notification?.({ type: 'SUCCESS' })
        : haptics.impact?.({ style: kind === 'light' ? 'LIGHT' : kind === 'medium' ? 'MEDIUM' : 'HEAVY' });
    // A motor that refuses is not an error worth surfacing.
    void Promise.resolve(pending).catch(() => {});
  } catch {
    /* same */
  }
  return true;
}

/** Test seam: forget the throttle's last tap. */
export function resetHapticsThrottle(): void {
  lastTapAt = Number.NEGATIVE_INFINITY;
}
