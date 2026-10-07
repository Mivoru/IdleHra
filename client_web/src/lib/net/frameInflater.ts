/**
 * Task 46: the server's compressed frames, inflated.
 *
 * Modul: a session that says `compress: 'deflate-raw'` in its handshake gets
 * every text frame as a BINARY frame of raw deflate, all from ONE deflate
 * stream on the server (FrameDeflater.cs) - which is what makes a state frame
 * cost ~160 bytes instead of ~5.4 KB: deflate finds the previous, nearly
 * identical snapshot in its window. So the frames are not independent: they
 * must go through ONE DecompressionStream, in arrival order, for the life of
 * the socket. Each frame ends with a sync flush and each message with '\n'
 * (never written unescaped by System.Text.Json), so the output is cut back
 * into messages on newlines.
 *
 * A browser without DecompressionStream('deflate-raw') simply does not ask,
 * and keeps the plain JSON text every client used to get.
 */

const FORMAT = 'deflate-raw' as CompressionFormat;

/** The handshake property and value, as FrameDeflater.HandshakeProperty/Value. */
export const COMPRESS_PROPERTY = 'compress';
export const COMPRESS_VALUE = 'deflate-raw';

/** Whether this runtime can inflate the server's frames. */
export function canInflate(): boolean {
  try {
    if (typeof DecompressionStream === 'undefined') return false;
    new DecompressionStream(FORMAT);
    return true;
  } catch {
    return false;
  }
}

export class FrameInflater {
  private readonly writer: WritableStreamDefaultWriter<BufferSource>;
  private readonly decoder = new TextDecoder();
  private pending = '';
  private closed = false;

  constructor(
    private readonly onMessage: (json: string) => void,
    private readonly onError: (error: unknown) => void,
  ) {
    const stream = new DecompressionStream(FORMAT);
    this.writer = stream.writable.getWriter();
    void this.pump(stream.readable.getReader());
  }

  /** One binary frame, in the order it arrived. */
  push(frame: ArrayBuffer | Uint8Array): void {
    if (this.closed) return;
    // Always a fresh ArrayBuffer-backed view: the stream API types reject a
    // view whose buffer might be a SharedArrayBuffer (a Uint8Array argument is
    // copied, an ArrayBuffer is wrapped).
    const bytes = new Uint8Array(frame);
    this.writer.write(bytes).catch((error) => this.fail(error));
  }

  /** The socket is gone; so is its stream. */
  close(): void {
    if (this.closed) return;
    this.closed = true;
    this.writer.abort().catch(() => {});
  }

  private async pump(reader: ReadableStreamDefaultReader<Uint8Array>): Promise<void> {
    try {
      for (;;) {
        const { value, done } = await reader.read();
        if (done || this.closed) return;
        this.pending += this.decoder.decode(value, { stream: true });
        let newline: number;
        while ((newline = this.pending.indexOf('\n')) >= 0) {
          const message = this.pending.slice(0, newline);
          this.pending = this.pending.slice(newline + 1);
          if (message.length > 0 && !this.closed) this.onMessage(message);
        }
      }
    } catch (error) {
      this.fail(error);
    }
  }

  private fail(error: unknown): void {
    if (this.closed) return;
    this.closed = true;
    this.onError(error);
  }
}
