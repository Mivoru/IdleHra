/**
 * The phone's five tab-bar entries, in order: four screens and More.
 *
 * Modul: TASK 95 - THE FIFTH TAB IS "MORE", NOT VILLAGE. Twenty-one of the
 * twenty-six destinations were reachable only through a Menu button at the
 * top-right of the page - scroll to the top, reach the far corner, scan a
 * list. More opens the same grouped nav as a sheet from where the thumb
 * already is, and Village moved into it: it is a place you visit when an
 * upgrade comes due, not one a session lives on.
 *
 * "Home", not "Map": the screen is two-thirds dashboard (what everybody is
 * doing, the next goal) and one-third painted valley.
 */
export const MAIN_TABS = [
  { key: 'hub', label: 'Home', icon: 'map' },
  { key: 'combat', label: 'Combat', icon: 'swords' },
  { key: 'gathering', label: 'Gathering', icon: 'pick' },
  { key: 'character', label: 'Character', icon: 'hero' },
  { key: 'more', label: 'More', icon: 'more' },
] as const;

/** The tab-bar entry that opens the More sheet rather than a screen. */
export const MORE_TAB = 'more';

/**
 * What the desktop hotkeys 1-5 open (task 52). The tab bar's four screens,
 * then Village - which held the fifth tab before More, and a desktop has no
 * sheet for "5" to open.
 */
export const HOTKEY_SCREENS = [
  ...MAIN_TABS.filter((t) => t.key !== MORE_TAB).map((t) => t.key),
  'village',
] as const;

/**
 * The tab a digit key names, or null. Ignored while typing (an input, a
 * textarea, a select, anything editable) and with any modifier held, so chat
 * and the browser's own shortcuts keep their keys.
 */
export function hotkeyTab(event: Pick<KeyboardEvent, 'key' | 'ctrlKey' | 'metaKey' | 'altKey' | 'repeat' | 'target'>): string | null {
  if (event.ctrlKey || event.metaKey || event.altKey || event.repeat) return null;
  const index = ['1', '2', '3', '4', '5'].indexOf(event.key);
  if (index < 0) return null;
  const target = event.target as { isContentEditable?: boolean; closest?: (selector: string) => unknown } | null;
  if (target?.isContentEditable) return null;
  if (target?.closest?.('input, textarea, select, [contenteditable]')) return null;
  return HOTKEY_SCREENS[index];
}
