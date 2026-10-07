import { describe, expect, it } from 'vitest';
import { constants, createDeflateRaw } from 'node:zlib';
import { canInflate, FrameInflater } from '../src/lib/net/frameInflater';

/**
 * Task 46: the server deflates every text frame through ONE raw-deflate
 * stream, sync-flushing after each message and ending each with '\n'
 * (FrameDeflater.cs). This is that server, in node, against the inflater.
 */
function serverStream() {
  const deflate = createDeflateRaw();
  const chunks: Buffer[] = [];
  deflate.on('data', (c: Buffer) => chunks.push(c));
  return {
    /** One message in, the bytes of its frame out - as FrameDeflater.Compress. */
    frame(message: string): Promise<Uint8Array> {
      return new Promise((resolve) => {
        chunks.length = 0;
        deflate.write(message + '\n');
        deflate.flush(constants.Z_SYNC_FLUSH, () => resolve(new Uint8Array(Buffer.concat(chunks))));
      });
    },
  };
}

function until(predicate: () => boolean): Promise<void> {
  return new Promise((resolve, reject) => {
    const started = Date.now();
    const poll = () => {
      if (predicate()) return resolve();
      if (Date.now() - started > 2000) return reject(new Error('timed out'));
      setTimeout(poll, 5);
    };
    poll();
  });
}

describe('FrameInflater', () => {
  it('this runtime can inflate', () => {
    expect(canInflate()).toBe(true);
  });

  it('hands back each message as its frame arrives, in order, from one stream', async () => {
    const server = serverStream();
    const got: string[] = [];
    const errors: unknown[] = [];
    const inflater = new FrameInflater((m) => got.push(m), (e) => errors.push(e));

    const messages = ['{"type":"StateUpdate","n":1}', '{"type":"ResponseChatMessage","t":"hi"}', '{"type":"StateUpdate","n":2}'];
    for (let i = 0; i < messages.length; i++) {
      inflater.push(await server.frame(messages[i]));
      // Each message is available before the next frame is sent - a frame
      // must not wait for the stream to end or for more input.
      await until(() => got.length === i + 1);
    }

    expect(got).toEqual(messages);
    expect(errors).toEqual([]);
    inflater.close();
  });

  it('a corrupt frame reports an error instead of a garbled packet', async () => {
    const got: string[] = [];
    const errors: unknown[] = [];
    const inflater = new FrameInflater((m) => got.push(m), (e) => errors.push(e));
    inflater.push(new Uint8Array([0xff, 0xff, 0xff, 0xff, 0x00, 0x13]));
    await until(() => errors.length > 0);
    expect(got).toEqual([]);
  });
});
