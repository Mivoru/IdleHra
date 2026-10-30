import { describe, it, expect, vi } from 'vitest';

// audio.ts reads localStorage and the navigation store at import.
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {} });

const { admitClip, MIN_GAP_MS, MAX_VOICES_PER_CLIP } = await import('../src/lib/ui/audio');

// Modul: owner, 2026-10-31 - after a long absence the sounds stacked into one
// shrill pile. admitClip is the one gate every clip passes; these pin it.
describe('nothing stacks', () => {
  const fresh = () => ({ lastStartAt: new Map(), voices: new Map(), stingerUntil: 0 });

  it('a stinger waits for the one before it to finish', () => {
    const state = fresh();
    expect(admitClip('levelUp', 0, state)).toBe(true);
    state.stingerUntil = 1500;
    expect(admitClip('lootRelic', 100, state)).toBe(false);
    expect(admitClip('achievementUnlock', 1499, state)).toBe(false);
    expect(admitClip('lootRare', 1500, state)).toBe(true);
  });

  it('an ordinary clip keeps its rhythm but cannot pile up', () => {
    const state = fresh();
    state.lastStartAt.set('hitMelee', 1000);
    expect(admitClip('hitMelee', 1000 + MIN_GAP_MS - 1, state)).toBe(false);
    expect(admitClip('hitMelee', 1000 + MIN_GAP_MS, state)).toBe(true);
    state.voices.set('hitMelee', MAX_VOICES_PER_CLIP);
    expect(admitClip('hitMelee', 5000, state)).toBe(false);
    // A stinger window does not silence combat.
    state.stingerUntil = 99999;
    expect(admitClip('hitRanged', 5000, state)).toBe(true);
  });
});
