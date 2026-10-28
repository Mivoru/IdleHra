// Modul: THE PLAYER'S OWN LOOK, two choices kept on this device (owner,
// 2026-10-28).
//
// Scheme - dark parchment-and-oak or light parchment - followed the phone's
// system setting and nothing else, so a player could not pick one and the
// owner could not test the other without changing their whole phone. And the
// seasonal event's look switched itself on with the calendar; a player who did
// not like the Samhain colours had no way back to the ordinary ones.
//
// Both land as attributes on <html>: `data-scheme` ('dark' | 'light', always
// resolved - app.css reads only this, never the media query) and the existing
// `data-event` (see eventTheme.ts, which asks seasonalLookEnabled() first).
// boot.js applies the same two from the same keys before the bundle arrives,
// so a reload does not flash the wrong scheme. These are presentation only:
// whether an event is RUNNING is still the wire's SeasonalEventId.
import { writable } from 'svelte/store';

export type SchemeChoice = 'system' | 'dark' | 'light';
export type SeasonalLook = 'auto' | 'off';

/** Read by boot.js too - keep the strings in step with public/boot.js. */
export const SCHEME_STORAGE_KEY = 'folkidle.scheme';
export const SEASONAL_LOOK_STORAGE_KEY = 'folkidle.seasonalLook';

function read(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function write(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Private mode or blocked storage: the choice holds for this session.
  }
}

function initialScheme(): SchemeChoice {
  const stored = read(SCHEME_STORAGE_KEY);
  return stored === 'dark' || stored === 'light' ? stored : 'system';
}

function initialSeasonal(): SeasonalLook {
  return read(SEASONAL_LOOK_STORAGE_KEY) === 'off' ? 'off' : 'auto';
}

export const schemeChoice = writable<SchemeChoice>(initialScheme());
export const seasonalLook = writable<SeasonalLook>(initialSeasonal());

let currentScheme: SchemeChoice = initialScheme();
let currentSeasonal: SeasonalLook = initialSeasonal();
const systemLight = typeof matchMedia === 'function' ? matchMedia('(prefers-color-scheme: light)') : null;

function resolvedScheme(): 'dark' | 'light' {
  if (currentScheme !== 'system') return currentScheme;
  return systemLight?.matches ? 'light' : 'dark';
}

function paintScheme(): void {
  document.documentElement.dataset.scheme = resolvedScheme();
}

// A player on 'system' who flips the phone to dark mode mid-session follows it.
systemLight?.addEventListener('change', () => {
  if (currentScheme === 'system') paintScheme();
});

/** Called once at startup; boot.js has normally set the same value already. */
export function applyAppearance(): void {
  paintScheme();
}

export function setSchemeChoice(choice: SchemeChoice): void {
  currentScheme = choice;
  write(SCHEME_STORAGE_KEY, choice);
  schemeChoice.set(choice);
  paintScheme();
}

/** False when the player turned the seasonal look off on this device. */
export function seasonalLookEnabled(): boolean {
  return currentSeasonal === 'auto';
}

const seasonalListeners = new Set<() => void>();

/** eventTheme.ts re-applies the event's look through this when the choice flips. */
export function onSeasonalLookChange(listener: () => void): void {
  seasonalListeners.add(listener);
}

export function setSeasonalLook(look: SeasonalLook): void {
  currentSeasonal = look;
  write(SEASONAL_LOOK_STORAGE_KEY, look);
  seasonalLook.set(look);
  for (const listener of seasonalListeners) listener();
}
