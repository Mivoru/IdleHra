/**
 * The five tab-bar destinations, in order. One list for two readers: the
 * phone's TabBar draws them, and the desktop hotkeys 1-5 (task 52) jump to
 * them - so "3" is always whatever the third tab is.
 */
export const MAIN_TABS = [
  { key: 'hub', label: 'Map', icon: 'map' },
  { key: 'combat', label: 'Combat', icon: 'swords' },
  { key: 'gathering', label: 'Gathering', icon: 'pick' },
  { key: 'character', label: 'Character', icon: 'hero' },
  { key: 'village', label: 'Village', icon: 'house' },
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
  return MAIN_TABS[index].key;
}
