import './helpers/nodeLocalStorage';
import { describe, it, expect } from 'vitest';
import { accountIdOf } from '../src/lib/net/auth';
import { continuesSession } from '../src/lib/stores/game';

// A JWT's shape with an unverified signature - accountIdOf only decodes.
function jwt(payload: object): string {
  const b64url = (o: object) =>
    Buffer.from(JSON.stringify(o)).toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${b64url({ alg: 'HS256', typ: 'JWT' })}.${b64url(payload)}.sig`;
}

describe('accountIdOf', () => {
  it('reads the aid claim', () => {
    expect(accountIdOf(jwt({ aid: 42, nonce: 'x' }))).toBe('42');
    expect(accountIdOf(jwt({ aid: 'a-guid' }))).toBe('a-guid');
  });

  it('is null for a token it cannot read', () => {
    expect(accountIdOf('not-a-jwt')).toBeNull();
    expect(accountIdOf('a.%%%.c')).toBeNull();
    expect(accountIdOf(jwt({ nonce: 'x' }))).toBeNull();
  });

  it('handles base64url payloads that need padding and url-safe characters', () => {
    // '?>' encodes to '/z4' in base64, i.e. '_z4' in base64url.
    expect(accountIdOf(jwt({ aid: 7, pad: '?>?>' }))).toBe('7');
  });
});

describe('continuesSession', () => {
  // The reported case: a refreshed token for the same player restarts the
  // session a few seconds after login, and must not wipe the offline drops.
  it('is true for a refreshed token of the same account', () => {
    expect(continuesSession('42', '42')).toBe(true);
  });

  it('is false for another account, a first session, or an unreadable token', () => {
    expect(continuesSession('42', '43')).toBe(false);
    expect(continuesSession(null, '42')).toBe(false);
    expect(continuesSession(null, null)).toBe(false);
    expect(continuesSession('42', null)).toBe(false);
  });
});
