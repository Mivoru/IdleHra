import { writable } from 'svelte/store';
import { readPref, writePref } from '../net/prefs';

/**
 * Modul: THE CHARACTER YOU PICKED IS THE ONE THAT WORKS (2026-10-08).
 *
 * Character, Combat, Gathering and Crafting each kept their own "which
 * character" - Combat had none at all and always sent slot 1. So a player who
 * picked slot 3 on the Character screen and pressed Fight watched slot 1 walk
 * off its gathering node into the fight. One choice now, shared by every
 * screen that assigns work, and remembered on this device.
 *
 * Holds a SLOT (1-3), not a character id: a slot is what every snapshot field
 * is keyed by, and screens already fall back to the first person present when
 * the slot is empty or locked.
 */
const PREF_SELECTED_SLOT = 'folkidle.character.selectedSlot';

function initialSlot(): number {
  const stored = Number(readPref(PREF_SELECTED_SLOT) ?? 1);
  return stored >= 1 && stored <= 3 ? stored : 1;
}

export const selectedCharacterSlot = writable<number>(initialSlot());

selectedCharacterSlot.subscribe((slot) => {
  if (slot >= 1 && slot <= 3) writePref(PREF_SELECTED_SLOT, String(slot));
});
