// Modul: the screen keys, in one importable place.
//
// They used to exist only as a type derived from App.svelte's own GROUPS
// array, which meant nothing outside App could name a destination - the hub
// could not say "take me to the market" without importing the component that
// renders it.
export const SCREEN_KEYS = [
  'hub',
  'combat',
  'gathering',
  'worldboss',
  'boosts',
  'character',
  'chest',
  'larder',
  'crafting',
  'forge',
  'mailbox',
  'market',
  'social',
  'guildops',
  'village',
  'skills',
  'progression',
  'codex',
  'breeding',
  'store',
  'settings',
] as const;

export type ScreenKey = (typeof SCREEN_KEYS)[number];
