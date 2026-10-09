import type { ConnectionPhase } from '../net/connection';

// Modul: THE LOADING SCREEN'S STEPS, in the order a launch meets them.
//
// The screen itself is plain HTML in index.html (#boot) because it has to
// paint before this bundle exists; this module is only the typed door to the
// `window.__boot` that public/boot.js defines. Every call is a no-op when that object is
// missing - a test page, or a second call after the screen has gone - so no
// caller has to guard it.
//
// `percent` is where the bar jumps when the step starts and `next` is the
// percentage it creeps toward while the step lasts (boot.js never lets it
// reach `next` on its own). The numbers are proportions of a typical launch,
// not measurements of anything; what matters is that they only rise.

type BootApi = {
  step(percent: number, message: string, next: number): void;
  done(): void;
};

function api(): BootApi | undefined {
  return (window as unknown as { __boot?: BootApi }).__boot;
}

export function bootStep(percent: number, message: string, next: number): void {
  api()?.step(percent, message, next);
}

export function bootDone(): void {
  api()?.done();
}

/**
 * What the loading screen should say for the app's state at this moment, or
 * `'done'` when the player needs to see the app itself.
 *
 * Pure so it can be tested without a DOM. `hasState` is the first snapshot:
 * until it arrives the Hub has nothing to draw, which is exactly the blank
 * moment this screen exists to cover.
 */
export function bootStage(input: {
  restoring: boolean;
  signedIn: boolean;
  phase: ConnectionPhase;
  hasState: boolean;
}): { percent: number; message: string; next: number } | 'done' {
  if (input.restoring) return { percent: 45, message: 'Signing you back in…', next: 58 };
  // Nobody signed in: the login form is the screen, nothing more to wait for.
  if (!input.signedIn) return 'done';
  if (input.hasState) return 'done';
  switch (input.phase) {
    case 'idle':
    case 'connecting':
      return { percent: 60, message: 'Connecting to the server…', next: 72 };
    case 'authenticating':
      return { percent: 72, message: 'Checking your credentials…', next: 84 };
    case 'live':
      return { percent: 85, message: 'Gathering your village…', next: 97 };
    // A connection problem is the app's to explain - ConnectionNotice says
    // what is wrong and offers a retry, and it cannot do that from behind a
    // full-screen picture. A rejected token goes back to the login form.
    case 'reconnecting':
    case 'failed':
    case 'signedout':
      return 'done';
  }
}
