using System;
using System.Collections.Concurrent;
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

        public WebSocketSession(WebSocket socket, string redisLockToken)
        {
            Socket = socket;
            RedisLockToken = redisLockToken;
            Throttler = new ClientInputThrottler();
            TokenBucket = NetworkThrottlingEngine.CreateBucket();
            ChatTokenBucket = ChatEngine.CreateChatBucket();
            DiagnosticSendBuffer = new byte[Marshal.SizeOf<StateUpdatePacket>()];
        }

        public async Task SendAsync(ArraySegment<byte> segment, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Socket.State != WebSocketState.Open) return;
                await Socket.SendAsync(segment, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        private readonly ChatEngine _chatEngine;
        private readonly byte[] _chatBroadcastBuffer = new byte[Marshal.SizeOf<ResponseChatMessagePacket>()];

        // Modul: a separate buffer from _chatBroadcastBuffer - the global
        // and guild Redis Pub/Sub channels are two independent
        // ChannelMessageQueue subscriptions (see ChatEngine.Subscribe), so
        // their handlers (BroadcastChatMessage / BroadcastGuildChatMessage)
        // can run concurrently with each other even though each
        // individually guarantees only one in-flight invocation for its
        // own channel. Sharing one buffer between the two would let a
        // guild broadcast overwrite a global broadcast's bytes mid-send (or
        // vice versa) - a real data race, not just a style preference.
        private readonly byte[] _guildChatBroadcastBuffer = new byte[Marshal.SizeOf<ResponseChatMessagePacket>()];

        public NetworkBroadcastSystem(IServiceProvider serviceProvider, string jwtSecretKey, string uriPrefix = "http://localhost:8080/")
        {
            _serviceProvider = serviceProvider;
            _contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();
            _redisSessionLock = serviceProvider.GetService<RedisPlayerSessionLock>();
            _jwtSecretKey = jwtSecretKey;
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add(uriPrefix);
            _chatEngine = new ChatEngine(serviceProvider);
            _chatEngine.OnMessageReceived += BroadcastChatMessage;
            _chatEngine.OnGuildMessageReceived += BroadcastGuildChatMessage;
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

        public void Start()
        {
            _httpListener.Start();
            _isRunning = true;
            Task.Run(ListenLoopAsync);
            SubscribeToSessionEviction();
            _chatEngine.Subscribe();
        }

        // Modul: fired by ChatEngine.OnMessageReceived whenever this pod's
        // Redis Pub/Sub subscription delivers a chat message - published by
        // any pod's PublishMessageAsync, including this one's own, so a
        // player sees their own message arrive back through the exact same
        // path as everyone else's rather than being echoed locally as a
        // special case. Sends to every currently connected local socket,
        // mirroring Broadcast(ref StateUpdatePacket)'s fanout pattern but
        // with its own buffer sized for ResponseChatMessagePacket - and,
        // unlike that method, fully awaited rather than fire-and-forget.
        // ChatEngine.Subscribe uses ChannelMessageQueue.OnMessage, which
        // guarantees this handler is only ever invoked for one message at a
        // time (never concurrently for a burst of near-simultaneous
        // publishes), so awaiting every SendAsync here in turn is both safe
        // and required: a fire-and-forget send here would let the next
        // queued message's broadcast start before this one's SendAsync
        // calls finished, racing multiple concurrent sends against the same
        // WebSocket instance - which .NET does not allow (an unawaited
        // second SendAsync on a socket with one already in flight throws,
        // and since nothing observed that faulted task, the message was
        // simply lost with no error surfaced anywhere).
        private async Task BroadcastChatMessage(ResponseChatMessagePacket packet)
        {
            // Modul: the ref-based span copy is factored into its own
            // synchronous method - ref locals/ref-returning APIs like
            // MemoryMarshal.CreateReadOnlySpan cannot be used directly in an
            // async method body (they cannot safely span an await),
            // matching the exact same restriction and fix already applied
            // to ContentRegistry's JSON export helper earlier in this
            // session.
            CopyChatPacketToBroadcastBuffer(packet);
            var segment = new ArraySegment<byte>(_chatBroadcastBuffer);

            foreach (var kvp in _connectedClients)
            {
                if (kvp.Value.Socket.State == WebSocketState.Open)
                {
                    try
                    {
                        await kvp.Value.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Chat broadcast send failed for player {kvp.Key}: {ex.Message}");
                    }
                }
            }
        }

        private void CopyChatPacketToBroadcastBuffer(ResponseChatMessagePacket packet)
        {
            ReadOnlySpan<ResponseChatMessagePacket> span = MemoryMarshal.CreateReadOnlySpan(ref packet, 1);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(span);
            bytes.CopyTo(_chatBroadcastBuffer);
        }

        // Modul: fired by ChatEngine.OnGuildMessageReceived - the guild-
        // channel counterpart to BroadcastChatMessage above. Routes
        // strictly to connected players sharing the identical GuildId
        // (compared via each session's cached GuildId field, never a
        // database read on this hot path), executing with zero managed
        // heap allocations: a plain foreach over the existing
        // _connectedClients dictionary with an int comparison filter, the
        // same pattern the unfiltered global broadcast already uses, no
        // LINQ, no intermediate collection.
        private async Task BroadcastGuildChatMessage(ResponseChatMessagePacket packet, long guildId)
        {
            CopyGuildChatPacketToBroadcastBuffer(packet);
            var segment = new ArraySegment<byte>(_guildChatBroadcastBuffer);

            foreach (var kvp in _connectedClients)
            {
                if (kvp.Value.GuildId != guildId)
                {
                    continue;
                }

                if (kvp.Value.Socket.State == WebSocketState.Open)
                {
                    try
                    {
                        await kvp.Value.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Guild chat broadcast send failed for player {kvp.Key}: {ex.Message}");
                    }
                }
            }
        }

        private void CopyGuildChatPacketToBroadcastBuffer(ResponseChatMessagePacket packet)
        {
            ReadOnlySpan<ResponseChatMessagePacket> span = MemoryMarshal.CreateReadOnlySpan(ref packet, 1);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(span);
            bytes.CopyTo(_guildChatBroadcastBuffer);
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

                    if (requestPath == "/admin/liveops" && context.Request.HttpMethod == "POST")
                    {
                        string secretKey = context.Request.Headers["X-Admin-Secret-Key"] ?? string.Empty;
                        string expectedKey = Environment.GetEnvironmentVariable("ADMIN_SECRET_KEY") ?? "supersecretadmin123";
                        if (secretKey != expectedKey)
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

                    if (requestPath == "/api/v1/breeding/preview" && context.Request.HttpMethod == "GET")
                    {
                        await HandleBreedingPreview(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/mastery/snapshot" && context.Request.HttpMethod == "GET")
                    {
                        await HandleMasterySnapshot(context);
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
                    if (requestPath == "/api/v1/mailbox/list" && context.Request.HttpMethod == "GET")
                    {
                        await HandleMailboxListSnapshot(context);
                        continue;
                    }

                    if (requestPath == "/api/v1/bank/list" && context.Request.HttpMethod == "GET")
                    {
                        await HandleBankListSnapshot(context);
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

                    if (requestPath == "/api/v1/market/listings" && context.Request.HttpMethod == "GET")
                    {
                        await HandleMarketBrowserListings(context);
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
        }

        private sealed class RaceMasterySnapshotEntryResponse
        {
            public int RaceId { get; set; }
            public int Level { get; set; }
            public long Experience { get; set; }
            public long NextLevelExperience { get; set; }
        }

        private sealed class LeaderboardEntryResponse
        {
            public int Rank { get; set; }
            public long PlayerId { get; set; }
            public string DisplayName { get; set; } = string.Empty;
            public int Level { get; set; }
            public long Xp { get; set; }
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
                int.TryParse(query["qualityTier"], out int qualityTier);
                int.TryParse(query["pageIndex"], out int pageIndex);
                if (!int.TryParse(query["pageSize"], out int pageSize))
                {
                    pageSize = 20;
                }

                if (string.IsNullOrEmpty(baseItemId) || !ClientCommandValidator.ValidateMarketBrowserQuery(playerId, pageIndex, pageSize))
                {
                    context.Response.StatusCode = 400;
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

                var listings = await MarketOrderBookEngine.FetchActiveListingsAsync(db, baseItemId, qualityTier, isQuarantined, pageIndex, pageSize);

                var response = new System.Collections.Generic.List<MarketListingResponse>(listings.Count);
                for (int i = 0; i < listings.Count; i++)
                {
                    response.Add(new MarketListingResponse
                    {
                        OrderId = listings[i].Id,
                        BaseItemId = listings[i].BaseItemId,
                        QualityTier = listings[i].QualityTier,
                        Price = listings[i].Price,
                        CreatedAtEpoch = listings[i].CreatedAtEpoch
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
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

                    for (int i = 0; i < redisEntries.Length; i++)
                    {
                        long pId = (long)redisEntries[i].Element;
                        if (players.TryGetValue(pId, out var p))
                        {
                            entries.Add(new LeaderboardEntryResponse
                            {
                                Rank = skip + i + 1,
                                PlayerId = p.Id,
                                DisplayName = "Player",
                                Level = p.CurrentLevel,
                                Xp = p.CurrentXp
                            });
                        }
                    }
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, entries);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Leaderboard error: {ex.Message}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
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

                foreach (var recipe in CraftingReceptuary.AllRecipes)
                {
                    string materialName = ContentRegistry.GetMaterialString(recipe.MaterialId);
                    materialQuantities.TryGetValue(materialName, out long currentStock);

                    response.Recipes.Add(new ForgeRecipeResponse
                    {
                        RecipeId = recipe.RecipeId,
                        ResultBaseItemId = recipe.ResultBaseItemId,
                        TierIndex = recipe.TierIndex,
                        MaterialName = materialName,
                        MaterialCost = recipe.MaterialCost,
                        CurrentMaterialStock = currentStock
                    });
                }

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

        private async Task HandleBankListSnapshot(HttpListenerContext context)
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

                var entries = await db.BankEquipmentInstances
                    .AsNoTracking()
                    .Where(b => b.PlayerId == playerId)
                    .Select(b => new BankEntryResponse
                    {
                        Id = b.Id,
                        BaseItemId = b.BaseItemId,
                        QualityTier = b.QualityTier,
                        IsAffixLocked = b.IsAffixLocked
                    })
                    .ToListAsync();

                await transaction.CommitAsync();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, entries);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Bank list snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
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

        private sealed class BreedingPreviewResponse
        {
            public bool IsEligible { get; set; }
            public string IneligibleReason { get; set; } = string.Empty;
            public bool IsInbredRisk { get; set; }
            public long BreedingCostGold { get; set; }
            public bool HasSufficientGold { get; set; }
            public System.Collections.Generic.List<GenePreviewLocusResponse> Loci { get; set; } = new();
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
                response.BreedingCostGold = 500L * (maxGen + 1);

                var goldRecord = await db.CommodityRecords
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
                response.HasSufficientGold = goldRecord != null && goldRecord.Quantity >= response.BreedingCostGold;

                AddLocusPreview(response.Loci, "Race", pVec.LocusRace, mVec.LocusRace, maxGen);
                AddLocusPreview(response.Loci, "Speed", pVec.LocusSpeed, mVec.LocusSpeed, maxGen);
                AddLocusPreview(response.Loci, "Crit", pVec.LocusCrit, mVec.LocusCrit, maxGen);
                AddLocusPreview(response.Loci, "Yield", pVec.LocusYield, mVec.LocusYield, maxGen);

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
                        NextTierReward = AchievementMilestones.GetNextTierReward(entry.AchievementId, entry.CompletedTier)
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
                        
                        var playerRegistry = _serviceProvider.GetRequiredService<PlayerSessionRegistry>();
                        playerRegistry.QuarantineNotificationQueue.Enqueue(new QuarantineNotification { PlayerId = player.Id });
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
                if (result.MessageType == WebSocketMessageType.Binary && result.Count >= Marshal.SizeOf<AuthHandshakePacket>())
                {
                    var authPacket = ParseAuthHandshakePacket(buffer, result.Count);
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
                            _ = ObserveSendFault(staleSession.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Superseded by a new login", CancellationToken.None), playerId);
                        }
                    }

                    _connectedClients[playerId] = new WebSocketSession(socket, redisLockToken ?? string.Empty);
                    CommandQueue.Enqueue(new PlayerCommand { PlayerId = playerId, Packet = new ClientCommandPacket { Command = CommandType.Login, TargetId = playerId } });
                }
                else
                {
                    await socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Expected Auth Handshake Packet", CancellationToken.None);
                    return;
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
                            string chatText = ExtractChatMessageText(ref chatRequest);

                            if (chatRequest.ChannelType == ChatEngine.GuildChannelType)
                            {
                                // A player not currently in a guild has
                                // nothing to route a guild message to -
                                // silently dropped, matching every other
                                // rejected-chat-message path (rate limit,
                                // empty content) rather than disconnecting.
                                if (session.GuildId > 0)
                                {
                                    _ = _chatEngine.PublishGuildMessageAsync(playerId, session.GuildId, chatText);
                                }
                            }
                            else
                            {
                                _ = _chatEngine.PublishMessageAsync(playerId, chatText);
                            }
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

            ReadOnlySpan<StateUpdatePacket> span = MemoryMarshal.CreateReadOnlySpan(ref packet, 1);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(span);
            bytes.CopyTo(session.DiagnosticSendBuffer);
            var segment = new ArraySegment<byte>(session.DiagnosticSendBuffer);

            // Fire-and-forget is intentional here - SendToPlayer is called
            // once per player per broadcast tick and must not block the
            // caller - but the fault is still observed and logged rather
            // than silently dropped, matching this task's error-
            // observability requirement.
            _ = ObserveSendFault(session.SendAsync(segment, WebSocketMessageType.Binary, true, CancellationToken.None), playerId);
        }

        private static async Task ObserveSendFault(Task sendTask, long playerId)
        {
            try
            {
                await sendTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"State broadcast send failed for player {playerId}: {ex.Message}");
            }
        }

        public void ForceDisconnect(long playerId)
        {
            if (_connectedClients.TryRemove(playerId, out var session))
            {
                if (_redisSessionLock != null && !string.IsNullOrEmpty(session.RedisLockToken))
                {
                    _ = _redisSessionLock.ReleaseAsync(playerId, session.RedisLockToken);
                }

                _ = ObserveSendFault(session.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Violent termination", CancellationToken.None), playerId);
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
