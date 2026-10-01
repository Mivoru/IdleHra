import { describe, it, expect, beforeEach } from 'vitest';
import {
  formatNumber,
  formatExact,
  formatDecimal,
  formatGold,
  numberTitle,
  isCompacted,
  COMPACT_THRESHOLD,
  GROUP_SEPARATOR,
} from '../src/lib/ui/format';
import { language } from '../src/lib/ui/i18n';

const S = GROUP_SEPARATOR;

/*
  ONE NUMBER FORMAT (task 74). Whole numbers with a thin-space group separator
  up to 100,000, compact above with three significant figures, the exact value
  one hover away, the decimal mark by UI language.
*/
beforeEach(() => language.set('En'));

describe('formatNumber', () => {
  it('writes small numbers whole with a thin-space group separator', () => {
    expect(formatNumber(0)).toBe('0');
    expect(formatNumber(999)).toBe('999');
    expect(formatNumber(1000)).toBe(`1${S}000`);
    expect(formatNumber(17_000)).toBe(`17${S}000`);
    expect(formatNumber(99_999)).toBe(`99${S}999`);
  });

  it('keeps 100,000 whole and compacts strictly above it', () => {
    expect(COMPACT_THRESHOLD).toBe(100_000);
    expect(formatNumber(100_000)).toBe(`100${S}000`);
    expect(formatNumber(100_001)).toBe(`100${S}k`);
    expect(formatNumber(123_456)).toBe(`123${S}k`);
    expect(formatNumber(345_678)).toBe(`346${S}k`);
  });

  it('carries 999,999 over into millions rather than printing 1000 k', () => {
    expect(formatNumber(999_499)).toBe(`999${S}k`);
    expect(formatNumber(999_999)).toBe(`1${S}M`);
    expect(formatNumber(1_000_000)).toBe(`1${S}M`);
  });

  it('compacts millions, billions and trillions to three significant figures', () => {
    expect(formatNumber(1_240_000)).toBe(`1.24${S}M`);
    expect(formatNumber(5_042_484)).toBe(`5.04${S}M`);
    expect(formatNumber(17_836_000)).toBe(`17.8${S}M`);
    expect(formatNumber(152_100_000)).toBe(`152${S}M`);
    expect(formatNumber(1_240_000_000)).toBe(`1.24${S}B`);
    expect(formatNumber(3_500_000_000_000)).toBe(`3.5${S}T`);
  });

  it('drops trailing zeros rather than implying precision it threw away', () => {
    expect(formatNumber(2_500_000)).toBe(`2.5${S}M`);
    expect(formatNumber(10_000_000)).toBe(`10${S}M`);
  });

  it('keeps the sign', () => {
    expect(formatNumber(-5)).toBe('-5');
    expect(formatNumber(-99_999)).toBe(`-99${S}999`);
    expect(formatNumber(-100_000)).toBe(`-100${S}000`);
    expect(formatNumber(-5_042_484)).toBe(`-5.04${S}M`);
  });

  it('rounds non-integers to whole numbers and never prints -0', () => {
    expect(formatNumber(1234.5)).toBe(`1${S}235`);
    expect(formatNumber(1234.4)).toBe(`1${S}234`);
    expect(formatNumber(0.4)).toBe('0');
    expect(formatNumber(-0.4)).toBe('0');
  });

  it('never renders NaN or Infinity at a player', () => {
    expect(formatNumber(Number.NaN)).toBe('0');
    expect(formatNumber(Number.POSITIVE_INFINITY)).toBe('0');
    expect(formatExact(Number.NaN)).toBe('0');
  });

  it('accepts the shapes the wire actually delivers', () => {
    expect(formatNumber('5042484')).toBe(`5.04${S}M`);
    expect(formatNumber(5_042_484n)).toBe(`5.04${S}M`);
    expect(formatExact(5_042_484n)).toBe(formatExact(5_042_484));
  });

  it('takes an earlier cut for the small item badge', () => {
    expect(formatNumber(9_999, 9_999)).toBe(`9${S}999`);
    expect(formatNumber(12_345, 9_999)).toBe(`12.3${S}k`);
    expect(numberTitle(12_345, 9_999)).toBe(`12${S}345`);
  });

  it('uses a decimal comma outside English, and the same thin space', () => {
    language.set('Cs');
    expect(formatNumber(5_042_484)).toBe(`5,04${S}M`);
    expect(formatNumber(12_345)).toBe(`12${S}345`);
    language.set('En');
    expect(formatNumber(5_042_484)).toBe(`5.04${S}M`);
  });
});

describe('formatExact and numberTitle', () => {
  it('writes the full figure, grouped, never compacted', () => {
    expect(formatExact(5_042_484)).toBe(`5${S}042${S}484`);
    expect(formatExact(-1234)).toBe(`-1${S}234`);
    expect(formatExact(0)).toBe('0');
  });

  it('gives a title only where the number was compacted', () => {
    expect(numberTitle(99_999)).toBeUndefined();
    expect(numberTitle(100_000)).toBeUndefined();
    expect(numberTitle(100_001)).toBe(`100${S}001`);
    expect(numberTitle(-5_042_484)).toBe(`-5${S}042${S}484`);
  });

  it('agrees with isCompacted about when the exact figure is worth publishing', () => {
    // Modul: this pairing keeps `data-exact`/title honest - a shortened number
    // must always have its exact figure behind it.
    for (const value of [0, 999, 99_999, 100_000, 100_001, 999_999, 1_000_000, -100_001]) {
      expect(isCompacted(value)).toBe(formatNumber(value) !== formatExact(value));
    }
  });
});

describe('formatDecimal', () => {
  it('groups and applies the language decimal mark', () => {
    expect(formatDecimal(1.25, 1)).toBe('1.3');
    expect(formatDecimal(1234.5, 1)).toBe(`1${S}234.5`);
    expect(formatDecimal(2, 1)).toBe('2');
    language.set('De');
    expect(formatDecimal(1.5, 1)).toBe('1,5');
  });
});

describe('formatGold', () => {
  it('keeps the terse g on a figure written out in full', () => {
    expect(formatGold(950)).toBe('950g');
    expect(formatGold(100_000)).toBe(`100${S}000g`);
  });

  it('spells the unit out once compacted, so it cannot read as kg or Mg', () => {
    expect(formatGold(150_000)).toBe(`150${S}k gold`);
    expect(formatGold(1_240_000)).toBe(`1.24${S}M gold`);
  });
});
