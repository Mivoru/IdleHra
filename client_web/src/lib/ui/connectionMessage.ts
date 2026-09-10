import type { ConnectionPhase } from '../net/connection';

// Modul: A PHASE NAME IS NOT AN ANSWER.
//
// The header prints `connectionStatus.phase` verbatim - "reconnecting",
// "failed" - and a banner underneath repeats it with an attempt count. On a
// desktop, where the connection either works or the whole machine is offline,
// that is enough. On a phone it is not: signal drops in a lift, on a train,
// walking into a shop, several times an hour, and each drop leaves a player
// looking at a word.
//
// The three things a player needs, in this order:
//   1. WHOSE FAULT IT IS. "Your phone is offline" and "we cannot reach the
//      server" call for completely different reactions, and the player can
//      tell the difference instantly if we say which one it is.
//   2. WHETHER THE GAME IS STILL RUNNING. It is - the simulation lives on the
//      server and offline catch-up pays for the gap - and this is the single
//      most calming fact available. A player who does not know it assumes a
//      dropped connection is lost progress.
//   3. WHAT THEY CAN DO. Usually nothing, and saying so plainly beats a
//      spinner. Where there IS something (retry now), it gets a button.
//
// Kept as a pure function over the phase rather than inside the component so
// the wording is testable. Every shipped defect in this client's history has
// been a thing that rendered fine and said nothing true.

export interface ConnectionMessage {
  /** Short heading. Sentence case, no trailing punctuation. */
  title: string;
  /** One or two sentences: what is happening, and what the game is doing. */
  body: string;
  /** Whether a manual retry is worth offering. */
  showRetry: boolean;
  /**
   * How loud to be. `working` is the ordinary first connect and a retry in
   * flight; `stuck` is a state that has stopped improving on its own.
   */
  tone: 'working' | 'stuck';
}

/**
 * Modul: the reassurance is the same sentence in every state ON PURPOSE.
 *
 * It is the fact the player is actually worried about - "am I losing time?" -
 * and rewording it per phase would make it read as a different, weaker promise
 * each time. It is also true in all of them: the server owns the simulation,
 * so a client that is not connected is a client that is not WATCHING, not a
 * character that has stopped.
 */
const KEEPS_PLAYING = 'Your character keeps playing on the server, so no progress is lost.';

export function describeConnection(
  phase: ConnectionPhase,
  attempt: number,
  online: boolean,
): ConnectionMessage {
  // Modul: `navigator.onLine` REFINES THE WORDING AND DECIDES NOTHING.
  //
  // It is famously optimistic - true for a phone attached to a wifi router
  // with no route to the internet - so it may never gate a retry or suppress a
  // reconnect. Read the other way round it is trustworthy enough to be useful:
  // when it says FALSE the device really has no interface up, and that is the
  // one case where "check your signal" is the right thing to tell somebody.
  if (!online) {
    return {
      title: 'You are offline',
      body: `This device has no connection. ${KEEPS_PLAYING} The game reconnects on its own the moment signal returns.`,
      showRetry: false,
      tone: 'stuck',
    };
  }

  switch (phase) {
    case 'connecting':
    case 'authenticating':
      return {
        title: 'Connecting',
        body: `Getting you back into the world. ${KEEPS_PLAYING}`,
        showRetry: false,
        tone: 'working',
      };

    case 'reconnecting':
      return {
        title: attempt > 1 ? `Reconnecting (try ${attempt})` : 'Reconnecting',
        body: `The connection dropped and the game is dialling back in. ${KEEPS_PLAYING}`,
        // Modul: offered from the second attempt, not the first. The backoff
        // starts at half a second, so a button during the first retry would be
        // gone before a thumb reached it - and pressing it would do exactly
        // what was already about to happen.
        showRetry: attempt > 1,
        tone: attempt > 3 ? 'stuck' : 'working',
      };

    case 'failed':
      return {
        title: 'Cannot reach FolkIdle',
        body: `The server is not answering. ${KEEPS_PLAYING} Try again, or come back in a few minutes.`,
        showRetry: true,
        tone: 'stuck',
      };

    // Modul: 'signedout' has a screen of its own. App.svelte turns that phase
    // into the login form, which is the only thing that can fix an expired
    // token - a panel offering "retry" over the top of it would be offering an
    // action that cannot work.
    case 'signedout':
    case 'idle':
    case 'live':
    default:
      return {
        title: 'Connected',
        body: '',
        showRetry: false,
        tone: 'working',
      };
  }
}

/**
 * Whether the mobile-shaped panel should be on screen at all.
 *
 * Modul: A GRACE PERIOD, because every session starts disconnected.
 *
 * Signing in walks through `connecting` and `authenticating` before it reaches
 * `live`, and on a working connection that is a few hundred milliseconds. A
 * panel that appeared for it would flash a scary red card at every single
 * launch - the classic loading-spinner mistake, where the indicator is the
 * only thing anyone ever sees of a fast operation.
 *
 * So the panel is a function of DURATION as well as phase: a hiccup shorter
 * than this resolves itself and is never mentioned.
 */
export const CONNECTION_GRACE_MS = 1_500;

export function shouldShowConnectionPanel(
  phase: ConnectionPhase,
  msSinceDisconnected: number,
): boolean {
  if (phase === 'live' || phase === 'idle' || phase === 'signedout') return false;
  // Modul: measured from when the session STOPPED being live, not from the
  // last phase change. `report('reconnecting', ...)` fires again on every
  // backoff attempt, so a per-change timer would be reset by the retries and
  // the panel would never appear during the one state it exists for.
  return msSinceDisconnected >= CONNECTION_GRACE_MS;
}
