import { describe, it, expect } from 'vitest';
import { bossGearProgress, BOSS_REQUIRED_QUALITY_TIER } from '../src/lib/ui/victories';

// Task 72: "you wear 3 of 8" under a locked region. The rule is the wall's own
// calibration - eight combat slots, the boss's region or later, its rarity or
// better - and tools never count.
describe('bossGearProgress', () => {
  const regionOf = (id: string) => Number(id.split(':')[0]);
  const need = BOSS_REQUIRED_QUALITY_TIER[1]; // region 2

  it('counts combat slots at the region and rarity, or better', () => {
    const pieces = [
      { SlotIndex: 0, QualityTier: need, BaseItemId: '2:sword' },
      { SlotIndex: 1, QualityTier: need + 2, BaseItemId: '3:helm' }, // later region, higher rarity
      { SlotIndex: 2, QualityTier: need - 1, BaseItemId: '2:chest' }, // one rarity short
      { SlotIndex: 3, QualityTier: 14, BaseItemId: '1:gloves' }, // a region behind
      { SlotIndex: 8, QualityTier: 14, BaseItemId: '5:axe' }, // a tool
    ];
    expect(bossGearProgress(2, pieces, regionOf)).toEqual({ meets: 2, of: 8, tier: need });
  });

  it('counts a slot once however it is listed', () => {
    const pieces = [
      { SlotIndex: 4, QualityTier: need, BaseItemId: '2:a' },
      { SlotIndex: 4, QualityTier: need, BaseItemId: '2:b' },
    ];
    expect(bossGearProgress(2, pieces, regionOf).meets).toBe(1);
  });
});
