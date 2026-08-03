// Modul: rarity lookup. The 14 GDD quality tiers.
//
// Kept separate from the five-tier AFFIX rarity that drives magnitude - two
// different axes that this project has conflated before. A quality tier drives
// the affix COUNT and the display colour; affix rarity drives each affix's
// size. Nothing here touches the latter.
//
// THE NAMES WERE WRONG, and wrong in the way that is hardest to notice: they
// were plausible fantasy words in the right order, just not the game's own.
// This file said Fine/Rare/Superior for tiers 4-6 while the rest of the
// project says Rare/Ultra Rare/Epic - an off-by-one from tier 4 upward, so a
// player reading "Rare" was looking at what every other surface calls Ultra
// Rare, and any conversation about a drop threshold would have been between
// two people using the same word for different things.
//
// The canonical list is ClientAffixRegistry._rarityNames, which is also the
// order CombatLootEngine's own weight table is commented against - the two
// agree exactly, which is what makes them the source rather than this file.
//
// Note the indexing: the canonical array is 1-BASED (tier 1 is Normal), so the
// zeroth entry here exists only so a tier can index directly.
export const RARITY_TIER_NAMES: readonly string[] = [
  'None',
  'Normal',
  'Common',
  'Uncommon',
  'Rare',
  'Ultra Rare',
  'Epic',
  'Legendary',
  'Mythic',
  'Relic',
  'Ancient',
  'Divine',
  'Demonic',
  'Godly',
  'Transcendent',
];

export const MAX_QUALITY_TIER = 14;

/**
 * The drop weights CombatLootEngine rolls against, as a share of all drops.
 *
 * Published here because the loot log and the auto-keep threshold both need to
 * say how rare something actually is, and "Legendary" means nothing without a
 * number. Derived from `_explicitWeights` plus the implicit Normal weight of
 * 100, before any loot-luck multiplier - luck scales every tier above Normal,
 * so these are the floor rather than the exact live odds.
 */
export const RARITY_DROP_SHARE: readonly number[] = (() => {
  const weights = [0, 100, 50, 25, 12.5, 5, 2.5, 1, 0.5, 0.1, 0.05, 0.01, 0.005, 0.001, 0.0001];
  const total = weights.reduce((sum, w) => sum + w, 0);
  return weights.map((w) => w / total);
})();

export function rarityColor(qualityTier: number): string {
  if (qualityTier < 1 || qualityTier > MAX_QUALITY_TIER) return 'var(--text-dim)';
  return `var(--rarity-${qualityTier})`;
}

export function rarityName(qualityTier: number): string {
  return RARITY_TIER_NAMES[qualityTier] ?? `Tier ${qualityTier}`;
}

/** Only the top tiers glow, or the effect stops meaning anything. */
export function shouldGlow(qualityTier: number): boolean {
  return qualityTier >= 10;
}

/** One drop in how many is this tier or better. For "Ultra Rare - 1 in 21". */
export function rarityOdds(qualityTier: number): number {
  let share = 0;
  for (let tier = qualityTier; tier <= MAX_QUALITY_TIER; tier++) {
    share += RARITY_DROP_SHARE[tier] ?? 0;
  }
  return share > 0 ? Math.round(1 / share) : 0;
}
