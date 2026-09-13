import { describe, it, expect } from 'vitest';
import { resolveHttpBase } from '../src/lib/net/config';

// Modul: WHICH SERVER A PAGE TALKS TO, found live on 2026-09-13.
//
// folkidle.cz was added beside folkidle.duckdns.org, but the bundle had the
// duckdns address compiled in, so a page served from folkidle.cz still opened
// its API and WebSocket on duckdns.org. In the owner's own browser every one of
// those sockets failed ("WebSocket connection to 'wss://folkidle.duckdns.org/'
// failed") while a clean headless Chromium on the same machine connected - the
// shape of a browser-side block on a dynamic-DNS domain. Caddy serves the API on
// every hostname it serves the page on, so a web page never needs a second host.
describe('which server a page talks to', () => {
  const configured = 'https://folkidle.duckdns.org';

  it('a web page served over https talks to its own origin', () => {
    expect(resolveHttpBase({ envBase: configured, pageOrigin: 'https://folkidle.cz', native: false }))
      .toBe('https://folkidle.cz');
    expect(resolveHttpBase({ envBase: configured, pageOrigin: 'https://www.folkidle.cz', native: false }))
      .toBe('https://www.folkidle.cz');
    expect(resolveHttpBase({ envBase: configured, pageOrigin: 'https://folkidle.duckdns.org', native: false }))
      .toBe('https://folkidle.duckdns.org');
  });

  it('a native build keeps the configured server - its page origin is the phone', () => {
    expect(resolveHttpBase({ envBase: configured, pageOrigin: 'https://localhost', native: true }))
      .toBe(configured);
    expect(resolveHttpBase({ envBase: configured, pageOrigin: 'capacitor://localhost', native: true }))
      .toBe(configured);
  });

  it('never mistakes a Capacitor origin for a server, even without the native flag', () => {
    expect(resolveHttpBase({ envBase: configured, pageOrigin: 'https://localhost', native: false }))
      .toBe(configured);
  });

  it('local development keeps the dev server on its own port', () => {
    expect(resolveHttpBase({ envBase: 'http://localhost:8080', pageOrigin: 'http://localhost:5173', native: false }))
      .toBe('http://localhost:8080');
    expect(resolveHttpBase({ envBase: undefined, pageOrigin: 'http://localhost:5173', native: false }))
      .toBe('http://localhost:8080');
  });

  it('with no page at all (tests, scripts) it uses the configured server', () => {
    expect(resolveHttpBase({ envBase: configured, pageOrigin: null, native: false })).toBe(configured);
    expect(resolveHttpBase({ envBase: undefined, pageOrigin: null, native: false })).toBe('http://localhost:8080');
  });
});
