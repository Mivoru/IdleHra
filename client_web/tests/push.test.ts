import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

/*
  REGISTERING A DEVICE FOR PUSH.

  The server half has existed for a long time and nothing had ever exercised it,
  which is how a 64-byte token field survived against an FCM token of roughly
  160 characters. These pin the client half's contract: what is sent, when it is
  sent, and - the part that matters most - that a browser build reads the
  missing plugin and says so rather than throwing.
*/

const authedPost = vi.fn(async (_path: string, _body: unknown) => null);
let platform = 'web';
let native = false;

vi.mock('../src/lib/net/auth', () => ({
  authedPost: (path: string, body: unknown) => authedPost(path, body),
}));

vi.mock('../src/lib/net/platform', () => ({
  isNativePlatform: () => native,
  platformName: () => platform,
}));

const requestScreen = vi.fn((_screen: string) => {});

vi.mock('../src/lib/stores/navigation', () => ({
  requestScreen: (screen: string) => requestScreen(screen),
}));

/*
  Modul: RE-IMPORTED PER TEST, and the first version was not.

  push.ts holds a module-level `listening` flag, deliberately: the registration
  listener must be attached exactly once per app launch, because a second one
  would post the same token twice on every refresh. That is right for an app and
  poison for a suite - after one test registered, every later test found
  addListener already spent and its captured listeners empty, which read as the
  iOS test failing for a reason that had nothing to do with iOS.

  Resetting the module registry gives each test its own launch, which is what it
  is actually simulating.
*/
type PushModule = typeof import('../src/lib/net/push');
let push: PushModule;

type Listener = (payload: {
  value?: string;
  error?: string;
  notification?: { data?: Record<string, unknown> };
}) => void;

function installPlugin(overrides: Record<string, unknown> = {}) {
  const listeners: Record<string, Listener> = {};
  const plugin = {
    checkPermissions: vi.fn(async () => ({ receive: 'prompt' })),
    requestPermissions: vi.fn(async () => ({ receive: 'granted' })),
    register: vi.fn(async () => {}),
    addListener: vi.fn((event: string, handler: Listener) => {
      listeners[event] = handler;
      return { remove: () => {} };
    }),
    ...overrides,
  };
  (globalThis as { Capacitor?: unknown }).Capacitor = { Plugins: { PushNotifications: plugin } };
  return { plugin, listeners };
}

beforeEach(async () => {
  authedPost.mockClear();
  requestScreen.mockClear();
  platform = 'android';
  native = true;
  vi.resetModules();
  push = await import('../src/lib/net/push');
});

afterEach(() => {
  delete (globalThis as { Capacitor?: unknown }).Capacitor;
});

describe('whether push can be offered at all', () => {
  it('says a browser cannot receive notifications', () => {
    native = false;
    platform = 'web';
    expect(push.pushUnavailableReason()).toMatch(/Android or iOS app/i);
  });

  it('says so when the native build has no plugin', () => {
    native = true;
    platform = 'android';
    expect(push.pushUnavailableReason()).toMatch(/no notification support/i);
  });

  it('is available when the shell injected the plugin', () => {
    installPlugin();
    expect(push.pushUnavailableReason()).toBeNull();
  });
});

describe('registering', () => {
  it('refuses without prompting when there is nothing to prompt', async () => {
    native = false;
    const outcome = await push.enablePushNotifications();
    expect(outcome.kind).toBe('unavailable');
    expect(authedPost).not.toHaveBeenCalled();
  });

  it('asks, registers, and sends the token the platform hands back', async () => {
    const { plugin, listeners } = installPlugin();

    const outcome = await push.enablePushNotifications();
    expect(outcome.kind).toBe('registered');
    expect(plugin.requestPermissions).toHaveBeenCalled();
    expect(plugin.register).toHaveBeenCalled();

    // Modul: THE TOKEN ARRIVES ON AN EVENT. register() resolves as soon as the
    // request is made, so nothing is posted until the platform calls back -
    // and the listener has to be attached BEFORE register or the first launch
    // loses the race.
    expect(authedPost).not.toHaveBeenCalled();

    const fcmToken = 'f'.repeat(163);
    listeners.registration({ value: fcmToken });
    await vi.waitFor(() => expect(authedPost).toHaveBeenCalledTimes(1));

    const [path, body] = authedPost.mock.calls[0] as unknown as [string, Record<string, string>];
    expect(path).toBe('/api/v1/player/push-token');
    expect(body.Token).toBe(fcmToken);
    expect(body.Platform).toBe('android');

    // The whole reason this is REST: an FCM token does not fit the 64-byte
    // wire field behind opcode 33.
    expect(body.Token.length).toBeGreaterThan(64);
  });

  it('reports a refusal as a refusal, not an error', async () => {
    installPlugin({
      checkPermissions: vi.fn(async () => ({ receive: 'prompt' })),
      requestPermissions: vi.fn(async () => ({ receive: 'denied' })),
    });

    const outcome = await push.enablePushNotifications();

    expect(outcome.kind).toBe('denied');
    expect(authedPost).not.toHaveBeenCalled();
  });

  it('sends ios for an iOS build', async () => {
    platform = 'ios';
    const { listeners } = installPlugin();

    await push.enablePushNotifications();
    listeners.registration({ value: 'a'.repeat(64) });
    await vi.waitFor(() => expect(authedPost).toHaveBeenCalledTimes(1));

    const [, body] = authedPost.mock.calls[0] as unknown as [string, Record<string, string>];
    expect(body.Platform).toBe('ios');
  });

  it('never throws out to the caller when the platform misbehaves', async () => {
    installPlugin({
      register: vi.fn(async () => {
        throw new Error('no google play services');
      }),
    });

    const outcome = await push.enablePushNotifications();
    expect(outcome.kind).toBe('failed');
  });
});

describe('the silent refresh at sign-in', () => {
  it('does nothing at all when permission was never granted', async () => {
    const { plugin } = installPlugin({
      checkPermissions: vi.fn(async () => ({ receive: 'prompt' })),
    });

    await push.refreshDeviceTokenIfPermitted();

    // Modul: it must NOT prompt. This runs on every sign-in; prompting here
    // would be the first-launch permission ask that this whole design exists
    // to avoid.
    expect(plugin.requestPermissions).not.toHaveBeenCalled();
    expect(plugin.register).not.toHaveBeenCalled();
  });

  it('re-registers when permission is already granted, because tokens rotate', async () => {
    const { plugin } = installPlugin({
      checkPermissions: vi.fn(async () => ({ receive: 'granted' })),
    });

    await push.refreshDeviceTokenIfPermitted();

    expect(plugin.register).toHaveBeenCalled();
    expect(plugin.requestPermissions).not.toHaveBeenCalled();
  });

  it('cannot take a sign-in down', async () => {
    installPlugin({
      checkPermissions: vi.fn(async () => {
        throw new Error('bridge not ready');
      }),
    });

    await expect(push.refreshDeviceTokenIfPermitted()).resolves.toBeUndefined();
  });
});

/*
  TAPPING THE NOTIFICATION.

  Modul: the destination is NOT decided here. The server puts a screen key in
  the message's data dictionary (PushNotificationTriggerEngine.DescribeTrigger)
  and this half forwards it. A switch in the client mapping trigger codes to
  screens would be that table written twice in two languages - the exact shape
  KNOWN_AFFIX_IDS had when ten of its twelve entries had silently drifted.

  These therefore assert FORWARDING, never a particular mapping: nothing here
  should have to change when a new trigger is added on the server.
*/
describe('opening the screen a notification is about', () => {
  it('forwards whatever screen the server named', () => {
    const { listeners } = installPlugin();

    push.watchNotificationTaps();
    listeners.pushNotificationActionPerformed({
      notification: { data: { screen: 'worldboss', payload: 'world_boss_window_open' } },
    });

    expect(requestScreen).toHaveBeenCalledWith('worldboss');
  });

  it('attaches once, so a second sign-in does not double-navigate', () => {
    const { listeners, plugin } = installPlugin();

    push.watchNotificationTaps();
    push.watchNotificationTaps();

    const taps = plugin.addListener.mock.calls.filter(
      (call: unknown[]) => call[0] === 'pushNotificationActionPerformed',
    );
    expect(taps).toHaveLength(1);

    listeners.pushNotificationActionPerformed({ notification: { data: { screen: 'progression' } } });
    expect(requestScreen).toHaveBeenCalledTimes(1);
  });

  it('ignores a message with no destination rather than navigating nowhere', () => {
    const { listeners } = installPlugin();

    push.watchNotificationTaps();
    listeners.pushNotificationActionPerformed({ notification: { data: { payload: 'unknown' } } });
    listeners.pushNotificationActionPerformed({ notification: {} });
    listeners.pushNotificationActionPerformed({});

    expect(requestScreen).not.toHaveBeenCalled();
  });

  it('does nothing in a browser, where there is no plugin to listen on', () => {
    native = false;
    platform = 'web';

    expect(() => push.watchNotificationTaps()).not.toThrow();
    expect(requestScreen).not.toHaveBeenCalled();
  });
});
