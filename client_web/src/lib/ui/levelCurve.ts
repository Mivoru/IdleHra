// Modul: THE XP A LEVEL COSTS, mirrored from ProgressionEngine.
//
// The Combat screen said "XP 0" at level 40 with no denominator, so a player
// could not tell a level from a week away (task 98). The wire carries
// CurrentXp WITHIN the level (the server subtracts the cost on every level-up,
// SimulationEngine's ApplyBulkExperience loop) but not the cost itself - the
// cost is a formula, so it is mirrored here and serverMirrors.test.ts compares
// both constants with the C# rather than trusting this comment.
export const LEVEL_CURVE_BASE = 220;
export const LEVEL_CURVE_GROWTH = 1.114;
// Modul: 2026-10-07 - the curve gained an opening term (see ProgressionEngine).
export const LEVEL_CURVE_OPENING_LOG = 4.4;
export const LEVEL_CURVE_OPENING_TAU = 6;

/** ProgressionEngine.GetRequiredXpForLevel: the XP that takes `level` to `level + 1`. */
export function xpToNextLevel(level: number): number {
  if (!Number.isFinite(level) || level < 1) return 0;
  const opening = Math.exp(LEVEL_CURVE_OPENING_LOG * (1 - Math.exp(-level / LEVEL_CURVE_OPENING_TAU)));
  return Math.ceil(LEVEL_CURVE_BASE * Math.pow(LEVEL_CURVE_GROWTH, level) * opening);
}
