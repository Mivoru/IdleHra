// Modul: the ONE place the server address is written down.
//
// The Unity client learned this lesson already: ClientServerConfig exists
// there because the address had been pasted into several scripts and they
// drifted. Same rule here - nothing else in this client may contain a host or
// a port.

import { isNativePlatform, CAPACITOR_ORIGINS } from './platform';

const DEFAULT_HTTP_BASE = 'http://localhost:8080';

/**
 * The server a page talks to.
 *
 * Modul: A WEB PAGE TALKS TO ITS OWN ORIGIN, 2026-09-13.
 *
 * This used to be VITE_FOLKIDLE_SERVER, full stop - one address compiled into
 * the bundle. That was right while the box had one name. Once folkidle.cz was
 * served beside folkidle.duckdns.org, a page loaded from folkidle.cz still sent
 * its API calls and WebSocket to duckdns.org, and in the owner's browser every
 * one of those sockets failed while a clean headless Chromium on the same
 * machine connected: a browser-side block on a dynamic-DNS domain, which a
 * player cannot be asked to diagnose. Caddy serves the API on every hostname it
 * serves the page on, so a web page never needs a second host.
 *
 * The configured address still wins where the page origin is NOT the server:
 *   - a native build, whose page is served from the phone (https://localhost or
 *     capacitor://localhost) - see CAPACITOR_ORIGINS;
 *   - local development, where Vite serves the page on :5173 and the game
 *     server listens on :8080;
 *   - no page at all (tests, scripts).
 * Plain http is never adopted either: an http page next to an https API would
 * turn the socket into ws://, which a secure context refuses.
 */
export function resolveHttpBase(input: {
  envBase: string | undefined;
  pageOrigin: string | null;
  native: boolean;
}): string {
  const configured = input.envBase ?? DEFAULT_HTTP_BASE;
  const page = input.pageOrigin;

  if (input.native || !page || !page.startsWith('https://')) return configured;
  if ((CAPACITOR_ORIGINS as readonly string[]).includes(page)) return configured;
  if (/^https:\/\/(localhost|127\.0\.0\.1)(:\d+)?$/.test(page)) return configured;

  return page;
}

function currentPageOrigin(): string | null {
  try {
    return typeof window !== 'undefined' && window.location ? window.location.origin : null;
  } catch {
    return null;
  }
}

/** Overridable per environment via a Vite env var, e.g. in .env.production - see resolveHttpBase. */
export const HTTP_BASE: string = resolveHttpBase({
  envBase: import.meta.env?.VITE_FOLKIDLE_SERVER,
  pageOrigin: currentPageOrigin(),
  native: isNativePlatform(),
});

/** Derived, never configured separately - two settings would be two truths. */
export const WS_URL: string = HTTP_BASE.replace(/^http/, 'ws') + '/';

/**
 * Where the artwork is served from. DERIVED from HTTP_BASE, so the rule above
 * still holds: with nothing set there is one address, and it is the server's.
 *
 * The override exists because the art does not have to travel with the API.
 * 214 files and 18 MB of icons served by a single small instance is the worst
 * possible use of it - a CDN does that better and does not go to sleep. So a
 * hosted build points this at the static site, which carries its own copy (see
 * vite.config.ts's sprite-copy plugin), while local development leaves it unset
 * and keeps talking to the dev server, which already has the files linked in
 * through FolkIdle.Server.csproj.
 *
 * Note this is only ever an ORIGIN. The paths underneath it come from
 * sprites.generated.ts either way, so the two copies cannot address differently.
 */
export const SPRITE_BASE: string = import.meta.env?.VITE_FOLKIDLE_SPRITES ?? HTTP_BASE;

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
