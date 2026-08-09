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
 * Mirrors BossFirstClearRules: until a boss is put down once it carries FIVE
 * times its health and TWICE its attack. A player who just did that fought a
 * different monster from the one they will farm afterwards, and the card
 * should say which.
 */
export const FIRST_CLEAR_HP_MULTIPLIER = 5;
export const FIRST_CLEAR_ATTACK_MULTIPLIER = 2;

export function formatFightDuration(seconds: number): string {
  if (seconds <= 0) return 'moments';
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  if (minutes < 60) return rest === 0 ? `${minutes}m` : `${minutes}m ${rest}s`;
  const hours = Math.floor(minutes / 60);
  return `${hours}h ${minutes % 60}m`;
}
