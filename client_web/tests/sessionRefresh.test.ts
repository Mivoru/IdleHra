import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

/*
  STAYING SIGNED IN OVERNIGHT.

  The JWT lasts 24 hours. In a browser tab that is a mild annoyance; on a phone
  it is a password prompt every morning, in a game whose entire proposition is
  that it runs while you are gone. The server issues a refresh token beside the
  JWT and rotates it on every use.

  Rotation is what makes the client half delicate, and it is what these pin. A
  refresh token is spent exactly once, and the SERVER treats a second
  presentation of a spent one as a theft - it revokes every session on the
  account. So a client that hangs on to a token the server has already refused
  does not merely fail to refresh: it signs the player out of every device they
  own the next time it tries. The rule is therefore "a refused token is
  discarded, an unreachable server's token is kept", and the difference between
  those two is the whole point.
*/

const REFRESH_KEY = 'folkidle.refresh';
const TOKEN_KEY = 'folkidle.token';

let native = false;

vi.mock('../src/lib/net/platform', () => ({
  isNativePlatform: () => native,
  platformName: () => (native ? 'android' : 'web'),
}));

vi.mock('../src/lib/net/config', () => ({
  api: (path: string) => `http://server.test${path}`,
}));

/*
  Modul: the suite runs on `environment: 'node'` (see vite.config.ts), so there
  is no Storage of any kind. A stub rather than switching this one file to jsdom:
  auth.ts uses four methods, the module picks its store at CALL time, and a
  whole DOM to hold two strings would be the slowest test in the suite.
*/
function memoryStorage(): Storage {
  const map = new Map<string, string>();
  return {
    get length() {
      return map.size;
    },
    key: (index: number) => Array.from(map.keys())[index] ?? null,
    getItem: (key: string) => map.get(key) ?? null,
    setItem: (key: string, value: string) => void map.set(key, String(value)),
    removeItem: (key: string) => void map.delete(key),
    clear: () => map.clear(),
  } as Storage;
}

type AuthModule = typeof import('../src/lib/net/auth');
let auth: AuthModule;

const fetchMock = vi.fn();

function jsonResponse(status: number, body: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as unknown as Response;
}

beforeEach(async () => {
  native = false;
  vi.stubGlobal('sessionStorage', memoryStorage());
  vi.stubGlobal('localStorage', memoryStorage());
  fetchMock.mockReset();
  vi.stubGlobal('fetch', fetchMock);
  vi.resetModules();
  auth = await import('../src/lib/net/auth');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('where the refresh token lives', () => {
  it('follows the JWT into localStorage on a phone, and sessionStorage in a tab', async () => {
    // Modul: the SAME store as the JWT, deliberately. On the web it lands in
    // sessionStorage and dies with the tab, so a browser's session length does
    // not change at all - only the native build gets a persistent login, which
    // is the case this exists for.
    native = true;
    vi.resetModules();
    auth = await import('../src/lib/net/auth');

    fetchMock.mockResolvedValue(
      jsonResponse(200, { Token: 'jwt-1', ExpiresAtEpoch: 10, RefreshToken: 'refresh-1' }),
    );
    await auth.loginWithDevice();

    expect(localStorage.getItem(REFRESH_KEY)).toBe('refresh-1');
    expect(sessionStorage.getItem(REFRESH_KEY)).toBeNull();
  });

  it('keeps a good token when a login answers without one', async () => {
    // Modul: an empty RefreshToken is a login whose SECOND half failed, and the
    // server deliberately does not fail the login over it. Overwriting a stored
    // token with the empty string would turn "you find out tomorrow" into "you
    // are signed out now", which is worse than not having the feature.
    sessionStorage.setItem(REFRESH_KEY, 'still-good');

    fetchMock.mockResolvedValue(
      jsonResponse(200, { Token: 'jwt-1', ExpiresAtEpoch: 10, RefreshToken: '' }),
    );
    await auth.loginWithDevice();

    expect(auth.storedRefreshToken()).toBe('still-good');
  });
});

describe('spending it', () => {
  it('does nothing at all when there is none', async () => {
    expect(await auth.refreshSession()).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('trades it for a session and stores the successor', async () => {
    sessionStorage.setItem(REFRESH_KEY, 'refresh-1');
    fetchMock.mockResolvedValue(
      jsonResponse(200, { Token: 'jwt-2', ExpiresAtEpoch: 99, RefreshToken: 'refresh-2' }),
    );

    const session = await auth.refreshSession();

    expect(session?.token).toBe('jwt-2');
    expect(sessionStorage.getItem(TOKEN_KEY)).toBe('jwt-2');

    // Rotation: the one just spent must not still be sitting there.
    expect(auth.storedRefreshToken()).toBe('refresh-2');

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('http://server.test/api/v1/auth/refresh');
    expect(JSON.parse(init.body as string)).toEqual({ refreshToken: 'refresh-1' });
  });

  it('THROWS AWAY a token the server refused', async () => {
    // The one that matters. The server revokes the whole account when a spent
    // token comes back, so keeping a refused one would make the next launch
    // sign the player out of every device they own.
    sessionStorage.setItem(REFRESH_KEY, 'refresh-spent');
    fetchMock.mockResolvedValue(jsonResponse(401, null));

    expect(await auth.refreshSession()).toBeNull();
    expect(auth.storedRefreshToken()).toBeNull();
  });

  it('KEEPS a token the server never saw', async () => {
    // The mirror image, and the reason the two cases cannot share a branch. A
    // phone in a tunnel has a perfectly good token; discarding it here would
    // make a lost signal indistinguishable from an expired session.
    sessionStorage.setItem(REFRESH_KEY, 'refresh-1');
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'));

    expect(await auth.refreshSession()).toBeNull();
    expect(auth.storedRefreshToken()).toBe('refresh-1');
  });

  it('never throws out to its caller', async () => {
    sessionStorage.setItem(REFRESH_KEY, 'refresh-1');
    fetchMock.mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => {
        throw new Error('not json');
      },
    } as unknown as Response);

    await expect(auth.refreshSession()).resolves.toBeNull();
  });
});

describe('ending a session', () => {
  it('tells the server, and forgets it locally either way', async () => {
    sessionStorage.setItem(REFRESH_KEY, 'refresh-1');
    fetchMock.mockResolvedValue(jsonResponse(204, null));

    auth.revokeRefreshToken();

    expect(auth.storedRefreshToken()).toBeNull();
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('http://server.test/api/v1/auth/revoke');
    expect(JSON.parse(init.body as string)).toEqual({ refreshToken: 'refresh-1' });
  });

  it('is local first, so a network failure cannot strand the player', async () => {
    sessionStorage.setItem(REFRESH_KEY, 'refresh-1');
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'));

    expect(() => auth.revokeRefreshToken()).not.toThrow();
    expect(auth.storedRefreshToken()).toBeNull();
  });

  it('says nothing to the server when there was nothing to revoke', () => {
    auth.revokeRefreshToken();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('clearToken leaves the refresh half alone', () => {
    // Modul: clearToken runs when a JWT is REJECTED, and the whole point of the
    // refresh half is that it outlives one. Clearing both there would mean a
    // JWT that expired in the player's pocket signs them out for good - the
    // exact defect this feature exists to fix.
    sessionStorage.setItem(TOKEN_KEY, 'jwt-1');
    sessionStorage.setItem(REFRESH_KEY, 'refresh-1');

    auth.clearToken();

    expect(auth.storedToken()).toBeNull();
    expect(auth.storedRefreshToken()).toBe('refresh-1');
  });
});
