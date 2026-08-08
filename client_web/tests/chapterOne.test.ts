import { describe, it, expect } from 'vitest';
import {
  CHAPTER_ONE,
  chapterOneState,
  completedCount,
  isChapterComplete,
  nextDeed,
} from '../src/lib/stores/chapterOne';
import type { StateUpdate } from '../src/lib/net/protocol.generated';

/** A snapshot with everything at zero, overridden per test. */
function snap(over: Partial<StateUpdate> = {}): StateUpdate {
  return {
    CurrentLevel: 1,
    EquippedWeaponId: 0,
    Food1_Count: 0,
    Food2_Count: 0,
    Food3_Count: 0,
    CachedWoodStock: 0,
    TotalItemsCraftedCount: 0,
    ...over,
  } as unknown as StateUpdate;
}

describe('Chapter I as a teaching order', () => {
  it('starts a brand new player with nothing done and points at the first fight', () => {
    const fresh = snap();
    expect(completedCount(fresh)).toBe(0);
    expect(nextDeed(fresh)?.id).toBe('first-blood');
  });

  it('points at the FIRST unfinished deed, not the nearest to done', () => {
    // 99 of 100 wood is nearly finished, but a player who has not won a fight
    // is not ready to be sent chopping - the order is the lesson.
    const odd = snap({ CachedWoodStock: 99 });
    expect(nextDeed(odd)?.id).toBe('first-blood');
  });

  it('walks the whole chapter in order as each fact becomes true', () => {
    const state: Partial<StateUpdate> = {};
    const expected = [
      'first-blood',
      'dress-up',
      'stock-larder',
      'hundred-logs',
      'first-craft',
      'level-ten',
    ];
    const satisfy: Partial<StateUpdate>[] = [
      { CurrentLevel: 2 },
      { EquippedWeaponId: 41 },
      { Food1_Count: 12 },
      { CachedWoodStock: 100 },
      { TotalItemsCraftedCount: 1 },
      { CurrentLevel: 10 },
    ];

    for (let i = 0; i < expected.length; i++) {
      expect(nextDeed(snap(state))?.id).toBe(expected[i]);
      Object.assign(state, satisfy[i]);
    }

    expect(nextDeed(snap(state))).toBeNull();
    expect(isChapterComplete(snap(state))).toBe(true);
  });

  it('counts food in any larder slot, not just the first', () => {
    // Reading slot one alone once told players with food in slots two or three
    // to fill a larder that was already full.
    for (const slot of ['Food1_Count', 'Food2_Count', 'Food3_Count'] as const) {
      const s = snap({ CurrentLevel: 2, EquippedWeaponId: 1, [slot]: 5 });
      expect(nextDeed(s)?.id).not.toBe('stock-larder');
    }
  });

  it('reports partial progress toward a counted deed', () => {
    const half = chapterOneState(snap({ CachedWoodStock: 45 })).find((d) => d.id === 'hundred-logs');
    expect(half?.current).toBe(45);
    expect(half?.target).toBe(100);
    expect(half?.done).toBe(false);
  });

  it('clamps progress at the target so a bar can never overrun', () => {
    const rich = chapterOneState(snap({ CachedWoodStock: 999_999, CurrentLevel: 80 }));
    for (const deed of rich) {
      expect(deed.current).toBeLessThanOrEqual(deed.target);
    }
  });

  it('treats a missing snapshot as nothing done rather than throwing', () => {
    expect(completedCount(null)).toBe(0);
    expect(nextDeed(null)).toBeNull();
    expect(isChapterComplete(null)).toBe(false);
  });

  it('gives every deed a title, a body naming what to do, and a real target', () => {
    for (const deed of CHAPTER_ONE) {
      expect(deed.title.length).toBeGreaterThan(0);
      expect(deed.body.length).toBeGreaterThan(20);
      expect(deed.target).toBeGreaterThan(0);
    }
    // Six, as the spec says. A chapter that grows silently is a chapter whose
    // reward no longer matches its cost.
    expect(CHAPTER_ONE.length).toBe(6);
  });

  it('has no duplicate deed ids', () => {
    expect(new Set(CHAPTER_ONE.map((d) => d.id)).size).toBe(CHAPTER_ONE.length);
  });
});
