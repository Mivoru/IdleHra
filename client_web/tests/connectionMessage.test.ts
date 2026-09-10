import { describe, it, expect } from 'vitest';
import {
  describeConnection,
  shouldShowConnectionPanel,
  CONNECTION_GRACE_MS,
} from '../src/lib/ui/connectionMessage';
import type { ConnectionPhase } from '../src/lib/net/connection';

/*
  WHAT A PLAYER IS TOLD WHEN THE SIGNAL GOES.

  The old presentation printed the phase name and an attempt count -
  "reconnecting (attempt 4)" - which is proportionate to a browser tab that
  either works or does not, and useless to a phone that loses signal in a lift,
  on a train and walking into a shop.

  `describeConnection` was split out as a pure function so the wording could be
  tested rather than eyeballed, and this is the file that makes that true. The
  properties below are the reason the function exists; the exact sentences are
  deliberately NOT asserted, so copy can be improved without a red test.
*/

const EVERY_PHASE: ConnectionPhase[] = [
  'idle',
  'connecting',
  'authenticating',
  'live',
  'reconnecting',
  'failed',
  'signedout',
];

/** The phases that put something on screen. */
const TROUBLED: ConnectionPhase[] = ['connecting', 'authenticating', 'reconnecting', 'failed'];

describe('what every trouble state says', () => {
  it.each(TROUBLED)('%s names the situation and promises nothing is lost', (phase) => {
    const message = describeConnection(phase, 1, true);

    expect(message.title.length).toBeGreaterThan(0);
    expect(message.body.length).toBeGreaterThan(0);

    // Modul: THE ONE FACT THE PLAYER IS ACTUALLY WORRIED ABOUT. "Am I losing
    // time?" - and the answer is no, in every one of these states, because the
    // server owns the simulation. A client that is not connected is a client
    // that is not WATCHING, not a character that has stopped.
    expect(message.body).toMatch(/keeps playing/i);
  });

  it.each(EVERY_PHASE)('%s is written for a person, not dumped as an enum', (phase) => {
    const message = describeConnection(phase, 1, true);

    // The defect this replaced was the header printing `connectionStatus.phase`
    // verbatim. "Reconnecting" is a perfectly good title that happens to spell
    // the phase, so the test is not "never say the word" - it is that a title
    // is written English and a troubled state always explains itself.
    expect(message.title).toMatch(/^[A-Z]/);
    if (message.body === '') {
      expect(TROUBLED).not.toContain(phase);
    }
  });

  it('says nothing at all when there is nothing wrong', () => {
    for (const phase of ['live', 'idle'] as ConnectionPhase[]) {
      expect(describeConnection(phase, 1, true).body).toBe('');
    }
  });
});

describe('whose fault it is', () => {
  it('blames the device when the device is the one that is offline', () => {
    const message = describeConnection('reconnecting', 3, false);

    expect(message.title).toMatch(/offline/i);
    expect(message.tone).toBe('stuck');

    // Modul: no retry button with no interface up. It is an action that cannot
    // work, and offering one makes the failure look like the player's to fix.
    expect(message.showRetry).toBe(false);
  });

  it('blames the server when the device is fine', () => {
    const message = describeConnection('failed', 5, true);

    expect(message.title).not.toMatch(/you are offline/i);
    expect(message.showRetry).toBe(true);
  });

  it('lets being offline override the phase, in every phase', () => {
    for (const phase of TROUBLED) {
      expect(describeConnection(phase, 1, false).title).toMatch(/offline/i);
    }
  });
});

describe('offering a retry', () => {
  it('does not offer one on the first reconnect attempt', () => {
    // The backoff starts at half a second: a button during the first retry is
    // gone before a thumb reaches it, and pressing it would do exactly what was
    // already about to happen.
    expect(describeConnection('reconnecting', 1, true).showRetry).toBe(false);
  });

  it('offers one once retrying has visibly not worked', () => {
    expect(describeConnection('reconnecting', 2, true).showRetry).toBe(true);
  });

  it('gets louder rather than staying cheerful forever', () => {
    expect(describeConnection('reconnecting', 1, true).tone).toBe('working');
    expect(describeConnection('reconnecting', 9, true).tone).toBe('stuck');
  });

  it('counts the attempt for the player only once retrying is real', () => {
    expect(describeConnection('reconnecting', 1, true).title).not.toMatch(/\d/);
    expect(describeConnection('reconnecting', 4, true).title).toMatch(/4/);
  });
});

describe('when the panel appears at all', () => {
  it('stays away for a hiccup shorter than the grace period', () => {
    // Modul: EVERY SESSION STARTS DISCONNECTED. Signing in walks through
    // connecting and authenticating, and on a working connection that is a few
    // hundred milliseconds. A panel that appeared for it would flash a red card
    // at every single launch - the classic loading-spinner mistake, where the
    // indicator is the only thing anyone ever sees of a fast operation.
    expect(shouldShowConnectionPanel('connecting', 0)).toBe(false);
    expect(shouldShowConnectionPanel('connecting', CONNECTION_GRACE_MS - 1)).toBe(false);
  });

  it('appears once the trouble has lasted', () => {
    expect(shouldShowConnectionPanel('connecting', CONNECTION_GRACE_MS)).toBe(true);
    expect(shouldShowConnectionPanel('reconnecting', 30_000)).toBe(true);
    expect(shouldShowConnectionPanel('failed', 30_000)).toBe(true);
  });

  it('never appears over a working session, however long ago it connected', () => {
    for (const phase of ['live', 'idle'] as ConnectionPhase[]) {
      expect(shouldShowConnectionPanel(phase, 10 * 60_000)).toBe(false);
    }
  });

  it('leaves a rejected token to the login form', () => {
    // 'signedout' is the one failure a retry cannot fix. App.svelte turns it
    // into the login screen, and a panel offering "try again" on top of that
    // would be offering an action that cannot work.
    expect(shouldShowConnectionPanel('signedout', 60_000)).toBe(false);
  });
});
