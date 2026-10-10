import { describe, it, expect, beforeEach } from 'vitest';
import { get } from 'svelte/store';
import {
  beginSeasonalFight,
  claimSeasonalDeath,
  noteSeasonalCombatEvent,
  noteSeasonalResults,
  resetSeasonalFightSession,
  seasonalFight,
  SEASONAL_BOSS_CLEARED,
  SEASONAL_BOSS_STARTED,
} from '../src/lib/stores/seasonalFight';

/*
  The Cailleach's fight window (owner, 2026-10-10) reads its outcome from what
  the server states - the Kill event, the death tick naming her boss, results
  69/70 - and never from a health difference. These pin which statement moves
  the window where, and that an earlier fight's events cannot end a new one.
*/
const BOSS = 95;
const refusal = (code: number) => code !== SEASONAL_BOSS_STARTED && code !== SEASONAL_BOSS_CLEARED && code !== 0;

describe('the seasonal fight window', () => {
  beforeEach(() => resetSeasonalFightSession(false));

  it('starts, fights on result 70 and is won by her boss\'s Kill event', () => {
    beginSeasonalFight(1, BOSS, 1, true);
    expect(get(seasonalFight)?.phase).toBe('starting');
    noteSeasonalResults([SEASONAL_BOSS_STARTED], refusal);
    expect(get(seasonalFight)?.phase).toBe('fighting');

    noteSeasonalCombatEvent(1, false, BOSS, 500);
    noteSeasonalCombatEvent(2, true, 91, 20); // another monster's kill is not hers
    expect(get(seasonalFight)?.phase).toBe('fighting');
    noteSeasonalCombatEvent(3, true, BOSS, 1234);
    expect(get(seasonalFight)).toMatchObject({ phase: 'won', xp: 1234, firstClear: true });

    noteSeasonalResults([SEASONAL_BOSS_CLEARED], refusal);
    expect(get(seasonalFight)?.rewardSealed).toBe(true);
  });

  it('claims a death by her boss so the ordinary death card stays shut', () => {
    beginSeasonalFight(2, BOSS, 1, false);
    noteSeasonalResults([SEASONAL_BOSS_STARTED], refusal);
    expect(claimSeasonalDeath(91)).toBe(false);
    expect(claimSeasonalDeath(BOSS)).toBe(true);
    expect(get(seasonalFight)?.phase).toBe('lost');
    // A second death after the window has said so is the death card's again.
    expect(claimSeasonalDeath(BOSS)).toBe(false);
  });

  it('closes on a refusal while starting - the toast says why', () => {
    beginSeasonalFight(3, BOSS, 1, true);
    noteSeasonalResults([47], refusal);
    expect(get(seasonalFight)).toBeNull();
  });

  it('ignores a kill from before Fight was pressed', () => {
    noteSeasonalCombatEvent(10, false, BOSS, 1);
    beginSeasonalFight(1, BOSS, 1, false);
    noteSeasonalResults([SEASONAL_BOSS_STARTED], refusal);
    noteSeasonalCombatEvent(9, true, BOSS, 50); // a late-arriving earlier kill
    expect(get(seasonalFight)?.phase).toBe('fighting');
    noteSeasonalCombatEvent(11, true, BOSS, 50);
    expect(get(seasonalFight)?.phase).toBe('won');
  });

  it('keeps the same player\'s window across a reconnect, renumbered from zero', () => {
    noteSeasonalCombatEvent(40, false, BOSS, 1);
    beginSeasonalFight(1, BOSS, 1, false);
    noteSeasonalResults([SEASONAL_BOSS_STARTED], refusal);
    resetSeasonalFightSession(true);
    noteSeasonalCombatEvent(0, true, BOSS, 7);
    expect(get(seasonalFight)?.phase).toBe('won');

    resetSeasonalFightSession(false);
    expect(get(seasonalFight)).toBeNull();
  });
});
