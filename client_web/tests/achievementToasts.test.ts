import { describe, it, expect } from 'vitest';
import {
  createWatermark,
  observeTierTotal,
  diffTiers,
  tierLabel,
  achievementName,
  crossedTierReward,
} from '../src/lib/stores/achievementToasts';

describe('achievement toast edge detection', () => {
  it('never fires on the first packet of a session', () => {
    // The failure this prevents: a player with eight tiers already earned
    // logs in and is buried under eight cards for deeds from months ago.
    const mark = createWatermark();
    expect(observeTierTotal(mark, 8)).toBe(false);
    expect(mark.highWater).toBe(8);
  });

  it('fires when the total rises after a baseline', () => {
    const mark = createWatermark();
    observeTierTotal(mark, 3);
    expect(observeTierTotal(mark, 4)).toBe(true);
  });

  it('does not fire on an unchanged total', () => {
    const mark = createWatermark();
    observeTierTotal(mark, 3);
    expect(observeTierTotal(mark, 3)).toBe(false);
    expect(observeTierTotal(mark, 3)).toBe(false);
  });

  it('does not re-fire when a spent-then-earned total recrosses', () => {
    // The server computes the total LIVE from gold, while the database keeps a
    // high-water mark. Spending below a Treasury threshold drops the byte;
    // earning it back raises it. That second crossing is not an achievement.
    const mark = createWatermark();
    observeTierTotal(mark, 5); // baseline
    expect(observeTierTotal(mark, 6)).toBe(true); // real crossing
    expect(observeTierTotal(mark, 5)).toBe(false); // spent back down
    expect(observeTierTotal(mark, 6)).toBe(false); // earned it again - not new
    expect(observeTierTotal(mark, 7)).toBe(true); // genuinely new
  });

  it('ignores nonsense values rather than treating them as a baseline', () => {
    const mark = createWatermark();
    expect(observeTierTotal(mark, Number.NaN)).toBe(false);
    expect(observeTierTotal(mark, -1)).toBe(false);
    // Still unset, so the next real value is the baseline and does not toast.
    expect(mark.highWater).toBe(-1);
    expect(observeTierTotal(mark, 2)).toBe(false);
  });
});

describe('naming which deed moved', () => {
  it('reports every deed that advanced, not just one', () => {
    // Two tiers crossed in the same checkpoint move the packet total by two
    // and must produce two cards.
    const before = [
      { AchievementId: 2, CompletedTier: 1 },
      { AchievementId: 4, CompletedTier: 1 },
    ];
    const after = [
      { AchievementId: 2, CompletedTier: 2 },
      { AchievementId: 4, CompletedTier: 2 },
    ];
    expect(diffTiers(before, after)).toEqual([
      { achievementId: 2, tier: 2 },
      { achievementId: 4, tier: 2 },
    ]);
  });

  it('shows the tier landed on, not every tier flown past', () => {
    const crossed = diffTiers(
      [{ AchievementId: 3, CompletedTier: 1 }],
      [{ AchievementId: 3, CompletedTier: 3 }],
    );
    expect(crossed).toEqual([{ achievementId: 3, tier: 3 }]);
  });

  it('treats a deed absent from the earlier snapshot as starting at zero', () => {
    // A first-ever tier has no prior row at all.
    expect(diffTiers([], [{ AchievementId: 2, CompletedTier: 1 }])).toEqual([
      { achievementId: 2, tier: 1 },
    ]);
  });

  it('reports nothing when a tier went backwards or stood still', () => {
    const rows = [{ AchievementId: 2, CompletedTier: 2 }];
    expect(diffTiers(rows, rows)).toEqual([]);
    expect(diffTiers(rows, [{ AchievementId: 2, CompletedTier: 1 }])).toEqual([]);
  });

  it('labels tiers as numerals and names every known deed', () => {
    expect(tierLabel(0)).toBe('');
    expect(tierLabel(4)).toBe('IV');
    for (const id of [1, 2, 3, 4]) {
      expect(achievementName(id)).not.toMatch(/^Deed #/);
    }
    expect(achievementName(99)).toBe('Deed #99');
  });
});

describe('pricing the card for the tier just earned', () => {
  it('reads the reward off the snapshot BEFORE the crossing', () => {
    // Before: tier 1 held, the next tier pays 250. After: tier 2 held, the
    // next pays 500. The card for tier 2 must say 250 - 500 belongs to tier 3.
    const before = { AchievementId: 2, CompletedTier: 1, NextTierReward: 250 };
    expect(crossedTierReward(before, 2)).toBe(250);
  });

  it('still prices the final tier, whose post-crossing reward is 0', () => {
    const before = { AchievementId: 2, CompletedTier: 3, NextTierReward: 1000 };
    expect(crossedTierReward(before, 4)).toBe(1000);
  });

  it('shows nothing for a multi-tier jump rather than a partial figure', () => {
    const before = { AchievementId: 3, CompletedTier: 1, NextTierReward: 250 };
    expect(crossedTierReward(before, 3)).toBe(0);
  });

  it('shows nothing when the deed was absent from the earlier snapshot', () => {
    expect(crossedTierReward(undefined, 1)).toBe(0);
  });
});
