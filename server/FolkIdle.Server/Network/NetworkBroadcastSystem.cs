using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
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
    public class WebSocketSession
    {
        public WebSocket Socket { get; }
        public ClientInputThrottler Throttler { get; }
        public string RedisLockToken { get; }
        public TokenBucket TokenBucket;
        public TokenBucket ChatTokenBucket;
        public byte[] DiagnosticSendBuffer { get; }

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
        // send path below takes the same reusable-buffer, blittable-write
        // route it always has. Nothing about the binary path changed to make
        // room for this.
        public bool UseJsonProtocol { get; }

        // Modul: .NET's WebSocket forbids more than one outstanding
        // send-family operation (SendAsync or CloseAsync) in flight at a
        // time on the same instance. State broadcasts (SendToPlayer, 1Hz),
        // chat broadcasts (BroadcastChatMessage), and disconnects
        // (ForceDisconnect, DisconnectAllClientsGracefullyAsync, stale-
        // session eviction) are independent call sites that can all target
        // the same socket - each individually well-behaved in isolation,
        // but unsynchronized against each other, which is what let two of
        // them race and throw "already one outstanding SendAsync call",
        // silently aborting the socket with no error surfaced anywhere.
        // Every send/close on this session's socket MUST go through
        // SendAsync/CloseAsync below rather than Socket.SendAsync/
        // Socket.CloseAsync directly, so exactly one send-family operation
        // is ever in flight regardless of which caller issued it.
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        public WebSocketSession(WebSocket socket, string redisLockToken, bool useJsonProtocol = false)
        {
            Socket = socket;
            RedisLockToken = redisLockToken;
            UseJsonProtocol = useJsonProtocol;
            Throttler = new ClientInputThrottler();
            TokenBucket = NetworkThrottlingEngine.CreateBucket();
            ChatTokenBucket = ChatEngine.CreateChatBucket();
            DiagnosticSendBuffer = new byte[Marshal.SizeOf<StateUpdatePacket>()];
        }

        /// <summary>
        /// How long one frame may take before the socket is considered wedged.
        ///
        /// Generous - a mobile client on a bad connection should not be evicted
        /// for a slow second. What it stops is the unbounded case: a peer that
        /// has stopped reading entirely, where the send never completes at all.
        /// </summary>
        private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(20);

        /// <summary>
        /// True once a send has timed out. The broadcast loop reads it and
        /// evicts the session, which is what lets the client's own reconnect
        /// logic run - see SendAsync for why silence was worse than a
        /// disconnect.
        /// </summary>
        public volatile bool IsWedged;

        /// <summary>
        /// Sends one frame, or drops it if this socket is already busy.
        ///
        /// THIS IS THE COMBAT FREEZE.
        ///
        /// The lock is necessary - .NET forbids two outstanding sends on one
        /// WebSocket - but it was taken with `WaitAsync(cancellationToken)`
        /// where every caller passes CancellationToken.None, and the sends are
        /// fire-and-forget from a 10 Hz broadcast. So when a peer stopped
        /// reading, TCP back-pressure left `Socket.SendAsync` pending
        /// indefinitely, that send kept the semaphore, and every following
        /// tick's send queued behind it forever.
        ///
        /// Nothing threw. Nothing closed. The socket stayed open with no frames
        /// arriving, so the client - whose reconnect logic is fine - never had
        /// anything to reconnect FROM: it was not disconnected, it was being
        /// ignored. HP stopped ticking, kills stopped appearing, and F5 fixed
        /// it because a new socket has an uncontended lock. It also grew the
        /// queue without bound for as long as the player left the tab open.
        ///
        /// Two changes, and the first is the one that matters. A state update
        /// is an ABSOLUTE SNAPSHOT, not a delta, so a frame that cannot be sent
        /// right now is worth nothing - the next tick carries the same truth,
        /// only fresher. Taking the lock with a zero timeout turns a stalled
        /// socket into dropped frames instead of an infinite queue. The second
        /// bounds the underlying send, so a socket that is truly gone is torn
        /// down and the client is told, rather than left watching a frozen
        /// screen.
        /// </summary>
        public async Task SendAsync(ArraySegment<byte> segment, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            // Zero timeout: do not queue behind an in-flight send.
            if (!await _sendLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                if (Socket.State != WebSocketState.Open) return;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(SendTimeout);

                try
                {
                    await Socket.SendAsync(segment, messageType, endOfMessage, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // The peer is not reading. Mark it so the broadcast loop
                    // evicts the session - a disconnect the client can act on
                    // beats a socket that is open and silent.
                    IsWedged = true;
                    throw new WebSocketException($"send timed out after {SendTimeout.TotalSeconds:F0}s");
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Sends one state frame on the binary protocol without allocating.
        ///
        /// The lock is taken BEFORE the copy, which is the whole point. The
        /// caller used to write this session's reusable buffer and then hand it
        /// to a fire-and-forget send - so the next tick's write could land in
        /// the buffer the previous send was still reading, and the client would
        /// receive a frame spliced out of two. Acquiring first means a frame is
        /// either copied and sent, or dropped before anything is touched.
        ///
        /// Synchronous Wait(0) rather than WaitAsync: it never blocks (zero
        /// timeout) and it keeps the copy off an await boundary, where a Span
        /// cannot go.
        /// </summary>
        public Task SendStateFrameAsync(ref StateUpdatePacket packet)
        {
            if (!_sendLock.Wait(0))
            {
                return Task.CompletedTask;
            }

            try
            {
                if (Socket.State != WebSocketState.Open) return Task.CompletedTask;

                ReadOnlySpan<StateUpdatePacket> span = MemoryMarshal.CreateReadOnlySpan(ref packet, 1);
                MemoryMarshal.AsBytes(span).CopyTo(DiagnosticSendBuffer);
            }
            catch
            {
                _sendLock.Release();
                throw;
            }

            return SendHeldAsync(new ArraySegment<byte>(DiagnosticSendBuffer), WebSocketMessageType.Binary);
        }

        /// <summary>Sends with the lock ALREADY held, and releases it.</summary>
        private async Task SendHeldAsync(ArraySegment<byte> segment, WebSocketMessageType messageType)
        {
            try
            {
                using var timeout = new CancellationTokenSource(SendTimeout);
                try
                {
                    await Socket.SendAsync(segment, messageType, true, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    IsWedged = true;
                    throw new WebSocketException($"send timed out after {SendTimeout.TotalSeconds:F0}s");
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            // Bounded, like SendAsync: a close must never be the thing that
            // hangs the eviction path for a socket that is already gone.
            if (!await _sendLock.WaitAsync(SendTimeout, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                if (Socket.State != WebSocketState.Open) return;
                await Socket.CloseAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct AdminCommandPacket
    {
        public byte CommandType; // 1 = XP, 2 = Drops
        public int MultiplierValue;
    }

    public class NetworkBroadcastSystem
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
        private PlayerSessionRegistry? _playerSessionRegistry;
        private readonly ChatEngine _chatEngine;

        // Modul: Full-Stack Social Layer, Part 1. A single buffer is now
        // sufficient - ChatEngine's dispatch worker drains
        // OutboundDispatchQueue with exactly one background task, so at
        // most one HandleChatDispatchAsync invocation (and therefore one
        // buffer copy-then-send sequence) is ever in flight at a time,
        // regardless of which channel (global/guild/whisper) an item came
        // from. Previously two buffers were required because the global
        // and guild Redis subscriptions could invoke their handlers
        // concurrently with each other; that is no longer true once
        // delivery is centralized behind one dispatch worker.
        private readonly byte[] _chatDispatchBuffer = new byte[Marshal.SizeOf<ResponseChatMessagePacket>()];

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
        }

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
        }

        // Modul: Loot Event Feed. Drains PlayerSessionRegistry.OutboundLootDropQueue
        // and pushes each drop to the socket of the player it belongs to.
        //
        // Its own background loop rather than a hook on the 10Hz tick,
        // because drops are produced by CombatLootEngine's own 3-second cron
        // (never on the tick thread) and a socket write must not be able to
        // stall the simulation. Mirrors ChatEngine's dispatch worker shape
        // exactly, including the 50ms idle sleep - loot is bursty and rare,
        // so a tight spin would burn a core to deliver a handful of messages
        // a minute.
        //
        // Allocation-free per drop: one reusable buffer, one blittable
        // write, no strings anywhere on the path (the packet carries a
        // numeric ContentRegistry item id which the client resolves through
        // its own content mirror).
        private readonly byte[] _lootDropDispatchBuffer = new byte[Marshal.SizeOf<ResponseLootDropPacket>()];

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

                if (!_connectedClients.TryGetValue(drop.PlayerId, out var session) || session.Socket.State != WebSocketState.Open)
                {
                    // The player logged off between the drop resolving and
                    // this dispatch. The item is already persisted, so
                    // dropping the notification loses nothing but the
                    // on-screen line.
                    continue;
                }

                try
                {
                    if (session.UseJsonProtocol)
                    {
                        byte[] json = PacketJsonCodec.SerializeToUtf8(ref drop);
                        await session.SendAsync(new ArraySegment<byte>(json), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    else
                    {
                        MemoryMarshal.Write(_lootDropDispatchBuffer, in drop);
                        await session.SendAsync(new ArraySegment<byte>(_lootDropDispatchBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Loot drop dispatch failed for player {drop.PlayerId}: {ex.Message}");
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
        // comment on OutboundDispatchQueue for why. ChatEngine guarantees
        // only one dispatch item is ever being processed at a time, so
        // awaiting every SendAsync here in turn (and reusing one shared
        // buffer) is both safe and required, matching the exact same
        // constraint the old two-buffer/two-handler split existed to
        // satisfy.
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

            CopyChatPacketToDispatchBuffer(item.Packet);
            var segment = new ArraySegment<byte>(_chatDispatchBuffer);

            // Modul: JSON WebSocket mode, 2026-08-02. Encoded at most once
            // per dispatched message no matter how many JSON recipients it
            // has, and not at all when every recipient is on the binary
            // protocol - which is the state of the world until a web client
            // actually connects.
            ResponseChatMessagePacket chatPacket = item.Packet;
            byte[]? chatJson = null;

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
                        if (targetSession.UseJsonProtocol)
                        {
                            chatJson ??= PacketJsonCodec.SerializeToUtf8(ref chatPacket);
                            await targetSession.SendAsync(new ArraySegment<byte>(chatJson), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                        else
                        {
                            await targetSession.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Whisper send failed for player {item.TargetPlayerId}: {ex.Message}");
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
                        if (kvp.Value.UseJsonProtocol)
                        {
                            chatJson ??= PacketJsonCodec.SerializeToUtf8(ref chatPacket);
                            await kvp.Value.SendAsync(new ArraySegment<byte>(chatJson), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                        else
                        {
                            await kvp.Value.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Chat dispatch send failed for player {kvp.Key}: {ex.Message}");
                    }
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

        private void CopyChatPacketToDispatchBuffer(ResponseChatMessagePacket packet)
        {
            ReadOnlySpan<ResponseChatMessagePacket> span = MemoryMarshal.CreateReadOnlySpan(ref packet, 1);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(span);
            bytes.CopyTo(_chatDispatchBuffer);
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

        private async Task ListenLoopAsync()
        {
            while (_isRunning)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();
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
                        continue;
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
                        continue;
                    }

                    if (requestPath == "/health/readiness")
                    {
                        InfrastructureHealthMonitor.WritePlainHealth(context.Response, InfrastructureHealthMonitor.IsReady());
                        continue;
                    }

                    if (requestPath == "/healthz")
                    {
                        context.Response.StatusCode = 200;
                        context.Response.Close();
                        continue;
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
                        continue;
                    }

                    if (GlobalEngineState.IsShuttingDown || !GlobalEngineState.IsColdBootRecoveryComplete)
                    {
                        context.Response.StatusCode = 503;
                        context.Response.Close();
                        continue;
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
                        continue;
                    }

                    if (requestPath == "/gamedata" && context.Request.HttpMethod == "GET")
                    {
                        await HandleGameDataManifest(context);
                        continue;
                    }

                    // Modul: web client port, Phase 7. The same ten sound
                    // effects the Unity client loads from Resources/Audio,
                    // linked into this project's output by the csproj rather
                    // than copied - see that link's own comment. Unauthenticated
                    // for the same reason the content files are: they ship
                    // inside the Unity app bundle already.
                    if (requestPath.StartsWith("/audio/", StringComparison.Ordinal) && context.Request.HttpMethod == "GET")
                    {
                        await HandleAudioFile(context, requestPath.Substring("/audio/".Length));
                        continue;
                    }

                    if (requestPath == "/audio" && context.Request.HttpMethod == "GET")
                    {
                        await HandleAudioManifest(context);
                        continue;
                    }

                    if (requestPath.StartsWith("/sprites/", StringComparison.Ordinal) && context.Request.HttpMethod == "GET")
                    {
                        await HandleSpriteFile(context, requestPath.Substring("/sprites/".Length));
                        continue;
                    }

                    if (requestPath == "/sprites" && context.Request.HttpMethod == "GET")
                    {
                        await HandleSpriteManifest(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/assets/handshake" && context.Request.HttpMethod == "POST")
                    {
                        string expectedHash = Environment.GetEnvironmentVariable("ExpectedCatalogHash") ?? string.Empty;
                        string clientHash = string.Empty;

                        if (context.Request.HasEntityBody)
                        {
                            using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                            string payload = await reader.ReadToEndAsync();
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
                            continue;
                        }

                        context.Response.StatusCode = 200;
                        context.Response.Close();
                        continue;
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
                        || requestPath == "/api/v1/auth/check-email"
                        || requestPath == "/api/v1/auth/oauth-link")
                    {
                        if (!AuthThrottle.TryConsume(AuthThrottle.ResolveClientAddress(context.Request)))
                        {
                            context.Response.StatusCode = 429;
                            context.Response.Headers["Retry-After"] = "60";
                            context.Response.Close();
                            continue;
                        }
                    }

                    if (requestPath == "/api/v1/auth/login" && context.Request.HttpMethod == "POST")
                    {
                        // Modul: dispatched fire-and-forget, not awaited
                        // inline, matching the WebSocket branch's own
                        // _ = HandleClientLoopAsync(...) pattern below. This
                        // loop otherwise processes one HttpListener context
                        // at a time end to end - under concurrent load,
                        // awaiting a provisioning transaction here (which
                        // may now retry with backoff under Serializable
                        // contention, see LoginOrProvisionAsync) would
                        // serialize every other connection's login behind
                        // it, compounding retry latency across all of them
                        // instead of letting them resolve in parallel.
                        // HandleAuthLogin already wraps its entire body in
                        // its own try/catch and always closes the response,
                        // so dispatching it this way does not drop error
                        // visibility.
                        _ = HandleAuthLogin(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/auth/oauth-link" && context.Request.HttpMethod == "POST")
                    {
                        _ = HandleOAuthLink(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/auth/check-email" && context.Request.HttpMethod == "POST")
                    {
                        _ = HandleCheckEmail(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/auth/register" && context.Request.HttpMethod == "POST")
                    {
                        _ = HandleAuthRegister(context);
                        continue;
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
                            continue;
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
                        continue;
                    }

                    if (requestPath == "/api/v1/billing/verify-receipt" && context.Request.HttpMethod == "POST")
                    {
                        // Modul: dispatched fire-and-forget, matching the
                        // auth-login branch above - both handlers wrap
                        // their entire body in a try/catch and always close
                        // the response, and both may now retry a
                        // Serializable conflict (see BillingVerificationEngine),
                        // so awaiting inline here would serialize every
                        // other connection behind a slow purchase
                        // verification the same way it would for login.
                        _ = HandleVerifyReceipt(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/billing/verify" && context.Request.HttpMethod == "POST")
                    {
                        _ = HandleBillingVerify(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/billing/refund-webhook" && context.Request.HttpMethod == "POST")
                    {
                        _ = HandleRefundWebhook(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/storefront/listings" && context.Request.HttpMethod == "GET")
                    {
                        await HandleStorefrontListings(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/chest/sell" && context.Request.HttpMethod == "POST")
                    {
                        await HandleChestAction(context, sell: true);
                        continue;
                    }

                    if (requestPath == "/api/v1/chest/discard" && context.Request.HttpMethod == "POST")
                    {
                        await HandleChestAction(context, sell: false);
                        continue;
                    }

                    if (requestPath == "/api/v1/guild/shard-match" && context.Request.HttpMethod == "GET")
                    {
                        await HandleGuildShardMatch(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/guild/logistics/snapshot" && context.Request.HttpMethod == "GET")
                    {
                        await HandleGuildLogisticsSnapshot(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/forge/inventory" && context.Request.HttpMethod == "GET")
                    {
                        await HandleForgeInventorySnapshot(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/codex/snapshot" && context.Request.HttpMethod == "GET")
                    {
                        await HandleCodexSnapshot(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/codex/regions" && context.Request.HttpMethod == "GET")
                    {
                        await HandleCodexRegionsSnapshot(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/breeding/roster" && context.Request.HttpMethod == "GET")
                    {
                        await HandleBreedingRosterSnapshot(context);
                        continue;
                    }

                    // Modul: the Book of Deeds. Five chapters, their live
                    // counters, and the Seals - which are BANKED on this read,
                    // because a Seal grants permanent skill points and a client
                    // that decided when it had earned one could award itself
                    // the whole tree.
                    if (requestPath == "/api/v1/deeds/snapshot" && context.Request.HttpMethod == "GET")
                    {
                        await HandleDeedsSnapshot(context);
                        continue;
                    }

                    // Modul: the Hall of Ancestors. The breeding roster answers
                    // "who can I pair"; this answers "who carries into next
                    // season, and where do they stand" - the cap, the marks,
                    // the pedigree and which of the three playable slots each
                    // member occupies.
                    if (requestPath == "/api/v1/ancestors/hall" && context.Request.HttpMethod == "GET")
                    {
                        await HandleAncestorsHall(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/breeding/preview" && context.Request.HttpMethod == "GET")
                    {
                        await HandleBreedingPreview(context);
                        continue;
                    }

                    // Modul: the same question asked of THE standard pair - a
                    // hero and somebody from the village. Separate because the
                    // partner is a village_newcomers row, not a character.
                    if (requestPath == "/api/v1/breeding/village-preview" && context.Request.HttpMethod == "GET")
                    {
                        await HandleVillagerBreedingPreview(context);
                        continue;
                    }

                    // Modul: the village gene pool. The roster above is the
                    // player's OWN characters; this is the outside blood they
                    // can marry into the line, which is a different list
                    // answering a different question.
                    if (requestPath == "/api/v1/village/newcomers" && context.Request.HttpMethod == "GET")
                    {
                        await HandleVillageNewcomers(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/mastery/snapshot" && context.Request.HttpMethod == "GET")
                    {
                        await HandleMasterySnapshot(context);
                        continue;
                    }

                    // Modul: UI audit follow-up. Friends roster - AddFriend/
                    // RemoveFriend/BlockPlayer/UnblockPlayer (RelationshipEngine)
                    // already existed and worked over the WebSocket wire, but
                    // there was no way for the client to list the current
                    // relationship set or discover a target player's numeric
                    // Id from their username. Mirrors HandleMasterySnapshot's
                    // exact authenticated-GET shape.
                    if (requestPath == "/api/v1/friends/list" && context.Request.HttpMethod == "GET")
                    {
                        await HandleFriendsList(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/players/resolve" && context.Request.HttpMethod == "GET")
                    {
                        await HandlePlayerResolve(context);
                        continue;
                    }

                    // Modul: UI rework. Reverse lookup of the above -
                    // "?ids=1,2,3" to usernames, so chat/whisper rows can
                    // show a name instead of the raw SenderPlayerId the
                    // wire protocol carries. Batched deliberately; see
                    // PlayerNameEntryResponse's own comment.
                    if (requestPath == "/api/v1/players/names" && context.Request.HttpMethod == "GET")
                    {
                        await HandlePlayerNames(context);
                        continue;
                    }

                    // Modul: Inventory screen. The only inventory-shaped
                    // endpoint that existed was HandleForgeInventorySnapshot,
                    // which is scoped to what the Forge needs (equipment
                    // instances plus the handful of materials the Forge's own
                    // recipes consume). Nothing anywhere exposed the village
                    // stash, the full commodity list, or which items are
                    // currently equipped, so no inventory screen was possible.
                    if (requestPath == "/api/v1/player/inventory" && context.Request.HttpMethod == "GET")
                    {
                        await HandlePlayerInventorySnapshot(context);
                        continue;
                    }

                    // Modul: Crafting Tree screen. ContentRegistry's 103
                    // recipes have been fully functional server-side for a
                    // long time but had no endpoint of any kind - the client
                    // could not even enumerate them, let alone show costs.
                    if (requestPath == "/api/v1/crafting/recipes" && context.Request.HttpMethod == "GET")
                    {
                        await HandleCraftingRecipeSnapshot(context);
                        continue;
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
                        continue;
                    }

                    // Modul: UI audit follow-up. No player-statistics engine
                    // existed anywhere server-side. Rather than invent new
                    // tracking, this aggregates fields that are already
                    // persisted for other systems (level/xp/diamonds on
                    // PlayerRecord, gold via CommodityRecords, claimed
                    // achievements, region completions, character count,
                    // guild membership) into one read-only snapshot.
                    if (requestPath == "/api/v1/player/statistics" && context.Request.HttpMethod == "GET")
                    {
                        await HandlePlayerStatistics(context);
                        continue;
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
                        continue;
                    }

                    if (requestPath == "/api/v1/guilds/list" && context.Request.HttpMethod == "GET")
                    {
                        await HandleGuildList(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/guilds/create" && context.Request.HttpMethod == "POST")
                    {
                        await HandleGuildCreate(context);
                        continue;
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
                        continue;
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
                        continue;
                    }

                    if (requestPath == "/api/v1/guild/applications/approve" && context.Request.HttpMethod == "POST")
                    {
                        await HandleGuildApplicationApprove(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/guild/applications/reject" && context.Request.HttpMethod == "POST")
                    {
                        await HandleGuildApplicationReject(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/achievements/snapshot" && context.Request.HttpMethod == "GET")
                    {
                        await HandleAchievementsSnapshot(context);
                        continue;
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
                        continue;
                    }

                    if (requestPath == "/api/v1/achievements/state" && context.Request.HttpMethod == "GET")
                    {
                        await HandleAchievementsState(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/mailbox/list" && context.Request.HttpMethod == "GET")
                    {
                        await HandleMailboxListSnapshot(context);
                        continue;
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
                        continue;
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
                        continue;
                    }

                    if (requestPath == "/api/v1/leaderboard/global" && context.Request.HttpMethod == "GET")
                    {
                        await HandleGlobalLeaderboard(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/leaderboard/guilds" && context.Request.HttpMethod == "GET")
                    {
                        await HandleGuildLeaderboard(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/market/listings" && context.Request.HttpMethod == "GET")
                    {
                        await HandleMarketBrowserListings(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/market/history" && context.Request.HttpMethod == "GET")
                    {
                        await HandleMarketPriceHistory(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/support/tickets/create" && context.Request.HttpMethod == "POST")
                    {
                        await HandleSupportTicket(context);
                        continue;
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
                catch (HttpListenerException)
                {
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Network error: {ex.Message}");
                }
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
        }

        private sealed class PlayerInventorySnapshotResponse
        {
            public int BackpackSlotsUsed { get; set; }
            public long MaxStackQuantity { get; set; }
            public System.Collections.Generic.List<InventoryEquipmentResponse> Equipment { get; set; } = new();
            public System.Collections.Generic.List<InventoryStackResponse> Stacks { get; set; } = new();
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

            // Modul: villager roster. Who actually lives in the village.
            // Carried on the statistics snapshot rather than as its own route
            // because it is small, read-only, and read at exactly the same
            // moment - and because CommandType.EvictVillager was implemented
            // and validated server-side with no way for a client to know WHICH
            // slots are occupied, so the command was unreachable in practice.
            public List<VillagerSlotResponse> Villagers { get; set; } = new List<VillagerSlotResponse>();

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

        // Modul: villager roster. One occupied village slot.
        private sealed class VillagerSlotResponse
        {
            public int SlotIndex { get; set; }
            public bool IsActive { get; set; }
            public double EfficiencyModifier { get; set; }
        }

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
                                KillsOfHardest = progressByPlayer.TryGetValue(p.Id, out var killed) ? killed.Kills : 0
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

                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var payload = JsonSerializer.Deserialize<JsonElement>(await reader.ReadToEndAsync());

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

                await transaction.CommitAsync();

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
            public bool HasEquipmentAttachment { get; set; }
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
                        HasEquipmentAttachment = m.AttachedEquipmentId.HasValue,
                        ReceivedTimestamp = m.ReceivedTimestamp
                    })
                    .ToListAsync();

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

        private sealed class RegionProgressResponse
        {
            public int RegionId { get; set; }
            public int CurrentKills { get; set; }
            public int RequiredKills { get; set; }
            public bool IsCompleted { get; set; }
            public int LootLuckBonusPct { get; set; }
        }

        // Modul 13.4.3: region-completion progress for the Codex regions UI. A
        // region is 6 distinct monster ids (5 standard/elite + 1 regional
        // boss, see CodexEngine's ((MonsterId - 1) % 30) / 6 + 1 grouping) and
        // completes only once every monster in it individually reaches 1000
        // kills - so CurrentKills here is the MINIMUM kill count across the
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

                var response = new System.Collections.Generic.List<RegionProgressResponse>(10);
                for (int region = 1; region <= 10; region++)
                {
                    int minKillsInRegion = -1;
                    bool regionExists = false;

                    for (int monsterIndex = 0; monsterIndex < ContentRegistry.Monsters.Length; monsterIndex++)
                    {
                        int monsterId = ContentRegistry.Monsters[monsterIndex].Id;
                        if (ContentRegistry.GetMonsterRegionTier(monsterId) != region)
                        {
                            continue;
                        }

                        regionExists = true;
                        killsByMonsterId.TryGetValue(monsterId, out int killCount);
                        if (killCount > 1000) killCount = 1000;
                        if (minKillsInRegion < 0 || killCount < minKillsInRegion)
                        {
                            minKillsInRegion = killCount;
                        }
                    }

                    if (!regionExists)
                    {
                        continue;
                    }

                    bool isCompleted = completedRegionSet.Contains(region);
                    response.Add(new RegionProgressResponse
                    {
                        RegionId = region,
                        CurrentKills = minKillsInRegion < 0 ? 0 : minKillsInRegion,
                        RequiredKills = 1000,
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

        private sealed class BreedingRosterEntryResponse
        {
            public string CharacterId { get; set; } = string.Empty;
            public int Level { get; set; }
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
            public int LocusSpeedDominant { get; set; }
            public int LocusSpeedRecessive { get; set; }
            public int LocusCritDominant { get; set; }
            public int LocusCritRecessive { get; set; }
            public int LocusYieldDominant { get; set; }
            public int LocusYieldRecessive { get; set; }
        }

        // Modul 13.4.3: the player's own bred/breedable character roster, for
        // the Breeding Lab's parent-selection slots. BreedingEngine.
        // ExecuteBreedingAsync's own eligibility rules (AgePhase >= 1,
        // Level >= 50, not already IsBreedingActive, not IsLockedInEscrow) are
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
                        innLevel, rows.Count, heldGold, recruitments) ?? string.Empty,
                    Newcomers = rows.ConvertAll(v => new
                    {
                        v.Id,
                        v.RaceId,
                        v.IsFemale,
                        v.AptitudeStrength,
                        v.AptitudeSkill,
                        v.AptitudeEndurance,
                        v.AptitudeFortune,
                        v.ArrivedAtEpoch,
                        v.IsElder,
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

                var chapters = Engine.DeedRegistry.Chapters;
                bool previousComplete = true;

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
                    Chapters = chapters.Select(chapter =>
                    {
                        bool isOpen = previousComplete;
                        bool isComplete = Engine.DeedRegistry.IsComplete(chapter, progress);

                        // A CHAPTER STAYS OPEN ONCE ITS PREDECESSOR IS SEALED,
                        // even if that predecessor's deeds later read as
                        // undone. Several are STATE rather than history - "wear
                        // a weapon" and "fill the larder" both go false the
                        // moment a player changes their mind - so keying the
                        // next chapter on live completeness would slam it shut
                        // behind somebody who swapped a sword. The Seal is the
                        // record that the chapter happened, and it is
                        // permanent; this is what it records.
                        previousComplete = isComplete
                            || Engine.DeedRegistry.HasSeal(player.SealsEarnedMask, chapter.Index);

                        return new
                        {
                            chapter.Index,
                            chapter.Title,
                            chapter.Reward,
                            IsOpen = isOpen,
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

                int cap = Engine.HallOfAncestorsRules.CapFor(player.AncestorSlotsPurchased);

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
                    MaxCap = Engine.HallOfAncestorsRules.MaxSlots,
                    SlotsPurchased = player.AncestorSlotsPurchased,
                    NextSlotCostDiamonds = Engine.HallOfAncestorsRules.NextSlotCostDiamonds(player.AncestorSlotsPurchased),
                    Diamonds = player.PremiumDiamonds,
                    PlayableSlots = Domain.Combat.CharacterSlotEngine.GetUnlockedSlotCount(townHallLevel),
                    Members = characters.ConvertAll(c =>
                    {
                        lineageById.TryGetValue(c.Id, out var lineage);
                        var genes = new GeneticVector(lineage?.GeneticVector ?? 0L);

                        return new
                        {
                            CharacterId = c.Id.ToString(),
                            c.Level,
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
                        Level = character.Level,
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
                        LocusSpeedDominant = geneVec.LocusSpeed.Dominant,
                        LocusSpeedRecessive = geneVec.LocusSpeed.Recessive,
                        LocusCritDominant = geneVec.LocusCrit.Dominant,
                        LocusCritRecessive = geneVec.LocusCrit.Recessive,
                        LocusYieldDominant = geneVec.LocusYield.Dominant,
                        LocusYieldRecessive = geneVec.LocusYield.Recessive
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

        private sealed class BreedingPreviewResponse
        {
            public bool IsEligible { get; set; }
            public string IneligibleReason { get; set; } = string.Empty;
            public bool IsInbredRisk { get; set; }
            public long BreedingCostGold { get; set; }
            public bool HasSufficientGold { get; set; }
            public System.Collections.Generic.List<GenePreviewLocusResponse> Loci { get; set; } = new();
            public System.Collections.Generic.List<AptitudePreviewResponse> Aptitudes { get; set; } = new();
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
                bool pOnCooldown = pChar.IsBreedingActive && pChar.BreedingCooldownEndEpoch > nowEpoch;
                bool mOnCooldown = mChar.IsBreedingActive && mChar.BreedingCooldownEndEpoch > nowEpoch;

                var pVec = new GeneticVector(pLineage.GeneticVector);
                var mVec = new GeneticVector(mLineage.GeneticVector);

                if (pChar.AgePhase < 1 || mChar.AgePhase < 1 || pChar.Level < 50 || mChar.Level < 50)
                {
                    response.IneligibleReason = "parent_not_mature";
                }
                else if (pChar.IsLockedInEscrow || mChar.IsLockedInEscrow)
                {
                    response.IneligibleReason = "parent_locked_in_escrow";
                }
                else if (pOnCooldown || mOnCooldown)
                {
                    response.IneligibleReason = "parent_on_cooldown";
                }
                else if (pVec.LocusRace.Dominant != mVec.LocusRace.Dominant)
                {
                    response.IneligibleReason = "race_mismatch";
                }
                else
                {
                    response.IsEligible = true;
                }

                response.IsInbredRisk = paternalId == mLineage.ParentPaternalId || paternalId == mLineage.ParentMaternalId
                    || maternalId == pLineage.ParentPaternalId || maternalId == pLineage.ParentMaternalId
                    || (pLineage.ParentPaternalId.HasValue && (pLineage.ParentPaternalId == mLineage.ParentPaternalId || pLineage.ParentPaternalId == mLineage.ParentMaternalId))
                    || (pLineage.ParentMaternalId.HasValue && (pLineage.ParentMaternalId == mLineage.ParentPaternalId || pLineage.ParentMaternalId == mLineage.ParentMaternalId));

                int maxGen = Math.Max(pLineage.GenerationIndex, mLineage.GenerationIndex);
                response.BreedingCostGold = Engine.BreedingEngine.CostFor(maxGen);

                var goldRecord = await db.CommodityRecords
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
                response.HasSufficientGold = goldRecord != null && goldRecord.Quantity >= response.BreedingCostGold;

                AddLocusPreview(response.Loci, "Race", pVec.LocusRace, mVec.LocusRace, maxGen);
                AddLocusPreview(response.Loci, "Speed", pVec.LocusSpeed, mVec.LocusSpeed, maxGen);
                AddLocusPreview(response.Loci, "Crit", pVec.LocusCrit, mVec.LocusCrit, maxGen);
                AddLocusPreview(response.Loci, "Yield", pVec.LocusYield, mVec.LocusYield, maxGen);
                AddAptitudePreviews(response.Aptitudes, pLineage.AptitudeVector(), mLineage.AptitudeVector());

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
                bool heroOnCooldown = hero.IsBreedingActive && hero.BreedingCooldownEndEpoch > nowEpoch;

                var heroVec = new GeneticVector(heroLineage.GeneticVector);
                var villagerVec = new GeneticVector(newcomer.Genome());

                // Only the hero needs the level gate - see the engine. The
                // villager only has to exist and not already be an elder.
                if (hero.AgePhase < 1 || hero.Level < 50)
                {
                    response.IneligibleReason = "hero_not_mature";
                }
                else if (hero.IsLockedInEscrow)
                {
                    response.IneligibleReason = "parent_locked_in_escrow";
                }
                else if (heroOnCooldown)
                {
                    response.IneligibleReason = "parent_on_cooldown";
                }
                else if (newcomer.IsElder)
                {
                    response.IneligibleReason = "villager_already_married";
                }
                else if (hero.IsFemale == newcomer.IsFemale)
                {
                    response.IneligibleReason = "same_sex";
                }
                else if (heroVec.LocusRace.Dominant != newcomer.RaceId)
                {
                    response.IneligibleReason = "race_mismatch";
                }
                else
                {
                    response.IsEligible = true;
                }

                // Never. A villager has no parents here and marries once.
                response.IsInbredRisk = false;

                int maxGen = heroLineage.GenerationIndex;
                response.BreedingCostGold = Engine.BreedingEngine.CostFor(maxGen);

                var goldRecord = await db.CommodityRecords
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
                response.HasSufficientGold = goldRecord != null && goldRecord.Quantity >= response.BreedingCostGold;

                AddLocusPreview(response.Loci, "Race", heroVec.LocusRace, villagerVec.LocusRace, maxGen);
                AddLocusPreview(response.Loci, "Speed", heroVec.LocusSpeed, villagerVec.LocusSpeed, maxGen);
                AddLocusPreview(response.Loci, "Crit", heroVec.LocusCrit, villagerVec.LocusCrit, maxGen);
                AddLocusPreview(response.Loci, "Yield", heroVec.LocusYield, villagerVec.LocusYield, maxGen);
                AddAptitudePreviews(response.Aptitudes, heroLineage.AptitudeVector(), newcomer.AptitudeVector());

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
        // a player would actually type, their username, to that Id. Exact,
        // case-sensitive match - Username's uniqueness index (unlike Email's)
        // is case-sensitive, so a case-insensitive lookup here could resolve
        // ambiguously against two differently-cased usernames.
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
                    .Where(p => p.Username == username)
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
                foreach (var rosterCharacter in await db.CharacterRecords.AsNoTracking().Where(c => c.PlayerId == playerId).ToListAsync())
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
                }

                var response = new PlayerInventorySnapshotResponse
                {
                    BackpackSlotsUsed = equipment.Count,
                    // Modul: unlimited village chest. Was
                    // VillageStashInstance.MaxStackQuantity (9999), which the
                    // Inventory screen rendered as "stacks cap at 9999". There
                    // is no cap any more, so 0 is the agreed "unbounded"
                    // sentinel and the client suppresses the line entirely.
                    MaxStackQuantity = 0L
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

                // Modul: THE ROSTER READ A TABLE NOTHING WRITES.
                //
                // This listed VillageResidents, and VillageResidents has no
                // INSERT anywhere in the codebase - two other comments in this
                // repository already say so, in StateCheckpointManager and
                // AchievementEngine. So the Village screen said "No villagers
                // yet" forever while the Character screen said 2/10, and both
                // were reporting honestly from different tables.
                //
                // The count moved to CharacterRecords when someone decided the
                // people who live in your village ARE your characters. The list
                // did not move with it. It does now, so one question has one
                // answer.
                //
                // SlotIndex carries through because that is what the roster
                // shows and what an eviction would have to name.
                var villagerRows = await db.CharacterRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId && !c.IsLockedInEscrow)
                    .OrderBy(c => c.SlotIndex)
                    .Select(c => new VillagerSlotResponse
                    {
                        SlotIndex = c.SlotIndex,
                        IsActive = c.ActiveActivityId > 0,
                        EfficiencyModifier = 1.0
                    })
                    .ToListAsync();

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
                    TotalPlayTimeSeconds = player.TotalPlayTimeSeconds,
                    Villagers = villagerRows
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

                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
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
                        _ => "That name will not do - one to a hundred characters.",
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

                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
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

                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
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

                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
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
                        CurrentProgress = entry.CurrentProgress,
                        CompletedTier = entry.CompletedTier,
                        NextTierTarget = AchievementMilestones.GetNextTierTarget(entry.AchievementId, entry.CompletedTier),
                        NextTierReward = AchievementMilestones.GetNextTierReward(entry.AchievementId, entry.CompletedTier),
                        IsClaimed = entry.IsClaimed
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

        private async Task<long> TryResolveAuthenticatedPlayerAsync(HttpListenerRequest request)
        {
            const string bearerPrefix = "Bearer ";
            string bearerHeader = request.Headers["Authorization"] ?? string.Empty;
            if (bearerHeader.Length <= bearerPrefix.Length || !bearerHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return 0L;
            }

            string token = bearerHeader.Substring(bearerPrefix.Length);
            JwtValidationResult result = AuthenticationEngine.ValidateJwt(token, _jwtSecretKey);
            if (!result.IsValid)
            {
                return 0L;
            }

            return await ResolvePlayerIdFromAccountIdAsync(result.AccountId);
        }

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

        // Modul: legacy webhook-style verification path - identifies the
        // player by AccountId (not a session Bearer token, matching a
        // platform-webhook caller rather than the game client itself).
        // Previously inserted a PrimaryPurchaseLedger row with PlayerId = 0
        // and returned 200 even when the account could not be resolved (the
        // purchase was silently lost with the TransactionId marked
        // processed, unrecoverable), and credited a hardcoded 100 diamonds
        // regardless of ProductId/CostCents. Both fixed here by delegating
        // to BillingVerificationEngine.VerifyPurchaseAsync, which resolves
        // the actual reward from ProductId server-side and never writes a
        // ledger row for an unresolved account.
        private async Task HandleVerifyReceipt(HttpListenerContext context)
        {
            try
            {
                if (_billingVerificationEngine == null)
                {
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    return;
                }

                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                var accountId = payload.GetProperty("AccountId").GetGuid();
                var transactionId = payload.GetProperty("TransactionId").GetString() ?? string.Empty;
                var productId = payload.GetProperty("ProductId").GetString() ?? string.Empty;

                long playerId = await ResolvePlayerIdFromAccountIdAsync(accountId);
                if (playerId == 0L)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                bool success = await _billingVerificationEngine.VerifyPurchaseAsync(playerId, transactionId, productId);
                context.Response.StatusCode = success ? 200 : 409;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Verify receipt error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }

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
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
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

                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

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
                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
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
                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                var traceLog = payload.GetProperty("TraceLog").GetString();
                
                // Server-side scrubbing logic is not requested here, the client runs the regex on its side.
                // Or maybe we should scrub here too? The task says "collection boundary", meaning before sending, 
                // but we also have to execute sanitization exclusively upon explicit ticket dispatch. So it runs on the client.

                Console.WriteLine("Received Support Ticket with Trace Log.");
                
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
            // masking runs in place over the packet's fixed byte buffer
            // BEFORE ExtractChatMessageText materializes any managed string -
            // the one zero-allocation insertion point on this path, and the
            // single choke point every channel shares.
            ApplyProfanityFilterInPlace(ref chatRequest);

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
        }

        private sealed class CheckEmailResponse
        {
            public bool Available { get; set; }
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
                if (_simulationEngine != null)
                {
                    EngineMetricsPayload metrics = _simulationEngine.GetMetrics();
                    tickCount = metrics.TotalTicksProcessed;
                    tickSumMs = metrics.TickDurationSumMs;
                    bucket10 = metrics.TickDurationBucketCount10Ms;
                    bucket25 = metrics.TickDurationBucketCount25Ms;
                    bucket50 = metrics.TickDurationBucketCount50Ms;
                    bucket100 = metrics.TickDurationBucketCount100Ms;
                    bucket250 = metrics.TickDurationBucketCount250Ms;
                    bucketInf = metrics.TickDurationBucketCountInf;
                }

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
                body.Append("# HELP folkidle_database_write_queue_length Players with state pending Redis write-behind flush.\n");
                body.Append("# TYPE folkidle_database_write_queue_length gauge\n");
                body.Append("folkidle_database_write_queue_length ").Append(writeQueueLength).Append('\n');

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

        // Modul: sole controlled entry point for account identity issuance.
        // DeviceId is a client-persisted GUID (see UiLoginWindow on the
        // client) - looked up or auto-provisioned via AuthenticationEngine.
        // LoginOrProvisionAsync, then a fresh SessionNonce is minted and
        // signed into a JWT. That SessionNonce round-trips through the
        // WebSocket AuthHandshakePacket at connect time and is what the
        // Redis eviction check in HandleClientLoopAsync uses to detect and
        // kick a stale prior session for the same account.
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

                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                string body = await reader.ReadToEndAsync();

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
                }
                else if (!string.IsNullOrWhiteSpace(deviceId) && deviceId.Length <= 128)
                {
                    (_, accountId) = await AuthenticationEngine.LoginOrProvisionAsync(authOptions, deviceId);
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
                string token = AuthenticationEngine.GenerateJwt(accountId, sessionNonce, _jwtSecretKey, out long expiresAtEpoch);

                var response = new AuthLoginResponse { Token = token, ExpiresAtEpoch = expiresAtEpoch };

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

        // Modul: Email/Password Auth. Availability check the register
        // screen calls before it reveals the username/password fields -
        // see AuthenticationEngine.IsEmailAvailableAsync for why an
        // invalid-format email also reports unavailable rather than a
        // separate error (this endpoint only ever answers yes/no, never
        // distinguishes the reason).
        private async Task HandleCheckEmail(HttpListenerContext context)
        {
            try
            {
                if (!context.Request.HasEntityBody)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                string body = await reader.ReadToEndAsync();

                string email = string.Empty;
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
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                if (string.IsNullOrWhiteSpace(email))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                var authOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
                bool available = await AuthenticationEngine.IsEmailAvailableAsync(authOptions, email);

                var response = new CheckEmailResponse { Available = available };
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Check-email error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: Email/Password Auth. Creates a new account (see
        // AuthenticationEngine.RegisterWithEmailAsync) and, on success,
        // immediately issues a JWT exactly like HandleAuthLogin does - a
        // successful registration logs the player straight into the game,
        // it does not require a separate follow-up login call.
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

                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                string body = await reader.ReadToEndAsync();

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
                string token = AuthenticationEngine.GenerateJwt(result.AccountId, sessionNonce, _jwtSecretKey, out long expiresAtEpoch);

                var response = new AuthLoginResponse { Token = token, ExpiresAtEpoch = expiresAtEpoch };
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
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId == 0L)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                Guid accountId = await ResolveAccountIdAsync(playerId);

                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                string body = await reader.ReadToEndAsync();

                string oauthProviderToken;
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
                }
                catch (System.Text.Json.JsonException)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
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

                    _connectedClients[playerId] = new WebSocketSession(socket, redisLockToken ?? string.Empty, useJsonProtocol);
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

            // Modul: JSON WebSocket mode, 2026-08-02. The binary path below is
            // byte for byte what it always was, including its reusable
            // per-session buffer - this is the single hottest path in the
            // codebase (once per online player per 10Hz tick) and the JSON
            // branch must not cost the Unity client anything but one already-
            // loaded bool test.
            // Modul: a wedged socket is evicted, not written to forever.
            //
            // SendAsync sets IsWedged when a frame times out, which means the
            // peer has stopped reading altogether. Left alone that connection
            // stays open and silent - the state the freeze report describes,
            // where the server simulates correctly and the screen does not
            // move. Dropping it gives the client something to react to, and its
            // reconnect logic (500 ms backing off to 15 s, token re-sent) then
            // does the rest.
            if (session.IsWedged)
            {
                Console.WriteLine($"Evicting wedged socket for player {playerId}: sends stopped completing.");
                ForceDisconnect(playerId);
                return;
            }

            // Fire-and-forget is intentional here - SendToPlayer is called
            // once per player per broadcast tick and must not block the
            // caller - but the fault is still observed and logged rather
            // than silently dropped, matching this task's error-
            // observability requirement. ContinueWith (not await/async) so
            // this allocates zero Task/state-machine objects on the
            // per-tick, per-player hot path - see _logSendFault's own doc
            // comment.
            //
            // Both branches drop the frame rather than queue it when the socket
            // is already busy - see WebSocketSession.SendAsync.
            Task send;
            if (session.UseJsonProtocol)
            {
                var segment = new ArraySegment<byte>(PacketJsonCodec.SerializeToUtf8(ref packet));
                send = session.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            else
            {
                // Takes the lock before touching the reusable buffer - see
                // SendStateFrameAsync for why the old order could splice two
                // frames together.
                send = session.SendStateFrameAsync(ref packet);
            }

            send.ContinueWith(_logSendFault, playerId, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
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

        public void ForceDisconnect(long playerId)
        {
            if (_connectedClients.TryRemove(playerId, out var session))
            {
                if (_redisSessionLock != null && !string.IsNullOrEmpty(session.RedisLockToken))
                {
                    _ = _redisSessionLock.ReleaseAsync(playerId, session.RedisLockToken);
                }

                session.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Violent termination", CancellationToken.None)
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
    }
}
