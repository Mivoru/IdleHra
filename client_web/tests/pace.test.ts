import { describe, expect, it } from 'vitest';
import { etaSeconds, formatEta, MIN_OBSERVED_MS } from '../src/lib/ui/pace';

describe('time to a goal from its own counter', () => {
  it('extrapolates the observed pace', () => {
    // 10 units in 5 minutes, 20 left -> 10 minutes.
    expect(etaSeconds({ at: 0, value: 0 }, { at: 300_000, value: 10 }, 30)).toBe(600);
  });

  it('says nothing before it has watched long enough', () => {
    expect(etaSeconds({ at: 0, value: 0 }, { at: MIN_OBSERVED_MS - 1, value: 5 }, 10)).toBeNull();
  });

  it('says nothing without progress, or when the counter went backwards', () => {
    expect(etaSeconds({ at: 0, value: 5 }, { at: 600_000, value: 5 }, 10)).toBeNull();
    expect(etaSeconds({ at: 0, value: 5 }, { at: 600_000, value: 2 }, 10)).toBeNull();
  });

  it('refuses to promise a date a month away', () => {
    // 1 unit in 10 minutes, 10,000 left -> ~69 days.
    expect(etaSeconds({ at: 0, value: 0 }, { at: 600_000, value: 1 }, 10_001)).toBeNull();
  });

  it('is coarse on purpose', () => {
    expect(formatEta(30)).toBe('under a minute');
    expect(formatEta(600)).toBe('about 10 min');
    expect(formatEta(3 * 3600 + 400)).toBe('about 3 h');
    expect(formatEta(5 * 86400)).toBe('about 5 days');
  });
});
