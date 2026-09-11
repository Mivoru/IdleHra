// Modul: A HANDSHAKE THAT HANGS USED TO HANG FOR EVER.
//
// Reported from the first real phone this client has ever run on: signed in
// successfully, then "connecting" for five minutes.
//
// Sign-in is REST and it worked, so the server, TLS and CORS were all fine.
// What had no way out was `open()`: it waits for `onopen` or `onclose` and
// nothing else. A REFUSED connection closes, and `scheduleReconnect` handles
// that properly. But a network that BLACKHOLES the packets rather than
// refusing them - a captive portal, a carrier NAT dropping the SYN, a handover
// between cells - leaves the socket in CONNECTING, where neither handler ever
// fires. No retry, no timeout, no message: the exact "what does the player
// see? nothing" shape this codebase keeps rediscovering.
//
// And the one mechanism written to rescue a phone made it worse.
// `resumeFromBackground` opened with `if (readyState === CONNECTING) return`,
// so bringing the app back to the foreground deliberately stepped around the
// stuck case. Restarting the app was the only cure.
//
// These tests drive GameConnection against a fake WebSocket that never
// resolves, which is the only way to express "and then nothing happened".
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { GameConnection } from '../src/lib/net/connection';

const CONNECTING = 0;
const OPEN = 1;
const CLOSED = 3;

/** Every socket the code under test opened, in order. */
let opened: FakeSocket[] = [];

class FakeSocket {
  static readonly CONNECTING = CONNECTING;
  static readonly OPEN = OPEN;
  static readonly CLOSED = CLOSED;

  readyState = CONNECTING;
  onopen: (() => void) | null = null;
  onmessage: ((e: unknown) => void) | null = null;
  onclose: ((e: { code: number; reason: string }) => void) | null = null;
  onerror: (() => void) | null = null;
  sent: string[] = [];
  closeCalls = 0;

  constructor(readonly url: string) {
    opened.push(this);
  }

  send(data: string): void {
    this.sent.push(data);
  }

  /** Mirrors the browser: closing a CONNECTING socket still fires onclose. */
  close(): void {
    this.closeCalls += 1;
    if (this.readyState === CLOSED) return;
    this.readyState = CLOSED;
    this.onclose?.({ code: 1006, reason: '' });
  }
}

beforeEach(() => {
  opened = [];
  vi.useFakeTimers();
  (globalThis as { WebSocket?: unknown }).WebSocket = FakeSocket;
});

afterEach(() => {
  vi.useRealTimers();
  delete (globalThis as { WebSocket?: unknown }).WebSocket;
});

function connect() {
  const phases: string[] = [];
  const conn = new GameConnection();
  conn.connect('a.jwt.token', { onStatus: (s) => phases.push(s.phase) });
  return { conn, phases };
}

describe('a WebSocket that never finishes connecting', () => {
  it('does not stay in "connecting" for ever', () => {
    const { phases } = connect();

    expect(opened).toHaveLength(1);
    expect(phases).toEqual(['connecting']);

    // Nothing happens on its own. This is the bug, reproduced.
    vi.advanceTimersByTime(5_000);
    expect(phases).toEqual(['connecting']);

    // Past the watchdog, the client gives up on this socket and says so.
    vi.advanceTimersByTime(10_000);
    expect(opened[0].closeCalls).toBeGreaterThan(0);
    expect(phases).toContain('reconnecting');
  });

  it('actually retries, rather than only reporting that it will', () => {
    connect();
    vi.advanceTimersByTime(15_000);

    // The backoff is jittered, so run well past its first window. A second
    // socket is the whole point: reporting 'reconnecting' and then opening
    // nothing would satisfy the test above and strand the player just as hard.
    vi.advanceTimersByTime(5_000);
    expect(opened.length).toBeGreaterThanOrEqual(2);
  });

  it('leaves a socket alone once it opens', () => {
    const { phases } = connect();

    opened[0].readyState = OPEN;
    opened[0].onopen?.();
    expect(phases).toContain('authenticating');

    // The watchdog must be disarmed by a successful handshake, or it would
    // close a perfectly good socket twelve seconds into the session.
    vi.advanceTimersByTime(60_000);
    expect(opened[0].closeCalls).toBe(0);
    expect(opened).toHaveLength(1);
  });

  it('lets the foreground rescue a socket whose watchdog is gone', () => {
    const { conn } = connect();

    // A socket stuck in CONNECTING with no watchdog armed is the state the old
    // `return` created. Coming back to the foreground must now replace it.
    const stuck = opened[0];
    conn.suspendForBackground();
    expect(stuck.closeCalls).toBeGreaterThan(0);

    conn.resumeFromBackground();
    expect(opened.length).toBeGreaterThanOrEqual(2);
  });

  it('does not fire a reconnect into an app nobody is looking at', () => {
    const { conn } = connect();
    conn.suspendForBackground();
    const afterSuspend = opened.length;

    // The watchdog was armed when the app went away. If suspending did not
    // disarm it, it would wake the socket back up in the background - which is
    // the opposite of what suspending is for.
    vi.advanceTimersByTime(60_000);
    expect(opened).toHaveLength(afterSuspend);
  });
});
