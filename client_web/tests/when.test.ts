import { describe, it, expect } from 'vitest';
import { formatRelative, formatLocalTime, formatWhen } from '../src/lib/ui/when';

/*
  ONE TIME CONVENTION (task 95). World Boss said "midnight UTC" and the Delve
  said "Monday, 02:00 CEST" for the same instant. Both now say the player's own
  clock plus how far away it is.
*/
describe('formatRelative', () => {
  const now = new Date('2026-10-02T10:00:00Z');
  const after = (ms: number) => new Date(now.getTime() + ms);

  it('rounds down, so it never promises something early', () => {
    expect(formatRelative(after(3 * 86_400_000 + 23 * 3_600_000), now)).toBe('in 3 d');
    expect(formatRelative(after(5 * 3_600_000 + 59 * 60_000), now)).toBe('in 5 h');
    expect(formatRelative(after(12 * 60_000 + 30_000), now)).toBe('in 12 min');
  });

  it('says now for anything under a minute, including the past', () => {
    expect(formatRelative(after(30_000), now)).toBe('now');
    expect(formatRelative(after(-60_000), now)).toBe('now');
  });
});

describe('formatWhen', () => {
  it('is a short weekday, a 24-hour local time and the distance', () => {
    const at = new Date(2026, 9, 5, 2, 0); // local Monday 02:00
    const now = new Date(2026, 9, 2, 1, 0);
    expect(formatLocalTime(at)).toBe('Mon 02:00');
    expect(formatWhen(at, now)).toBe('Mon 02:00 - in 3 d');
  });

  it('never names a time zone or UTC', () => {
    expect(formatWhen(new Date(), new Date())).not.toMatch(/UTC|GMT|CES?T/);
  });
});
