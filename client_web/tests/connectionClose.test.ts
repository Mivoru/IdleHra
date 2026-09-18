import { describe, expect, it } from 'vitest';
import { interpretClose } from '../src/lib/net/connection';

// Reported live: "after ctrl shift r I get Connection lost - reconnecting
// (attempt 5). Server terminated the session (Invalid or expired token). This
// is almost always a stale LogicEpochCounter or an unanswered anti-cheat
// challenge."
//
// It was neither. The JWT lives 24 hours (AuthenticationEngine
// .TokenLifetimeSeconds) and there is no refresh token, so the message named
// two innocent subsystems while the reconnect loop retried a token that could
// never work again - hiding the login form behind a spinner.
describe('interpretClose', () => {
  it('treats a rejected token as a sign-out, not a retry', () => {
    const outcome = interpretClose(1008, 'Invalid or expired token');

    expect(outcome.phase).toBe('signedout');
    expect(outcome.reconnect).toBe(false);
    expect(outcome.detail).toMatch(/sign in again/i);
    // The two subsystems it used to accuse must not appear.
    expect(outcome.detail).not.toMatch(/LogicEpoch|anti-cheat/i);
  });

  it('still blames the wire obligations for other 1008 closes', () => {
    // These really are the usual cause of a 1008 that is not about a token,
    // and that hint is worth keeping - it is why the message exists.
    const outcome = interpretClose(1008, 'Stale epoch');

    expect(outcome.phase).toBe('reconnecting');
    expect(outcome.reconnect).toBe(true);
    expect(outcome.detail).toMatch(/LogicEpochCounter/);
  });

  it('reconnects on an ordinary drop', () => {
    const outcome = interpretClose(1006, '');

    expect(outcome.reconnect).toBe(true);
    expect(outcome.detail).toContain('1006');
  });

  it('reads the reason case-insensitively', () => {
    // The server's wording is the only signal here, so this must not hinge on
    // its exact casing.
    expect(interpretClose(1008, 'invalid or expired TOKEN').reconnect).toBe(false);
  });

  // Modul: session-security final-review Finding 1 - ForceDisconnect
  // (blacklist, anti-cheat, epoch violations, cross-pod eviction, and now
  // session revocation) and the WS handshake's nonce-rejection path both
  // used to close with a reason that did NOT contain "token"
  // ("Violent termination" / "Session revoked"), so a revoked session read
  // as a transient drop and this client reconnected with the same dead
  // token forever instead of ever showing the login form. Pins the fixed
  // wording so a future edit cannot silently drop the word again.
  it('treats a forced disconnect as a sign-out once its reason names the token', () => {
    const forceDisconnect = interpretClose(1008, 'Violent termination - token no longer valid');
    expect(forceDisconnect.phase).toBe('signedout');
    expect(forceDisconnect.reconnect).toBe(false);

    const nonceRejected = interpretClose(1008, 'Session revoked - token no longer valid');
    expect(nonceRejected.phase).toBe('signedout');
    expect(nonceRejected.reconnect).toBe(false);
  });
});
