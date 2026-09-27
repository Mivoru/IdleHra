import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { get } from 'svelte/store';

/*
  HAPTICS (task 45). Three promises: nothing on the web, nothing with the
  toggle off, and no more than one buzz per 80 ms in a fast fight.
*/

let native = false;
vi.mock('../src/lib/net/platform', () => ({
  isNativePlatform: () => native,
  platformName: () => (native ? 'android' : 'web'),
}));

type HapticsModule = typeof import('../src/lib/net/haptics');
let haptics: HapticsModule;
const impact = vi.fn(async (_o: { style: string }) => {});
const notification = vi.fn(async (_o: { type: string }) => {});

beforeEach(async () => {
  vi.resetModules();
  impact.mockClear();
  notification.mockClear();
  native = true;
  (globalThis as Record<string, unknown>).Capacitor = { Plugins: { Haptics: { impact, notification } } };
  haptics = await import('../src/lib/net/haptics');
});

afterEach(() => {
  delete (globalThis as Record<string, unknown>).Capacitor;
});

describe('haptics', () => {
  it('defaults to on', () => {
    expect(get(haptics.hapticsEnabled)).toBe(true);
  });

  it('does nothing on the web', () => {
    native = false;
    expect(haptics.tap('light', 1000)).toBe(false);
    expect(impact).not.toHaveBeenCalled();
  });

  it('does nothing when the plugin is missing', () => {
    (globalThis as Record<string, unknown>).Capacitor = { Plugins: {} };
    expect(haptics.tap('light', 1000)).toBe(false);
  });

  it('respects the toggle', () => {
    haptics.hapticsEnabled.set(false);
    expect(haptics.tap('medium', 1000)).toBe(false);
    expect(impact).not.toHaveBeenCalled();
    haptics.hapticsEnabled.set(true);
    expect(haptics.tap('medium', 1000)).toBe(true);
    expect(impact).toHaveBeenCalledWith({ style: 'MEDIUM' });
  });

  it('throttles to one tap per 80 ms', () => {
    expect(haptics.tap('light', 1000)).toBe(true);
    expect(haptics.tap('light', 1040)).toBe(false);
    expect(haptics.tap('heavy', 1079)).toBe(false);
    expect(haptics.tap('heavy', 1080)).toBe(true);
    expect(impact).toHaveBeenCalledTimes(2);
    expect(impact).toHaveBeenLastCalledWith({ style: 'HEAVY' });
  });

  it('maps success to a notification haptic', () => {
    expect(haptics.tap('success', 5000)).toBe(true);
    expect(notification).toHaveBeenCalledWith({ type: 'SUCCESS' });
    expect(impact).not.toHaveBeenCalled();
  });
});
