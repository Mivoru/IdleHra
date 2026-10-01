import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { get, writable } from 'svelte/store';
import { createInFlight, watchNewResults } from '../src/lib/ui/inFlight';
import { createConfirmArm } from '../src/lib/ui/confirmArm';
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

describe('inFlight: one command per target until the server answers', () => {
  it('refuses a second send for the same key (the double tap)', () => {
    const tracker = createInFlight();
    const send = vi.fn(() => ({ ok: true }));
    expect(tracker.run('market:1', send)).toEqual({ ok: true });
    expect(tracker.run('market:1', send)).toBeNull();
    expect(send).toHaveBeenCalledTimes(1);
    expect(get(tracker).has('market:1')).toBe(true);
  });

  it('does not hold an unrelated key', () => {
    const tracker = createInFlight();
    tracker.run('market:1', () => ({ ok: true }));
    expect(tracker.run('market:2', () => ({ ok: true }))).toEqual({ ok: true });
  });

  it('releases at once when the client refused to send', () => {
    const tracker = createInFlight();
    tracker.run('mail:3', () => ({ ok: false }));
    expect(tracker.has('mail:3')).toBe(false);
  });

  it('releases after the timeout if no result ever arrives', () => {
    const tracker = createInFlight({ timeoutMs: 3000 });
    tracker.run('k', () => ({ ok: true }));
    vi.advanceTimersByTime(2999);
    expect(tracker.has('k')).toBe(true);
    vi.advanceTimersByTime(1);
    expect(tracker.has('k')).toBe(false);
  });

  it('a result releases after the grace, not instantly - the refetch is still in flight', () => {
    const tracker = createInFlight({ timeoutMs: 3000, settleGraceMs: 600 });
    tracker.run('k', () => ({ ok: true }));
    tracker.settle();
    expect(tracker.has('k')).toBe(true);
    vi.advanceTimersByTime(600);
    expect(tracker.has('k')).toBe(false);
  });

  it('a key sent during the grace waits for its own result', () => {
    const tracker = createInFlight({ timeoutMs: 3000, settleGraceMs: 600 });
    tracker.run('a', () => ({ ok: true }));
    tracker.settle();
    tracker.run('b', () => ({ ok: true }));
    vi.advanceTimersByTime(600);
    expect(tracker.has('a')).toBe(false);
    expect(tracker.has('b')).toBe(true);
  });
});

describe('watchNewResults: any unseen result id is an answer', () => {
  it('ignores what was already on screen, fires on new server and local ids, not on expiry', () => {
    const results = writable<{ id: number }[]>([{ id: 4 }]);
    const onNew = vi.fn();
    watchNewResults(results, onNew);
    expect(onNew).not.toHaveBeenCalled();

    results.set([{ id: 4 }, { id: 5 }]);
    expect(onNew).toHaveBeenCalledTimes(1);

    // A toast expiring shrinks the list; that is not an answer.
    results.set([{ id: 5 }]);
    expect(onNew).toHaveBeenCalledTimes(1);

    // pushLocalNotice counts down from -1.
    results.set([{ id: 5 }, { id: -1 }]);
    expect(onNew).toHaveBeenCalledTimes(2);
  });
});

describe('confirmArm: first press arms, second commits', () => {
  it('commits only on the second press', () => {
    const onCommit = vi.fn();
    const arm = createConfirmArm({ onCommit });
    expect(arm.press()).toBe('armed');
    expect(onCommit).not.toHaveBeenCalled();
    expect(arm.press()).toBe('committed');
    expect(onCommit).toHaveBeenCalledTimes(1);
    expect(arm.armed).toBe(false);
  });

  it('disarms by itself, so a much later tap starts over', () => {
    const onCommit = vi.fn();
    const arm = createConfirmArm({ onCommit, timeoutMs: 4000 });
    arm.press();
    vi.advanceTimersByTime(4000);
    expect(arm.armed).toBe(false);
    expect(arm.press()).toBe('armed');
    expect(onCommit).not.toHaveBeenCalled();
  });

  it('disarm (Escape, blur) cancels and reports the change', () => {
    const onChange = vi.fn();
    const arm = createConfirmArm({ onCommit: () => {}, onChange });
    arm.press();
    arm.disarm();
    expect(onChange.mock.calls).toEqual([[true], [false]]);
  });
});

// Modul: the native dialog is what ConfirmButton replaced; one coming back is
// the two-apps look the audit flagged.
describe('no native confirm() in the client', () => {
  it('no screen or component calls window.confirm', () => {
    const src = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');
    const offenders: string[] = [];
    for (const dir of ['routes', join('lib', 'ui')]) {
      for (const file of readdirSync(join(src, dir)).sort()) {
        if (!file.endsWith('.svelte') && !file.endsWith('.ts')) continue;
        const text = readFileSync(join(src, dir, file), 'utf8').replace(/\/\/.*$/gm, '');
        if (/(^|[^\w.])confirm\(/m.test(text)) offenders.push(join(dir, file));
      }
    }
    expect(offenders).toEqual([]);
  });
});
