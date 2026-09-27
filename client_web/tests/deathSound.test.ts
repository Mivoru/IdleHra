import { describe, it, expect, beforeEach, vi } from 'vitest';

/*
  A DEATH SOUNDS LIKE WHO DIED. The sex comes from the Hall, fetched once and
  cached; a failed lookup still makes a sound.
*/

const fetchAncestorsHall = vi.fn();
const playWithFallback = vi.fn();
vi.mock('../src/lib/net/rest', () => ({ fetchAncestorsHall: () => fetchAncestorsHall() }));
vi.mock('../src/lib/ui/audio', () => ({
  playWithFallback: (name: string, fallback: string) => playWithFallback(name, fallback),
}));

const { playDeathFor, isFemaleCharacter, resetDeathSoundCache } = await import('../src/lib/ui/deathSound');

const hall = {
  Members: [
    { CharacterId: 'aaa', IsFemale: true },
    { CharacterId: 'bbb', IsFemale: false },
  ],
};

const settle = () => new Promise((r) => setTimeout(r, 0));

beforeEach(() => {
  resetDeathSoundCache();
  fetchAncestorsHall.mockReset();
  playWithFallback.mockReset();
});

describe('death sound', () => {
  it("plays the woman's clip for a woman and the man's for a man", async () => {
    fetchAncestorsHall.mockResolvedValue(hall);
    playDeathFor('aaa');
    await settle();
    playDeathFor('bbb');
    await settle();
    expect(playWithFallback.mock.calls).toEqual([
      ['playerDiedFemale', 'error'],
      ['playerDied', 'error'],
    ]);
  });

  it('asks the server once, however many deaths', async () => {
    fetchAncestorsHall.mockResolvedValue(hall);
    await Promise.all([isFemaleCharacter('aaa'), isFemaleCharacter('bbb')]);
    await isFemaleCharacter('aaa');
    expect(fetchAncestorsHall).toHaveBeenCalledTimes(1);
  });

  it('still makes a sound when the lookup fails', async () => {
    fetchAncestorsHall.mockRejectedValue(new Error('offline'));
    playDeathFor('aaa');
    await settle();
    expect(playWithFallback).toHaveBeenCalledWith('playerDied', 'error');
  });
});
