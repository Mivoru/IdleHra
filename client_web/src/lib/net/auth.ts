// Modul: authentication and token persistence.
//
// Token storage decision (port plan 4d listed this as "decide before Phase 1
// ships"): sessionStorage, not localStorage, and not an httpOnly cookie.
//
// - An httpOnly cookie is the safest against XSS, but the WebSocket handshake
//   carries the JWT INSIDE the AuthHandshake packet rather than in a header,
//   so the client must be able to read the token. A cookie it cannot read
//   would need a second token-issuing round trip purely to feed the socket.
// - Between localStorage and sessionStorage, both are XSS-readable, so the
//   difference is blast radius over time: sessionStorage dies with the tab.
//   That is the weaker convenience but the better default, and "remembered
//   device" is a server-side concept here anyway (rememberedDeviceId), not a
//   reason to keep a bearer token on disk forever.
//
// This is a deliberate, revisitable choice, not an accident - if a persistent
// login is wanted later, the right move is a refresh token with a short-lived
// JWT, not upgrading this to localStorage.
//
// THE NATIVE BUILD REVISITS IT, because the reasoning above does not survive
// the move to a phone. "Dies with the tab" is a sensible lifetime for a
// browser session the player chose to close; on Android and iOS the OS
// suspends and kills apps on its own schedule, so the same rule logs the
// player out at moments they did not cause and cannot predict. A native shell
// also has no other origin sharing its storage, which is most of what made
// sessionStorage the safer of the two in the first place.
//
// So: sessionStorage on the web, localStorage under Capacitor. Same key, one
// branch, stated here rather than discovered later.

import { api } from './config';
import { isNativePlatform } from './platform';

const TOKEN_KEY = 'folkidle.token';
const DEVICE_KEY = 'folkidle.deviceId';

/**
 * Where the bearer token lives.
 *
 * Deliberately synchronous, and therefore localStorage rather than Capacitor
 * Preferences: `storedToken()` is called from `authedGet` on every request and
 * from the app's own startup path, and making those async would ripple through
 * the whole client for no gain. Preferences' only advantage here is surviving
 * a WebView data clear, which also destroys the device id and forces a
 * re-login anyway.
 */
function tokenStore(): Storage {
  return isNativePlatform() ? localStorage : sessionStorage;
}

export interface AuthSession {
  token: string;
  expiresAtEpoch: number;
}

export class AuthError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
    this.name = 'AuthError';
  }
}

export function storedToken(): string | null {
  return tokenStore().getItem(TOKEN_KEY);
}

export function storeToken(token: string): void {
  tokenStore().setItem(TOKEN_KEY, token);
}

export function clearToken(): void {
  // Cleared from BOTH, not just the active one. A build that switches
  // platforms - or a developer testing the native path in a browser - would
  // otherwise leave a token behind in the store that is no longer being read,
  // and signing out would not actually sign out.
  sessionStorage.removeItem(TOKEN_KEY);
  localStorage.removeItem(TOKEN_KEY);
}

/**
 * A stable per-browser id. localStorage (not sessionStorage) on purpose: this
 * is an identifier, not a credential, and the whole point is that it survives
 * so an anonymous account is not re-provisioned on every visit.
 */
export function deviceId(): string {
  let id = localStorage.getItem(DEVICE_KEY);
  if (!id) {
    id = crypto.randomUUID();
    localStorage.setItem(DEVICE_KEY, id);
  }
  return id;
}

async function postJson(path: string, body: unknown): Promise<Response> {
  return fetch(api(path), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

async function readSession(response: Response, context: string): Promise<AuthSession> {
  if (!response.ok) {
    // The server answers register failures with a { Reason } body; login
    // failures carry nothing useful, so the status has to speak.
    let reason = '';
    try {
      const parsed = await response.json();
      reason = parsed?.Reason ?? '';
    } catch {
      /* no body, or not JSON - the status is the whole message */
    }
    throw new AuthError(reason || `${context} failed (HTTP ${response.status})`, response.status);
  }

  const parsed = await response.json();
  const token: string = parsed.Token ?? '';
  if (!token) {
    throw new AuthError(`${context} succeeded but returned no token`, response.status);
  }

  storeToken(token);
  return { token, expiresAtEpoch: parsed.ExpiresAtEpoch ?? 0 };
}

/** Logs in, or auto-provisions a fresh anonymous account for this browser. */
export async function loginWithDevice(): Promise<AuthSession> {
  return readSession(await postJson('/api/v1/auth/login', { deviceId: deviceId() }), 'Login');
}

export async function loginWithEmail(email: string, password: string): Promise<AuthSession> {
  return readSession(await postJson('/api/v1/auth/login', { email, password }), 'Login');
}

export async function register(
  email: string,
  password: string,
  username: string,
): Promise<AuthSession> {
  const response = await postJson('/api/v1/auth/register', {
    email,
    password,
    username,
    deviceId: deviceId(),
  });
  return readSession(response, 'Registration');
}

/**
 * Asks for a reset link.
 *
 * ALWAYS RESOLVES, and the caller must show the same message either way -
 * unknown address, known address and provider outage are deliberately
 * indistinguishable, because any difference rebuilds the account enumeration
 * oracle that /api/v1/auth/check-email was deleted for. The server answers 200
 * regardless for the same reason.
 */
export async function requestPasswordReset(email: string): Promise<void> {
  await postJson('/api/v1/auth/request-password-reset', { email }).catch(() => undefined);
}

/**
 * Spends a reset link and sets the new password.
 *
 * These outcomes ARE distinguished, unlike the request above: the caller is
 * already holding the token, so naming the reason leaks nothing and refusing
 * silently would strand them in front of a form that will not accept them.
 */
export async function completePasswordReset(token: string, password: string): Promise<void> {
  const response = await postJson('/api/v1/auth/reset-password', { token, password });
  if (response.ok) return;

  if (response.status === 410) {
    throw new AuthError('That link has expired or has already been used. Ask for a new one.', 410);
  }
  if (response.status === 422) {
    throw new AuthError('That password is too short - eight characters or more.', 422);
  }
  throw new AuthError('That reset link is not valid. Ask for a new one.', response.status);
}

/**
 * The reset token from the URL, or null.
 *
 * A HASH FRAGMENT, never a query string: everything after the # is never sent
 * to a server, so the token stays out of this box's access log, out of any
 * proxy's, and out of the Referer header on the next page the player opens.
 *
 * Cleared from the address bar as soon as it is read, so a shared screenshot or
 * a browser history entry does not carry a live credential.
 */
export function takeResetTokenFromUrl(): string | null {
  const hash = window.location.hash ?? '';
  const match = hash.match(/^#reset=(.+)$/);
  if (!match) return null;

  history.replaceState(null, '', window.location.pathname + window.location.search);
  return decodeURIComponent(match[1]);
}

// Modul: isEmailAvailable IS GONE, with the endpoint behind it. Asking the
// server whether an address has an account here is an enumeration oracle -
// feed it a breach dump, get back the subset who play this game - and nothing
// in this client ever called it. Registration still refuses a duplicate.

/** Authenticated GET against the REST surface. */
export async function authedGet<T>(path: string): Promise<T> {
  const token = storedToken();
  if (!token) throw new AuthError('not signed in', 401);

  const response = await fetch(api(path), {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    throw new AuthError(`GET ${path} failed (HTTP ${response.status})`, response.status);
  }
  return (await response.json()) as T;
}

/**
 * Authenticated POST.
 *
 * Returns `null` for a 200 with an empty body rather than throwing, because
 * several endpoints here answer with a bare status and no JSON at all - the
 * support-ticket endpoint among them. Parsing unconditionally would turn a
 * success into a SyntaxError and report the opposite of what happened.
 */
export async function authedPost<T>(path: string, body: unknown): Promise<T | null> {
  const token = storedToken();
  if (!token) throw new AuthError('not signed in', 401);

  const response = await fetch(api(path), {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw new AuthError(`POST ${path} failed (HTTP ${response.status})`, response.status);
  }

  const text = await response.text();
  if (!text) return null;
  return JSON.parse(text) as T;
}
