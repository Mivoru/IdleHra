// Modul: A PENDING STATE FOR A WEBSOCKET COMMAND BUTTON.
//
// A REST action awaits its response and can hold a `busy` flag across it. A
// WebSocket command cannot: `connection.send` returns at once, and the answer
// arrives later as a slot in StateUpdatePacket's result ring - which does not
// say WHICH command it answers. So Market Buy, Mailbox Claim, Forge Fuse,
// Crafting and "Send on" all stayed live after the tap, and a double tap sent
// two commands: a success toast, then a red "Target not found." for the same
// press.
//
// The key is the TARGET (a listing id, a mail id, a stack), not the button, so
// a row that re-renders keeps its pending state, and an unrelated row is not
// greyed out by somebody else's purchase.
//
// It clears on the NEXT command result of any kind, because the ring cannot be
// matched to a command - plus a short grace, because a result is also the
// moment every query is invalidated (stores/game.ts processCommandResults) and
// the refetch that removes the claimed mail or the bought listing is still in
// flight. Without the grace the row came back live for a few hundred
// milliseconds holding stale data, which is the double-submit window again.
// A hard timeout covers a command whose result never comes (a reconnect, a
// command the server answers by silence).

import { writable, type Readable } from 'svelte/store';

export interface InFlightTimers {
  set: (fn: () => void, ms: number) => unknown;
  clear: (handle: unknown) => void;
}

export interface InFlightOptions {
  /** Released even if no result ever arrives. */
  timeoutMs?: number;
  /** How long after a command result the keys stay held, for the refetch. */
  settleGraceMs?: number;
  timers?: InFlightTimers;
}

export interface InFlightTracker extends Readable<ReadonlySet<string>> {
  /** True while `key` has a command out. */
  has(key: string): boolean;
  /**
   * Marks `key` pending and runs `send`, returning what it returned. Refuses
   * (returns null, sends nothing) while `key` is already pending - that is the
   * double tap. If `send` reports `{ ok: false }` the key is released at once:
   * nothing went out.
   */
  run<T extends { ok: boolean } | void>(key: string, send: () => T): T | null;
  /** A command result arrived: release everything after the grace. */
  settle(): void;
  /** Releases one key now. */
  release(key: string): void;
  /** Releases everything now (sign-out, tests). */
  reset(): void;
}

const defaultTimers: InFlightTimers = {
  set: (fn, ms) => setTimeout(fn, ms),
  clear: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
};

export function createInFlight(options: InFlightOptions = {}): InFlightTracker {
  const timeoutMs = options.timeoutMs ?? 3000;
  const graceMs = options.settleGraceMs ?? 600;
  const timers = options.timers ?? defaultTimers;

  const keys = new Map<string, unknown>();
  const store = writable<ReadonlySet<string>>(new Set());
  const publish = () => store.set(new Set(keys.keys()));

  function release(key: string) {
    const handle = keys.get(key);
    if (handle === undefined && !keys.has(key)) return;
    timers.clear(handle);
    keys.delete(key);
    publish();
  }

  return {
    subscribe: store.subscribe,
    has: (key) => keys.has(key),
    run(key, send) {
      if (keys.has(key)) return null;
      keys.set(
        key,
        timers.set(() => release(key), timeoutMs),
      );
      publish();
      const outcome = send();
      if (outcome && outcome.ok === false) release(key);
      return outcome;
    },
    settle() {
      // Only the keys out at this moment: a command sent during the grace is
      // waiting for its OWN result, not this one.
      for (const key of [...keys.keys()]) {
        timers.clear(keys.get(key));
        keys.set(
          key,
          timers.set(() => release(key), graceMs),
        );
      }
    },
    release,
    reset() {
      for (const handle of keys.values()) timers.clear(handle);
      keys.clear();
      publish();
    },
  };
}

/**
 * Calls `onNew` whenever a results list gains an entry it has not shown
 * before. The list is a toast queue that also SHRINKS (entries expire), so
 * "the length grew" is not the test - an id never seen before is. Server
 * results count up from 1; the client's own refusals (pushLocalNotice) count
 * down from -1, and both mean "the command you just pressed has its answer".
 */
export function watchNewResults(
  results: Readable<readonly { id: number }[]>,
  onNew: () => void,
): () => void {
  let highest = 0;
  let lowest = 0;
  let primed = false;
  return results.subscribe((entries) => {
    let fresh = false;
    for (const entry of entries) {
      if (entry.id > highest) {
        highest = entry.id;
        fresh = true;
      } else if (entry.id < lowest) {
        lowest = entry.id;
        fresh = true;
      }
    }
    // The first value is whatever was already on screen when this started.
    if (primed && fresh) onNew();
    primed = true;
  });
}
