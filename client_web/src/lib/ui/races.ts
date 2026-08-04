// Modul: the six playable races, in one place.
//
// This list existed twice - once in Progression.svelte and once about to be
// written into RaceIcon.svelte - and the first copy had already gone stale:
// it stopped at five, so anyone who unlocked the sixth saw "Race 6". A table
// that is copied is a table that drifts, and this one drifts silently because
// a wrong race name looks like a translation gap rather than a bug.

/**
 * The six races, as the design list names them.
 *
 * Modul: 4 and 6 read "Kobold" and "Moosleute" - the server's internal
 * identifiers, which are not what the game is called in any design document or
 * on any piece of art. The art ships Leshy and Bes sheets; the list is human,
 * vila, draugr, leshy, vodnik, bes. The sprite table had the last two the
 * wrong way round on top of that, so a Leshy showed a Bes.
 *
 * The C# side still says Kobold and Moosleute internally. Renaming an
 * identifier used in forty places is a separate change from fixing what a
 * player reads, and only the second one is a bug.
 */
export const RACE_NAMES: Readonly<Record<number, string>> = {
  1: 'Human',
  2: 'Vila',
  3: 'Draugr',
  4: 'Leshy',
  5: 'Vodnik',
  6: 'Bes',
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
