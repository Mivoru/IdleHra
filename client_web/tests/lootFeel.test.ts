import { describe, it, expect } from 'vitest';
import { LootFeelGate, lootPitch, shouldReplaceReveal, FLASH_INTERVAL_MS } from '../src/lib/ui/lootFeel';

describe('task 50: what a drop earns', () => {
  it('ignores materials and anything below Rare', () => {
    const gate = new LootFeelGate();
    expect(gate.accept(3, 1, 0)).toEqual({ flash: false, reveal: false });
    expect(gate.accept(10, 0, 0)).toEqual({ flash: false, reveal: false });
  });

  it('bursts a Rare row at most once every 5 s', () => {
    const gate = new LootFeelGate();
    expect(gate.accept(4, 1, 1000).flash).toBe(true);
    expect(gate.accept(6, 1, 1000 + FLASH_INTERVAL_MS - 1).flash).toBe(false);
    expect(gate.accept(4, 1, 1000 + FLASH_INTERVAL_MS).flash).toBe(true);
  });

  it('reveals Legendary and up, whatever the flash throttle says', () => {
    const gate = new LootFeelGate();
    gate.accept(5, 1, 0);
    expect(gate.accept(7, 1, 10)).toEqual({ flash: false, reveal: true });
    expect(gate.accept(6, 1, 20000).reveal).toBe(false);
  });

  it('pitches up a semitone per tier above Rare', () => {
    expect(lootPitch(4)).toBe(1);
    expect(lootPitch(16)).toBeCloseTo(Math.pow(2, 10 / 12), 10);
    expect(lootPitch(7)).toBeCloseTo(Math.pow(2, 3 / 12), 10);
  });

  it('a better drop replaces a showing card, a worse one does not', () => {
    expect(shouldReplaceReveal(null, 7)).toBe(true);
    expect(shouldReplaceReveal(9, 8)).toBe(false);
    expect(shouldReplaceReveal(8, 9)).toBe(true);
  });
});
