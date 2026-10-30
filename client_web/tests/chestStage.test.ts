import { describe, it, expect } from 'vitest';
import { existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  chestStills,
  clampRarity,
  particleCount,
  rayCount,
  shakeFor,
  RARITY_LIGHT,
  BURST_PEAK_MS,
  BURST_MS,
  type ChestRarity,
} from '../src/lib/ui/chestStage';

const here = dirname(fileURLToPath(import.meta.url));
const publicDir = join(here, '..', 'public');

describe('chest stage', () => {
  it('every chest it can draw has both stills on disk', () => {
    for (const r of [1, 2, 3, 4] as ChestRarity[]) {
      for (const art of ['loot', 'cosmetic'] as const) {
        const { closed, open } = chestStills(art, r);
        expect(existsSync(join(publicDir, closed)), closed).toBe(true);
        expect(existsSync(join(publicDir, open)), open).toBe(true);
      }
    }
  });

  it('the cosmetic casket is one chest at every rarity; loot changes with it', () => {
    expect(chestStills('cosmetic', 1)).toEqual(chestStills('cosmetic', 4));
    expect(chestStills('loot', 1).closed).not.toBe(chestStills('loot', 4).closed);
  });

  it('clamps whatever the server sends to the four rarities', () => {
    expect([0, 1, 2.4, 3, 4, 9].map(clampRarity)).toEqual([1, 1, 2, 3, 4, 4]);
  });

  it('each tap shakes harder, and the third is the hardest', () => {
    const [a, b, c] = [1, 2, 3].map(shakeFor);
    expect(a.degrees).toBeLessThan(b.degrees);
    expect(b.degrees).toBeLessThan(c.degrees);
    expect(shakeFor(7)).toEqual(c);
  });

  it('rarer chests throw more light, and reduce motion throws none', () => {
    const counts = ([1, 2, 3, 4] as ChestRarity[]).map((r) => particleCount(r, false, 8));
    expect(counts).toEqual([...counts].sort((x, y) => x - y));
    expect(particleCount(4, true, 8)).toBe(0);
    expect(particleCount(4, false, 4)).toBeLessThan(particleCount(4, false, 8));
    expect(rayCount(4)).toBeGreaterThan(rayCount(1));
  });

  it('every rarity has a light colour and the swap happens inside the burst', () => {
    for (const r of [1, 2, 3, 4] as ChestRarity[]) expect(RARITY_LIGHT[r]).toHaveLength(3);
    expect(BURST_PEAK_MS).toBeGreaterThan(0);
    expect(BURST_PEAK_MS).toBeLessThan(BURST_MS);
  });
});
