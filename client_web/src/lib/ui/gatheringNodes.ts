// Modul: WHAT A NODE GIVES, by name (task 101).
//
// The Gathering screen said "Working Sunlit Plains - 40 %": where, never what.
// Every location has three nodes (wood, ore, fish), so "where" does not
// identify the job. The node definitions carry no yield - that is the server's
// loot table (ContentRegistry, rows 77-106: each node rolls a common at weight
// 90 and a rare at 10) - and the client is never sent a loot table. This is
// that table's COMMON column written down, the same pairs the Village and the
// guild buffs already mirror; serverMirrors.test.ts reads the server's
// comments row by row so the two cannot drift.

import { VILLAGE_TIER_MATERIALS } from './wikiData';
import { RAW_FISH_BASE_IDS } from '../net/content';
import { nodeLocation } from './locations';

/** The common material a node yields, or '' for an id that is not a node. */
export function nodeYieldBaseId(activityId: number): string {
  const location = nodeLocation(activityId);
  if (location < 1) return '';
  const band = Math.floor(activityId / 1000);
  const tier = VILLAGE_TIER_MATERIALS[location - 1];
  if (band === 1) return tier?.log ?? '';
  if (band === 2) return tier?.ore ?? '';
  if (band === 3) return RAW_FISH_BASE_IDS[(location - 1) * 2] ?? '';
  return '';
}

/** The rare (10 %) material of the same node. */
export function nodeRareBaseId(activityId: number): string {
  const location = nodeLocation(activityId);
  if (location < 1) return '';
  const band = Math.floor(activityId / 1000);
  const tier = VILLAGE_TIER_MATERIALS[location - 1];
  if (band === 1) return tier?.rareLog ?? '';
  if (band === 2) return tier?.rareOre ?? '';
  if (band === 3) return RAW_FISH_BASE_IDS[(location - 1) * 2 + 1] ?? '';
  return '';
}

/** "Mining" -> the verb a status line uses. */
export const PROFESSION_VERBS: Record<number, string> = {
  0: 'Chopping',
  1: 'Mining',
  2: 'Fishing',
};

/**
 * SimulationEngine.MasteryXpForLevel: 50 * (level + 1)^2. Mastery XP is the
 * progress INTO the current level (AdvanceMastery subtracts each requirement
 * as it is met), so a bar is simply xp / this.
 */
export function masteryXpForLevel(level: number): number {
  return 50 * (level + 1) * (level + 1);
}
