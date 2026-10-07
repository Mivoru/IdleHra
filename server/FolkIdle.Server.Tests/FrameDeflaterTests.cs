using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 46: a session that asked for compression sends every text frame as
    /// raw deflate from one stream; the client inflates the concatenation and
    /// splits on newlines. These tests are that client.
    /// </summary>
    public class FrameDeflaterTests
    {
        private readonly ITestOutputHelper _output;

        public FrameDeflaterTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>What the browser's DecompressionStream('deflate-raw') does with the frames so far.</summary>
        private static List<string> Inflate(IEnumerable<byte[]> frames)
        {
            var all = new MemoryStream();
            foreach (byte[] f in frames) all.Write(f);
            all.Position = 0;
            using var inflate = new DeflateStream(all, CompressionMode.Decompress);
            var text = new MemoryStream();
            // A sync-flushed stream has no end marker; read what is there.
            var buffer = new byte[8192];
            int n;
            try
            {
                while ((n = inflate.Read(buffer, 0, buffer.Length)) > 0) text.Write(buffer, 0, n);
            }
            catch (InvalidDataException)
            {
                // Not reached for sync-flushed data, but an unterminated stream
                // must not be mistaken for a test failure on some runtime.
            }
            return Encoding.UTF8.GetString(text.ToArray())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        [Fact]
        public void EachFrame_InflatesToItsMessage_AfterTheOnesBeforeIt()
        {
            using var deflater = new FrameDeflater();
            var frames = new List<byte[]>();
            var messages = new[] { "{\"type\":\"A\",\"n\":1}", "{\"type\":\"B\",\"n\":2}", "{\"type\":\"A\",\"n\":3}" };
            for (int i = 0; i < messages.Length; i++)
            {
                frames.Add(deflater.Compress(Encoding.UTF8.GetBytes(messages[i])).ToArray());
                // After every frame - not only at the end - the stream so far
                // decodes to exactly the messages so far.
                Assert.Equal(messages.Take(i + 1), Inflate(frames));
            }
        }

        [Fact]
        public void ARealStateFrame_ShrinksWellBelowTheThreshold_OnceTheStreamHasSeenOne()
        {
            ContentRegistry.Initialize();
            using var deflater = new FrameDeflater();
            var packet = StateFrameSizeTests.BuildRealisticMidGamePacket();
            byte[] json = PacketJsonCodec.SerializeToUtf8(ref packet);

            int first = deflater.Compress(json).Count;
            int total = 0;
            for (int i = 0; i < 60; i++)
            {
                // A ticking frame: the epoch and the health move every second.
                packet.LogicEpochCounter += 10;
                packet.PlayerHp = 1000 + i * 37;
                total += deflater.Compress(PacketJsonCodec.SerializeToUtf8(ref packet)).Count;
            }

            _output.WriteLine($"json {json.Length} B, first frame {first} B, next 60 average {total / 60} B = {total / 1024.0:F1} KB a minute");
            Assert.True(first < json.Length / 2, $"first frame {first} of {json.Length}");
            // The task's threshold is 150 KB a player a minute.
            Assert.True(total < 150 * 1024 / 4, $"{total} bytes a minute");
        }

        [Fact]
        public async Task ACompressingSession_SendsBinaryDeflate_AndAPlainOneStillSendsText()
        {
            var plainSocket = new FakeWebSocket(gateOpen: true);
            var plain = new WebSocketSession(plainSocket, string.Empty, useJsonProtocol: true);
            var deflatedSocket = new FakeWebSocket(gateOpen: true);
            var deflated = new WebSocketSession(deflatedSocket, string.Empty, useJsonProtocol: true, compressFrames: true);
            Assert.False(plain.CompressFrames);
            Assert.True(deflated.CompressFrames);

            foreach (var session in new[] { plain, deflated })
            {
                session.EnqueueEvent(Encoding.UTF8.GetBytes("{\"e\":1}"), WebSocketMessageType.Text);
                session.EnqueueEvent(Encoding.UTF8.GetBytes("{\"e\":2}"), WebSocketMessageType.Text);
            }
            await plainSocket.WaitForFramesAsync(2);
            await deflatedSocket.WaitForFramesAsync(2);

            Assert.Equal(new[] { "{\"e\":1}", "{\"e\":2}" }, plainSocket.Texts());
            Assert.Equal(new[] { "{\"e\":1}", "{\"e\":2}" }, Inflate(deflatedSocket.Frames));
        }

        [Fact]
        public void TheBinaryProtocol_IsNeverDeflated()
        {
            var session = new WebSocketSession(new FakeWebSocket(gateOpen: true), string.Empty, useJsonProtocol: false, compressFrames: true);
            Assert.False(session.CompressFrames);
        }
    }
}
