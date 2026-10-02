import { describe, expect, it } from 'vitest';
import { formatOfflineCap, nearestScreenUnlock, nextUnlockLine } from '../src/lib/ui/homeNow';

describe('home offline cap', () => {
  it('says hours and minutes, and nothing when the server has not said', () => {
    expect(formatOfflineCap(12 * 3600)).toBe('12 h');
    expect(formatOfflineCap(12 * 3600 + 1800)).toBe('12 h 30 min');
    expect(formatOfflineCap(45 * 60)).toBe('45 min');
    expect(formatOfflineCap(0)).toBeNull();
    expect(formatOfflineCap(NaN)).toBeNull();
  });
});

describe('home next unlock', () => {
  const none = () => 1;
  it('is absent before the worn data arrives and after the last region', () => {
    expect(nextUnlockLine(1, 5, 'Boss', null, none)).toBeNull();
    expect(nextUnlockLine(5, 5, 'Boss', [], none)).toBeNull();
  });
  it('names the region, the boss and the count worn', () => {
    const line = nextUnlockLine(1, 5, 'Big Rat', [], none);
    expect(line).toContain('Region 2');
    expect(line).toContain('Big Rat');
    expect(line).toContain('0 of 8');
  });
});

describe('home next unlock on day one (task 109)', () => {
  const none = () => 1;
  const locks = (closed: string[]) => (screen: string) => (closed.includes(screen) ? 'x' : null);
  it('names the nearest locked screen in reach order', () => {
    expect(nearestScreenUnlock(2, locks(['forge', 'market', 'delve']))).toBe('The Forge opens at level 5 - you are level 2.');
    expect(nearestScreenUnlock(7, locks(['market', 'delve']))).toContain('level 10');
    expect(nearestScreenUnlock(12, locks(['delve']))).toMatch(/7\D?000 gold/);
    expect(nearestScreenUnlock(40, locks([]))).toBeNull();
  });
  it('prefers the nearer unlock while not one boss piece is worn', () => {
    expect(nextUnlockLine(1, 5, 'Big Rat', [], none, 'Forge soon')).toBe('Forge soon');
    const worn = [{ SlotIndex: 0, QualityTier: 14, BaseItemId: 'x' }];
    expect(nextUnlockLine(1, 5, 'Big Rat', worn, none, 'Forge soon')).toContain('Big Rat');
  });
});
