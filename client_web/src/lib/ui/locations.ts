// Modul: THE FIVE LOCATIONS HAVE NAMES.
//
// Every screen used to say "Region 1".."Region 5" and gathering said "tier 1"
// .."tier 5" - the same five places wearing two anonymous numbers, neither of
// which matched the art or the monsters. ContentRegistry.LocationNames is the
// server's copy; this is the client's, and the two must not drift.
export const LOCATION_NAMES: readonly string[] = [
  'Sunlit Plains',
  'Whispering Woods',
  'Scorched Wasteland',
  'Frozen Peaks',
  'Shadow Citadel',
];

export const LOCATION_COUNT = LOCATION_NAMES.length;

/** 1-based, matching the server. */
export function locationName(locationIndex: number): string {
  return LOCATION_NAMES[locationIndex - 1] ?? `Location ${locationIndex}`;
}

/**
 * Which location a gathering node sits in, 1-5. Node ids are band + location,
 * so this is the last digit - ContentRegistry.GetNodeLocation, ported.
 */
export function nodeLocation(activityId: number): number {
  const location = activityId % 1000;
  return location >= 1 && location <= LOCATION_COUNT ? location : 0;
}

/** ContentRegistry.GetCanonicalLocation. Ids 91-115, five per location. */
export function monsterLocation(monsterId: number): number {
  if (monsterId < 91 || monsterId > 115) return 0;
  return Math.floor((monsterId - 91) / 5) + 1;
}
