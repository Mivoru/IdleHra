/**
 * Task 73: the small pure pieces of the Home "Right now" card.
 *
 * Modul: kept out of the component so the two sentences that read server
 * numbers can be tested. Nothing here invents a constant: the offline cap is
 * the server's own OfflineCapSeconds (already the EFFECTIVE cap, with race
 * bonuses) and the boss wall is victories.ts, which mirrors BossFirstClearRules.
 */
import { PREF_LAST_MONSTER, readPref } from '../net/prefs';
import { rarityName } from './rarity';
import { bossGearProgress } from './victories';
import { DELVE_FIRST_ENTRY_GOLD, FORGE_OPEN_LEVEL, MARKET_OPEN_LEVEL } from './unlocks';
import { formatNumber } from './format';


/** Per-character last job, remembered on this device (see prefs.ts). */
export const PREF_LAST_ACTIVITY_PREFIX = 'folkidle.home.lastActivity.';

export function lastActivityKey(characterId: string): string {
  return PREF_LAST_ACTIVITY_PREFIX + characterId;
}

/**
 * The job to offer as "Continue". Slot 1 falls back to Combat's own
 * remembered monster (task 72), so a player who only ever fought from Combat
 * still gets a one-tap resume. 0 means nothing remembered.
 */
export function rememberedActivity(characterId: string, slot: number): number {
  const own = Number(readPref(lastActivityKey(characterId)) ?? 0);
  if (Number.isInteger(own) && own > 0) return own;
  if (slot === 1) {
    const monster = Number(readPref(PREF_LAST_MONSTER) ?? 0);
    if (Number.isInteger(monster) && monster > 0) return monster;
  }
  return 0;
}

/** "12 h", "12 h 30 min", "45 min" - or null when the server has not said. */
/**
 * Task 109: the nearest SCREEN that is still locked, in the order a new player
 * reaches them, as a sentence - or null when all of them are open. `locked`
 * is the menu's own rule (ui/unlocks.ts through tutorial.ts screenLocks), so
 * the Home line and the greyed menu entry cannot disagree.
 */
export function nearestScreenUnlock(level: number, locked: (screen: string) => string | null): string | null {
  const you = Number.isFinite(level) && level > 0 ? ` - you are level ${level}` : '';
  if (locked('forge')) return `The Forge opens at level ${FORGE_OPEN_LEVEL}${you}.`;
  if (locked('market')) return `The Market and guilds open at level ${MARKET_OPEN_LEVEL}${you}.`;
  if (locked('delve')) return `The Delve opens once you hold ${formatNumber(DELVE_FIRST_ENTRY_GOLD)} gold.`;
  return null;
}

export function formatOfflineCap(seconds: number): string | null {
  if (!Number.isFinite(seconds) || seconds <= 0) return null;
  const minutes = Math.round(seconds / 60);
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  if (h === 0) return `${m} min`;
  return m === 0 ? `${h} h` : `${h} h ${m} min`;
}

/**
 * "Next unlock": the region behind the boss of the region the player is in,
 * and how much of that boss's gear ask is already worn. Null once the last
 * region is open (there is no next one) or before the data is in.
 */
export function nextUnlockLine(
  unlockedRegion: number,
  regionCount: number,
  bossName: string | null,
  pieces: readonly { SlotIndex: number; QualityTier: number; BaseItemId: string }[] | null,
  regionTierOf: (baseItemId: string) => number,
  nearer: string | null = null,
): string | null {
  if (!pieces || unlockedRegion < 1 || unlockedRegion >= regionCount) return nearer;
  const progress = bossGearProgress(unlockedRegion, pieces, regionTierOf);
  // Task 109: not one piece towards the boss yet - the boss line is a goal
  // for weeks from now. Say the unlock that is days (or minutes) away.
  if (progress.meets === 0 && nearer) return nearer;
  const boss = bossName ? `beat ${bossName}` : 'beat the boss';
  return (
    `Region ${unlockedRegion + 1} opens when you ${boss}. ` +
    `You wear ${progress.meets} of ${progress.of} pieces at region ${unlockedRegion} ` +
    `${rarityName(progress.tier)} or better.`
  );
}
