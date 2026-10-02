import { describe, expect, it, vi, afterEach } from 'vitest';
import { hotkeyTab, MAIN_TABS, HOTKEY_SCREENS } from '../src/lib/ui/tabs';
import { readPrefAs, writePref } from '../src/lib/net/prefs';

function key(k: string, extra: Record<string, unknown> = {}) {
  return { key: k, ctrlKey: false, metaKey: false, altKey: false, repeat: false, target: null, ...extra } as never;
}

describe('task 52 hotkeys', () => {
  it('maps 1-4 onto the tab bar in order, and 5 onto Village', () => {
    // Task 95: the fifth tab is More, which is a sheet rather than a screen.
    expect(['1', '2', '3', '4', '5'].map((k) => hotkeyTab(key(k)))).toEqual([
      ...MAIN_TABS.slice(0, 4).map((t) => t.key),
      'village',
    ]);
    expect(HOTKEY_SCREENS).toHaveLength(5);
    expect(MAIN_TABS[4].key).toBe('more');
  });

  it('leaves other keys, modifiers and repeats alone', () => {
    expect(hotkeyTab(key('6'))).toBeNull();
    expect(hotkeyTab(key('a'))).toBeNull();
    expect(hotkeyTab(key('1', { ctrlKey: true }))).toBeNull();
    expect(hotkeyTab(key('1', { altKey: true }))).toBeNull();
    expect(hotkeyTab(key('1', { repeat: true }))).toBeNull();
  });

  it('never steals a digit typed into a field', () => {
    const input = { closest: (sel: string) => (sel.includes('input') ? {} : null) };
    expect(hotkeyTab(key('2', { target: input }))).toBeNull();
    expect(hotkeyTab(key('2', { target: { isContentEditable: true } }))).toBeNull();
    expect(hotkeyTab(key('2', { target: { closest: () => null } }))).toBe('combat');
  });
});

describe('task 52 prefs', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('falls back when storage throws, and swallows a failed write', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => {
        throw new Error('blocked');
      },
      setItem: () => {
        throw new Error('blocked');
      },
    });
    expect(readPrefAs('k', () => true, 'hub')).toBe('hub');
    expect(() => writePref('k', 'x')).not.toThrow();
  });

  it('rejects a stored value the caller no longer accepts', () => {
    const store = new Map<string, string>([['k', 'retired-screen']]);
    vi.stubGlobal('localStorage', { getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => store.set(k, v) });
    expect(readPrefAs('k', (v) => v === 'combat', 'hub')).toBe('hub');
    writePref('k', 'combat');
    expect(readPrefAs('k', (v) => v === 'combat', 'hub')).toBe('combat');
  });
});
