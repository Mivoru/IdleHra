using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Network;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A client that stops reading must not freeze its own game.
    ///
    /// THE COMBAT FREEZE. Reported repeatedly: HP stops ticking down, kills
    /// stop appearing, the screen looks frozen - and F5 shows the correct
    /// state, so the server was simulating all along and only delivery had
    /// stopped. The broadcast dirty check, Redis and the reconnect backoff had
    /// all been ruled out.
    ///
    /// It was the send lock. .NET forbids two outstanding sends on one
    /// WebSocket, so every send took a semaphore - with no timeout, from a
    /// fire-and-forget 10 Hz broadcast. When a peer stopped reading, TCP
    /// back-pressure left one send pending forever, it kept the semaphore, and
    /// every later frame queued behind it. Nothing threw and nothing closed:
    /// the socket stayed open and silent, so the client was not disconnected,
    /// it was being ignored - and a client that is not disconnected does not
    /// reconnect.
    ///
    /// These two tests are the shape of that bug, not the shape of the fix.
    /// Since task 41 the socket has a per-session outbox (SessionOutboxTests);
    /// these still hold against it: snapshots do not pile up behind a stalled
    /// send, and a socket that never drains is eventually marked wedged.
    /// </summary>
    public class SocketBackpressureTests
    {
        [Fact]
        public async Task SnapshotsAreNotQueuedBehindAStalledSend()
        {
            var socket = new FakeWebSocket();
            var session = new WebSocketSession(socket, redisLockToken: string.Empty, useJsonProtocol: true);
            var payload = new byte[] { 1, 2, 3, 4 };

            // First snapshot reaches the socket and never completes - the peer
            // is not reading.
            session.OfferSnapshot(payload, WebSocketMessageType.Text);
            await socket.WaitForSendStartedAsync();

            // Every later snapshot must be accepted at once and supersede the
            // one before it. Before the original fix these queued on the
            // semaphore, one per broadcast tick, forever.
            for (int i = 0; i < 50; i++)
            {
                session.OfferSnapshot(payload, WebSocketMessageType.Text);
            }

            await Task.Delay(50);

            // And it really was one attempt at the socket, not fifty-one.
            Assert.Equal(1, socket.SendAttempts);

            // Once the peer drains, exactly one more (the latest) goes out.
            socket.OpenGate();
            await socket.WaitForFramesAsync(2);
            await Task.Delay(50);
            Assert.Equal(2, socket.SendAttempts);
        }

        /// <summary>
        /// State updates are absolute snapshots, so dropping one costs nothing -
        /// but a socket that never drains at all has to be given up on, or the
        /// player sits in front of a frozen screen indefinitely. The timeout
        /// marks it, and SendToPlayer evicts it so the client's own reconnect
        /// can run.
        /// </summary>
        [Fact]
        public async Task AStalledSocketIsEventuallyMarkedWedged()
        {
            var socket = new FakeWebSocket();
            var session = new WebSocketSession(socket, string.Empty, useJsonProtocol: true, sendTimeout: TimeSpan.FromMilliseconds(50));

            Assert.False(session.IsWedged);
            session.OfferSnapshot(new byte[] { 1 }, WebSocketMessageType.Text);

            await session.WriterTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(session.IsWedged);
        }
    }
}
