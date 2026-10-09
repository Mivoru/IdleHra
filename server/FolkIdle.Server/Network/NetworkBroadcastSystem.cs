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
                || requestPath == "/api/v1/worldboss/practice/score")
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

            // Modul: UI audit follow-up. DailyLoginRewardEngine
            // already grants a real, server-authoritative streak
            // reward on every login/register, but the result was
            // discarded (awaited, never returned to the client) -
            // the player had no way to see their streak or today's
            // reward. Read-only snapshot, mirrors HandleMasterySnapshot.
            if (requestPath == "/api/v1/login-bonus/state" && context.Request.HttpMethod == "GET")
            {
                await HandleLoginBonusState(context);
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

        private sealed class LoginBonusStateResponse
        {
            public int CurrentStreakDay { get; set; }
            public bool CreditedToday { get; set; }
            public long[] WeeklyGoldSchedule { get; set; } = Array.Empty<long>();
            public int Day7DiamondBonus { get; set; }
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

        // Modul: task 102, "My orders". It was read-only while the book had no
        // cancel for equipment; POST /api/v1/market/cancel (below) is that
        // cancel now, and each row here carries the OrderId it takes.
        private async Task HandleMarketOwnOrders(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var orders = await MarketOrderBookEngine.FetchOwnOpenOrdersAsync(db, playerId);

                var rows = new System.Collections.Generic.List<MarketOwnOrderResponse>(orders.Count);
                foreach (var order in orders)
                {
                    rows.Add(new MarketOwnOrderResponse
                    {
                        OrderId = order.Id,
                        OrderType = order.OrderType,
                        BaseItemId = order.BaseItemId,
                        QualityTier = order.QualityTier,
                        Price = order.Price,
                        CreatedAtEpoch = order.CreatedAtEpoch,
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, rows);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Market own orders error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: CANCEL ONE OF MY ORDERS. REST, like the cosmetic market's
        // cancel and the My orders read beside it - not a wire command,
        // because nothing it changes is tick state: the piece and the gold
        // both live in rows, so there is nothing to flush first. A refusal
        // answers 200 with its Result (the "silent rollback" rule); only a
        // malformed body is a 400. On success the session reloads, which
        // flushes and then reads the refunded gold row back - the
        // MarketMatchQueue display credit is NOT used as well, or the
        // header would show a refund twice.
        private async Task HandleMarketCancelOrder(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                long orderId;
                try
                {
                    using var parsed = JsonDocument.Parse(await ReadBodyAsync(context));
                    if (!parsed.RootElement.TryGetProperty("OrderId", out var idEl)
                        || !idEl.TryGetInt64(out orderId)
                        || orderId <= 0)
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }
                }
                catch (JsonException)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var outcome = await FolkIdle.Server.Domain.Economy.MarketEscrowEngine.CancelOrderAsync(db, playerId, orderId);

                if (outcome.Result == FolkIdle.Server.Domain.Economy.MarketCancelResult.Ok)
                {
                    CommandQueue.Enqueue(new PlayerCommand
                    {
                        PlayerId = playerId,
                        Packet = new ClientCommandPacket { Command = CommandType.ReloadState }
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new
                {
                    Result = outcome.Result.ToString(),
                    outcome.ReturnedEquipmentId,
                    outcome.RefundedGold,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Market cancel error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private sealed class MarketListingResponse
        {
            public long OrderId { get; set; }
            public string BaseItemId { get; set; } = string.Empty;
            public int QualityTier { get; set; }
            public long Price { get; set; }
            public long CreatedAtEpoch { get; set; }
        }

        // Modul 40: marketplace browser page. Uses the authenticated HTTP
        // snapshot pattern established by HandleForgeInventorySnapshot /
        // HandleGuildLogisticsSnapshot / HandleGlobalLeaderboard for
        // variable-length, on-demand player data rather than a fixed-layout
        // WebSocket packet - a paginated result set has no natural fixed
        // size, so it does not fit StateUpdatePacket's binary layout the way
        // scalar per-tick fields do.
        /// <summary>
        /// Parses "1,3,5" into a bounded set, ignoring anything out of range or
        /// unparseable rather than rejecting the whole request - a stale client
        /// sending a slot index that no longer exists (the offhand was 6) should
        /// see an unfiltered market, not an error.
        /// </summary>
        private static System.Collections.Generic.HashSet<int> ParseIdSet(string? raw, int min, int max)
        {
            var parsed = new System.Collections.Generic.HashSet<int>();
            if (string.IsNullOrWhiteSpace(raw)) return parsed;

            foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out int value) && value >= min && value <= max)
                {
                    parsed.Add(value);
                }
            }

            return parsed;
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

        /// <summary>
        /// Price history for one item at one rarity.
        ///
        /// Reads historical_market_archives, which has recorded every completed
        /// trade with BaseItemId, QualityTier, ExecutionPrice and a millisecond
        /// timestamp since the market shipped - so this is retroactively
        /// correct for trades that already happened, and needs no new table.
        /// (The handoff said no completed trade was recorded anywhere; the
        /// archive is written by both MarketEscrowEngine and
        /// MarketOrderBookEngine.)
        /// </summary>
        private async Task HandleMarketPriceHistory(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                string baseItemId = query["baseItemId"] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(baseItemId) || baseItemId.Length > 255)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                if (!int.TryParse(query["qualityTier"], out int qualityTier))
                {
                    qualityTier = -1; // every rarity
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                // Milliseconds - ExecutionTimestampEpoch is written with
                // ToUnixTimeMilliseconds, and comparing it against seconds
                // would silently place every trade in the future.
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                const long DayMs = 86_400_000L;
                long windowStartMs = nowMs - (DayMs * 30L);

                var trades = await db.HistoricalMarketArchives
                    .AsNoTracking()
                    .Where(a => a.BaseItemId == baseItemId
                        && (qualityTier < 0 || a.QualityTier == qualityTier)
                        && a.ExecutionPrice > 0
                        && a.ExecutionTimestampEpoch >= windowStartMs)
                    .OrderBy(a => a.ExecutionTimestampEpoch)
                    .Select(a => new MarketPricePointResponse { Epoch = a.ExecutionTimestampEpoch, Price = a.ExecutionPrice })
                    .ToListAsync();

                var response = new MarketPriceHistoryResponse
                {
                    BaseItemId = baseItemId,
                    QualityTier = qualityTier,
                    Points = trades,
                    TradeCount = trades.Count
                };

                if (trades.Count > 0)
                {
                    long sum = 0L;
                    long low = long.MaxValue;
                    long high = long.MinValue;
                    for (int i = 0; i < trades.Count; i++)
                    {
                        long price = trades[i].Price;
                        sum += price;
                        if (price < low) low = price;
                        if (price > high) high = price;
                    }

                    response.LastPrice = trades[^1].Price;
                    response.AveragePrice = sum / trades.Count;
                    response.LowPrice = low;
                    response.HighPrice = high;

                    response.ChangeDayPct = await ComputeChangePctAsync(db, baseItemId, qualityTier, response.LastPrice, nowMs - DayMs);
                    response.ChangeWeekPct = await ComputeChangePctAsync(db, baseItemId, qualityTier, response.LastPrice, nowMs - (DayMs * 7L));
                    response.ChangeMonthPct = await ComputeChangePctAsync(db, baseItemId, qualityTier, response.LastPrice, nowMs - (DayMs * 30L));
                }

                // The seller's own numbers. Mirrors MarketEscrowEngine's
                // brackets exactly - if those move, this must move with them.
                long sellerWealth = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId && c.ItemId == "gold")
                    .Select(c => c.Quantity)
                    .FirstOrDefaultAsync();

                response.FeePct = sellerWealth > 5_000_000 ? 15 : sellerWealth >= 500_000 ? 8 : 5;

                long guildId = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.GuildId)
                    .FirstOrDefaultAsync();

                if (guildId > 0)
                {
                    int taxRatePct = await db.GuildRecords
                        .AsNoTracking()
                        .Where(g => g.Id == guildId)
                        .Select(g => g.TaxRatePct)
                        .FirstOrDefaultAsync();

                    response.GuildTaxPct = Math.Clamp(taxRatePct, GuildRecord.MinTaxRatePct, GuildRecord.MaxTaxRatePct);
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Market price history error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// Change from the last trade before <paramref name="sinceMs"/> to now.
        ///
        /// Null rather than zero when nothing traded before the window opened.
        /// A market with three days of history has no honest month-over-month
        /// figure, and "0%" would claim it does - that is the difference
        /// between "unchanged" and "unknown", and a price chart that confuses
        /// them is worse than one that omits the number.
        /// </summary>
        private static async Task<double?> ComputeChangePctAsync(FolkIdleDbContext db, string baseItemId, int qualityTier, long lastPrice, long sinceMs)
        {
            long baseline = await db.HistoricalMarketArchives
                .AsNoTracking()
                .Where(a => a.BaseItemId == baseItemId
                    && (qualityTier < 0 || a.QualityTier == qualityTier)
                    && a.ExecutionPrice > 0
                    && a.ExecutionTimestampEpoch < sinceMs)
                .OrderByDescending(a => a.ExecutionTimestampEpoch)
                .Select(a => a.ExecutionPrice)
                .FirstOrDefaultAsync();

            if (baseline <= 0L) return null;

            return (lastPrice - baseline) * 100.0 / baseline;
        }

        private async Task HandleMarketBrowserListings(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                string baseItemId = query["baseItemId"] ?? string.Empty;
                int.TryParse(query["pageIndex"], out int pageIndex);
                if (!int.TryParse(query["pageSize"], out int pageSize))
                {
                    pageSize = 24;
                }

                // Modul: EVERY FILTER IS OPTIONAL NOW.
                //
                // This endpoint used to 400 without an exact BaseItemId, and
                // matched QualityTier exactly as well - so the only question a
                // player could ask was "is this precise item at this precise
                // rarity for sale", which nobody can ask about a marketplace
                // they have not seen. No filters at all is the default and it
                // returns the whole book, paginated.
                // Modul: a comma-separated SET of slots and of region tiers,
                // because the filter is a row of checkboxes rather than a
                // dropdown - "helmets, chests and leggings" is one question, and
                // a single-value parameter made it three round trips.
                //
                // `slotIndex` (singular) is still accepted so an older client
                // keeps working; it simply becomes a set of one.
                var slotIndices = ParseIdSet(query["slotIndexes"] ?? query["slotIndex"], 0, EquipmentSlotEngine.SlotCount - 1);
                var regionTiers = ParseIdSet(query["tiers"], 1, ContentRegistry.LocationCount);

                if (!int.TryParse(query["minQualityTier"], out int minQualityTier))
                {
                    minQualityTier = 0;
                }

                if (!int.TryParse(query["maxQualityTier"], out int maxQualityTier))
                {
                    maxQualityTier = ForgeSplicingEngine.MaxQualityTier;
                }

                string sortBy = query["sortBy"] ?? "price";
                bool descending = query["descending"] == "1" || string.Equals(query["descending"], "true", StringComparison.OrdinalIgnoreCase);

                if (!ClientCommandValidator.ValidateMarketBrowserQuery(playerId, pageIndex, pageSize))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                if (minQualityTier < 0) minQualityTier = 0;
                if (maxQualityTier > ForgeSplicingEngine.MaxQualityTier) maxQualityTier = ForgeSplicingEngine.MaxQualityTier;
                if (maxQualityTier < minQualityTier) maxQualityTier = minQualityTier;
                if (sortBy != "price" && sortBy != "rarity" && sortBy != "name") sortBy = "price";

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                bool isQuarantined = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.IsQuarantined || p.Quarantine_Active)
                    .SingleOrDefaultAsync();

                var page = await MarketOrderBookEngine.BrowseActiveListingsAsync(db, new MarketOrderBookEngine.MarketBrowseQuery
                {
                    BaseItemId = baseItemId,
                    SlotIndices = slotIndices,
                    RegionTiers = regionTiers,
                    MinQualityTier = minQualityTier,
                    MaxQualityTier = maxQualityTier,
                    IsQuarantined = isQuarantined,
                    PageIndex = pageIndex,
                    PageSize = pageSize,
                    SortBy = sortBy,
                    Descending = descending,
                });

                var rows = new System.Collections.Generic.List<MarketListingResponse>(page.Listings.Count);
                for (int i = 0; i < page.Listings.Count; i++)
                {
                    rows.Add(new MarketListingResponse
                    {
                        OrderId = page.Listings[i].Id,
                        BaseItemId = page.Listings[i].BaseItemId,
                        QualityTier = page.Listings[i].QualityTier,
                        Price = page.Listings[i].Price,
                        CreatedAtEpoch = page.Listings[i].CreatedAtEpoch
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                // An envelope rather than a bare array: without TotalCount the
                // browser cannot draw a pager, and "did I reach the end" is not
                // answerable from a full page.
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new MarketBrowseResponse
                {
                    Listings = rows,
                    TotalCount = page.TotalCount,
                    PageIndex = pageIndex,
                    PageSize = pageSize,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Market browser listings error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleGlobalLeaderboard(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                bool isQuarantined = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.IsQuarantined || p.Quarantine_Active)
                    .SingleOrDefaultAsync();

                System.Collections.Generic.List<LeaderboardEntryResponse> entries = new();
                if (isQuarantined)
                {
                    entries = BuildSpoofedLeaderboard(playerId);
                }
                else
                {
                    int skip = 0;
                    int take = 50;
                    var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                    if (int.TryParse(query["skip"], out int parsedSkip)) skip = parsedSkip;
                    if (int.TryParse(query["take"], out int parsedTake)) take = parsedTake;

                    if (!ClientCommandValidator.ValidateLeaderboardQuery(playerId, skip, take))
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    var dbRedis = _serviceProvider.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>().GetDatabase();
                    var redisEntries = await dbRedis.SortedSetRangeByRankWithScoresAsync("leaderboard:mastery", skip, skip + take - 1, StackExchange.Redis.Order.Descending);

                    // How many players are ranked in total, which is what
                    // decides whether a tier pays anything at all - see the
                    // population floor in LeaderboardTierRegistry. Read here so
                    // the board can show an HONEST reward next to each rank
                    // instead of a headline figure the payout would refuse.
                    long rankedPopulation = await dbRedis.SortedSetLengthAsync("leaderboard:mastery");

                    var playerIds = redisEntries.Select(e => (long)e.Element).ToList();
                    
                    var players = await db.PlayerRecords
                        .AsNoTracking()
                        .Where(p => playerIds.Contains(p.Id))
                        .ToDictionaryAsync(p => p.Id);

                    // Modul: THE BOARD RANKS BY PROGRESS, so it has to SHOW
                    // progress. The rank order is level, then the hardest
                    // monster ever put down, then kills of it - and a board
                    // that sorts by something it does not display is a board
                    // whose order looks arbitrary.
                    //
                    // Read back out of the composite score rather than
                    // re-queried: the score IS the ranking inputs packed
                    // together (LeaderboardCronEngine.CompositeScore), so
                    // unpacking it cannot disagree with the order.
                    var progressByPlayer = new System.Collections.Generic.Dictionary<long, (int Hardest, int Kills)>();
                    foreach (var entry in redisEntries)
                    {
                        long packed = (long)entry.Score;
                        progressByPlayer[(long)entry.Element] = (
                            (int)((packed / 1_000_000L) % 10_000L),
                            (int)(packed % 1_000_000L));
                    }

                    for (int i = 0; i < redisEntries.Length; i++)
                    {
                        long pId = (long)redisEntries[i].Element;
                        if (players.TryGetValue(pId, out var p))
                        {
                            entries.Add(new LeaderboardEntryResponse
                            {
                                Rank = skip + i + 1,
                                PlayerId = p.Id,

                                // Modul: leaderboard names, 2026-08-01. Was the
                                // literal string "Player" for every row, so the
                                // entire global leaderboard read as fifty
                                // identical entries and could not tell anyone
                                // apart - while PlayerRecords."Username" sat on
                                // the very record already loaded into this
                                // dictionary two lines up.
                                //
                                // Username is nullable (accounts created before
                                // it existed), so it falls back to the id rather
                                // than rendering an empty row.
                                DisplayName = string.IsNullOrWhiteSpace(p.Username) ? $"Player #{p.Id}" : p.Username!,
                                Level = p.CurrentLevel,
                                Xp = p.CurrentXp,
                                HardestMonsterId = progressByPlayer.TryGetValue(p.Id, out var progress) ? progress.Hardest : 0,
                                HardestMonsterName = progressByPlayer.TryGetValue(p.Id, out var named) && named.Hardest > 0
                                    ? ContentRegistry.GetMonsterName(named.Hardest)
                                    : string.Empty,
                                KillsOfHardest = progressByPlayer.TryGetValue(p.Id, out var killed) ? killed.Kills : 0,

                                // Resolved from the ONE table that also decides
                                // the payout, so the colour a player sees and
                                // the diamonds they receive come from the same
                                // place. WeeklyDiamonds is what this rank earns
                                // AT THE CURRENT POPULATION - so on a small
                                // server it honestly reads 0 rather than
                                // advertising a prize the floor will refuse.
                                TierId = LeaderboardTierRegistry.TierIdForRank(skip + i + 1),
                                TierName = LeaderboardTierRegistry.TierForRank(skip + i + 1)?.Name ?? string.Empty,
                                WeeklyDiamonds = LeaderboardTierRegistry.WeeklyDiamondsFor(skip + i + 1, (int)rankedPopulation)
                            });
                        }
                    }
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, entries);
            }
            catch (StackExchange.Redis.RedisException ex)
            {
                // Redis is an optional dependency everywhere else in this
                // server (Program.cs: "session locking, write-behind, and
                // telemetry streaming simply no-op" without one) - the
                // leaderboard ZSET is exactly that kind of write-behind
                // cache, so an unreachable Redis should degrade to "no
                // ranked data yet", not a 500 that looks like a real server
                // bug to the client.
                Console.WriteLine($"Leaderboard unavailable (Redis): {ex.Message}");
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new System.Collections.Generic.List<LeaderboardEntryResponse>());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Leaderboard error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: Comprehensive Game System Audit, Part 3.2. Global guild
        // leaderboard read endpoint - mirrors HandleGlobalLeaderboard's
        // exact shape (authenticated GET, skip/take pagination through
        // the same ValidateLeaderboardQuery bounds, Redis ZSET populated
        // by LeaderboardCronEngine.SyncGuildLeaderboardAsync, DB hydration
        // of display fields).
        private async Task HandleGuildLeaderboard(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                int skip = 0;
                int take = 50;
                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                if (int.TryParse(query["skip"], out int parsedSkip)) skip = parsedSkip;
                if (int.TryParse(query["take"], out int parsedTake)) take = parsedTake;

                if (!ClientCommandValidator.ValidateLeaderboardQuery(playerId, skip, take))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                var dbRedis = _serviceProvider.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>().GetDatabase();
                var redisEntries = await dbRedis.SortedSetRangeByRankWithScoresAsync("leaderboard:guilds", skip, skip + take - 1, StackExchange.Redis.Order.Descending);

                var guildIds = redisEntries.Select(e => (long)e.Element).ToList();

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var guilds = await db.GuildRecords
                    .AsNoTracking()
                    .Where(g => guildIds.Contains(g.Id))
                    .ToDictionaryAsync(g => g.Id);

                var entries = new System.Collections.Generic.List<GuildLeaderboardEntryResponse>(redisEntries.Length);
                for (int i = 0; i < redisEntries.Length; i++)
                {
                    long gId = (long)redisEntries[i].Element;
                    if (guilds.TryGetValue(gId, out var g))
                    {
                        entries.Add(new GuildLeaderboardEntryResponse
                        {
                            Rank = skip + i + 1,
                            GuildId = g.Id,
                            Name = g.Name,
                            GuildTier = g.CurrentTier,
                            GuildMMR = g.GuildMMR
                        });
                    }
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, entries);
            }
            catch (StackExchange.Redis.RedisException ex)
            {
                // See HandleGlobalLeaderboard's matching catch - Redis is an
                // optional write-behind cache everywhere else in this
                // server, so being unreachable should degrade to "no ranked
                // guild data yet", not a 500.
                Console.WriteLine($"Guild leaderboard unavailable (Redis): {ex.Message}");
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new System.Collections.Generic.List<GuildLeaderboardEntryResponse>());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild leaderboard error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private sealed class GuildLeaderboardEntryResponse
        {
            public int Rank { get; set; }
            public long GuildId { get; set; }
            public string Name { get; set; } = string.Empty;
            public int GuildTier { get; set; }
            public int GuildMMR { get; set; }
        }

        private static System.Collections.Generic.List<LeaderboardEntryResponse> BuildSpoofedLeaderboard(long playerId)
        {
            var entries = new System.Collections.Generic.List<LeaderboardEntryResponse>(50);
            uint seed = unchecked((uint)playerId) ^ 0xA5A5A5A5u;
            for (int i = 0; i < 50; i++)
            {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                entries.Add(new LeaderboardEntryResponse
                {
                    Rank = i + 1,
                    PlayerId = 900000000L + i,
                    DisplayName = "LocalRank",
                    Level = 100 - i,
                    Xp = 1000000L - (i * 2500L) + (seed % 1000)
                });
            }

            return entries;
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

        /// <summary>
        /// Task 79: GET /api/v1/player/gold-ledger. Spending by category over 7
        /// days, 30 days and since recording began, read from gold_spend_daily
        /// (GoldLedger). Sorted by the all-time amount, biggest sink first.
        /// Phase 2 adds income by source (gold_income_daily) and the material
        /// flow (material_flow_daily), each with its own "recorded since".
        /// </summary>
        private async Task HandleGoldLedger(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                long lifetime = await db.PlayerRecords.AsNoTracking()
                    .Where(p => p.Id == playerId).Select(p => p.LifetimeGoldSpent).FirstOrDefaultAsync();
                var rows = await db.GoldSpendDaily.AsNoTracking().Where(g => g.PlayerId == playerId).ToListAsync();

                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var response = new GoldLedgerResponse
                {
                    LifetimeSpent = lifetime,
                    RecordedSince = rows.Count == 0 ? null : rows.Min(r => r.Day).ToString("yyyy-MM-dd"),
                    Categories = rows
                        .GroupBy(r => r.Category)
                        .Select(g => new GoldLedgerCategoryResponse
                        {
                            Category = Enum.IsDefined(typeof(GoldSpendCategory), g.Key) ? ((GoldSpendCategory)g.Key).ToString() : $"Other{g.Key}",
                            Last7Days = g.Where(r => r.Day > today.AddDays(-7)).Sum(r => r.Amount),
                            Last30Days = g.Where(r => r.Day > today.AddDays(-30)).Sum(r => r.Amount),
                            SinceRecorded = g.Sum(r => r.Amount),
                        })
                        .OrderByDescending(c => c.SinceRecorded)
                        .ToList(),
                };

                // Task 79, phase 2: income by source, and the material flow.
                var incomeRows = await db.GoldIncomeDaily.AsNoTracking().Where(g => g.PlayerId == playerId).ToListAsync();
                response.IncomeRecordedSince = incomeRows.Count == 0 ? null : incomeRows.Min(r => r.Day).ToString("yyyy-MM-dd");
                response.Income = incomeRows
                    .GroupBy(r => r.Source)
                    .Select(g => new GoldLedgerCategoryResponse
                    {
                        Category = Enum.IsDefined(typeof(GoldIncomeSource), g.Key) ? ((GoldIncomeSource)g.Key).ToString() : $"Other{g.Key}",
                        Last7Days = g.Where(r => r.Day > today.AddDays(-7)).Sum(r => r.Amount),
                        Last30Days = g.Where(r => r.Day > today.AddDays(-30)).Sum(r => r.Amount),
                        SinceRecorded = g.Sum(r => r.Amount),
                    })
                    .OrderByDescending(c => c.SinceRecorded)
                    .ToList();

                var materialRows = await db.MaterialFlowDaily.AsNoTracking().Where(m => m.PlayerId == playerId).ToListAsync();
                response.MaterialsRecordedSince = materialRows.Count == 0 ? null : materialRows.Min(r => r.Day).ToString("yyyy-MM-dd");
                response.Materials = materialRows
                    .GroupBy(r => (r.ItemId, r.Direction))
                    .Select(g => new MaterialFlowResponse
                    {
                        ItemId = g.Key.ItemId,
                        Direction = Enum.IsDefined(typeof(MaterialFlowDirection), g.Key.Direction) ? ((MaterialFlowDirection)g.Key.Direction).ToString() : $"Other{g.Key.Direction}",
                        Last7Days = g.Where(r => r.Day > today.AddDays(-7)).Sum(r => r.Amount),
                        Last30Days = g.Where(r => r.Day > today.AddDays(-30)).Sum(r => r.Amount),
                        SinceRecorded = g.Sum(r => r.Amount),
                    })
                    .OrderByDescending(m => m.SinceRecorded)
                    .ToList();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Gold ledger error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
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
        /// Task 69: the plan a stack fusion would carry out now, from the same
        /// planner and the same fee the fusion uses (ForgeSplicingEngine
        /// .PlanStack / FusionFee), so the preview cannot quote one price and
        /// the anvil charge another. A GET, so no stripe lock - it writes
        /// nothing.
        /// </summary>
        private async Task HandleForgeStackPreview(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                if (!long.TryParse(context.Request.QueryString["item"], out long sampleId) || sampleId <= 0
                    || !int.TryParse(context.Request.QueryString["to"], out int toTier)
                    || toTier < 2 || toTier > ForgeSplicingEngine.MaxQualityTier)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                var preview = await new ForgeSplicingEngine(_serviceProvider).PreviewStackFusionAsync(playerId, sampleId, toTier);
                if (preview == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var (baseItemId, plan) = preview.Value;
                var response = new ForgeStackPreviewResponse
                {
                    BaseItemId = baseItemId,
                    FromTier = plan.FromTier,
                    CeilingTier = plan.CeilingTier,
                    ForgeLevel = plan.ForgeLevel,
                    TotalFusions = plan.TotalFusions,
                    GoldCost = plan.GoldCost,
                    GoldAvailable = plan.GoldAvailable,
                    StoppedByGold = plan.StoppedByGold,
                    StoppedByCap = plan.StoppedByCap,
                };
                for (int tier = plan.FromTier; tier <= Math.Max(plan.FromTier, plan.CeilingTier) && tier < plan.CountsAfter.Length; tier++)
                {
                    if (plan.CountsAfter[tier] > 0) response.Result.Add(new ForgeStackTierCount { Tier = tier, Count = plan.CountsAfter[tier] });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Forge stack preview error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleChestBulkAction(HttpListenerContext context, bool sell)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var payload = JsonSerializer.Deserialize<JsonElement>(await ReadBodyAsync(context));

                if (!payload.TryGetProperty("maxQualityTier", out var tierElement)
                    || tierElement.ValueKind != JsonValueKind.Number)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                int maxQualityTier = tierElement.GetInt32();
                if (maxQualityTier < 1 || maxQualityTier > VillageChestEngine.MaxSweepableQualityTier)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var outcome = await VillageChestEngine.RemoveEquipmentUpToTierAsync(
                    db, playerId, maxQualityTier, sell);

                // Modul: THE GOLD HAS TO REACH THE LIVE SESSION, and it must
                // not be banked twice on the way.
                //
                // The engine already credited CommodityRecords["gold"], the
                // authoritative row. But a player doing this is logged in - it
                // is a button on a screen - and their session holds a separate
                // unbanked CurrentGold that the wire reports and the checkpoint
                // later applies to that row AS AN INCREMENT. So the payload
                // needs its displayed total corrected, and its pending delta
                // left strictly alone: adding to RedisPendingGoldDelta here
                // would credit the row a second time at the next checkpoint.
                //
                // That asymmetry is the whole reason this is a different queue
                // from AutoSalvageQueue, which does the exact opposite - see
                // ChestSaleGoldNotification.
                if (outcome.GoldGained > 0L)
                {
                    _playerSessionRegistry?.ChestSaleGoldQueue.Enqueue(new ChestSaleGoldNotification
                    {
                        PlayerId = playerId,
                        GoldGained = outcome.GoldGained
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new ChestBulkActionResponse
                {
                    Success = true,
                    RemovedCount = outcome.RemovedCount,
                    GoldGained = outcome.GoldGained,
                    SkippedWornCount = outcome.SkippedWornCount
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chest bulk action error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
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

        /// <summary>
        /// The shield wheel's four routes (task 36, spec 5).
        ///
        /// Modul: TIMESTAMPS AND CHOICES ONLY. Nothing a request carries is a
        /// quantity the server adopts: Seq, TapMs, a parry enum and a plate
        /// 0-4, every one of them scored against the server's own schedule.
        /// Every refusal answers 200 with a Result the screen can say out loud;
        /// malformed JSON is 400 and a missing token 401.
        /// </summary>
        private async Task HandleWorldBossStrikeRoute(HttpListenerContext context, string requestPath)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                var service = _serviceProvider.GetRequiredService<FolkIdle.Server.Domain.Combat.WorldBossStrike.WorldBossStrikeService>();
                string method = context.Request.HttpMethod;
                object? answer = null;

                if (requestPath == "/api/v1/worldboss/challenge" && method == "GET")
                {
                    answer = await service.GetChallengeAsync(playerId);
                }
                else if (method == "POST")
                {
                    string body;
                    body = await ReadBodyAsync(context);

                    try
                    {
                        switch (requestPath)
                        {
                            case "/api/v1/worldboss/challenge":
                                bool practice = false;
                                if (!string.IsNullOrWhiteSpace(body))
                                {
                                    using var parsed = JsonDocument.Parse(body);
                                    if (parsed.RootElement.TryGetProperty("Practice", out var p) && p.ValueKind == JsonValueKind.True) practice = true;
                                }
                                answer = await service.IssueChallengeAsync(playerId, practice);
                                break;
                            case "/api/v1/worldboss/throw":
                                var throwRequest = JsonSerializer.Deserialize<FolkIdle.Server.Domain.Combat.WorldBossStrike.ThrowRequest>(body, WorldBossStrikeJson);
                                if (throwRequest == null) { context.Response.StatusCode = 400; return; }
                                answer = service.Throw(playerId, throwRequest);
                                break;
                            case "/api/v1/worldboss/practice/score":
                                var scoreRequest = JsonSerializer.Deserialize<FolkIdle.Server.Domain.Combat.WorldBossStrike.StrikeRequest>(body, WorldBossStrikeJson);
                                if (scoreRequest == null) { context.Response.StatusCode = 400; return; }
                                answer = service.ScorePractice(playerId, scoreRequest);
                                break;
                            case "/api/v1/worldboss/strike":
                                var strikeRequest = string.IsNullOrWhiteSpace(body)
                                    ? new FolkIdle.Server.Domain.Combat.WorldBossStrike.StrikeRequest()
                                    : JsonSerializer.Deserialize<FolkIdle.Server.Domain.Combat.WorldBossStrike.StrikeRequest>(body, WorldBossStrikeJson);
                                answer = await service.StrikeAsync(playerId, strikeRequest ?? new FolkIdle.Server.Domain.Combat.WorldBossStrike.StrikeRequest());
                                if (answer == null) { context.Response.StatusCode = 400; return; }
                                break;
                        }
                    }
                    catch (JsonException)
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }
                }

                if (answer == null)
                {
                    context.Response.StatusCode = 405;
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, answer, answer.GetType(), WorldBossStrikeJson);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"World boss strike route error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleDelveView(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var engine = _serviceProvider.GetRequiredService<FolkIdle.Server.Domain.Economy.DelveEngine>();
                var view = await engine.GetViewAsync(playerId);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, view);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Delve view error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// start / door / bank.
        ///
        /// Modul: THE ONLY NUMBER THIS ACCEPTS FROM THE CLIENT IS A DOOR INDEX,
        /// and it is bounds-checked in the engine. There is no field here that a
        /// tampered client could make profitable - no floor, no depth, no score,
        /// no reward. That is deliberate and it is the whole security design of
        /// the feature: opcode 39 once granted diamonds straight out of an
        /// unsigned client field, and a minigame is exactly the shape that
        /// invites the same mistake a second time.
        ///
        /// Every refusal answers 200 with a Result the screen can read, not a
        /// bare status code. A silent rollback here would present as a dead
        /// button, which is this server's favourite way to lie.
        /// </summary>
        private async Task HandleDelveAction(HttpListenerContext context, string requestPath)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var engine = _serviceProvider.GetRequiredService<FolkIdle.Server.Domain.Economy.DelveEngine>();
                FolkIdle.Server.Domain.Economy.DelveActionOutcome outcome;

                if (requestPath == "/api/v1/delve/start")
                {
                    outcome = await engine.StartRunAsync(playerId);
                }
                else if (requestPath == "/api/v1/delve/bank")
                {
                    outcome = await engine.BankAsync(playerId);
                }
                else if (requestPath == "/api/v1/delve/door")
                {
                    string body = await ReadBodyAsync(context);

                    int door;
                    try
                    {
                        using var parsed = JsonDocument.Parse(body);
                        if (!parsed.RootElement.TryGetProperty("Door", out var doorElement)
                            || doorElement.ValueKind != JsonValueKind.Number)
                        {
                            context.Response.StatusCode = 400;
                            context.Response.Close();
                            return;
                        }
                        door = doorElement.GetInt32();
                    }
                    catch (JsonException)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    outcome = await engine.ChooseDoorAsync(playerId, door);
                }
                else if (requestPath == "/api/v1/delve/deep/descend")
                {
                    // Modul: QuotedStake is the stake the screen SHOWED, and it
                    // is only ever a ceiling - the engine charges its own
                    // number and answers PriceChanged if that is higher. A
                    // missing or malformed quote is a 400, not a zero: a zero
                    // quote would just read as "price changed" for ever.
                    string body = await ReadBodyAsync(context);

                    long quotedStake;
                    try
                    {
                        using var parsed = JsonDocument.Parse(body);
                        if (!parsed.RootElement.TryGetProperty("QuotedStake", out var quoteElement)
                            || quoteElement.ValueKind != JsonValueKind.Number
                            || !quoteElement.TryGetInt64(out quotedStake))
                        {
                            context.Response.StatusCode = 400;
                            context.Response.Close();
                            return;
                        }
                    }
                    catch (JsonException)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    outcome = await engine.DescendAsync(playerId, quotedStake);
                }
                else if (requestPath == "/api/v1/delve/deep/lantern")
                {
                    // No body is read: the action is the whole request, and the
                    // price is the engine's (stake x 2^bought, frozen stake).
                    outcome = await engine.BuyLanternAsync(playerId);
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                // Modul: gold and diamonds moved in the database, out of band
                // from the live payload - so the session has to be told, or the
                // header keeps showing the old balance until the player signs in
                // again. Reported once already as "I have to press F5 for the
                // gold to update"; ReloadState is the answer every other
                // off-tick engine here uses.
                //
                // A descent moves both: it banks floors 1-8 (diamonds or
                // consolation gold) and debits a toll, all in the database.
                //
                // A Deep floor that is a new weekly record pays one diamond on the
                // door endpoint (2026-10-07), so a cleared floor that paid reloads too.
                bool changedBalances =
                    (outcome.Result == FolkIdle.Server.Domain.Economy.DelveResult.Ok
                        && (requestPath == "/api/v1/delve/start"
                            || requestPath == "/api/v1/delve/bank"
                            || requestPath == "/api/v1/delve/deep/descend"
                            || requestPath == "/api/v1/delve/deep/lantern"))
                    || (outcome.Result == FolkIdle.Server.Domain.Economy.DelveResult.FloorCleared
                        && outcome.DiamondsGranted > 0);

                if (changedBalances)
                {
                    CommandQueue.Enqueue(new PlayerCommand
                    {
                        PlayerId = playerId,
                        Packet = new ClientCommandPacket { Command = CommandType.ReloadState }
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new
                {
                    Result = outcome.Result.ToString(),
                    outcome.DiamondsGranted,
                    outcome.GoldReturned,
                    outcome.GoldCharged,
                    outcome.View
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Delve action error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

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

        /// <summary>
        /// Task 78: GET /api/v1/combat/projection?slot=N (0-based, default 0).
        /// Every canonical monster, projected against this character's live
        /// payload. 409 with Reason NoSession when the player has no running
        /// session, because there is nothing live to project from.
        /// </summary>
        private async Task HandleCombatProjection(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                int slot = 0;
                string? slotText = context.Request.QueryString["slot"];
                if (slotText != null && (!int.TryParse(slotText, out slot) || slot < 0 || slot > 2))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                CombatProjectionResponse body;
                if (_projectionCache.TryGetValue((playerId, slot), out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(1))
                {
                    body = cached.Body;
                }
                else
                {
                    if (_playerSessionRegistry == null)
                    {
                        context.Response.StatusCode = 503;
                        context.Response.Close();
                        return;
                    }

                    var order = new Domain.Combat.PayloadSnapshotOrder { PlayerId = playerId };
                    _playerSessionRegistry.PayloadSnapshotQueue.Enqueue(order);
                    var finished = await Task.WhenAny(order.Completion.Task, Task.Delay(3000));
                    var snapshot = finished == order.Completion.Task ? order.Completion.Task.Result : null;
                    if (snapshot == null)
                    {
                        context.Response.StatusCode = 409;
                        context.Response.ContentType = "application/json";
                        await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { Reason = "NoSession" });
                        context.Response.Close();
                        return;
                    }

                    var payload = snapshot.Value.Payload;
                    // Slots 2 and 3 are parked beside the register; swap the
                    // COPY so the projection fights in that character's gear.
                    Domain.Combat.SimulationEngine.SwapSlotIntoActiveRegister(ref payload, slot);

                    body = new CombatProjectionResponse { Slot = slot };
                    for (int id = ContentRegistry.FirstCanonicalMonsterId; id <= ContentRegistry.Monsters.Length; id++)
                    {
                        var e = Domain.Combat.HuntingProjection.Project(
                            in payload, id, snapshot.Value.GlobalXpMultiplier, snapshot.Value.ActiveGlobalEventId);
                        body.Monsters.Add(new HuntingEstimateResponse
                        {
                            MonsterId = e.MonsterId,
                            CanDamage = e.CanDamage,
                            SecondsPerKill = Math.Round(e.SecondsPerKill, 1),
                            SecondsPerKillLow = Math.Round(e.SecondsPerKillLow, 1),
                            SecondsPerKillHigh = Math.Round(e.SecondsPerKillHigh, 1),
                            XpPerHour = e.XpPerHour,
                            GoldPerHour = e.GoldPerHour,
                            SurvivesWithFood = e.SurvivesWithFood,
                            SurvivesWithoutFood = e.SurvivesWithoutFood,
                            KillsBeforeDeathWithoutFood = e.KillsBeforeDeathWithoutFood,
                            FoodPerHour = Math.Round(e.FoodPerHour, 1),
                        });
                    }
                    _projectionCache[(playerId, slot)] = (DateTime.UtcNow, body);
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, body);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Combat projection error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }
        /// <summary>
        /// Reads (GET) or sets (POST) the auto-salvage floor.
        ///
        /// Opt-in and off by default, the same shape as email consent: quality
        /// tier 1 is a real item a new player wants, so changing nothing until
        /// the player asks is the only safe default.
        /// </summary>
        private async Task HandleChestSettings(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var player = await db.PlayerRecords.SingleOrDefaultAsync(p => p.Id == playerId);
                if (player == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                if (context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);

                    // Modul: BOTH FIELDS ARE OPTIONAL, AND AT LEAST ONE IS
                    // REQUIRED. The Settings screen and exercise.mjs post only
                    // AutoSalvageBelowTier. The Chest's rules panel (task 81)
                    // posts both. A field that is absent keeps its stored
                    // value, so an older caller cannot wipe the region rules
                    // just by not knowing about them.
                    int tier = player.AutoSalvageBelowTier;
                    int regionTiers = player.AutoSalvageRegionTiers;
                    try
                    {
                        using var parsed = JsonDocument.Parse(body);
                        bool hasTier = parsed.RootElement.TryGetProperty("AutoSalvageBelowTier", out var tierElement);
                        bool hasRegions = parsed.RootElement.TryGetProperty("AutoSalvageRegionTiers", out var regionsElement);
                        if (!hasTier && !hasRegions)
                        {
                            context.Response.StatusCode = 400;
                            context.Response.Close();
                            return;
                        }

                        if (hasTier)
                        {
                            if (tierElement.ValueKind != JsonValueKind.Number)
                            {
                                context.Response.StatusCode = 400;
                                context.Response.Close();
                                return;
                            }
                            tier = tierElement.GetInt32();
                        }

                        if (hasRegions)
                        {
                            var perRegion = new List<int>();
                            if (regionsElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var element in regionsElement.EnumerateArray())
                                {
                                    if (element.ValueKind != JsonValueKind.Number) { perRegion = null; break; }
                                    perRegion.Add(element.GetInt32());
                                }
                            }
                            else
                            {
                                perRegion = null;
                            }

                            // TryPack refuses a wrong count or an out-of-range
                            // tier, for the same reason as the check below.
                            if (perRegion == null || !ChestSalvageRules.TryPack(perRegion, out regionTiers))
                            {
                                context.Response.StatusCode = 400;
                                context.Response.Close();
                                return;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or InvalidOperationException)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    // Refused rather than clamped. Clamping a 14 down to 6 would
                    // silently give the player a setting they did not choose,
                    // and this one destroys items.
                    if (tier < 0 || tier > VillageChestEngine.MaxSweepableQualityTier)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    player.AutoSalvageBelowTier = tier;
                    player.AutoSalvageRegionTiers = regionTiers;
                    await db.SaveChangesAsync();

                    // The loot engine reads this off the live payload (see
                    // TickStatePayload.AutoSalvageBelowTier), which is hydrated
                    // at login - so without this the setting would not take
                    // effect until the player signed out and back in, and would
                    // look like a toggle that does nothing.
                    _playerSessionRegistry?.ChestSettingsQueue.Enqueue(new ChestSettingsNotification
                    {
                        PlayerId = playerId,
                        AutoSalvageBelowTier = tier,
                        AutoSalvageRegionTiers = regionTiers
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new ChestSettingsResponse
                {
                    AutoSalvageBelowTier = player.AutoSalvageBelowTier,
                    AutoSalvageRegionTiers = ChestSalvageRules.Unpack(player.AutoSalvageRegionTiers),
                    MaxSweepableQualityTier = VillageChestEngine.MaxSweepableQualityTier
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chest settings error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// Sells or bins one thing from the village chest.
        ///
        /// One handler for both because they differ only in whether gold is
        /// paid - the lookup, the ownership check and the transaction are
        /// identical, and duplicating them would be duplicating the part that
        /// destroys a player's property.
        ///
        /// REST rather than a WebSocket command deliberately: the caller needs
        /// to be told HOW MUCH GOLD it got, and the fixed-layout command packet
        /// has no reply channel. It is also an explicit, deliberate action - a
        /// player pressing "sell" is waiting for the answer.
        /// </summary>
        private async Task HandleChestAction(HttpListenerContext context, bool sell)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var payload = JsonSerializer.Deserialize<JsonElement>(await ReadBodyAsync(context));

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                VillageChestEngine.ChestActionResult result;
                long gold;

                // An equipment id and a material id are different shapes, so
                // which one is present decides the operation rather than a
                // separate "kind" field that could disagree with the payload.
                if (payload.TryGetProperty("equipmentId", out var equipmentElement))
                {
                    (result, gold) = await VillageChestEngine.RemoveEquipmentAsync(
                        db, playerId, equipmentElement.GetInt64(), sell);
                }
                else if (payload.TryGetProperty("itemId", out var itemElement))
                {
                    long quantity = payload.TryGetProperty("quantity", out var q) ? q.GetInt64() : 0L;
                    (result, gold) = await VillageChestEngine.RemoveMaterialAsync(
                        db, playerId, itemElement.GetString() ?? string.Empty, quantity, sell);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new ChestActionResponse
                {
                    Success = result == VillageChestEngine.ChestActionResult.Success,
                    GoldGained = gold,
                    Reason = result.ToString()
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chest action error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// Toggles one item's affix lock. Mirrors HandleChestAction's shape
        /// exactly, including the ChestActionResult it reports back, so the
        /// screen has one vocabulary for everything it can ask of an item.
        /// </summary>
        private async Task HandleChestToggleLock(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var payload = JsonSerializer.Deserialize<JsonElement>(await ReadBodyAsync(context));

                if (!payload.TryGetProperty("equipmentId", out var equipmentElement))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var (result, locked) = await VillageChestEngine.ToggleAffixLockAsync(
                    db, playerId, equipmentElement.GetInt64());

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new ChestLockResponse
                {
                    Success = result == VillageChestEngine.ChestActionResult.Success,
                    Locked = locked,
                    Reason = result.ToString()
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chest lock error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

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

        private async Task HandleGuildWarUnlock(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                // Evaluates rather than reading a cache, so the screen shows
                // today's count - and if this is the request that crosses the
                // floor, it records the unlock like any other evaluator would.
                var status = await GuildWarUnlock.EvaluateAsync(db);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GuildWarUnlockResponse
                {
                    Unlocked = status.Unlocked,
                    QualifyingPlayers = status.QualifyingPlayers,
                    RequiredPlayers = GuildWarUnlock.RequiredQualifyingPlayers,
                    QualifyingGuilds = status.QualifyingGuilds,
                    RequiredGuilds = GuildWarUnlock.RequiredGuilds,
                    RequiredMembersPerGuild = GuildWarUnlock.RequiredMembersPerGuild,
                    MinimumLevel = LeaderboardTierRegistry.MinimumRankedLevel
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild war unlock error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleGuildShardMatch(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                long guildId = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.GuildId)
                    .FirstOrDefaultAsync();

                GuildShardMatchResponse? payload = null;

                if (guildId > 0)
                {
                    var match = await db.GuildMatchmakingSnapshots
                        .AsNoTracking()
                        .Where(m => !m.IsComplete && (m.AttackerGuildId == guildId || m.DefenderGuildId == guildId))
                        .OrderBy(m => m.TournamentGroupIndex)
                        .FirstOrDefaultAsync();

                    if (match != null)
                    {
                        payload = new GuildShardMatchResponse
                        {
                            MatchUuid = match.MatchUuid.ToString(),
                            ActiveMatchMmr = match.ActiveMatchMmr,
                            GlobalNodeRemainingHp = match.GlobalNodeRemainingHp,
                            IsAttacker = match.AttackerGuildId == guildId
                        };
                    }
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                // A player with no guild, or a guild with no running match,
                // gets `null` rather than a 404 - "there is no match" is a
                // normal answer to this question, not a failure to answer it.
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, payload);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild shard match error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleGuildLogisticsSnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                if (!string.IsNullOrEmpty(context.Request.Url?.Query))
                {
                    ForceDisconnect(playerId);
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                long guildId = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.GuildId)
                    .SingleOrDefaultAsync();

                var snapshot = await db.GuildLogisticsDepots
                    .AsNoTracking()
                    .Where(d => d.GuildId == guildId && guildId > 0)
                    .OrderBy(d => d.MaterialId)
                    .Select(d => new GuildLogisticsSnapshotResponse
                    {
                        MaterialId = d.MaterialId,
                        CurrentStock = d.CurrentStock,
                        TargetRequirement = d.TargetRequirement
                    })
                    .ToListAsync();

                await transaction.CommitAsync();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, snapshot);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild logistics snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul 21: on-demand snapshot for the client Forge crafting/reroll panels.
        // StateUpdatePacket is fixed-size and carries scalars only, so the player's
        // full owned-equipment list and per-recipe material stock (both variable
        // length) are served here instead, following the same authenticated
        // read-only HTTP pattern as HandleGuildLogisticsSnapshot/HandleGlobalLeaderboard.
        private async Task HandleForgeInventorySnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var ownedEquipment = await db.EquipmentInstances
                    .AsNoTracking()
                    .Where(e => e.PlayerId == playerId)
                    .ToListAsync();

                var materialQuantities = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .ToDictionaryAsync(c => c.ItemId, c => c.Quantity);

                // All ELEVEN slots - eight combat, then axe, pickaxe, rod.
                var wornLoadouts = await db.CharacterRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .ToListAsync();

                await transaction.CommitAsync();

                var wornIds = new System.Collections.Generic.HashSet<long>();
                foreach (var c in wornLoadouts)
                {
                    foreach (var id in new[]
                    {
                        c.EquippedWeaponId, c.EquippedHelmetId, c.EquippedChestId, c.EquippedGlovesId,
                        c.EquippedLeggingsId, c.EquippedBootsId, c.EquippedAmuletId, c.EquippedRingId,
                        c.EquippedAxeId, c.EquippedPickaxeId, c.EquippedRodId,
                    })
                    {
                        if (id.HasValue) wornIds.Add(id.Value);
                    }
                }

                var response = new ForgeInventorySnapshotResponse();

                foreach (var item in ownedEquipment)
                {
                    var affixes = new System.Collections.Generic.Dictionary<string, int>();
                    bool jsonLockFlag = false;

                    if (!string.IsNullOrWhiteSpace(item.AffixPayload) &&
                        System.Text.Json.Nodes.JsonNode.Parse(item.AffixPayload) is System.Text.Json.Nodes.JsonObject affixObject)
                    {
                        foreach (var kvp in affixObject)
                        {
                            if (kvp.Value is not System.Text.Json.Nodes.JsonValue affixValue)
                            {
                                continue;
                            }

                            if (kvp.Key == "is_affix_locked")
                            {
                                jsonLockFlag = affixValue.TryGetValue(out bool lockedFlag) && lockedFlag;
                                continue;
                            }

                            if (affixValue.TryGetValue(out int magnitude))
                            {
                                affixes[kvp.Key] = magnitude;
                            }
                        }
                    }

                    response.OwnedEquipment.Add(new ForgeEquipmentInstanceResponse
                    {
                        Id = item.Id,
                        BaseItemId = item.BaseItemId,
                        QualityTier = item.QualityTier,
                        IsAffixLocked = item.IsAffixLocked || jsonLockFlag,
                        IsEquipped = wornIds.Contains(item.Id),
                        Affixes = affixes
                    });
                }

                // Modul: the Forge no longer serves recipes. It fuses and
                // rerolls what the player looted; making equipment out of ore
                // was the second crafting system, and it is gone. Recipes stay
                // on /api/v1/crafting/recipes, which serves the tool tree.
                // response.Recipes is left on the DTO and simply comes back
                // empty - removing it would be a wire break for a client that
                // has not reloaded yet, and it costs two bytes of JSON.

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Forge inventory snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
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

        // Modul: excludes rows already claimed or with a claim currently in
        // flight (IsPending) - matches ClaimMailItemAsync's own rejection
        // condition exactly, so the list a player sees only ever contains
        // ids that a claim request against them can actually succeed on.
        private async Task HandleMailboxListSnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var entries = await db.MailboxInstances
                    .AsNoTracking()
                    .Where(m => m.PlayerId == playerId && !m.IsClaimed && !m.IsPending)
                    .OrderByDescending(m => m.ReceivedTimestamp)
                    .Select(m => new MailboxEntryResponse
                    {
                        Id = m.Id,
                        BaseItemId = m.BaseItemId,
                        QualityTier = m.QualityTier,
                        Quantity = m.Quantity,
                        GoldAttachment = m.GoldAttachment,
                        DiamondAttachment = m.DiamondAttachment,
                        TitleAttachment = m.TitleAttachment,
                        HasEquipmentAttachment = m.AttachedEquipmentId.HasValue,
                        SenderName = m.SenderName,
                        MessageText = m.MessageText,
                        ReceivedTimestamp = m.ReceivedTimestamp
                    })
                    .ToListAsync();

                foreach (var entry in entries)
                {
                    var title = FolkIdle.Server.Domain.Progression.TitleRegistry.Find(entry.TitleAttachment);
                    entry.TitleAttachment = title?.DisplayName;
                    entry.TitleAttachmentColor = title?.Color;
                }

                await transaction.CommitAsync();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, entries);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Mailbox list snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
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

        // Modul: static content, not per-player data - no database access
        // needed, just an authenticated read of ContentRegistry.Balance
        // (already loaded once at boot from GameBalanceConfig.json).
        private async Task HandleStoreCatalog(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var entries = new System.Collections.Generic.List<StoreCatalogEntryResponse>();
                foreach (var kvp in ContentRegistry.Balance.IapProductPrices)
                {
                    entries.Add(new StoreCatalogEntryResponse { ProductId = kvp.Key, DiamondAmount = kvp.Value });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, entries);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Store catalog error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private sealed class GuildRosterEntryResponse
        {
            public long PlayerId { get; set; }
            public int Role { get; set; }
            public long ContributionPoints { get; set; }
            public bool IsOnline { get; set; }
        }

        // Modul: resolves the requesting player's own GuildId first (never
        // a client-supplied one), then lists every GuildMembers row sharing
        // that GuildId - a player can only ever see their own guild's
        // roster. IsOnline is resolved directly from this pod's own
        // _connectedClients (the same dictionary BroadcastChatMessage/
        // UpdateSessionGuildId already read/write), not a database column
        // - guild membership is persistent, but presence is a live,
        // in-memory fact. PlayerSessionRegistry is deliberately not used
        // here - it is never registered in Program.cs's DI container
        // (only ever constructed directly and passed to specific engine
        // constructors), so resolving it via _serviceProvider.
        // GetRequiredService would throw at runtime.
        private async Task HandleGuildRoster(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                long guildId = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.GuildId)
                    .SingleOrDefaultAsync();

                if (guildId <= 0)
                {
                    await transaction.CommitAsync();
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.OutputStream, new System.Collections.Generic.List<GuildRosterEntryResponse>());
                    context.Response.Close();
                    return;
                }

                var members = await db.GuildMembers
                    .AsNoTracking()
                    .Where(m => m.GuildId == guildId)
                    .OrderByDescending(m => m.Role)
                    .ThenByDescending(m => m.ContributionPoints)
                    .ToListAsync();

                await transaction.CommitAsync();

                var entries = new System.Collections.Generic.List<GuildRosterEntryResponse>(members.Count);
                foreach (var member in members)
                {
                    entries.Add(new GuildRosterEntryResponse
                    {
                        PlayerId = member.PlayerId,
                        Role = member.Role,
                        ContributionPoints = member.ContributionPoints,
                        IsOnline = _connectedClients.ContainsKey(member.PlayerId)
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, entries);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild roster error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private sealed class PlayerMetadataResponse
        {
            public int ChroniclePassLevel { get; set; }
            public int AccumulatedSeasonalXp { get; set; }
            public int EventHorizonTransactionCount { get; set; }
        }

        // Modul: Production Release Hardening, Part 2. ActiveChroniclePassLevel/
        // AccumulatedSeasonalXp were removed from StateUpdatePacket - this
        // is their new home. EventHorizonTransactionCount was never
        // actually populated on the old packet field at all (dead code -
        // always sent as 0); this computes the real value for the first
        // time, from EventHorizonPremiumLedgers (the same ledger every
        // premium-balance change already writes to).
        private async Task HandlePlayerMetadata(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var pass = await db.PlayerChroniclePasses
                    .AsNoTracking()
                    .Where(p => p.PlayerId == playerId)
                    .Select(p => new { p.PassLevel, p.AccumulatedXp })
                    .SingleOrDefaultAsync();

                int transactionCount = await db.EventHorizonPremiumLedgers
                    .AsNoTracking()
                    .CountAsync(l => l.PlayerId == playerId);

                await transaction.CommitAsync();

                var response = new PlayerMetadataResponse
                {
                    ChroniclePassLevel = pass?.PassLevel ?? 0,
                    AccumulatedSeasonalXp = pass?.AccumulatedXp ?? 0,
                    EventHorizonTransactionCount = transactionCount
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player metadata error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
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

        /// <summary>
        /// Reads (GET) or sets (POST) whether this player wants notification
        /// email. Opt-in: the column starts false for every account and this is
        /// the only route that writes it.
        /// </summary>
        /// <remarks>
        /// Modul: separate from the settings the client keeps for itself,
        /// because this one is a PERMISSION rather than a preference. It has to
        /// be stored where the sender can read it - a background job on the
        /// server decides to mail somebody hours after they closed the tab, so
        /// a consent flag living in the browser would be a consent flag nobody
        /// asks.
        /// </remarks>
        private async Task HandleEmailConsent(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var player = await db.PlayerRecords.SingleOrDefaultAsync(p => p.Id == playerId);
                if (player == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                if (context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);

                    bool consent;
                    try
                    {
                        using var parsed = JsonDocument.Parse(body);
                        if (!parsed.RootElement.TryGetProperty("Consented", out var flag)
                            || (flag.ValueKind != JsonValueKind.True && flag.ValueKind != JsonValueKind.False))
                        {
                            context.Response.StatusCode = 400;
                            context.Response.Close();
                            return;
                        }
                        consent = flag.GetBoolean();
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    // Consenting with no address on the account would arm a
                    // notifier that can never send. Refused, so the client can
                    // say why rather than showing a toggle that does nothing.
                    if (consent && string.IsNullOrWhiteSpace(player.Email))
                    {
                        context.Response.StatusCode = 409;
                        context.Response.Close();
                        return;
                    }

                    player.EmailNotificationsConsented = consent;

                    // Withdrawing consent clears the once-per-absence marker as
                    // well, so opting back in later is not silently blocked by
                    // a send that happened before the player opted out.
                    if (!consent) player.OfflineCapEmailSentEpoch = 0L;

                    await db.SaveChangesAsync();
                }

                var response = new EmailConsentResponse
                {
                    Consented = player.EmailNotificationsConsented,
                    HasEmailAddress = !string.IsNullOrWhiteSpace(player.Email)
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Email consent error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// Stores this device's push token against the signed-in player.
        /// </summary>
        /// <remarks>
        /// Modul: EVERY REFUSAL HERE IS AUDIBLE, ON PURPOSE.
        ///
        /// The engine behind this used to be reachable only through opcode 33,
        /// whose whole failure mode was silence: `RegisterDeviceAsync` returned
        /// early on a bad length and the client - which had no reply channel
        /// anyway - carried on believing it had registered. This route answers,
        /// and the Settings screen repeats the answer to the player, because a
        /// push feature that silently never fires is indistinguishable from one
        /// the player turned off.
        ///
        ///   400  the body is not a token, or names a platform that is not
        ///        ios/android
        ///   401  no session
        ///   503  the engine was never registered (a start-up fault, not the
        ///        player's)
        ///   500  the write failed
        ///
        /// The platform arrives as a WORD and is mapped to the stored 1/2 here,
        /// once. A magic number crossing the wire would be that mapping written
        /// down in two languages, which is how KNOWN_AFFIX_IDS drifted.
        /// </remarks>
        /// <summary>
        /// Tells a phone which web bundle it should be running.
        ///
        /// Modul: FAILS SAFE, and that is the most important property here.
        ///
        /// With no bundle configured this answers "you are up to date". It does
        /// not guess a version, it does not 500, and it does not hand back a URL
        /// that might not exist - because the consequence of getting this wrong
        /// is not a failed request, it is every phone downloading and applying
        /// something broken. Silence is always a safe answer for an update
        /// check; a wrong answer never is.
        ///
        /// So the feature is INERT until FOLKIDLE_BUNDLE_VERSION and
        /// FOLKIDLE_BUNDLE_URL are both set. Deploying this code without them
        /// changes nothing for anybody, which is the state it should stay in
        /// until somebody has watched an update land on a real device.
        ///
        /// The bundle itself is a static file served by Caddy - see
        /// ops/oracle/caddy/Caddyfile. This endpoint only ever names it.
        /// </summary>
        private async Task HandleLiveBundleManifest(HttpListenerContext context)
        {
            try
            {
                string version = Environment.GetEnvironmentVariable("FOLKIDLE_BUNDLE_VERSION") ?? string.Empty;
                string url = Environment.GetEnvironmentVariable("FOLKIDLE_BUNDLE_URL") ?? string.Empty;

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";

                // The plugin treats a reply carrying no `url` as "nothing to do".
                string payload = (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url))
                    ? "{\"message\":\"no bundle configured\"}"
                    : "{\"version\":\"" + JsonEscape(version) + "\",\"url\":\"" + JsonEscape(url) + "\"}";

                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HandleLiveBundleManifest failed: {ex.Message}");
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch { }
            }
        }

        /// <summary>Minimal JSON string escaping for the two values above.</summary>
        private static string JsonEscape(string value)
            => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private async Task HandlePushTokenRegistration(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                if (_pushNotificationTriggerEngine == null)
                {
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    return;
                }

                string body = await ReadBodyAsync(context);

                string token;
                string platform;
                try
                {
                    using var parsed = JsonDocument.Parse(body);
                    token = parsed.RootElement.TryGetProperty("Token", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
                    platform = parsed.RootElement.TryGetProperty("Platform", out var pf) ? (pf.GetString() ?? string.Empty) : string.Empty;
                }
                catch (System.Text.Json.JsonException)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                byte platformFamily = platform.ToLowerInvariant() switch
                {
                    "android" => (byte)1,
                    "ios" => (byte)2,
                    _ => (byte)0
                };

                if (platformFamily == 0)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                // Bounds checked HERE as well as in the engine, so a token that
                // is obviously not one is a 400 the client can explain rather
                // than a 500 it cannot.
                int tokenBytes = System.Text.Encoding.UTF8.GetByteCount(token.Trim());
                if (tokenBytes < PushNotificationTriggerEngine.MinDeviceTokenBytes
                    || tokenBytes > PushNotificationTriggerEngine.MaxDeviceTokenBytes)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                bool stored = await _pushNotificationTriggerEngine.RegisterDeviceTokenAsync(playerId, token, platformFamily);
                context.Response.StatusCode = stored ? 200 : 500;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Push token registration error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
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

        /// <summary>
        /// Reads (GET) or replaces (PUT) the set of onboarding explanations
        /// this player has been shown.
        /// </summary>
        /// <remarks>
        /// Modul: REPLACE, NOT APPEND, and the client is the one that merges.
        ///
        /// An append endpoint looks safer and is not: the client already holds
        /// a local copy for synchronous reads, it unions that with whatever the
        /// server returns at sign-in, and it is the only place that knows about
        /// "forget this one" and "forget all". Two merge strategies for one set
        /// - one here and one there - is the two-sources-of-truth shape this
        /// codebase keeps getting bitten by. So: the server stores, the client
        /// decides.
        ///
        /// NULL SURVIVES. A PUT is what creates the record, and until one
        /// arrives the column stays null and `HasRecord` stays false. That is
        /// load-bearing - see PlayerRecord.OnboardingSeenIds.
        /// </remarks>
        private async Task HandleOnboardingSeen(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var player = await db.PlayerRecords.SingleOrDefaultAsync(p => p.Id == playerId);
                if (player == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                if (context.Request.HttpMethod == "PUT" || context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);

                    string[] ids;
                    try
                    {
                        using var parsed = JsonDocument.Parse(body);
                        if (!parsed.RootElement.TryGetProperty("Seen", out var seenElement)
                            || seenElement.ValueKind != JsonValueKind.Array)
                        {
                            context.Response.StatusCode = 400;
                            context.Response.Close();
                            return;
                        }

                        var collected = new List<string>();
                        foreach (var entry in seenElement.EnumerateArray())
                        {
                            if (entry.ValueKind != JsonValueKind.String) continue;
                            string id = entry.GetString() ?? string.Empty;

                            // Modul: silently DROPPED rather than refused. An id
                            // this client no longer recognises is what a rolled
                            // back deploy looks like, and rejecting the whole
                            // body over one would lose the other twenty-five.
                            if (id.Length == 0 || id.Length > MaxOnboardingIdLength) continue;
                            if (!collected.Contains(id)) collected.Add(id);
                        }

                        if (collected.Count > MaxOnboardingSeenIds)
                        {
                            context.Response.StatusCode = 400;
                            context.Response.Close();
                            return;
                        }

                        ids = collected.ToArray();
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    player.OnboardingSeenIds = JsonSerializer.Serialize(ids);
                    await db.SaveChangesAsync();
                }

                var response = new OnboardingSeenResponse
                {
                    HasRecord = player.OnboardingSeenIds != null,
                    Seen = ParseOnboardingSeen(player.OnboardingSeenIds)
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Onboarding seen error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// Reads the stored column back, treating anything unreadable as empty.
        /// </summary>
        /// <remarks>
        /// A column that will not parse must not 500 a sign-in. The worst
        /// outcome of treating it as empty is that the player is taught
        /// something again; the worst outcome of throwing is that they cannot
        /// play.
        /// </remarks>
        private static string[] ParseOnboardingSeen(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return System.Array.Empty<string>();
            try
            {
                return JsonSerializer.Deserialize<string[]>(stored) ?? System.Array.Empty<string>();
            }
            catch (System.Text.Json.JsonException)
            {
                return System.Array.Empty<string>();
            }
        }

        private sealed class AchievementsStateResponse
        {
            public int ClaimedAchievementFlags { get; set; }
            public int TotalAchievementsClaimedCount { get; set; }
            public ulong ClaimedMilestonesBitmask { get; set; }
        }

        // Modul: Production Release Hardening, Part 2. ClaimedAchievementFlags/
        // TotalAchievementsClaimedCount/ClaimedMilestonesBitmask were
        // removed from StateUpdatePacket - this is their new home. Distinct
        // from the pre-existing /api/v1/achievements/snapshot (which lists
        // the tiered Treasury/Forging/Logistics achievement family's
        // per-achievement progress) - this endpoint covers the separate
        // bitflag-based legacy achievement system plus the Chronicle Pass
        // milestone claim bitmask, matching this task's explicitly named
        // route.
        private async Task HandleAchievementsState(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                int claimedAchievementFlags = await db.PlayerAchievements
                    .AsNoTracking()
                    .Where(a => a.PlayerId == playerId)
                    .Select(a => a.ClaimedAchievementFlags)
                    .SingleOrDefaultAsync();

                int totalAchievementsClaimedCount = await db.PlayerLifetimeAchievements
                    .AsNoTracking()
                    .CountAsync(a => a.PlayerId == playerId && a.IsClaimed);

                ulong claimedMilestonesBitmask = await db.PlayerChroniclePasses
                    .AsNoTracking()
                    .Where(p => p.PlayerId == playerId)
                    .Select(p => p.ClaimedMilestonesBitmask)
                    .SingleOrDefaultAsync();

                await transaction.CommitAsync();

                var response = new AchievementsStateResponse
                {
                    ClaimedAchievementFlags = claimedAchievementFlags,
                    TotalAchievementsClaimedCount = totalAchievementsClaimedCount,
                    ClaimedMilestonesBitmask = claimedMilestonesBitmask
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Achievements state error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul 23: authorized snapshot of the player's real Monster Codex
        // progress. MonsterCodexEntries is already populated by CodexEngine's
        // kill-event cron (SimulationEngine enqueues a KillEvent on every monster
        // death; CodexEngine batches and upserts it off the 10 Hz hot path). Level
        // is read directly off the persisted column rather than recomputed here,
        // so this endpoint can never drift from CodexEngine.CalculateLevelFromKillCount
        // (Level = KillCount / 10, uncapped) if that formula ever changes.
        // Modul 23 fix: previously ran raw SQL against "MonsterCodexEntries"
        // (PascalCase, quoted), but the table is mapped via
        // [Table("monster_codex_entries")] (lowercase, unlike every other
        // table in this codebase - see FolkIdleDbContextModelSnapshot's
        // ToTable("monster_codex_entries")), so the quoted identifier never
        // matched the real table and Postgres would reject it outright.
        // Switched to plain LINQ, matching HandleMasterySnapshot's established
        // fix for this exact lowercase-table situation - EF Core resolves the
        // mapping correctly on its own, sidestepping manual identifier
        // quoting entirely.
        private async Task HandleCodexSnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var entries = await db.MonsterCodexEntries
                    .AsNoTracking()
                    .Where(e => e.PlayerId == playerId)
                    .ToListAsync();

                var response = new System.Collections.Generic.List<CodexSnapshotEntryResponse>(entries.Count);

                foreach (var entry in entries)
                {
                    response.Add(new CodexSnapshotEntryResponse
                    {
                        MonsterId = entry.MonsterId,
                        Level = entry.Level,
                        Kills = entry.KillCount,
                        NextLevelKills = (entry.Level + 1) * 10L
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Codex snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
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

        /// <summary>
        /// What a player's drops are rolling at, from the figures the loot
        /// worker last rolled with and the engine's own expectation - see
        /// CombatLootEngine.TryGetLastOdds and RarityTier.ExpectedFinalShares.
        /// Task 26's odds line in the Wiki.
        /// </summary>
        internal static LootOddsResponse BuildLootOdds(bool known, CombatLootEngine.LootOddsSnapshot odds)
        {
            var response = new LootOddsResponse
            {
                Known = known,
                EquipmentDropChance = CombatLootEngine.EquipmentDropChance,
            };
            if (!known) return response;

            double[] shares = RarityTier.ExpectedFinalShares(
                odds.LootLuckPct, odds.RarityElevationPct,
                odds.HasGoldenFleece ? 1.0 / SimulationEngine.GoldenFleeceKillInterval : 0.0,
                SimulationEngine.GoldenFleeceBonusTiers);

            double legendaryPlus = 0, ancientPlus = 0;
            for (int tier = RarityTier.Legendary; tier <= RarityTier.Transcendent; tier++)
            {
                legendaryPlus += shares[tier];
                if (tier >= RarityTier.Ancient) ancientPlus += shares[tier];
            }

            response.LootLuckPct = odds.LootLuckPct;
            response.RarityElevationPct = odds.RarityElevationPct;
            response.HasGoldenFleece = odds.HasGoldenFleece;
            response.LegendaryPlusPerDrop = legendaryPlus;
            response.AncientPlusPerDrop = ancientPlus;
            response.TierShares = shares;
            return response;
        }

        private async Task HandleLootOdds(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                bool known = CombatLootEngine.TryGetLastOdds(playerId, out var odds);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, BuildLootOdds(known, odds));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Loot odds error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
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

        // Modul 13.4.3: region-completion progress for the Codex regions UI. A
        // region is the five canonical monsters of one location and completes
        // once each regular has 1000 kills and the boss 100 (see
        // RegionCompletionRules) - so CurrentKills here is the MINIMUM kill count across the
        // region's monsters (the true bottleneck to completion), not a sum.
        // IsCompleted comes from PlayerRegionCompletions (the durable ledger
        // CodexEngine writes to and never re-grants) rather than being
        // re-derived from kill counts here, so it can never flip back to
        // false if kill counts are read at a slightly different instant than
        // the completion check ran. LootLuckBonusPct mirrors
        // StatsCalculator's "+1.0% Loot Luck per completed area" exactly.
        private async Task HandleCodexRegionsSnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var codexEntries = await db.MonsterCodexEntries
                    .AsNoTracking()
                    .Where(e => e.PlayerId == playerId)
                    .ToListAsync();

                var completedRegionIds = await db.PlayerRegionCompletions
                    .AsNoTracking()
                    .Where(r => r.PlayerId == playerId)
                    .Select(r => r.RegionId)
                    .ToListAsync();
                var completedRegionSet = new System.Collections.Generic.HashSet<int>(completedRegionIds);

                var killsByMonsterId = new System.Collections.Generic.Dictionary<int, int>(codexEntries.Count);
                for (int i = 0; i < codexEntries.Count; i++)
                {
                    killsByMonsterId[codexEntries[i].MonsterId] = codexEntries[i].KillCount;
                }

                // Modul: task 26 (H5) - the FIVE canonical regions and the rule
                // in RegionCompletionRules. This used to walk RegionTier 1-10,
                // legacy monsters included, and report ten bars of which none
                // could ever fill. CurrentKills is still the weakest REGULAR
                // (the bottleneck), and the boss has its own, smaller target.
                // IsCompleted also accepts a region the rule says is done but the
                // ledger has not recorded yet (CodexEngine writes it on the next
                // kill), so the screen agrees with the luck login already grants.
                System.Func<int, int> killsFor = id => killsByMonsterId.TryGetValue(id, out int k) ? k : 0;
                var response = new System.Collections.Generic.List<RegionProgressResponse>(ContentRegistry.LocationCount);
                for (int region = 1; region <= ContentRegistry.LocationCount; region++)
                {
                    int minRegular = int.MaxValue;
                    int bossKills = 0;
                    int first = RegionCompletionRules.FirstMonsterOf(region);
                    for (int monsterId = first; monsterId < first + ContentRegistry.MonstersPerRegion; monsterId++)
                    {
                        int kills = killsFor(monsterId);
                        if (ContentRegistry.IsRegionalBoss(monsterId)) bossKills = kills;
                        else if (kills < minRegular) minRegular = kills;
                    }
                    if (minRegular == int.MaxValue) minRegular = 0;

                    bool isCompleted = completedRegionSet.Contains(region)
                        || RegionCompletionRules.IsComplete(region, killsFor);
                    response.Add(new RegionProgressResponse
                    {
                        RegionId = region,
                        CurrentKills = Math.Min(minRegular, RegionCompletionRules.RegularKillsRequired),
                        RequiredKills = RegionCompletionRules.RegularKillsRequired,
                        BossKills = Math.Min(bossKills, RegionCompletionRules.BossKillsRequired),
                        RequiredBossKills = RegionCompletionRules.BossKillsRequired,
                        IsCompleted = isCompleted,
                        LootLuckBonusPct = isCompleted ? 1 : 0
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Codex regions snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// A character row as BreedingGateRules wants to see it. The endpoints
        /// and the engine build the same struct from the same columns, so the
        /// preview and the command cannot disagree about who may pair.
        /// </summary>
        private static Engine.BreedingGateRules.Parent ToGateParent(Models.CharacterRecord character, long geneticVector) => new()
        {
            AgePhase = character.AgePhase,
            IsFemale = character.IsFemale,
            RaceId = new GeneticVector(geneticVector).LocusRace.Dominant,
            IsLockedInEscrow = character.IsLockedInEscrow,
            IsBreedingActive = character.IsBreedingActive,
            BreedingCooldownEndEpoch = character.BreedingCooldownEndEpoch,
        };

        private sealed class BreedingRosterEntryResponse
        {
            public string CharacterId { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public int AgePhase { get; set; }
            public int GenerationIndex { get; set; }
            public bool IsBreedingActive { get; set; }
            public long BreedingCooldownEndEpoch { get; set; }
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

        // Modul 13.4.3: the player's own bred/breedable character roster, for
        // the Breeding Lab's parent-selection slots. BreedingEngine.
        // BreedingGateRules' own eligibility rules (AgePhase >= 1, not
        // already IsBreedingActive, not IsLockedInEscrow) are
        // intentionally NOT filtered out here - the client shows every owned
        // character and lets the preview/execute round trip surface exactly
        // why an ineligible pairing was rejected, rather than this endpoint
        // silently hiding characters and leaving a player unable to tell an
        // "under cooldown" character apart from one that was never bred.
        /// <summary>
        /// The village's current gene pool, and how much room is left in it.
        ///
        /// Returns the CAP alongside the people, because "Village 11/14" is the
        /// number that makes the keep-or-turn-away decision legible, and
        /// deriving it client-side would mean mirroring
        /// VillagerArrivalRules.PopulationCapFor into TypeScript for one label.
        /// </summary>
        private async Task HandleVillageQuote(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                // Matured upgrades are applied by the next command or login;
                // the quote reads rows as they stand, which can trail a
                // finished timer by one level for a moment. The screen asks
                // again when the infrastructure notification lands.
                var levels = await db.VillageInfrastructures
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId)
                    .Select(v => new { v.BuildingId, v.CurrentLevel })
                    .ToListAsync();

                var buildings = new List<(int Id, int Level, Domain.Progression.VillageManagementEngine.UpgradeCostLine[] Lines)>();
                var wanted = new HashSet<string>();
                for (uint id = 1; id <= 10; id++)
                {
                    if (!Domain.Progression.VillageManagementEngine.IsValidBuildingId(id)) continue;
                    int level = levels.FirstOrDefault(l => l.BuildingId == (int)id)?.CurrentLevel ?? 0;
                    var lines = Domain.Progression.VillageManagementEngine.QuoteUpgrade((int)id, level);
                    buildings.Add(((int)id, level, lines));
                    foreach (var line in lines) wanted.Add(line.ItemId);
                }

                // The same two places TryConsumeUnifiedAsync spends from: the
                // backpack (CommodityRecords) first, then the village stash.
                var backpack = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId && wanted.Contains(c.ItemId))
                    .Select(c => new { c.ItemId, c.Quantity })
                    .ToListAsync();
                var stash = await db.VillageStashInstances
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId && wanted.Contains(v.ItemId))
                    .Select(v => new { v.ItemId, v.Quantity })
                    .ToListAsync();

                var held = new Dictionary<string, long>();
                foreach (var row in backpack) held[row.ItemId] = held.GetValueOrDefault(row.ItemId) + row.Quantity;
                foreach (var row in stash) held[row.ItemId] = held.GetValueOrDefault(row.ItemId) + row.Quantity;

                var payload = new
                {
                    Buildings = buildings.ConvertAll(b => new
                    {
                        BuildingId = b.Id,
                        CurrentLevel = b.Level,
                        Lines = Array.ConvertAll(b.Lines, line => new
                        {
                            line.ItemId,
                            line.Quantity,
                            // Gold here is the DURABLE balance; the client shows
                            // the live one, which is what the header reads.
                            Held = held.GetValueOrDefault(line.ItemId),
                        }),
                    }),
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, payload);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Village quote error: {ex}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        private async Task HandleVillageNewcomers(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var rows = await db.VillageNewcomers
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId)
                    .OrderByDescending(v => v.ArrivedAtEpoch)
                    .ToListAsync();

                int innLevel = await db.VillageInfrastructures
                    .AsNoTracking()
                    .Where(b => b.PlayerId == playerId
                             && b.BuildingId == Domain.Progression.VillageManagementEngine.InnBuildingId)
                    .Select(b => b.CurrentLevel)
                    .FirstOrDefaultAsync();

                // Modul: recruitment. The price escalates 1.6x per recruitment
                // WITHIN a season off a counter only the server has, so the
                // client cannot compute it and a button that guessed would
                // eventually quote the wrong number. The refusal comes from the
                // same function the command runs, so a disabled button and a
                // rolled-back command can never disagree about why.
                int recruitments = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.VillagerRecruitmentsThisSeason)
                    .FirstOrDefaultAsync();

                long heldGold = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId && c.ItemId == "gold")
                    .Select(c => c.Quantity)
                    .FirstOrDefaultAsync();

                var payload = new
                {
                    InnLevel = innLevel,
                    PopulationCap = Engine.VillagerArrivalRules.PopulationCapFor(innLevel),
                    IntervalSeconds = Engine.VillagerArrivalRules.IntervalSecondsFor(innLevel),
                    RecruitCostGold = Engine.VillagerArrivalRules.RecruitCostGold(recruitments),
                    RecruitBlockedReason = Engine.VillagerArrivalRules.RecruitBlockedReason(
                        innLevel, rows.Count(v => !v.IsElder), heldGold, recruitments) ?? string.Empty,
                    // Who takes a bed against PopulationCap - elders do not
                    // (VillageArrivalEngine.CountRoomTakersAsync), so the
                    // screen's "n / cap" must not count Newcomers.length.
                    Residents = rows.Count(v => !v.IsElder),
                    Newcomers = rows.ConvertAll(v => new
                    {
                        v.Id,
                        // Derived from the id, not stored - see FolkNameRegistry.ForNewcomer.
                        Name = Engine.FolkNameRegistry.ForNewcomer(v.Id, v.IsFemale),
                        v.RaceId,
                        v.IsFemale,
                        v.AptitudeStrength,
                        v.AptitudeSkill,
                        v.AptitudeEndurance,
                        v.AptitudeFortune,
                        v.ArrivedAtEpoch,
                        v.IsElder,
                        v.TraitMask,
                    }),
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, payload);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HandleVillageNewcomers failed: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        /// <summary>
        /// The Book of Deeds: five chapters, every deed with a live x / y, and
        /// the Seals.
        ///
        /// AWARDS ON READ. There is no claim command and deliberately is not
        /// one - a claim button is a thing to forget, and the question "how am
        /// I doing" is the same question as "have I finished a chapter". A
        /// chapter that completes while the player is offline is theirs the
        /// next time they look.
        ///
        /// A CHAPTER OPENS WHEN THE ONE BEFORE IT COMPLETES, so the payload
        /// says which are open rather than letting the client guess from the
        /// Seal mask - "open" and "sealed" differ for exactly one chapter at a
        /// time and that is the one the player is working on.
        /// </summary>
        private async Task HandleDeedsSnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var progress = await Engine.DeedProgressSource.LoadAsync(db, playerId);

                var player = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
                if (player == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                int newlyAwarded = await Engine.SealEngine.AwardCompletedChaptersAsync(db, player, progress);

                // Task 57: the Lifetime chapter. Monster Slayer is paid here, on
                // read, like a Seal; the other three are paid by the checkpoint.
                var (lifetimePaid, diamondBalance) = await Engine.LifetimeAchievementBank.BankMonsterSlayerAsync(db, playerId, progress.TotalKills);
                if (lifetimePaid > 0)
                {
                    // The live payload owns PremiumCurrency and the next
                    // checkpoint assigns it back - hand it the new balance, the
                    // same way AchievementEngine's sweep does.
                    _playerSessionRegistry?.BillingSyncQueue.Enqueue(new BillingSyncNotification
                    {
                        PlayerId = playerId,
                        PremiumDiamondsBalance = diamondBalance
                    });
                }
                var lifetime = await Engine.LifetimeAchievementBank.ReadAsync(db, playerId, progress.TotalKills);

                var chapters = Engine.DeedRegistry.Chapters;

                // A chapter is DONE once sealed or complete - see
                // DeedRegistry.IsOpen for why sealed counts. The open rule
                // itself (task 75: II-IV together, V at two of them) lives
                // there, not here.
                int doneMask = 0;
                foreach (var chapter in chapters)
                {
                    if (Engine.DeedRegistry.HasSeal(player.SealsEarnedMask, chapter.Index)
                        || Engine.DeedRegistry.IsComplete(chapter, progress))
                    {
                        doneMask |= 1 << (chapter.Index - 1);
                    }
                }

                var payload = new
                {
                    SealsEarnedMask = player.SealsEarnedMask,
                    SealCount = Engine.DeedRegistry.SealCount(player.SealsEarnedMask),
                    SkillPointsFromSeals = Engine.DeedRegistry.SkillPointsFrom(player.SealsEarnedMask),
                    SkillPointsPerSeal = Engine.DeedRegistry.SkillPointsPerSeal,
                    // A bitmask of chapters sealed by THIS request, so the
                    // client can celebrate the moment rather than noticing a
                    // number changed.
                    NewlySealedMask = newlyAwarded,
                    Lifetime = lifetime,
                    LifetimeDiamondsPaidNow = lifetimePaid,
                    // Hidden deeds say only their category until they are done.
                    Hidden = Engine.DeedRegistry.Hidden.Select(h =>
                    {
                        bool done = h.Progress(progress) >= h.Target;
                        return new
                        {
                            h.Id,
                            h.Category,
                            Title = done ? h.Title : "???",
                            Body = done ? h.Body : string.Empty,
                            Done = done,
                        };
                    }).ToList(),
                    Chapters = chapters.Select(chapter =>
                    {
                        bool isOpen = Engine.DeedRegistry.IsOpen(chapter.Index, doneMask);
                        bool isComplete = Engine.DeedRegistry.IsComplete(chapter, progress);

                        return new
                        {
                            chapter.Index,
                            chapter.Title,
                            chapter.Reward,
                            IsOpen = isOpen,
                            // What a closed chapter waits for, so the client
                            // does not restate the rule (it said "the chapter
                            // above", which stopped being true with task 75).
                            OpensWhen = isOpen ? string.Empty : Engine.DeedRegistry.OpensWhen(chapter.Index),
                            IsComplete = isComplete,
                            HasSeal = Engine.DeedRegistry.HasSeal(player.SealsEarnedMask, chapter.Index),
                            Deeds = chapter.Deeds.Select(deed => new
                            {
                                deed.Id,
                                deed.Title,
                                deed.Body,
                                deed.Screen,
                                deed.Target,
                                Current = Math.Min(deed.Progress(progress), deed.Target),
                                Done = deed.Progress(progress) >= deed.Target,
                            }).ToList(),
                        };
                    }).ToList(),
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, payload);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HandleDeedsSnapshot failed: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        private async Task HandleCollection(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var view = await Engine.CollectionLog.BuildAsync(db, playerId, DateTime.UtcNow);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, view);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HandleCollection failed: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        private async Task HandleInsights(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var view = await Engine.StatSampler.BuildInsightsAsync(db, playerId, DateTime.UtcNow);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, view);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HandleInsights failed: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        /// <summary>
        /// The Hall of Ancestors: everyone the account owns, what they carry,
        /// and how many of them survive the next rollover.
        ///
        /// Returns the CAP and the marks together, because "11 / 14" and "these
        /// four are safe" are the same decision seen from two sides, and a
        /// client that had to derive the cap would be mirroring
        /// HallOfAncestorsRules into TypeScript for one label.
        ///
        /// Also returns the ranking the cull would use RIGHT NOW - see
        /// WouldCarry. A cap that only reveals what it did after a rollover has
        /// already deleted somebody is not a decision, it is a surprise.
        /// </summary>
        private async Task HandleRebirthPreview(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var preview = await new RebirthEngine(_serviceProvider).PreviewAsync(playerId);
                if (preview == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, preview);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Rebirth preview error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private sealed class RebirthRequestBody
        {
            public int ExpectedRebirthCount { get; set; } = -1;
            // What the player typed - RebirthRules.ConfirmationWord.
            public string? Confirm { get; set; }
        }

        /// <summary>
        /// Task 88: the player ends their run. Body: {ExpectedRebirthCount},
        /// the count the preview showed - the idempotency token.
        ///
        /// Modul: EVERY OUTCOME ANSWERS WITH A RESULT, never a bare status and
        /// never silence (CLAUDE.md, "silent rollback"): 200 Ok, 409
        /// AlreadyReborn / InFlight, 503 when the tick did not answer in time.
        /// </summary>
        private async Task HandleRebirth(HttpListenerContext context)
        {
            long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
            if (playerId <= 0)
            {
                context.Response.StatusCode = 401;
                context.Response.Close();
                return;
            }

            RebirthRequestBody? body;
            try
            {
                string raw = await ReadBodyAsync(context);
                body = JsonSerializer.Deserialize<RebirthRequestBody>(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            }
            catch (JsonException)
            {
                body = null;
            }
            if (body == null || body.ExpectedRebirthCount < 0)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            // Not without the typed word - a reason, not a bare 400, so the
            // panel can say what is missing.
            if (!RebirthRules.IsConfirmed(body.Confirm))
            {
                await WriteRebirthAsync(context, 400, new RebirthOutcome(RebirthResult.ConfirmationRequired, body.ExpectedRebirthCount, 0, 0, 0, false));
                return;
            }

            var engine = new RebirthEngine(_serviceProvider);
            RebirthOutcome? outcome = null;

            // A live session: the tick owns the payload, so the rebirth runs
            // as the continuation of that payload's own flush.
            if (_playerSessionRegistry != null && _playerSessionRegistry.IsPlayerOnline(playerId))
            {
                var request = new FolkIdle.Server.Domain.Progression.RebirthRequest
                {
                    PlayerId = playerId,
                    ExpectedRebirthCount = body.ExpectedRebirthCount,
                    Engine = engine,
                };
                _playerSessionRegistry.RebirthRequestQueue.Enqueue(request);

                var finished = await Task.WhenAny(request.Completion.Task, Task.Delay(TimeSpan.FromSeconds(15)));
                if (finished != request.Completion.Task)
                {
                    await WriteRebirthAsync(context, 503, new RebirthOutcome(RebirthResult.Failed, body.ExpectedRebirthCount, 0, 0, 0, false));
                    return;
                }
                outcome = await request.Completion.Task;
            }

            // No live payload (offline, or between sign-in and the tick picking
            // the session up): wait out any checkpoint still queued for this
            // player, then reset directly.
            if (outcome == null)
            {
                if (_checkpointManager != null)
                {
                    await _checkpointManager.WaitForPendingFlushesAsync(playerId);
                }
                outcome = await engine.RebirthAsync(playerId, body.ExpectedRebirthCount);
            }

            int status = outcome.Value.Result switch
            {
                RebirthResult.Ok => 200,
                RebirthResult.AlreadyReborn => 409,
                RebirthResult.InFlight => 409,
                RebirthResult.NotFound => 404,
                _ => 500,
            };
            await WriteRebirthAsync(context, status, outcome.Value);
        }

        private static async Task WriteRebirthAsync(HttpListenerContext context, int status, RebirthOutcome outcome)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, new
            {
                Result = outcome.Result.ToString(),
                outcome.RebirthCount,
                outcome.RenownedRebirths,
                outcome.DamageBonusPct,
                outcome.ShardsEarned,
                outcome.Renowned,
            });
            context.Response.Close();
        }

        private async Task HandleAncestorsHall(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var player = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => new { p.PlayerGuid, p.AncestorSlotsPurchased, p.PremiumDiamonds })
                    .FirstOrDefaultAsync();

                if (player == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var characters = await db.CharacterRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .OrderBy(c => c.SlotIndex)
                    .ToListAsync();

                var characterIds = characters.ConvertAll(c => c.Id);
                var lineages = await db.CharacterLineages
                    .AsNoTracking()
                    .Where(l => characterIds.Contains(l.CharacterId))
                    .ToListAsync();

                var lineageById = new System.Collections.Generic.Dictionary<Guid, CharacterLineageRegistry>(lineages.Count);
                for (int i = 0; i < lineages.Count; i++) lineageById[lineages[i].CharacterId] = lineages[i];

                int greatWorkSlots = await Domain.Progression.GreatWorksEngine.HallSlotsAsync(db, playerId);
                int cap = Engine.HallOfAncestorsRules.CapFor(player.AncestorSlotsPurchased, greatWorkSlots);

                var ranking = new System.Collections.Generic.List<Engine.HallOfAncestorsRules.Member>(characters.Count);
                for (int i = 0; i < characters.Count; i++)
                {
                    lineageById.TryGetValue(characters[i].Id, out var lineage);
                    ranking.Add(new Engine.HallOfAncestorsRules.Member(
                        characters[i].Id,
                        characters[i].Id == player.PlayerGuid,
                        lineage?.IsKeptAtRollover ?? false,
                        lineage?.IsEpicMutation ?? false,
                        lineage is null ? 0 : lineage.AptitudeVector().Sum(),
                        lineage?.GenerationIndex ?? 0));
                }

                var carried = new System.Collections.Generic.HashSet<Guid>(
                    Engine.HallOfAncestorsRules.ChooseSurvivors(ranking, cap));

                int townHallLevel = await db.VillageInfrastructures
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId
                             && v.BuildingId == Domain.Progression.VillageManagementEngine.TownHallBuildingId)
                    .Select(v => v.CurrentLevel)
                    .FirstOrDefaultAsync();

                var payload = new
                {
                    Cap = cap,
                    MaxCap = Engine.HallOfAncestorsRules.MaxCapFor(greatWorkSlots),
                    GreatWorkSlots = greatWorkSlots,
                    SlotsPurchased = player.AncestorSlotsPurchased,
                    NextSlotCostDiamonds = Engine.HallOfAncestorsRules.NextSlotCostDiamonds(player.AncestorSlotsPurchased),
                    Diamonds = player.PremiumDiamonds,
                    PlayableSlots = Domain.Combat.CharacterSlotEngine.GetUnlockedSlotCount(townHallLevel),
                    Members = characters.ConvertAll(c =>
                    {
                        lineageById.TryGetValue(c.Id, out var lineage);
                        var genes = new GeneticVector(lineage?.GeneticVector ?? 0L);

                        // Modul: the pedigree read "b6b704ca x 0214b4e9" - Guid
                        // prefixes, a day after characters got names. A parent
                        // culled at a rollover is no longer in the roster, so
                        // an empty name means "not here any more", not unknown.
                        string NameOf(Guid? id) =>
                            id is { } value && characters.Find(x => x.Id == value) is { } parent ? parent.Name : string.Empty;

                        return new
                        {
                            CharacterId = c.Id.ToString(),
                            c.Name,
                            c.AgePhase,
                            c.IsFemale,
                            c.SlotIndex,
                            // -1 rather than null for "not fielded": the client
                            // compares this against a slot number and a null
                            // would have to be special-cased at every use.
                            PlayableSlot = c.SlotIndex < Domain.Combat.CharacterSlotEngine.MaxCharacterSlots ? c.SlotIndex : -1,
                            RaceId = (int)genes.LocusRace.Dominant,
                            GenerationIndex = lineage?.GenerationIndex ?? 0,
                            IsEpicMutation = lineage?.IsEpicMutation ?? false,
                            IsInbred = lineage?.IsInbred ?? false,
                            TraitMask = lineage?.TraitMask ?? 0L,
                            IsKept = lineage?.IsKeptAtRollover ?? false,
                            WouldCarry = carried.Contains(c.Id),
                            IsMainCharacter = c.Id == player.PlayerGuid,
                            AptitudeStrength = lineage?.AptitudeStrength ?? 0,
                            AptitudeSkill = lineage?.AptitudeSkill ?? 0,
                            AptitudeEndurance = lineage?.AptitudeEndurance ?? 0,
                            AptitudeFortune = lineage?.AptitudeFortune ?? 0,
                            // The pedigree. Empty rather than null for an
                            // unknown parent - a founder and a villager's child
                            // both have one, and neither is an error.
                            ParentPaternalId = lineage?.ParentPaternalId?.ToString() ?? string.Empty,
                            ParentMaternalId = lineage?.ParentMaternalId?.ToString() ?? string.Empty,
                            ParentPaternalName = NameOf(lineage?.ParentPaternalId),
                            ParentMaternalName = NameOf(lineage?.ParentMaternalId),
                        };
                    }),
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, payload);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HandleAncestorsHall failed: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        private async Task HandleBreedingRosterSnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var characters = await db.CharacterRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .ToListAsync();

                var characterIds = new System.Collections.Generic.List<Guid>(characters.Count);
                for (int i = 0; i < characters.Count; i++)
                {
                    characterIds.Add(characters[i].Id);
                }

                var lineages = await db.CharacterLineages
                    .AsNoTracking()
                    .Where(l => characterIds.Contains(l.CharacterId))
                    .ToListAsync();

                var lineageByCharacterId = new System.Collections.Generic.Dictionary<Guid, CharacterLineageRegistry>(lineages.Count);
                for (int i = 0; i < lineages.Count; i++)
                {
                    lineageByCharacterId[lineages[i].CharacterId] = lineages[i];
                }

                var response = new System.Collections.Generic.List<BreedingRosterEntryResponse>(characters.Count);
                for (int i = 0; i < characters.Count; i++)
                {
                    var character = characters[i];
                    if (!lineageByCharacterId.TryGetValue(character.Id, out var lineage))
                    {
                        continue;
                    }

                    var geneVec = new GeneticVector(lineage.GeneticVector);

                    response.Add(new BreedingRosterEntryResponse
                    {
                        CharacterId = character.Id.ToString(),
                        Name = character.Name,
                        AgePhase = character.AgePhase,
                        GenerationIndex = lineage.GenerationIndex,
                        IsBreedingActive = character.IsBreedingActive,
                        BreedingCooldownEndEpoch = character.BreedingCooldownEndEpoch,
                        IsEpicMutation = lineage.IsEpicMutation,
                        IsInbred = lineage.IsInbred,
                        IsFemale = character.IsFemale,
                        AptitudeStrength = lineage.AptitudeStrength,
                        AptitudeSkill = lineage.AptitudeSkill,
                        AptitudeEndurance = lineage.AptitudeEndurance,
                        AptitudeFortune = lineage.AptitudeFortune,
                        LocusRaceDominant = geneVec.LocusRace.Dominant,
                        LocusRaceRecessive = geneVec.LocusRace.Recessive,
                        TraitMask = lineage.TraitMask
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Breeding roster snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleBreedingTraits(HttpListenerContext context)
        {
            try
            {
                // Modul: static content, not per-player data - same convention
                // as HandleStoreCatalog/HandleCodexRegionsSnapshot. Nothing
                // player-specific is returned, but every sibling handler still
                // 401s an unauthenticated request rather than serving anyone
                // who can reach the port, and the client always calls this
                // through authedGet anyway.
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var catalogue = Engine.TraitRegistry.All.Select(t => new
                {
                    Id = t.Bit,
                    t.Key,
                    t.Name,
                    t.Description,
                    Rarity = t.Rarity.ToString(),
                    Effect = t.Effect.ToString(),
                    t.Value,
                }).ToList();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, catalogue);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Breeding traits error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
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
        }

        private static void AddAptitudePreviews(
            System.Collections.Generic.List<AptitudePreviewResponse> into, int[] hero, int[] partner)
        {
            for (int i = 0; i < Engine.BreedingAptitudes.Count; i++)
            {
                Engine.BreedingAptitudes.PreviewOne(hero[i], partner[i], out int min, out int max);
                into.Add(new AptitudePreviewResponse
                {
                    AptitudeName = Engine.BreedingAptitudes.NameOf(i),
                    ParentHero = hero[i],
                    ParentPartner = partner[i],
                    PredictedMin = min,
                    PredictedMax = max,
                });
            }
        }

        private static void AddTraitPreviews(BreedingPreviewResponse response, long heroMask, long partnerMask, int groundsLevel)
        {
            foreach (var odds in Engine.BreedingTraits.PreviewOdds(heroMask, partnerMask))
            {
                response.TraitOdds.Add(new TraitOddsResponse { TraitId = odds.Bit, ChancePct = odds.ChancePct, Source = odds.Source });
            }
            response.MutationChancePct = Engine.BreedingTraits.MutationPercentFor(groundsLevel);
            response.FlawChancePct = Engine.BreedingTraits.FlawPercentFor(response.IsInbredRisk);
        }

        private static async Task<int> BreedingGroundsLevelAsync(FolkIdleDbContext db, long playerId)
            => await db.VillageInfrastructures
                .AsNoTracking()
                .Where(v => v.PlayerId == playerId && v.BuildingId == Domain.Progression.VillageManagementEngine.BreedingGroundsBuildingId)
                .Select(v => (int?)v.CurrentLevel)
                .SingleOrDefaultAsync() ?? 0;

        // Modul 13.4.3: read-only preview of ExecuteBreedingAsync's outcome -
        // never writes to the DB. Mirrors that engine's own ownership,
        // eligibility, and inbreeding checks exactly (see BreedingEngine.
        // ExecuteBreedingAsync) so a preview can never promise a pairing the
        // real execute call would actually reject, but computes the gene
        // spectrum via GeneticSplicingEngine.PreviewLocus (an exact
        // enumeration of Breed()'s possible non-mutated outcomes) instead of
        // performing the real, single-sample random splice.
        private async Task HandleBreedingPreview(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                if (!Guid.TryParse(query["paternalId"], out Guid paternalId) || !Guid.TryParse(query["maternalId"], out Guid maternalId))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                if (!ClientCommandValidator.ValidateBreedingPreviewQuery(playerId, paternalId, maternalId))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var pChar = await db.CharacterRecords.AsNoTracking().FirstOrDefaultAsync(c => c.Id == paternalId);
                var mChar = await db.CharacterRecords.AsNoTracking().FirstOrDefaultAsync(c => c.Id == maternalId);
                var pLineage = await db.CharacterLineages.AsNoTracking().FirstOrDefaultAsync(l => l.CharacterId == paternalId);
                var mLineage = await db.CharacterLineages.AsNoTracking().FirstOrDefaultAsync(l => l.CharacterId == maternalId);

                if (pChar == null || mChar == null || pLineage == null || mLineage == null || pChar.PlayerId != playerId || mChar.PlayerId != playerId)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var response = new BreedingPreviewResponse();

                long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var pVec = new GeneticVector(pLineage.GeneticVector);
                var mVec = new GeneticVector(mLineage.GeneticVector);

                // Modul: THE SAME GATE THE ENGINE USES. This block used to be a
                // hand-copied second version of the engine's rules, and a
                // comment beside it said the two "must refuse the same pairs or
                // the preview lies again" - which is a thing to enforce, not to
                // write down. It had already drifted once: race was checked and
                // sex was not, so a woman chosen as the paternal parent priced
                // an ELIGIBLE pairing that the engine then rolled back without
                // a word. One call now, and BreedingGateTests fails if either
                // side grows a rule of its own.
                var previewRefusal = Engine.BreedingGateRules.CheckPair(
                    ToGateParent(pChar, pLineage.GeneticVector),
                    ToGateParent(mChar, mLineage.GeneticVector),
                    nowEpoch);

                response.IsEligible = previewRefusal == Engine.BreedingRefusal.None;
                response.IneligibleReason = Engine.BreedingGateRules.ReasonSlugFor(previewRefusal);

                // The same relatedness check the engine runs - it used to be a
                // hand-copied expression here too, and both copies missed cousins.
                response.IsInbredRisk = await Engine.BreedingRelatedness.AreRelatedAsync(db, pLineage, mLineage);

                int maxGen = Math.Max(pLineage.GenerationIndex, mLineage.GenerationIndex);
                response.BreedingCostGold = Engine.BreedingEngine.CostFor(maxGen);

                var goldRecord = await db.CommodityRecords
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
                response.HasSufficientGold = goldRecord != null && goldRecord.Quantity >= response.BreedingCostGold;

                AddLocusPreview(response.Loci, "Race", pVec.LocusRace, mVec.LocusRace, maxGen);
                AddAptitudePreviews(response.Aptitudes, pLineage.AptitudeVector(), mLineage.AptitudeVector());
                AddTraitPreviews(response, pLineage.TraitMask, mLineage.TraitMask, await BreedingGroundsLevelAsync(db, playerId));

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Breeding preview error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: hero x villager preview. Mirrors
        // BreedingEngine.ExecuteHeroVillagerBreedingAsync's refusals in the same
        // order, for the same reason the character preview mirrors its own
        // engine: a preview that can promise a pairing the execute call rejects
        // is worse than no preview, because the player learns nothing from the
        // silence that follows.
        private async Task HandleVillagerBreedingPreview(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                if (!Guid.TryParse(query["heroId"], out Guid heroId)
                    || !long.TryParse(query["newcomerId"], out long newcomerId)
                    || heroId == Guid.Empty
                    || newcomerId <= 0)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var hero = await db.CharacterRecords.AsNoTracking().FirstOrDefaultAsync(c => c.Id == heroId);
                var heroLineage = await db.CharacterLineages.AsNoTracking().FirstOrDefaultAsync(l => l.CharacterId == heroId);
                var newcomer = await db.VillageNewcomers.AsNoTracking().FirstOrDefaultAsync(v => v.Id == newcomerId);

                if (hero == null || heroLineage == null || newcomer == null
                    || hero.PlayerId != playerId || newcomer.PlayerId != playerId)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var response = new BreedingPreviewResponse();

                long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var heroVec = new GeneticVector(heroLineage.GeneticVector);
                var villagerVec = new GeneticVector(newcomer.Genome());

                // The same gate the engine uses - see the roster preview above
                // for why this is a call rather than a second copy of the rules.
                var previewRefusal = Engine.BreedingGateRules.CheckVillagerPair(
                    ToGateParent(hero, heroLineage.GeneticVector),
                    newcomer.IsFemale,
                    (byte)newcomer.RaceId,
                    newcomer.IsElder,
                    nowEpoch);

                response.IsEligible = previewRefusal == Engine.BreedingRefusal.None;
                response.IneligibleReason = Engine.BreedingGateRules.ReasonSlugFor(previewRefusal);

                // Never. A villager has no parents here and marries once.
                response.IsInbredRisk = false;

                int maxGen = heroLineage.GenerationIndex;
                response.BreedingCostGold = Engine.BreedingEngine.CostFor(maxGen);

                var goldRecord = await db.CommodityRecords
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
                response.HasSufficientGold = goldRecord != null && goldRecord.Quantity >= response.BreedingCostGold;

                AddLocusPreview(response.Loci, "Race", heroVec.LocusRace, villagerVec.LocusRace, maxGen);
                AddAptitudePreviews(response.Aptitudes, heroLineage.AptitudeVector(), newcomer.AptitudeVector());
                AddTraitPreviews(response, heroLineage.TraitMask, newcomer.TraitMask, await BreedingGroundsLevelAsync(db, playerId));

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Villager breeding preview error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private static void AddLocusPreview(System.Collections.Generic.List<GenePreviewLocusResponse> loci, string name, Locus pLocus, Locus mLocus, int maxGeneration)
        {
            GeneticSplicingEngine.PreviewLocus(pLocus, mLocus, maxGeneration, out byte minDominant, out byte maxDominant, out double mutationChancePct);

            loci.Add(new GenePreviewLocusResponse
            {
                LocusName = name,
                ParentPaternalDominant = pLocus.Dominant,
                ParentMaternalDominant = mLocus.Dominant,
                PredictedMinDominant = minDominant,
                PredictedMaxDominant = maxDominant,
                MutationChancePct = mutationChancePct
            });
        }

        // Modul 13: authorized snapshot of the player's real Race Mastery
        // progress. PlayerRaceMasteries is already populated by CodexEngine's
        // kill-event cron. Uses plain LINQ rather than raw SQL - the table is
        // mapped via [Table("player_race_masteries")] (lowercase, unlike every
        // other table in this codebase), and EF Core resolves that mapping
        // correctly on its own, sidestepping manual identifier quoting entirely.
        private async Task HandleMasterySnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var entries = await db.PlayerRaceMasteries
                    .AsNoTracking()
                    .Where(m => m.PlayerId == playerId)
                    .ToListAsync();

                var response = new System.Collections.Generic.List<RaceMasterySnapshotEntryResponse>(entries.Count);

                foreach (var entry in entries)
                {
                    response.Add(new RaceMasterySnapshotEntryResponse
                    {
                        RaceId = entry.RaceId,
                        Level = entry.MasteryLevel,
                        Experience = entry.CumulativeXp,
                        NextLevelExperience = CodexEngine.GetRaceMasteryRequiredXp(entry.MasteryLevel)
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Race mastery snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
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

        /// <summary>
        /// Task 110e. The newest world, announcement and (own) guild messages,
        /// oldest first, so a player signing in does not read "Nothing in this
        /// channel yet" over a channel that was busy a minute ago. Filtering is
        /// ChatHistory.ReadRecentAsync's: the reader's current guild only, and
        /// nothing from anyone the reader has blocked. Whispers are not here.
        /// </summary>
        private async Task HandleChatRecent(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var recent = await ChatHistory.ReadRecentAsync(db, playerId);

                await transaction.CommitAsync();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, recent);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chat history error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>
        /// One row per person this player has exchanged messages with, newest
        /// first, with the last line and an unread count.
        /// </summary>
        private async Task HandleConversationList(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                // Modul: the pair is stored sorted, so "my threads" is the rows
                // where I am either end. The counterpart is then whichever end
                // is not me - which is why sender and recipient are kept
                // alongside the sorted pair rather than derived from it.
                var mine = await db.ConversationMessages
                    .AsNoTracking()
                    .Where(m => m.LowPlayerId == playerId || m.HighPlayerId == playerId)
                    .OrderByDescending(m => m.SentAtEpochMs)
                    .ToListAsync();

                var counterpartIds = mine
                    .Select(m => m.LowPlayerId == playerId ? m.HighPlayerId : m.LowPlayerId)
                    .Distinct()
                    .ToList();

                var names = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => counterpartIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, p => p.Username);

                await transaction.CommitAsync();

                var summaries = new System.Collections.Generic.List<ConversationSummaryResponse>(counterpartIds.Count);
                foreach (long other in counterpartIds)
                {
                    var thread = mine.Where(m => m.LowPlayerId == other || m.HighPlayerId == other).ToList();
                    var latest = thread[0]; // already ordered newest first

                    summaries.Add(new ConversationSummaryResponse
                    {
                        PlayerId = other,
                        Username = names.TryGetValue(other, out string? name) && name != null ? name : "(unknown player)",
                        LastMessage = latest.MessageText,
                        LastMessageAtEpochMs = latest.SentAtEpochMs,
                        LastMessageWasMine = latest.SenderPlayerId == playerId,
                        // Unread means addressed TO me and not yet opened. A
                        // message cannot be unread by the person who sent it.
                        UnreadCount = thread.Count(m => m.RecipientPlayerId == playerId && m.ReadAtEpochMs == null),
                        IsOnline = _connectedClients.ContainsKey(other)
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, summaries);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Conversation list error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>
        /// One thread, oldest-last, capped. `withPlayerId` names the other
        /// person; `before` pages backwards through time.
        /// </summary>
        private async Task HandleConversationHistory(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                if (!long.TryParse(context.Request.QueryString["withPlayerId"], out long otherPlayerId) || otherPlayerId <= 0)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                // Paging is by timestamp rather than offset: new messages
                // arrive while a player scrolls, and an offset would then skip
                // or repeat a line as the window shifts under it.
                long before = long.TryParse(context.Request.QueryString["before"], out long b) && b > 0
                    ? b
                    : long.MaxValue;

                const int PageSize = 50;

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var (low, high) = ConversationMessage.PairKey(playerId, otherPlayerId);

                var page = await db.ConversationMessages
                    .AsNoTracking()
                    .Where(m => m.LowPlayerId == low && m.HighPlayerId == high && m.SentAtEpochMs < before)
                    .OrderByDescending(m => m.SentAtEpochMs)
                    .Take(PageSize)
                    .ToListAsync();

                await transaction.CommitAsync();

                // Returned oldest-first, which is the order a conversation is
                // read in. The query runs newest-first because that is what the
                // index serves and what paging needs.
                page.Reverse();

                var response = page.Select(m => new ConversationMessageResponse
                {
                    Id = m.Id,
                    SenderPlayerId = m.SenderPlayerId,
                    Mine = m.SenderPlayerId == playerId,
                    MessageText = m.MessageText,
                    SentAtEpochMs = m.SentAtEpochMs,
                    Read = m.ReadAtEpochMs != null
                }).ToList();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Conversation history error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>Marks everything the caller has RECEIVED in one thread as read.</summary>
        private async Task HandleConversationMarkRead(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                if (!payload.TryGetProperty("withPlayerId", out var withProp)
                    || !withProp.TryGetInt64(out long otherPlayerId)
                    || otherPlayerId <= 0)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var (low, high) = ConversationMessage.PairKey(playerId, otherPlayerId);
                long nowEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // Modul: RecipientPlayerId == playerId is what makes this safe
                // to expose. The caller can only ever mark their own incoming
                // messages read - naming somebody else's thread marks nothing,
                // because no row in it is addressed to them.
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE conversation_messages SET \"ReadAtEpochMs\" = {0} " +
                    "WHERE \"LowPlayerId\" = {1} AND \"HighPlayerId\" = {2} " +
                    "AND \"RecipientPlayerId\" = {3} AND \"ReadAtEpochMs\" IS NULL",
                    nowEpochMs, low, high, playerId);

                context.Response.StatusCode = 200;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Conversation mark-read error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleFriendsList(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var relationships = await db.PlayerRelationships
                    .AsNoTracking()
                    .Where(r => r.PlayerId == playerId)
                    .ToListAsync();

                var targetIds = relationships.Select(r => r.TargetPlayerId).ToList();
                var targets = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => targetIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, p => p);

                await transaction.CommitAsync();

                var response = new System.Collections.Generic.List<FriendEntryResponse>(relationships.Count);
                foreach (var rel in relationships)
                {
                    targets.TryGetValue(rel.TargetPlayerId, out var target);
                    response.Add(new FriendEntryResponse
                    {
                        PlayerId = rel.TargetPlayerId,
                        Username = target?.Username ?? "(unknown player)",
                        Level = target?.CurrentLevel ?? 0,
                        IsBlocked = rel.RelationType == RelationType.Blocked,

                        // Live connection table rather than a persisted column:
                        // a stored "is online" flag goes stale the moment a
                        // process dies without a clean logout, and would then
                        // claim someone is online forever.
                        //
                        // POD-LOCAL. _connectedClients is this pod's own
                        // WebSocket table, so a friend connected to a different
                        // pod reads as offline. Correct for a single-pod
                        // deployment, which is what this runs as today; a
                        // multi-pod answer needs a Redis presence key, and
                        // inventing one here would be a second source of truth
                        // about who is online. Recorded rather than faked.
                        IsOnline = _connectedClients.ContainsKey(rel.TargetPlayerId)
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Friends list error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: UI audit follow-up. AddFriend/BlockPlayer (RelationshipEngine,
        // WebSocketClient.SendAddFriendCommandZeroAlloc/SendBlockPlayerCommandZeroAlloc)
        // only ever took a raw numeric TargetPlayerId with no client-side way
        // to discover it - this resolves the one piece of public information
        // a player would actually type, their username, to that Id.
        // It is now case-insensitive using ILike so players don't have to match case perfectly.
        private async Task HandlePlayerResolve(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                string username = (query["username"] ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(username))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                long targetPlayerId = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Username != null && EF.Functions.ILike(p.Username, username))
                    .Select(p => p.Id)
                    .FirstOrDefaultAsync();

                await transaction.CommitAsync();

                if (targetPlayerId <= 0)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new PlayerResolveResponse { PlayerId = targetPlayerId });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player resolve error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: ONE COMPACT ANSWER, built by PublicProfiles.BuildProfileAsync.
        // This used to serialise every CharacterRecord on the account whole
        // (185 on the dev fixture) plus raw equipment entities, and the client
        // drew a blank "Level" per bred child. A profile opens on a tap, so its
        // cost is paid by a waiting thumb - see PublicProfiles for the shape.
        private async Task HandlePlayerProfile(HttpListenerContext context)
        {
            try
            {
                long requesterId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (requesterId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                if (!long.TryParse(query["id"], out long targetId) || targetId <= 0)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var view = await PublicProfiles.BuildProfileAsync(db, targetId, id => _connectedClients.ContainsKey(id));
                if (view == null)
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, view);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Profile fetch error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        // Modul: A GUILD SEEN FROM OUTSIDE, opened from a member's profile. The
        // directory is for guildless players only (task 107), so this is how a
        // guild member looks at another guild: by id, read-only, and with only
        // the fields a stranger may see - no treasury gold, no depot, no
        // per-member contribution, no applications. See PublicProfiles.
        private async Task HandleGuildView(HttpListenerContext context)
        {
            try
            {
                long requesterId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (requesterId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                if (!long.TryParse(query["id"], out long guildId) || guildId <= 0)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var view = await PublicProfiles.BuildGuildAsync(db, guildId, requesterId, id => _connectedClients.ContainsKey(id));
                if (view == null)
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, view);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild view error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleWorldBossBoard(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var view = await FolkIdle.Server.Domain.Combat.WorldBossStrike.WorldBossBoard.ViewAsync(db, playerId);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, view);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"World boss board error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleDeepestLeaderboard(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var rows = await FolkIdle.Server.Domain.Economy.DeepestBoard.TopAsync(db, DateTime.UtcNow);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { Entries = rows });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Deepest board error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task WriteTitlesAsync(HttpListenerContext context, FolkIdleDbContext db, long playerId, string? result)
        {
            var player = await db.PlayerRecords.AsNoTracking().SingleOrDefaultAsync(p => p.Id == playerId);
            var titles = await FolkIdle.Server.Domain.Progression.TitleEngine.ListAsync(db, playerId);
            var active = FolkIdle.Server.Domain.Progression.TitleRegistry.Find(player?.ActiveTitleSlug);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, new
            {
                Result = result,
                Titles = titles.Select(t => new { t.Slug, t.Name, t.EarnedAtUtc }),
                Active = active == null ? null : new { active.Slug, Name = active.DisplayName },
            });
        }

        private async Task HandleTitlesView(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await WriteTitlesAsync(context, db, playerId, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Titles view error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>
        /// Wear a title, or clear it with { "Slug": null }. An unearned or
        /// unknown slug answers 200 with its Result - NotEarned / UnknownTitle -
        /// and changes nothing, so the screen can say why.
        /// </summary>
        private async Task HandleTitleSet(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                string? slug;
                try
                {
                    using var parsed = JsonDocument.Parse(await ReadBodyAsync(context));
                    if (!parsed.RootElement.TryGetProperty("Slug", out var slugElement)) { context.Response.StatusCode = 400; return; }
                    if (slugElement.ValueKind == JsonValueKind.Null) slug = null;
                    else if (slugElement.ValueKind == JsonValueKind.String) slug = slugElement.GetString();
                    else { context.Response.StatusCode = 400; return; }
                }
                catch (JsonException)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var result = await FolkIdle.Server.Domain.Progression.TitleEngine.SetActiveAsync(db, playerId, slug);
                await WriteTitlesAsync(context, db, playerId, result.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Title set error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleGuildKick(HttpListenerContext context)
        {
            if (context.Request.HttpMethod != "POST") { context.Response.StatusCode = 405; context.Response.Close(); return; }
            try
            {
                long kickerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (kickerId <= 0) { context.Response.StatusCode = 401; context.Response.Close(); return; }

                var payload = JsonSerializer.Deserialize<JsonElement>(await ReadBodyAsync(context));
                if (!payload.TryGetProperty("targetPlayerId", out var targetEl)) { context.Response.StatusCode = 400; context.Response.Close(); return; }
                long targetId = targetEl.GetInt64();

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException());

                bool ok = await guildManagementEngine.KickMemberAsync(kickerId, targetId);
                context.Response.StatusCode = ok ? 200 : 403;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild kick error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }

        private async Task HandleGuildPromote(HttpListenerContext context)
        {
            if (context.Request.HttpMethod != "POST") { context.Response.StatusCode = 405; context.Response.Close(); return; }
            try
            {
                long promoterId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (promoterId <= 0) { context.Response.StatusCode = 401; context.Response.Close(); return; }

                var payload = JsonSerializer.Deserialize<JsonElement>(await ReadBodyAsync(context));
                if (!payload.TryGetProperty("targetPlayerId", out var targetEl)) { context.Response.StatusCode = 400; context.Response.Close(); return; }
                long targetId = targetEl.GetInt64();

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException());

                bool ok = await guildManagementEngine.PromoteMemberAsync(promoterId, targetId);
                context.Response.StatusCode = ok ? 200 : 403;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild promote error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }

        private async Task HandleStatsOnline(HttpListenerContext context)
        {
            try
            {
                int count = _connectedClients.Count;
                string json = "{\"OnlineCount\":" + count + "}";
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                // Modul: this answered 500 and logged nothing - the silent-failure
                // shape CLAUDE.md warns about. Say why, in the file's own style.
                Console.WriteLine($"Stats online failed: {ex.Message}");
                if (context != null && context.Response != null)
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
            }
        }

        private async Task HandleGuildDemote(HttpListenerContext context)
        {
            if (context.Request.HttpMethod != "POST") { context.Response.StatusCode = 405; context.Response.Close(); return; }
            try
            {
                long demoterId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (demoterId <= 0) { context.Response.StatusCode = 401; context.Response.Close(); return; }

                var payload = JsonSerializer.Deserialize<JsonElement>(await ReadBodyAsync(context));
                if (!payload.TryGetProperty("targetPlayerId", out var targetEl)) { context.Response.StatusCode = 400; context.Response.Close(); return; }
                long targetId = targetEl.GetInt64();

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException());

                bool ok = await guildManagementEngine.DemoteMemberAsync(demoterId, targetId);
                context.Response.StatusCode = ok ? 200 : 403;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild demote error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }

        // Modul: Inventory screen. One read-only snapshot covering all three
        // places a player's belongings actually live: EquipmentInstances
        // (backpack gear), CommodityRecords (carried material stacks) and
        // VillageStashInstances (the overflow stash CombatLootEngine spills
        // into when the backpack is full). Equipped state comes from
        // PlayerRecord's three equipped-id columns, which is what
        // EquipmentSlotEngine itself treats as authoritative.
        private async Task HandlePlayerInventorySnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var player = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(pr => pr.Id == playerId);

                var equipment = await db.EquipmentInstances
                    .AsNoTracking()
                    .Where(e => e.PlayerId == playerId)
                    .ToListAsync();

                var commodities = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .ToListAsync();

                var stash = await db.VillageStashInstances
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId)
                    .ToListAsync();

                // Modul: TWO DIFFERENT QUESTIONS, TWO DIFFERENT QUERIES.
                //
                // "Who wears this item" must consider EVERY character the player
                // owns, including the benched ones past slot 2. A benched
                // character really does keep its EquippedWeaponId - measured: a
                // SlotIndex 4 character on the dev fixture holds a weapon and an
                // axe - so narrowing this to the playable three would report
                // those items as free, offer them in the picker, and let one
                // EquipmentInstance end up worn by two characters at once the
                // moment that ancestor is fielded.
                //
                // "What is this character's combat rating" is only ever asked
                // about the three PLAYABLE slots, which is what the paper doll
                // has tabs for - and computing it walks the gear of every row it
                // is given, so handing it the whole lineage would cost 20+
                // needless stat computations per inventory fetch on a long-played
                // account.
                // Two queries rather than one filtered in memory: the worn-item
                // pass needs no lineage at all, and joining it for a whole
                // lineage - 29 rows on the dev fixture, more on a long-played
                // account - to read race off three of them is work this endpoint
                // does on every inventory refresh.
                var allCharacters = await db.CharacterRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .ToListAsync();

                var rosterCharacters = await db.CharacterRecords
                    .AsNoTracking()
                    .Include(c => c.Lineage)
                    .Where(c => c.PlayerId == playerId
                        && c.SlotIndex >= 0
                        && c.SlotIndex < CharacterSlotEngine.MaxCharacterSlots)
                    .OrderBy(c => c.SlotIndex)
                    .ThenBy(c => c.Id)
                    .ToListAsync();

                // Modul: paper-doll combat rating, per character. STR/DEX/CON/
                // LCK, potions, area completions and race mastery are all
                // account-wide (see PlayerRecord/PlayerRaceMastery) - only age
                // phase, race/genetics and gear differ per roster slot - so
                // these are fetched once here rather than once per character.
                var raceMasteries = await db.PlayerRaceMasteries
                    .AsNoTracking()
                    .Where(m => m.PlayerId == playerId)
                    .ToListAsync();
                int humanMastery = 0, vilaMastery = 0, draugrMastery = 0;
                for (int i = 0; i < raceMasteries.Count; i++)
                {
                    if (raceMasteries[i].RaceId == RaceIds.Human) humanMastery = raceMasteries[i].MasteryLevel;
                    else if (raceMasteries[i].RaceId == RaceIds.Vila) vilaMastery = raceMasteries[i].MasteryLevel;
                    else if (raceMasteries[i].RaceId == RaceIds.Draugr) draugrMastery = raceMasteries[i].MasteryLevel;
                }
                var completedRegionIds = await db.PlayerRegionCompletions
                    .AsNoTracking()
                    .Where(r => r.PlayerId == playerId)
                    .Select(r => r.RegionId)
                    .ToListAsync();
                int completedAreaFlags = 0;
                for (int i = 0; i < completedRegionIds.Count; i++)
                {
                    completedAreaFlags |= 1 << completedRegionIds[i];
                }

                await transaction.CommitAsync();

                // Modul: per-character equipment. "Is this item equipped" is an
                // account-wide question for the inventory screen - an item worn
                // by the second character is just as unavailable as one worn by
                // the first, and showing it as free to sell would be a lie.
                // Modul: roster loadouts. Maps each worn item to the SLOT INDEX
                // of the character wearing it, rather than only recording that
                // somebody wears it. The wire deliberately carries only the
                // active character's gear (gear changes on a button press, not
                // at 10Hz), so this snapshot is the only place the Roster screen
                // can learn what characters 2 and 3 are actually wearing.
                //
                // -1 means "not worn". Slot index rather than character Guid
                // because the Roster screen is laid out by slot and would only
                // have to map the Guid back anyway.
                var wornItemSlotIndices = new Dictionary<long, int>();
                var wornItemEquipSlots = new Dictionary<long, int>();
                foreach (var rosterCharacter in allCharacters)
                {
                    void RecordWorn(long? itemId, int equipSlotIndex)
                    {
                        if (!itemId.HasValue) return;
                        wornItemSlotIndices[itemId.Value] = rosterCharacter.SlotIndex;
                        wornItemEquipSlots[itemId.Value] = equipSlotIndex;
                    }

                    RecordWorn(rosterCharacter.EquippedWeaponId, EquipmentSlotEngine.SlotWeapon);
                    RecordWorn(rosterCharacter.EquippedHelmetId, EquipmentSlotEngine.SlotHelmet);
                    RecordWorn(rosterCharacter.EquippedChestId, EquipmentSlotEngine.SlotChest);
                    RecordWorn(rosterCharacter.EquippedGlovesId, EquipmentSlotEngine.SlotGloves);
                    RecordWorn(rosterCharacter.EquippedLeggingsId, EquipmentSlotEngine.SlotLeggings);
                    RecordWorn(rosterCharacter.EquippedBootsId, EquipmentSlotEngine.SlotBoots);
                    RecordWorn(rosterCharacter.EquippedAmuletId, EquipmentSlotEngine.SlotAmulet);
                    RecordWorn(rosterCharacter.EquippedRingId, EquipmentSlotEngine.SlotRing);

                    // Modul: THE THREE TOOL SLOTS, missing here since tools
                    // were added. This is the whole of "I equip a tool and
                    // nothing appears in the slot": the equip SUCCEEDS and
                    // EquippedAxeId is written, but this snapshot is the only
                    // thing the Character screen reads to decide what a slot
                    // holds. Unrecorded meant EquippedByCharacterSlot came back
                    // -1, so the doll drew the slot empty AND the tool stayed
                    // in its own picker as available - the item was worn and
                    // offered at the same time.
                    RecordWorn(rosterCharacter.EquippedAxeId, EquipmentSlotEngine.SlotAxe);
                    RecordWorn(rosterCharacter.EquippedPickaxeId, EquipmentSlotEngine.SlotPickaxe);
                    RecordWorn(rosterCharacter.EquippedRodId, EquipmentSlotEngine.SlotRod);
                }

                var rosterCombatStats = new System.Collections.Generic.List<RosterCombatStatsResponse>();
                if (player != null)
                {
                    foreach (var rosterCharacter in rosterCharacters)
                    {
                        (CombatStats stats, EquippedSetIds wornSets) = await EquipmentSlotEngine.ComputeCharacterCombatStatsWithSetsAsync(
                            db, player, rosterCharacter, humanMastery, vilaMastery, draugrMastery, completedAreaFlags);

                        var activeSets = new System.Collections.Generic.List<ActiveSetResponse>();
                        foreach (var set in SetBonusEngine.DescribeActive(wornSets))
                        {
                            activeSets.Add(new ActiveSetResponse
                            {
                                SetId = set.SetId,
                                Family = ArmourSetRegistry.FamilyOfSetId(set.SetId),
                                Offensive = SetBonusEngine.IsOffensiveSet(set.SetId),
                                Pieces = set.Pieces,
                                Tier = set.Tier,
                                NextTierPieces = set.Tier == 1 ? SetBonusEngine.TierTwoPieces : set.Tier == 2 ? SetBonusEngine.TierThreePieces : 0,
                                QualityScale = Math.Round(set.QualityScale, 2),
                                DamagePct = Math.Round(set.Bonus.FireDamageMultiplierPct, 1),
                                ArmorPct = Math.Round(set.Bonus.TotalArmorMultiplierPct, 1),
                                Burn = set.Bonus.BurnApplicationActive,
                                Thorns = set.Bonus.ThornsReflectionActive,
                                DamageCap = set.Bonus.DamageCapActive,
                            });
                        }

                        rosterCombatStats.Add(new RosterCombatStatsResponse
                        {
                            SlotIndex = rosterCharacter.SlotIndex,
                            Accuracy = stats.AccuracyRating,
                            Armor = stats.FlatPhysicalArmor,
                            BlockPct = stats.BlockStrengthPct,
                            ActiveSets = activeSets
                        });
                    }
                }

                var response = new PlayerInventorySnapshotResponse
                {
                    BackpackSlotsUsed = equipment.Count,
                    // Modul: unlimited village chest. Was
                    // VillageStashInstance.MaxStackQuantity (9999), which the
                    // Inventory screen rendered as "stacks cap at 9999". There
                    // is no cap any more, so 0 is the agreed "unbounded"
                    // sentinel and the client suppresses the line entirely.
                    MaxStackQuantity = 0L,
                    RosterCombatStats = rosterCombatStats
                };

                for (int i = 0; i < equipment.Count; i++)
                {
                    var item = equipment[i];

                    var affixes = new Dictionary<string, int>();
                    bool payloadLockFlag = false;
                    if (!string.IsNullOrWhiteSpace(item.AffixPayload) &&
                        System.Text.Json.Nodes.JsonNode.Parse(item.AffixPayload) is System.Text.Json.Nodes.JsonObject affixObject)
                    {
                        foreach (var kvp in affixObject)
                        {
                            if (kvp.Value is not System.Text.Json.Nodes.JsonValue affixValue) continue;

                            if (kvp.Key == "is_affix_locked")
                            {
                                payloadLockFlag = affixValue.TryGetValue(out bool lockedFlag) && lockedFlag;
                                continue;
                            }

                            if (affixValue.TryGetValue(out int magnitude))
                            {
                                affixes[kvp.Key] = magnitude;
                            }
                        }
                    }

                    response.Equipment.Add(new InventoryEquipmentResponse
                    {
                        Id = item.Id,
                        BaseItemId = item.BaseItemId,
                        QualityTier = item.QualityTier,
                        IsEquipped = wornItemSlotIndices.ContainsKey(item.Id),
                        // Modul: roster loadouts. -1 when carried rather than worn.
                        EquippedByCharacterSlot = wornItemSlotIndices.TryGetValue(item.Id, out int wearerSlotIndex) ? wearerSlotIndex : -1,
                        EquippedInSlotIndex = wornItemEquipSlots.TryGetValue(item.Id, out int wornEquipSlot) ? wornEquipSlot : -1,
                        Affixes = affixes,
                        IsAffixLocked = item.IsAffixLocked || payloadLockFlag
                    });
                }

                // Both tables are summed into one row per item. They are one
                // store as far as every consumer is concerned - see
                // InventoryStackResponse.Quantity.
                var stacksByItemId = new System.Collections.Generic.Dictionary<string, InventoryStackResponse>(commodities.Count + stash.Count);

                for (int i = 0; i < commodities.Count; i++)
                {
                    var row = commodities[i];
                    if (!stacksByItemId.TryGetValue(row.ItemId, out var stack))
                    {
                        stack = new InventoryStackResponse { ItemId = row.ItemId };
                        stacksByItemId[row.ItemId] = stack;
                    }
                    stack.Quantity += row.Quantity;
                }

                for (int i = 0; i < stash.Count; i++)
                {
                    var row = stash[i];
                    if (!stacksByItemId.TryGetValue(row.ItemId, out var stack))
                    {
                        stack = new InventoryStackResponse { ItemId = row.ItemId };
                        stacksByItemId[row.ItemId] = stack;
                    }
                    stack.Quantity += row.Quantity;
                }

                response.Stacks.AddRange(stacksByItemId.Values);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player inventory snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
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
        }

        // Modul: THE MAIN CHARACTER's gear, because that is who EquipItem with
        // Guid.Empty dresses (ResolveCharacterForUpdateAsync: the character whose
        // Id is PlayerRecords.PlayerGuid). The loot list's Wear sends no target,
        // so comparing against anyone else would compare against a piece the
        // press does not replace. All ELEVEN slots - the tools included.
        private async Task HandlePlayerWornSnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                // Modul: ?characterId= names whose gear to compare against
                // (2026-10-08). A drop belongs to whichever character is
                // fighting, and that is not always the main one - comparing a
                // fighter's boots drop against the main character's empty boot
                // slot told the player "+1000 DEF, boots slot is empty" while
                // the fighter wore Transcendent boots. Must be this player's
                // own character; anything else falls back to the main one.
                var mainGuid = await db.PlayerRecords.AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.PlayerGuid)
                    .FirstOrDefaultAsync();
                if (Guid.TryParse(context.Request.QueryString["characterId"], out var askedFor) && askedFor != Guid.Empty)
                {
                    mainGuid = askedFor;
                }
                var main = mainGuid == Guid.Empty
                    ? null
                    : await db.CharacterRecords.AsNoTracking()
                        .FirstOrDefaultAsync(c => c.Id == mainGuid && c.PlayerId == playerId);

                var pieces = new List<WornPieceResponse>(11);
                if (main != null)
                {
                    var slotByInstance = new Dictionary<long, int>(11);
                    void Worn(long? id, int slot)
                    {
                        if (id.HasValue) slotByInstance[id.Value] = slot;
                    }
                    Worn(main.EquippedWeaponId, EquipmentSlotEngine.SlotWeapon);
                    Worn(main.EquippedHelmetId, EquipmentSlotEngine.SlotHelmet);
                    Worn(main.EquippedChestId, EquipmentSlotEngine.SlotChest);
                    Worn(main.EquippedGlovesId, EquipmentSlotEngine.SlotGloves);
                    Worn(main.EquippedLeggingsId, EquipmentSlotEngine.SlotLeggings);
                    Worn(main.EquippedBootsId, EquipmentSlotEngine.SlotBoots);
                    Worn(main.EquippedAmuletId, EquipmentSlotEngine.SlotAmulet);
                    Worn(main.EquippedRingId, EquipmentSlotEngine.SlotRing);
                    Worn(main.EquippedAxeId, EquipmentSlotEngine.SlotAxe);
                    Worn(main.EquippedPickaxeId, EquipmentSlotEngine.SlotPickaxe);
                    Worn(main.EquippedRodId, EquipmentSlotEngine.SlotRod);

                    var ids = slotByInstance.Keys.ToList();
                    var rows = await db.EquipmentInstances.AsNoTracking()
                        .Where(e => e.PlayerId == playerId && ids.Contains(e.Id))
                        .Select(e => new { e.Id, e.BaseItemId, e.QualityTier })
                        .ToListAsync();
                    foreach (var row in rows)
                    {
                        pieces.Add(new WornPieceResponse
                        {
                            InstanceId = row.Id,
                            BaseItemId = row.BaseItemId,
                            QualityTier = row.QualityTier,
                            SlotIndex = slotByInstance[row.Id]
                        });
                    }
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { Pieces = pieces });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player worn snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandlePlayerRecords(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var row = await db.PlayerRecords.AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => new
                    {
                        p.BestHit,
                        p.BossBestKillTenthsR1,
                        p.BossBestKillTenthsR2,
                        p.BossBestKillTenthsR3,
                        p.BossBestKillTenthsR4,
                        p.BossBestKillTenthsR5,
                        p.BestDropTier,
                        p.BestDropBaseId,
                        p.BestDropAtUtc,
                        p.DelveDeepestFloor,
                    })
                    .FirstOrDefaultAsync();

                if (row == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new
                {
                    row.BestHit,
                    BossBestKillTenths = new[] { row.BossBestKillTenthsR1, row.BossBestKillTenthsR2, row.BossBestKillTenthsR3, row.BossBestKillTenthsR4, row.BossBestKillTenthsR5 },
                    row.BestDropTier,
                    row.BestDropBaseId,
                    row.BestDropAtUtc,
                    row.DelveDeepestFloor,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player records error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandlePlayerMaterialsSnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var commodities = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .Select(c => new { c.ItemId, c.Quantity })
                    .ToListAsync();

                var stash = await db.VillageStashInstances
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId)
                    .Select(v => new { v.ItemId, v.Quantity })
                    .ToListAsync();

                var stacksByItemId = new System.Collections.Generic.Dictionary<string, InventoryStackResponse>(
                    commodities.Count + stash.Count);

                for (int i = 0; i < commodities.Count; i++)
                {
                    var row = commodities[i];
                    if (!stacksByItemId.TryGetValue(row.ItemId, out var stack))
                    {
                        stack = new InventoryStackResponse { ItemId = row.ItemId };
                        stacksByItemId[row.ItemId] = stack;
                    }
                    stack.Quantity += row.Quantity;
                }

                for (int i = 0; i < stash.Count; i++)
                {
                    var row = stash[i];
                    if (!stacksByItemId.TryGetValue(row.ItemId, out var stack))
                    {
                        stack = new InventoryStackResponse { ItemId = row.ItemId };
                        stacksByItemId[row.ItemId] = stack;
                    }
                    stack.Quantity += row.Quantity;
                }

                var response = new PlayerMaterialsSnapshotResponse();
                response.Stacks.AddRange(stacksByItemId.Values);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player materials snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: Crafting Tree screen. Joins ContentRegistry's static recipe
        // table against this player's live unified material balance in one
        // response, so the client never has to guess at either half.
        private async Task HandleCraftingRecipeSnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var player = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(pr => pr.Id == playerId);

                var commodities = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .ToListAsync();

                var stash = await db.VillageStashInstances
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId)
                    .ToListAsync();

                await transaction.CommitAsync();

                var unifiedStock = new System.Collections.Generic.Dictionary<string, long>(commodities.Count + stash.Count);
                for (int i = 0; i < commodities.Count; i++)
                {
                    unifiedStock.TryGetValue(commodities[i].ItemId, out long existing);
                    unifiedStock[commodities[i].ItemId] = existing + commodities[i].Quantity;
                }
                for (int i = 0; i < stash.Count; i++)
                {
                    unifiedStock.TryGetValue(stash[i].ItemId, out long existing);
                    unifiedStock[stash[i].ItemId] = existing + stash[i].Quantity;
                }

                var response = new CraftingRecipeSnapshotResponse
                {
                    PlayerLevel = player?.CurrentLevel ?? 0
                };

                // Extracted to a synchronous helper: ContentRegistry.Recipes
                // is a ReadOnlySpan, and a ref struct local is not permitted
                // inside an async method under this language version.
                AppendCraftingRecipes(response.Recipes, unifiedStock);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Crafting recipe snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private static void AppendCraftingRecipes(
            System.Collections.Generic.List<CraftingRecipeResponse> target,
            System.Collections.Generic.Dictionary<string, long> unifiedStock)
        {
            ReadOnlySpan<ContentRegistry.RecipeDefinition> recipes = ContentRegistry.Recipes;
            for (int i = 0; i < recipes.Length; i++)
            {
                ContentRegistry.RecipeDefinition recipe = recipes[i];

                string mat1BaseId = recipe.Mat1Id > 0 ? ContentRegistry.GetItemBaseId(recipe.Mat1Id) : string.Empty;
                string mat2BaseId = recipe.Mat2Id > 0 ? ContentRegistry.GetItemBaseId(recipe.Mat2Id) : string.Empty;

                unifiedStock.TryGetValue(mat1BaseId, out long mat1Stock);
                unifiedStock.TryGetValue(mat2BaseId, out long mat2Stock);

                target.Add(new CraftingRecipeResponse
                {
                    ResultItemId = recipe.ResultItemId,
                    ResultBaseItemId = ContentRegistry.GetItemBaseId(recipe.ResultItemId),
                    ProfessionType = recipe.ProfessionType,
                    RequiredLevel = recipe.RequiredLevel,
                    CraftingTimeMs = recipe.CraftingTimeMs,
                    Mat1Id = recipe.Mat1Id,
                    Mat1BaseItemId = mat1BaseId,
                    Mat1Count = recipe.Mat1Count,
                    Mat1CurrentStock = mat1Stock,
                    Mat2Id = recipe.Mat2Id,
                    Mat2BaseItemId = mat2BaseId,
                    Mat2Count = recipe.Mat2Count,
                    Mat2CurrentStock = mat2Stock
                });
            }
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

        private async Task HandleGameDataFile(HttpListenerContext context, string fileName)
        {
            try
            {
                if (!GameDataFileNamePattern.IsMatch(fileName) || ServerOnlyGameDataFiles.Contains(fileName))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                string fullPath = System.IO.Path.Combine(GameDataDirectory, fileName);
                if (!System.IO.File.Exists(fullPath))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var info = new System.IO.FileInfo(fullPath);

                // Content changes only on deploy, but a stale content file is
                // a silent wrong-data bug rather than a visible failure, so
                // this revalidates every time and answers 304 when nothing
                // moved rather than letting a browser cache it blind.
                string etag = $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
                if (string.Equals(context.Request.Headers["If-None-Match"], etag, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 304;
                    context.Response.Headers["ETag"] = etag;
                    context.Response.Close();
                    return;
                }

                byte[] payload = await System.IO.File.ReadAllBytesAsync(fullPath);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.Headers["ETag"] = etag;
                context.Response.Headers["Cache-Control"] = "no-cache";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GameData file error for '{fileName}': {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: so a web client discovers which content files exist instead
        // of hardcoding the list - the Unity client hardcodes it, and that
        // hardcoded list is exactly the kind of second copy the port plan
        // says must not be created.
        private static readonly System.Text.RegularExpressions.Regex AudioFileNamePattern =
            new(@"^[A-Za-z0-9_\-]+\.wav$", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string AudioDirectory => System.IO.Path.Combine(AppContext.BaseDirectory, "Audio");

        // Same shape as HandleGameDataFile: the name is validated rather than
        // sanitized, so a request that is not exactly "word.wav" is rejected
        // outright and path traversal never needs reasoning about.
        private async Task HandleAudioFile(HttpListenerContext context, string fileName)
        {
            try
            {
                if (!AudioFileNamePattern.IsMatch(fileName))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                string fullPath = System.IO.Path.Combine(AudioDirectory, fileName);
                if (!System.IO.File.Exists(fullPath))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var info = new System.IO.FileInfo(fullPath);
                string etag = $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
                if (string.Equals(context.Request.Headers["If-None-Match"], etag, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 304;
                    context.Response.Headers["ETag"] = etag;
                    context.Response.Close();
                    return;
                }

                byte[] payload = await System.IO.File.ReadAllBytesAsync(fullPath);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "audio/wav";
                context.Response.Headers["ETag"] = etag;
                // Sound effects change only on a deploy and are fetched once
                // per session, so a long cache is safe and saves ten requests.
                context.Response.Headers["Cache-Control"] = "public, max-age=86400";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Audio file error for '{fileName}': {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: BACKGROUND MUSIC (owner, 2026-09-28) - tracks of about 4 MB,
        // so unlike the clips they are served with Range support: an <audio>
        // element streams a track and seeks with byte ranges, and a server
        // that ignores Range makes Chromium refuse to seek or loop it cleanly.
        // Names are validated, never sanitized, exactly as the clips are.
        private static readonly System.Text.RegularExpressions.Regex MusicFileNamePattern =
            new(@"^[A-Za-z0-9_\-]+\.mp3$", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string MusicDirectory => System.IO.Path.Combine(AudioDirectory, "Music");

        private async Task HandleMusicManifest(HttpListenerContext context)
        {
            try
            {
                var files = System.IO.Directory.Exists(MusicDirectory)
                    ? System.IO.Directory.GetFiles(MusicDirectory, "*.mp3")
                        .Select(f => System.IO.Path.GetFileName(f))
                        .Where(n => MusicFileNamePattern.IsMatch(n))
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList()
                    : new System.Collections.Generic.List<string>();

                var tracks = files.Select(f => new
                {
                    File = f,
                    // "Where_the_Path_Begins.mp3" -> "Where the Path Begins".
                    Title = System.IO.Path.GetFileNameWithoutExtension(f).Replace('_', ' '),
                });

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.Headers["Cache-Control"] = "public, max-age=3600";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { Tracks = tracks });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Music manifest error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleMusicFile(HttpListenerContext context, string fileName)
        {
            try
            {
                if (!MusicFileNamePattern.IsMatch(fileName))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                string fullPath = System.IO.Path.Combine(MusicDirectory, fileName);
                if (!System.IO.File.Exists(fullPath))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                long length = new System.IO.FileInfo(fullPath).Length;
                long start = 0, end = length - 1;
                bool partial = false;

                string? range = context.Request.Headers["Range"];
                if (range != null && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    string[] bounds = range.Substring(6).Split('-', 2);
                    if (bounds.Length == 2 && long.TryParse(bounds[0], out long rangeStart) && rangeStart >= 0 && rangeStart < length)
                    {
                        start = rangeStart;
                        end = long.TryParse(bounds[1], out long rangeEnd) && rangeEnd >= start && rangeEnd < length ? rangeEnd : length - 1;
                        partial = true;
                    }
                    else
                    {
                        context.Response.StatusCode = 416;
                        context.Response.Headers["Content-Range"] = $"bytes */{length}";
                        context.Response.Close();
                        return;
                    }
                }

                long count = end - start + 1;
                context.Response.StatusCode = partial ? 206 : 200;
                context.Response.ContentType = "audio/mpeg";
                context.Response.Headers["Accept-Ranges"] = "bytes";
                context.Response.Headers["Cache-Control"] = "public, max-age=86400";
                if (partial) context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
                context.Response.ContentLength64 = count;

                await using var stream = new System.IO.FileStream(fullPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
                stream.Seek(start, System.IO.SeekOrigin.Begin);
                byte[] buffer = new byte[64 * 1024];
                long remaining = count;
                while (remaining > 0)
                {
                    int read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) break;
                    await context.Response.OutputStream.WriteAsync(buffer, 0, read);
                    remaining -= read;
                }
            }
            catch (Exception ex)
            {
                // A listener that closed the tab mid-track lands here too - one line, not an alarm.
                Console.WriteLine($"Music file '{fileName}': {ex.Message}");
            }

            try { context.Response.Close(); } catch { }
        }

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

        private async Task HandleSpriteFile(HttpListenerContext context, string relativePath)
        {
            try
            {
                string decoded = Uri.UnescapeDataString(relativePath);

                // WebP, not PNG - this art is hand-painted with gradients,
                // which is exactly what PNG compresses worst. See
                // tools/clean_sprites.py.
                if (!decoded.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                string[] segments = decoded.Split('/');
                foreach (string segment in segments)
                {
                    // An empty segment ("a//b"), a dot segment, or anything
                    // outside the permitted set is refused rather than cleaned.
                    if (segment.Length == 0 || segment == "." || segment == ".." || !SpriteSegmentPattern.IsMatch(segment))
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                        return;
                    }
                }

                string fullPath = System.IO.Path.Combine(SpritesDirectory, System.IO.Path.Combine(segments));

                // Belt and braces: even with the segment check above, the
                // resolved path is confirmed to still be inside the sprite
                // root before anything is read.
                string resolved = System.IO.Path.GetFullPath(fullPath);
                string root = System.IO.Path.GetFullPath(SpritesDirectory);
                if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(resolved))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var info = new System.IO.FileInfo(resolved);
                string etag = $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
                if (string.Equals(context.Request.Headers["If-None-Match"], etag, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 304;
                    context.Response.Headers["ETag"] = etag;
                    context.Response.Close();
                    return;
                }

                byte[] payload = await System.IO.File.ReadAllBytesAsync(resolved);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "image/webp";
                context.Response.Headers["ETag"] = etag;
                // Art changes only on a deploy, and a screen can ask for fifty
                // of these at once, so a long cache matters more here than it
                // does for the ten sound effects.
                context.Response.Headers["Cache-Control"] = "public, max-age=604800";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Sprite file error for '{relativePath}': {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Lists every sprite as a forward-slashed relative path, so the web
        // client's generator can build its lookup tables from what actually
        // shipped rather than from a checked-in list that drifts.
        private async Task HandleSpriteManifest(HttpListenerContext context)
        {
            try
            {
                var files = new System.Collections.Generic.List<string>();
                if (System.IO.Directory.Exists(SpritesDirectory))
                {
                    string root = System.IO.Path.GetFullPath(SpritesDirectory);
                    foreach (string path in System.IO.Directory.EnumerateFiles(root, "*.webp", System.IO.SearchOption.AllDirectories))
                    {
                        string relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
                        if (relative.Split('/').All(s => SpriteSegmentPattern.IsMatch(s))) files.Add(relative);
                    }
                }

                files.Sort(StringComparer.Ordinal);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GameDataManifestResponse { Files = files });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Sprite manifest error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleAudioManifest(HttpListenerContext context)
        {
            try
            {
                var files = new System.Collections.Generic.List<string>();
                if (System.IO.Directory.Exists(AudioDirectory))
                {
                    foreach (string path in System.IO.Directory.EnumerateFiles(AudioDirectory, "*.wav"))
                    {
                        string name = System.IO.Path.GetFileName(path);
                        if (AudioFileNamePattern.IsMatch(name)) files.Add(name);
                    }
                }

                files.Sort(StringComparer.Ordinal);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GameDataManifestResponse { Files = files });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Audio manifest error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleGameDataManifest(HttpListenerContext context)
        {
            try
            {
                var files = new System.Collections.Generic.List<string>();
                if (System.IO.Directory.Exists(GameDataDirectory))
                {
                    foreach (string path in System.IO.Directory.EnumerateFiles(GameDataDirectory, "*.json"))
                    {
                        string name = System.IO.Path.GetFileName(path);
                        if (GameDataFileNamePattern.IsMatch(name) && !ServerOnlyGameDataFiles.Contains(name))
                        {
                            files.Add(name);
                        }
                    }
                }

                files.Sort(StringComparer.Ordinal);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GameDataManifestResponse { Files = files });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GameData manifest error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private sealed class GameDataManifestResponse
        {
            public System.Collections.Generic.List<string> Files { get; set; } = new();
        }

        private Task HandleMonsterLoot(HttpListenerContext context)
        {
            try
            {
                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                if (!int.TryParse(query["monsterId"], out int monsterId) || monsterId <= 0)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return Task.CompletedTask;
                }

                var rows = new System.Collections.Generic.List<MonsterLootEntryResponse>();

                ReadOnlySpan<MonsterDefinition> monsters = ContentRegistry.Monsters;
                if (monsterId > monsters.Length)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return Task.CompletedTask;
                }

                int lootTableId = monsters[monsterId - 1].LootTableId;
                ReadOnlySpan<LootTableEntry> table = ContentRegistry.GetLootTable(lootTableId);

                // Total weight first: an entry's odds are its share of the
                // table, and quoting raw weights would be unreadable.
                long totalWeight = 0;
                for (int i = 0; i < table.Length; i++)
                {
                    if (table[i].Weight > 0) totalWeight += table[i].Weight;
                }

                for (int i = 0; i < table.Length; i++)
                {
                    if (table[i].Weight <= 0 || totalWeight <= 0) continue;

                    double share = table[i].Weight / (double)totalWeight;

                    rows.Add(new MonsterLootEntryResponse
                    {
                        ItemId = table[i].ItemId,
                        BaseItemId = ContentRegistry.GetItemBaseId(table[i].ItemId),
                        ChancePct = CombatLootEngine.MaterialDropChance * share * 100.0,
                        // Legacy entries carry 0 here, which would render as
                        // "0-1" and read as "might drop nothing" when the entry
                        // already succeeded its roll. One is the real floor.
                        MinQuantity = table[i].MinQuantity > 0 ? table[i].MinQuantity : 1,

                        // MaxQuantity <= 0 is the documented legacy "one unit
                        // per successful roll" shape - see LootTableEntry.
                        MaxQuantity = table[i].MaxQuantity > 0 ? table[i].MaxQuantity : 1,
                        IsEquipment = false
                    });
                }

                // Equipment does not come from the weighted table at all - it
                // rolls on its own chance against this monster's drop table, so
                // it has to be reported separately or the screen would claim a
                // monster drops no gear.
                //
                // Modul: THE REAL ITEMS, not four "any_melee_weapon" placeholder
                // rows. Those were honest when every monster in a region shared
                // one pool and the answer genuinely was "some weapon" - now each
                // monster has its own list, and the list is the whole point of
                // the change, so the screen names what falls. It is also the only
                // way a player can tell that the thing they are missing comes
                // from the boar and not the mouse.
                ReadOnlySpan<int> equipment = EquipmentDropTable.GetDrops(monsterId);
                if (equipment.Length > 0)
                {
                    // Uniform within the table, so each entry's odds are the
                    // equipment roll divided by the table size. A boss rolls a
                    // second, guaranteed time, so its per-item chance is that
                    // much higher - quote what actually happens rather than the
                    // ordinary-monster number.
                    double rolls = CombatLootEngine.EquipmentDropChance;
                    if (ContentRegistry.IsRegionalBoss(monsterId)) rolls += 1.0;

                    double perItem = rolls / equipment.Length * 100.0;

                    for (int i = 0; i < equipment.Length; i++)
                    {
                        rows.Add(new MonsterLootEntryResponse
                        {
                            ItemId = equipment[i],
                            BaseItemId = ContentRegistry.GetItemBaseId(equipment[i]),
                            ChancePct = perItem,
                            MinQuantity = 1,
                            MaxQuantity = 1,
                            IsEquipment = true
                        });
                    }
                }

                context.Response.ContentType = "application/json";
                return JsonSerializer.SerializeAsync(context.Response.OutputStream, rows)
                    .ContinueWith(_ => context.Response.Close());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Monster loot lookup error: {ex}");
                context.Response.StatusCode = 500;
                context.Response.Close();
                return Task.CompletedTask;
            }
        }

        private async Task HandleGuildList(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);

                int skip = 0;
                int take = 25;
                if (int.TryParse(query["skip"], out int parsedSkip)) skip = parsedSkip;
                if (int.TryParse(query["take"], out int parsedTake)) take = parsedTake;

                // Clamped rather than rejected: an out-of-range page is a
                // client bug, not an attack, and returning a usable first page
                // beats a 400 the UI has to special-case.
                if (skip < 0) skip = 0;
                if (take < 1) take = 1;
                if (take > GuildListMaxTake) take = GuildListMaxTake;

                string nameFilter = (query["name"] ?? string.Empty).Trim();

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var guildQuery = db.GuildRecords.AsNoTracking();

                if (nameFilter.Length > 0)
                {
                    // Case-insensitive contains, so a half-remembered name still
                    // finds the guild - the entire point of this endpoint.
                    string lowered = nameFilter.ToLowerInvariant();
                    guildQuery = guildQuery.Where(g => g.Name.ToLower().Contains(lowered));
                }

                var rows = await guildQuery
                    .OrderByDescending(g => g.ActiveMembers)
                    .ThenBy(g => g.Id)
                    .Skip(skip)
                    .Take(take)
                    .Select(g => new GuildDirectoryEntryResponse
                    {
                        GuildId = g.Id,
                        Name = g.Name,
                        CurrentTier = g.CurrentTier,
                        ActiveMembers = g.ActiveMembers,
                        MaxMembers = g.MaxMembers,
                        GuildMMR = g.GuildMMR,
                        TaxRatePct = g.TaxRatePct,
                        JoinType = g.JoinType,
                        MinApplicationLevel = g.MinApplicationLevel
                    })
                    .ToListAsync();

                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, rows);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild list lookup error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandlePlayerNames(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                string rawIds = (query["ids"] ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(rawIds))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                string[] parts = rawIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var requestedIds = new System.Collections.Generic.List<long>(parts.Length);
                for (int i = 0; i < parts.Length && requestedIds.Count < NameLookupBatchLimit; i++)
                {
                    if (long.TryParse(parts[i], out long parsed) && parsed > 0 && !requestedIds.Contains(parsed))
                    {
                        requestedIds.Add(parsed);
                    }
                }

                if (requestedIds.Count == 0)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var rows = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => requestedIds.Contains(p.Id))
                    .Select(p => new PlayerNameEntryResponse { PlayerId = p.Id, Username = p.Username ?? string.Empty })
                    .ToListAsync();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, rows);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player names lookup error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: UI audit follow-up. DailyLoginRewardEngine.TryGrantLoginRewardAsync
        // already runs on every /api/v1/auth/login and /api/v1/auth/register
        // call - this only reads back the state it already persisted
        // (PlayerRecord.LastLoginTimestamp/LoginStreakDays), plus previews
        // the current week's reward schedule via the same GetGoldReward
        // table the grant itself used, so the client can show "day 3 of 7,
        // here's what the rest of the week pays" without duplicating the
        // reward matrices.
        private async Task HandleLoginBonusState(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var player = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => new { p.LastLoginTimestamp, p.LoginStreakDays })
                    .SingleOrDefaultAsync();

                await transaction.CommitAsync();

                if (player == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long todayDateKey = nowEpoch / 86400L;
                long lastLoginDateKey = player.LastLoginTimestamp / 86400L;
                bool creditedToday = player.LastLoginTimestamp > 0 && lastLoginDateKey == todayDateKey;

                long[] schedule = new long[7];
                for (int day = 1; day <= 7; day++)
                {
                    schedule[day - 1] = DailyLoginRewardEngine.GetGoldReward(todayDateKey, day);
                }

                var response = new LoginBonusStateResponse
                {
                    CurrentStreakDay = player.LoginStreakDays,
                    CreditedToday = creditedToday,
                    WeeklyGoldSchedule = schedule,
                    Day7DiamondBonus = DailyLoginRewardEngine.PremiumDiamondsOnDay7Completion
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Login bonus state error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: UI audit follow-up. Player Statistics - see this route's
        // registration comment. Read-only aggregate over data every field
        // already persisted for another system's own purposes; nothing new
        // is tracked to build this.
        private async Task HandlePlayerStatistics(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var player = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => new
                    {
                        p.CurrentLevel,
                        p.CurrentXp,
                        p.PremiumDiamonds,
                        p.LoginStreakDays,
                        p.GuildId,
                        p.AvailableSkillPoints,
                        p.TotalItemsCrafted,
                        p.TotalDeaths,
                        p.TotalPlayTimeSeconds
                    })
                    .SingleOrDefaultAsync();

                if (player == null)
                {
                    await transaction.CommitAsync();
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                long gold = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId && c.ItemId == "gold")
                    .Select(c => c.Quantity)
                    .FirstOrDefaultAsync();

                int achievementsClaimedCount = await db.PlayerLifetimeAchievements
                    .AsNoTracking()
                    .CountAsync(a => a.PlayerId == playerId && a.IsClaimed);

                int regionsCompletedCount = await db.PlayerRegionCompletions
                    .AsNoTracking()
                    .CountAsync(r => r.PlayerId == playerId);

                int characterCount = await db.CharacterRecords
                    .AsNoTracking()
                    .CountAsync(c => c.PlayerId == playerId);

                string guildName = string.Empty;
                if (player.GuildId > 0)
                {
                    guildName = await db.GuildRecords
                        .AsNoTracking()
                        .Where(g => g.Id == player.GuildId)
                        .Select(g => g.Name)
                        .FirstOrDefaultAsync() ?? string.Empty;
                }

                // Modul: lifetime statistics. Kills come from the codex rather
                // than a dedicated counter: monster_codex_entries has recorded a
                // per-monster KillCount since the codex shipped, so summing it
                // is retroactively correct for every existing player and cannot
                // drift from a second source of truth.
                long totalKills = await db.MonsterCodexEntries
                    .AsNoTracking()
                    .Where(e => e.PlayerId == playerId)
                    .SumAsync(e => (long)e.KillCount);

                // The five canonical region bosses. Ids 91-115 are the five
                // regions of four regulars plus a boss, so every fifth id
                // starting at 95 is a boss - see ContentRegistry's monster
                // block and Test_Content_RegionBossesAreContinuousWithTheirRegionCurve.
                long bossesSlain = await db.MonsterCodexEntries
                    .AsNoTracking()
                    .Where(e => e.PlayerId == playerId && CanonicalBossMonsterIds.Contains(e.MonsterId))
                    .SumAsync(e => (long)e.KillCount);

                // Modul: there was a `Villagers` roster here - every character,
                // as "Slot N · working/idle" - feeding the Village screen's
                // "Work slots" panel, which called those people "production
                // slots". Removed with the panel (task 77); the Character screen
                // owns the roster and CharacterCount above is the number.

                await transaction.CommitAsync();

                var response = new PlayerStatisticsResponse
                {
                    Level = player.CurrentLevel,
                    Xp = player.CurrentXp,
                    Gold = gold,
                    PremiumDiamonds = player.PremiumDiamonds,
                    LoginStreakDays = player.LoginStreakDays,
                    AchievementsClaimedCount = achievementsClaimedCount,
                    RegionsCompletedCount = regionsCompletedCount,
                    CharacterCount = characterCount,
                    AvailableSkillPoints = player.AvailableSkillPoints,
                    GuildName = guildName,

                    // Modul: lifetime statistics. Playtime is reported as of the
                    // last checkpoint rather than including the live session's
                    // current stretch: the authoritative session start lives on
                    // the tick thread's payload, and reaching across to it from
                    // an HTTP handler to gain sub-checkpoint precision on a
                    // number displayed in whole hours is not worth the coupling.
                    TotalKills = totalKills,
                    BossesSlain = bossesSlain,
                    TotalItemsCrafted = player.TotalItemsCrafted,
                    TotalDeaths = player.TotalDeaths,
                    TotalPlayTimeSeconds = player.TotalPlayTimeSeconds
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player statistics error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: UI audit follow-up - see this route's registration comment
        // for why this is HTTP rather than a WS CommandType. Delegates
        // entirely to GuildManagementEngine.CreateGuildAsync, which already
        // handles the level-20 gate, blank/oversized name rejection, and the
        // Serializable-isolation name-uniqueness guard - this handler only
        // translates its long? result into an HTTP response.
        private async Task HandleGuildCreate(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                if (!payload.TryGetProperty("guildName", out var guildNameElement))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                string guildName = guildNameElement.GetString() ?? string.Empty;

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException("NetworkBroadcastSystem: PlayerSessionRegistry not registered - call RegisterPlayerSessionRegistry before Start()."));

                var outcome = await guildManagementEngine.CreateGuildAsync(playerId, guildName);
                if (outcome.GuildId <= 0)
                {
                    // Modul: the refusal now says which rule was broken. This
                    // used to be a bare 409 with no body for four different
                    // reasons, so the player was told "Could not create" and
                    // had no way to discover that guilds need level 20.
                    string reason = outcome.Refusal switch
                    {
                        GuildManagementEngine.GuildCreateRefusal.AlreadyInAGuild
                            => "You are already in a guild. Leave it before founding another.",
                        GuildManagementEngine.GuildCreateRefusal.LevelTooLow
                            => $"Guilds open at level {outcome.RequiredLevel}. You are level {outcome.CurrentLevel}.",
                        GuildManagementEngine.GuildCreateRefusal.NameTaken
                            => "That name is taken.",
                        _ => $"That name will not do - one to {GuildManagementEngine.MaxGuildNameLength} characters.",
                    };

                    context.Response.StatusCode = 409;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GuildCreateRefusalResponse { Reason = reason });
                    context.Response.Close();
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GuildCreateResponse { GuildId = outcome.GuildId });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild create error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: UI audit follow-up - see this route's registration comment
        // for the "join = self-service join-by-name" semantics. Guild name
        // -> id resolution happens inline (no separate lookup endpoint
        // exists for guilds, unlike Friends' username resolve) since there
        // is no "browse guilds" UI that would want the id on its own yet.
        private async Task HandleGuildJoin(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                if (!payload.TryGetProperty("guildName", out var guildNameElement))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                string guildName = guildNameElement.GetString() ?? string.Empty;

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                long guildId = await db.GuildRecords
                    .AsNoTracking()
                    .Where(g => g.Name == guildName)
                    .Select(g => g.Id)
                    .FirstOrDefaultAsync();

                if (guildId <= 0)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException("NetworkBroadcastSystem: PlayerSessionRegistry not registered - call RegisterPlayerSessionRegistry before Start()."));

                bool joined = await guildManagementEngine.JoinGuildAsync(playerId, guildId);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GuildJoinResponse { Joined = joined });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild join error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: task 94 - see the route's registration comment. A refusal
        // (not in a guild, or a race with a kick) is a 409 WITH a reason, not
        // a silent 200: the player pressed a button and must be told why
        // nothing happened.
        private async Task HandleGuildLeave(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException("NetworkBroadcastSystem: PlayerSessionRegistry not registered - call RegisterPlayerSessionRegistry before Start()."));

                var outcome = await guildManagementEngine.LeaveAsync(playerId);

                context.Response.StatusCode = outcome.Left ? 200 : 409;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GuildLeaveResponse
                {
                    Left = outcome.Left,
                    ClosedGuild = outcome.ClosedGuild,
                    SuccessorPlayerId = outcome.SuccessorPlayerId,
                    Reason = outcome.Left ? string.Empty : "You are not in a guild.",
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild leave error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleGuildLeavePreview(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException("NetworkBroadcastSystem: PlayerSessionRegistry not registered - call RegisterPlayerSessionRegistry before Start()."));

                var preview = await guildManagementEngine.PreviewLeaveAsync(playerId);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GuildLeavePreviewResponse
                {
                    InGuild = preview.InGuild,
                    IsLeader = preview.IsLeader,
                    ClosesGuild = preview.ClosesGuild,
                    SuccessorPlayerId = preview.SuccessorPlayerId,
                    RemainingMembers = preview.RemainingMembers,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild leave preview error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: Play Mode audit fix. Leader-only list of this player's
        // guild's pending GuildApplications, joined against PlayerRecords
        // for a real Username (mirrors HandleFriendsList's exact reasoning
        // for not surfacing a bare numeric Id). Anyone who isn't the
        // guild's Leader (including players with no guild) gets an empty
        // list rather than a 403, matching HandleGuildRoster's "no guild"
        // convention.
        private async Task HandleGuildApplicationsPending(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var membership = await db.GuildMembers.AsNoTracking().FirstOrDefaultAsync(m => m.PlayerId == playerId);
                if (membership == null || membership.Role != GuildManagementEngine.RoleLeader)
                {
                    await transaction.CommitAsync();
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.OutputStream, new System.Collections.Generic.List<GuildApplicationEntryResponse>());
                    context.Response.Close();
                    return;
                }

                var applications = await db.GuildApplications
                    .AsNoTracking()
                    .Where(a => a.GuildId == membership.GuildId)
                    .OrderBy(a => a.CreatedAtEpoch)
                    .ToListAsync();

                var applicantIds = applications.Select(a => a.PlayerId).ToList();
                var applicants = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => applicantIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, p => p);

                await transaction.CommitAsync();

                var entries = new System.Collections.Generic.List<GuildApplicationEntryResponse>(applications.Count);
                foreach (var application in applications)
                {
                    applicants.TryGetValue(application.PlayerId, out var applicantProfile);
                    entries.Add(new GuildApplicationEntryResponse
                    {
                        Id = application.Id,
                        PlayerId = application.PlayerId,
                        Username = applicantProfile?.Username ?? "(unknown player)",
                        ApplicantLevel = application.ApplicantLevel,
                        CreatedAtEpoch = application.CreatedAtEpoch
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, entries);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild applications pending error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleGuildApplicationApprove(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                if (!payload.TryGetProperty("applicationId", out var applicationIdElement))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                long applicationId = applicationIdElement.GetInt64();

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException("NetworkBroadcastSystem: PlayerSessionRegistry not registered - call RegisterPlayerSessionRegistry before Start()."));

                bool approved = await guildManagementEngine.ApproveApplicationAsync(playerId, applicationId);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GuildApplicationActionResponse { Success = approved });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild application approve error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleGuildApplicationReject(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                if (!payload.TryGetProperty("applicationId", out var applicationIdElement))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                long applicationId = applicationIdElement.GetInt64();

                var guildManagementEngine = new GuildManagementEngine(
                    _serviceProvider.GetRequiredService<RetryingDbContextOptions>(),
                    _playerSessionRegistry ?? throw new InvalidOperationException("NetworkBroadcastSystem: PlayerSessionRegistry not registered - call RegisterPlayerSessionRegistry before Start()."));

                bool rejected = await guildManagementEngine.RejectApplicationAsync(playerId, applicationId);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new GuildApplicationActionResponse { Success = rejected });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild application reject error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul 13: authorized snapshot of the player's real lifetime achievement
        // progress (PlayerLifetimeAchievements, including but not limited to the
        // three auto-awarded tiered achievements from AchievementMilestones).
        private async Task HandleAchievementsSnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var entries = await db.PlayerLifetimeAchievements
                    .AsNoTracking()
                    .Where(a => a.PlayerId == playerId)
                    .ToListAsync();

                await transaction.CommitAsync();

                var response = new System.Collections.Generic.List<AchievementSnapshotEntryResponse>(entries.Count);

                foreach (var entry in entries)
                {
                    response.Add(new AchievementSnapshotEntryResponse
                    {
                        AchievementId = entry.AchievementId,
                        Title = AchievementMilestones.TitleFor(entry.AchievementId),
                        Description = AchievementMilestones.DescriptionFor(entry.AchievementId),
                        CurrentProgress = entry.CurrentProgress,
                        CompletedTier = entry.CompletedTier,
                        NextTierTarget = AchievementMilestones.GetNextTierTarget(entry.AchievementId, entry.CompletedTier),
                        NextTierReward = AchievementMilestones.GetNextTierReward(entry.AchievementId, entry.CompletedTier),
                        // Task 57: every achievement pays itself now. A tier the
                        // checkpoint already paid reads as claimed, so an old
                        // client does not offer a button that used to pay it twice.
                        IsClaimed = entry.IsClaimed || entry.CompletedTier > 0
                    });
                }

                // Modul: Achievement claim button. AchievementEngine.ProcessClaimsQueueAsync
                // only ever creates the monster-kill achievement's row the first time a
                // claim is attempted - so without this, the claim button would have
                // nothing to attach to until the player had already (impossibly, with no
                // button) claimed once. Synthesize the unclaimed row here so it is always
                // visible and claimable from a fresh account.
                if (!response.Exists(r => r.AchievementId == AchievementMilestones.MonsterKillAchievementId))
                {
                    response.Add(new AchievementSnapshotEntryResponse
                    {
                        AchievementId = AchievementMilestones.MonsterKillAchievementId,
                        Title = AchievementMilestones.TitleFor(AchievementMilestones.MonsterKillAchievementId),
                        Description = AchievementMilestones.DescriptionFor(AchievementMilestones.MonsterKillAchievementId),
                        CurrentProgress = 0,
                        CompletedTier = 0,
                        NextTierTarget = AchievementMilestones.GetNextTierTarget(AchievementMilestones.MonsterKillAchievementId, 0),
                        NextTierReward = AchievementMilestones.GetNextTierReward(AchievementMilestones.MonsterKillAchievementId, 0),
                        IsClaimed = false
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Achievements snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleStorefrontListings(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                string query = context.Request.Url?.Query ?? string.Empty;
                if (!ClientCommandValidator.ValidateStorefrontQuery(playerId, query))
                {
                    ForceDisconnect(playerId);
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                // Modul: Phase - Full-Stack Production Polish Phase 2, Part
                // 4.1. Resolves StorefrontSegmentationEngine.ResolveCohort's
                // three real inputs from this player's own transaction
                // history/account record, replacing the previous pure
                // playerId-hash cohort assignment. lifetimeValue is the
                // cumulative granted premium-diamond total (see
                // ProcessedTransactions - the authoritative anti-replay IAP
                // ledger); a player with no rows here has never purchased
                // anything, so lastTransactionEpoch stays null and
                // daysSinceLastTransaction resolves to int.MaxValue,
                // correctly excluding them from both the "active high-value"
                // and "recently active veteran" branches.
                long lifetimeValue = await db.ProcessedTransactions
                    .AsNoTracking()
                    .Where(t => t.PlayerId == playerId)
                    .Select(t => (long)t.PremiumDiamondsGranted)
                    .SumAsync();

                long? lastTransactionEpoch = await db.ProcessedTransactions
                    .AsNoTracking()
                    .Where(t => t.PlayerId == playerId)
                    .OrderByDescending(t => t.ProcessedAtEpoch)
                    .Select(t => (long?)t.ProcessedAtEpoch)
                    .FirstOrDefaultAsync();

                long ageInTicks = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.LogicEpochCounter)
                    .SingleOrDefaultAsync();

                long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int daysSinceLastTransaction = lastTransactionEpoch.HasValue
                    ? (int)Math.Min(int.MaxValue, (nowEpoch - lastTransactionEpoch.Value) / 86400L)
                    : int.MaxValue;

                int cohort = StorefrontSegmentationEngine.ResolveCohort(lifetimeValue, ageInTicks, daysSinceLastTransaction);

                await using (var profileTransaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "INSERT INTO \"PlayerSegmentationProfiles\" (\"PlayerId\", \"CohortTag\", \"LifetimeValueCents\", \"ChurnRiskScore\") VALUES ({0}, {1}, {2}, {3}) ON CONFLICT (\"PlayerId\") DO UPDATE SET \"CohortTag\" = EXCLUDED.\"CohortTag\", \"LifetimeValueCents\" = EXCLUDED.\"LifetimeValueCents\", \"ChurnRiskScore\" = EXCLUDED.\"ChurnRiskScore\";",
                        playerId,
                        cohort,
                        (int)Math.Min(int.MaxValue, lifetimeValue),
                        Math.Min(1.0, daysSinceLastTransaction / 90.0));
                    await profileTransaction.CommitAsync();
                }

                await using var listingsTransaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                var products = await db.SegmentedStorefrontListings
                    .AsNoTracking()
                    .Where(l => l.TargetCohort == cohort)
                    .OrderBy(l => l.ListingId)
                    .Select(l => new StorefrontListingResponse
                    {
                        ListingId = l.ListingId,
                        ProductIdentifier = l.ProductIdentifier,
                        DiamondPackageYield = l.DiamondPackageYield,
                        PriceInCents = l.PriceInCents
                    })
                    .ToListAsync();
                await listingsTransaction.CommitAsync();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, products);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Storefront listings error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
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

        private async Task<bool> IsNonceCurrentAsync(Guid accountId, string presentedNonce)
        {
            if (!_accountCurrentNonce.TryGetValue(accountId, out string? currentNonce))
            {
                var authOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
                currentNonce = await AuthenticationEngine.GetCurrentSessionNonceAsync(authOptions, accountId);
                _accountCurrentNonce[accountId] = currentNonce;
            }

            // Modul: null is the bootstrap state (see PlayerRecord.
            // CurrentSessionNonce's own doc comment) - no revocation event
            // has ever happened for this account, so every token validates
            // exactly as it did before this column existed.
            return currentNonce == null || currentNonce == presentedNonce;
        }

        /// <summary>
        /// Writes a just-bumped nonce into the cache and immediately
        /// disconnects this account's live WebSocket, if it has one - so a
        /// stolen access token stops working on its very next use rather
        /// than only once its normal 24-hour clock runs out.
        /// </summary>
        private async Task EvictAccountSessionAsync(Guid accountId, string newNonce)
        {
            _accountCurrentNonce[accountId] = newNonce;
            long playerId = await ResolvePlayerIdFromAccountIdAsync(accountId);
            if (playerId > 0L)
            {
                ForceDisconnect(playerId);
            }
        }

        // Modul: final-review Finding 3 - this used to duplicate
        // TryResolveAuthenticatedPlayerWithMethodAsync almost line-for-line
        // (bearer-prefix parsing, ValidateJwt, the nonce check, player-id
        // resolution), differing only in what it returned. Delegating keeps
        // there being exactly one place that decides what makes a bearer
        // token authenticated; every existing caller here only ever wanted
        // the PlayerId half of that tuple.
        private async Task<long> TryResolveAuthenticatedPlayerAsync(HttpListenerRequest request)
            => (await TryResolveAuthenticatedPlayerWithMethodAsync(request)).PlayerId;

        private async Task<long> ResolvePlayerIdFromAccountIdAsync(Guid accountId)
        {
            if (_accountIdToPlayerIdCache.TryGetValue(accountId, out long cachedPlayerId))
            {
                return cachedPlayerId;
            }

            await using var db = await _contextFactory.CreateDbContextAsync();
            var player = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.PlayerGuid == accountId);
            if (player == null)
            {
                return 0L;
            }

            _accountIdToPlayerIdCache[accountId] = player.Id;
            return player.Id;
        }

        // Modul: sibling of TryResolveAuthenticatedPlayerAsync that also
        // hands back the token's AuthMethod ("pw"/"dev") - added for the
        // step-up gate (RequiresPasswordStepUpAsync below) rather than
        // changing the original, which has seven-plus callers that only
        // ever needed the playerId.
        private async Task<(long PlayerId, string AuthMethod)> TryResolveAuthenticatedPlayerWithMethodAsync(HttpListenerRequest request)
        {
            const string bearerPrefix = "Bearer ";
            string bearerHeader = request.Headers["Authorization"] ?? string.Empty;
            if (bearerHeader.Length <= bearerPrefix.Length || !bearerHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return (0L, string.Empty);
            }

            string token = bearerHeader.Substring(bearerPrefix.Length);
            JwtValidationResult result = AuthenticationEngine.ValidateJwt(token, _jwtSecretKey);
            if (!result.IsValid || !await IsNonceCurrentAsync(result.AccountId, result.SessionNonce))
            {
                return (0L, string.Empty);
            }

            long playerId = await ResolvePlayerIdFromAccountIdAsync(result.AccountId);
            return (playerId, result.AuthMethod);
        }

        /// <summary>
        /// True only when this session was established by a silent
        /// device-bearer login AND the account actually has a password to
        /// step up TO - the one case that proves nothing about who is
        /// holding the device. False for a real password/OAuth login (or a
        /// JWT that predates the "m" claim, defaulted safe), which never
        /// needs a step-up. A pure guest (PasswordHash still null) has
        /// nothing to step up to, so a stolen DeviceId on a guest account
        /// gains nothing extra from this gate - it is exactly as exposed as
        /// it always was.
        /// </summary>
        private async Task<bool> RequiresPasswordStepUpAsync(long playerId, string authMethod)
        {
            if (authMethod != "dev") return false;

            await using var db = await _contextFactory.CreateDbContextAsync();
            var player = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId);
            return player?.PasswordHash != null;
        }

        private static async Task<bool> VerifyStepUpPasswordAsync(FolkIdleDbContext db, long playerId, string suppliedPassword)
        {
            var player = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId);
            return player != null && PasswordHasher.Verify(suppliedPassword, player.PasswordHash);
        }

        private static void WriteStepUpRequired(HttpListenerContext context)
        {
            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes("{\"StepUpRequired\":true}");
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        // Modul: HandleVerifyReceipt REMOVED 2026-09-18 - it was the REST
        // wrapper around VerifyPurchaseAsync (an unauthenticated
        // AccountId/TransactionId/ProductId out of the request body, no
        // signature check) exposed at what the client believed was the
        // hardened URL. See the routing comment above where
        // /api/v1/billing/verify-receipt used to be registered.

        // Modul: the real, hardened IAP verification endpoint. Identifies
        // the player from the caller's own session Bearer JWT (see
        // TryResolveAuthenticatedPlayerAsync) rather than trusting a
        // client-supplied AccountId, and passes the raw base64 receipt
        // straight through to BillingVerificationEngine.VerifyReceiptAsync,
        // which is the only place TransactionId/ProductId/reward amount are
        // ever derived from - none of them come from this request body
        // directly.
        private async Task HandleBillingVerify(HttpListenerContext context)
        {
            try
            {
                var (playerId, authMethod) = await TryResolveAuthenticatedPlayerWithMethodAsync(context.Request);
                if (playerId == 0L)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                if (_billingVerificationEngine == null)
                {
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    return;
                }

                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                // Modul: step-up gate, Task 10. A device-bearer session on an
                // account that already has a password proves only "held the
                // DeviceId", not "is the account owner" - a real-money
                // purchase requires re-proving the password in this same
                // request body before VerifyReceiptAsync runs.
                if (await RequiresPasswordStepUpAsync(playerId, authMethod))
                {
                    string suppliedPassword = payload.TryGetProperty("password", out var pwElement) ? (pwElement.GetString() ?? string.Empty) : string.Empty;
                    await using var stepUpDb = await _contextFactory.CreateDbContextAsync();
                    if (suppliedPassword.Length == 0 || !await VerifyStepUpPasswordAsync(stepUpDb, playerId, suppliedPassword))
                    {
                        // Modul: this endpoint is now in the AuthThrottle
                        // budget (see the allowlist above) precisely because
                        // it verifies a password - the request-count throttle
                        // bounds the guess rate, and this line leaves a trace
                        // in the server's own log of which player a
                        // brute-force attempt targeted, which the throttle
                        // alone would not record anywhere.
                        Console.WriteLine($"Step-up rejected: player {playerId} presented a device-bearer session with a missing or incorrect password on billing/verify.");
                        WriteStepUpRequired(context);
                        context.Response.Close();
                        return;
                    }
                }

                if (!payload.TryGetProperty("receipt", out var receiptElement))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                string base64Receipt = receiptElement.GetString() ?? string.Empty;
                bool success = await _billingVerificationEngine.VerifyReceiptAsync(playerId, base64Receipt);
                context.Response.StatusCode = success ? 200 : 409;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Billing verify error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }

        private async Task HandleRefundWebhook(HttpListenerContext context)
        {
            try
            {
                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                var accountId = payload.GetProperty("AccountId").GetGuid();
                var refundedDiamonds = payload.GetProperty("RefundedDiamonds").GetInt32();

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                var player = await db.PlayerRecords.FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"PlayerGuid\" = {0} FOR UPDATE", accountId).FirstOrDefaultAsync();
                if (player != null)
                {
                    player.PremiumDiamonds -= refundedDiamonds;
                    if (player.PremiumDiamonds < 0)
                    {
                        player.Quarantine_Active = true;
                        player.IsQuarantined = true;
                        
                        _playerSessionRegistry?.QuarantineNotificationQueue.Enqueue(new QuarantineNotification { PlayerId = player.Id });
                    }
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                context.Response.StatusCode = 200;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Refund webhook error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }

        private async Task HandleSupportTicket(HttpListenerContext context)
        {
            try
            {
                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                var traceLog = payload.GetProperty("TraceLog").GetString();
                
                // Server-side scrubbing logic is not requested here, the client runs the regex on its side.
                // Or maybe we should scrub here too? The task says "collection boundary", meaning before sending, 
                // but we also have to execute sanitization exclusively upon explicit ticket dispatch. So it runs on the client.

                // Modul: THE MESSAGE USED TO BE THROWN AWAY. This line printed
                // "Received Support Ticket" and nothing else, so whatever the
                // player wrote reached no one - the Settings screen was honest
                // that nobody would reply, but not that nobody could read it.
                // It is logged now, on one grep-able line (`[support]`), with
                // the account when the request carries a token. Newlines are
                // escaped so one ticket stays one log line; the body cap is
                // ReadBodyAsync's, the print cap is here.
                long supportPlayerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                string text = string.Join(" | ", (traceLog ?? string.Empty).Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries));
                if (text.Length > 4000) text = text.Substring(0, 4000) + " [truncated]";
                Console.WriteLine($"[support] player={supportPlayerId} {text}");
                
                context.Response.StatusCode = 200;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Support ticket error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }

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

        // Modul: hand-rolled Prometheus text-exposition-format metrics
        // endpoint (no prometheus-net or other external dependency, per this
        // task's explicit constraint). Unauthenticated, matching the
        // existing /health/* endpoints - Prometheus scraping is expected to
        // happen from inside the cluster network, not across the public
        // internet. TickDurationBucketCount*/TickDurationSumMs read directly
        // off SimulationEngine.GetMetrics() (a ref struct accessor, no
        // allocation) if a SimulationEngine has been registered; the write
        // queue length comes from SCARD against RedisSessionCache.
        // DirtyPlayersSetKey (see RedisWriteBehindEngine.FlushNowAsync,
        // which drains that same Redis set), defaulting to 0 if Redis is
        // unavailable rather than failing the whole scrape.
        private async Task HandleMetrics(HttpListenerContext context)
        {
            try
            {
                int activeSessions = _connectedClients.Count;

                long tickCount = 0;
                long tickSumMs = 0;
                long bucket10 = 0, bucket25 = 0, bucket50 = 0, bucket100 = 0, bucket250 = 0, bucketInf = 0;
                long ticksDropped = 0, catchUpTicks = 0;
                if (_simulationEngine != null)
                {
                    EngineMetricsPayload metrics = _simulationEngine.GetMetrics();
                    tickCount = metrics.TotalTicksProcessed;
                    ticksDropped = metrics.TicksDropped;
                    catchUpTicks = metrics.CatchUpTicks;
                    tickSumMs = metrics.TickDurationSumMs;
                    bucket10 = metrics.TickDurationBucketCount10Ms;
                    bucket25 = metrics.TickDurationBucketCount25Ms;
                    bucket50 = metrics.TickDurationBucketCount50Ms;
                    bucket100 = metrics.TickDurationBucketCount100Ms;
                    bucket250 = metrics.TickDurationBucketCount250Ms;
                    bucketInf = metrics.TickDurationBucketCountInf;
                }

                // Task 43: a rolling-window p99 (see GetRecentTickPercentiles)
                // and the checkpoint writer's back-pressure.
                var recentTicks = _simulationEngine?.GetRecentTickPercentiles();
                var checkpointWriter = _simulationEngine?.CheckpointManager.Writer;

                long writeQueueLength = 0;
                var redis = _serviceProvider.GetService<StackExchange.Redis.IConnectionMultiplexer>();
                if (redis != null && redis.IsConnected)
                {
                    try
                    {
                        writeQueueLength = await redis.GetDatabase().SetLengthAsync(RedisSessionCache.DirtyPlayersSetKey);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Metrics: failed to read write-behind queue length: {ex.Message}");
                    }
                }

                var body = new System.Text.StringBuilder();
                body.Append("# HELP folkidle_active_sessions_total Current number of connected WebSocket sessions.\n");
                body.Append("# TYPE folkidle_active_sessions_total gauge\n");
                body.Append("folkidle_active_sessions_total ").Append(activeSessions).Append('\n');
                body.Append('\n');
                body.Append("# HELP folkidle_tick_duration_milliseconds Duration of the 10Hz simulation tick loop.\n");
                body.Append("# TYPE folkidle_tick_duration_milliseconds histogram\n");
                body.Append("folkidle_tick_duration_milliseconds_bucket{le=\"10\"} ").Append(bucket10).Append('\n');
                body.Append("folkidle_tick_duration_milliseconds_bucket{le=\"25\"} ").Append(bucket25).Append('\n');
                body.Append("folkidle_tick_duration_milliseconds_bucket{le=\"50\"} ").Append(bucket50).Append('\n');
                body.Append("folkidle_tick_duration_milliseconds_bucket{le=\"100\"} ").Append(bucket100).Append('\n');
                body.Append("folkidle_tick_duration_milliseconds_bucket{le=\"250\"} ").Append(bucket250).Append('\n');
                body.Append("folkidle_tick_duration_milliseconds_bucket{le=\"+Inf\"} ").Append(bucketInf).Append('\n');
                body.Append("folkidle_tick_duration_milliseconds_sum ").Append(tickSumMs).Append('\n');
                body.Append("folkidle_tick_duration_milliseconds_count ").Append(tickCount).Append('\n');
                body.Append('\n');
                body.Append("# HELP folkidle_ticks_catch_up_total Ticks started immediately because the fixed schedule was behind.\n");
                body.Append("# TYPE folkidle_ticks_catch_up_total counter\n");
                body.Append("folkidle_ticks_catch_up_total ").Append(catchUpTicks).Append('\n');
                body.Append("# HELP folkidle_ticks_dropped_total Ticks owed beyond the catch-up cap and never run.\n");
                body.Append("# TYPE folkidle_ticks_dropped_total counter\n");
                body.Append("folkidle_ticks_dropped_total ").Append(ticksDropped).Append('\n');
                body.Append('\n');
                if (recentTicks is { } rt)
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    body.Append("# HELP folkidle_tick_duration_recent_milliseconds Tick duration over the last ").Append(SimulationEngine.RecentTickWindow).Append(" ticks.\n");
                    body.Append("# TYPE folkidle_tick_duration_recent_milliseconds summary\n");
                    body.Append("folkidle_tick_duration_recent_milliseconds{quantile=\"0.5\"} ").Append(rt.P50Ms.ToString("0.###", inv)).Append('\n');
                    body.Append("folkidle_tick_duration_recent_milliseconds{quantile=\"0.95\"} ").Append(rt.P95Ms.ToString("0.###", inv)).Append('\n');
                    body.Append("folkidle_tick_duration_recent_milliseconds{quantile=\"0.99\"} ").Append(rt.P99Ms.ToString("0.###", inv)).Append('\n');
                    body.Append("folkidle_tick_duration_recent_milliseconds{quantile=\"1\"} ").Append(rt.MaxMs.ToString("0.###", inv)).Append('\n');
                    body.Append("folkidle_tick_duration_recent_milliseconds_count ").Append(rt.Samples).Append('\n');
                    body.Append('\n');
                }
                if (checkpointWriter != null)
                {
                    body.Append("# HELP folkidle_checkpoint_queue_depth Checkpoint jobs queued on CheckpointWriter.\n");
                    body.Append("# TYPE folkidle_checkpoint_queue_depth gauge\n");
                    body.Append("folkidle_checkpoint_queue_depth ").Append(checkpointWriter.QueueDepth).Append('\n');
                    body.Append("# TYPE folkidle_checkpoint_flushes_committed_total counter\n");
                    body.Append("folkidle_checkpoint_flushes_committed_total ").Append(checkpointWriter.FlushesCommitted).Append('\n');
                    body.Append("# TYPE folkidle_checkpoint_flushes_failed_total counter\n");
                    body.Append("folkidle_checkpoint_flushes_failed_total ").Append(checkpointWriter.FlushesFailed).Append('\n');
                    body.Append("# HELP folkidle_checkpoint_dead_letters_total CHECKPOINT-DEADLETTER lines written (grep the server log).\n");
                    body.Append("# TYPE folkidle_checkpoint_dead_letters_total counter\n");
                    body.Append("folkidle_checkpoint_dead_letters_total ").Append(checkpointWriter.DeadLetters).Append('\n');
                    body.Append('\n');
                }
                body.Append("# HELP folkidle_database_write_queue_length Players with state pending Redis write-behind flush.\n");
                body.Append("# TYPE folkidle_database_write_queue_length gauge\n");
                body.Append("folkidle_database_write_queue_length ").Append(writeQueueLength).Append('\n');

                // Modul: task 41. The outbox's one remaining way to lose an
                // event is a full queue (512, drop-oldest), so that drop is
                // counted here - an uncounted drop would be the silent loss
                // the outbox was built to end. The depth is summed across
                // live sessions; a steadily high value means peers that are
                // not keeping up.
                long outboxDepth = 0;
                foreach (var kvp in _connectedClients)
                {
                    outboxDepth += kvp.Value.PendingEventCount;
                }
                body.Append('\n');
                body.Append("# HELP folkidle_forced_disconnects_total Sessions ended by ForceDisconnect (validator refusals, anti-cheat, rollbacks). The app log says which handler: grep [kick].\n");
                body.Append("# TYPE folkidle_forced_disconnects_total counter\n");
                body.Append("folkidle_forced_disconnects_total ").Append(ForcedDisconnectsTotal).Append('\n');
                body.Append('\n');
                body.Append("# HELP folkidle_outbox_events_dropped_total Event frames (loot, combat, chat) dropped because a session outbox was full.\n");
                body.Append("# TYPE folkidle_outbox_events_dropped_total counter\n");
                body.Append("folkidle_outbox_events_dropped_total ").Append(WebSocketSession.EventsDroppedTotal).Append('\n');
                body.Append('\n');
                body.Append("# HELP folkidle_outbox_queue_depth Event frames waiting in session outboxes, summed over connected sessions.\n");
                body.Append("# TYPE folkidle_outbox_queue_depth gauge\n");
                body.Append("folkidle_outbox_queue_depth ").Append(outboxDepth).Append('\n');

                // Modul: task 46 (audit item 8, "8a - measure"). See
                // StateFrameMetrics for what these count and why. Read
                // straight off the static counters, the same pattern as
                // folkidle_outbox_events_dropped_total above.
                body.Append('\n');
                body.Append("# HELP folkidle_state_frame_bytes_total Bytes of state frame (JSON serialize output, or the binary struct size) offered to a session, summed since start.\n");
                body.Append("# TYPE folkidle_state_frame_bytes_total counter\n");
                body.Append("folkidle_state_frame_bytes_total ").Append(StateFrameMetrics.BytesTotal).Append('\n');
                body.Append("# HELP folkidle_ws_deflate_input_bytes_total Bytes of text frames handed to a session deflater (task 46), summed since start.\n");
                body.Append("# TYPE folkidle_ws_deflate_input_bytes_total counter\n");
                body.Append("folkidle_ws_deflate_input_bytes_total ").Append(FrameDeflater.InputBytesTotal).Append('\n');
                body.Append("# HELP folkidle_ws_deflate_output_bytes_total Bytes those deflaters put on the wire, summed since start.\n");
                body.Append("# TYPE folkidle_ws_deflate_output_bytes_total counter\n");
                body.Append("folkidle_ws_deflate_output_bytes_total ").Append(FrameDeflater.OutputBytesTotal).Append('\n');
                body.Append('\n');
                body.Append("# HELP folkidle_state_frames_total State frames (JSON or binary) offered to a session, summed since start.\n");
                body.Append("# TYPE folkidle_state_frames_total counter\n");
                body.Append("folkidle_state_frames_total ").Append(StateFrameMetrics.FramesTotal).Append('\n');
                body.Append('\n');
                body.Append("# HELP folkidle_state_frame_serialize_microseconds Time PacketJsonCodec.SerializeToUtf8 took to build one JSON state frame. Binary-path frames are not timed - see StateFrameMetrics.\n");
                body.Append("# TYPE folkidle_state_frame_serialize_microseconds histogram\n");
                body.Append("folkidle_state_frame_serialize_microseconds_bucket{le=\"100\"} ").Append(StateFrameMetrics.Bucket100Us).Append('\n');
                body.Append("folkidle_state_frame_serialize_microseconds_bucket{le=\"250\"} ").Append(StateFrameMetrics.Bucket250Us).Append('\n');
                body.Append("folkidle_state_frame_serialize_microseconds_bucket{le=\"500\"} ").Append(StateFrameMetrics.Bucket500Us).Append('\n');
                body.Append("folkidle_state_frame_serialize_microseconds_bucket{le=\"1000\"} ").Append(StateFrameMetrics.Bucket1000Us).Append('\n');
                body.Append("folkidle_state_frame_serialize_microseconds_bucket{le=\"2500\"} ").Append(StateFrameMetrics.Bucket2500Us).Append('\n');
                body.Append("folkidle_state_frame_serialize_microseconds_bucket{le=\"+Inf\"} ").Append(StateFrameMetrics.BucketInfUs).Append('\n');
                body.Append("folkidle_state_frame_serialize_microseconds_sum ").Append(StateFrameMetrics.SerializeSumMicroseconds).Append('\n');
                body.Append("folkidle_state_frame_serialize_microseconds_count ").Append(StateFrameMetrics.SerializeCount).Append('\n');

                byte[] payload = System.Text.Encoding.UTF8.GetBytes(body.ToString());
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/plain; version=0.0.4";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Metrics endpoint error: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        /// <summary>
        /// Issues the refresh half of a login, and never fails the login.
        /// </summary>
        /// <remarks>
        /// Modul: A LOGIN THAT SUCCEEDED MUST NOT BE THROWN AWAY BECAUSE THE
        /// SECOND HALF DID NOT. The JWT is already minted and valid for a day
        /// by the time this runs; if the insert fails the player is signed in
        /// exactly as they were before any of this existed, and finds out
        /// tomorrow. Failing the whole login instead would turn a feature that
        /// saves a password prompt into one that prevents a sign-in.
        /// </remarks>
        private async Task<(string Token, long ExpiresAtEpoch)> TryIssueRefreshTokenAsync(
            RetryingDbContextOptions authOptions, Guid accountId, string authMethod)
        {
            try
            {
                return await AuthenticationEngine.IssueRefreshTokenAsync(authOptions, accountId, authMethod);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Refresh token issue failed for account {accountId}: {ex.Message}");
                return (string.Empty, 0L);
            }
        }

        /// <summary>
        /// Trades a refresh token for a fresh JWT, and rotates it.
        /// </summary>
        /// <remarks>
        /// Modul: THIS IS WHAT MAKES THE APP OPEN STRAIGHT INTO THE GAME.
        ///
        /// Before it, the 24-hour JWT meant a player who opened FolkIdle on
        /// Tuesday morning after playing on Monday evening met the login form -
        /// daily, in a game whose entire promise is that it runs while you are
        /// gone.
        ///
        /// EVERY FAILURE IS 401, AND SAYS NOTHING ELSE. Unknown, expired and
        /// replayed are one answer on the wire, because the difference between
        /// them is information about somebody else's session. The client needs
        /// only "this did not work, show the login form", which is precisely
        /// the case B2 also asked to land on the login screen rather than hang.
        /// The reason IS logged, because the replay case is the one anybody
        /// investigating a stolen account will come looking for.
        /// </remarks>
        private async Task HandleAuthRefresh(HttpListenerContext context)
        {
            try
            {
                var authOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();

                string body = await ReadBodyAsync(context);

                string rawToken;
                try
                {
                    using var parsed = JsonDocument.Parse(body);
                    rawToken = parsed.RootElement.TryGetProperty("refreshToken", out var t)
                        ? (t.GetString() ?? string.Empty)
                        : string.Empty;
                }
                catch (System.Text.Json.JsonException)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                var result = await AuthenticationEngine.RedeemRefreshTokenAsync(authOptions, rawToken);
                if (result.Outcome != AuthenticationEngine.RefreshOutcome.Rotated)
                {
                    if (result.Outcome == AuthenticationEngine.RefreshOutcome.Replayed)
                    {
                        // The only one worth a line in the log: a spent token
                        // came back, so every session on that account was just
                        // revoked. See RedeemRefreshTokenAsync for why.
                        Console.WriteLine("Refresh token replay detected; all sessions for that account revoked.");

                        // Modul: "all sessions" used to mean only the refresh
                        // half - RedeemRefreshTokenAsync already revoked every
                        // refresh token for this account before returning
                        // Replayed. The access token survived that, for up to
                        // 24 more hours, until this bump.
                        string newNonce = await AuthenticationEngine.BumpSessionNonceAsync(authOptions, result.AccountId);
                        await EvictAccountSessionAsync(result.AccountId, newNonce);
                    }

                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                // Modul: THE NONCE DOES NOT FEED SESSION EVICTION - that is a
                // SEPARATE mechanism (RedisPlayerSessionLock, its own
                // per-connection RedisLockToken, see SubscribeToSessionEviction
                // and the WebSocket handshake's ForceAcquireAndEvictAsync
                // call). An earlier comment here claimed otherwise; it was
                // wrong. This nonce exists so a logout/reset/replay event -
                // none of which are a new login - can invalidate an access
                // token nothing else touches. See IsNonceCurrentAsync.
                string sessionNonce = AuthenticationEngine.GenerateSessionNonce();
                await AuthenticationEngine.SetCurrentSessionNonceAsync(authOptions, result.AccountId, sessionNonce);
                _accountCurrentNonce[result.AccountId] = sessionNonce;
                string jwt = AuthenticationEngine.GenerateJwt(result.AccountId, sessionNonce, result.AuthMethod, _jwtSecretKey, out long expiresAtEpoch);

                var response = new AuthLoginResponse
                {
                    Token = jwt,
                    ExpiresAtEpoch = expiresAtEpoch,
                    RefreshToken = result.Token,
                    RefreshExpiresAtEpoch = result.ExpiresAtEpoch
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Auth refresh error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// Signing out: invalidates the refresh token on this device, AND the
        /// live access token, AND any open WebSocket for the account.
        /// </summary>
        /// <remarks>
        /// Answers 204 whatever happened, including for a token that never
        /// existed. There is nothing a sign-out button would do differently on
        /// being told the token was already gone, and distinguishing the cases
        /// would confirm which tokens are live to anybody who can reach the
        /// route.
        ///
        /// The JWT used to be untouched and stay valid for the rest of its day
        /// - a bearer token this server does not store, so nothing checked it
        /// again on the way in. That is fixed by bumping the account's session
        /// nonce (signed into every JWT at login) and evicting the cached
        /// nonce + any live WebSocket, so a request bearing the old token now
        /// fails the nonce check and a connected session is disconnected
        /// immediately, not just prevented from silently refreshing.
        /// </remarks>
        private async Task HandleAuthRevoke(HttpListenerContext context)
        {
            try
            {
                var authOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();

                string body = await ReadBodyAsync(context);

                string rawToken = string.Empty;
                try
                {
                    using var parsed = JsonDocument.Parse(body);
                    if (parsed.RootElement.TryGetProperty("refreshToken", out var t))
                    {
                        rawToken = t.GetString() ?? string.Empty;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // A malformed body is still a sign-out. Nothing to revoke.
                }

                Guid revokedAccountId = await AuthenticationEngine.RevokeRefreshTokenAsync(authOptions, rawToken);
                if (revokedAccountId != Guid.Empty)
                {
                    // Modul: the refresh token being gone was already true.
                    // What was missing is this: the ACCESS token this device
                    // is still holding stays valid for up to 24 more hours
                    // unless something invalidates it too. Bump and evict so
                    // "sign out" actually ends the session everywhere, not
                    // just the ability to silently get a new one.
                    string newNonce = await AuthenticationEngine.BumpSessionNonceAsync(authOptions, revokedAccountId);
                    await EvictAccountSessionAsync(revokedAccountId, newNonce);
                }

                context.Response.StatusCode = 204;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Auth revoke error: {ex}");

                // Still 204. A sign-out that reports a failure invites the
                // player to press it again, and the local half - which is the
                // half that stops this browser being signed in - has already
                // happened by the time this is called.
                context.Response.StatusCode = 204;
            }

            context.Response.Close();
        }

        // Modul: sole controlled entry point for account identity issuance.
        // DeviceId is a client-persisted GUID (see UiLoginWindow on the
        // client) - looked up or auto-provisioned via AuthenticationEngine.
        // LoginOrProvisionAsync, then a fresh SessionNonce is minted and
        // signed into a JWT. THE NONCE DOES NOT FEED SESSION EVICTION - that
        // is a separate mechanism (RedisPlayerSessionLock, its own
        // per-connection RedisLockToken, see SubscribeToSessionEviction and
        // the WebSocket handshake's ForceAcquireAndEvictAsync call). An
        // earlier comment here claimed the nonce was what the Redis eviction
        // check used to detect and kick a stale prior session; it was wrong.
        // This nonce exists so a logout/reset/replay event - none of which
        // are a new login - can invalidate an access token nothing else
        // touches. See IsNonceCurrentAsync.
        // Modul: accepts either deviceId (existing login-or-provision flow,
        // unchanged) or oauthProviderToken (OAuth recovery login, Part 1 of
        // this task). oauthProviderToken is a validated PROOF-OF-OWNERSHIP
        // token, never a bare provider ID - accepting a raw ID directly
        // would let any caller claim any linked account just by supplying
        // its external ID with no proof of ownership at all. Recovery only:
        // if no account is linked to the validated (ProviderType,
        // ExternalProviderId) pair, this returns 404 rather than
        // auto-provisioning a new account - linking is a separate,
        // explicit, authenticated action (see HandleOAuthLink).
        private async Task HandleAuthLogin(HttpListenerContext context)
        {
            try
            {
                if (!context.Request.HasEntityBody)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                string body = await ReadBodyAsync(context);

                string deviceId = string.Empty;
                string oauthProviderToken = string.Empty;
                string rememberedDeviceId = string.Empty;
                string email = string.Empty;
                string password = string.Empty;
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("oauthProviderToken", out var oauthElement))
                    {
                        oauthProviderToken = oauthElement.GetString() ?? string.Empty;
                    }
                    if (document.RootElement.TryGetProperty("deviceId", out var deviceIdElement))
                    {
                        deviceId = deviceIdElement.GetString() ?? string.Empty;
                    }
                    // Modul: Email/Password Auth. A separate field from
                    // deviceId (not a rename) - deviceId still means "log in
                    // or auto-provision a fresh anonymous account" (the
                    // pre-existing behavior, unchanged), while
                    // rememberedDeviceId means "silently resume ONLY if this
                    // device already completed a real Register/email login,
                    // otherwise tell the caller to show the login/register
                    // choice screen" (see UiLoginWindow).
                    if (document.RootElement.TryGetProperty("rememberedDeviceId", out var rememberedElement))
                    {
                        rememberedDeviceId = rememberedElement.GetString() ?? string.Empty;
                    }
                    if (document.RootElement.TryGetProperty("email", out var emailElement))
                    {
                        email = emailElement.GetString() ?? string.Empty;
                    }
                    if (document.RootElement.TryGetProperty("password", out var passwordElement))
                    {
                        password = passwordElement.GetString() ?? string.Empty;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                var authOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
                Guid accountId;
                string authMethod;

                if (!string.IsNullOrWhiteSpace(oauthProviderToken))
                {
                    var validator = _serviceProvider.GetRequiredService<IOAuthTokenValidator>();
                    var oauthResult = await AuthenticationEngine.TryLoginByOAuthAsync(authOptions, oauthProviderToken, validator);
                    if (!oauthResult.Found)
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                        return;
                    }
                    accountId = oauthResult.AccountId;
                    authMethod = "pw";
                }
                else if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrEmpty(password))
                {
                    var emailResult = await AuthenticationEngine.LoginWithEmailAsync(authOptions, email, password, string.IsNullOrWhiteSpace(deviceId) ? null : deviceId);
                    if (emailResult.Outcome != EmailLoginOutcome.Success)
                    {
                        context.Response.StatusCode = 401;
                        context.Response.Close();
                        return;
                    }
                    accountId = emailResult.AccountId;
                    authMethod = "pw";
                }
                else if (!string.IsNullOrWhiteSpace(rememberedDeviceId) && rememberedDeviceId.Length <= 128)
                {
                    var rememberedResult = await AuthenticationEngine.TryLoginByDeviceIdAsync(authOptions, rememberedDeviceId);
                    if (!rememberedResult.Found)
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                        return;
                    }
                    accountId = rememberedResult.AccountId;
                    authMethod = "dev";
                }
                else if (!string.IsNullOrWhiteSpace(deviceId) && deviceId.Length <= 128)
                {
                    (_, accountId) = await AuthenticationEngine.LoginOrProvisionAsync(authOptions, deviceId);
                    authMethod = "dev";
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                // Modul: daily login reward - server-authoritative, keyed
                // off PlayerRecord.LastLoginTimestamp, so a replayed login
                // request on the same UTC day is a genuine no-op rather
                // than a repeat grant (see DailyLoginRewardEngine). A
                // failed grant is logged internally and never blocks login
                // - awaited inline rather than fired-and-forgotten only
                // because this handler is already on the async HTTP path,
                // not the 10 Hz tick.
                await DailyLoginRewardEngine.TryGrantLoginRewardAsync(authOptions, accountId);

                string sessionNonce = AuthenticationEngine.GenerateSessionNonce();
                await AuthenticationEngine.SetCurrentSessionNonceAsync(authOptions, accountId, sessionNonce);
                _accountCurrentNonce[accountId] = sessionNonce;
                string token = AuthenticationEngine.GenerateJwt(accountId, sessionNonce, authMethod, _jwtSecretKey, out long expiresAtEpoch);

                var refresh = await TryIssueRefreshTokenAsync(authOptions, accountId, authMethod);

                var response = new AuthLoginResponse
                {
                    Token = token,
                    ExpiresAtEpoch = expiresAtEpoch,
                    RefreshToken = refresh.Token,
                    RefreshExpiresAtEpoch = refresh.ExpiresAtEpoch
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Auth login error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: /api/v1/auth/check-email IS GONE.
        //
        // It answered, unauthenticated and unthrottled, whether a given address
        // has an account here - which is an account ENUMERATION ORACLE: feed it
        // a breach dump and it hands back the subset of addresses that play
        // this game, ready for credential stuffing or a targeted phish. It was
        // built for a convenience nothing ended up using: `isEmailAvailable`
        // had no caller anywhere in the client.
        //
        // Registration still refuses a duplicate address, so nothing a real
        // player does has changed. The difference is that finding out now costs
        // one registration attempt through the auth throttle instead of one
        // cheap POST per address.


        // Modul: Email/Password Auth. Creates a new account (see
        // AuthenticationEngine.RegisterWithEmailAsync) and, on success,
        // immediately issues a JWT exactly like HandleAuthLogin does - a
        // successful registration logs the player straight into the game,
        // it does not require a separate follow-up login call.
        /// <summary>
        /// "I forgot my password."
        ///
        /// ALWAYS ANSWERS 200, whatever happened. Unknown address, known
        /// address, an account with no password, the mail provider being down -
        /// every one of them is reported identically, because any difference
        /// turns this into the account enumeration oracle that
        /// /api/v1/auth/check-email was deleted for. The player is told to
        /// check their email either way, which is also the honest instruction:
        /// if they own the address, the mail is on its way.
        /// </summary>
        private async Task HandleRequestPasswordReset(HttpListenerContext context)
        {
            // Modul: WHETHER THIS SERVER CAN SEND MAIL AT ALL, told to the client.
            //
            // Production ran for weeks with no provider configured, and the
            // screen told every player "a reset link is on its way" - a promise
            // nothing could keep. This answers a question about the SERVER, not
            // about the address: it is the same value for every request, known
            // and unknown accounts alike, so it rebuilds no enumeration oracle.
            bool emailDelivery = _serviceProvider.GetService<Engine.IEmailSender>() is not (null or Engine.DisabledEmailSender);

            try
            {
                string email = string.Empty;

                if (context.Request.HasEntityBody)
                {
                    string body = await ReadBodyAsync(context);
                    try
                    {
                        using var document = System.Text.Json.JsonDocument.Parse(body);
                        if (document.RootElement.TryGetProperty("email", out var emailElement))
                        {
                            email = emailElement.GetString() ?? string.Empty;
                        }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // Even a malformed body answers 200 - see above.
                    }
                }

                if (email.Length > 0)
                {
                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                    long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    string? token = await Engine.PasswordResetEngine.BeginResetAsync(db, email, nowEpoch);

                    if (token != null)
                    {
                        // THE TOKEN TRAVELS IN A HASH FRAGMENT. Everything after
                        // the # is never sent to a server, so the link does not
                        // land in this box's access log, in a proxy's, or in a
                        // Referer header on the next page the player opens.
                        string origin = ResolveClientOrigin(context.Request);
                        string resetUrl = origin + "/#reset=" + token;

                        var sender = _serviceProvider.GetRequiredService<Engine.IEmailSender>();
                        await sender.SendAsync(
                            email,
                            "Reset your FolkIdle password",
                            Engine.PasswordResetEngine.BuildEmailBody(resetUrl));
                    }
                }

                await WriteResetRequestAnswerAsync(context, emailDelivery);
            }
            catch (Exception ex)
            {
                // Logged without the address, for the same reason the provider
                // failure path does not log it.
                Console.WriteLine("Password reset request error: " + ex.Message);
                // STILL 200, with the same body. An exception that answered 500
                // for known addresses and 200 for unknown ones would be the
                // oracle again, wearing a status code.
                try
                {
                    await WriteResetRequestAnswerAsync(context, emailDelivery);
                }
                catch
                {
                    context.Response.Close();
                }
            }
        }

        private static async Task WriteResetRequestAnswerAsync(HttpListenerContext context, bool emailDelivery)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { EmailDelivery = emailDelivery });
            context.Response.Close();
        }

        /// <summary>
        /// "Here is the link, and my new password."
        ///
        /// Unlike the request side these outcomes ARE distinguished: the caller
        /// is already holding a 256-bit token, so "that link has expired" tells
        /// them nothing they could not infer, and refusing without a reason
        /// would strand them in front of a form.
        /// </summary>
        private async Task HandleResetPassword(HttpListenerContext context)
        {
            try
            {
                if (!context.Request.HasEntityBody)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                string body = await ReadBodyAsync(context);

                string token = string.Empty;
                string newPassword = string.Empty;
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("token", out var tokenElement))
                    {
                        token = tokenElement.GetString() ?? string.Empty;
                    }
                    if (document.RootElement.TryGetProperty("password", out var passwordElement))
                    {
                        newPassword = passwordElement.GetString() ?? string.Empty;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var (outcome, accountId, newNonce) = await Engine.PasswordResetEngine.CompleteResetAsync(db, token, newPassword, nowEpoch);

                if (outcome == Engine.PasswordResetOutcome.Success)
                {
                    // Modul: the nonce is already persisted - CompleteResetAsync
                    // wrote it in the same save as the password hash. This is
                    // cache-plus-disconnect only, no second DB write.
                    await EvictAccountSessionAsync(accountId, newNonce);
                }

                context.Response.StatusCode = outcome switch
                {
                    Engine.PasswordResetOutcome.Success => 200,
                    Engine.PasswordResetOutcome.InvalidPassword => 422,
                    Engine.PasswordResetOutcome.Expired => 410,
                    Engine.PasswordResetOutcome.AlreadyUsed => 410,
                    _ => 400,
                };
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Password reset error: " + ex.Message);
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        /// <summary>
        /// Where the client lives, for building a link back into it.
        ///
        /// The Origin header ONLY when it is one this server already trusts -
        /// FOLKIDLE_WEB_ORIGINS is the existing CORS allowlist, so reusing it
        /// means a reset link can only ever point somewhere this deployment
        /// already serves. An attacker-supplied Origin is otherwise a way to
        /// have us email somebody a link to the attacker's own site.
        /// </summary>
        private static string ResolveClientOrigin(HttpListenerRequest request)
        {
            string origin = request.Headers["Origin"] ?? string.Empty;
            string configured = Environment.GetEnvironmentVariable("FOLKIDLE_WEB_ORIGINS") ?? string.Empty;
            var allowedOrigins = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (origin.Length > 0)
            {
                foreach (string allowed in allowedOrigins)
                {
                    if (string.Equals(allowed.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    {
                        return origin.TrimEnd('/');
                    }
                }
            }

            // Falls back to the FIRST configured origin rather than to the
            // request's - a link nobody can follow is better than one that
            // points at somebody else's server.
            foreach (string allowed in allowedOrigins)
            {
                return allowed.TrimEnd('/');
            }

            return string.Empty;
        }

        private async Task HandleAuthRegister(HttpListenerContext context)
        {
            try
            {
                if (!context.Request.HasEntityBody)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                string body = await ReadBodyAsync(context);

                string email = string.Empty;
                string username = string.Empty;
                string password = string.Empty;
                string deviceId = string.Empty;
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("email", out var emailElement))
                    {
                        email = emailElement.GetString() ?? string.Empty;
                    }
                    if (document.RootElement.TryGetProperty("username", out var usernameElement))
                    {
                        username = usernameElement.GetString() ?? string.Empty;
                    }
                    if (document.RootElement.TryGetProperty("password", out var passwordElement))
                    {
                        password = passwordElement.GetString() ?? string.Empty;
                    }
                    if (document.RootElement.TryGetProperty("deviceId", out var deviceIdElement))
                    {
                        deviceId = deviceIdElement.GetString() ?? string.Empty;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                var authOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
                var result = await AuthenticationEngine.RegisterWithEmailAsync(authOptions, email, username, password, string.IsNullOrWhiteSpace(deviceId) ? null : deviceId);

                if (result.Outcome != EmailRegisterOutcome.Success)
                {
                    context.Response.StatusCode = result.Outcome switch
                    {
                        EmailRegisterOutcome.EmailInUse => 409,
                        EmailRegisterOutcome.UsernameInUse => 409,
                        EmailRegisterOutcome.InvalidEmail => 400,
                        EmailRegisterOutcome.InvalidUsername => 400,
                        EmailRegisterOutcome.InvalidPassword => 400,
                        _ => 500
                    };
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.OutputStream, new RegisterErrorResponse { Reason = result.Outcome.ToString() });
                    context.Response.Close();
                    return;
                }

                await DailyLoginRewardEngine.TryGrantLoginRewardAsync(authOptions, result.AccountId);

                string sessionNonce = AuthenticationEngine.GenerateSessionNonce();
                await AuthenticationEngine.SetCurrentSessionNonceAsync(authOptions, result.AccountId, sessionNonce);
                _accountCurrentNonce[result.AccountId] = sessionNonce;
                string token = AuthenticationEngine.GenerateJwt(result.AccountId, sessionNonce, "pw", _jwtSecretKey, out long expiresAtEpoch);

                var refresh = await TryIssueRefreshTokenAsync(authOptions, result.AccountId, "pw");

                var response = new AuthLoginResponse
                {
                    Token = token,
                    ExpiresAtEpoch = expiresAtEpoch,
                    RefreshToken = refresh.Token,
                    RefreshExpiresAtEpoch = refresh.ExpiresAtEpoch
                };
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Auth register error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: irreversibly links the caller's OWN authenticated session
        // (resolved from the Bearer JWT, see TryResolveAuthenticatedPlayerAsync)
        // to an external OAuth identity. Requires an already-authenticated
        // session precisely because linking must bind to "the current
        // active session's AccountId", not to an AccountId the caller could
        // otherwise supply directly in the request body.
        private async Task HandleOAuthLink(HttpListenerContext context)
        {
            try
            {
                var (playerId, authMethod) = await TryResolveAuthenticatedPlayerWithMethodAsync(context.Request);
                if (playerId == 0L)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                Guid accountId = await ResolveAccountIdAsync(playerId);

                string body = await ReadBodyAsync(context);

                string oauthProviderToken;
                string suppliedPassword;
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(body);
                    if (!document.RootElement.TryGetProperty("oauthProviderToken", out var tokenElement))
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }
                    oauthProviderToken = tokenElement.GetString() ?? string.Empty;
                    suppliedPassword = document.RootElement.TryGetProperty("password", out var pwElement)
                        ? (pwElement.GetString() ?? string.Empty)
                        : string.Empty;
                }
                catch (System.Text.Json.JsonException)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                // Modul: step-up gate, Task 10 - see HandleBillingVerify's
                // identical block. Linking an external identity is just as
                // irreversible/account-changing as a purchase, so a
                // device-bearer session on a password-holding account must
                // re-prove the password before LinkOAuthAccountAsync runs.
                if (await RequiresPasswordStepUpAsync(playerId, authMethod))
                {
                    await using var stepUpDb = await _contextFactory.CreateDbContextAsync();
                    if (suppliedPassword.Length == 0 || !await VerifyStepUpPasswordAsync(stepUpDb, playerId, suppliedPassword))
                    {
                        // Modul: matches HandleBillingVerify's own log line -
                        // this route is already in the AuthThrottle allowlist
                        // (it verified a password for recovery-login purposes
                        // long before this gate existed), but the throttle
                        // alone leaves no record of which player a
                        // brute-force attempt targeted.
                        Console.WriteLine($"Step-up rejected: player {playerId} presented a device-bearer session with a missing or incorrect password on oauth-link.");
                        WriteStepUpRequired(context);
                        context.Response.Close();
                        return;
                    }
                }

                var authOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
                var validator = _serviceProvider.GetRequiredService<IOAuthTokenValidator>();
                OAuthLinkOutcome outcome = await AuthenticationEngine.LinkOAuthAccountAsync(authOptions, accountId, oauthProviderToken, validator);

                context.Response.StatusCode = outcome switch
                {
                    OAuthLinkOutcome.Success => 200,
                    OAuthLinkOutcome.InvalidToken => 400,
                    OAuthLinkOutcome.AccountNotFound => 404,
                    OAuthLinkOutcome.AlreadyLinked => 409,
                    OAuthLinkOutcome.ExternalIdentityInUse => 409,
                    _ => 500
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"OAuth link error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
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
        internal static bool DevToolsEnabled()
            => DevToolsEnabled(
                Environment.GetEnvironmentVariable("FOLKIDLE_DEV_TOOLS"),
                Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"));

        // Modul: the flag alone was a convention - the box's real .env is not
        // versioned, and one stray FOLKIDLE_DEV_TOOLS=1 there would let any
        // signed-in guest open or close the world boss window and wipe every
        // player's attempts. Production (ops/oracle sets DOTNET_ENVIRONMENT)
        // refuses the tools whatever the flag says.
        internal static bool DevToolsEnabled(string? flag, string? dotnetEnvironment)
            => flag == "1"
               && !string.Equals(dotnetEnvironment, "Production", StringComparison.OrdinalIgnoreCase);

        private sealed class DevWorldBossWindowRequest
        {
            public bool Open { get; set; }
            public long DurationSeconds { get; set; } = 900;
        }

        /// <summary>
        /// Dev-box tools. Every route here answers 404 unless FOLKIDLE_DEV_TOOLS=1.
        ///
        /// Modul: NOT under /api/v1/admin/, because the dev fixture is not an
        /// admin by IsAdmin's rule (username Mivoru or one e-mail), and a gate the
        /// fixture fails is a tool exercise.mjs cannot use. The environment
        /// variable is the gate instead, and it fails CLOSED: run-dev.ps1 sets
        /// it, ops/oracle does not, so production 404s. It exists because the
        /// world boss window is calendar-driven and nothing could strike on the
        /// other 13-16 days of a month (task 25).
        /// </summary>
        private async Task HandleDevEndpoints(HttpListenerContext context, string requestPath)
        {
            try
            {
                if (!DevToolsEnabled())
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                // Modul: THE DEEP'S WAY IN (task 37). Descending needs a run at
                // the bottom of floor 8, which no fixture sheet reaches reliably
                // - the doors are a gamble by design. This places the CALLER's
                // run there, as a bank-ready full clear, so exercise.mjs can
                // test a descent on every run instead of on a lucky one.
                if (requestPath == "/api/v1/dev/delve/at-bottom" && context.Request.HttpMethod == "POST")
                {
                    var delve = _serviceProvider.GetRequiredService<FolkIdle.Server.Domain.Economy.DelveEngine>();
                    var view = await delve.DevPlaceRunAtBottomAsync(playerId);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.OutputStream, view);
                    return;
                }

                // The lantern purchase needs a Deep run whose light is out,
                // which failing doors reaches only by chance.
                if (requestPath == "/api/v1/dev/delve/lantern-out" && context.Request.HttpMethod == "POST")
                {
                    var delve = _serviceProvider.GetRequiredService<FolkIdle.Server.Domain.Economy.DelveEngine>();
                    bool done = await delve.DevPutOutTheLanternAsync(playerId);
                    context.Response.StatusCode = done ? 200 : 409;
                    return;
                }

                // Task 83: finish the running Workshop commission now, and with
                // {"Refund": true} give its price back - so exercise.mjs can
                // place, collect and discard one without spending the fixture.
                if (requestPath == "/api/v1/dev/workshop/finish" && context.Request.HttpMethod == "POST")
                {
                    await HandleDevWorkshopFinish(context, playerId);
                    return;
                }

                // The quest line: take back a claim's reward so exercise.mjs
                // can claim on the fixture and round-trip it.
                if (requestPath == "/api/v1/dev/quests/unclaim" && context.Request.HttpMethod == "POST")
                {
                    await HandleDevQuestUnclaim(context, playerId);
                    return;
                }

                // Task 54: a cosmetic chest for the caller, so exercise.mjs can
                // open one without thousands of kills first.
                if (requestPath == "/api/v1/dev/cosmetics/chest" && context.Request.HttpMethod == "POST")
                {
                    await HandleDevCosmeticChest(context, playerId);
                    return;
                }

                // Task 69: nine Normal pieces of one region-5 item the fixture
                // does not otherwise hold, so exercise.mjs can fuse a whole
                // stack (9 -> 3 -> 1) and bin the result - a round trip that
                // leaves the fixture as it found it, instead of eating the
                // fixture's own piles one run at a time.
                if (requestPath == "/api/v1/dev/forge/stack" && context.Request.HttpMethod == "POST")
                {
                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                    long[] ids = await DevFixtureSeeder.GrantForgeStackAsync(db, playerId);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.OutputStream, new
                    {
                        BaseItemId = DevFixtureSeeder.DevStackBaseId,
                        Ids = ids,
                    });
                    return;
                }

                // A title for the caller, through the real idempotent grant, so
                // the title picker and the profile can be exercised without a
                // player first clearing floor 10 by luck.
                if (requestPath == "/api/v1/dev/titles/grant" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    string? slug = null;
                    try
                    {
                        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                        if (parsed.RootElement.TryGetProperty("Slug", out var el) && el.ValueKind == JsonValueKind.String) slug = el.GetString();
                    }
                    catch (JsonException) { }

                    if (slug == null || FolkIdle.Server.Domain.Progression.TitleRegistry.Find(slug) == null)
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                    await FolkIdle.Server.Domain.Progression.TitleEngine.GrantAsync(db, playerId, slug, DateTime.UtcNow);
                    context.Response.StatusCode = 200;
                    return;
                }

                // Task 87: put one boss's Ascension ladder back to a step, so
                // exercise.mjs can climb a rung and leave the fixture as it found
                // it. Body {Region, Step, BossDefeated?}; reloads the live session so the tick's
                // cache of the ladder agrees with the table again.
                if (requestPath == "/api/v1/dev/boss-ascension/restore" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    int region = 0, step = 0;
                    bool? bossDefeated = null;
                    try
                    {
                        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                        if (parsed.RootElement.TryGetProperty("Region", out var r) && r.TryGetInt32(out int rv)) region = rv;
                        if (parsed.RootElement.TryGetProperty("Step", out var st) && st.TryGetInt32(out int sv)) step = sv;
                        if (parsed.RootElement.TryGetProperty("BossDefeated", out var bd)
                            && (bd.ValueKind == JsonValueKind.True || bd.ValueKind == JsonValueKind.False)) bossDefeated = bd.GetBoolean();
                    }
                    catch (JsonException) { }

                    if (!FolkIdle.Server.Domain.Combat.BossAscensionRegistry.IsValidRegion(region))
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                        await FolkIdle.Server.Domain.Combat.BossAscensionEngine.DevRestoreAsync(db, playerId, region, step, bossDefeated);
                    }
                    CommandQueue.Enqueue(new PlayerCommand
                    {
                        PlayerId = playerId,
                        Packet = new ClientCommandPacket { Command = CommandType.ReloadState }
                    });
                    await WriteJsonAsync(context, new { Ok = true });
                    return;
                }

                // Task 84: put one Great Work back to (Stage, Progress) and move the
                // stock of one material by a signed StockDelta, so exercise.mjs can
                // deposit and leave the fixture as it found it. Body {Region, Stage,
                // Progress, Material (0 log, 1 ore), StockDelta}; reloads the session
                // so the tick's cache of the stages agrees with the table again.
                if (requestPath == "/api/v1/dev/great-works/restore" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    int region = 0, stage = 0, material = 0;
                    long progress = 0, stockDelta = 0;
                    try
                    {
                        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                        if (parsed.RootElement.TryGetProperty("Region", out var r) && r.TryGetInt32(out int rv)) region = rv;
                        if (parsed.RootElement.TryGetProperty("Stage", out var st) && st.TryGetInt32(out int sv)) stage = sv;
                        if (parsed.RootElement.TryGetProperty("Material", out var mt) && mt.TryGetInt32(out int mv)) material = mv;
                        if (parsed.RootElement.TryGetProperty("Progress", out var pr) && pr.TryGetInt64(out long pv)) progress = pv;
                        if (parsed.RootElement.TryGetProperty("StockDelta", out var sd) && sd.TryGetInt64(out long sdv)) stockDelta = sdv;
                    }
                    catch (JsonException) { }

                    if (!FolkIdle.Server.Domain.Progression.GreatWorksRegistry.IsValidRegion(region)
                        || !FolkIdle.Server.Domain.Progression.GreatWorksRegistry.IsValidMaterialKind(material))
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                        await FolkIdle.Server.Domain.Progression.GreatWorksEngine.DevRestoreAsync(db, playerId, region, stage, progress, material, stockDelta);
                    }
                    CommandQueue.Enqueue(new PlayerCommand
                    {
                        PlayerId = playerId,
                        Packet = new ClientCommandPacket { Command = CommandType.ReloadState }
                    });
                    await WriteJsonAsync(context, new { Ok = true });
                    return;
                }

                if (_worldBossEngine == null)
                {
                    context.Response.StatusCode = 503;
                    return;
                }

                if (requestPath == "/api/v1/dev/worldboss/window" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    var req = JsonSerializer.Deserialize<DevWorldBossWindowRequest>(
                        string.IsNullOrWhiteSpace(body) ? "{}" : body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new DevWorldBossWindowRequest();

                    if (req.Open)
                    {
                        await _worldBossEngine.OpenManualWindowAsync(req.DurationSeconds);
                    }
                    else
                    {
                        await _worldBossEngine.CloseManualWindowAsync();
                    }

                    await WriteDevWorldBossStateAsync(context, playerId);
                    return;
                }

                if (requestPath == "/api/v1/dev/worldboss/attempt" && context.Request.HttpMethod == "GET")
                {
                    await WriteDevWorldBossStateAsync(context, playerId);
                    return;
                }

                context.Response.StatusCode = 404;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Dev endpoint error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>The boss and the CALLER's attempt row, straight from the database.</summary>
        private async Task WriteDevWorldBossStateAsync(HttpListenerContext context, long playerId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            var snapshot = await db.WorldBossSnapshots.AsNoTracking()
                .FirstOrDefaultAsync(b => b.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);
            var attempt = await db.PlayerWorldBossAttempts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.PlayerId == playerId && a.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, new
            {
                EventState = snapshot?.EventState ?? 0,
                CurrentHp = snapshot?.CurrentHp ?? 0,
                MaxHp = snapshot?.MaxHp ?? 0,
                EventEndEpoch = snapshot?.EventEndEpoch ?? 0,
                AttemptCount = attempt?.AttemptCount ?? 0,
                TotalInflictedDamage = attempt?.TotalInflictedDamage ?? 0,
            });
        }

        private bool IsAdmin(FolkIdle.Server.Models.PlayerRecord? player)
        {
            if (player == null) return false;
            if (!string.IsNullOrEmpty(player.Username) && player.Username.Equals("Mivoru", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(player.Email) && player.Email.Equals("prochalcz@gmail.com", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private async Task HandleAdminEndpoints(HttpListenerContext context, string requestPath)
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

                var player = await db.PlayerRecords.AsNoTracking().Where(p => p.Id == requesterId).FirstOrDefaultAsync();
                if (!IsAdmin(player))
                {
                    context.Response.StatusCode = 403;
                    return;
                }

                if (requestPath == "/api/v1/admin/status" && context.Request.HttpMethod == "GET")
                {
                    string json = JsonSerializer.Serialize(new { isAdmin = true, profanityEnabled = FolkIdle.Server.Engine.ChatProfanityFilter.IsEnabled });
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    return;
                }

                // Modul: SEASON CONTROL (2026-09-28). The owner runs the
                // season by hand: pause the rollover, move the end, or end it
                // now. See SeasonalEraRecord.IsRolloverPaused.
                if (requestPath == "/api/v1/admin/season" && context.Request.HttpMethod == "GET")
                {
                    var era = await db.SeasonalEraRecords.AsNoTracking()
                        .Where(e => e.IsActive)
                        .OrderBy(e => e.EndTimestamp)
                        .FirstOrDefaultAsync();
                    string json = JsonSerializer.Serialize(new
                    {
                        EraId = era?.EraId ?? 0,
                        EndTimestamp = era?.EndTimestamp ?? 0L,
                        Paused = era?.IsRolloverPaused ?? false,
                        EndRequested = FolkIdle.Server.Engine.SeasonalRotationEngine.EndNowPending,
                        // Task 88: the date is kept and shown, and no longer
                        // ends anything - players end their own runs.
                        CalendarEndsSeason = false,
                    });
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    return;
                }

                if (requestPath == "/api/v1/admin/season" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    string action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? string.Empty : string.Empty;

                    var era = await db.SeasonalEraRecords
                        .Where(e => e.IsActive)
                        .OrderBy(e => e.EndTimestamp)
                        .FirstOrDefaultAsync();
                    if (era == null)
                    {
                        context.Response.StatusCode = 409;
                        return;
                    }

                    long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    switch (action)
                    {
                        case "pause":
                            era.IsRolloverPaused = true;
                            break;
                        case "resume":
                            // Resuming a season whose end has already passed
                            // would roll it over within five minutes, which is
                            // "end now" by accident. Refused; move the end first.
                            if (era.EndTimestamp <= nowEpoch)
                            {
                                context.Response.StatusCode = 409;
                                return;
                            }
                            era.IsRolloverPaused = false;
                            break;
                        case "setEnd":
                            long end = doc.RootElement.TryGetProperty("endTimestamp", out var e) && e.TryGetInt64(out long v) ? v : 0L;
                            // At least an hour ahead: an end in the past on an
                            // unpaused season is an immediate wipe.
                            if (end < nowEpoch + 3600)
                            {
                                context.Response.StatusCode = 400;
                                return;
                            }
                            era.EndTimestamp = end;
                            break;
                        default:
                            context.Response.StatusCode = 400;
                            return;
                    }

                    await db.SaveChangesAsync();
                    Console.WriteLine($"[season] admin {requesterId}: {action} -> era {era.EraId} end {era.EndTimestamp} paused {era.IsRolloverPaused}");
                    context.Response.StatusCode = 200;
                    return;
                }

                if (requestPath == "/api/v1/admin/season/end-now" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    string phrase = doc.RootElement.TryGetProperty("confirm", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                    // Every player's level, gear and gold go. A typed phrase,
                    // like account deletion, so a stray tap cannot do it.
                    if (phrase != "END SEASON")
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }
                    Console.WriteLine($"[season] admin {requesterId}: END NOW requested");
                    FolkIdle.Server.Engine.SeasonalRotationEngine.RequestEndNow();
                    context.Response.StatusCode = 202;
                    return;
                }

                if (requestPath == "/api/v1/admin/profanity" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    var req = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, bool>>(body);
                    if (req != null && req.TryGetValue("enabled", out bool isEnabled))
                    {
                        FolkIdle.Server.Engine.ChatProfanityFilter.IsEnabled = isEnabled;
                        context.Response.StatusCode = 200;
                        return;
                    }
                    context.Response.StatusCode = 400;
                    return;
                }

                if (requestPath == "/api/v1/admin/announce" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    var req = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(body);
                    if (req != null && req.TryGetValue("text", out string? message) && !string.IsNullOrWhiteSpace(message))
                    {
                        if (message.Length > 128) message = message.Substring(0, 128);
                        
                        var bytes = System.Text.Encoding.UTF8.GetBytes(message);
                        var packet = new ResponseChatMessagePacket
                        {
                            MessageLength = (ushort)bytes.Length,
                            ChannelType = 3, // ANNOUNCEMENT
                            SenderPlayerId = -1,
                            TimestampEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                        unsafe
                        {
                            fixed (byte* src = bytes)
                            {
                                System.Buffer.MemoryCopy(src, packet.MessageText, 128, bytes.Length);
                            }
                        }

                        // Broadcast to everyone, through each outbox - the
                        // same fan-out chat uses (see ChatFrames), so an
                        // announcement is queued rather than dropped behind
                        // a state frame and one stalled socket cannot hold
                        // this request open.
                        var frames = new ChatFrames(packet);
                        foreach (var target in _connectedClients.Values)
                        {
                            if (target.Socket.State == System.Net.WebSockets.WebSocketState.Open)
                            {
                                frames.EnqueueTo(target);
                            }
                        }

                        // Task 110e: an admin announcement is News too, so a
                        // player who signs in afterwards still reads it.
                        await ChatHistory.RecordAsync(_serviceProvider, ChatEngine.AnnouncementChannelType, 0, packet.SenderPlayerId, message, packet.TimestampEpochMs);
                        
                        context.Response.StatusCode = 200;
                        return;
                    }
                    context.Response.StatusCode = 400;
                    return;
                }
                
                // Modul: the whole point of the penalty table - a moderator can
                // answer "why is this account restricted" from the admin
                // screen, rather than by reading two booleans in a database
                // console and guessing which of five writers set them.
                if (requestPath == "/api/v1/admin/penalties" && context.Request.HttpMethod == "GET")
                {
                    var pq = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                    string who = pq["username"] ?? string.Empty;
                    if (string.IsNullOrEmpty(who))
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    var subject = await db.PlayerRecords.AsNoTracking()
                        .FirstOrDefaultAsync(p => p.Username != null && p.Username.ToLower() == who.ToLower());
                    if (subject == null)
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    var history = await db.AccountPenalties.AsNoTracking()
                        .Where(a => a.PlayerId == subject.Id)
                        .OrderByDescending(a => a.AppliedAtEpochMs)
                        .Take(50)
                        .ToListAsync();

                    var payload = new
                    {
                        subject.Id,
                        subject.Username,
                        Restricted = subject.IsQuarantined || subject.Quarantine_Active,
                        // Modul: a restricted account with NO row predates this
                        // table. Said plainly rather than rendered as an empty
                        // history, because "we do not know" and "nothing
                        // happened" are different answers and only one of them
                        // is honest.
                        ReasonKnown = history.Count > 0,
                        Penalties = history.Select(a => new
                        {
                            a.Id,
                            Source = PenaltySource.Describe(a.Source),
                            a.ReasonCode,
                            a.DetailCode,
                            a.AppliedAtEpochMs,
                            a.AppliedBy,
                            a.Note,
                            a.LiftedAtEpochMs,
                            a.LiftedBy
                        })
                    };

                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.OutputStream, payload);
                    return;
                }

                if (requestPath == "/api/v1/admin/ban" && context.Request.HttpMethod == "POST")
                {
                    var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                    string targetUsername = query["username"] ?? string.Empty;
                    if (!string.IsNullOrEmpty(targetUsername))
                    {
                        var target = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Username != null && p.Username.ToLower() == targetUsername.ToLower());
                        if (target != null)
                        {
                            target.IsQuarantined = true;
                            target.Quarantine_Active = true;

                            // Modul: A MODERATOR BAN IS NOT AN ANTI-CHEAT FLAG,
                            // 2026-09-01. Both write the same two booleans on
                            // PlayerRecords, so after the fact they were
                            // indistinguishable - "the detector caught you" and
                            // "a human decided" read identically, which is
                            // unfair to the player and useless to the
                            // moderator. The row carries WHO, WHEN and WHICH.
                            db.AccountPenalties.Add(new AccountPenalty
                            {
                                PlayerId = target.Id,
                                Source = PenaltySource.Admin,
                                ReasonCode = 0,
                                DetailCode = 0,
                                AppliedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                AppliedBy = player?.Username,
                                Note = query["reason"]
                            });

                            await db.SaveChangesAsync();
                            context.Response.StatusCode = 200;
                            return;
                        }
                    }
                    context.Response.StatusCode = 404;
                    return;
                }

                if (requestPath == "/api/v1/admin/unban" && context.Request.HttpMethod == "POST")
                {
                    var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                    string targetUsername = query["username"] ?? string.Empty;
                    if (!string.IsNullOrEmpty(targetUsername))
                    {
                        var target = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Username != null && p.Username.ToLower() == targetUsername.ToLower());
                        if (target != null)
                        {
                            target.IsQuarantined = false;
                            target.Quarantine_Active = false;

                            // Modul: AND UNFREEZE THEIR MARKET ORDERS. The
                            // anti-cheat's shadow ban flags every open listing
                            // (see AntiCheatTelemetryEngine), and lifting the
                            // account flag did not clear them - so an unbanned
                            // player was returned to a game where their
                            // listings stayed dead, with nothing saying why.
                            await db.Database.ExecuteSqlRawAsync(
                                "UPDATE \"MarketOrderRecords\" SET \"IsQuarantined\" = FALSE WHERE \"SellerId\" = {0}",
                                target.Id);

                            // Stamped, not deleted - "flagged in August, cleared
                            // in September" is the history that makes a second
                            // flag readable.
                            long liftedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            await db.Database.ExecuteSqlRawAsync(
                                "UPDATE account_penalties SET \"LiftedAtEpochMs\" = {0}, \"LiftedBy\" = {1} " +
                                "WHERE \"PlayerId\" = {2} AND \"LiftedAtEpochMs\" IS NULL",
                                liftedAt, (object?)player?.Username ?? System.DBNull.Value, target.Id);

                            await db.SaveChangesAsync();
                            context.Response.StatusCode = 200;
                            return;
                        }
                    }
                    context.Response.StatusCode = 404;
                    return;
                }

                if (requestPath == "/api/v1/admin/mail" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    var req = JsonSerializer.Deserialize<AdminMailRequest>(body);
                    if (req == null
                        || req.Diamonds < 0 || req.Gold < 0
                        || (!string.IsNullOrEmpty(req.TitleSlug) && FolkIdle.Server.Domain.Progression.TitleRegistry.Find(req.TitleSlug) == null))
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    // Modul: SEVERAL NAMES, comma-separated (2026-10-09), so a
                    // gift to three testers is one send - and an empty name is
                    // still "everyone", which is why a mail carrying diamonds or
                    // a title must NAME its recipients: a slip of the field must
                    // not hand every guest on the server a title.
                    var names = (req.TargetUsername ?? string.Empty)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(n => n.ToLowerInvariant())
                        .Distinct()
                        .ToList();
                    if (names.Count == 0 && (req.Diamonds > 0 || !string.IsNullOrEmpty(req.TitleSlug)))
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    var targets = names.Count == 0
                        ? await db.PlayerRecords.Select(p => new { p.Id, p.Username, p.Email }).ToListAsync()
                        : await db.PlayerRecords
                            .Where(p => p.Username != null && names.Contains(p.Username.ToLower()))
                            .Select(p => new { p.Id, p.Username, p.Email })
                            .ToListAsync();
                    if (names.Count > 0 && targets.Count != names.Count)
                    {
                        // A misspelt name sends nothing, rather than a gift to some.
                        context.Response.StatusCode = 404;
                        return;
                    }

                    db.MailboxInstances.AddRange(targets.Select(t => new FolkIdle.Server.Models.MailboxInstance
                    {
                        PlayerId = t.Id,
                        BaseItemId = req.BaseItemId ?? string.Empty,
                        QualityTier = req.QualityTier,
                        Quantity = req.Quantity,
                        GoldAttachment = req.Gold,
                        DiamondAttachment = req.Diamonds,
                        TitleAttachment = string.IsNullOrEmpty(req.TitleSlug) ? null : req.TitleSlug,
                        SenderName = req.SenderName,
                        MessageText = req.MessageText,
                        ReceivedTimestamp = now
                    }));
                    await db.SaveChangesAsync();

                    // Modul: the wrapper is CZECH on purpose (owner, 2026-10-09):
                    // the testers are Czech and the owner writes the message in
                    // Czech, so an English greeting around it read as mixed.
                    // The same message by email, to the named recipients that
                    // have an address. Never to "everyone": that is a newsletter,
                    // and it needs consent this endpoint does not check.
                    int emailed = 0;
                    if (req.SendEmail && names.Count > 0 && !string.IsNullOrWhiteSpace(req.MessageText))
                    {
                        var sender = _serviceProvider.GetRequiredService<Engine.IEmailSender>();
                        string subject = string.IsNullOrWhiteSpace(req.EmailSubject) ? "Zpráva z FolkIdle" : req.EmailSubject!;
                        foreach (var t in targets.Where(t => !string.IsNullOrWhiteSpace(t.Email)))
                        {
                            string text = $"Ahoj {t.Username},\n\n{req.MessageText}\n\nOdměnu najdeš v ingame mailboxu.\n\nhttps://folkidle.duckdns.org";
                            if (await sender.SendAsync(t.Email!, subject, text)) emailed++;
                        }
                    }

                    await WriteJsonAsync(context, new { Mailed = targets.Count, Emailed = emailed });
                    return;
                }

                context.Response.StatusCode = 404;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Admin endpoint error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
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
                var activeBuffs = await db.GuildActiveBuffs.Where(b => b.GuildId == member.GuildId && b.ExpiresAt > DateTime.UtcNow).ToListAsync();
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

        private async Task HandleGuildDepotDonate(HttpListenerContext context)
        {
            try
            {
                long requesterId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (requesterId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                string itemId = payload.GetProperty("itemId").GetString() ?? "";
                int quantity = 0;
                var qProp = payload.GetProperty("quantity");
                if (qProp.ValueKind == JsonValueKind.Number)
                {
                    quantity = qProp.GetInt32();
                }
                else if (qProp.ValueKind == JsonValueKind.String)
                {
                    int.TryParse(qProp.GetString(), out quantity);
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.PlayerId == requesterId);
                if (member == null)
                {
                    context.Response.StatusCode = 403;
                    return;
                }

                var engine = new GuildContributionEngine(_serviceProvider, _playerSessionRegistry);
                bool success = await engine.ContributeDepotMaterialAsync(requesterId, member.GuildId, itemId, quantity);

                context.Response.StatusCode = success ? 200 : 400;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling depot donate: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleGuildBuffsActivate(HttpListenerContext context)
        {
            try
            {
                long requesterId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (requesterId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                var body = await ReadBodyAsync(context);
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                string buffType = payload.GetProperty("buffType").GetString() ?? "";
                int tier = payload.GetProperty("tier").GetInt32();
                string path = payload.GetProperty("path").GetString() ?? "common"; // "common" or "rare"

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var member = await db.GuildMembers.FirstOrDefaultAsync(m => m.PlayerId == requesterId);
                if (member == null)
                {
                    context.Response.StatusCode = 403;
                    return;
                }

                var engine = new GuildContributionEngine(_serviceProvider, _playerSessionRegistry);
                bool success = await engine.ActivateGuildBuffAsync(requesterId, member.GuildId, buffType, tier, path);

                context.Response.StatusCode = success ? 200 : 400;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling buff activate: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

}
}
