using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Network;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A socket whose sends wait on a gate - a peer that is slow, or has
    /// stopped reading, until the test says otherwise. Records every frame
    /// and the most sends it ever saw in flight at once.
    /// </summary>
    internal sealed class FakeWebSocket : WebSocket
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _frameArrived = new(0);
        private readonly SemaphoreSlim _sendStarted = new(0);
        private int _inFlight;

        public FakeWebSocket(bool gateOpen = false)
        {
            if (gateOpen) _gate.TrySetResult();
        }

        public readonly ConcurrentQueue<byte[]> Frames = new();
        public int SendAttempts;
        public int MaxConcurrentOperations;
        public int CloseCalls;
        public WebSocketState CurrentState = WebSocketState.Open;

        public void OpenGate() => _gate.TrySetResult();

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => CurrentState;
        public override string? SubProtocol => null;

        public override void Abort() => CurrentState = WebSocketState.Aborted;
        public override void Dispose() { }
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken t) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken t) => new TaskCompletionSource<WebSocketReceiveResult>().Task;

        public override async Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken t)
        {
            Enter();
            try
            {
                Interlocked.Increment(ref CloseCalls);
                CurrentState = WebSocketState.Closed;
                await Task.Yield();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                Interlocked.Increment(ref SendAttempts);
                byte[] copy = buffer.ToArray();
                _sendStarted.Release();
                await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                Frames.Enqueue(copy);
                _frameArrived.Release();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private void Enter()
        {
            int now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref MaxConcurrentOperations)) &&
                   Interlocked.CompareExchange(ref MaxConcurrentOperations, now, seen) != seen)
            {
            }
        }

        /// <summary>Waits until the writer has entered a send (and is now blocked on the gate).</summary>
        public async Task WaitForSendStartedAsync()
        {
            Assert.True(await _sendStarted.WaitAsync(TimeSpan.FromSeconds(5)), "the writer never started a send");
        }

        public async Task WaitForFramesAsync(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Assert.True(await _frameArrived.WaitAsync(TimeSpan.FromSeconds(5)), $"only {i} of {count} frames arrived");
            }
        }

        public List<string> Texts() => Frames.Select(f => System.Text.Encoding.UTF8.GetString(f)).ToList();
    }

    /// <summary>
    /// Task 41: the per-session outbox. No database - every test drives a
    /// WebSocketSession over a FakeWebSocket.
    /// </summary>
    /// <remarks>
    /// Before the outbox, a loot line, a combat blow or a private message that
    /// met a state frame in flight was dropped on the floor, and each dispatch
    /// loop awaited its sends in turn so one stalled peer delayed everybody.
    /// </remarks>
    public class SessionOutboxTests
    {
        private static byte[] Text(string s) => System.Text.Encoding.UTF8.GetBytes(s);

        // (a)
        [Fact]
        public async Task EventsEnqueuedDuringABlockedSendArriveInOrderAfterwards()
        {
            var socket = new FakeWebSocket();
            var session = new WebSocketSession(socket, string.Empty, useJsonProtocol: true);

            session.OfferSnapshot(Text("S0"), WebSocketMessageType.Text);
            await socket.WaitForSendStartedAsync();

            for (int i = 1; i <= 5; i++)
            {
                session.EnqueueEvent(Text("E" + i), WebSocketMessageType.Text);
            }

            socket.OpenGate();
            await socket.WaitForFramesAsync(6);

            Assert.Equal(new[] { "S0", "E1", "E2", "E3", "E4", "E5" }, socket.Texts());
            Assert.Equal(1, socket.MaxConcurrentOperations);
        }

        // (b) - and events go out BEFORE the snapshot of the same wake-up.
        [Fact]
        public async Task OnlyTheLatestSnapshotIsDeliveredAndEventsPrecedeIt()
        {
            var socket = new FakeWebSocket();
            var session = new WebSocketSession(socket, string.Empty, useJsonProtocol: true);

            session.EnqueueEvent(Text("E0"), WebSocketMessageType.Text);
            await socket.WaitForSendStartedAsync();

            session.OfferSnapshot(Text("S1"), WebSocketMessageType.Text);
            session.OfferSnapshot(Text("S2"), WebSocketMessageType.Text);
            session.OfferSnapshot(Text("S3"), WebSocketMessageType.Text);
            session.EnqueueEvent(Text("E1"), WebSocketMessageType.Text);

            socket.OpenGate();
            await socket.WaitForFramesAsync(3);
            await Task.Delay(100);

            Assert.Equal(new[] { "E0", "E1", "S3" }, socket.Texts());
        }

        // (c)
        [Fact]
        public async Task ABlockedSessionDoesNotDelayAnotherSessionsEvent()
        {
            var stalled = new FakeWebSocket();
            var healthy = new FakeWebSocket(gateOpen: true);
            var sessionA = new WebSocketSession(stalled, string.Empty, useJsonProtocol: true);
            var sessionB = new WebSocketSession(healthy, string.Empty, useJsonProtocol: true);
            var clients = new ConcurrentDictionary<long, WebSocketSession>();
            clients[1] = sessionA;
            clients[2] = sessionB;

            // Wedge A's writer inside a send.
            sessionA.EnqueueEvent(Text("block"), WebSocketMessageType.Text);
            await stalled.WaitForSendStartedAsync();

            // Warm the JSON encoder so the measurement is the dispatch, not the JIT.
            var warm = new ResponseLootDropPacket { PlayerId = 99 };
            NetworkBroadcastSystem.EnqueueEventTo(new ConcurrentDictionary<long, WebSocketSession>(), 99, ref warm);
            _ = PacketJsonCodec.SerializeToUtf8(ref warm);

            var dropA = new ResponseLootDropPacket { PlayerId = 1 };
            var dropB = new ResponseLootDropPacket { PlayerId = 2 };

            var clock = Stopwatch.StartNew();
            Assert.True(NetworkBroadcastSystem.EnqueueEventTo(clients, 1, ref dropA));
            Assert.True(NetworkBroadcastSystem.EnqueueEventTo(clients, 2, ref dropB));
            clock.Stop();

            Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(5), $"dispatch took {clock.Elapsed.TotalMilliseconds:F2} ms");

            // B's event arrives while A is still stuck.
            await healthy.WaitForFramesAsync(1);
            Assert.Empty(stalled.Frames);

            stalled.OpenGate();
            await stalled.WaitForFramesAsync(2);
        }

        // (d)
        [Fact]
        public async Task The513thEventDropsTheOldestAndIsCounted()
        {
            var socket = new FakeWebSocket();
            var session = new WebSocketSession(socket, string.Empty, useJsonProtocol: true);
            long totalBefore = WebSocketSession.EventsDroppedTotal;

            session.EnqueueEvent(Text("0"), WebSocketMessageType.Text);
            await socket.WaitForSendStartedAsync();

            for (int i = 1; i <= WebSocketSession.EventCapacity + 1; i++)
            {
                session.EnqueueEvent(Text(i.ToString()), WebSocketMessageType.Text);
            }

            Assert.Equal(1, session.EventsDropped);
            Assert.True(WebSocketSession.EventsDroppedTotal >= totalBefore + 1);
            Assert.Equal(WebSocketSession.EventCapacity, session.PendingEventCount);

            socket.OpenGate();
            await socket.WaitForFramesAsync(WebSocketSession.EventCapacity + 1);

            List<string> texts = socket.Texts();
            Assert.Equal("0", texts[0]);
            Assert.Equal("2", texts[1]); // "1" was the oldest waiting, and it went
            Assert.Equal((WebSocketSession.EventCapacity + 1).ToString(), texts[^1]);
        }

        // (e)
        [Fact]
        public async Task ASendTimeoutMarksTheSessionWedged()
        {
            var socket = new FakeWebSocket();
            var session = new WebSocketSession(socket, string.Empty, useJsonProtocol: true, sendTimeout: TimeSpan.FromMilliseconds(50));

            Assert.False(session.IsWedged);
            session.EnqueueEvent(Text("never"), WebSocketMessageType.Text);

            Task finished = await Task.WhenAny(session.WriterTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(session.WriterTask, finished);
            Assert.True(session.IsWedged);

            // A close after the writer stopped must not hang the eviction path.
            Task close = session.CloseAsync(WebSocketCloseStatus.PolicyViolation, "token", CancellationToken.None);
            Assert.True(close.IsCompleted);
        }

        // The close sentinel: a close never overlaps a send.
        [Fact]
        public async Task ACloseWaitsForTheInFlightSendAndNeverOverlapsIt()
        {
            var socket = new FakeWebSocket();
            var session = new WebSocketSession(socket, string.Empty, useJsonProtocol: true);

            session.EnqueueEvent(Text("E0"), WebSocketMessageType.Text);
            await socket.WaitForSendStartedAsync();
            session.EnqueueEvent(Text("E1"), WebSocketMessageType.Text);

            Task close = session.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            await Task.Delay(50);
            Assert.False(close.IsCompleted);
            Assert.Equal(0, socket.CloseCalls);

            socket.OpenGate();
            await close.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, socket.CloseCalls);
            Assert.Equal(1, socket.MaxConcurrentOperations);
            // The close jumped the queue: E1 was for a connection going away.
            Assert.Equal(new[] { "E0" }, socket.Texts());
            await session.WriterTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        // Binary sessions (the retired Unity client) still get whole frames,
        // each from its own rented buffer.
        [Fact]
        public async Task BinaryStateFramesAreDeliveredWhole()
        {
            var socket = new FakeWebSocket(gateOpen: true);
            var session = new WebSocketSession(socket, string.Empty);

            var packet = new StateUpdatePacket { PlayerId = 424242 };
            session.OfferStateFrame(ref packet);
            await socket.WaitForFramesAsync(1);

            byte[] frame = socket.Frames.Single();
            Assert.Equal(System.Runtime.InteropServices.Marshal.SizeOf<StateUpdatePacket>(), frame.Length);
            StateUpdatePacket echoed = System.Runtime.InteropServices.MemoryMarshal.Read<StateUpdatePacket>(frame);
            Assert.Equal(424242L, echoed.PlayerId);
        }
    }
}
