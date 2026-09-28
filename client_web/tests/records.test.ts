import { describe, it, expect } from 'vitest';
import { RecordWatch, HIT_TOAST_INTERVAL_MS, formatTenths, type RecordFields } from '../src/lib/stores/records';

const packet = (hit: number, bosses: number[] = [0, 0, 0, 0, 0]): RecordFields => ({
  BestHit: hit,
  BossBestKillTenthsR1: bosses[0],
  BossBestKillTenthsR2: bosses[1],
  BossBestKillTenthsR3: bosses[2],
  BossBestKillTenthsR4: bosses[3],
  BossBestKillTenthsR5: bosses[4],
});

describe('task 51: New record toasts', () => {
  it('treats the first packet as a baseline, so a relogin announces nothing', () => {
    const w = new RecordWatch();
    expect(w.observe(packet(900, [300, 0, 0, 0, 0]), 0)).toEqual([]);
    expect(w.observe(packet(900, [300, 0, 0, 0, 0]), 1000)).toEqual([]);
  });

  it('says a hit record, then holds further ones for two minutes and says the latest', () => {
    const w = new RecordWatch();
    w.observe(packet(100), 0);
    expect(w.observe(packet(150), 1000)).toEqual(['New record: highest hit 150']);
    expect(w.observe(packet(170), 2000)).toEqual([]);
    expect(w.observe(packet(190), 1000 + HIT_TOAST_INTERVAL_MS)).toEqual(['New record: highest hit 190']);
  });

  it('says a first boss kill and a faster one, never a slower one', () => {
    const w = new RecordWatch();
    w.observe(packet(0), 0);
    expect(w.observe(packet(0, [0, 425, 0, 0, 0]), 1)).toEqual(['New record: region 2 boss in 42.5 s']);
    expect(w.observe(packet(0, [0, 425, 0, 0, 0]), 2)).toEqual([]);
    expect(w.observe(packet(0, [0, 401, 0, 0, 0]), 3)).toEqual(['New record: region 2 boss in 40.1 s']);
  });

  it('says a drop record only once the durable baseline is known, and only from Rare', () => {
    const w = new RecordWatch();
    expect(w.observeDrop(9, 1)).toBe(false); // no baseline yet
    w.seedDrop(2);
    expect(w.observeDrop(3, 1)).toBe(false); // a record, but below Rare
    expect(w.observeDrop(5, 0)).toBe(false); // a material
    expect(w.observeDrop(5, 1)).toBe(true);
    expect(w.observeDrop(5, 1)).toBe(false); // a tie is not a record
  });

  it('formats a fight length', () => {
    expect(formatTenths(0)).toBe('-');
    expect(formatTenths(425)).toBe('42.5 s');
    expect(formatTenths(834)).toBe('1:23.4');
  });
});
