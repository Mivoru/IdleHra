/**
 * Per-device conveniences remembered between visits: the last screen, the
 * Chest's filters, the Wiki's tab (task 52).
 *
 * Modul: every access is wrapped. localStorage throws in a private window, with
 * site data blocked, and in some embedded WebViews - and none of these values
 * is worth a screen failing to open. A read that fails answers the default; a
 * write that fails is forgotten. The caller also validates what it reads back,
 * because a value stored by an older build can name a screen or tab that no
 * longer exists.
 */
export const PREF_LAST_SCREEN = 'folkidle.lastScreen';
export const PREF_CHEST_FILTER = 'folkidle.chest.filter';
export const PREF_CHEST_MIN_RARITY = 'folkidle.chest.minRarity';
export const PREF_WIKI_TAB = 'folkidle.wiki.tab';

export function readPref(key: string): string | null {
  try {
    return globalThis.localStorage?.getItem(key) ?? null;
  } catch {
    return null;
  }
}

/** The stored value if `accept` allows it, otherwise `fallback`. */
export function readPrefAs<T extends string>(key: string, accept: (value: string) => value is T, fallback: T): T;
export function readPrefAs(key: string, accept: (value: string) => boolean, fallback: string): string;
export function readPrefAs(key: string, accept: (value: string) => boolean, fallback: string): string {
  const value = readPref(key);
  return value !== null && accept(value) ? value : fallback;
}

export function writePref(key: string, value: string): void {
  try {
    globalThis.localStorage?.setItem(key, value);
  } catch {
    // Remembering is a convenience; failing to is not an error.
  }
}
