// Modul: the six playable races, in one place.
//
// This list existed twice - once in Progression.svelte and once about to be
// written into RaceIcon.svelte - and the first copy had already gone stale:
// it stopped at five, so anyone who unlocked Moosleute saw "Race 6". A table
// that is copied is a table that drifts, and this one drifts silently because
// a wrong race name looks like a translation gap rather than a bug.

/** ContentRegistry.RaceIds. The ids are not contiguous by theme - 5 is Vodnik
 *  and 6 is Moosleute - so this is authored, never derived from a range. */
export const RACE_NAMES: Readonly<Record<number, string>> = {
  1: 'Human',
  2: 'Vila',
  3: 'Draugr',
  4: 'Kobold',
  5: 'Vodnik',
  6: 'Moosleute',
};

export const ALL_RACE_IDS: readonly number[] = [1, 2, 3, 4, 5, 6];

export function raceName(raceId: number): string {
  return RACE_NAMES[raceId] ?? `Race ${raceId}`;
}

/**
 * `UnlockedRaceBitmask` sets bit (raceId - 1), so Human is bit 0 and Moosleute
 * is bit 5. It is a BYTE on the wire - exactly enough for six races, and it
 * would silently stop recording a seventh.
 */
export function isRaceUnlocked(mask: number, raceId: number): boolean {
  return (mask & (1 << (raceId - 1))) !== 0;
}
