import { describe, it, expect } from 'vitest';
import { rarityTitle, powerMultiplier, MAX_QUALITY_TIER } from '../src/lib/ui/rarity';

// Task 74: a rarity name on hover says where it sits and what it is worth.
describe('rarityTitle', () => {
  it('names the tier, its place on the ladder and the base-power multiplier', () => {
    expect(rarityTitle(1)).toBe(`Normal - tier 1 of ${MAX_QUALITY_TIER}, base power x1.00`);
    expect(rarityTitle(MAX_QUALITY_TIER)).toContain(`tier ${MAX_QUALITY_TIER} of ${MAX_QUALITY_TIER}`);
    expect(rarityTitle(8)).toContain(`x${powerMultiplier(8).toFixed(2)}`);
  });

  it('falls back to the bare name outside the ladder', () => {
    expect(rarityTitle(0)).not.toContain('base power');
  });
});
