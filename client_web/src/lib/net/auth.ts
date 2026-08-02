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

import { api } from './config';

const TOKEN_KEY = 'folkidle.token';
const DEVICE_KEY = 'folkidle.deviceId';

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
  return sessionStorage.getItem(TOKEN_KEY);
}

export function storeToken(token: string): void {
  sessionStorage.setItem(TOKEN_KEY, token);
}

export function clearToken(): void {
  sessionStorage.removeItem(TOKEN_KEY);
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

export async function isEmailAvailable(email: string): Promise<boolean> {
  const response = await postJson('/api/v1/auth/check-email', { email });
  if (!response.ok) return false;
  const parsed = await response.json();
  return parsed.Available === true;
}

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
