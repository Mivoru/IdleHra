import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

/*
  THE RESTING REMINDER (task 45). Scheduled on background an hour before the
  server's EFFECTIVE offline cap runs out, cancelled on resume, never on the
  web, never without permission - and permission is never requested here.
*/

let native = true;
vi.mock('../src/lib/net/platform', () => ({
  isNativePlatform: () => native,
  platformName: () => (native ? 'android' : 'web'),
}));

type Module = typeof import('../src/lib/net/localNotify');
let notify: Module;
let display: 'granted' | 'denied' | 'prompt' = 'granted';
const schedule = vi.fn(async (_o: unknown) => ({}));
const cancel = vi.fn(async (_o: unknown) => {});
const checkPermissions = vi.fn(async () => ({ display }));
const requestPermissions = vi.fn(async () => ({ display }));

beforeEach(async () => {
  vi.resetModules();
  schedule.mockClear();
  cancel.mockClear();
  checkPermissions.mockClear();
  requestPermissions.mockClear();
  native = true;
  display = 'granted';
  (globalThis as Record<string, unknown>).Capacitor = {
    Plugins: { LocalNotifications: { schedule, cancel, checkPermissions, requestPermissions } },
  };
  notify = await import('../src/lib/net/localNotify');
});

afterEach(() => {
  delete (globalThis as Record<string, unknown>).Capacitor;
});

describe('schedule arithmetic', () => {
  it('fires one hour before the cap', () => {
    expect(notify.restingNoticeAt(1_000_000, 43200)?.getTime()).toBe(1_000_000 + (43200 - 3600) * 1000);
  });

  it('follows the server cap, e.g. the Vodnik 18 h extension', () => {
    expect(notify.restingNoticeAt(0, 18 * 3600)?.getTime()).toBe(17 * 3600 * 1000);
  });

  it('refuses a cap no longer than the lead', () => {
    expect(notify.restingNoticeAt(0, 3600)).toBeNull();
    expect(notify.restingNoticeAt(0, 0)).toBeNull();
  });
});

describe('scheduleRestingNotice', () => {
  it('schedules at now + cap - 1 h with the fixed id', async () => {
    notify.noteOfflineCap(43200);
    const at = await notify.scheduleRestingNotice(5000);
    expect(at?.getTime()).toBe(5000 + 39600 * 1000);
    expect(schedule).toHaveBeenCalledTimes(1);
    const arg = schedule.mock.calls[0][0] as { notifications: { id: number; schedule: { at: Date } }[] };
    expect(arg.notifications).toHaveLength(1);
    expect(arg.notifications[0].id).toBe(notify.RESTING_NOTIFICATION_ID);
    expect(arg.notifications[0].schedule.at.getTime()).toBe(5000 + 39600 * 1000);
  });

  it('does nothing before a state frame has said what the cap is', async () => {
    expect(await notify.scheduleRestingNotice(0)).toBeNull();
    expect(schedule).not.toHaveBeenCalled();
  });

  it('does nothing on the web', async () => {
    native = false;
    notify.noteOfflineCap(43200);
    expect(await notify.scheduleRestingNotice(0)).toBeNull();
    await notify.cancelRestingNotice();
    expect(schedule).not.toHaveBeenCalled();
    expect(cancel).not.toHaveBeenCalled();
  });

  it('does nothing without permission, and never asks for it', async () => {
    display = 'denied';
    notify.noteOfflineCap(43200);
    expect(await notify.scheduleRestingNotice(0)).toBeNull();
    expect(schedule).not.toHaveBeenCalled();
    expect(requestPermissions).not.toHaveBeenCalled();
  });
});

describe('cancel on resume', () => {
  it('cancels the fixed id', async () => {
    await notify.cancelRestingNotice();
    expect(cancel).toHaveBeenCalledWith({ notifications: [{ id: notify.RESTING_NOTIFICATION_ID }] });
  });
});

describe('the Settings button', () => {
  it('is the only path that requests permission', async () => {
    expect(await notify.enableLocalNotifications()).toBe('granted');
    expect(requestPermissions).toHaveBeenCalledTimes(1);
  });

  it('reports unavailable on the web', async () => {
    native = false;
    expect(await notify.enableLocalNotifications()).toBe('unavailable');
    expect(notify.localNotifyUnavailableReason()).not.toBeNull();
  });
});
