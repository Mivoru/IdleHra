import { rarityName } from './rarity';
import { AFFIX_RARITY_NAMES } from './affixes';
// Modul: what a first boss clear actually GAVE you.
//
// The rewards are on the wire; the UNLOCKS are not, and they are the
// interesting half - a boss opens a playable race, a new region and the five
// monsters in it, and until now a player found that out by noticing a new
// option on some other screen days later.
//
// Derived here rather than sent, because every input is static content the
// client already has: the boss id says which region it belonged to, the region
// ladder says what opens next, and the monster table says what lives there. A
// server field would be a second copy of a mapping that cannot change without
// changing the content both sides already read.
import { raceName } from './races';

/** Boss monster ids are `90 + region * 5` - the last id in each region's block
 *  of five. Mirrors RaceUnlockRegistry.GetRegionBossMonsterId. */
export const FIRST_REGION = 1;
export const LAST_REGION = 5;
export const MONSTERS_PER_REGION = 5;
export const FIRST_CANONICAL_MONSTER_ID = 91;

export function regionOfBoss(monsterId: number): number {
  for (let region = FIRST_REGION; region <= LAST_REGION; region++) {
    if (90 + region * 5 === monsterId) return region;
  }
  return 0;
}

/**
 * Which race a boss unlocks, or 0.
 *
 * Mirrors RaceUnlockRegistry.GetRaceUnlockedByBoss, and is keyed on the BOSS
 * ID rather than derived from a region tier - deriving it is the mistake that
 * once pulled 41 creatures into region 1.
 */
export function raceUnlockedByBoss(monsterId: number): number {
  switch (monsterId) {
    case 95:
      return 2; // Vila
    case 100:
      return 3; // Draugr
    case 105:
      return 4; // Leshy
    case 110:
      return 5; // Vodnik
    case 115:
      return 6; // Bes
    default:
      return 0;
  }
}

export interface VictoryUnlocks {
  /** The region this boss guarded. */
  clearedRegion: number;
  /** Name of the race this clear granted, or null. */
  raceUnlocked: string | null;
  /** The region that just opened, or null when this was the last boss. */
  openedRegion: number | null;
  /** Monster ids of the region that opened. */
  openedMonsterIds: number[];
}

export function unlocksFor(monsterId: number): VictoryUnlocks {
  const clearedRegion = regionOfBoss(monsterId);
  const raceId = raceUnlockedByBoss(monsterId);

  // Clearing region 5 opens no sixth region - it is the last. Saying "you
  // unlocked region 6" there would be a lie the content cannot back.
  const openedRegion = clearedRegion > 0 && clearedRegion < LAST_REGION ? clearedRegion + 1 : null;

  const openedMonsterIds: number[] = [];
  if (openedRegion !== null) {
    const first = FIRST_CANONICAL_MONSTER_ID + (openedRegion - 1) * MONSTERS_PER_REGION;
    for (let i = 0; i < MONSTERS_PER_REGION; i++) openedMonsterIds.push(first + i);
  }

  return {
    clearedRegion,
    raceUnlocked: raceId > 0 ? raceName(raceId) : null,
    openedRegion,
    openedMonsterIds,
  };
}

/**
 * What the first clear cost extra, for saying so out loud.
 *
 * Mirrors BossFirstClearRules, which is a TABLE and not a pair of constants
 * since 2026-09-12: a flat 5x health and 2x attack for every boss is what let a
 * full set of region-4 gear beat Malakor, the last monster in the game. Indexed
 * by region, 1-based - entry 0 is unused so a region can index directly.
 *
 * serverMirrors.test.ts parses the C# arrays and compares these element by
 * element. Do not edit one side alone.
 */
export const FIRST_CLEAR_HP_MULTIPLIERS: readonly number[] = [3, 4, 6, 9, 14];
export const FIRST_CLEAR_ATTACK_MULTIPLIERS: readonly number[] = [3.7, 2.6, 5.7, 11.4, 21.4];

/**
 * The gear each boss is calibrated to need, as QualityTier, in the boss's OWN
 * region - mirrors BossFirstClearRules._requiredQualityTierByRegion.
 *
 * The screen says this out loud because the wall is brutal by design: a full set
 * one region behind dies to its boss in about two seconds. A death that fast
 * with no explanation is indistinguishable from a bug, which is how the
 * ORIGINAL defect here got reported.
 */
export const BOSS_REQUIRED_QUALITY_TIER: readonly number[] = [4, 7, 8, 10, 11];

/** Affix rarity each boss expects, as AffixRarity (1 Common .. 5 Legendary). */
export const BOSS_REQUIRED_AFFIX_RARITY: readonly number[] = [1, 1, 3, 4, 5];

/**
 * The region a monster is the boss OF, or 0 when it is not a region boss.
 *
 * Mirrors RaceUnlockRegistry: the canonical 25 run from monster id 91 and every
 * fifth one is its region's boss. Lives here rather than in a screen because
 * both the Combat list and the victory card index the tables below by it, and a
 * second copy of this arithmetic is how the "every fifth monster" convention has
 * mis-classified content before.
 */
export function bossRegionOf(monsterId: number): number {
  const offset = monsterId - 91;
  if (offset < 0 || offset >= 25) return 0;
  return offset % 5 === 4 ? Math.floor(offset / 5) + 1 : 0;
}

/**
 * What a boss asks for, in one sentence a player can act on.
 *
 * Modul: TWO RARITY SCALES SHARE THEIR WORDS. The 14 quality tiers and the 5
 * affix rarities both have a "Legendary", and the first version of this tooltip
 * printed "region-2 gear at Legendary or better with Common affixes" - quality
 * tier 7 and affix rarity 1, both correct and together unreadable. The tier is
 * named AS a tier here, and the affix clause is dropped entirely when the
 * requirement is Common, which every affix already is.
 *
 * Said out loud at all because the wall is brutal by design: a full set one
 * region behind dies to its boss in about two seconds. A death that fast with no
 * explanation is indistinguishable from a bug - which is how the defect that
 * started this work got reported in the first place.
 */
export function describeBossGearRequirement(region: number): string {
  const tier = BOSS_REQUIRED_QUALITY_TIER[region - 1] ?? BOSS_REQUIRED_QUALITY_TIER[0];
  const affixRarity = BOSS_REQUIRED_AFFIX_RARITY[region - 1] ?? BOSS_REQUIRED_AFFIX_RARITY[0];

  let text =
    `Bring a full set of region-${region} gear at rarity ${tier} (${rarityName(tier)}) or better` +
    ` - gear from the previous region will not survive it.`;

  if (affixRarity > 1) {
    text += ` Its affixes want to be ${AFFIX_RARITY_NAMES[affixRarity]}` +
      `${affixRarity < 5 ? ' or better' : ''}, too.`;
  }

  return text;
}

/** The first-clear health multiplier for a boss's region, 1-based. */
export function firstClearHpMultiplier(region: number): number {
  return FIRST_CLEAR_HP_MULTIPLIERS[region - 1] ?? FIRST_CLEAR_HP_MULTIPLIERS[0];
}

/** The first-clear attack multiplier for a boss's region, 1-based. */
export function firstClearAttackMultiplier(region: number): number {
  return FIRST_CLEAR_ATTACK_MULTIPLIERS[region - 1] ?? FIRST_CLEAR_ATTACK_MULTIPLIERS[0];
}

export function formatFightDuration(seconds: number): string {
  if (seconds <= 0) return 'moments';
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  if (minutes < 60) return rest === 0 ? `${minutes}m` : `${minutes}m ${rest}s`;
  const hours = Math.floor(minutes / 60);
  return `${hours}h ${minutes % 60}m`;
}
