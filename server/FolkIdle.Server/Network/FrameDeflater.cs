using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace FolkIdle.Server.Network
{
    // Modul: TASK 46, THE STATE FRAME IS COMPRESSED BY HAND because the
    // listener cannot do it for us.
    //
    // A JSON snapshot is ~5.4 KB (measured live 2026-10-07: 203,741 bytes over
    // 38 frames) and a session gets about one a second, ~320 KB a player a
    // minute against the 150 KB threshold the task set. The standard answer is
    // permessage-deflate, and System.Net.HttpListener has no way to negotiate
    // it - AcceptWebSocketAsync takes a subprotocol and buffer sizes, nothing
    // else. Moving the socket to Kestrel would be the whole HTTP layer.
    //
    // So a client that asks for it in the handshake ("compress":"deflate-raw")
    // gets every text frame as a BINARY frame of raw deflate, from ONE deflate
    // stream that lives as long as the session. The shared stream is the
    // point: two consecutive snapshots are nearly identical, and deflate's
    // 32 KB window finds the previous one, so a frame costs a few hundred bytes
    // instead of the ~2.3 KB it compresses to alone. Each frame ends with a
    // sync flush (DeflateStream.Flush), so the bytes sent so far always
    // decompress to every message so far; the browser feeds them into one
    // DecompressionStream('deflate-raw') and splits the output on '\n', which
    // System.Text.Json never writes unescaped. A client that does not ask -
    // every APK in the field today - gets the plain JSON text it always has.
    //
    // Not thread-safe, and does not need to be: only the session's writer
    // loop calls it, one frame at a time, and the returned segment is sent
    // before the next call reuses the buffer.
    internal sealed class FrameDeflater : IDisposable
    {
        public const string HandshakeProperty = "compress";
        public const string HandshakeValue = "deflate-raw";

        private static long s_inputBytesTotal;
        private static long s_outputBytesTotal;

        /// <summary>Bytes handed to a deflater since start (folkidle_ws_deflate_input_bytes_total).</summary>
        public static long InputBytesTotal => Interlocked.Read(ref s_inputBytesTotal);

        /// <summary>Bytes a deflater put on the wire since start (folkidle_ws_deflate_output_bytes_total).</summary>
        public static long OutputBytesTotal => Interlocked.Read(ref s_outputBytesTotal);

        private readonly MemoryStream _output = new();
        private readonly DeflateStream _deflate;

        public FrameDeflater()
        {
            _deflate = new DeflateStream(_output, CompressionLevel.Optimal, leaveOpen: true);
        }

        /// <summary>One message in, its compressed bytes out - valid until the next call.</summary>
        public ArraySegment<byte> Compress(ReadOnlySpan<byte> message)
        {
            _output.SetLength(0);
            _deflate.Write(message);
            _deflate.WriteByte((byte)'\n');
            _deflate.Flush();
            int length = (int)_output.Length;
            Interlocked.Add(ref s_inputBytesTotal, message.Length + 1);
            Interlocked.Add(ref s_outputBytesTotal, length);
            return new ArraySegment<byte>(_output.GetBuffer(), 0, length);
        }

        public void Dispose()
        {
            _deflate.Dispose();
            _output.Dispose();
        }
    }
}
