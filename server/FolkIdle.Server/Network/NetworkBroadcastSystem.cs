using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Network
{
    /// <summary>
    /// One connected player's socket, and the only code that writes to it.
    /// </summary>
    /// <remarks>
    /// Modul: A PER-SESSION OUTBOX (task 41, audit item 3, 2026-09-27).
    ///
    /// .NET forbids two outstanding send-family operations on one WebSocket,
    /// and five independent call sites target the same socket: the 10 Hz state
    /// broadcast, the loot feed, the combat feed, chat, and every disconnect
    /// path. This class used to serialise them with a semaphore taken with a
    /// ZERO timeout - which was the right call for snapshots (see
    /// OfferSnapshot) and silently wrong for everything else. A loot line, a
    /// combat blow or a private message that arrived while a state frame was
    /// in flight was simply discarded, and at 10 Hz a state frame is in flight
    /// a large share of the time. On top of that, each dispatch loop awaited
    /// its sends one after another, so one peer that had stopped reading held
    /// its loop - and every other player behind it - for up to the 20 s send
    /// timeout.
    ///
    /// Now there is one writer task per session and nothing else touches the
    /// socket. Producers never await and never block:
    ///   - EnqueueEvent: a bounded FIFO (512, drop-OLDEST, counted). Events
    ///     are deltas - each one is a line the player would otherwise never
    ///     see - so they queue rather than drop.
    ///   - OfferSnapshot / OfferStateFrame: one slot, latest wins. A state
    ///     frame is an ABSOLUTE snapshot, so an older one still waiting when a
    ///     newer one arrives is worth nothing. That is THE COMBAT FREEZE fix
    ///     below, kept.
    ///   - CloseAsync: a sentinel the writer honours before anything else, so
    ///     a close never races a send.
    ///
    /// THE COMBAT FREEZE, for the record, because it is why snapshots keep
    /// latest-wins semantics: the old lock was once taken with no timeout from
    /// a fire-and-forget 10 Hz broadcast. When a peer stopped reading, TCP
    /// back-pressure left one send pending forever, it kept the lock, and every
    /// later frame queued behind it - nothing threw, nothing closed, the socket
    /// stayed open and silent, and the client (whose reconnect logic is fine)
    /// had nothing to reconnect FROM. The send timeout and IsWedged below are
    /// the second half of that fix, and they now live in the writer.
    /// </remarks>
    // Modul: task 46 (audit item 8, "8a - measure"). Nothing on /metrics
    // answered how big a JSON state frame actually is or how long
    // PacketJsonCodec.SerializeToUtf8 takes on the tick thread, and
    // docs/TASK_BOARD.md #46's go/no-go call - close the item, or add
    // compression (8c) and maybe deltas (8b) - needs a week of real
    // production numbers, not a guess. CLAUDE.md: "a number a test PRINTS is
    // not a number a test CHECKS" applies to a dashboard nobody reads just
    // as much as to a test nobody asserts on.
    //
    // Bytes count every frame actually handed to a session: the JSON
    // serialize output, plus the binary struct's fixed size for the
    // (retired-client) binary path - a memcpy of a constant-size unmanaged
    // struct, cheap enough to fold into the byte total without its own
    // timer. The microsecond histogram is JSON-only, wrapped tightly around
    // SerializeToUtf8 itself, because that Utf8JsonWriter walk over ~230
    // fields is the actual cost the CPU-budget threshold is about - timing
    // the binary path would just measure noise.
    //
    // Plain Interlocked counters rather than a lock: written from the single
    // tick thread (SendToPlayer runs once per online player per 10Hz tick),
    // read from a concurrent HTTP scrape - the same shape as
    // WebSocketSession.EventsDroppedTotal below.
    public static class StateFrameMetrics
    {
        private static long s_bytesTotal;
        private static long s_framesTotal;
        private static long s_serializeSumUs;
        private static long s_serializeCount;
        private static long s_bucket100Us;
        private static long s_bucket250Us;
        private static long s_bucket500Us;
        private static long s_bucket1000Us;
        private static long s_bucket2500Us;
        private static long s_bucketInfUs;

        public static long BytesTotal => Interlocked.Read(ref s_bytesTotal);
        public static long FramesTotal => Interlocked.Read(ref s_framesTotal);
        public static long SerializeSumMicroseconds => Interlocked.Read(ref s_serializeSumUs);
        public static long SerializeCount => Interlocked.Read(ref s_serializeCount);
        public static long Bucket100Us => Interlocked.Read(ref s_bucket100Us);
        public static long Bucket250Us => Interlocked.Read(ref s_bucket250Us);
        public static long Bucket500Us => Interlocked.Read(ref s_bucket500Us);
        public static long Bucket1000Us => Interlocked.Read(ref s_bucket1000Us);
        public static long Bucket2500Us => Interlocked.Read(ref s_bucket2500Us);
        public static long BucketInfUs => Interlocked.Read(ref s_bucketInfUs);

        /// <summary>Records one JSON state frame: its wire size, and how long SerializeToUtf8 took to build it.</summary>
        public static void RecordJsonFrame(int bytes, long serializeMicroseconds)
        {
            Interlocked.Add(ref s_bytesTotal, bytes);
            Interlocked.Increment(ref s_framesTotal);
            Interlocked.Add(ref s_serializeSumUs, serializeMicroseconds);
            Interlocked.Increment(ref s_serializeCount);
            if (serializeMicroseconds <= 100) Interlocked.Increment(ref s_bucket100Us);
            if (serializeMicroseconds <= 250) Interlocked.Increment(ref s_bucket250Us);
            if (serializeMicroseconds <= 500) Interlocked.Increment(ref s_bucket500Us);
            if (serializeMicroseconds <= 1000) Interlocked.Increment(ref s_bucket1000Us);
            if (serializeMicroseconds <= 2500) Interlocked.Increment(ref s_bucket2500Us);
            Interlocked.Increment(ref s_bucketInfUs);
        }

        /// <summary>Records one binary state frame's fixed size. No serialize timer - see the class remarks.</summary>
        public static void RecordBinaryFrame(int bytes)
        {
            Interlocked.Add(ref s_bytesTotal, bytes);
            Interlocked.Increment(ref s_framesTotal);
        }

        // Modul: test-only reset. StateFrameMetrics is exercised directly by
        // StateFrameSizeTests with no database or socket involved, and these
        // are process-wide static counters (deliberately, like every other
        // /metrics counter in this file) - without a reset, an earlier
        // test's frames would leak into a later test's assertions.
        internal static void ResetForTests()
        {
            Interlocked.Exchange(ref s_bytesTotal, 0);
            Interlocked.Exchange(ref s_framesTotal, 0);
            Interlocked.Exchange(ref s_serializeSumUs, 0);
            Interlocked.Exchange(ref s_serializeCount, 0);
            Interlocked.Exchange(ref s_bucket100Us, 0);
            Interlocked.Exchange(ref s_bucket250Us, 0);
            Interlocked.Exchange(ref s_bucket500Us, 0);
            Interlocked.Exchange(ref s_bucket1000Us, 0);
            Interlocked.Exchange(ref s_bucket2500Us, 0);
            Interlocked.Exchange(ref s_bucketInfUs, 0);
        }
    }

    public class WebSocketSession
    {
        public WebSocket Socket { get; }
        public ClientInputThrottler Throttler { get; }
        public string RedisLockToken { get; }
        public TokenBucket TokenBucket;
        public TokenBucket ChatTokenBucket;

        // Modul: cached from the player's live TickStatePayload.GuildId
        // (see SimulationEngine.AddActivePlayer/UpdateSessionGuildId) so
        // guild-channel chat routing (BroadcastGuildChatMessage) can filter
        // _connectedClients without this network-layer class needing a
        // reference back into SimulationEngine's own _guildMembersIndex. 0
        // means "not in a guild" - never matches a real GuildId, which are
        // always positive.
        public long GuildId;

        // Modul: JSON WebSocket mode, 2026-08-02. Phase 0 of the web client
        // port plan. Per connection, never global, and decided once at
        // handshake time - a session cannot switch protocols mid-stream.
        //
        // False is the default in the fullest sense: the Unity client sends a
        // binary AuthHandshakePacket, lands in the binary branch, and every
        // send path below takes the blittable-write route it always has.
        public bool UseJsonProtocol { get; }

        // Task 46: text frames go out deflated (FrameDeflater), when the
        // client asked for it in the handshake. Owned by the writer loop.
        private readonly FrameDeflater? _deflater;

        /// <summary>Whether this session's text frames are sent deflated.</summary>
        public bool CompressFrames => _deflater != null;

        /// <summary>How many events one session may hold before the oldest is dropped.</summary>
        public const int EventCapacity = 512;

        /// <summary>
        /// Events dropped because an outbox was full, across every session
        /// since start. Exposed on /metrics as
        /// folkidle_outbox_events_dropped_total.
        /// </summary>
        /// <remarks>
        /// Modul: a drop nobody counts is the silent loss this outbox exists
        /// to end, so the one place it can still drop reports that it did.
        /// </remarks>
        public static long EventsDroppedTotal => Interlocked.Read(ref s_eventsDroppedTotal);
        private static long s_eventsDroppedTotal;

        /// <summary>Events dropped from THIS session's outbox.</summary>
        public long EventsDropped => Interlocked.Read(ref _eventsDropped);
        private long _eventsDropped;

        /// <summary>Events waiting for this session's writer.</summary>
        public int PendingEventCount => _events.Reader.Count;

        /// <summary>
        /// How long one frame may take before the socket is considered wedged.
        ///
        /// Generous - a mobile client on a bad connection should not be evicted
        /// for a slow second. What it stops is the unbounded case: a peer that
        /// has stopped reading entirely, where the send never completes at all.
        /// </summary>
        public static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(20);
        private readonly TimeSpan _sendTimeout;

        /// <summary>
        /// True once a send has timed out. SendToPlayer reads it and evicts
        /// the session, which is what lets the client's own reconnect logic
        /// run - a disconnect the client can act on beats a socket that is
        /// open and silent.
        /// </summary>
        public volatile bool IsWedged;

        private readonly struct OutboundFrame
        {
            public readonly byte[] Buffer;
            public readonly WebSocketMessageType Type;

            public OutboundFrame(byte[] buffer, WebSocketMessageType type)
            {
                Buffer = buffer;
                Type = type;
            }
        }

        private sealed class CloseRequest
        {
            public readonly WebSocketCloseStatus Status;
            public readonly string Description;
            public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public CloseRequest(WebSocketCloseStatus status, string description)
            {
                Status = status;
                Description = description;
            }
        }

        private readonly Channel<OutboundFrame> _events;

        // Modul: the snapshot slot is guarded by a plain lock rather than an
        // Interlocked.Exchange of a frame object, so the binary path can hand
        // over a rented buffer without allocating a holder per 10 Hz tick.
        // Held for a few field writes only - never across an await.
        private readonly object _snapshotGate = new();
        private byte[]? _snapshotBuffer;
        private int _snapshotLength;
        private WebSocketMessageType _snapshotType;
        private bool _snapshotRented;

        private readonly object _lifecycleGate = new();
        private CloseRequest? _closeRequest;
        private bool _writerExited;
        private volatile bool _shutdown;

        // Modul: one wake-up per burst, not one per frame. A producer
        // releases the semaphore only on the 0 -> 1 transition of this flag,
        // and the writer clears it BEFORE it drains - so anything enqueued
        // during the drain signals again and is picked up by the next pass
        // rather than lost between a check and a wait.
        private readonly SemaphoreSlim _signal = new(0);
        private int _signalled;

        private readonly Task _writer;

        public WebSocketSession(WebSocket socket, string redisLockToken, bool useJsonProtocol = false, bool compressFrames = false)
            : this(socket, redisLockToken, useJsonProtocol, DefaultSendTimeout, compressFrames)
        {
        }

        // Test seam: SessionOutboxTests drives the wedge path with a
        // millisecond timeout rather than waiting twenty real seconds.
        internal WebSocketSession(WebSocket socket, string redisLockToken, bool useJsonProtocol, TimeSpan sendTimeout, bool compressFrames = false)
        {
            Socket = socket;
            RedisLockToken = redisLockToken;
            UseJsonProtocol = useJsonProtocol;
            _deflater = useJsonProtocol && compressFrames ? new FrameDeflater() : null;
            Throttler = new ClientInputThrottler();
            TokenBucket = NetworkThrottlingEngine.CreateBucket();
            ChatTokenBucket = ChatEngine.CreateChatBucket();
            _sendTimeout = sendTimeout;

            _events = Channel.CreateBounded<OutboundFrame>(
                new BoundedChannelOptions(EventCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                },
                _ =>
                {
                    Interlocked.Increment(ref _eventsDropped);
                    Interlocked.Increment(ref s_eventsDroppedTotal);
                });

            _writer = Task.Run(WriterLoopAsync);
        }

        /// <summary>
        /// Queues one event frame. Never blocks, never awaits.
        /// </summary>
        /// <remarks>
        /// The buffer is sent as-is at some later point, so the caller must
        /// not write to it afterwards. One immutable buffer may be enqueued to
        /// many sessions - chat does exactly that.
        /// </remarks>
        public void EnqueueEvent(byte[] frame, WebSocketMessageType messageType)
        {
            if (_shutdown) return;
            _events.Writer.TryWrite(new OutboundFrame(frame, messageType));
            Signal();
        }

        /// <summary>Encodes one packet for this session's protocol and queues it as an event.</summary>
        public void EnqueuePacket<T>(ref T packet) where T : unmanaged
        {
            if (UseJsonProtocol)
            {
                EnqueueEvent(PacketJsonCodec.SerializeToUtf8(ref packet), WebSocketMessageType.Text);
            }
            else
            {
                EnqueueEvent(EncodeBinary(ref packet), WebSocketMessageType.Binary);
            }
        }

        /// <summary>A fresh blittable copy of one packet - owned by the caller, safe to share.</summary>
        public static byte[] EncodeBinary<T>(ref T packet) where T : unmanaged
        {
            ReadOnlySpan<T> span = MemoryMarshal.CreateReadOnlySpan(ref packet, 1);
            return MemoryMarshal.AsBytes(span).ToArray();
        }

        /// <summary>
        /// Offers a state snapshot. Replaces any snapshot still waiting - the
        /// newer one carries the same truth, only fresher.
        /// </summary>
        public void OfferSnapshot(byte[] frame, WebSocketMessageType messageType)
        {
            SetSnapshot(frame, frame.Length, messageType, rented: false);
        }

        /// <summary>
        /// Offers a state snapshot on the binary protocol.
        /// </summary>
        /// <remarks>
        /// Modul: this used to copy into one per-session DiagnosticSendBuffer
        /// and send from it, which is only safe while a lock guarantees the
        /// previous send has finished reading. With a writer that sends later,
        /// the next tick's copy would land in the buffer the writer is still
        /// sending - a frame spliced out of two. So each offer copies into a
        /// buffer rented from the shared pool, and whoever retires it (the
        /// writer after sending, or a newer offer superseding it) returns it.
        /// </remarks>
        public void OfferStateFrame(ref StateUpdatePacket packet)
        {
            int size = Unsafe.SizeOf<StateUpdatePacket>();
            byte[] rented = ArrayPool<byte>.Shared.Rent(size);
            ReadOnlySpan<StateUpdatePacket> span = MemoryMarshal.CreateReadOnlySpan(ref packet, 1);
            MemoryMarshal.AsBytes(span).CopyTo(rented);
            SetSnapshot(rented, size, WebSocketMessageType.Binary, rented: true);
        }

        private void SetSnapshot(byte[] buffer, int length, WebSocketMessageType type, bool rented)
        {
            if (_shutdown)
            {
                if (rented) ArrayPool<byte>.Shared.Return(buffer);
                return;
            }

            byte[]? superseded;
            bool supersededRented;
            lock (_snapshotGate)
            {
                superseded = _snapshotBuffer;
                supersededRented = _snapshotRented;
                _snapshotBuffer = buffer;
                _snapshotLength = length;
                _snapshotType = type;
                _snapshotRented = rented;
            }

            if (superseded != null && supersededRented)
            {
                ArrayPool<byte>.Shared.Return(superseded);
            }

            Signal();
        }

        /// <summary>
        /// Asks the writer to close the socket. Completes when the close has
        /// run (or the writer has already stopped).
        /// </summary>
        /// <remarks>
        /// Modul: a sentinel rather than a direct Socket.CloseAsync, so a
        /// close can never be the second outstanding send-family operation on
        /// the socket. It jumps the queue: events still waiting are for a
        /// connection that is going away. The first close requested wins - a
        /// later one (say the shutdown sweep after an eviction) just waits on
        /// the same result.
        /// </remarks>
        public Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            Task done;
            lock (_lifecycleGate)
            {
                if (_writerExited) return Task.CompletedTask;
                _closeRequest ??= new CloseRequest(closeStatus, statusDescription);
                done = _closeRequest.Done.Task;
            }

            Signal();
            return cancellationToken.CanBeCanceled ? done.WaitAsync(cancellationToken) : done;
        }

        /// <summary>
        /// Stops the writer without closing - the connection has already
        /// ended. Called once the receive loop for this socket has finished.
        /// </summary>
        public void Shutdown()
        {
            _shutdown = true;
            Signal();
        }

        /// <summary>The writer task. Test-only observability.</summary>
        internal Task WriterTask => _writer;

        private void Signal()
        {
            if (Interlocked.Exchange(ref _signalled, 1) == 0)
            {
                _signal.Release();
            }
        }

        private async Task WriterLoopAsync()
        {
            try
            {
                while (true)
                {
                    await _signal.WaitAsync().ConfigureAwait(false);
                    Volatile.Write(ref _signalled, 0);

                    if (await TryHonourCloseAsync().ConfigureAwait(false)) return;
                    if (_shutdown || IsTerminal(Socket.State)) return;

                    // Modul: EVENTS FIRST, then the snapshot, in the same
                    // wake-up. The combat feed needs this order: a blow must
                    // not arrive visibly after the health change it explains.
                    //
                    // And a BUDGET: at most one queue's worth per pass.
                    // Draining `while (TryRead)` unbounded would let a producer
                    // that never pauses starve the snapshot forever - the
                    // exact shape that stopped equipment drops server-wide in
                    // CombatLootEngine. The budget is a fixed bound rather
                    // than the depth read at the top, deliberately: an event
                    // that arrived while an earlier send was blocked is still
                    // older news than the snapshot waiting behind it, so it
                    // goes first. Whatever is left has signalled again and is
                    // next pass's work.
                    for (int i = 0; i < EventCapacity && _events.Reader.TryRead(out OutboundFrame frame); i++)
                    {
                        if (Volatile.Read(ref _closeRequest) != null || _shutdown) break;
                        if (!await SendFrameAsync(new ArraySegment<byte>(frame.Buffer), frame.Type).ConfigureAwait(false)) return;
                    }

                    if (Volatile.Read(ref _closeRequest) != null || _shutdown)
                    {
                        Signal();
                        continue;
                    }

                    byte[]? snapshot;
                    int length;
                    WebSocketMessageType type;
                    bool rented;
                    lock (_snapshotGate)
                    {
                        snapshot = _snapshotBuffer;
                        length = _snapshotLength;
                        type = _snapshotType;
                        rented = _snapshotRented;
                        _snapshotBuffer = null;
                    }

                    if (snapshot != null)
                    {
                        bool sent;
                        try
                        {
                            sent = await SendFrameAsync(new ArraySegment<byte>(snapshot, 0, length), type).ConfigureAwait(false);
                        }
                        finally
                        {
                            if (rented) ArrayPool<byte>.Shared.Return(snapshot);
                        }

                        if (!sent) return;
                    }

                    // Events left over by the budget were enqueued before
                    // this pass cleared the signal, so nothing else will wake
                    // the writer for them.
                    if (_events.Reader.Count > 0) Signal();
                }
            }
            catch (Exception ex)
            {
                // Modul: nothing escapes a writer - an unobserved fault here
                // would be a player whose screen stops moving with no log.
                Console.WriteLine($"Session writer stopped: {ex.GetBaseException().Message}");
            }
            finally
            {
                // The deflater's only caller is this loop, so it ends with it.
                _deflater?.Dispose();
                ExitWriter();
            }
        }

        /// <summary>Sends one frame. False means the writer must stop.</summary>
        private async Task<bool> SendFrameAsync(ArraySegment<byte> segment, WebSocketMessageType type)
        {
            WebSocketState state = Socket.State;
            if (state != WebSocketState.Open)
            {
                // CloseReceived: the peer is leaving and the receive loop is
                // about to request the close - keep going so it can.
                return !IsTerminal(state);
            }

            if (_deflater != null && type == WebSocketMessageType.Text)
            {
                // Every text frame, events and snapshots alike, through the one
                // stream: the client inflates them in the order they were sent.
                segment = _deflater.Compress(segment);
                type = WebSocketMessageType.Binary;
            }

            using var timeout = new CancellationTokenSource(_sendTimeout);
            try
            {
                await Socket.SendAsync(segment, type, true, timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // The peer is not reading. Mark it so SendToPlayer evicts the
                // session - a disconnect the client can act on beats a socket
                // that is open and silent. A cancelled send has aborted the
                // socket, so this writer has nothing left to do.
                IsWedged = true;
                Console.WriteLine($"Session send timed out after {_sendTimeout.TotalSeconds:F0}s; marked wedged.");
                return false;
            }
            catch (Exception ex) when (ex is WebSocketException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                Console.WriteLine($"Session send failed: {ex.Message}");
                return !IsTerminal(Socket.State);
            }
        }

        private async Task<bool> TryHonourCloseAsync()
        {
            CloseRequest? request = Volatile.Read(ref _closeRequest);
            if (request == null) return false;

            try
            {
                WebSocketState state = Socket.State;
                if (state == WebSocketState.Open || state == WebSocketState.CloseReceived)
                {
                    // Bounded, like a send: a close must never be the thing
                    // that hangs for a peer that is already gone.
                    using var timeout = new CancellationTokenSource(_sendTimeout);
                    await Socket.CloseAsync(request.Status, request.Description, timeout.Token).ConfigureAwait(false);
                }

                request.Done.TrySetResult();
            }
            catch (Exception ex)
            {
                request.Done.TrySetException(ex);
            }

            return true;
        }

        private void ExitWriter()
        {
            CloseRequest? pending;
            lock (_lifecycleGate)
            {
                _writerExited = true;
                pending = _closeRequest;
            }

            _shutdown = true;
            pending?.Done.TrySetResult();

            byte[]? snapshot;
            bool rented;
            lock (_snapshotGate)
            {
                snapshot = _snapshotBuffer;
                rented = _snapshotRented;
                _snapshotBuffer = null;
            }

            if (snapshot != null && rented) ArrayPool<byte>.Shared.Return(snapshot);
        }

        private static bool IsTerminal(WebSocketState state) =>
            state == WebSocketState.Closed || state == WebSocketState.Aborted;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct AdminCommandPacket
    {
        public byte CommandType; // 1 = XP, 2 = Drops
        public int MultiplierValue;
    }

    public partial class NetworkBroadcastSystem
    {
        private readonly HttpListener _httpListener;
        private readonly ConcurrentDictionary<long, WebSocketSession> _connectedClients = new();

        private bool _isRunning;

        public ref long GetThrottledCounter() => ref _throttledCounter;
        private long _throttledCounter;
        private long _acceptedPacketsWindow;
        private long _throughputWindowEpoch;

        private readonly IServiceProvider _serviceProvider;
        private readonly IDbContextFactory<FolkIdleDbContext> _contextFactory;
        private readonly RedisPlayerSessionLock? _redisSessionLock;
        private readonly string _jwtSecretKey;
        private AntiCheatTelemetryEngine? _antiCheatTelemetryEngine;
        private SimulationEngine? _simulationEngine;
        private BillingVerificationEngine? _billingVerificationEngine;

        // Modul: registered rather than constructed here for the same reason
        // the billing engine is - PushNotificationTriggerEngine is built in
        // Program.cs after this system exists.
        private PushNotificationTriggerEngine? _pushNotificationTriggerEngine;
        private PlayerSessionRegistry? _playerSessionRegistry;
        private readonly ChatEngine _chatEngine;

        public NetworkBroadcastSystem(IServiceProvider serviceProvider, string jwtSecretKey, string uriPrefix = "http://localhost:8080/")
        {
            _serviceProvider = serviceProvider;
            _contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();
            _redisSessionLock = serviceProvider.GetService<RedisPlayerSessionLock>();
            _jwtSecretKey = jwtSecretKey;
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add(uriPrefix);
            _chatEngine = new ChatEngine(serviceProvider);
            _chatEngine.OnDispatchReady += HandleChatDispatchAsync;
        }

        // Modul: called from SimulationEngine.AddActivePlayer (initial
        // login) and the GuildMembershipChangeQueue drain (join/leave) -
        // the two points where a player's live GuildId is established or
        // changes. A guildId of 0 (not in a guild) is a valid, expected
        // value here, not an error.
        public void UpdateSessionGuildId(long playerId, long guildId)
        {
            if (_connectedClients.TryGetValue(playerId, out var session))
            {
                session.GuildId = guildId;
            }
        }

        public void RegisterCheckpointManager(StateCheckpointManager manager)
        {
            manager.RegisterDisconnectCallback(ForceDisconnect);
            _checkpointManager = manager;
        }

        // Task 88: an offline rebirth waits for the player's queued
        // checkpoints before it resets the rows they are writing.
        private StateCheckpointManager? _checkpointManager;

        public void RegisterAntiCheatTelemetryEngine(AntiCheatTelemetryEngine engine)
        {
            _antiCheatTelemetryEngine = engine;
        }

        // Modul: back-reference for the /metrics endpoint's tick-duration
        // histogram (see HandleMetrics) - mirrors the existing
        // RegisterCheckpointManager/RegisterAntiCheatTelemetryEngine wiring
        // pattern, since SimulationEngine and NetworkBroadcastSystem are
        // constructed independently in Program.cs with no natural
        // constructor-time reference in either direction.
        public void RegisterSimulationEngine(SimulationEngine engine)
        {
            _simulationEngine = engine;
        }

        // Modul: the world boss engine, for the dev-only window override only.
        // Null in any graph that never registers it, and the dev route then
        // answers 503 rather than pretending.
        private WorldBossEngine? _worldBossEngine;

        public void RegisterWorldBossEngine(WorldBossEngine engine)
        {
            _worldBossEngine = engine;
        }

        // Modul: matches the RegisterSimulationEngine wiring pattern -
        // BillingVerificationEngine is constructed independently in
        // Program.cs (it needs RetryingDbContextOptions and
        // IIapReceiptValidator, neither registered in the DI container),
        // so it is handed to NetworkBroadcastSystem explicitly rather than
        // resolved through _serviceProvider.
        public void RegisterBillingVerificationEngine(BillingVerificationEngine engine)
        {
            _billingVerificationEngine = engine;
        }

        public void RegisterPushNotificationTriggerEngine(PushNotificationTriggerEngine engine)
        {
            _pushNotificationTriggerEngine = engine;
        }

        // Modul: Play Mode audit fix. PlayerSessionRegistry is constructed
        // in Program.cs after NetworkBroadcastSystem (same "no natural
        // constructor-time reference" situation as RegisterSimulationEngine
        // above) and was never registered in the DI container either -
        // HandleGuildCreate/HandleGuildJoin's _serviceProvider.GetRequiredService<PlayerSessionRegistry>()
        // therefore always threw InvalidOperationException and 500'd,
        // found via a live Play Mode + direct HTTP guild-create test.
        public void RegisterPlayerSessionRegistry(PlayerSessionRegistry registry)
        {
            _playerSessionRegistry = registry;
        }

        public void Start()
        {
            _httpListener.Start();
            _isRunning = true;
            Task.Run(ListenLoopAsync);
            SubscribeToSessionEviction();
            _chatEngine.Subscribe();
            Task.Run(LootDropDispatchLoopAsync);
            Task.Run(CombatEventDispatchLoopAsync);
        }

        // Modul: Loot Event Feed. Drains PlayerSessionRegistry.OutboundLootDropQueue
        // and hands each drop to the outbox of the player it belongs to.
        //
        // Its own background loop rather than a hook on the 10Hz tick,
        // because drops are produced by CombatLootEngine's own 3-second cron
        // (never on the tick thread). The 50ms idle sleep stays - loot is
        // bursty and rare, so a tight spin would burn a core to deliver a
        // handful of messages a minute.
        //
        // Modul: task 41. This loop ENQUEUES and never awaits a socket. It
        // used to await each send in turn, so one peer that had stopped
        // reading held every other player's loot for up to the 20 s send
        // timeout - and a drop that met a state frame in flight was thrown
        // away. The session's own writer does the sending now.
        private async Task LootDropDispatchLoopAsync()
        {
            while (_isRunning)
            {
                var registry = _playerSessionRegistry;
                if (registry == null || !registry.OutboundLootDropQueue.TryDequeue(out ResponseLootDropPacket drop))
                {
                    await Task.Delay(50);
                    continue;
                }

                try
                {
                    // False when the player logged off between the drop
                    // resolving and this dispatch. The item is already
                    // persisted, so that loses nothing but the on-screen line.
                    EnqueueEventTo(_connectedClients, drop.PlayerId, ref drop);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Loot drop dispatch failed for player {drop.PlayerId}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Queues one event packet on a connected player's outbox. Never awaits.
        /// </summary>
        /// <remarks>
        /// Static over the session map so SessionOutboxTests can prove that a
        /// dispatch returns at once even when the target's socket is stalled.
        /// </remarks>
        internal static bool EnqueueEventTo<T>(ConcurrentDictionary<long, WebSocketSession> clients, long playerId, ref T packet) where T : unmanaged
        {
            if (!clients.TryGetValue(playerId, out var session) || session.Socket.State != WebSocketState.Open)
            {
                return false;
            }

            session.EnqueuePacket(ref packet);
            return true;
        }

        // Modul: Combat Event Feed. Drains Domain.Combat.CombatEventFeed and
        // hands each resolved blow to the outbox of the player it belongs to.
        //
        // The same shape as the loot loop directly above, with one difference
        // worth knowing: combat events are produced by the 10Hz simulation
        // tick itself rather than by a 3-second cron, so the idle sleep is
        // shorter. At 50ms a burst of events resolved in one tick would be
        // delivered over several hundred milliseconds and arrive visibly after
        // the health change they explain. The writer keeps the other half of
        // that promise: it sends queued events BEFORE the pending snapshot.
        //
        // The feed is bounded and drops when full (see CombatEventFeed), so a
        // client that cannot keep up costs the simulation nothing.
        private async Task CombatEventDispatchLoopAsync()
        {
            while (_isRunning)
            {
                if (!Domain.Combat.CombatEventFeed.TryDequeue(out ResponseCombatEventPacket combatEvent))
                {
                    await Task.Delay(20);
                    continue;
                }

                try
                {
                    // False when nobody is watching. The blow already happened
                    // and is already reflected in the authoritative state;
                    // only the on-screen line is lost.
                    EnqueueEventTo(_connectedClients, combatEvent.PlayerId, ref combatEvent);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Combat event dispatch failed for player {combatEvent.PlayerId}: {ex.Message}");
                }
            }
        }

        // Modul: fired by ChatEngine.OnDispatchReady whenever the dispatch
        // worker dequeues an item - published by any pod's PublishMessageAsync/
        // PublishGuildMessageAsync/PublishWhisperMessageAsync, including this
        // one's own, so a player sees their own message arrive back through
        // the exact same path as everyone else's rather than being echoed
        // locally as a special case. Runs entirely off ChatEngine's own
        // background dispatch worker, never on the Redis message pump and
        // never on the 10Hz simulation tick - see ChatEngine's own doc
        // comment on OutboundDispatchQueue for why.
        //
        // Modul: task 41. Every recipient's frame goes on that recipient's
        // outbox; nothing here awaits a socket. Chat - private messages
        // included - used to be dropped whenever the recipient had a state
        // frame in flight, and one stalled recipient held the fan-out for
        // everyone after it. The message is encoded at most once per protocol
        // and the SAME immutable buffer is queued to every recipient, which is
        // safe only because nothing writes to it afterwards (the old shared
        // _chatDispatchBuffer is gone for exactly that reason).
        //
        // Modul: Full-Stack Social Layer, Part 2.2. Block filtering. One
        // query per dispatched message (not per recipient) fetches every
        // PlayerId who has blocked the sender; filtering _connectedClients
        // against that set is then an O(1) HashSet lookup per candidate
        // recipient, executed here on the async dispatch path - never on
        // the 10Hz tick - satisfying the "asynchronous or zero-allocation"
        // constraint by construction.
        private async Task HandleChatDispatchAsync(ChatEngine.ChatDispatchItem item)
        {
            // Every accepted player message passes here once, whisper or not;
            // system announcements carry no sender.
            FolkIdle.Server.Domain.Progression.MissionEvents.Record(item.Packet.SenderPlayerId, FolkIdle.Server.Domain.Progression.MissionAction.ChatMessage);
            System.Collections.Generic.HashSet<long> blockedByRecipients = await GetPlayersWhoBlockedAsync(item.Packet.SenderPlayerId);

            ResponseChatMessagePacket chatPacket = item.Packet;
            var frames = new ChatFrames(chatPacket);

            if (item.DispatchMode == ChatEngine.DispatchModeWhisper)
            {
                if (!_connectedClients.TryGetValue(item.TargetPlayerId, out var targetSession) || blockedByRecipients.Contains(item.TargetPlayerId))
                {
                    return;
                }

                if (targetSession.Socket.State == WebSocketState.Open)
                {
                    try
                    {
                        frames.EnqueueTo(targetSession);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Whisper dispatch failed for player {item.TargetPlayerId}: {ex.Message}");
                    }
                }
                return;
            }

            foreach (var kvp in _connectedClients)
            {
                if (item.DispatchMode == ChatEngine.DispatchModeGuild && kvp.Value.GuildId != item.GuildId)
                {
                    continue;
                }

                if (blockedByRecipients.Contains(kvp.Key))
                {
                    continue;
                }

                if (kvp.Value.Socket.State == WebSocketState.Open)
                {
                    try
                    {
                        frames.EnqueueTo(kvp.Value);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Chat dispatch failed for player {kvp.Key}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// One chat message, encoded lazily and at most once per protocol, for
        /// fan-out to many outboxes. The buffers are shared and never written
        /// after encoding.
        /// </summary>
        private sealed class ChatFrames
        {
            private ResponseChatMessagePacket _packet;
            private byte[]? _json;
            private byte[]? _binary;

            public ChatFrames(ResponseChatMessagePacket packet)
            {
                _packet = packet;
            }

            public void EnqueueTo(WebSocketSession session)
            {
                if (session.UseJsonProtocol)
                {
                    _json ??= PacketJsonCodec.SerializeToUtf8(ref _packet);
                    session.EnqueueEvent(_json, WebSocketMessageType.Text);
                }
                else
                {
                    _binary ??= WebSocketSession.EncodeBinary(ref _packet);
                    session.EnqueueEvent(_binary, WebSocketMessageType.Binary);
                }
            }
        }

        // Modul: escaped double-quoted identifiers per this codebase's
        // Postgres case-sensitivity safeguard. Returns the empty set (never
        // null) so callers can unconditionally call .Contains without a
        // null check.
        private async Task<System.Collections.Generic.HashSet<long>> GetPlayersWhoBlockedAsync(long senderPlayerId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            var blockerIds = await db.PlayerRelationships.AsNoTracking()
                .Where(r => r.TargetPlayerId == senderPlayerId && r.RelationType == Models.RelationType.Blocked)
                .Select(r => r.PlayerId)
                .ToListAsync();

            return new System.Collections.Generic.HashSet<long>(blockerIds);
        }

        // Modul: one persistent pod-wide subscription (not one per
        // connection) to RedisPlayerSessionLock.EvictionChannel - a login on
        // any pod (including this one) publishes "{playerId}:{newToken}"
        // whenever it force-acquires that player's session lock. If this pod
        // is holding a _connectedClients entry for that player whose lock
        // token does not match what was just announced, that connection is
        // the one that just got superseded and is disconnected immediately -
        // this is what makes eviction work across pods, not just within one.
        private void SubscribeToSessionEviction()
        {
            var redis = _serviceProvider.GetService<IConnectionMultiplexer>();
            if (redis == null || !redis.IsConnected)
            {
                return;
            }

            var subscriber = redis.GetSubscriber();
            subscriber.Subscribe(RedisChannel.Literal(RedisPlayerSessionLock.EvictionChannel), HandleSessionEvictionMessage);
        }

        private void HandleSessionEvictionMessage(RedisChannel channel, RedisValue message)
        {
            string payload = message.ToString();
            int separatorIndex = payload.IndexOf(':');
            if (separatorIndex <= 0)
            {
                return;
            }

            if (!long.TryParse(payload.AsSpan(0, separatorIndex), out long playerId))
            {
                return;
            }

            string newToken = payload.Substring(separatorIndex + 1);

            if (_connectedClients.TryGetValue(playerId, out var session) && session.RedisLockToken != newToken)
            {
                Console.WriteLine($"Session eviction: player {playerId} superseded by a new login, disconnecting stale connection.");
                ForceDisconnect(playerId);
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _httpListener.Stop();
        }

        // Modul: THE ACCEPT LOOP ONLY ACCEPTS NOW (task 40, audit item 1).
        // It used to await every handler inline - the whole if-chain below,
        // body reads included - so ONE slow request stalled every login,
        // every WebSocket upgrade and every health probe behind it, and a
        // body trickled a byte at a time at the unauthenticated asset
        // handshake froze the server without a password. Each context now
        // runs on its own task (RouteAsync); what the serial loop used to
        // prevent by accident - a double-tapped sale racing itself - is the
        // per-account striped lock's job (AccountStripes).
        private async Task ListenLoopAsync()
        {
            while (_isRunning)
            {
                HttpListenerContext context;
                try
                {
                    context = await _httpListener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Network error: {ex.Message}");
                    continue;
                }

                _ = Task.Run(() => RouteAsync(context));
            }
        }

        // Modul: per-request state that has to reach a handler without
        // widening 79 signatures: the body-read deadline, and the account
        // stripe this request holds (released by RouteAsync, never by a
        // handler). AsyncLocal flows into every awaited handler call.
        private sealed class RequestScope
        {
            public CancellationToken Aborted;
            public SemaphoreSlim? Stripe;
            public bool BodyRejected;
        }

        private static readonly AsyncLocal<RequestScope?> _currentRequest = new();

        internal static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan AccountStripeWait = TimeSpan.FromSeconds(10);
        internal const int MaxRequestBodyBytes = 64 * 1024;

        // Modul: STRIPED, not a lock per account - 1024 semaphores whatever
        // the population, nothing to evict, nothing to leak. Two accounts that
        // hash to one stripe serialise against each other, which costs a
        // few milliseconds and is otherwise harmless.
        private const int AccountStripeCount = 1024;
        private static readonly SemaphoreSlim[] AccountStripes = CreateAccountStripes();

        private static SemaphoreSlim[] CreateAccountStripes()
        {
            var stripes = new SemaphoreSlim[AccountStripeCount];
            for (int i = 0; i < stripes.Length; i++) stripes[i] = new SemaphoreSlim(1, 1);
            return stripes;
        }

        private async Task RouteAsync(HttpListenerContext context)
        {
            using var deadline = new CancellationTokenSource(RequestDeadline);
            var scope = new RequestScope { Aborted = deadline.Token };
            _currentRequest.Value = scope;
            long routeStarted = DevRouteTiming.Enabled ? Stopwatch.GetTimestamp() : 0;
            try
            {
                await RouteCoreAsync(context);
            }
            catch (HttpListenerException)
            {
            }
            catch (RequestBodyRejectedException)
            {
                // ReadBodyAsync already answered (413) or dropped the socket.
            }
            catch (ObjectDisposedException) when (scope.BodyRejected)
            {
                // A handler's own catch tried to write a 500 onto the response
                // ReadBodyAsync had already closed. Nothing left to say.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Network error: {ex.Message}");
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch
                {
                    // The handler already closed (or the peer left); either way
                    // there is no response left to repair.
                }
            }
            finally
            {
                scope.Stripe?.Release();
                scope.Stripe = null;
                if (routeStarted != 0) DevRouteTiming.Record(context.Request.Url?.AbsolutePath, Stopwatch.GetTimestamp() - routeStarted);
            }
        }

        // Modul: THE PER-ACCOUNT LOCK. Every non-GET request carrying a valid
        // bearer token holds its account's stripe for the whole handler, so a
        // double-tapped sale, two bulk salvages or a Delve action racing
        // itself run one after the other exactly as they did on the old
        // serial loop. ValidateJwt is CPU-only; the handler still does the
        // nonce check, so a revoked-but-well-signed token can at worst wait
        // in line, never act. A GET that MUTATES is not covered - say so at
        // the handler if you write one (CLAUDE.md).
        //
        // Returns false only when the wait timed out and a 429 has been sent.
        private async Task<bool> EnterAccountStripeAsync(HttpListenerContext context)
        {
            string method = context.Request.HttpMethod;
            if (method == "GET" || method == "OPTIONS" || method == "HEAD") return true;

            const string bearerPrefix = "Bearer ";
            string bearerHeader = context.Request.Headers["Authorization"] ?? string.Empty;
            if (bearerHeader.Length <= bearerPrefix.Length || !bearerHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            JwtValidationResult jwt = AuthenticationEngine.ValidateJwt(bearerHeader.Substring(bearerPrefix.Length), _jwtSecretKey);
            if (!jwt.IsValid) return true;

            var stripe = AccountStripes[jwt.AccountId.GetHashCode() & (AccountStripeCount - 1)];
            var scope = _currentRequest.Value;
            if (scope == null || !await stripe.WaitAsync(AccountStripeWait))
            {
                if (scope == null) return true;

                // Modul: a refusal the player can SEE (CLAUDE.md, "silent
                // rollback"): the client reads Reason like every other refusal.
                context.Response.StatusCode = 429;
                context.Response.Headers["Retry-After"] = "1";
                context.Response.ContentType = "application/json";
                byte[] reply = Encoding.UTF8.GetBytes("{\"Success\":false,\"Reason\":\"AccountBusy\"}");
                await context.Response.OutputStream.WriteAsync(reply);
                context.Response.Close();
                return false;
            }

            scope.Stripe = stripe;
            return true;
        }

        private sealed class RequestBodyRejectedException : Exception
        {
            public RequestBodyRejectedException(string message) : base(message) { }
        }

        // Modul: THE ONLY WAY A HANDLER READS A BODY. A cap (64 KB, 413 past
        // it - no endpoint takes more than a few hundred bytes) and the
        // request's 30 s deadline, after which the socket is aborted rather
        // than answered, because a peer trickling a body is not reading
        // replies either. WaitAsync, not a token passed to ReadAsync: the
        // listener's request stream only checks a token before it starts a
        // read, never during one. HttpRouterConcurrencyTests greps this file
        // for any ReadToEndAsync that tries to come back.
        //
        // Takes the context rather than the request so it can answer; takes
        // nothing else from it, to keep a later move off HttpListener small
        // (audit plan D5).
        private static async Task<string> ReadBodyAsync(HttpListenerContext context, int maxBytes = MaxRequestBodyBytes, CancellationToken ct = default)
        {
            var scope = _currentRequest.Value;
            if (!ct.CanBeCanceled && scope != null) ct = scope.Aborted;

            var request = context.Request;
            if (request.ContentLength64 > maxBytes)
            {
                RejectBody(context, scope, abort: false);
            }

            using var buffer = new System.IO.MemoryStream();
            byte[] chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                while (true)
                {
                    int read;
                    try
                    {
                        read = await request.InputStream.ReadAsync(chunk, 0, chunk.Length).WaitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        RejectBody(context, scope, abort: true);
                        throw; // unreachable, RejectBody throws
                    }

                    if (read == 0) break;
                    if (buffer.Length + read > maxBytes)
                    {
                        RejectBody(context, scope, abort: false);
                    }
                    buffer.Write(chunk, 0, read);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
            }

            Encoding encoding = request.ContentEncoding ?? Encoding.UTF8;
            return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        private static void RejectBody(HttpListenerContext context, RequestScope? scope, bool abort)
        {
            if (scope != null) scope.BodyRejected = true;
            try
            {
                if (abort)
                {
                    context.Response.Abort();
                }
                else
                {
                    context.Response.StatusCode = 413;
                    context.Response.Close();
                }
            }
            catch
            {
                // Already gone.
            }
            throw new RequestBodyRejectedException(abort ? "request body deadline passed" : "request body too large");
        }

        // Modul: today's if-chain, moved out of the accept loop unchanged
        // except that `continue` became `return` and the auth handlers that
        // were dispatched fire-and-forget (so as not to serialise the old
        // loop) are awaited - the loop no longer waits on them, and awaiting
        // keeps them inside the account stripe and the outer 500 guard.
        private async Task RouteCoreAsync(HttpListenerContext context)
        {
            string requestPath = context.Request.Url?.AbsolutePath ?? "/";

            // Modul: browser client support, 2026-08-02. Phase 0 of the
            // web client port plan.
            //
            // A browser refuses every cross-origin response that does
            // not carry these headers, so without this the web client
            // cannot make a single successful call - not even login.
            // The Unity client is unaffected: it is not a browser and
            // ignores them.
            //
            // Allow-list, never "*", because these endpoints carry a
            // bearer token. A wildcard would let any page on the
            // internet call this API with a user's credentials once
            // credentials are ever sent.
            ApplyCorsHeaders(context);

            // A browser sends OPTIONS before any request carrying an
            // Authorization header, and expects a bodyless 204. Answered
            // here rather than per-route so a new endpoint cannot forget
            // it - forgetting is invisible until a browser tries.
            if (context.Request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            // Modul: previously both paths unconditionally returned 200
            // regardless of real engine state - InfrastructureHealthMonitor
            // (IsLive/IsReady/WritePlainHealth) already existed with the
            // correct distinct semantics but was never actually called
            // from here, so Kubernetes could never detect a pod still
            // mid cold-boot-recovery or under heap pressure and would
            // route live traffic to it regardless. Liveness only checks
            // GlobalEngineState.IsShuttingDown (restart-worthy failure);
            // readiness additionally requires cold-boot recovery to have
            // completed and heap usage under the readiness limit
            // (service-endpoint-worthy, not restart-worthy - see
            // InfrastructureHealthMonitor.IsReady).
            if (requestPath == "/health/liveness")
            {
                InfrastructureHealthMonitor.WritePlainHealth(context.Response, InfrastructureHealthMonitor.IsLive());
                return;
            }

            if (requestPath == "/health/readiness")
            {
                InfrastructureHealthMonitor.WritePlainHealth(context.Response, InfrastructureHealthMonitor.IsReady());
                return;
            }

            if (requestPath == "/healthz")
            {
                context.Response.StatusCode = 200;
                context.Response.Close();
                return;
            }

            // Modul: Prometheus scrape target. Exempt from the
            // cold-boot-recovery/shutdown gate below, same as the
            // health endpoints above - Prometheus should keep
            // observing a pod's state (including zero active
            // sessions during cold boot) rather than getting 503s
            // that would just show up as scrape failures in its own
            // monitoring instead of real data.
            if (requestPath == "/metrics" && context.Request.HttpMethod == "GET")
            {
                await HandleMetrics(context);
                return;
            }

            if (GlobalEngineState.IsShuttingDown || !GlobalEngineState.IsColdBootRecoveryComplete)
            {
                context.Response.StatusCode = 503;
                context.Response.Close();
                return;
            }

            // Modul: taken here - after the probes and the cold-boot gate,
            // before any handler - and released by RouteAsync's finally.
            if (!await EnterAccountStripeAsync(context))
            {
                return;
            }

            // Modul: browser client support, 2026-08-02. Phase 0, step
            // 3 of the web client port plan. Serves the exact content
            // files the Unity client reads from StreamingAssets, so a
            // browser client mirrors monsters/items/skills/gathering
            // from the same bytes rather than shipping its own copy.
            // Unauthenticated by design - see HandleGameDataFile.
            if (requestPath.StartsWith("/gamedata/", StringComparison.Ordinal) && context.Request.HttpMethod == "GET")
            {
                await HandleGameDataFile(context, requestPath.Substring("/gamedata/".Length));
                return;
            }

            if (requestPath == "/gamedata" && context.Request.HttpMethod == "GET")
            {
                await HandleGameDataManifest(context);
                return;
            }

            // Modul: web client port, Phase 7. The same ten sound
            // effects the Unity client loads from Resources/Audio,
            // linked into this project's output by the csproj rather
            // than copied - see that link's own comment. Unauthenticated
            // for the same reason the content files are: they ship
            // inside the Unity app bundle already.
            // Background music (owner, 2026-09-28): the track list, then a track.
            if (requestPath == "/audio/music" && context.Request.HttpMethod == "GET")
            {
                await HandleMusicManifest(context);
                return;
            }

            if (requestPath.StartsWith("/audio/music/", StringComparison.Ordinal) && context.Request.HttpMethod == "GET")
            {
                await HandleMusicFile(context, requestPath.Substring("/audio/music/".Length));
                return;
            }

            if (requestPath.StartsWith("/audio/", StringComparison.Ordinal) && context.Request.HttpMethod == "GET")
            {
                await HandleAudioFile(context, requestPath.Substring("/audio/".Length));
                return;
            }

            if (requestPath == "/audio" && context.Request.HttpMethod == "GET")
            {
                await HandleAudioManifest(context);
                return;
            }

            if (requestPath.StartsWith("/sprites/", StringComparison.Ordinal) && context.Request.HttpMethod == "GET")
            {
                await HandleSpriteFile(context, requestPath.Substring("/sprites/".Length));
                return;
            }

            if (requestPath == "/sprites" && context.Request.HttpMethod == "GET")
            {
                await HandleSpriteManifest(context);
                return;
            }

            if (requestPath == "/api/v1/assets/handshake" && context.Request.HttpMethod == "POST")
            {
                string expectedHash = Environment.GetEnvironmentVariable("ExpectedCatalogHash") ?? string.Empty;
                string clientHash = string.Empty;

                if (context.Request.HasEntityBody)
                {
                    string payload = await ReadBodyAsync(context);
                    try
                    {
                        var json = System.Text.Json.JsonDocument.Parse(payload);
                        if (json.RootElement.TryGetProperty("catalog.hash", out var hashElement))
                        {
                            clientHash = hashElement.GetString() ?? string.Empty;
                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(expectedHash) && clientHash != expectedHash)
                {
                    context.Response.StatusCode = 426; // Upgrade Required
                    context.Response.Close();
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.Close();
                return;
            }

            // Modul: THE AUTHENTICATION ENDPOINTS HAVE A BUDGET NOW.
            //
            // Eight wrong passwords in a row used to return eight plain
            // 401s with nothing in between. Unlimited guessing against
            // any known email, and - because every attempt runs PBKDF2
            // at 210,000 iterations - a way to spend the box's CPU from
            // a laptop. See AuthThrottle on why the budget counts
            // requests rather than failures, and why it reads
            // X-Forwarded-For rather than the socket's address.
            if (requestPath == "/api/v1/auth/login"
                || requestPath == "/api/v1/auth/register"
                || requestPath == "/api/v1/auth/oauth-link"
                // Modul: the reset endpoints belong in the budget too.
                // The request side sends mail to somebody else's
                // address, so unthrottled it is a way to use this
                // server to spam a stranger; the completion side is a
                // guess against a token.
                || requestPath == "/api/v1/auth/request-password-reset"
                || requestPath == "/api/v1/auth/reset-password"
                || requestPath == "/api/v1/auth/refresh"
                // Modul: Task 10's step-up gate made this endpoint
                // verify a password too (HandleBillingVerify, for a
                // device-bearer session on a password-holding
                // account) - without this it would be an unthrottled
                // oracle for guessing that one account's password at
                // full PBKDF2 cost, with no email enumeration even
                // needed since the bearer token already identifies
                // the account.
                || requestPath == "/api/v1/billing/verify")
            {
                if (!AuthThrottle.TryConsume(AuthThrottle.ResolveClientAddress(context.Request)))
                {
                    context.Response.StatusCode = 429;
                    context.Response.Headers["Retry-After"] = "60";
                    context.Response.Close();
                    return;
                }
            }

            if (requestPath == "/api/v1/auth/login" && context.Request.HttpMethod == "POST")
            {
                // Modul: this and the other auth handlers used to be
                // dispatched fire-and-forget, so a provisioning transaction
                // retrying under Serializable contention (see
                // LoginOrProvisionAsync) would not serialise every other
                // connection behind the old one-at-a-time accept loop.
                // Every request has its own task now (RouteAsync), so they
                // are awaited like everything else - which keeps them
                // inside the account stripe and the router's 500 guard.
                await HandleAuthLogin(context);
                return;
            }

            if (requestPath == "/api/v1/auth/oauth-link" && context.Request.HttpMethod == "POST")
            {
                await HandleOAuthLink(context);
                return;
            }

            if (requestPath == "/api/v1/auth/register" && context.Request.HttpMethod == "POST")
            {
                await HandleAuthRegister(context);
                return;
            }

            // Modul: PASSWORD RESET. Registration used to be the only
            // place this server set a password, so a player who forgot
            // theirs had permanently lost the account - on a live game.
            if (requestPath == "/api/v1/auth/request-password-reset" && context.Request.HttpMethod == "POST")
            {
                await HandleRequestPasswordReset(context);
                return;
            }

            if (requestPath == "/api/v1/auth/reset-password" && context.Request.HttpMethod == "POST")
            {
                await HandleResetPassword(context);
                return;
            }

            // Modul: EXCHANGING A REFRESH TOKEN FOR A JWT, AND WHY IT
            // IS IN THE THROTTLE BUDGET.
            //
            // The body is a 256-bit secret, so guessing it is not a
            // realistic attack - but the route is unauthenticated by
            // construction (its whole job is to run when there is no
            // valid session) and every unauthenticated route on this
            // server that touches the database has to cost something,
            // or it is a way to spend the box from a laptop. The reset
            // endpoints are in this list for the same reason.
            if (requestPath == "/api/v1/auth/refresh" && context.Request.HttpMethod == "POST")
            {
                await HandleAuthRefresh(context);
                return;
            }

            // Signing out. Unauthenticated on purpose: a player whose
            // JWT has already expired must still be able to invalidate
            // the refresh token sitting on the device, and requiring a
            // live session to do it would make that impossible in
            // exactly the case where it matters.
            if (requestPath == "/api/v1/auth/revoke" && context.Request.HttpMethod == "POST")
            {
                await HandleAuthRevoke(context);
                return;
            }

            if (requestPath == "/admin/liveops" && context.Request.HttpMethod == "POST")
            {
                // Modul: NO DEFAULT PASSWORD. This read
                // `?? "supersecretadmin123"`, and this repository is
                // public - so on any deployment that had not set the
                // variable, the admin credential was a string anybody
                // could read on GitHub. It happened to be unreachable
                // from the internet, because ops/oracle/Caddyfile's api
                // matcher does not list /admin/* and the static file
                // server answers it instead. That is an accident of a
                // path list, not a decision, and it would have ended the
                // first time someone added a proxy rule.
                //
                // Unset now means CLOSED. An operator who wants the
                // endpoint sets a key; nobody inherits one.
                string secretKey = context.Request.Headers["X-Admin-Secret-Key"] ?? string.Empty;
                string expectedKey = Environment.GetEnvironmentVariable("ADMIN_SECRET_KEY") ?? string.Empty;

                // Constant-time, like every other secret comparison in
                // this codebase - `!=` on strings returns as soon as two
                // bytes differ, which leaks the prefix a byte at a time.
                bool keyMatches = expectedKey.Length > 0
                    && secretKey.Length == expectedKey.Length
                    && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(secretKey),
                        System.Text.Encoding.UTF8.GetBytes(expectedKey));

                if (!keyMatches)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                if (context.Request.InputStream != null)
                {
                    var buffer = new byte[Marshal.SizeOf<AdminCommandPacket>()];
                    int bytesRead = await context.Request.InputStream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead >= Marshal.SizeOf<AdminCommandPacket>())
                    {
                        ParseAdminCommand(buffer, bytesRead);
                    }
                }

                context.Response.StatusCode = 200;
                context.Response.Close();
                return;
            }

            // Modul: `/api/v1/billing/verify-receipt` USED TO ROUTE HERE TO
            // HandleVerifyReceipt, which trusted a client-supplied
            // AccountId/TransactionId/ProductId out of the request body and
            // credited diamonds with NO signature check at all - the REST
            // wrapper around VerifyPurchaseAsync, the method the doc comment
            // on that engine method calls "the legacy in-session notification
            // path" for the internal WebSocket opcode 39 handler, never meant
            // to be reachable over public HTTP. The web client's own billing.ts
            // had always POSTed to this exact URL believing it was the
            // signature-checking endpoint (its header comment said so), so no
            // real purchase ever verified anything; anyone who knew their own
            // AccountId could grant themselves unlimited free diamonds by
            // hand. Route and handler removed 2026-09-18. The only REST
            // purchase path now is /api/v1/billing/verify below, which
            // resolves the player from the caller's own bearer JWT and checks
            // the store's signature before granting anything.
            if (requestPath == "/api/v1/billing/verify" && context.Request.HttpMethod == "POST")
            {
                await HandleBillingVerify(context);
                return;
            }

            if (requestPath == "/api/v1/billing/refund-webhook" && context.Request.HttpMethod == "POST")
            {
                await HandleRefundWebhook(context);
                return;
            }

            if (requestPath == "/api/v1/storefront/listings" && context.Request.HttpMethod == "GET")
            {
                await HandleStorefrontListings(context);
                return;
            }

            if (requestPath == "/api/v1/chest/sell" && context.Request.HttpMethod == "POST")
            {
                await HandleChestAction(context, sell: true);
                return;
            }

            if (requestPath == "/api/v1/chest/discard" && context.Request.HttpMethod == "POST")
            {
                await HandleChestAction(context, sell: false);
                return;
            }

            // Modul: the chest's drain. One call clears a whole rarity
            // band; the per-item routes above cannot, and seventeen
            // thousand calls to them is not an alternative. See
            // HandleChestBulkAction.
            if (requestPath == "/api/v1/chest/bulk-sell" && context.Request.HttpMethod == "POST")
            {
                await HandleChestBulkAction(context, sell: true);
                return;
            }

            if (requestPath == "/api/v1/chest/bulk-discard" && context.Request.HttpMethod == "POST")
            {
                await HandleChestBulkAction(context, sell: false);
                return;
            }

            // Modul: the lock's write side. See
            // VillageChestEngine.ToggleAffixLockAsync - the flag was
            // read in ten places and set by nothing, so none of that
            // code could ever run.
            if (requestPath == "/api/v1/chest/lock" && context.Request.HttpMethod == "POST")
            {
                await HandleChestToggleLock(context);
                return;
            }

            // Task 69: what "fuse this stack up to tier N" would do, before the
            // player commits to it. Read-only; the fusion itself is opcode 78.
            if (requestPath == "/api/v1/forge/stack-preview" && context.Request.HttpMethod == "GET")
            {
                await HandleForgeStackPreview(context);
                return;
            }

            // Task 78: the hunting advisor - per monster, what this character's
            // kills take, pay and cost. Read-only; see HuntingProjection.
            if (requestPath == "/api/v1/combat/projection" && context.Request.HttpMethod == "GET")
            {
                await HandleCombatProjection(context);
                return;
            }

            // Every stat of one character and the cap it runs into; see
            // CharacterStatSheet. Read-only, from the same live snapshot.
            if (requestPath == "/api/v1/character/stats" && context.Request.HttpMethod == "GET")
            {
                await HandleCharacterStats(context);
                return;
            }

            // The big-inventory prompt's numbers: sweepable pieces by region
            // and rarity, without shipping the rows themselves.
            if (requestPath == "/api/v1/chest/summary" && context.Request.HttpMethod == "GET")
            {
                await HandleChestSummary(context);
                return;
            }

            if (requestPath == "/api/v1/chest/settings")
            {
                await HandleChestSettings(context);
                return;
            }

            // The Delve - a gold sink shaped like a game. REST rather
            // than opcodes on purpose: a run is a handful of requests
            // minutes apart, nothing in the 10 Hz tick reads any of it,
            // and the state packet is already near its 800-byte layout
            // guard. See DelveEngine.
            if (requestPath == "/api/v1/delve" && context.Request.HttpMethod == "GET")
            {
                await HandleDelveView(context);
                return;
            }

            if (requestPath.StartsWith("/api/v1/delve/") && context.Request.HttpMethod == "POST")
            {
                await HandleDelveAction(context, requestPath);
                return;
            }

            // The world boss shield wheel (task 36). REST like the
            // Delve: a strike is a handful of requests and nothing in
            // the tick reads them. See WorldBossStrikeService.
            if (requestPath == "/api/v1/worldboss/challenge"
                || requestPath == "/api/v1/worldboss/throw"
                || requestPath == "/api/v1/worldboss/strike"
                || requestPath == "/api/v1/worldboss/practice/score"
                || requestPath == "/api/v1/worldboss/duel"
                || requestPath == "/api/v1/worldboss/duel/call")
            {
                await HandleWorldBossStrikeRoute(context, requestPath);
                return;
            }

            if (requestPath == "/api/v1/guild/shard-match" && context.Request.HttpMethod == "GET")
            {
                await HandleGuildShardMatch(context);
                return;
            }

            // Modul: the Guild War population lock's progress, for the
            // locked line on the Guild screen and for the text of the
            // GuildWarsLocked command result. REST, not a packet field:
            // it changes on the scale of days, and StateUpdatePacket
            // has about a byte of headroom. See GuildWarUnlock.
            if (requestPath == "/api/v1/guild/war-unlock" && context.Request.HttpMethod == "GET")
            {
                await HandleGuildWarUnlock(context);
                return;
            }

            if (requestPath == "/api/v1/guild/logistics/snapshot" && context.Request.HttpMethod == "GET")
            {
                await HandleGuildLogisticsSnapshot(context);
                return;
            }

            if (requestPath == "/api/v1/forge/inventory" && context.Request.HttpMethod == "GET")
            {
                await HandleForgeInventorySnapshot(context);
                return;
            }

            if (requestPath == "/api/v1/codex/snapshot" && context.Request.HttpMethod == "GET")
            {
                await HandleCodexSnapshot(context);
                return;
            }

            if (requestPath == "/api/v1/codex/regions" && context.Request.HttpMethod == "GET")
            {
                await HandleCodexRegionsSnapshot(context);
                return;
            }

            if (requestPath == "/api/v1/player/loot-odds" && context.Request.HttpMethod == "GET")
            {
                await HandleLootOdds(context);
                return;
            }

            if (requestPath == "/api/v1/breeding/roster" && context.Request.HttpMethod == "GET")
            {
                await HandleBreedingRosterSnapshot(context);
                return;
            }

            // Modul: the trait catalogue. Served, never copied: the client
            // renders what this says, so a new trait is a server change only.
            if (requestPath == "/api/v1/breeding/traits" && context.Request.HttpMethod == "GET")
            {
                await HandleBreedingTraits(context);
                return;
            }

            // Modul: the Book of Deeds. Five chapters, their live
            // counters, and the Seals - which are BANKED on this read,
            // because a Seal grants permanent skill points and a client
            // that decided when it had earned one could award itself
            // the whole tree.
            if (requestPath == "/api/v1/deeds/snapshot" && context.Request.HttpMethod == "GET")
            {
                await HandleDeedsSnapshot(context);
                return;
            }

            // Task 57: the collection log. A GET that WRITES, and says so: it
            // folds the Chest into player_collection first. Safe without the
            // account stripe because the only write is an upsert that can only
            // raise a tier (CollectionLog.RecordAsync) - two at once agree.
            if (requestPath == "/api/v1/player/collection" && context.Request.HttpMethod == "GET")
            {
                await HandleCollection(context);
                return;
            }

            // Task 56: rates, style and timeline, from StatSampler's samples.
            // Read-only.
            if (requestPath == "/api/v1/player/insights" && context.Request.HttpMethod == "GET")
            {
                await HandleInsights(context);
                return;
            }

            // Modul: the Hall of Ancestors. The breeding roster answers
            // "who can I pair"; this answers "who carries into next
            // season, and where do they stand" - the cap, the marks,
            // the pedigree and which of the three playable slots each
            // member occupies.
            if (requestPath == "/api/v1/ancestors/hall" && context.Request.HttpMethod == "GET")
            {
                await HandleAncestorsHall(context);
                return;
            }

            // Task 88: rebirth on demand. The preview is read-only; the POST
            // is a mutating request and so holds the account stripe for its
            // whole handler (EnterAccountStripeAsync), which is what makes a
            // double-tap wait for the first rather than race it.
            if (requestPath == "/api/v1/rebirth/preview" && context.Request.HttpMethod == "GET")
            {
                await HandleRebirthPreview(context);
                return;
            }

            if (requestPath == "/api/v1/rebirth" && context.Request.HttpMethod == "POST")
            {
                await HandleRebirth(context);
                return;
            }

            if (requestPath == "/api/v1/breeding/preview" && context.Request.HttpMethod == "GET")
            {
                await HandleBreedingPreview(context);
                return;
            }

            // Modul: "who should my hero marry?" (2026-11-13) - the best
            // partners for one hero, ranked by BreedingMatchmaker.
            if (requestPath == "/api/v1/breeding/suggest" && context.Request.HttpMethod == "GET")
            {
                await HandleBreedingSuggest(context);
                return;
            }

            // Modul: the same question asked of THE standard pair - a
            // hero and somebody from the village. Separate because the
            // partner is a village_newcomers row, not a character.
            if (requestPath == "/api/v1/breeding/village-preview" && context.Request.HttpMethod == "GET")
            {
                await HandleVillagerBreedingPreview(context);
                return;
            }

            // Modul: the village gene pool. The roster above is the
            // player's OWN characters; this is the outside blood they
            // can marry into the line, which is a different list
            // answering a different question.
            if (requestPath == "/api/v1/village/newcomers" && context.Request.HttpMethod == "GET")
            {
                await HandleVillageNewcomers(context);
                return;
            }

            // Modul: what the next level of every building costs, and how much
            // of each line the player holds. The Village screen used to price
            // upgrades from its own copy of the tier table, and the copy had
            // drifted from what the handler charges - see
            // VillageManagementEngine.QuoteUpgrade. Read-only GET, no lock.
            if (requestPath == "/api/v1/village/quote" && context.Request.HttpMethod == "GET")
            {
                await HandleVillageQuote(context);
                return;
            }

            if (requestPath == "/api/v1/mastery/snapshot" && context.Request.HttpMethod == "GET")
            {
                await HandleMasterySnapshot(context);
                return;
            }

            // Modul: UI audit follow-up. Friends roster - AddFriend/
            // RemoveFriend/BlockPlayer/UnblockPlayer (RelationshipEngine)
            // already existed and worked over the WebSocket wire, but
            // there was no way for the client to list the current
            // relationship set or discover a target player's numeric
            // Id from their username. Mirrors HandleMasterySnapshot's
            // exact authenticated-GET shape.
            // Modul: conversations are read over REST, deliberately not
            // over the wire. Every packet is demultiplexed by exact
            // byte size and the state packet has about a byte of
            // headroom, so putting history on it would cost a layout
            // guard change and a protocol regeneration for something
            // that is a paged list - which is what HTTP is for, and
            // what the friends list and mailbox beside it already do.
            // Task 110e: world, guild and announcement history at sign-in.
            // REST for the same reason as conversations just below.
            if (requestPath == "/api/v1/chat/recent" && context.Request.HttpMethod == "GET")
            {
                await HandleChatRecent(context);
                return;
            }

            if (requestPath == "/api/v1/conversations/list" && context.Request.HttpMethod == "GET")
            {
                await HandleConversationList(context);
                return;
            }

            if (requestPath == "/api/v1/conversations/history" && context.Request.HttpMethod == "GET")
            {
                await HandleConversationHistory(context);
                return;
            }

            if (requestPath == "/api/v1/conversations/read" && context.Request.HttpMethod == "POST")
            {
                await HandleConversationMarkRead(context);
                return;
            }

            if (requestPath == "/api/v1/friends/list" && context.Request.HttpMethod == "GET")
            {
                await HandleFriendsList(context);
                return;
            }

            if (requestPath == "/api/v1/players/resolve" && context.Request.HttpMethod == "GET")
            {
                await HandlePlayerResolve(context);
                return;
            }

            if (requestPath == "/api/v1/players/profile" && context.Request.HttpMethod == "GET")
            {
                await HandlePlayerProfile(context);
                return;
            }

            if (requestPath == "/api/v1/guilds/view" && context.Request.HttpMethod == "GET")
            {
                await HandleGuildView(context);
                return;
            }

            if (requestPath == "/api/v1/stats/online" && context.Request.HttpMethod == "GET")
            {
                await HandleStatsOnline(context);
                return;
            }

            if (requestPath == "/api/v1/guilds/kick" && context.Request.HttpMethod == "POST")
            {
                await HandleGuildKick(context);
                return;
            }

            if (requestPath == "/api/v1/guilds/promote" && context.Request.HttpMethod == "POST")
            {
                await HandleGuildPromote(context);
                return;
            }

            if (requestPath == "/api/v1/guilds/demote" && context.Request.HttpMethod == "POST")
            {
                await HandleGuildDemote(context);
                return;
            }

            if (requestPath == "/api/v1/guilds/transfer" && context.Request.HttpMethod == "POST")
            {
                await HandleGuildTransfer(context);
                return;
            }

            // Modul: UI rework. Reverse lookup of the above -
            // "?ids=1,2,3" to usernames, so chat/whisper rows can
            // show a name instead of the raw SenderPlayerId the
            // wire protocol carries. Batched deliberately; see
            // PlayerNameEntryResponse's own comment.
            if (requestPath == "/api/v1/players/names" && context.Request.HttpMethod == "GET")
            {
                await HandlePlayerNames(context);
                return;
            }

            // Modul: Inventory screen. The only inventory-shaped
            // endpoint that existed was HandleForgeInventorySnapshot,
            // which is scoped to what the Forge needs (equipment
            // instances plus the handful of materials the Forge's own
            // recipes consume). Nothing anywhere exposed the village
            // stash, the full commodity list, or which items are
            // currently equipped, so no inventory screen was possible.
            // Modul: the stackable half only, for the screens that read
            // nothing else. See HandlePlayerMaterialsSnapshot - the
            // route below serves 3.2 MB on a long-played account and
            // three screens were fetching all of it to count fish.
            if (requestPath == "/api/v1/player/materials" && context.Request.HttpMethod == "GET")
            {
                await HandlePlayerMaterialsSnapshot(context);
                return;
            }

            // Modul: task 49. What the main character wears, and nothing else -
            // the loot list compares a fresh drop against the piece it would
            // replace, and the inventory route below is 3.2 MB on a long-played
            // account. Eleven rows at most.
            if (requestPath == "/api/v1/player/worn" && context.Request.HttpMethod == "GET")
            {
                await HandlePlayerWornSnapshot(context);
                return;
            }

            // Modul: task 51. The personal records a screen lists. The hit and
            // the boss times also ride StateUpdate (live, for the toast); this is
            // the durable copy plus the two the stream does not carry.
            if (requestPath == "/api/v1/player/records" && context.Request.HttpMethod == "GET")
            {
                await HandlePlayerRecords(context);
                return;
            }

            if (requestPath == "/api/v1/player/inventory" && context.Request.HttpMethod == "GET")
            {
                await HandlePlayerInventorySnapshot(context);
                return;
            }

            // Modul: Crafting Tree screen. ContentRegistry's 103
            // recipes have been fully functional server-side for a
            // long time but had no endpoint of any kind - the client
            // could not even enumerate them, let alone show costs.
            if (requestPath == "/api/v1/crafting/recipes" && context.Request.HttpMethod == "GET")
            {
                await HandleCraftingRecipeSnapshot(context);
                return;
            }

            // The login week - see NetworkBroadcastSystem.LoginWeek.cs.
            if (await TryHandleLoginWeekAsync(context, requestPath))
            {
                return;
            }

            // Daily missions - see NetworkBroadcastSystem.Missions.cs.
            if (await TryHandleMissionsAsync(context, requestPath))
            {
                return;
            }

            // The Chronicle pass - see NetworkBroadcastSystem.Chronicle.cs.
            if (await TryHandleChronicleAsync(context, requestPath))
            {
                return;
            }

            // Modul: UI audit follow-up. No player-statistics engine
            // existed anywhere server-side. Rather than invent new
            // tracking, this aggregates fields that are already
            // persisted for other systems (level/xp/diamonds on
            // PlayerRecord, gold via CommodityRecords, claimed
            // achievements, region completions, character count,
            // guild membership) into one read-only snapshot.
            // Task 79: where this player's gold went, by category.
            if (requestPath == "/api/v1/player/gold-ledger" && context.Request.HttpMethod == "GET")
            {
                await HandleGoldLedger(context);
                return;
            }

            if (requestPath == "/api/v1/player/statistics" && context.Request.HttpMethod == "GET")
            {
                await HandlePlayerStatistics(context);
                return;
            }

            // Modul: UI audit follow-up. GuildManagementEngine.
            // CreateGuildAsync/JoinGuildAsync already existed
            // (server/FolkIdle.Server/Domain/Social/GuildManagementEngine.cs)
            // but had no HTTP route or CommandType exposing them -
            // UiGuildCreatePanel's buttons were wired client-side to
            // a clearly-labeled no-op rather than guessing at an
            // unofficial packet shape. POST (not a WS CommandType)
            // because a guild name is a variable-length string,
            // which ClientCommandPacket's fixed-size binary layout
            // has no field for - matches how Email/Password auth
            // (also string-carrying) already uses HTTP, not the WS
            // command loop. Called directly rather than routed
            // through SimulationEngine's tick thread, matching
            // GuildManagementEngine's own header comment that it
            // "never touches SimulationEngine state directly."
            if (requestPath == "/api/v1/monsters/loot" && context.Request.HttpMethod == "GET")
            {
                await HandleMonsterLoot(context);
                return;
            }

            if (requestPath == "/api/v1/guilds/list" && context.Request.HttpMethod == "GET")
            {
                await HandleGuildList(context);
                return;
            }

            if (requestPath.StartsWith("/api/v1/admin/"))
            {
                await HandleAdminEndpoints(context, requestPath);
                return;
            }

            // Lockstep control socket (docs/design/LOCKSTEP_SIM.md): before the
            // dev REST gate, because it signs in as no player - it carries the
            // whole bot process. 404 unless dev tools AND lockstep are on.
            if (requestPath == "/api/v1/dev/sim")
            {
                await HandleSimControlSocketAsync(context);
                return;
            }

            // Modul: dev-only tools. 404 unless FOLKIDLE_DEV_TOOLS=1,
            // so production answers exactly as if the route did not
            // exist. See HandleDevEndpoints.
            if (requestPath.StartsWith("/api/v1/dev/"))
            {
                await HandleDevEndpoints(context, requestPath);
                return;
            }

            if (requestPath == "/api/v1/guilds/create" && context.Request.HttpMethod == "POST")
            {
                await HandleGuildCreate(context);
                return;
            }

            // "Join" here means self-service join-by-name against
            // JoinGuildAsync(playerId, guildId) - the only guild-
            // joining capability that actually exists server-side.
            // There is no player-to-player invite/notification
            // mechanism anywhere in this codebase (no pending-invite
            // table, no accept/decline flow) - building one would be
            // a materially larger, separate feature, not a wiring
            // gap. The name->id resolution happens inline in the
            // same request rather than as a separate GET+POST round
            // trip (unlike Friends' username resolve), since there
            // is no existing "browse guilds" UI that would want the
            // id on its own.
            if (requestPath == "/api/v1/guilds/join" && context.Request.HttpMethod == "POST")
            {
                await HandleGuildJoin(context);
                return;
            }

            // Modul: task 94. LeaveGuildAsync existed, succession and all, and
            // nothing called it - no route, no command, no button - so a player
            // in a dead guild was stuck for good, and every Join and Create
            // stayed disabled for them. A POST like create/join (the mutating
            // path holds the account stripe lock); the GET preview is a pure
            // read so the confirm can name the next leader BEFORE the tap.
            if (requestPath == "/api/v1/guilds/leave" && context.Request.HttpMethod == "POST")
            {
                await HandleGuildLeave(context);
                return;
            }

            if (requestPath == "/api/v1/guilds/leave-preview" && context.Request.HttpMethod == "GET")
            {
                await HandleGuildLeavePreview(context);
                return;
            }

            // Modul: Play Mode audit fix. JoinGuildAsync has always
            // filed a GuildApplication row for Application-Required
            // guilds, but nothing anywhere ever reviewed one - see
            // GuildManagementEngine.ListPendingApplicationsAsync/
            // ApproveApplicationAsync/RejectApplicationAsync's own
            // comment. GET is leader-only (returns an empty list
            // for anyone else, matching HandleGuildRoster's
            // no-guild convention rather than a 403).
            if (requestPath == "/api/v1/guild/applications/pending" && context.Request.HttpMethod == "GET")
            {
                await HandleGuildApplicationsPending(context);
                return;
            }

            if (requestPath == "/api/v1/guild/applications/approve" && context.Request.HttpMethod == "POST")
            {
                await HandleGuildApplicationApprove(context);
                return;
            }

            if (requestPath == "/api/v1/guild/applications/reject" && context.Request.HttpMethod == "POST")
            {
                await HandleGuildApplicationReject(context);
                return;
            }

            if (requestPath == "/api/v1/guilds/depot" && context.Request.HttpMethod == "GET")
            {
                await HandleGuildDepot(context);
                return;
            }

            if (requestPath == "/api/v1/guilds/depot/donate" && context.Request.HttpMethod == "POST")
            {
                await HandleGuildDepotDonate(context);
                return;
            }

            if (requestPath == "/api/v1/guilds/buffs/activate" && context.Request.HttpMethod == "POST")
            {
                await HandleGuildBuffsActivate(context);
                return;
            }

            if (requestPath == "/api/v1/achievements/snapshot" && context.Request.HttpMethod == "GET")
            {
                await HandleAchievementsSnapshot(context);
                return;
            }

            // Modul: Phase - Full-Stack Production Polish, Part 1.2.
            // MailboxAndBankEngine's Claim/Deposit/Withdraw commands
            // already existed on the WebSocket wire protocol
            // (ClaimMailItem/DepositToBank/WithdrawFromBank) - what
            // was missing was any way for the client to discover
            // WHICH ids exist to act on. Paginated-list snapshot
            // endpoints, mirroring HandleForgeInventorySnapshot's
            // exact shape (an authenticated, read-only, per-player
            // list query) rather than StateUpdatePacket's fixed
            // binary layout, for the same reason every other
            // variable-length listing in this file uses HTTP.
            // Modul: Production Release Hardening, Part 2. Both
            // routes below carry fields removed from
            // StateUpdatePacket to shrink the 10Hz hot-path packet
            // (see that struct's own trailing doc comment) -
            // low-frequency/static metadata that does not need a
            // ~10-times-per-second broadcast. Mirrors every other
            // REST-snapshot handler's exact shape.
            if (requestPath == "/api/v1/player/metadata" && context.Request.HttpMethod == "GET")
            {
                await HandlePlayerMetadata(context);
                return;
            }

            // Modul: the player's own answer to "may we email you".
            // Opt-in: PlayerRecord.EmailNotificationsConsented starts
            // false for every account and only this route sets it.
            if (requestPath == "/api/v1/player/email-consent")
            {
                await HandleEmailConsent(context);
                return;
            }

            // Modul: THE DEVICE TOKEN GOES OVER REST, NOT OVER
            // OPCODE 33.
            //
            // `ClientCommandPacket.DeviceTokenBytes` is a fixed
            // `byte[64]`. An FCM registration token is around 160
            // characters, so Android push could never have travelled
            // that path and iOS push fitted it with nothing to spare.
            // The purchase receipt hit the same wall and took the same
            // answer, for the same reason - see HandleBillingVerify.
            //
            // Awaited rather than dispatched: this one writes a single
            // small row and the client is standing in Settings waiting
            // to be told whether its device is registered.
            if (requestPath == "/api/v1/player/push-token" && context.Request.HttpMethod == "POST")
            {
                await HandlePushTokenRegistration(context);
                return;
            }

            // Modul: WHICH WEB BUNDLE A PHONE SHOULD BE RUNNING.
            //
            // The live-update plugin POSTs here on a cold start with
            // the bundle it currently has, and this answers with the
            // one it should have. UNAUTHENTICATED on purpose: it is
            // asked before anybody signs in - that is the whole point,
            // the player opens the app and the update is already
            // arriving - and it reveals nothing but a version number
            // and a URL that serves a public static file anyway.
            if (requestPath == "/api/v1/app/bundle" && context.Request.HttpMethod == "POST")
            {
                await HandleLiveBundleManifest(context);
                return;
            }

            // Modul: which explanations this player has already read.
            // Was localStorage only, which taught a returning player
            // the whole game again on a second device - see
            // PlayerRecord.OnboardingSeenIds for why that trade stopped
            // being worth it.
            if (requestPath == "/api/v1/player/onboarding-seen")
            {
                await HandleOnboardingSeen(context);
                return;
            }

            if (requestPath == "/api/v1/achievements/state" && context.Request.HttpMethod == "GET")
            {
                await HandleAchievementsState(context);
                return;
            }

            if (requestPath == "/api/v1/mailbox/list" && context.Request.HttpMethod == "GET")
            {
                await HandleMailboxListSnapshot(context);
                return;
            }

            // Modul: Phase - Full-Stack Production Polish Phase 2,
            // Part 3.1. Exposes ContentRegistry.Balance.
            // IapProductPrices (loaded from GameBalanceConfig.json)
            // to the client's Store window - previously only read
            // server-side (BillingVerificationEngine.
            // ResolvePremiumDiamondsForProduct), with no way for a
            // client to discover which packages exist or what they
            // cost without hardcoding a second, driftable copy.
            if (requestPath == "/api/v1/store/catalog" && context.Request.HttpMethod == "GET")
            {
                await HandleStoreCatalog(context);
                return;
            }

            // Modul: Phase - Full-Stack Production Polish Phase 2,
            // Part 3.1 (UiGuildRosterPanel). No prior endpoint
            // exposed a guild's member list at all - guild UI so
            // far (logistics/raid/war panels) only ever showed
            // aggregate guild-wide numbers, never individual
            // members or their Role.
            if (requestPath == "/api/v1/guild/roster" && context.Request.HttpMethod == "GET")
            {
                await HandleGuildRoster(context);
                return;
            }

            if (requestPath == "/api/v1/leaderboard/global" && context.Request.HttpMethod == "GET")
            {
                await HandleGlobalLeaderboard(context);
                return;
            }

            if (requestPath == "/api/v1/leaderboard/guilds" && context.Request.HttpMethod == "GET")
            {
                await HandleGuildLeaderboard(context);
                return;
            }

            // The world boss damage board (owner, 2026-09-26): who dealt
            // what this encounter, and the server's total. Read-only SQL;
            // the payout ranks from the same rows (WorldBossBoard).
            if (requestPath == "/api/v1/worldboss/board" && context.Request.HttpMethod == "GET")
            {
                await HandleWorldBossBoard(context);
                return;
            }

            // The Deep's weekly board (task 37). Read-only SQL; pays nothing.
            if (requestPath == "/api/v1/leaderboard/deepest" && context.Request.HttpMethod == "GET")
            {
                await HandleDeepestLeaderboard(context);
                return;
            }

            // "Your road" (2026-11-13): the endgame tracks side by side.
            if (requestPath == "/api/v1/player/journey" && context.Request.HttpMethod == "GET")
            {
                await HandleJourney(context);
                return;
            }

            // The Bloodline week board (2026-11-13). Read-only SQL; pays nothing.
            if (requestPath == "/api/v1/leaderboard/bloodline" && context.Request.HttpMethod == "GET")
            {
                await HandleBloodlineWeekBoard(context);
                return;
            }

            // The Fastest lives board (2026-11-13). Read-only SQL; pays nothing.
            if (requestPath == "/api/v1/leaderboard/lives" && context.Request.HttpMethod == "GET")
            {
                await HandleLifeRecordBoard(context);
                return;
            }

            // Cosmetics (task 54), and their market - see NetworkBroadcastSystem.Cosmetics.cs.
            if (await TryHandleCosmeticsAsync(context, requestPath))
            {
                return;
            }

            // Task 85: the automation rules - see NetworkBroadcastSystem.AutomationRules.cs.
            if (await TryHandleAutomationRulesAsync(context, requestPath))
            {
                return;
            }

            // Workshop commissions (task 83) - see NetworkBroadcastSystem.Workshop.cs.
            if (await TryHandleWorkshopAsync(context, requestPath))
            {
                return;
            }

            // Loot chests - see NetworkBroadcastSystem.LootChests.cs.
            if (await TryHandleDungeonAsync(context, requestPath))
            {
                return;
            }

            if (await TryHandleLootChestsAsync(context, requestPath))
            {
                return;
            }

            // Expeditions - see NetworkBroadcastSystem.Expeditions.cs.
            if (await TryHandleExpeditionsAsync(context, requestPath))
            {
                return;
            }

            // The Descent into Nav - see NetworkBroadcastSystem.Nav.cs (404 unless FOLKIDLE_NAV=on).
            if (await TryHandleNavAsync(context, requestPath))
            {
                return;
            }

            // The skill tree screen's definition, draft preview and commit -
            // see NetworkBroadcastSystem.SkillTree.cs.
            if (await TryHandleSkillTreeAsync(context, requestPath))
            {
                return;
            }

            // The quest line (owner, 2026-10-07) - see NetworkBroadcastSystem.QuestLine.cs.
            if (await TryHandleQuestLineAsync(context, requestPath))
            {
                return;
            }

            // Titles (task 37): REST, not the wire - no StateUpdatePacket field.
            if (requestPath == "/api/v1/player/titles" && context.Request.HttpMethod == "GET")
            {
                await HandleTitlesView(context);
                return;
            }

            if (requestPath == "/api/v1/player/title" && context.Request.HttpMethod == "POST")
            {
                await HandleTitleSet(context);
                return;
            }

            if (requestPath == "/api/v1/market/listings" && context.Request.HttpMethod == "GET")
            {
                await HandleMarketBrowserListings(context);
                return;
            }

            if (requestPath == "/api/v1/market/history" && context.Request.HttpMethod == "GET")
            {
                await HandleMarketPriceHistory(context);
                return;
            }

            if (requestPath == "/api/v1/market/mine" && context.Request.HttpMethod == "GET")
            {
                await HandleMarketOwnOrders(context);
                return;
            }

            // A POST, so the router's per-account stripe lock already holds
            // for the whole handler - a double-tapped Cancel runs one at a time.
            if (requestPath == "/api/v1/market/cancel" && context.Request.HttpMethod == "POST")
            {
                await HandleMarketCancelOrder(context);
                return;
            }

            if (requestPath == "/api/v1/support/tickets/create" && context.Request.HttpMethod == "POST")
            {
                await HandleSupportTicket(context);
                return;
            }

            if (context.Request.IsWebSocketRequest)
            {
                var webSocketContext = await context.AcceptWebSocketAsync(null);
                _ = HandleClientLoopAsync(webSocketContext.WebSocket);
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
        }

        public struct PlayerCommand
        {
            public long PlayerId;
            public ClientCommandPacket Packet;
        }

        public ConcurrentQueue<PlayerCommand> CommandQueue { get; } = new();

        private sealed class StorefrontListingResponse
        {
            public int ListingId { get; set; }
            public string ProductIdentifier { get; set; } = string.Empty;
            public int DiamondPackageYield { get; set; }
            public int PriceInCents { get; set; }
        }

        private sealed class GuildLogisticsSnapshotResponse
        {
            public int MaterialId { get; set; }
            public long CurrentStock { get; set; }
            public long TargetRequirement { get; set; }
        }

        private sealed class ForgeEquipmentInstanceResponse
        {
            public long Id { get; set; }
            public string BaseItemId { get; set; } = string.Empty;
            public int QualityTier { get; set; }
            public bool IsAffixLocked { get; set; }
            /// <summary>
            /// Worn by ANY of the player's characters, all eleven slots. Task
            /// 100: the Forge picks fusion pieces itself now, and the wire only
            /// names the active character's gear - a piece worn by character 2
            /// was offered as a sacrifice, refused (ItemEquipped), and offered
            /// again on the next tap.
            /// </summary>
            public bool IsEquipped { get; set; }
            /// <summary>
            /// Which character slot (0-2) wears it, or -1. Owner, 2026-11-03:
            /// the reroll picker showed only the active character's gear, so a
            /// player with three characters had to dig through everything they
            /// own to reroll the second one's ring. The Forge groups worn gear
            /// by character with this.
            /// </summary>
            public int EquippedByCharacterSlot { get; set; } = -1;
            /// <summary>Which of the eleven equipment slots it is worn in, or -1 - orders a character's column like its paper doll.</summary>
            public int EquippedInSlotIndex { get; set; } = -1;
            /// <summary>What one reroll / one Refine of this piece costs - AffixRerollEngine.GoldCostFor, the engine's own price (2026-11-03).</summary>
            public long RerollGoldCost { get; set; }
            public long RefineGoldCost { get; set; }
            public System.Collections.Generic.Dictionary<string, int> Affixes { get; set; } = new();
        }

        private sealed class ForgeRecipeResponse
        {
            public int RecipeId { get; set; }
            public string ResultBaseItemId { get; set; } = string.Empty;
            public int TierIndex { get; set; }
            public string MaterialName { get; set; } = string.Empty;
            public int MaterialCost { get; set; }
            public long CurrentMaterialStock { get; set; }
        }

        private sealed class ForgeInventorySnapshotResponse
        {
            public System.Collections.Generic.List<ForgeEquipmentInstanceResponse> OwnedEquipment { get; set; } = new();
            public System.Collections.Generic.List<ForgeRecipeResponse> Recipes { get; set; } = new();
        }

        private sealed class CodexSnapshotEntryResponse
        {
            public int MonsterId { get; set; }
            public int Level { get; set; }
            public long Kills { get; set; }
            public long NextLevelKills { get; set; }
        }

        private sealed class AchievementSnapshotEntryResponse
        {
            public int AchievementId { get; set; }
            public string Title { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public long CurrentProgress { get; set; }
            public int CompletedTier { get; set; }
            public long NextTierTarget { get; set; }
            public int NextTierReward { get; set; }
            public bool IsClaimed { get; set; }
        }

        private sealed class RaceMasterySnapshotEntryResponse
        {
            public int RaceId { get; set; }
            public int Level { get; set; }
            public long Experience { get; set; }
            public long NextLevelExperience { get; set; }
        }

        private sealed class FriendEntryResponse
        {
            public long PlayerId { get; set; }
            public string Username { get; set; } = string.Empty;
            public int Level { get; set; }
            public bool IsBlocked { get; set; }

            // Modul: friend online status, 2026-08-02. The friend list carried
            // no online state at all, so it could not answer the one question a
            // friend list exists to answer - who is around right now. The
            // capability was already there: PlayerSessionRegistry.IsPlayerOnline
            // is what the market escrow uses to decide between crediting a live
            // payload and writing to the database.
            public bool IsOnline { get; set; }
        }

        private sealed class PlayerResolveResponse
        {
            public long PlayerId { get; set; }
        }

        // Modul: UI rework. The reverse of PlayerResolveResponse - chat,
        // guild rosters and whisper threads all carry a raw numeric
        // SenderPlayerId over the wire (ResponseChatMessagePacket has no
        // room for a name), so every social surface in the client was
        // rendering "Player #1042" instead of a username. Batched by
        // construction: a chat log resolves one request for every id it is
        // currently displaying, not one request per row.
        private sealed class PlayerNameEntryResponse
        {
            public long PlayerId { get; set; }
            public string Username { get; set; } = string.Empty;
        }

        // Modul: Inventory screen.
        private sealed class InventoryEquipmentResponse
        {
            public long Id { get; set; }
            public string BaseItemId { get; set; } = string.Empty;
            public int QualityTier { get; set; }
            public bool IsEquipped { get; set; }

            // Modul: roster loadouts. Which character slot (0-2) wears this,
            // or -1 if it is carried. IsEquipped stays because the Inventory
            // screen only cares whether an item is available at all.
            public int EquippedByCharacterSlot { get; set; } = -1;

            // Modul: WHICH equipment slot it is worn in, 0-6, or -1 if it is
            // merely carried. The paper doll used to re-derive this from the
            // BaseItemId with the client's port of ResolveSlotIndex - which
            // disagreed with the character row for four of the seven pieces,
            // so a fully equipped character showed three filled slots and four
            // empty ones. The row already knows; it just was not being asked.
            public int EquippedInSlotIndex { get; set; } = -1;

            // The Vault lock: the region this life must reach before the piece
            // can be worn, 0 when none (EquipmentInstance.VaultLockRegion).
            public int VaultLockRegion { get; set; }

            // Modul: Affix System Unification. Without these the Inventory
            // screen could name an item and its rarity but say nothing about
            // what it actually does, which is the entire point of a rarity
            // system. Keyed by GDD affix id (AffixRegistry), magnitudes in
            // whole points for flat affixes and tenths of a percent for
            // percentage ones.
            public Dictionary<string, int> Affixes { get; set; } = new();

            public bool IsAffixLocked { get; set; }

            // Modul: WHAT THE CHEST PAYS FOR IT, from the same function
            // RemoveEquipmentAsync pays with. The Sell button used to name no
            // price, and computing BaseValueGold x tier x 0.40 in the client
            // would have been a second copy of the vendor rule (task 99).
            public long SellValueGold => Engine.VillageChestEngine.ValueEquipment(BaseItemId, QualityTier);
        }

        private sealed class InventoryStackResponse
        {
            public string ItemId { get; set; } = string.Empty;
            /// <summary>
            /// How many the player has, full stop.
            ///
            /// Modul: ONE NUMBER. This used to be BackpackQuantity and
            /// StashQuantity, mirroring two tables the server had already
            /// stopped distinguishing - every spend goes through
            /// TryConsumeUnifiedAsync, which draws from both and refuses only
            /// when the SUM is short. Exposing the split made three screens
            /// filter on one half and hide stock the server would have taken:
            /// the larder, the boosts and the guild deposit, each found
            /// separately, each the same bug.
            ///
            /// The old fields are gone rather than deprecated. A field that
            /// still exists is a field someone will read.
            /// </summary>
            public long Quantity { get; set; }

            // Modul: what ONE of these sells for, by the function
            // RemoveMaterialAsync pays with. 0 means the chest pays nothing
            // for it - gold (refused outright) and the gathering slugs with no
            // items.json entry - and the screen must say so rather than guess.
            public long UnitSellValueGold =>
                string.Equals(ItemId, Domain.Progression.VillageManagementEngine.GoldItemId, StringComparison.OrdinalIgnoreCase)
                    ? 0L
                    : Engine.VillageChestEngine.ValueMaterial(ItemId, 1);
        }

        // Modul: paper-doll combat rating, per roster character. See
        // EquipmentSlotEngine.ComputeCharacterCombatStatsAsync for why this
        // could not simply be read off StateUpdate - that packet is the
        // active character's only.
        private sealed class RosterCombatStatsResponse
        {
            public int SlotIndex { get; set; }
            public long Accuracy { get; set; }
            public long Armor { get; set; }
            public double BlockPct { get; set; }
            // Task 63: every worn set with two or more pieces, as the server
            // pays it. The screen used to invent "+10% armour, +15% damage"
            // for every family while the server paid nothing at all.
            public System.Collections.Generic.List<ActiveSetResponse> ActiveSets { get; set; } = new();
        }

        private sealed class ActiveSetResponse
        {
            public int SetId { get; set; }
            public string Family { get; set; } = string.Empty;
            public bool Offensive { get; set; }
            public int Pieces { get; set; }
            public int Tier { get; set; }
            public int NextTierPieces { get; set; }
            public double QualityScale { get; set; }
            public double DamagePct { get; set; }
            public double ArmorPct { get; set; }
            public bool Burn { get; set; }
            public bool Thorns { get; set; }
            public bool DamageCap { get; set; }
        }

        private sealed class PlayerInventorySnapshotResponse
        {
            public int BackpackSlotsUsed { get; set; }
            public long MaxStackQuantity { get; set; }
            public System.Collections.Generic.List<InventoryEquipmentResponse> Equipment { get; set; } = new();
            public System.Collections.Generic.List<InventoryStackResponse> Stacks { get; set; } = new();
            public System.Collections.Generic.List<RosterCombatStatsResponse> RosterCombatStats { get; set; } = new();
            // The highest region this life has killed in - what VaultLockRegion is held against.
            public int LifeHighestRegion { get; set; }
        }

        // Modul: Crafting Tree screen. CurrentStock is the UNIFIED
        // backpack+stash balance, i.e. exactly what
        // InventoryAndStashSystem.TryConsumeUnifiedAsync will actually spend
        // when the craft runs - so an affordable-looking recipe is genuinely
        // affordable, rather than reporting one tier and spending from two.
        private sealed class CraftingRecipeResponse
        {
            public int ResultItemId { get; set; }
            public string ResultBaseItemId { get; set; } = string.Empty;
            public int ProfessionType { get; set; }
            public int RequiredLevel { get; set; }
            public int CraftingTimeMs { get; set; }
            public int Mat1Id { get; set; }
            public string Mat1BaseItemId { get; set; } = string.Empty;
            public int Mat1Count { get; set; }
            public long Mat1CurrentStock { get; set; }
            public int Mat2Id { get; set; }
            public string Mat2BaseItemId { get; set; } = string.Empty;
            public int Mat2Count { get; set; }
            public long Mat2CurrentStock { get; set; }
        }

        private sealed class CraftingRecipeSnapshotResponse
        {
            public int PlayerLevel { get; set; }
            public System.Collections.Generic.List<CraftingRecipeResponse> Recipes { get; set; } = new();
        }

        private sealed class PlayerStatisticsResponse
        {
            public int Level { get; set; }
            public long Xp { get; set; }
            public long Gold { get; set; }
            public int PremiumDiamonds { get; set; }
            public int LoginStreakDays { get; set; }
            public int AchievementsClaimedCount { get; set; }
            public int RegionsCompletedCount { get; set; }
            public int CharacterCount { get; set; }
            public int AvailableSkillPoints { get; set; }
            public string GuildName { get; set; } = string.Empty;

            // Modul: lifetime statistics.
            public long TotalKills { get; set; }
            public long BossesSlain { get; set; }
            public long TotalItemsCrafted { get; set; }
            public long TotalDeaths { get; set; }
            public long TotalPlayTimeSeconds { get; set; }
        }

        // Modul: lifetime statistics. The five canonical region bosses, one per
        // region across monster ids 91-115. A static array rather than an
        // inline literal so the "every fifth id from 95" rule is stated once.
        private static readonly int[] CanonicalBossMonsterIds = { 95, 100, 105, 110, 115 };

        private sealed class GuildCreateResponse
        {
            public long GuildId { get; set; }
        }

        private sealed class MarketBrowseResponse
        {
            public System.Collections.Generic.List<MarketListingResponse> Listings { get; set; } = new();
            public int TotalCount { get; set; }
            public int PageIndex { get; set; }
            public int PageSize { get; set; }
        }

        private sealed class GuildCreateRefusalResponse
        {
            public string Reason { get; set; } = string.Empty;
        }

        private sealed class GuildJoinResponse
        {
            public bool Joined { get; set; }
        }

        private sealed class GuildLeaveResponse
        {
            public bool Left { get; set; }
            public bool ClosedGuild { get; set; }
            public long SuccessorPlayerId { get; set; }
            public string Reason { get; set; } = string.Empty;
        }

        private sealed class GuildLeavePreviewResponse
        {
            public bool InGuild { get; set; }
            public bool IsLeader { get; set; }
            public bool ClosesGuild { get; set; }
            public long SuccessorPlayerId { get; set; }
            public int RemainingMembers { get; set; }
        }

        private sealed class GuildApplicationEntryResponse
        {
            public long Id { get; set; }
            public long PlayerId { get; set; }
            public string Username { get; set; } = string.Empty;
            public int ApplicantLevel { get; set; }
            public long CreatedAtEpoch { get; set; }
        }

        private sealed class GuildApplicationActionResponse
        {
            public bool Success { get; set; }
        }

        // Modul: guild discovery, 2026-08-01. Everything a player needs to
        // decide whether a guild is worth applying to, without a second
        // round-trip: how full it is, how strong, what it taxes, and whether
        // they even meet the level requirement.
        // Modul: drop preview, 2026-08-02. What a monster can drop and how
        // likely each entry is, so the combat screen can answer "is this worth
        // farming" before the player commits.
        //
        // ChancePct is the REAL probability per kill, already combining the
        // 35% material roll with the entry's share of its table's weight -
        // not the raw weight, which is meaningless without the total.
        private sealed class MonsterLootEntryResponse
        {
            public int ItemId { get; set; }
            public string BaseItemId { get; set; } = string.Empty;
            public double ChancePct { get; set; }
            public int MinQuantity { get; set; }
            public int MaxQuantity { get; set; }
            public bool IsEquipment { get; set; }
        }

        private sealed class GuildDirectoryEntryResponse
        {
            public long GuildId { get; set; }
            public string Name { get; set; } = string.Empty;
            public int CurrentTier { get; set; }
            public int ActiveMembers { get; set; }
            public int MaxMembers { get; set; }
            public int GuildMMR { get; set; }
            public int TaxRatePct { get; set; }
            public int JoinType { get; set; }
            public int MinApplicationLevel { get; set; }
        }

        private sealed class LeaderboardEntryResponse
        {
            public int Rank { get; set; }
            public long PlayerId { get; set; }
            public string DisplayName { get; set; } = string.Empty;
            public int Level { get; set; }
            public long Xp { get; set; }

            // How far they have actually got - the second and third ranking
            // keys, so the board can show what it sorted by.
            public int HardestMonsterId { get; set; }
            public string HardestMonsterName { get; set; } = string.Empty;
            public int KillsOfHardest { get; set; }

            // Modul: THE TIER TRAVELS WITH THE ROW, rather than the client
            // deriving it from Rank.
            //
            // It is the same information, but deriving it client-side would be
            // LeaderboardTierRegistry's thresholds written down a second time
            // in a second language - and an ordered table that crosses the wire
            // is precisely the shape that drifted in KNOWN_AFFIX_IDS, where ten
            // of twelve entries were wrong and the feature looked dead. The
            // client needs a COLOUR per tier and nothing else, so only the id
            // and the name cross.
            //
            // -1 means no tier: ranked, but below the bottom rung.
            public int TierId { get; set; }
            public string TierName { get; set; } = string.Empty;
            public int WeeklyDiamonds { get; set; }
        }

        private sealed class MarketOwnOrderResponse
        {
            public long OrderId { get; set; }
            /// <summary>"SELL" (a listing or sell order) or "BUY" (a resting buy order).</summary>
            public string OrderType { get; set; } = string.Empty;
            public string BaseItemId { get; set; } = string.Empty;
            /// <summary>0 on a buy order means "any quality".</summary>
            public int QualityTier { get; set; }
            public long Price { get; set; }
            public long CreatedAtEpoch { get; set; }
        }

        private sealed class MarketListingResponse
        {
            public long OrderId { get; set; }
            public string BaseItemId { get; set; } = string.Empty;
            public int QualityTier { get; set; }
            public long Price { get; set; }
            public long CreatedAtEpoch { get; set; }
        }

        private sealed class MarketPricePointResponse
        {
            public long Epoch { get; set; }
            public long Price { get; set; }
        }

        private sealed class MarketPriceHistoryResponse
        {
            public string BaseItemId { get; set; } = string.Empty;
            public int QualityTier { get; set; }

            /// <summary>Most recent execution, or 0 when nothing has ever traded.</summary>
            public long LastPrice { get; set; }

            public long TradeCount { get; set; }

            /// <summary>Mean execution price over the whole window returned.</summary>
            public long AveragePrice { get; set; }

            public long LowPrice { get; set; }
            public long HighPrice { get; set; }

            /// <summary>
            /// Percentage change against the last price BEFORE each window
            /// opened. Null where nothing traded before that point - which is
            /// the honest answer for a young market and is rendered as "-",
            /// not as 0%.
            /// </summary>
            public double? ChangeDayPct { get; set; }
            public double? ChangeWeekPct { get; set; }
            public double? ChangeMonthPct { get; set; }

            /// <summary>Oldest first, so a chart can plot it directly.</summary>
            public System.Collections.Generic.List<MarketPricePointResponse> Points { get; set; } = new();

            /// <summary>
            /// What the seller keeps on a sale at LastPrice: the wealth-scaled
            /// burn plus their own guild's cut. Quoted here rather than
            /// recomputed in the client, because the client guessing at the
            /// bracket is how two numbers for one fee start.
            /// </summary>
            public int FeePct { get; set; }
            public int GuildTaxPct { get; set; }
        }

        private sealed class GuildLeaderboardEntryResponse
        {
            public int Rank { get; set; }
            public long GuildId { get; set; }
            public string Name { get; set; } = string.Empty;
            public int GuildTier { get; set; }
            public int GuildMMR { get; set; }
        }

        private sealed class GuildShardMatchResponse
        {
            public string MatchUuid { get; set; } = string.Empty;
            public long ActiveMatchMmr { get; set; }
            public long GlobalNodeRemainingHp { get; set; }
            public bool IsAttacker { get; set; }
        }

        // Modul: the committed cross-shard match, exposed so a client can
        // actually send SubmitShardAttack.
        //
        // ValidateGuildWarAction refuses an attack aimed at any match other
        // than payload.ActiveCrossShardMatchId - and refuses it by
        // DISCONNECTING - but that Guid lived only in the server's own tick
        // state. No packet and no endpoint carried it, so the only way for a
        // client to send the command was to guess, and a wrong guess ended the
        // session. The web client shipped the screen with the button missing
        // and a paragraph explaining why; this is the fix that paragraph
        // needed.
        //
        // It is a REST read rather than a new StateUpdatePacket field because
        // that packet is 695 bytes against a 700-byte ceiling the tests pin,
        // and a Guid is 16. Spending 16 bytes on every broadcast to every
        // player, for a value only the guild-war screen reads and only while
        // it is open, would be the wrong trade even if the room existed.
        //
        // The query deliberately mirrors StateCheckpointManager's own, so the
        // id a client attacks with is the id the validator will compare it
        // against - a second, subtly different query here would produce
        // exactly the disconnect this endpoint exists to prevent.
        private sealed class ChestActionResponse
        {
            public bool Success { get; set; }
            public long GoldGained { get; set; }
            public string Reason { get; set; } = string.Empty;
        }

        private sealed class ChestSummaryResponse
        {
            public int Total { get; set; }
            /// <summary>Unworn, unlocked, not in the Vault lock - what a sweep could take.</summary>
            public System.Collections.Generic.List<ChestSummaryRow> Sweepable { get; set; } = new();
            /// <summary>Roughly what the full inventory download weighs, in bytes.</summary>
            public long ApproxInventoryBytes { get; set; }
            public int MaxSweepableQualityTier { get; set; }
        }

        private sealed class ChestSummaryRow
        {
            public int Region { get; set; }
            public int QualityTier { get; set; }
            public int Count { get; set; }
        }

        // Measured: 3.2 MB for 17,836 pieces (2026-09-03), about 180 bytes a
        // row of /api/v1/player/inventory JSON, affixes included.
        private const long ApproxInventoryBytesPerPiece = 180;

        private sealed class ChestBulkActionResponse
        {
            public bool Success { get; set; }
            public int RemovedCount { get; set; }
            public long GoldGained { get; set; }
            /// <summary>
            /// Pieces inside the chosen rarity band that were spared because a
            /// character is wearing them. Reported so the count the player sees
            /// afterwards adds up - "it said 4,102 and 3 are still there" is a
            /// bug report, and this is the sentence that prevents it.
            /// </summary>
            public int SkippedWornCount { get; set; }
        }

        private sealed class GoldLedgerCategoryResponse
        {
            public string Category { get; set; } = string.Empty;
            public long Last7Days { get; set; }
            public long Last30Days { get; set; }
            public long SinceRecorded { get; set; }
        }

        private sealed class GoldLedgerResponse
        {
            /// <summary>Every coin spent since the ledger began - what the Treasury deed pays on.</summary>
            public long LifetimeSpent { get; set; }
            /// <summary>The first UTC day with a row, so the screen can say "since"; null when empty.</summary>
            public string? RecordedSince { get; set; }
            public List<GoldLedgerCategoryResponse> Categories { get; set; } = new();

            // Task 79, phase 2. Income has its own "since": it began recording
            // later than spending, and a screen that shared one date would
            // claim income was counted before it was.
            public string? IncomeRecordedSince { get; set; }
            /// <summary>Category holds the GoldIncomeSource name.</summary>
            public List<GoldLedgerCategoryResponse> Income { get; set; } = new();
            public string? MaterialsRecordedSince { get; set; }
            public List<MaterialFlowResponse> Materials { get; set; } = new();
        }

        private sealed class MaterialFlowResponse
        {
            public string ItemId { get; set; } = string.Empty;
            /// <summary>The MaterialFlowDirection name: Gathered, Spent, LostToWarehouseCap, Sold, Discarded.</summary>
            public string Direction { get; set; } = string.Empty;
            public long Last7Days { get; set; }
            public long Last30Days { get; set; }
            public long SinceRecorded { get; set; }
        }
        private sealed class ChestSettingsResponse
        {
            public int AutoSalvageBelowTier { get; set; }
            /// <summary>
            /// One floor per region, region 1 first (task 81). 0 is no rule.
            /// A region's floor can only raise AutoSalvageBelowTier (see
            /// ChestSalvageRules).
            /// </summary>
            public int[] AutoSalvageRegionTiers { get; set; } = new int[ChestSalvageRules.RegionCount];
            /// <summary>
            /// The ceiling the server will accept, published so the client
            /// builds its dropdown from the server's rule rather than a second
            /// copy of it that can drift. Legendary and above is never
            /// sweepable - see VillageChestEngine.MaxSweepableQualityTier.
            /// </summary>
            public int MaxSweepableQualityTier { get; set; }
        }

        /// <summary>
        /// Sells or bins EVERY carried piece at or below a rarity, in one call.
        ///
        /// Modul: the chest had no drain. Loot arrives on 15% of kills and the
        /// only removal the game offered was a per-item button - so one live
        /// account reached 17,836 EquipmentInstances rows, at which point the
        /// inventory screen that would have let them clear it was itself too
        /// slow to open. The remedy cannot be the same per-item call in a loop;
        /// seventeen thousand round trips is not a remedy.
        ///
        /// The tier is validated here as well as in the engine. A client that
        /// sent 14 would otherwise sweep away every Legendary the player owns,
        /// and there is no undo for that.
        /// </summary>
        private sealed class ForgeStackPreviewResponse
        {
            public string BaseItemId { get; set; } = string.Empty;
            public int FromTier { get; set; }
            public int CeilingTier { get; set; }
            public int ForgeLevel { get; set; }
            public int TotalFusions { get; set; }
            public long GoldCost { get; set; }
            public long GoldAvailable { get; set; }
            public bool StoppedByGold { get; set; }
            public bool StoppedByCap { get; set; }
            /// <summary>Pieces per tier after the fusion, tiers FromTier..CeilingTier, non-zero only.</summary>
            public List<ForgeStackTierCount> Result { get; set; } = new();
        }

        private sealed class ForgeStackTierCount
        {
            public int Tier { get; set; }
            public int Count { get; set; }
        }

        /// <summary>
        /// What the Delve screen draws: the run if there is one, and what a run
        /// would cost if there is not.
        /// </summary>
        private static readonly JsonSerializerOptions WorldBossStrikeJson = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        private sealed class HuntingEstimateResponse
        {
            public int MonsterId { get; set; }
            public bool CanDamage { get; set; }
            public double SecondsPerKill { get; set; }
            public double SecondsPerKillLow { get; set; }
            public double SecondsPerKillHigh { get; set; }
            public long XpPerHour { get; set; }
            public long GoldPerHour { get; set; }
            public bool SurvivesWithFood { get; set; }
            public bool SurvivesWithoutFood { get; set; }
            public int KillsBeforeDeathWithoutFood { get; set; }
            public double FoodPerHour { get; set; }
        }

        private sealed class CombatProjectionResponse
        {
            public int Slot { get; set; }
            public List<HuntingEstimateResponse> Monsters { get; set; } = new();
        }

        // Modul: ONE MINUTE PER CHARACTER. The projection is ~2M cheap ticks of
        // arithmetic, and the Combat screen asks every time it opens. Gear and
        // levels change on a scale of minutes, and a stale estimate is still
        // labelled "estimate". A fresh one comes after the next equip at worst
        // a minute late.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(long PlayerId, int Slot), (DateTime At, CombatProjectionResponse Body)> _projectionCache = new();

        private sealed class ChestLockResponse
        {
            public bool Success { get; set; }

            /// <summary>The state the item ended in, so the screen never guesses.</summary>
            public bool Locked { get; set; }

            public string Reason { get; set; } = string.Empty;
        }

        private sealed class GuildWarUnlockResponse
        {
            public bool Unlocked { get; set; }
            public int QualifyingPlayers { get; set; }
            public int RequiredPlayers { get; set; }
            public int QualifyingGuilds { get; set; }
            public int RequiredGuilds { get; set; }
            public int RequiredMembersPerGuild { get; set; }
            public int MinimumLevel { get; set; }
        }

        private sealed class MailboxEntryResponse
        {
            public long Id { get; set; }
            public string BaseItemId { get; set; } = string.Empty;
            public int QualityTier { get; set; }
            public int Quantity { get; set; }
            public long GoldAttachment { get; set; }
            public int DiamondAttachment { get; set; }
            /// <summary>The attached title's display name, resolved here - the client keeps no title list.</summary>
            public string? TitleAttachment { get; set; }
            public string? TitleAttachmentColor { get; set; }
            public bool HasEquipmentAttachment { get; set; }
            public string? SenderName { get; set; }
            public string? MessageText { get; set; }
            public long ReceivedTimestamp { get; set; }
        }

        private sealed class BankEntryResponse
        {
            public long Id { get; set; }
            public string BaseItemId { get; set; } = string.Empty;
            public int QualityTier { get; set; }
            public bool IsAffixLocked { get; set; }
        }

        private sealed class StoreCatalogEntryResponse
        {
            public string ProductId { get; set; } = string.Empty;
            public int DiamondAmount { get; set; }
        }

        private sealed class GuildRosterEntryResponse
        {
            public long PlayerId { get; set; }
            public int Role { get; set; }
            public long ContributionPoints { get; set; }
            public bool IsOnline { get; set; }

            // Modul: WHAT THE VIEWER MAY DO TO THIS MEMBER (2026-11-03). The
            // four ranks' rules live in GuildManagementEngine.MayAssign and the
            // caps; the client draws a button only where these say yes, so the
            // permission table exists once. The engine still checks every
            // action - these only decide which buttons to show.
            public bool CanPromote { get; set; }
            public bool CanDemote { get; set; }
            public bool CanKick { get; set; }
            public bool CanTransfer { get; set; }
        }

        private sealed class PlayerMetadataResponse
        {
            public int ChroniclePassLevel { get; set; }
            public int AccumulatedSeasonalXp { get; set; }
            public int EventHorizonTransactionCount { get; set; }
        }

        private sealed class EmailConsentResponse
        {
            public bool Consented { get; set; }
            /// <summary>
            /// Whether the account has an address to send to at all. A guest
            /// has none, and the toggle says so instead of pretending it works.
            /// </summary>
            public bool HasEmailAddress { get; set; }
        }

        private sealed class OnboardingSeenResponse
        {
            /// <summary>
            /// Empty when nothing has been recorded. `HasRecord` is what
            /// distinguishes that from "never taught".
            /// </summary>
            public string[] Seen { get; set; } = System.Array.Empty<string>();

            /// <summary>
            /// False for an account that has never been baselined on any
            /// device. The client uses it to decide whether to mark everything
            /// already true as seen instead of queueing every explanation at a
            /// player who has been at this for weeks.
            /// </summary>
            public bool HasRecord { get; set; }
        }

        /// <summary>The most ids one account may store.</summary>
        /// <remarks>
        /// There are twenty-six explanations. The ceiling is generous because
        /// the point is to refuse a body that is plainly not a seen-set - a
        /// client looping a bug into the column - rather than to predict how
        /// many explanations this game will end up with.
        /// </remarks>
        private const int MaxOnboardingSeenIds = 256;

        /// <summary>Longest a single explanation id may be.</summary>
        private const int MaxOnboardingIdLength = 64;

        private sealed class AchievementsStateResponse
        {
            public int ClaimedAchievementFlags { get; set; }
            public int TotalAchievementsClaimedCount { get; set; }
            public ulong ClaimedMilestonesBitmask { get; set; }
        }

        internal sealed class LootOddsResponse
        {
            /// <summary>False until this player's first drop request since the server started.</summary>
            public bool Known { get; set; }
            public float LootLuckPct { get; set; }
            public float RarityElevationPct { get; set; }
            public bool HasGoldenFleece { get; set; }
            /// <summary>CombatLootEngine.EquipmentDropChance: how often a kill drops gear at all.</summary>
            public double EquipmentDropChance { get; set; }
            /// <summary>Share of equipment drops that land at Legendary or better, after fleece and elevation.</summary>
            public double LegendaryPlusPerDrop { get; set; }
            public double AncientPlusPerDrop { get; set; }
            /// <summary>Per-tier share of drops, index = tier (0 unused).</summary>
            public double[] TierShares { get; set; } = Array.Empty<double>();
        }

        private sealed class RegionProgressResponse
        {
            public int RegionId { get; set; }
            public int CurrentKills { get; set; }
            public int RequiredKills { get; set; }
            public int BossKills { get; set; }
            public int RequiredBossKills { get; set; }
            public bool IsCompleted { get; set; }
            public int LootLuckBonusPct { get; set; }
        }

        private sealed class BreedingRosterEntryResponse
        {
            public string CharacterId { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public int AgePhase { get; set; }
            public int GenerationIndex { get; set; }
            public bool IsBreedingActive { get; set; }
            public long BreedingCooldownEndEpoch { get; set; }
            /// <summary>Away on an expedition (2026-11-13): resting for breeding until it returns.</summary>
            public bool IsAway { get; set; }
            public bool IsEpicMutation { get; set; }
            public bool IsInbred { get; set; }

            // Modul: hero x villager. A pair needs one of each, and the roster
            // did not say which a character was - so a client could only offer
            // every villager for every hero and let the server silently roll
            // back half of them. The four aptitudes are here for the same
            // reason: which villager to marry is a comparison against what the
            // hero already carries, and that comparison was unshowable.
            public bool IsFemale { get; set; }
            public int AptitudeStrength { get; set; }
            public int AptitudeSkill { get; set; }
            public int AptitudeEndurance { get; set; }
            public int AptitudeFortune { get; set; }
            public int LocusRaceDominant { get; set; }
            public int LocusRaceRecessive { get; set; }
            public long TraitMask { get; set; }
        }

        private sealed class RebirthRequestBody
        {
            public int ExpectedRebirthCount { get; set; } = -1;
            // What the player typed - RebirthRules.ConfirmationWord.
            public string? Confirm { get; set; }
            // Task 114: equipment ids to carry through, each paid for by rarity.
            public long[]? KeepEquipmentIds { get; set; }
            /// <summary>The Rebirth Trial for the next life (RebirthTrials), 0 or absent for none.</summary>
            public int TrialId { get; set; }
        }

        private sealed class GenePreviewLocusResponse
        {
            public string LocusName { get; set; } = string.Empty;
            public int ParentPaternalDominant { get; set; }
            public int ParentMaternalDominant { get; set; }
            public int PredictedMinDominant { get; set; }
            public int PredictedMaxDominant { get; set; }
            public double MutationChancePct { get; set; }
        }

        // Modul: aptitudes in the preview. The four aptitudes are what a pairing
        // is actually FOR - loci are a 1.5%-a-generation curiosity, aptitudes
        // are the axis a season leaves standing - and the preview did not
        // mention them, so the one decision the gene pool exists to pose was
        // being made blind. Exact rather than sampled: see
        // BreedingAptitudes.PreviewOne.
        private sealed class AptitudePreviewResponse
        {
            public string AptitudeName { get; set; } = string.Empty;
            public int ParentHero { get; set; }
            public int ParentPartner { get; set; }
            public int PredictedMin { get; set; }
            public int PredictedMax { get; set; }
            // The band when this aptitude is SELECTED on the Breeding Grounds:
            // the better parent is taken outright, so only the mutation moves
            // it. Sent rather than derived so the screen can show what a tick
            // buys without a client copy of the rule.
            public int SelectedMin { get; set; }
            // The selected band's top (BreedingAptitudes.PreviewSelected).
            public int SelectedMax { get; set; }
            // True when the bloodline ceiling, not the parents, set PredictedMax.
            public bool AtCeiling { get; set; }
        }

        private sealed class TraitOddsResponse
        {
            public int TraitId { get; set; }
            public int ChancePct { get; set; }
            public string Source { get; set; } = string.Empty;
        }

        private sealed class BreedingPreviewResponse
        {
            public bool IsEligible { get; set; }
            public string IneligibleReason { get; set; } = string.Empty;
            public bool IsInbredRisk { get; set; }
            public long BreedingCostGold { get; set; }
            public bool HasSufficientGold { get; set; }
            public System.Collections.Generic.List<GenePreviewLocusResponse> Loci { get; set; } = new();
            public System.Collections.Generic.List<AptitudePreviewResponse> Aptitudes { get; set; } = new();
            public System.Collections.Generic.List<TraitOddsResponse> TraitOdds { get; set; } = new();
            public int MutationChancePct { get; set; }
            public int FlawChancePct { get; set; }
            // BreedingAptitudes.CeilingFor this account's rebirths, and what the
            // next rebirth would raise it to (equal at the last one).
            public int BloodlineCeiling { get; set; }
            public int NextBloodlineCeiling { get; set; }
            // The races the child can be born as: the first is the default
            // (father / hero), the second the other parent's when it differs.
            // Sent so the picker never offers a race the engine would replace.
            public System.Collections.Generic.List<int> ChildRaceOptions { get; set; } = new();
            // How many aptitudes the Grounds lets the player select now.
            public int SelectableCount { get; set; }
        }

        private sealed class BreedingSuggestionResponse
        {
            /// <summary>The partner picker's key: "c:" + character id, or "v:" + newcomer id.</summary>
            public string Key { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public bool IsVillager { get; set; }
            public bool IsRelated { get; set; }
            public int RaceId { get; set; }
            /// <summary>Expected child aptitude total, one decimal.</summary>
            public double ExpectedTotal { get; set; }
            public int ChildFloorTotal { get; set; }
            public int SelectionMask { get; set; }
            public long CostGold { get; set; }
        }

        // Modul: UI audit follow-up. Lists both relationship kinds (Friend
        // and Blocked - RelationType) the caller has with other players,
        // joined against PlayerRecords for a real Username/Level to show
        // rather than a bare numeric Id. Read-only, matching every other
        // snapshot handler's transaction shape.
        // ---------------------------------------------------------------
        // Conversations. The durable half of private chat - see
        // ConversationMessage. The WebSocket still delivers a whisper live;
        // these three answer "what did we say", which it never could.
        // ---------------------------------------------------------------

        private sealed class ConversationSummaryResponse
        {
            public long PlayerId { get; set; }
            public string Username { get; set; } = string.Empty;
            public string LastMessage { get; set; } = string.Empty;
            public long LastMessageAtEpochMs { get; set; }
            public bool LastMessageWasMine { get; set; }
            public int UnreadCount { get; set; }
            public bool IsOnline { get; set; }
        }

        private sealed class ConversationMessageResponse
        {
            public long Id { get; set; }
            public long SenderPlayerId { get; set; }
            public bool Mine { get; set; }
            public string MessageText { get; set; } = string.Empty;
            public long SentAtEpochMs { get; set; }
            public bool Read { get; set; }
        }

        private sealed class PlayerMaterialsSnapshotResponse
        {
            public System.Collections.Generic.List<InventoryStackResponse> Stacks { get; set; } = new();
        }

        /// <summary>
        /// The stackable half of the chest, and nothing else.
        ///
        /// Modul: THE LARDER WAS DOWNLOADING THREE MEGABYTES TO COUNT FISH.
        ///
        /// /api/v1/player/inventory answers one question with one blob:
        /// equipment, material stacks and per-character combat ratings
        /// together. Three screens - the Larder, Boosts and the guild deposit -
        /// read ONLY `Stacks`, which is 63 rows on the live database. They were
        /// paying for the equipment list to get there, and on the
        /// worst-affected account that list is 17,836 rows and 3.2 MB, parsed
        /// row by row through JsonNode on the way out. Every command result
        /// invalidates the query, so it was not once per visit either.
        ///
        /// Two queries and no JSON parsing at all. The equipment blob still
        /// exists for the screens that genuinely need it - the chest, the
        /// market's sell form and the paper doll.
        ///
        /// Shares InventoryStackResponse with the big snapshot deliberately:
        /// the two must never disagree about what a stack is, and the summing
        /// rule below is the same one, for the same reason (see that type's
        /// comment on why the backpack/stash split is not exposed).
        /// </summary>
        private sealed class WornPieceResponse
        {
            public long InstanceId { get; set; }
            public string BaseItemId { get; set; } = string.Empty;
            public int QualityTier { get; set; }
            public int SlotIndex { get; set; }
            // 2026-10-11: each affix's rarity (1 Common .. 5 Legendary), so the
            // boss wall can say how many of the worn affixes meet its rarity -
            // bosses 4 and 5 gate on affix rarity, which no screen counted.
            public List<int> AffixRarities { get; set; } = new();
        }

        // Modul: UI rework. Batch id-to-username resolution for every
        // social surface that only ever receives a numeric player id (chat
        // rows, whisper threads). Capped at NameLookupBatchLimit ids per
        // request so a malformed or hostile query string cannot turn one
        // GET into an unbounded IN(...) scan; unknown ids are simply absent
        // from the response rather than 404ing the whole batch, since a
        // chat log legitimately contains ids of players that no longer
        // exist.
        private const int NameLookupBatchLimit = 64;

        // Modul: guild discovery, 2026-08-01.
        //
        // Joining was by EXACT NAME with nothing to browse, so a player who had
        // not been told a guild's precise spelling had no way to find one at
        // all. Create, join, roster and the application flow all existed; only
        // the ability to discover a guild was missing.
        //
        // Paging deliberately mirrors the leaderboard's skip/take shape, which
        // was audited correct on 2026-08-01 - same validation, same clamping,
        // rather than a second convention to keep in sync.
        private const int GuildListMaxTake = 50;

        // Modul: drop preview, 2026-08-02.
        //
        // An endpoint rather than a shipped content file on purpose. Drop rates
        // are balance data; as a client asset they would drift from the server
        // table and show players odds the server does not honour. The rates are
        // read from CombatLootEngine's own constants for the same reason.
        // Modul: browser client support, 2026-08-02.
        //
        // Origins come from FOLKIDLE_WEB_ORIGINS, comma separated. Unset means
        // no browser origin is allowed, which is the correct default for a
        // deployment that has no web client yet - it fails closed rather than
        // silently opening the API to every page on the internet.
        //
        // Local development typically wants:
        //   FOLKIDLE_WEB_ORIGINS=http://localhost:5173,http://127.0.0.1:5173
        // which is Vite's default port.
        private static readonly string[] AllowedWebOrigins = ResolveAllowedWebOrigins();

        private static string[] ResolveAllowedWebOrigins()
        {
            string raw = Environment.GetEnvironmentVariable("FOLKIDLE_WEB_ORIGINS") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<string>();
            }

            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static void ApplyCorsHeaders(HttpListenerContext context)
        {
            string? origin = context.Request.Headers["Origin"];
            if (string.IsNullOrEmpty(origin))
            {
                // Not a browser request - the Unity client, curl, health checks.
                return;
            }

            bool allowed = false;
            for (int i = 0; i < AllowedWebOrigins.Length; i++)
            {
                if (string.Equals(AllowedWebOrigins[i], origin, StringComparison.OrdinalIgnoreCase))
                {
                    allowed = true;
                    break;
                }
            }

            if (!allowed)
            {
                // Deliberately silent: echoing back a rejected origin would
                // defeat the allow-list, and the browser reports the failure
                // clearly enough on its side.
                return;
            }

            context.Response.Headers["Access-Control-Allow-Origin"] = origin;
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            context.Response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";

            // Origin is echoed rather than fixed, so any cache between here and
            // the browser must key on it.
            context.Response.Headers["Vary"] = "Origin";
        }

        // Modul: browser client support, 2026-08-02. Phase 0, step 3 of the
        // web client port plan.
        //
        // The Unity client reads these five files off disk from
        // StreamingAssets/GameData; they are byte-identical to server/GameData
        // (the server copy is authoritative and the client's is a mirror), so
        // serving the server's own copy gives a browser client the same bytes
        // without introducing a third one.
        //
        // Unauthenticated on purpose. These files already ship inside the
        // Unity app bundle, so they are public by construction - gating them
        // behind a bearer token would add a login dependency to content
        // loading while protecting nothing.
        //
        // GameBalanceConfig.json is the one file in that directory that is
        // NOT part of the client mirror, and it is excluded rather than
        // served: it is server balance data (drop weights, price tables), and
        // this codebase has already decided once that balance data does not
        // ship to clients - see HandleMonsterLoot's own comment on why drop
        // rates are an endpoint rather than a content file.
        private static readonly System.Collections.Generic.HashSet<string> ServerOnlyGameDataFiles =
            new(StringComparer.OrdinalIgnoreCase) { "GameBalanceConfig.json" };

        // Anything else in GameData/ is served, so adding a content file does
        // not also require editing a list here. The name is validated rather
        // than sanitized - a request that is not exactly "word.json" is
        // rejected outright, which forecloses path traversal without needing
        // to reason about how many ways ".." can be spelled.
        private static readonly System.Text.RegularExpressions.Regex GameDataFileNamePattern =
            new(@"^[A-Za-z0-9_\-]+\.json$", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string GameDataDirectory => System.IO.Path.Combine(AppContext.BaseDirectory, "GameData");

        // Modul: so a web client discovers which content files exist instead
        // of hardcoding the list - the Unity client hardcodes it, and that
        // hardcoded list is exactly the kind of second copy the port plan
        // says must not be created.
        private static readonly System.Text.RegularExpressions.Regex AudioFileNamePattern =
            new(@"^[A-Za-z0-9_\-]+\.wav$", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string AudioDirectory => System.IO.Path.Combine(AppContext.BaseDirectory, "Audio");

        // Modul: BACKGROUND MUSIC (owner, 2026-09-28) - tracks of about 4 MB,
        // so unlike the clips they are served with Range support: an <audio>
        // element streams a track and seeks with byte ranges, and a server
        // that ignores Range makes Chromium refuse to seek or loop it cleanly.
        // Names are validated, never sanitized, exactly as the clips are.
        /// <summary>The prefix of a screen theme in the music folder (served, not listed).</summary>
        internal const string ScreenThemePrefix = "screen_";

        private static readonly System.Text.RegularExpressions.Regex MusicFileNamePattern =
            new(@"^[A-Za-z0-9_\-]+\.mp3$", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string MusicDirectory => System.IO.Path.Combine(AudioDirectory, "Music");

        // Modul: the generated 2D artwork, served the same way the audio is.
        //
        // Unlike audio, the sprite tree is NESTED and its filenames contain
        // spaces and ampersands ("Tools&Equipment/Melee weapons/Doom Edge.png"),
        // because they were authored for a Unity import rather than for a URL.
        // Renaming them would break the Unity-side AssetRegistryBuilder, so the
        // path is validated segment by segment instead.
        //
        // The pattern below permits exactly the characters those names actually
        // use and nothing else. A ".." segment cannot match it, so path
        // traversal is impossible by construction rather than by sanitisation -
        // the same reasoning HandleAudioFile uses, extended to a subpath.
        private static readonly System.Text.RegularExpressions.Regex SpriteSegmentPattern =
            new(@"^[A-Za-z0-9 _\-&'.]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string SpritesDirectory => System.IO.Path.Combine(AppContext.BaseDirectory, "Sprites");

        private sealed class GameDataManifestResponse
        {
            public System.Collections.Generic.List<string> Files { get; set; } = new();
        }

        // Modul: small in-memory cache to avoid a DB round trip on every
        // single authenticated HTTP request (market/codex/breeding/mastery/
        // achievements/forge/guild-logistics/storefront/leaderboard
        // snapshots all call through here) - the AccountId<->PlayerId
        // mapping is immutable once an account exists, so this never needs
        // invalidation or expiry.
        private readonly ConcurrentDictionary<Guid, long> _accountIdToPlayerIdCache = new();

        // Modul: mirrors _accountIdToPlayerIdCache immediately above - avoid a
        // DB round trip on every authenticated request. Unlike that cache,
        // this one DOES need invalidation: a revocation event (logout,
        // password reset, refresh-token replay) writes a new value here at
        // the moment it bumps the DB column, so this server's own writes
        // never go stale. A miss (this pod just started, or the account has
        // never been looked up here) falls through to the DB and populates
        // the cache either way, including with null.
        private readonly ConcurrentDictionary<Guid, string?> _accountCurrentNonce = new();

        private bool ParseValidateAndEnqueue(byte[] buffer, int count, long playerId, WebSocketSession session)
        {
            ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, 0, count);
            var packet = MemoryMarshal.Read<ClientCommandPacket>(span);
            return ValidateAndEnqueue(ref packet, playerId, session);
        }

        // Modul: JSON WebSocket mode, 2026-08-02. Split out of
        // ParseValidateAndEnqueue so a command that arrived as JSON goes
        // through the SAME token bucket, the same flood infraction, the same
        // telemetry event and the same command queue as one that arrived as
        // bytes. The encoding is a transport detail; anti-cheat and rate
        // limiting are not, and a second entry point that skipped them would
        // be a way in rather than a second client.
        private bool ValidateAndEnqueue(ref ClientCommandPacket packet, long playerId, WebSocketSession session)
        {
            if (!ClientCommandValidator.ValidateNetworkThroughput(ref session.TokenBucket, playerId, ref packet, out int reasonCode))
            {
                // Modul: A FLOOD ABORT SAYS SO. It used to close the socket with
                // no log line at all, which twice cost a day of guessing (x10 in
                // 2026-11-01, the 20-bot lockstep run in 2026-11-03).
                Console.WriteLine($"[flood] player {playerId} aborted: token bucket empty on command {packet.Command} (reason {reasonCode})");
                session.Socket.Abort();
                _connectedClients.TryRemove(playerId, out _);
                _ = MarkFloodInfractionAsync(playerId);
                TelemetryStreamer.TryWrite(new TelemetryEvent
                {
                    PlayerId = playerId,
                    EventType = 3,
                    Value1 = (byte)packet.Command,
                    Value2 = reasonCode,
                    Timestamp = Environment.TickCount64
                });
                return false;
            }

            RecordAcceptedPacket();
            _antiCheatTelemetryEngine?.RecordCommand(playerId, (byte)packet.Command);
            CommandQueue.Enqueue(new PlayerCommand { PlayerId = playerId, Packet = packet });
            return true;
        }

        // Modul: JSON WebSocket mode, 2026-08-02. Lifted verbatim out of the
        // binary receive loop so the JSON path routes chat through exactly
        // the same code - profanity masking, channel routing, the
        // guild-membership check and the silent-drop conventions all live
        // here once. Not async, so the unsafe in-place helpers below can take
        // the packet by ref (unsafe blocks are not permitted directly inside
        // an async method in this project's language version - the same split
        // ExtractChatMessageText already uses).
        private void DispatchInboundChatRequest(long playerId, WebSocketSession session, ref RequestChatMessagePacket chatRequest)
        {
            // Modul: Comprehensive Game System Audit, Part 2.3. Profanity
            // filtering on all unmoderated channels before transmission.
            if (FolkIdle.Server.Engine.ChatProfanityFilter.IsEnabled)
            {
                ApplyProfanityFilterInPlace(ref chatRequest);
            }

            string chatText = ExtractChatMessageText(ref chatRequest);

            if (chatRequest.ChannelType == ChatEngine.GuildChannelType)
            {
                // A player not currently in a guild has nothing to route a
                // guild message to - silently dropped, matching every other
                // rejected-chat-message path (rate limit, empty content)
                // rather than disconnecting.
                if (session.GuildId > 0)
                {
                    _ = _chatEngine.PublishGuildMessageAsync(playerId, session.GuildId, chatText);
                }
            }
            else if (chatRequest.ChannelType == ChatEngine.WhisperChannelType)
            {
                // Modul: Full-Stack Social Layer, Part 3. Client-supplied
                // recipient, same treatment as every other
                // rejected-chat-message path - an invalid target (0, self) is
                // silently dropped by PublishWhisperMessageAsync's own guard
                // rather than disconnecting.
                _ = _chatEngine.PublishWhisperMessageAsync(playerId, chatRequest.TargetPlayerId, chatText);
            }
            else
            {
                _ = _chatEngine.PublishMessageAsync(playerId, chatText);
            }
        }

        // Modul: JSON WebSocket mode, 2026-08-02. The binary protocol never
        // needed this: every one of its six packets is a fixed size well
        // under the 1024-byte receive buffer, arriving as exactly one
        // unfragmented frame. JSON is neither - a ClientCommand renders to
        // roughly 2 KB, and a browser's WebSocket stack is free to fragment
        // it - so a JSON session must accumulate until EndOfMessage or it
        // would silently parse a truncated prefix.
        //
        // Bounded on purpose. Without a cap, a client could stream an
        // unbounded "message" and make the server buy the memory one frame at
        // a time, which is a denial of service that never reaches the token
        // bucket because the token bucket only sees completed packets.
        private const int MaxJsonMessageBytes = 64 * 1024;

        private static async Task<byte[]?> ReadTextMessageAsync(WebSocket socket, byte[] firstChunk, WebSocketReceiveResult firstResult, CancellationToken cancellationToken)
        {
            if (firstResult.Count > MaxJsonMessageBytes)
            {
                return null;
            }

            if (firstResult.EndOfMessage)
            {
                var single = new byte[firstResult.Count];
                Array.Copy(firstChunk, single, firstResult.Count);
                return single;
            }

            using var accumulator = new System.IO.MemoryStream(firstResult.Count * 2);
            accumulator.Write(firstChunk, 0, firstResult.Count);

            var continuation = new byte[firstChunk.Length];
            while (true)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(continuation), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (accumulator.Length + result.Count > MaxJsonMessageBytes)
                {
                    return null;
                }

                accumulator.Write(continuation, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return accumulator.ToArray();
                }
            }
        }

        private AuthHandshakePacket ParseAuthHandshakePacket(byte[] buffer, int count)
        {
            ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, 0, count);
            return MemoryMarshal.Read<AuthHandshakePacket>(span);
        }

        // Mirrors SimulationEngine.CopyDeviceTokenBytes's exact fixed-buffer
        // read pattern (see ClientCommandPacket.DeviceTokenBytes), just
        // trimmed to the sender-declared JwtTokenLength instead of always
        // copying the full fixed capacity.
        private static unsafe string ExtractJwtToken(ref AuthHandshakePacket packet)
        {
            int length = packet.JwtTokenLength;
            if (length < 0 || length > AuthHandshakePacket.JwtTokenCapacity)
            {
                length = 0;
            }

            fixed (byte* source = packet.JwtToken)
            {
                return System.Text.Encoding.UTF8.GetString(source, length);
            }
        }

        // Mirrors ExtractJwtToken's exact fixed-buffer read pattern, clamping
        // an attacker-controlled MessageLength to the buffer's real capacity
        // before ever reading it, so a lie about length cannot read past the
        // fixed array.
        private static unsafe string ExtractChatMessageText(ref RequestChatMessagePacket packet)
        {
            int length = packet.MessageLength;
            if (length < 0 || length > RequestChatMessagePacket.MessageCapacity)
            {
                length = 0;
            }

            fixed (byte* source = packet.MessageText)
            {
                return System.Text.Encoding.UTF8.GetString(source, length);
            }
        }

        // Modul: Comprehensive Game System Audit, Part 2.3. Masks
        // blacklisted words in place over the packet's fixed byte buffer -
        // see ChatProfanityFilter's own doc comment for the
        // zero-allocation design. Lives in its own static method (not
        // inline in the receive loop) because unsafe blocks are not
        // permitted directly inside async methods in this project's
        // language version - the exact split ExtractChatMessageText above
        // already uses.
        private static unsafe void ApplyProfanityFilterInPlace(ref RequestChatMessagePacket packet)
        {
            int length = packet.MessageLength;
            if (length <= 0 || length > RequestChatMessagePacket.MessageCapacity)
            {
                return;
            }

            fixed (byte* source = packet.MessageText)
            {
                FolkIdle.Server.Engine.ChatProfanityFilter.FilterInPlace(new Span<byte>(source, length), length);
            }
        }

        private void ParseAdminCommand(byte[] buffer, int count)
        {
            ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(buffer, 0, count);
            var adminPacket = MemoryMarshal.Read<AdminCommandPacket>(span);

            if (adminPacket.CommandType == 1)
            {
                GlobalEngineState.GlobalXpMultiplier = adminPacket.MultiplierValue;
            }
            else if (adminPacket.CommandType == 2)
            {
                GlobalEngineState.GlobalDropMultiplier = adminPacket.MultiplierValue;
            }
        }

        private void RecordAcceptedPacket()
        {
            long currentSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long observedSecond = Interlocked.Read(ref _throughputWindowEpoch);
            if (observedSecond != currentSecond &&
                Interlocked.CompareExchange(ref _throughputWindowEpoch, currentSecond, observedSecond) == observedSecond)
            {
                long previousCount = Interlocked.Exchange(ref _acceptedPacketsWindow, 0L);
                GlobalEngineState.SetActiveConnectionThroughput(previousCount);
            }

            Interlocked.Increment(ref _acceptedPacketsWindow);
        }

        private sealed class AuthLoginResponse
        {
            public string Token { get; set; } = string.Empty;
            public long ExpiresAtEpoch { get; set; }

            /// <summary>
            /// The long-lived half, and the ONLY moment it is ever transmitted.
            /// Empty is a valid value: issuing it is allowed to fail without
            /// failing the login, because a player who is signed in now cares
            /// far more about that than about tomorrow morning.
            /// </summary>
            public string RefreshToken { get; set; } = string.Empty;

            public long RefreshExpiresAtEpoch { get; set; }
        }

        private sealed class RegisterErrorResponse
        {
            public string Reason { get; set; } = string.Empty;
        }

        private async Task<bool> IsPlayerBlacklistedAsync(long playerId)
        {
            Guid accountId = await ResolveAccountIdAsync(playerId);
            await using var context = await _contextFactory.CreateDbContextAsync();
            var quota = await context.AccountSecurityQuotas.AsNoTracking().FirstOrDefaultAsync(q => q.AccountId == accountId);
            return quota?.IsPermanentlyBlacklisted == true;
        }

        private async Task MarkFloodInfractionAsync(long playerId)
        {
            Guid accountId = await ResolveAccountIdAsync(playerId);
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var context = await _contextFactory.CreateDbContextAsync();
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE \"AccountSecurityQuotas\" SET \"IsPermanentlyBlacklisted\" = TRUE WHERE \"AccountId\" = {0}", accountId);
        }

        private async Task<Guid> ResolveAccountIdAsync(long playerId)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            var player = await context.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId);
            if (player != null && player.PlayerGuid != Guid.Empty)
            {
                return player.PlayerGuid;
            }

            long mixed = playerId ^ 0x71A7E11D5F3759DFL;
            return new Guid(
                unchecked((int)playerId),
                unchecked((short)(playerId >> 32)),
                unchecked((short)(playerId >> 48)),
                unchecked((byte)mixed),
                unchecked((byte)(mixed >> 8)),
                unchecked((byte)(mixed >> 16)),
                unchecked((byte)(mixed >> 24)),
                unchecked((byte)(mixed >> 32)),
                unchecked((byte)(mixed >> 40)),
                unchecked((byte)(mixed >> 48)),
                unchecked((byte)(mixed >> 56)));
        }

        private async Task HandleClientLoopAsync(WebSocket socket)
        {
            var buffer = new byte[1024];
            long playerId = 0;
            string? redisLockToken = null;
            CancellationTokenSource? lockRenewalCts = null;
            Task? lockRenewalTask = null;
            WebSocketSession? session = null;
            try
            {
                using var cts = new CancellationTokenSource(5000);
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

                // Modul: mandatory JWT-gated handshake. No gameplay CommandType
                // is ever accepted before this succeeds - the receive loop
                // below is only reached once playerId has been resolved from a
                // cryptographically verified token, replacing the old scheme
                // where any syntactically-valid, previously-unseen raw Guid
                // token auto-provisioned a brand new account with zero
                // credential verification (the exact vulnerability this
                // handshake exists to close).
                // Modul: JSON WebSocket mode, 2026-08-02. The per-connection
                // protocol switch, decided here and nowhere else.
                //
                // The switch IS the frame type of the handshake: a Binary
                // first frame means the byte protocol (what the Unity client
                // has always sent - that branch below is unchanged), a Text
                // first frame means JSON. A frame's type is unforgeable and
                // already carried by every WebSocket implementation, so this
                // needs no negotiation round-trip and no way for the two
                // sides to disagree about which protocol they are speaking.
                // The JSON handshake additionally carries an explicit
                // "mode":"json" so the intent is legible in a packet capture
                // rather than implied; it is validated, not inferred.
                AuthHandshakePacket authPacket;
                bool useJsonProtocol = false;
                bool compressFrames = false;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    byte[]? handshakeJson = await ReadTextMessageAsync(socket, buffer, result, cts.Token);
                    if (handshakeJson == null)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Handshake message too large", CancellationToken.None);
                        return;
                    }

                    if (!PacketJsonCodec.TryParseEnvelope(handshakeJson, out JsonDocument? handshakeDocument, out string handshakeType, out string? handshakeError))
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, $"Malformed handshake: {handshakeError}", CancellationToken.None);
                        return;
                    }

                    using (handshakeDocument)
                    {
                        if (handshakeType != PacketJsonCodec.TypeAuthHandshake)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Expected an AuthHandshake packet", CancellationToken.None);
                            return;
                        }

                        if (handshakeDocument!.RootElement.TryGetProperty(PacketJsonCodec.ModePropertyName, out JsonElement modeElement))
                        {
                            string declaredMode = modeElement.GetString() ?? string.Empty;
                            if (!string.Equals(declaredMode, PacketJsonCodec.ModeJson, StringComparison.OrdinalIgnoreCase))
                            {
                                // A JSON handshake asking for the binary mode
                                // is a contradiction, and honouring either
                                // half of it would leave the two sides
                                // speaking different protocols.
                                await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData,
                                    $"Handshake sent as JSON but declared mode '{declaredMode}'", CancellationToken.None);
                                return;
                            }
                        }

                        // Task 46: a client that can inflate says so (FrameDeflater).
                        // Anything else, or nothing, is the plain JSON text every
                        // existing client expects.
                        compressFrames = handshakeDocument.RootElement.TryGetProperty(FrameDeflater.HandshakeProperty, out JsonElement compressElement)
                            && compressElement.ValueKind == JsonValueKind.String
                            && string.Equals(compressElement.GetString(), FrameDeflater.HandshakeValue, StringComparison.Ordinal);

                        if (!PacketJsonCodec.TryRead(handshakeDocument.RootElement, out authPacket, out string? readError))
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, $"Malformed handshake: {readError}", CancellationToken.None);
                            return;
                        }
                    }

                    useJsonProtocol = true;
                }
                else if (result.MessageType == WebSocketMessageType.Binary && result.Count >= Marshal.SizeOf<AuthHandshakePacket>())
                {
                    authPacket = ParseAuthHandshakePacket(buffer, result.Count);
                }
                else
                {
                    await socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Expected Auth Handshake Packet", CancellationToken.None);
                    return;
                }

                {
                    string jwtToken = ExtractJwtToken(ref authPacket);

                    JwtValidationResult validation = AuthenticationEngine.ValidateJwt(jwtToken, _jwtSecretKey);
                    if (!validation.IsValid)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid or expired token", CancellationToken.None);
                        return;
                    }

                    if (!await IsNonceCurrentAsync(validation.AccountId, validation.SessionNonce))
                    {
                        // Modul: must contain "token" (case-insensitive) - the
                        // client's interpretClose (connection.ts) classifies a
                        // 1008 close as a dead session only when the reason
                        // matches /token/i. Without that word here, a revoked
                        // token reads as a transient drop and the client
                        // reconnects with the same (still-revoked) token forever.
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Session revoked - token no longer valid", CancellationToken.None);
                        return;
                    }

                    long resolvedPlayerId = await ResolvePlayerIdFromAccountIdAsync(validation.AccountId);
                    if (resolvedPlayerId <= 0)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unknown account", CancellationToken.None);
                        return;
                    }

                    playerId = resolvedPlayerId;

                    if (await IsPlayerBlacklistedAsync(playerId))
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Account blacklisted", CancellationToken.None);
                        return;
                    }

                    // Modul: force-acquire always succeeds and publishes an
                    // eviction notice (see RedisPlayerSessionLock.
                    // ForceAcquireAndEvictAsync) rather than the old
                    // TryAcquireAsync, which rejected a NEW connection outright
                    // whenever an old lock was still held - a successful JWT
                    // handshake is a deliberate, authenticated act of claiming
                    // this account's single live session, so it always wins
                    // against whatever connection existed before it, closing
                    // the multi-boxing exploit this task's Part 2 exists to fix.
                    if (_redisSessionLock != null)
                    {
                        redisLockToken = await _redisSessionLock.ForceAcquireAndEvictAsync(playerId);

                        lockRenewalCts = new CancellationTokenSource();
                        lockRenewalTask = RunRedisLockRenewalAsync(playerId, redisLockToken, lockRenewalCts.Token);
                    }

                    if (!ClientCommandValidator.ValidateAssetIntegrity(authPacket.AssetHash, authPacket.PlatformSignature, playerId))
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Asset Integrity Failure", CancellationToken.None);
                        return;
                    }

                    // Modul: same-pod eviction complements the cross-pod Redis
                    // Pub/Sub eviction above - if this exact pod already holds
                    // the stale connection for this account (the common case
                    // for a simple reconnect), it is force-disconnected here
                    // immediately rather than waiting on the eviction message
                    // this same handshake just published to itself.
                    if (_connectedClients.TryRemove(playerId, out var staleSession))
                    {
                        if (staleSession.Socket.State == WebSocketState.Open)
                        {
                            _ = staleSession.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Superseded by a new login", CancellationToken.None)
                                .ContinueWith(_logSendFault, playerId, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                        }
                    }

                    _connectedClients[playerId] = new WebSocketSession(socket, redisLockToken ?? string.Empty, useJsonProtocol, compressFrames);
                    CommandQueue.Enqueue(new PlayerCommand { PlayerId = playerId, Packet = new ClientCommandPacket { Command = CommandType.Login, TargetId = playerId } });
                }

                if (!_connectedClients.TryGetValue(playerId, out session)) return;

                while (socket.State == WebSocketState.Open)
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await session.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
                        break;
                    }

                    // Modul: JSON WebSocket mode, 2026-08-02. A JSON session
                    // demultiplexes on the "type" discriminator; the binary
                    // branches below still demultiplex on exact byte length
                    // (see NetworkPacketLayoutGuard), unchanged. The two
                    // never mix: a session picked one at handshake time, and
                    // a frame of the other kind is simply not this
                    // connection's protocol.
                    if (session.UseJsonProtocol)
                    {
                        if (result.MessageType != WebSocketMessageType.Text)
                        {
                            continue;
                        }

                        byte[]? messageJson = await ReadTextMessageAsync(socket, buffer, result, CancellationToken.None);
                        if (messageJson == null)
                        {
                            await session.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", CancellationToken.None);
                            break;
                        }

                        if (!PacketJsonCodec.TryParseEnvelope(messageJson, out JsonDocument? document, out string messageType, out string? parseError))
                        {
                            // Malformed input is dropped, not a disconnect -
                            // the same treatment a rejected chat message
                            // gets, and the same reason: a client bug should
                            // not look like a flood infraction. A real flood
                            // is still caught by the token bucket below.
                            Console.WriteLine($"JSON packet from player {playerId} rejected: {parseError}");
                            continue;
                        }

                        bool flooded = false;
                        using (document)
                        {
                            if (messageType == PacketJsonCodec.TypeRequestChatMessage)
                            {
                                if (ChatEngine.TryConsumeChatToken(ref session.ChatTokenBucket) &&
                                    PacketJsonCodec.TryRead(document!.RootElement, out RequestChatMessagePacket jsonChatRequest, out _))
                                {
                                    DispatchInboundChatRequest(playerId, session, ref jsonChatRequest);
                                }
                            }
                            else if (messageType == PacketJsonCodec.TypeClientCommand)
                            {
                                if (PacketJsonCodec.TryRead(document!.RootElement, out ClientCommandPacket jsonCommand, out _))
                                {
                                    flooded = !ValidateAndEnqueue(ref jsonCommand, playerId, session);
                                }
                            }
                            else
                            {
                                // Includes the three server-to-client types.
                                // A client sending one of those is confused,
                                // not hostile.
                                Console.WriteLine($"JSON packet from player {playerId} has unroutable type '{messageType}'.");
                            }
                        }

                        if (flooded)
                        {
                            Interlocked.Increment(ref _throttledCounter);
                            if (socket.State == WebSocketState.Open)
                            {
                                await session.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Packet flood", CancellationToken.None);
                            }
                            break;
                        }

                        continue;
                    }

                    if (result.MessageType == WebSocketMessageType.Binary && result.Count == Marshal.SizeOf<RequestChatMessagePacket>())
                    {
                        // Modul: a rejected chat message (rate limited or
                        // invalid content) is silently dropped, never a
                        // disconnect-worthy event - spam is normal,
                        // recoverable user behavior, unlike the structural
                        // packet-flood violation the branch below guards.
                        if (ChatEngine.TryConsumeChatToken(ref session.ChatTokenBucket))
                        {
                            var chatRequest = MemoryMarshal.Read<RequestChatMessagePacket>(new ReadOnlySpan<byte>(buffer, 0, result.Count));
                            DispatchInboundChatRequest(playerId, session, ref chatRequest);
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary && result.Count >= Marshal.SizeOf<ClientCommandPacket>())
                    {
                        if (ParseValidateAndEnqueue(buffer, result.Count, playerId, session))
                        {
                        }
                        else
                        {
                            Interlocked.Increment(ref _throttledCounter);
                            if (socket.State == WebSocketState.Open)
                            {
                                await session.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Packet flood", CancellationToken.None);
                            }
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout during handshake - session may not exist yet if
                // this fired before registration (the common case), so
                // fall back to closing the raw socket directly; nothing
                // else can be racing an unregistered socket.
                if (socket.State == WebSocketState.Open)
                {
                    if (session != null)
                    {
                        await session.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Handshake timeout", CancellationToken.None);
                    }
                    else
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Handshake timeout", CancellationToken.None);
                    }
                }
            }
            catch (Exception)
            {
                // Disconnected abruptly
            }
            finally
            {
                // Modul: the receive loop is over, so this session's outbox
                // writer has nobody left to write to - stop it rather than
                // leave it parked on its signal.
                session?.Shutdown();

                if (playerId != 0)
                {
                    _connectedClients.TryRemove(playerId, out _);
                    CommandQueue.Enqueue(new PlayerCommand { PlayerId = playerId, Packet = new ClientCommandPacket { Command = CommandType.Logout, TargetId = playerId } });
                    if (lockRenewalCts != null)
                    {
                        lockRenewalCts.Cancel();
                    }

                    if (lockRenewalTask != null)
                    {
                        try
                        {
                            await lockRenewalTask;
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }

                    if (_redisSessionLock != null && redisLockToken != null)
                    {
                        await _redisSessionLock.ReleaseAsync(playerId, redisLockToken);
                    }
                }
                lockRenewalCts?.Dispose();
                socket.Dispose();
            }
        }

        private async Task RunRedisLockRenewalAsync(long playerId, string token, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                if (_redisSessionLock == null)
                {
                    return;
                }

                bool renewed = await _redisSessionLock.RenewAsync(playerId, token);
                if (!renewed)
                {
                    ForceDisconnect(playerId);
                    return;
                }
            }
        }

        public void SendToPlayer(long playerId, ref StateUpdatePacket packet)
        {
            if (!_connectedClients.TryGetValue(playerId, out var session) || session.Socket.State != WebSocketState.Open)
            {
                return;
            }

            // Modul: a wedged socket is evicted, not written to forever.
            //
            // The session's writer sets IsWedged when a frame times out, which
            // means the peer has stopped reading altogether. Left alone that
            // connection stays open and silent - the state the freeze report
            // describes, where the server simulates correctly and the screen
            // does not move. Dropping it gives the client something to react
            // to, and its reconnect logic (500 ms backing off to 15 s, token
            // re-sent) then does the rest.
            if (session.IsWedged)
            {
                Console.WriteLine($"Evicting wedged socket for player {playerId}: sends stopped completing.");
                ForceDisconnect(playerId);
                return;
            }

            // Modul: task 41. The snapshot is OFFERED, never sent from here -
            // this runs once per online player per 10Hz tick and must not
            // block the tick. The outbox keeps only the latest snapshot (an
            // absolute state frame supersedes any older one still waiting) and
            // the session's writer sends it after any queued events. Send
            // faults are logged by the writer itself.
            //
            // Modul: task 46 (8a - measure). The Stopwatch wraps ONLY
            // SerializeToUtf8, not the OfferSnapshot call below it, so the
            // recorded microseconds are the Utf8JsonWriter cost alone and not
            // contaminated by the outbox's (cheap, non-blocking) enqueue.
            // See StateFrameMetrics above for what this feeds on /metrics.
            if (session.UseJsonProtocol)
            {
                long serializeStartTimestamp = Stopwatch.GetTimestamp();
                byte[] json = PacketJsonCodec.SerializeToUtf8(ref packet);
                long serializeElapsedUs = (Stopwatch.GetTimestamp() - serializeStartTimestamp) * 1_000_000L / Stopwatch.Frequency;
                StateFrameMetrics.RecordJsonFrame(json.Length, serializeElapsedUs);
                session.OfferSnapshot(json, WebSocketMessageType.Text);
            }
            else
            {
                // A rented copy per offer, not a shared per-session buffer -
                // see OfferStateFrame for the spliced-frame trap. The size is
                // a compile-time constant for this unmanaged struct, so
                // recording it costs nothing worth timing.
                StateFrameMetrics.RecordBinaryFrame(Unsafe.SizeOf<StateUpdatePacket>());
                session.OfferStateFrame(ref packet);
            }
        }

        // Modul: Full-Stack Production Hardening Phase 3, Part 2. Static,
        // non-capturing continuation - replaces the previous
        // async Task ObserveSendFault(Task, long) wrapper, which allocated
        // a Task plus a boxed async state machine on every call once its
        // await suspended past a not-synchronously-completing SendAsync.
        // SendToPlayer runs once per online player every 10Hz tick - at 100
        // concurrent players that was on the order of 1000 heap
        // allocations/sec purely for fire-and-forget fault observation, on
        // the single hottest path in the codebase. ContinueWith schedules
        // against the antecedent Task's own completion list rather than
        // building a new async state machine, so no continuation-class
        // allocation occurs here; the only remaining cost is boxing
        // playerId (a long) into the object state parameter, unavoidable
        // with this Task-based API without a custom awaitable. Reading
        // t.Exception inside the callback also explicitly marks the
        // antecedent's exception as observed, preventing an
        // UnobservedTaskException on finalization.
        //
        // Test-only observability note: internal (not private) via
        // InternalsVisibleTo("FolkIdle.Server.Tests") so
        // Test_NetworkBroadcastSystem_ObserveSendFault_ZeroAllocation can
        // invoke this exact delegate directly and measure
        // GC.GetAllocatedBytesForCurrentThread() around it.
        //
        // Modul: the `static` lambda modifier makes the compiler verify
        // and enforce zero captures at compile time (an accidental
        // capture here becomes CS8927, not a runtime surprise). Note this
        // does not make delegate.Target null - the C# compiler still
        // binds even a `static` lambda to a method on a per-type cached
        // singleton display class (<>c), and Target ends up pointing at
        // that stateless singleton - but the singleton itself is
        // allocated at most once (lazily, on first use) and reused for
        // every subsequent invocation, so this field is still assigned
        // exactly once at class load and never re-created per call, which
        // is the actual zero-per-call-allocation property being relied on
        // here (see the delegate-identity and allocation-delta assertions
        // in the corresponding test).
        internal static readonly Action<Task, object?> _logSendFault = static (t, state) =>
        {
            long playerId = (long)state!;
            Console.WriteLine($"State broadcast send failed for player {playerId}: {t.Exception?.GetBaseException().Message}");
        };

        private static long _forcedDisconnectsTotal;
        internal static long ForcedDisconnectsTotal => Interlocked.Read(ref _forcedDisconnectsTotal);

        // Modul: A KICK MUST SAY WHO KICKED. Some fifty sites end a session
        // through here (every validator refusal via TerminateSessionForSecurity,
        // the epoch gate, anti-cheat, market/forge/guild rollbacks...) and none
        // of them logged, so "the phone relogs a few seconds after login" had
        // no server-side trace at all - the close reason is one shared string.
        // TerminateSessionForSecurity reaches the coordinators as an
        // Action<long>, so [CallerMemberName] cannot see through it; a short
        // stack walk can, and a kick is rare enough that its cost is nothing.
        // The line names the handler (e.g. ClientSessionTickCoordinator.
        // HandleSwitchLanguage) - grep the app log for "[kick]".
        internal static string DescribeKickOrigin(int skipFrames)
        {
            var frames = new StackTrace(skipFrames + 1, false).GetFrames();
            var names = frames
                .Select(f => f.GetMethod())
                .Where(m => m?.DeclaringType?.Namespace?.StartsWith("FolkIdle", StringComparison.Ordinal) == true)
                .Select(m => $"{m!.DeclaringType!.Name}.{m.Name}")
                .Where(n => !n.StartsWith("SimulationEngine.TerminateSessionForSecurity", StringComparison.Ordinal))
                .Distinct()
                .Take(4);
            return string.Join(" <- ", names);
        }

        public void ForceDisconnect(long playerId)
        {
            Interlocked.Increment(ref _forcedDisconnectsTotal);
            Console.WriteLine($"[kick] player {playerId} force-disconnected by {DescribeKickOrigin(1)}");
            if (_connectedClients.TryRemove(playerId, out var session))
            {
                if (_redisSessionLock != null && !string.IsNullOrEmpty(session.RedisLockToken))
                {
                    _ = _redisSessionLock.ReleaseAsync(playerId, session.RedisLockToken);
                }

                // Modul: the close reason must satisfy the client's /token/i
                // check (connection.ts's interpretClose) or the client reads
                // this as a transient drop and reconnects with the very same
                // token forever, showing a misleading "stale LogicEpochCounter"
                // message instead of the login screen. EVERY caller of
                // ForceDisconnect - blacklist, anti-cheat, epoch violations,
                // cross-pod "superseded by a new login", and the new session
                // revocation path - wants the same outcome: stop, don't retry
                // with this token. Keep the word "token" in this string.
                session.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Violent termination - token no longer valid", CancellationToken.None)
                    .ContinueWith(_logSendFault, playerId, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
        }

        // Modul: no-op under the JWT scheme - there is no server-side token
        // cache to purge anymore (a JWT is self-verifying and stateless; it
        // remains cryptographically valid until it naturally expires).
        // Retained only so the ~10 existing SimulationEngine call sites that
        // pair this with ForceDisconnect on a validation failure need no
        // changes - ForceDisconnect is what actually terminates the
        // connection at each of those sites; this call was never anything
        // more than a companion cleanup step even under the old scheme.
        public void PurgeTokensForPlayer(long playerId)
        {
        }

        public async Task DisconnectAllClientsGracefullyAsync()
        {
            var tasks = new System.Collections.Generic.List<Task>();
            var sockets = new System.Collections.Generic.List<WebSocket>();
            foreach (var kvp in _connectedClients)
            {
                var socket = kvp.Value.Socket;
                if (socket.State == WebSocketState.Open)
                {
                    sockets.Add(socket);
                    tasks.Add(kvp.Value.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None));
                }
            }

            if (tasks.Count == 0)
                return;

            var whenAllTask = Task.WhenAll(tasks);
            var timeoutTask = Task.Delay(2000);

            if (await Task.WhenAny(whenAllTask, timeoutTask) == timeoutTask)
            {
                foreach (var socket in sockets)
                {
                    if (socket.State != WebSocketState.Closed && socket.State != WebSocketState.Aborted)
                    {
                        socket.Abort();
                    }
                }
            }
        }

        private sealed class DevWorldBossWindowRequest
        {
            public bool Open { get; set; }
            public long DurationSeconds { get; set; } = 900;
        }

        private class AdminMailRequest
        {
            public string? TargetUsername { get; set; }
            public string? BaseItemId { get; set; }
            public int QualityTier { get; set; }
            public int Quantity { get; set; }
            public long Gold { get; set; }
            public int Diamonds { get; set; }
            public string? TitleSlug { get; set; }
            public string? SenderName { get; set; }
            public string? MessageText { get; set; }
            public bool SendEmail { get; set; }
            public string? EmailSubject { get; set; }
        }
            private async Task HandleGuildDepot(HttpListenerContext context)
        {
            try
            {
                long requesterId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (requesterId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.PlayerId == requesterId);
                if (member == null)
                {
                    context.Response.StatusCode = 403;
                    return;
                }

                var depotItems = await db.GuildDepotBalances.Where(d => d.GuildId == member.GuildId).ToListAsync();
                var activeBuffs = await db.GuildActiveBuffs.Where(b => b.GuildId == member.GuildId && b.ExpiresAt > GameClock.UtcNow).ToListAsync();
                var members = await db.GuildMembers
                    .Where(m => m.GuildId == member.GuildId)
                    .Join(db.PlayerRecords, m => m.PlayerId, p => p.Id, (m, p) => new { m, p })
                    .OrderByDescending(x => x.m.WeeklyContributionPoints)
                    .Select(x => new { PlayerId = x.m.PlayerId, Name = x.p.Username, WeeklyContributionPoints = x.m.WeeklyContributionPoints })
                    .ToListAsync();
                    
                var guildRecord = await db.GuildRecords.FirstOrDefaultAsync(g => g.Id == member.GuildId);

                var depotByBaseId = depotItems
                    .Select(d => new { BaseId = ContentRegistry.GetItemBaseId(d.ItemDefinitionId), d.Quantity })
                    .Where(x => !string.IsNullOrEmpty(x.BaseId))
                    .ToDictionary(x => x.BaseId, x => x.Quantity);

                var responseObj = new {
                    Balances = depotItems,
                    DepotByBaseId = depotByBaseId,
                    ActiveBuffs = activeBuffs,
                    Leaderboard = members,
                    GuildGold = guildRecord?.GuildTreasuryGold ?? 0,
                    ExpiresAtEpoch = 0
                };

                byte[] responseBytes = JsonSerializer.SerializeToUtf8Bytes(responseObj);
                context.Response.ContentType = "application/json";
                context.Response.StatusCode = 200;
                await context.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling depot: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }
}
}
