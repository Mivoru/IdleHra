import { describe, it, expect } from 'vitest';
import { LootFeelGate, LootSoundGate, lootClipFor, shouldReplaceReveal, FLASH_INTERVAL_MS, LOOT_SOUND_COOLDOWN_MS } from '../src/lib/ui/lootFeel';

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

  it('picks a clip by band, never by pitch', () => {
    expect(lootClipFor(3)).toBeNull();
    expect(lootClipFor(4)).toBe('lootRare');
    expect(lootClipFor(6)).toBe('lootRare');
    expect(lootClipFor(7)).toBe('lootLegendary');
    expect(lootClipFor(8)).toBe('lootLegendary');
    expect(lootClipFor(9)).toBe('lootRelic');
    expect(lootClipFor(16)).toBe('lootRelic');
  });

  it('a burst of drops is one sound, at its best band', () => {
    // An offline catch-up: thirty drops in one window.
    const gate = new LootSoundGate();
    let windows = 0;
    for (const tier of [4, 5, 4, 8, 4, 6, 10, 4, ...Array(22).fill(5)]) if (gate.offer(tier)) windows++;
    expect(windows).toBe(1);
    expect(gate.flush(1000)).toBe('lootRelic');
    expect(gate.flush(1001)).toBeNull(); // nothing pending
  });

  it('inside the cooldown only a better band sounds again', () => {
    const gate = new LootSoundGate();
    gate.offer(7);
    expect(gate.flush(0)).toBe('lootLegendary');
    gate.offer(8);
    expect(gate.flush(100)).toBeNull();
    gate.offer(4);
    expect(gate.flush(200)).toBeNull();
    gate.offer(11);
    expect(gate.flush(300)).toBe('lootRelic');
    gate.offer(4);
    expect(gate.flush(300 + LOOT_SOUND_COOLDOWN_MS)).toBe('lootRare');
    expect(gate.offer(2)).toBe(false); // below Rare is silent
  });

  it('a better drop replaces a showing card, a worse one does not', () => {
    expect(shouldReplaceReveal(null, 7)).toBe(true);
    expect(shouldReplaceReveal(9, 8)).toBe(false);
    expect(shouldReplaceReveal(8, 9)).toBe(true);
  });
});
