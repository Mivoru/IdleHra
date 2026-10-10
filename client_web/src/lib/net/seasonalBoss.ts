// The seasonal boss (The Cailleach): REST for the ladder, the StartSeasonalBoss
// command (commands.ts) for the fight. Every tier's boss, strength and reward
// is SeasonalBossRegistry's; this file only types the answer. Whether a tier
// is cleared is ALSO on the wire (SeasonalBossClearedMask), which the panel
// prefers so a clear unlocks the next tier without a refetch.
import { authedGet } from './auth';

export interface SeasonalBossTier {
  Tier: number;
  Region: number;
  BossMonsterId: number;
  BossName: string;
  Diamonds: number;
  Gold: number;
  Currency: number;
  Title: string | null;
  Pet: string | null;
  HpPct: number;
  AttackPct: number;
  Cleared: boolean;
}

export interface SeasonalBossView {
  EventId: number;
  Phase?: number;
  Name?: string;
  Tiers: SeasonalBossTier[];
}

export const seasonalBossKeys = { all: ['seasonalBoss'] as const };

export function fetchSeasonalBoss(): Promise<SeasonalBossView> {
  return authedGet<SeasonalBossView>('/api/v1/seasonal-boss');
}

export function tierCleared(mask: number, tier: number): boolean {
  return (mask & (1 << (tier - 1))) !== 0;
}

export const ROMAN = ['', 'I', 'II', 'III', 'IV', 'V', 'VI'];

/**
 * Why a tier's Fight is off, in words, or null. One copy for the ladder and
 * the fight window's "Next winter": the server's own order of checks
 * (SeasonalBossRegistry.Validate) - the region boss, then the winter below.
 */
export function seasonalTierBlocked(
  t: SeasonalBossTier,
  tiers: readonly SeasonalBossTier[],
  clearedMask: number,
  bossMask: number,
  live: boolean,
): string | null {
  if ((bossMask & (1 << (t.Region - 1))) === 0) return `Beat region ${t.Region}'s boss first.`;
  if (t.Tier > 1 && !tierCleared(clearedMask, t.Tier - 1) && !tiers[t.Tier - 2]?.Cleared) return `Break winter ${ROMAN[t.Tier - 1]} first.`;
  if (!live) return 'Waiting for the connection.';
  return null;
}
