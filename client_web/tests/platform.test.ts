import { describe, it, expect } from 'vitest';
import { configurationProblem } from '../src/lib/net/config';
import { CAPACITOR_ORIGINS } from '../src/lib/net/platform';

// Modul: the two ways a native build silently cannot reach its server.
//
// Both fail as a connection timeout, which reads like the server being down
// rather than like a build that was pointed at the wrong place - so the
// detection is the only thing standing between a misconfigured APK and an hour
// spent debugging the wrong end. That makes it worth testing even though it is
// four lines of regex.
//
// The default HTTP_BASE is http://localhost:8080, which is exactly the
// mistake, so these run against the real value rather than a fixture.

describe('native configuration check', () => {
  it('says nothing at all for a browser build', () => {
    // localhost is CORRECT on the web - it is the developer's own machine.
    expect(configurationProblem(false)).toBeNull();
  });

  it('catches localhost on a native build, which means the phone itself', () => {
    const problem = configurationProblem(true);
    expect(problem).not.toBeNull();
    expect(problem).toContain('phone itself');
  });
});

describe('capacitor origins', () => {
  it('lists both schemes, because the two platforms differ', () => {
    // Android serves from https://localhost (androidScheme in
    // capacitor.config.json), iOS from capacitor://localhost. The server's
    // CORS allow-list is exact-match, so shipping only one of these breaks
    // the other platform completely.
    expect(CAPACITOR_ORIGINS).toContain('https://localhost');
    expect(CAPACITOR_ORIGINS).toContain('capacitor://localhost');
  });

  it('carries no trailing slash - the allow-list compares origins exactly', () => {
    for (const origin of CAPACITOR_ORIGINS) {
      expect(origin.endsWith('/')).toBe(false);
    }
  });
});
