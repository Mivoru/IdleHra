import { describe, expect, it } from 'vitest';
import type { HuntingEstimate } from '../src/lib/net/rest';
import { bestPick, estimateLine, killTimeText, safety } from '../src/lib/ui/huntingEstimate';

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

  it('recommends the most XP an hour among open monsters farmed without food', () => {
    const rows: HuntingEstimate[] = [
      { ...base, MonsterId: 91, XpPerHour: 100 },
      { ...base, MonsterId: 92, XpPerHour: 300 },
      { ...base, MonsterId: 93, XpPerHour: 900, SurvivesWithoutFood: false },
      { ...base, MonsterId: 94, XpPerHour: 800, CanDamage: false },
      { ...base, MonsterId: 96, XpPerHour: 700 },
    ];
    expect(bestPick(rows, (id) => id < 95)).toBe(92);
    expect(bestPick(rows, () => true)).toBe(96);
    expect(bestPick([{ ...base, MonsterId: 91 }, { ...base, MonsterId: 92 }], () => true)).toBe(91);
    expect(bestPick(rows, () => false)).toBeNull();
  });
});
