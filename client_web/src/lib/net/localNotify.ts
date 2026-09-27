// Modul: THE "YOUR FOLK ARE RESTING" LOCAL NOTIFICATION (task 45, plan 7b).
//
// Push needs Firebase, which is not finished; a LOCAL notification needs no
// server at all. When the app goes to the background (native only - the same
// decision lifecycle.ts makes) one notification is scheduled for an hour
// before the offline simulation stops accruing, and it is cancelled again the
// moment the app comes back.
//
// THE CAP IS READ, NEVER COMPUTED. OfflineCapSeconds arrives on every state
// frame as the server's EFFECTIVE cap, Vodnik extension included. A client
// copy of "12 h, or 18 h at Vodnik 25" would be the two-sources-of-truth bug
// class; the day the rule moves, this notification would lie.
//
// ONE FIXED ID, so scheduling is idempotent: visibilitychange and
// appStateChange both report one backgrounding, and the second schedule simply
// replaces the first.
//
// PERMISSION is asked from a Settings button, never here. Android 13+ refuses
// an undeclared runtime request silently and remembers the refusal for ever,
// which is why POST_NOTIFICATIONS is DECLARED in the manifest; and a prompt at
// launch, before the player knows what it is for, is the likeliest "no".
//
// Plugin read off `Capacitor.Plugins` (no web import), and INSTALLED:
// `@capacitor/local-notifications` is a dependency, pinned by
// nativeProjects.test.ts.
import { isNativePlatform, platformName } from './platform';

export const RESTING_NOTIFICATION_ID = 4501;
const LEAD_SECONDS = 3600;

type PermissionState = 'prompt' | 'prompt-with-rationale' | 'granted' | 'denied';

interface LocalNotificationsPlugin {
  checkPermissions?: () => Promise<{ display: PermissionState }>;
  requestPermissions?: () => Promise<{ display: PermissionState }>;
  schedule?: (options: {
    notifications: {
      id: number;
      title: string;
      body: string;
      schedule?: { at: Date; allowWhileIdle?: boolean };
    }[];
  }) => Promise<unknown>;
  cancel?: (options: { notifications: { id: number }[] }) => Promise<void>;
}

function plugin(): LocalNotificationsPlugin | null {
  const capacitor = (globalThis as { Capacitor?: { Plugins?: { LocalNotifications?: LocalNotificationsPlugin } } })
    .Capacitor;
  return capacitor?.Plugins?.LocalNotifications ?? null;
}

let offlineCapSeconds = 0;

/** Fed from every state frame (stores/game.ts). */
export function noteOfflineCap(seconds: number): void {
  const value = Number(seconds);
  if (Number.isFinite(value) && value > 0) offlineCapSeconds = value;
}

/**
 * When the notice should fire, or null if it cannot be meaningful: no cap
 * known yet, or a cap no longer than the lead itself.
 */
export function restingNoticeAt(nowMs: number, capSeconds: number): Date | null {
  if (!Number.isFinite(capSeconds) || capSeconds <= LEAD_SECONDS) return null;
  return new Date(nowMs + (capSeconds - LEAD_SECONDS) * 1000);
}

/** Whether asking is even possible, phrased for a player. */
export function localNotifyUnavailableReason(): string | null {
  if (!isNativePlatform()) {
    return 'Reminders need the Android or iOS app. A browser tab cannot schedule them.';
  }
  if (plugin() === null) {
    return `No reminder support is built into this ${platformName()} build.`;
  }
  return null;
}

export type LocalNotifyOutcome = 'granted' | 'denied' | 'unavailable';

/** The Settings button. The only place that ever prompts. */
export async function enableLocalNotifications(): Promise<LocalNotifyOutcome> {
  const notifications = plugin();
  if (!isNativePlatform() || notifications === null || typeof notifications.requestPermissions !== 'function') {
    return 'unavailable';
  }
  try {
    const { display } = await notifications.requestPermissions();
    return display === 'granted' ? 'granted' : 'denied';
  } catch {
    return 'denied';
  }
}

async function permitted(notifications: LocalNotificationsPlugin): Promise<boolean> {
  if (typeof notifications.checkPermissions !== 'function') return false;
  try {
    const { display } = await notifications.checkPermissions();
    return display === 'granted';
  } catch {
    return false;
  }
}

/**
 * On background. Schedules nothing on the web, without permission (checked,
 * never requested), or before a state frame has said what the cap is.
 * Returns when it will fire, or null.
 */
export async function scheduleRestingNotice(nowMs: number = Date.now()): Promise<Date | null> {
  if (!isNativePlatform()) return null;
  const notifications = plugin();
  if (notifications === null || typeof notifications.schedule !== 'function') return null;
  const at = restingNoticeAt(nowMs, offlineCapSeconds);
  if (at === null) return null;
  if (!(await permitted(notifications))) return null;

  try {
    await notifications.schedule({
      notifications: [
        {
          id: RESTING_NOTIFICATION_ID,
          title: 'FolkIdle',
          body: 'Your folk are resting - they stop earning in 1 h.',
          schedule: { at, allowWhileIdle: true },
        },
      ],
    });
    return at;
  } catch {
    return null;
  }
}

/** On resume. Idempotent; cancelling an unscheduled id is harmless. */
export async function cancelRestingNotice(): Promise<void> {
  if (!isNativePlatform()) return;
  const notifications = plugin();
  if (notifications === null || typeof notifications.cancel !== 'function') return;
  try {
    await notifications.cancel({ notifications: [{ id: RESTING_NOTIFICATION_ID }] });
  } catch {
    /* nothing to cancel is fine */
  }
}

/** Test seam. */
export function resetLocalNotifyForTests(): void {
  offlineCapSeconds = 0;
}
