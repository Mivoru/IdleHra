// Modul: PUSH WAS FINISHED ON THE SERVER AND HAD NO CLIENT HALF.
//
// Opcode 33 has a handler, two validator paths and PushNotificationTriggerEngine
// behind it. Nothing had ever sent one - and it turns out nothing could:
// `ClientCommandPacket.DeviceTokenBytes` is 64 wide, and Capacitor's
// `Token.value` is an APNS token on iOS and an FCM token on Android. An FCM
// registration token is roughly 160 ASCII characters. Android push was
// impossible through that path and iOS push fitted with nothing to spare.
//
// So the token goes over REST, which is the same answer the purchase receipt
// reached for the same reason - see billing.ts, which says outright that a
// receipt is "far too large for the fixed-layout command packet".
//
// WHY THERE IS NO npm DEPENDENCY HERE.
//
// The plugin is read off the runtime-injected `Capacitor.Plugins` object, the
// way lifecycle.ts reads `App`. `@capacitor/push-notifications` is not a
// dependency of the WEB build, must never become one, and adding it would make
// every browser bundle carry a module it can do nothing with. The native shell
// injects the plugin; the web build simply finds nothing there and says so.
import { authedPost } from './auth';
import { isNativePlatform, platformName } from './platform';
import { requestScreen } from '../stores/navigation';

type PermissionState = 'prompt' | 'prompt-with-rationale' | 'granted' | 'denied';

interface PushPlugin {
  checkPermissions?: () => Promise<{ receive: PermissionState }>;
  requestPermissions?: () => Promise<{ receive: PermissionState }>;
  register?: () => Promise<void>;
  addListener?: (
    event: 'registration' | 'registrationError' | 'pushNotificationActionPerformed',
    handler: (payload: PushEventPayload) => void,
  ) => unknown;
}

function plugin(): PushPlugin | null {
  const capacitor = (globalThis as { Capacitor?: { Plugins?: { PushNotifications?: PushPlugin } } })
    .Capacitor;
  return capacitor?.Plugins?.PushNotifications ?? null;
}

/**
 * Everything the three listeners can be handed.
 *
 * `registration` carries `value`; `registrationError` carries `error`; a tap
 * carries the whole notification, whose `data` is the dictionary
 * `PushNotificationTriggerEngine.SendFcmV1Async` put on the message. Every
 * field is optional because all of it crosses a native bridge, and a shape
 * assumed rather than checked is how a tap would throw instead of navigating.
 */
interface PushEventPayload {
  value?: string;
  error?: string;
  notification?: { data?: Record<string, unknown> };
}

export type PushOutcome =
  | { kind: 'registered' }
  | { kind: 'denied' }
  | { kind: 'unavailable'; reason: string }
  | { kind: 'failed'; reason: string };

/** Whether asking is even possible, phrased for a player rather than a log. */
export function pushUnavailableReason(): string | null {
  if (!isNativePlatform()) {
    return 'Notifications need the Android or iOS app. A browser tab cannot receive them.';
  }
  if (plugin() === null) {
    return `No notification support is built into this ${platformName()} build.`;
  }
  return null;
}

/**
 * Hands a device token to the server.
 *
 * Exported for the sake of the listener below and for tests; nothing else
 * should need it.
 */
export async function submitDeviceToken(token: string): Promise<void> {
  // Modul: the platform is sent as a WORD, not the 1/2 the server stores. A
  // number crossing this boundary is a magic constant in two files, and this
  // codebase's dominant bug class is two copies of one truth drifting.
  const platform = platformName() === 'ios' ? 'ios' : 'android';
  await authedPost('/api/v1/player/push-token', { Token: token, Platform: platform });
}

let listening = false;

/**
 * Asks for notification permission and registers the device.
 *
 * Modul: CALLED FROM A CONTROL THE PLAYER PRESSED, never on first launch. An
 * app that opens with a permission prompt before it has shown anything worth
 * being notified about gets denied once and never gets asked again - the OS
 * remembers. The Settings toggle is the moment that earns it.
 *
 * Declining is a normal outcome, not an error: the game stays fully playable
 * and nothing here asks a second time.
 */
export async function enablePushNotifications(): Promise<PushOutcome> {
  const unavailable = pushUnavailableReason();
  if (unavailable !== null) return { kind: 'unavailable', reason: unavailable };

  const push = plugin()!;

  try {
    let status = (await push.checkPermissions?.()) ?? { receive: 'prompt' as PermissionState };
    if (status.receive === 'prompt' || status.receive === 'prompt-with-rationale') {
      status = (await push.requestPermissions?.()) ?? status;
    }
    if (status.receive !== 'granted') return { kind: 'denied' };

    // Modul: the token arrives on an EVENT, not as a return value - register()
    // resolves as soon as the request is made. Attaching the listener after
    // calling register would be a race the first launch loses, so it goes
    // first, and it is attached once because a second listener would post the
    // same token twice on every refresh.
    if (!listening && typeof push.addListener === 'function') {
      listening = true;

      push.addListener('registration', (payload) => {
        const value = payload?.value;
        if (!value) return;
        void submitDeviceToken(value).catch((err) => {
          // A token the server did not accept is a push feature that silently
          // never fires, which is indistinguishable from the player having
          // turned notifications off. Say it where it can be found.
          console.error('Push token registration failed', err);
        });
      });

      push.addListener('registrationError', (payload) => {
        console.error('Push registration error from the platform', payload?.error ?? 'unknown');
      });
    }

    await push.register?.();
    return { kind: 'registered' };
  } catch (err) {
    return { kind: 'failed', reason: err instanceof Error ? err.message : 'registration failed' };
  }
}

let watchingTaps = false;

/**
 * Sends a tapped notification to the screen it is about.
 *
 * Modul: THE DESTINATION COMES FROM THE SERVER, in `data.screen`, and this
 * function does not know what any trigger means. The alternative - a switch
 * here mapping `world_boss_window_open` to a screen key - would be the server's
 * trigger table written down a second time, in another language, kept in step
 * by hand. That is precisely the shape `KNOWN_AFFIX_IDS` had when ten of its
 * twelve entries turned out to have drifted. App.svelte validates the key
 * against the nav it actually renders, so an unknown one is ignored rather than
 * navigating to a blank screen.
 *
 * ATTACHED AT SIGN-IN, WHICH IS AFTER THE TAP THAT CAUSED IT. Tapping a
 * notification while the app is closed launches it, and the event fires long
 * before any of this has run. Capacitor retains that one until something
 * consumes it, which is the only reason attaching late works - so this must
 * stay a plain listener with no "only if the app was already open" guard.
 */
export function watchNotificationTaps(): void {
  if (watchingTaps) return;
  const push = plugin();
  if (push === null || typeof push.addListener !== 'function') return;

  watchingTaps = true;
  try {
    push.addListener('pushNotificationActionPerformed', (payload) => {
      const target = payload?.notification?.data?.screen;
      if (typeof target === 'string' && target.length > 0) requestScreen(target);
    });
  } catch {
    // A tap that cannot navigate still opened the game, which is most of what
    // the player wanted. Never let the bridge take a sign-in down.
    watchingTaps = false;
  }
}

/**
 * Re-registers silently when permission was already granted on a previous run.
 *
 * Modul: tokens ROTATE - a reinstall, a restore from backup, or the platform
 * simply deciding to. A player who enabled notifications in March and stopped
 * receiving them in June would have no way to know why, and no reason to go and
 * press the button again. This runs at sign-in and does nothing at all unless
 * permission is already granted, so it never prompts.
 */
export async function refreshDeviceTokenIfPermitted(): Promise<void> {
  if (pushUnavailableReason() !== null) return;

  try {
    const status = await plugin()!.checkPermissions?.();
    if (status?.receive !== 'granted') return;
    await enablePushNotifications();
  } catch {
    // Never let a silent refresh take a sign-in down.
  }
}
