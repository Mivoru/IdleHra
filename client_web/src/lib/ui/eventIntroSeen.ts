// Modul: WHETHER THE EVENT INTRO IS STILL TO COME, for anything that must not
// open on top of it (2026-10-20: the big-chest prompt opened over it and its
// scrim swallowed the intro's "Got it" - exercise.mjs caught it). One source
// for the seen key both read; `eventIntroClosed` ticks when it is written,
// because a localStorage write is not reactive.
import { writable } from 'svelte/store';

export const EVENT_INTRO_STORAGE_PREFIX = 'folkidle.eventIntro.';

export const eventIntroClosed = writable(0);

export function eventIntroSeen(key: string): boolean {
  try {
    return localStorage.getItem(EVENT_INTRO_STORAGE_PREFIX + key) === '1';
  } catch {
    return false;
  }
}

export function markEventIntroSeen(key: string): void {
  try {
    localStorage.setItem(EVENT_INTRO_STORAGE_PREFIX + key, '1');
  } catch {
    // Storage blocked: it will show again next session, nothing worse.
  }
  eventIntroClosed.update((n) => n + 1);
}
