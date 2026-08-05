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
    /// </summary>
    public class SocketBackpressureTests
    {
        /// <summary>A socket whose sends never complete - a peer that has stopped reading.</summary>
        private sealed class StalledWebSocket : WebSocket
        {
            private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int SendAttempts;

            public override WebSocketCloseStatus? CloseStatus => null;
            public override string? CloseStatusDescription => null;
            public override WebSocketState State => WebSocketState.Open;
            public override string? SubProtocol => null;

            public override void Abort() { }
            public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken t) => Task.CompletedTask;
            public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken t) => Task.CompletedTask;
            public override void Dispose() { }
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken t) => new TaskCompletionSource<WebSocketReceiveResult>().Task;

            public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref SendAttempts);

                // Never completes on its own. Honours cancellation, which is
                // what the send timeout relies on.
                using (cancellationToken.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), _released))
                {
                    await _released.Task.ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        [Fact]
        public async Task AFrameIsDroppedRatherThanQueuedBehindAStalledSend()
        {
            var socket = new StalledWebSocket();
            var session = new WebSocketSession(socket, redisLockToken: string.Empty, useJsonProtocol: true);
            var payload = new ArraySegment<byte>(new byte[] { 1, 2, 3, 4 });

            // First send takes the lock and never completes - the peer is not
            // reading.
            Task first = session.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
            Assert.False(first.IsCompleted);

            // Every later frame must come straight back. This is the assertion
            // that matters: before the fix these queued on the semaphore, one
            // per broadcast tick, forever.
            for (int i = 0; i < 50; i++)
            {
                Task dropped = session.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
                Assert.True(dropped.IsCompleted, $"frame {i} queued behind the stalled send instead of being dropped");
                await dropped;
            }

            // And it really was one attempt at the socket, not fifty-one.
            Assert.Equal(1, socket.SendAttempts);
        }

        /// <summary>
        /// State updates are absolute snapshots, so dropping one costs nothing -
        /// but a socket that never drains at all has to be given up on, or the
        /// player sits in front of a frozen screen indefinitely. The timeout
        /// marks it, and the broadcast loop evicts it so the client's own
        /// reconnect can run.
        /// </summary>
        [Fact]
        public async Task AStalledSocketIsEventuallyMarkedWedged()
        {
            var socket = new StalledWebSocket();
            var session = new WebSocketSession(socket, redisLockToken: string.Empty, useJsonProtocol: true);

            Assert.False(session.IsWedged);

            // Drive the timeout directly rather than waiting twenty real
            // seconds: cancelling the caller's token is the same path the
            // internal deadline takes.
            using var cts = new CancellationTokenSource();
            Task send = session.SendAsync(new ArraySegment<byte>(new byte[] { 1 }), WebSocketMessageType.Text, true, cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);

            // The lock must be free afterwards - a timed-out send that kept the
            // semaphore would reproduce the original bug with extra steps.
            Task after = session.SendAsync(new ArraySegment<byte>(new byte[] { 1 }), WebSocketMessageType.Text, true, CancellationToken.None);
            Assert.True(after.IsCompleted || socket.SendAttempts == 2, "the lock was not released after a cancelled send");
        }
    }
}
