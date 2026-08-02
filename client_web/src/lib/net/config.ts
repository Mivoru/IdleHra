// Modul: the ONE place the server address is written down.
//
// The Unity client learned this lesson already: ClientServerConfig exists
// there because the address had been pasted into several scripts and they
// drifted. Same rule here - nothing else in this client may contain a host or
// a port.

const DEFAULT_HTTP_BASE = 'http://localhost:8080';

/** Overridable per environment via a Vite env var, e.g. in .env.production. */
export const HTTP_BASE: string = import.meta.env?.VITE_FOLKIDLE_SERVER ?? DEFAULT_HTTP_BASE;

/** Derived, never configured separately - two settings would be two truths. */
export const WS_URL: string = HTTP_BASE.replace(/^http/, 'ws') + '/';

/**
 * Whether this build can actually reach its server, and why not if it cannot.
 *
 * Two configuration mistakes are invisible until they are not:
 *
 *   - A NATIVE build pointed at `localhost`. On a phone that means the phone,
 *     not the machine that built the app, so nothing responds. It surfaces as
 *     a connection timeout, which reads like a server outage.
 *   - A native build on plain `http`. Capacitor serves the page from an https
 *     or capacitor scheme, so the WebView blocks an insecure WebSocket as
 *     mixed content - and the block is silent, with no error the page can
 *     catch.
 *
 * Returned as a message rather than thrown, so the login screen can say it
 * plainly instead of the app appearing to hang.
 */
export function configurationProblem(native: boolean): string | null {
  if (!native) return null;

  if (/^https?:\/\/(localhost|127\.0\.0\.1)\b/.test(HTTP_BASE)) {
    return `This build points at ${HTTP_BASE}, which on a phone means the phone itself. Rebuild with VITE_FOLKIDLE_SERVER set to a reachable address.`;
  }

  if (HTTP_BASE.startsWith('http://')) {
    return 'This build uses an insecure address. A native build serves its page over https, and the WebView blocks a plain ws:// socket as mixed content. Use https:// so the socket becomes wss://.';
  }

  return null;
}

export const GAMEDATA_BASE = `${HTTP_BASE}/gamedata`;

export const api = (path: string): string => `${HTTP_BASE}${path}`;
