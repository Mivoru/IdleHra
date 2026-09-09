import { describe, it, expect } from 'vitest';
import { formatCompact, formatExact, isCompacted, COMPACT_THRESHOLD } from '../src/lib/ui/format';

/*
  HOW A BIG NUMBER IS WRITTEN DOWN.

  The two properties worth pinning are the boundary - because a threshold that
  drifts by one changes what every screen shows - and that compaction never
  loses the sign or invents precision.
*/
describe('formatCompact', () => {
  it('writes six digits and under in full, because the separator still works there', () => {
    expect(formatCompact(0)).toBe('0');
    expect(formatCompact(999)).toBe(formatExact(999));
    expect(formatCompact(17_000)).toBe(formatExact(17_000));
    expect(formatCompact(250_000)).toBe(formatExact(250_000));
    expect(formatCompact(COMPACT_THRESHOLD - 1)).toBe(formatExact(COMPACT_THRESHOLD - 1));
  });

  it('compacts from a million', () => {
    expect(formatCompact(1_000_000)).toBe('1M');
    expect(formatCompact(5_042_484)).toBe('5.04M');
    expect(formatCompact(17_836_000)).toBe('17.8M');
    expect(formatCompact(152_100_000)).toBe('152M');
    expect(formatCompact(1_240_000_000)).toBe('1.24B');
    expect(formatCompact(3_500_000_000_000)).toBe('3.5T');
  });

  it('drops trailing zeros rather than implying precision it threw away', () => {
    // "1.00M" is longer than "1M" and says nothing more.
    expect(formatCompact(1_000_000)).toBe('1M');
    expect(formatCompact(2_500_000)).toBe('2.5M');
    expect(formatCompact(10_000_000)).toBe('10M');
  });

  it('keeps the sign', () => {
    expect(formatCompact(-5_042_484)).toBe('-5.04M');
    expect(formatCompact(-999)).toBe(formatExact(-999));
  });

  it('never renders NaN or Infinity at a player', () => {
    expect(formatCompact(Number.NaN)).toBe('0');
    expect(formatCompact(Number.POSITIVE_INFINITY)).toBe('0');
    expect(formatExact(Number.NaN)).toBe('0');
  });

  it('accepts the shapes the wire actually delivers', () => {
    // StateUpdate fields arrive as numbers, some ledgers as strings, and the
    // gold column is a bigint on the server side.
    expect(formatCompact('5042484')).toBe('5.04M');
    expect(formatCompact(5_042_484n)).toBe('5.04M');
    expect(formatExact(5_042_484n)).toBe(formatExact(5_042_484));
  });

  it('agrees with isCompacted about when the exact figure is worth publishing', () => {
    // Modul: this pairing is what keeps `data-exact` honest. A component asks
    // isCompacted whether to publish the full value; if the two disagreed, a
    // shortened number could appear with no exact figure behind it, and the
    // checkers that read the attribute would silently fall back to parsing
    // display text - which is the coupling this whole file exists to remove.
    for (const value of [0, 999, 999_999, 1_000_000, 5_042_484, -5_042_484]) {
      expect(isCompacted(value)).toBe(formatCompact(value) !== formatExact(value));
    }
  });
});
