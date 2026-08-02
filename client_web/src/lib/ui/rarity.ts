// Modul: rarity lookup. The 14 GDD quality tiers.
//
// Kept separate from the five-tier AFFIX rarity that drives magnitude - two
// different axes that this project has conflated before. A quality tier drives
// the affix COUNT and the display colour; affix rarity drives each affix's
// size. Nothing here touches the latter.

export const RARITY_TIER_NAMES: readonly string[] = [
  'None',
  'Crude',
  'Common',
  'Uncommon',
  'Fine',
  'Rare',
  'Superior',
  'Epic',
  'Masterwork',
  'Legendary',
  'Mythic',
  'Ancient',
  'Divine',
  'Celestial',
  'Eternal',
];

export const MAX_QUALITY_TIER = 14;

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
