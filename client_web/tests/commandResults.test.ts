import { describe, it, expect } from 'vitest';
import {
  CommandResultFeed,
  readResultSlots,
  COMMAND_RESULT_MESSAGES,
} from '../src/lib/stores/commandResults';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * The body of a C# enum declared in the packet definitions, so a test can
 * compare a client mirror against the server's own source. Same approach as
 * serverMirrors.test.ts and for the same reason: a regex over a one-line-per-
 * member enum needs no build step, and a test that needs one gets skipped.
 */
function readServerEnum(name: string): string {
  const here = dirname(fileURLToPath(import.meta.url));
  const source = readFileSync(
    join(here, '..', '..', 'server', 'FolkIdle.Server', 'Network', 'StateUpdatePacket.cs'),
    'utf8',
  );

  const start = source.indexOf(`enum ${name}`);
  if (start < 0) throw new Error(`enum ${name} not found in StateUpdatePacket.cs`);

  const open = source.indexOf('{', start);
  const close = source.indexOf('}', open);
  if (open < 0 || close < 0) throw new Error(`enum ${name} has no body`);

  return source.slice(open + 1, close);
}

// Modul: the result ring buffer turns a silently-rejected command into an
// explanation. Getting the edge detection wrong reintroduces the exact silence
// it exists to end - or, in the other direction, pops toasts for commands the
// player issued minutes ago.

function packet(slots: [number, number][]): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  slots.forEach(([code, tick], i) => {
    out[`CommandResult${i}_Code`] = code;
    out[`CommandResult${i}_Tick`] = tick;
  });
  return out;
}

describe('readResultSlots', () => {
  it('reads the four flattened byte+uint pairs', () => {
    const slots = readResultSlots(packet([[5, 10], [0, 0], [0, 0], [0, 0]]));
    expect(slots).toHaveLength(4);
    expect(slots[0]).toEqual({ code: 5, tick: 10 });
  });
});

describe('CommandResultFeed', () => {
  it('emits nothing on the first packet of a session', () => {
    // The ring buffer persists across a reconnect, so replaying it on connect
    // would pop toasts for commands issued long before - and again on every
    // subsequent reconnect.
    const feed = new CommandResultFeed();
    expect(feed.accept(packet([[5, 42], [3, 41], [0, 0], [0, 0]]), 0)).toHaveLength(0);
  });

  it('emits a newly populated slot', () => {
    const feed = new CommandResultFeed();
    feed.accept(packet([[0, 0], [0, 0], [0, 0], [0, 0]]), 0);

    const fresh = feed.accept(packet([[5, 1], [0, 0], [0, 0], [0, 0]]), 100);
    expect(fresh).toHaveLength(1);
    expect(fresh[0].code).toBe(5);
    expect(fresh[0].message).toBe(COMMAND_RESULT_MESSAGES[5]);
  });

  it('never emits the same result twice, however often it is rebroadcast', () => {
    // The slot keeps its value for the rest of the session, and StateUpdate
    // repeats it on every packet - so a naive "code != 0" check would toast
    // the same rejection forever.
    const feed = new CommandResultFeed();
    feed.accept(packet([[0, 0], [0, 0], [0, 0], [0, 0]]), 0);
    expect(feed.accept(packet([[5, 1], [0, 0], [0, 0], [0, 0]]), 10)).toHaveLength(1);
    for (let i = 0; i < 20; i++) {
      expect(feed.accept(packet([[5, 1], [0, 0], [0, 0], [0, 0]]), 20 + i)).toHaveLength(0);
    }
  });

  it('emits several rejections that landed between two broadcasts, oldest first', () => {
    // THE reason the server uses a four-slot ring rather than a scalar: a
    // client that missed one broadcast while two commands were rejected back
    // to back must still see both.
    const feed = new CommandResultFeed();
    feed.accept(packet([[0, 0], [0, 0], [0, 0], [0, 0]]), 0);

    const fresh = feed.accept(packet([[5, 2], [3, 1], [11, 3], [0, 0]]), 10);
    expect(fresh.map((f) => f.tick)).toEqual([1, 2, 3]);
    expect(fresh.map((f) => f.code)).toEqual([3, 5, 11]);
  });

  it('tracks the watermark across slots, not per slot', () => {
    // The ring wraps, so a newer result can land in a lower slot index. Only
    // the tick ordering is meaningful.
    const feed = new CommandResultFeed();
    feed.accept(packet([[0, 0], [0, 0], [0, 0], [0, 0]]), 0);
    feed.accept(packet([[5, 4], [0, 0], [0, 0], [0, 0]]), 10);

    // Slot 3 now carries an OLDER tick than one already shown.
    expect(feed.accept(packet([[5, 4], [0, 0], [0, 0], [8, 2]]), 20)).toHaveLength(0);
    // ...and a newer one is still picked up.
    expect(feed.accept(packet([[5, 4], [0, 0], [0, 0], [8, 9]]), 30)).toHaveLength(1);
  });

  it('names every code the server can send', () => {
    // Modul: THIS TEST SAID "0-15" AND THE ENUM HAD REACHED 22.
    //
    // It was a hand-written upper bound, so it went stale the first time a code
    // was added and then sat there passing - seven codes were added after it
    // and it checked none of them. Codes 16 and 17 did in fact ship with
    // nothing to render them, and the player saw a bare number; this test was
    // supposed to be what stopped that.
    //
    // It reads the enum now. A ratchet nobody watches fails open, and the only
    // bound that cannot go stale is the server's own source.
    const enumBlock = readServerEnum('CommandResultCode');
    const codes = [...enumBlock.matchAll(/^\s*([A-Za-z]\w*)\s*=\s*(\d+)\s*,?\s*$/gm)];

    expect(codes.length).toBeGreaterThan(20);

    for (const [, name, value] of codes) {
      expect(
        COMMAND_RESULT_MESSAGES[Number(value)],
        `CommandResultCode.${name} = ${value} has no message, so it renders as a bare number`,
      ).toBeTruthy();
    }
  });

  it('falls back to a readable string for a code it does not know', () => {
    const feed = new CommandResultFeed();
    feed.accept(packet([[0, 0], [0, 0], [0, 0], [0, 0]]), 0);
    const fresh = feed.accept(packet([[99, 1], [0, 0], [0, 0], [0, 0]]), 10);
    expect(fresh[0].message).toContain('99');
  });

  it('reprimes after reset, so a reconnect does not replay the buffer', () => {
    const feed = new CommandResultFeed();
    feed.accept(packet([[0, 0], [0, 0], [0, 0], [0, 0]]), 0);
    feed.accept(packet([[5, 1], [0, 0], [0, 0], [0, 0]]), 10);

    feed.reset();
    expect(feed.accept(packet([[5, 1], [0, 0], [0, 0], [0, 0]]), 20)).toHaveLength(0);
  });
});
