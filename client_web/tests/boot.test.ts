import { describe, expect, it } from 'vitest';
import { bootStage } from '../src/lib/ui/boot';

// The loading screen covers the app until there is something to show. These
// pin the two ways that goes wrong: a screen that never leaves (the player is
// stuck behind a picture) and one that leaves too early (an empty Hub).
describe('bootStage', () => {
  const base = { restoring: false, signedIn: true, phase: 'connecting' as const, hasState: false };

  it('leaves at once for the login form', () => {
    expect(bootStage({ ...base, signedIn: false, phase: 'idle' })).toBe('done');
  });

  it('waits while a stored session is restored', () => {
    expect(bootStage({ ...base, restoring: true, signedIn: false })).not.toBe('done');
  });

  it('stays through the connection until the first snapshot', () => {
    for (const phase of ['idle', 'connecting', 'authenticating', 'live'] as const) {
      expect(bootStage({ ...base, phase })).not.toBe('done');
    }
    expect(bootStage({ ...base, phase: 'live', hasState: true })).toBe('done');
  });

  it('gets out of the way of a connection problem', () => {
    for (const phase of ['reconnecting', 'failed', 'signedout'] as const) {
      expect(bootStage({ ...base, phase })).toBe('done');
    }
  });

  it('only ever moves the bar forward along a normal launch', () => {
    const launch = [
      bootStage({ ...base, restoring: true, signedIn: false }),
      bootStage({ ...base, phase: 'connecting' }),
      bootStage({ ...base, phase: 'authenticating' }),
      bootStage({ ...base, phase: 'live' }),
    ];
    let last = 35; // main.ts's step, before any of these
    for (const stage of launch) {
      if (stage === 'done') throw new Error('unexpected done');
      expect(stage.percent).toBeGreaterThanOrEqual(last);
      expect(stage.next).toBeGreaterThan(stage.percent);
      last = stage.percent;
    }
  });
});
