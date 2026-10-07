import { describe, it, expect } from 'vitest';
import {
  claimableSteps,
  describeReward,
  nextQuest,
  pickGuideTarget,
  questLineFinished,
  questSummary,
  spotlightStillWanted,
} from '../src/lib/ui/questLine';
import type { QuestLine, QuestState, QuestStep } from '../src/lib/net/quests';

/*
  The quest panel's RULES. The server decides what is done and what it pays
  (QuestLineEngine); these pin only what the client chooses to SHOW from the
  server's answer - which step is "next", when the panel retires, when a
  highlight is still wanted.
*/

function step(order: number, state: QuestState, overrides: Partial<QuestStep> = {}): QuestStep {
  return {
    Id: `step${order}`,
    Order: order,
    Title: `Step ${order}`,
    Explanation: 'Do the thing.',
    Screen: 'forge',
    GuideTargets: ['a', 'b'],
    UnlockHint: state === 'locked' ? 'Reach level 5' : '',
    State: state,
    Claimable: state === 'done',
    ...overrides,
  };
}

function line(states: QuestState[]): QuestLine {
  const steps = states.map((s, i) => step(i + 1, s));
  return {
    Result: null,
    Reward: { Region: 1, Gold: 1750, MaterialLog: 'birch_log', MaterialOre: 'copper_ore', MaterialQuantity: 25 },
    Done: steps.filter((s) => s.State === 'done' || s.State === 'claimed').length,
    Claimed: steps.filter((s) => s.State === 'claimed').length,
    Total: steps.length,
    Steps: steps,
  };
}

describe('nextQuest', () => {
  it('is null before the list has loaded', () => {
    expect(nextQuest(null)).toBeNull();
    expect(nextQuest(undefined)).toBeNull();
  });

  it('puts a reward that is waiting ahead of everything', () => {
    // Money on the table is the one thing a player should not have to look for.
    expect(nextQuest(line(['available', 'done', 'available']))!.Id).toBe('step2');
  });

  it('otherwise picks the earliest step that is open', () => {
    expect(nextQuest(line(['claimed', 'available', 'available']))!.Id).toBe('step2');
  });

  it('keeps a skipped step in play - it is next again once nothing earlier is open', () => {
    // Step 1 was skipped (still available), step 2 is available too: the
    // earlier one stays the highlight rather than being forgotten.
    expect(nextQuest(line(['available', 'available']))!.Id).toBe('step1');
  });

  it('shows the nearest locked step, with its hint, when nothing is open', () => {
    const next = nextQuest(line(['claimed', 'locked', 'locked']))!;
    expect(next.Id).toBe('step2');
    expect(next.UnlockHint).not.toBe('');
  });

  it('is null when every step is claimed', () => {
    expect(nextQuest(line(['claimed', 'claimed']))).toBeNull();
  });

  it('does not rely on the server sending the steps in order', () => {
    const l = line(['available', 'available']);
    l.Steps.reverse();
    expect(nextQuest(l)!.Id).toBe('step1');
  });
});

describe('the panel summary', () => {
  it('counts done steps and the rewards waiting', () => {
    expect(questSummary(line(['claimed', 'done', 'available', 'locked']))).toBe('2 of 4 done - 1 to claim');
    expect(questSummary(line(['available', 'locked']))).toBe('0 of 2 done');
  });

  it('lists only the done-but-unclaimed steps as claimable', () => {
    expect(claimableSteps(line(['claimed', 'done', 'done', 'locked'])).map((s) => s.Id)).toEqual(['step2', 'step3']);
    expect(claimableSteps(null)).toEqual([]);
  });

  it('retires only when every step has been claimed', () => {
    expect(questLineFinished(line(['claimed', 'claimed']))).toBe(true);
    expect(questLineFinished(line(['claimed', 'done']))).toBe(false);
    expect(questLineFinished(line(['claimed', 'available']))).toBe(false);
    expect(questLineFinished(null)).toBe(false);
  });
});

describe('the highlight', () => {
  it('lights the first target the page has, most specific first', () => {
    expect(pickGuideTarget(['a', 'b'], (t) => t === 'b')).toBe('b');
    expect(pickGuideTarget(['a', 'b'], () => true)).toBe('a');
    expect(pickGuideTarget(['a', 'b'], () => false)).toBeNull();
    expect(pickGuideTarget([], () => true)).toBeNull();
  });

  it('is wanted only while the step is open', () => {
    expect(spotlightStillWanted(line(['available']), 'step1')).toBe(true);
    expect(spotlightStillWanted(line(['done']), 'step1')).toBe(false);
    expect(spotlightStillWanted(line(['claimed']), 'step1')).toBe(false);
    expect(spotlightStillWanted(line(['locked']), 'step1')).toBe(false);
    // Not loaded yet, or a step the list does not know: do not flicker it away.
    expect(spotlightStillWanted(null, 'step1')).toBe(true);
    expect(spotlightStillWanted(line(['available']), 'nope')).toBe(true);
  });
});

describe('describeReward', () => {
  it('words the server reward with the caller supplying item names', () => {
    const text = describeReward(line([]).Reward, (id) => id.replace('_', ' '));
    expect(text).toBe('1,750 gold + 25 birch log + 25 copper ore');
  });
});
