import { describe, expect, it } from 'vitest';
import type { DeedChapterEntry, DeedEntry } from '../src/lib/net/rest';
import { closestDeed } from '../src/lib/ui/homeGoal';

function deed(id: string, current: number, target: number, done = false): DeedEntry {
  return { Id: id, Title: id, Body: '', Screen: 'combat', Target: target, Current: current, Done: done };
}

function chapter(index: number, deeds: DeedEntry[], open = true, complete = false): DeedChapterEntry {
  return { Index: index, Title: `c${index}`, Reward: '', IsOpen: open, OpensWhen: open ? '' : 'Opens later.', IsComplete: complete, HasSeal: complete, Deeds: deeds };
}

describe('the home screen goal', () => {
  it('picks the unfinished deed with the largest share done', () => {
    const got = closestDeed([chapter(1, [deed('a', 1, 10), deed('b', 90, 100), deed('c', 3, 4)])]);
    expect(got?.Id).toBe('b');
  });

  it('never picks a finished deed, however full its bar', () => {
    const got = closestDeed([chapter(1, [deed('done', 10, 10, true), deed('open', 1, 10)])]);
    expect(got?.Id).toBe('open');
  });

  it('ignores closed and completed chapters', () => {
    const got = closestDeed([
      chapter(1, [deed('sealed', 9, 10)], true, true),
      chapter(2, [deed('here', 1, 10)]),
      chapter(3, [deed('locked', 9, 10)], false),
    ]);
    expect(got?.Id).toBe('here');
  });

  it('breaks a tie in reading order', () => {
    const got = closestDeed([chapter(1, [deed('first', 5, 10)]), chapter(2, [deed('second', 1, 2)])]);
    expect(got?.Id).toBe('first');
  });

  it('skips a deed with no target and returns null when nothing is left', () => {
    expect(closestDeed([chapter(1, [deed('zero', 0, 0)])])).toBeNull();
    expect(closestDeed([])).toBeNull();
  });
});
