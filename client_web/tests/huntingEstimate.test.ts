import { describe, expect, it } from 'vitest';
import type { HuntingEstimate } from '../src/lib/net/rest';
import { estimateLine, killTimeText, safety } from '../src/lib/ui/huntingEstimate';

const base: HuntingEstimate = {
  MonsterId: 91,
  CanDamage: true,
  SecondsPerKill: 9.3,
  SecondsPerKillLow: 7.5,
  SecondsPerKillHigh: 12,
  XpPerHour: 12_300,
  GoldPerHour: 4_100,
  SurvivesWithFood: true,
  SurvivesWithoutFood: true,
  KillsBeforeDeathWithoutFood: 0,
  FoodPerHour: 0,
};

describe('huntingEstimate', () => {
  it('shows the kill time as a band', () => {
    expect(killTimeText(base)).toBe('7.5-12 s');
    expect(killTimeText({ ...base, SecondsPerKillLow: 4, SecondsPerKillHigh: 4 })).toBe('4.0 s');
    expect(killTimeText({ ...base, SecondsPerKillLow: 120, SecondsPerKillHigh: 300 })).toBe('2-5 min');
  });

  it('says what keeps the character alive', () => {
    expect(safety(base).tone).toBe('safe');
    expect(safety({ ...base, SurvivesWithoutFood: false, FoodPerHour: 41.2 })).toEqual({
      tone: 'food',
      text: 'needs food (~42/h)',
    });
    expect(safety({ ...base, SurvivesWithoutFood: false, SurvivesWithFood: false }).tone).toBe('danger');
  });

  it('reads "Estimate" - it is a projection, not a promise', () => {
    expect(estimateLine(base).startsWith('Estimate: ')).toBe(true);
    expect(estimateLine({ ...base, CanDamage: false })).toBe('Estimate: you cannot hurt it yet');
  });
});
