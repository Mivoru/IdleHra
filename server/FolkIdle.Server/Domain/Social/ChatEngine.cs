using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Network;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Social
{
    // Modul: global chat, scaled across pods via Redis Pub/Sub - mirrors
    // NetworkBroadcastSystem.SubscribeToSessionEviction's exact shape (one
    // persistent pod-wide subscription, not one per connection). A pod that
    // receives a chat message from a connected client publishes it to
    // GlobalChatChannel; every pod (including the publisher, so its own
    // sender sees their own message the same way as everyone else) is
    // subscribed and fires OnMessageReceived, which NetworkBroadcastSystem
    // hooks to fan the message out to its own locally connected sockets -
    // this class never touches a WebSocket directly, only Redis and the
    // packet shape.
    //
    // Rate limiting reuses the existing zero-allocation TokenBucket struct
    // (see NetworkThrottlingEngine) but with its own, stricter bucket
    // instance per connection (WebSocketSession.ChatTokenBucket) and its own
    // capacity/refill constants - deliberately separate from the general
    // per-packet flood throttle, since that one disconnects on a single
    // violation (appropriate for a suspected exploit attempt) while chat
    // spam is normal, recoverable user behavior that should only ever drop
    // the excess message, never the connection.
    public sealed class ChatEngine
    {
        public const string GlobalChatChannel = "chat:global";

        // Modul: a separate Redis channel from GlobalChatChannel, not a
        // shared channel with an in-payload discriminator - keeps the
        // existing "playerId:timestamp:message" global payload format
        // completely untouched (no parsing ambiguity risk) and lets each
        // pod subscribe/unsubscribe to the two independently if that is
        // ever needed. Payload format is "playerId:guildId:timestamp:
        // message" (4 colon-delimited parts, one more than global's 3).
        public const string GuildChatChannel = "chat:guild";

        // Modul: mirrors RequestChatMessagePacket/ResponseChatMessagePacket.
        // ChannelType exactly (0 = Global, 1 = Guild) - both client and
        // server wire-format mirrors must agree on these literal values.
        public const byte GlobalChannelType = 0;
        public const byte GuildChannelType = 1;

        // Modul: Full-Stack Social Layer, Part 3. Private Whisper channel -
        // a direct-message counterpart to Global/Guild, routed to exactly
        // one online recipient rather than broadcast/guild-filtered.
        public const byte WhisperChannelType = 2;

        // Modul: mirrors GuildChatChannel's own rationale - its own Redis
        // channel rather than a shared payload discriminator, so a pod's
        // subscription set stays independently manageable. Payload format
        // is "senderPlayerId:targetPlayerId:timestamp:message", the same
        // 4-part shape GuildChatChannel already uses (guildId's slot is
        // simply targetPlayerId here).
        public const string WhisperChatChannel = "chat:whisper";

        public const double ChatBucketCapacity = 5.0;
        public const double ChatBucketRefillRatePerSecond = 0.5;

        // Modul: Full-Stack Social Layer, Part 1. Asynchronous chat
        // offloading. Every inbound Redis Pub/Sub message (global, guild,
        // or whisper) is turned into a ChatDispatchItem and pushed here
        // rather than having its network fan-out awaited directly inside
        // the Redis subscription callback - ChannelMessageQueue.OnMessage
        // only guarantees THIS pod's messages for one channel are handled
        // one at a time, so awaiting a potentially-slow multi-socket
        // fan-out there would stall delivery of the next queued message
        // behind however long that fan-out takes. A single dedicated
        // background worker (StartDispatchWorker) drains this queue and
        // performs the actual per-connection network I/O, decoupled from
        // the Redis message pump entirely. ConcurrentQueue<T> is the same
        // lock-free ring-buffer-style primitive this codebase already uses
        // for CombatLootEngine.DropRequestQueue and the various
        // notification queues on PlayerSessionRegistry.
        public readonly struct ChatDispatchItem
        {
            public readonly ResponseChatMessagePacket Packet;
            public readonly byte DispatchMode;
            public readonly long GuildId;
            public readonly long TargetPlayerId;

            public ChatDispatchItem(ResponseChatMessagePacket packet, byte dispatchMode, long guildId, long targetPlayerId)
            {
                Packet = packet;
                DispatchMode = dispatchMode;
                GuildId = guildId;
                TargetPlayerId = targetPlayerId;
            }
        }

        public const byte DispatchModeGlobal = 0;
        public const byte DispatchModeGuild = 1;
        public const byte DispatchModeWhisper = 2;

        public readonly ConcurrentQueue<ChatDispatchItem> OutboundDispatchQueue = new();

        // Modul: high-rarity announcements, 2026-08-01.
        //
        // A third channel type alongside Global(0) and Guild(1). The client
        // needs to tell an announcement apart from an ordinary global message
        // to colour it by rarity and attach the congratulate button, and a
        // dedicated channel byte does that without parsing message text.
        public const byte AnnouncementChannelType = 3;

        // STATIC on purpose. ChatEngine is constructed inside
        // NetworkBroadcastSystem rather than registered in DI, so an engine
        // like AffixRerollEngine has no way to resolve an instance. Threading a
        // reference through every engine that might announce something would
        // couple half the server to chat; a queue keeps the dependency
        // one-directional, matching how every other cross-thread hand-off in
        // this codebase works.
        //
        // Bounded: an announcement is a nice-to-have, so under a flood it is
        // correct to drop rather than to grow without limit or to block the
        // engine that produced it.
        public const int MaxQueuedAnnouncements = 256;
        public static readonly ConcurrentQueue<string> SystemAnnouncementQueue = new();

        public static void EnqueueSystemAnnouncement(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (SystemAnnouncementQueue.Count >= MaxQueuedAnnouncements) return;

            SystemAnnouncementQueue.Enqueue(text);
        }

        // Modul: the network layer (NetworkBroadcastSystem) owns the
        // WebSocket connections ChatEngine never touches directly - this
        // event is how the dispatch worker below hands a queued item back
        // to whoever can actually perform the send.
        public event Func<ChatDispatchItem, Task>? OnDispatchReady;

        private int _dispatchWorkerStarted;

        // Modul: idempotent - Start() calling this every time it runs is
        // safe, only the first call actually spins up the worker thread.
        public void StartDispatchWorker()
        {
            if (Interlocked.Exchange(ref _dispatchWorkerStarted, 1) != 0)
            {
                return;
            }

            Task.Run(DispatchWorkerLoopAsync);
        }

        private async Task DispatchWorkerLoopAsync()
        {
            while (true)
            {
                // Drained here rather than on the producing thread so the
                // packet build and the send both stay on this worker.
                // SenderPlayerId 0 marks it as system-authored - no real player
                // has id 0, so the client can trust the distinction.
                while (SystemAnnouncementQueue.TryDequeue(out string? announcement))
                {
                    ResponseChatMessagePacket announcementPacket = BuildResponsePacket(
                        senderPlayerId: 0L,
                        timestampEpochMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        messageText: announcement,
                        channelType: AnnouncementChannelType);

                    OutboundDispatchQueue.Enqueue(new ChatDispatchItem(announcementPacket, DispatchModeGlobal, guildId: 0, targetPlayerId: 0));
                }

                if (OutboundDispatchQueue.TryDequeue(out ChatDispatchItem item))
                {
                    if (OnDispatchReady != null)
                    {
                        try
                        {
                            await OnDispatchReady.Invoke(item);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Chat dispatch failed: {ex.Message}");
                        }
                    }
                }
                else
                {
                    await Task.Delay(10);
                }
            }
        }

        private readonly IServiceProvider _serviceProvider;

        // Modul: ChannelMessageQueue.OnMessage (used below, not the sync
        // Subscribe overload) guarantees each Redis channel's messages
        // reach these handlers strictly one at a time, never concurrently
        // for a burst of near-simultaneous publishes - previously load-
        // bearing because the handler awaited the full network fan-out
        // inline (a fire-and-forget failure there silently dropped
        // messages under any real burst, "There is already one
        // outstanding SendAsync call for this WebSocket instance"). Now
        // that the handlers below only enqueue a ChatDispatchItem and
        // return immediately (see OutboundDispatchQueue/StartDispatchWorker
        // above), that ordering guarantee no longer needs to hold across a
        // slow send - it just keeps enqueue order matching publish order,
        // which is still exactly what is wanted.
        public ChatEngine(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public static TokenBucket CreateChatBucket()
        {
            return new TokenBucket
            {
                AvailableTokens = ChatBucketCapacity,
                LastRefillTimestampEpoch = System.Diagnostics.Stopwatch.GetTimestamp()
            };
        }

        // Modul: identical refill/consume math to NetworkThrottlingEngine.
        // TryConsume, parameterized by capacity/refill rate instead of that
        // class's fixed constants, so this can enforce chat's own stricter
        // budget against the same TokenBucket struct type without touching
        // the general packet-flood throttle's behavior at all. Public and
        // synchronous (called by NetworkBroadcastSystem's receive loop
        // directly against session.ChatTokenBucket) rather than folded into
        // PublishMessageAsync below - async methods cannot take a ref
        // parameter, and this check must run against the caller's own
        // TokenBucket field in place, not a copy.
        public static bool TryConsumeChatToken(ref TokenBucket bucket)
        {
            long currentTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            if (bucket.LastRefillTimestampEpoch <= 0L)
            {
                bucket.AvailableTokens = ChatBucketCapacity;
                bucket.LastRefillTimestampEpoch = currentTicks;
            }

            long elapsedTicks = currentTicks - bucket.LastRefillTimestampEpoch;
            if (elapsedTicks > 0L)
            {
                bucket.AvailableTokens = Math.Min(ChatBucketCapacity, bucket.AvailableTokens + (double)elapsedTicks / System.Diagnostics.Stopwatch.Frequency * ChatBucketRefillRatePerSecond);
                bucket.LastRefillTimestampEpoch = currentTicks;
            }

            if (bucket.AvailableTokens < 1.0)
            {
                return false;
            }

            bucket.AvailableTokens -= 1.0;
            return true;
        }

        public void Subscribe()
        {
            // Modul: started BEFORE the Redis availability check, not after.
            // Without Redis this pod still has to deliver its own players'
            // messages to each other through the local-loopback path added
            // to the three Publish*Async methods below - and that path
            // enqueues onto OutboundDispatchQueue, which is inert unless
            // this worker is running. Idempotent, so the Redis-present path
            // reaching it again below is harmless.
            StartDispatchWorker();

            var redis = _serviceProvider.GetService<IConnectionMultiplexer>();
            if (redis == null)
            {
                // No multiplexer registered at all - single-pod mode. The
                // local-loopback path above already delivers this pod's own
                // messages, so there is nothing to retry against.
                return;
            }

            // Modul: chat resilience, 2026-08-01.
            //
            // This used to be `if (!redis.IsConnected) return;` with no retry.
            // A server that booted while Redis was still starting - which
            // container start order makes routine - skipped all three
            // subscriptions and had permanently dead global, guild and whisper
            // chat for the lifetime of the process, with no error logged
            // anywhere. Confirmed live on 2026-08-01: zero messages delivered,
            // and starting Redis afterwards did not help; only a server restart
            // did.
            //
            // Two independent recovery paths now, because they cover different
            // failures. ConnectionRestored handles a Redis that goes away and
            // comes back under a running server. The retry loop handles a Redis
            // that was never reachable at boot, where no "restored" event ever
            // fires because there was never a connection to restore.
            redis.ConnectionRestored += (_, _) =>
            {
                // Fires per-endpoint and can fire repeatedly; TrySubscribeAll is
                // idempotent so a storm of these costs one interlocked read.
                TrySubscribeAll(redis);
            };

            if (!TrySubscribeAll(redis))
            {
                _ = RetrySubscribeUntilConnectedAsync(redis);
            }
        }

        // 0 = not yet subscribed, 1 = subscribed. Guards against the boot call,
        // the ConnectionRestored handler and the retry loop all racing to
        // subscribe the same three channels.
        private int _redisSubscribed;

        // Modul: chat resilience, 2026-08-01. These MUST be held as fields.
        //
        // ChannelMessageQueue is what actually pumps messages to the OnMessage
        // handler; leaving the three of them as locals lets the GC collect them
        // and delivery stops silently - the subscription still shows up in
        // Redis PUBSUB CHANNELS, so it looks healthy from the outside while no
        // message is ever handled.
        //
        // Observed exactly that during the live test: the retry path logged a
        // successful subscribe, redis-cli confirmed the publish reached
        // chat:global with the server as a subscriber, and the client received
        // nothing. The boot path appeared to work only because its locals
        // happened to survive long enough.
        private ChannelMessageQueue? _globalQueue;
        private ChannelMessageQueue? _guildQueue;
        private ChannelMessageQueue? _whisperQueue;

        private bool TrySubscribeAll(IConnectionMultiplexer redis)
        {
            if (!redis.IsConnected)
            {
                return false;
            }

            if (Interlocked.Exchange(ref _redisSubscribed, 1) != 0)
            {
                return true;
            }

            try
            {
                var subscriber = redis.GetSubscriber();

                _globalQueue = subscriber.Subscribe(RedisChannel.Literal(GlobalChatChannel));
                _globalQueue.OnMessage(HandleRedisMessageAsync);

                _guildQueue = subscriber.Subscribe(RedisChannel.Literal(GuildChatChannel));
                _guildQueue.OnMessage(HandleGuildRedisMessageAsync);

                _whisperQueue = subscriber.Subscribe(RedisChannel.Literal(WhisperChatChannel));
                _whisperQueue.OnMessage(HandleWhisperRedisMessageAsync);

                StartDispatchWorker();
                Console.WriteLine("ChatEngine: Redis chat channels subscribed.");
                return true;
            }
            catch (Exception ex)
            {
                // Release the guard so a later attempt can genuinely retry -
                // leaving it set would make one transient failure permanent,
                // which is the whole bug this method exists to fix.
                Interlocked.Exchange(ref _redisSubscribed, 0);
                Console.WriteLine($"ChatEngine: chat subscribe failed, will retry: {ex.Message}");
                return false;
            }
        }

        // Bounded retry rather than forever: if Redis is still unreachable
        // after this long, the deployment is broken in a way a background loop
        // cannot fix, and a silently spinning task would hide that. Chat still
        // works pod-locally throughout via the loopback path.
        private const int SubscribeRetryDelayMs = 5000;
        private const int SubscribeRetryAttempts = 60;

        private async Task RetrySubscribeUntilConnectedAsync(IConnectionMultiplexer redis)
        {
            for (int attempt = 0; attempt < SubscribeRetryAttempts; attempt++)
            {
                await Task.Delay(SubscribeRetryDelayMs);

                if (Volatile.Read(ref _redisSubscribed) != 0)
                {
                    // ConnectionRestored got there first.
                    return;
                }

                if (TrySubscribeAll(redis))
                {
                    return;
                }
            }

            Console.WriteLine(
                $"ChatEngine: Redis chat channels still unsubscribed after {SubscribeRetryAttempts} attempts. " +
                "Cross-pod chat is unavailable; pod-local chat continues to work.");
        }

        private Task HandleRedisMessageAsync(ChannelMessage message)
        {
            string payload = message.Message.ToString();
            string[] parts = payload.Split(':', 3);
            if (parts.Length != 3)
            {
                return Task.CompletedTask;
            }

            if (!long.TryParse(parts[0], out long senderPlayerId) || !long.TryParse(parts[1], out long timestampEpochMs))
            {
                return Task.CompletedTask;
            }

            ResponseChatMessagePacket packet = BuildResponsePacket(senderPlayerId, timestampEpochMs, parts[2], GlobalChannelType);
            OutboundDispatchQueue.Enqueue(new ChatDispatchItem(packet, DispatchModeGlobal, guildId: 0, targetPlayerId: 0));
            return Task.CompletedTask;
        }

        // Modul: payload format "playerId:guildId:timestamp:message" - one
        // more colon-delimited part than the global channel's format (see
        // GuildChatChannel's own comment).
        private Task HandleGuildRedisMessageAsync(ChannelMessage message)
        {
            string payload = message.Message.ToString();
            string[] parts = payload.Split(':', 4);
            if (parts.Length != 4)
            {
                return Task.CompletedTask;
            }

            if (!long.TryParse(parts[0], out long senderPlayerId) || !long.TryParse(parts[1], out long guildId) || !long.TryParse(parts[2], out long timestampEpochMs))
            {
                return Task.CompletedTask;
            }

            ResponseChatMessagePacket packet = BuildResponsePacket(senderPlayerId, timestampEpochMs, parts[3], GuildChannelType);
            OutboundDispatchQueue.Enqueue(new ChatDispatchItem(packet, DispatchModeGuild, guildId, targetPlayerId: 0));
            return Task.CompletedTask;
        }

        // Modul: payload format "senderPlayerId:targetPlayerId:timestamp:
        // message" - see WhisperChatChannel's own comment.
        private Task HandleWhisperRedisMessageAsync(ChannelMessage message)
        {
            string payload = message.Message.ToString();
            string[] parts = payload.Split(':', 4);
            if (parts.Length != 4)
            {
                return Task.CompletedTask;
            }

            if (!long.TryParse(parts[0], out long senderPlayerId) || !long.TryParse(parts[1], out long targetPlayerId) || !long.TryParse(parts[2], out long timestampEpochMs))
            {
                return Task.CompletedTask;
            }

            ResponseChatMessagePacket packet = BuildResponsePacket(senderPlayerId, timestampEpochMs, parts[3], WhisperChannelType);
            OutboundDispatchQueue.Enqueue(new ChatDispatchItem(packet, DispatchModeWhisper, guildId: 0, targetPlayerId));
            return Task.CompletedTask;
        }

        private static unsafe ResponseChatMessagePacket BuildResponsePacket(long senderPlayerId, long timestampEpochMs, string messageText, byte channelType)
        {
            byte[] textBytes = Encoding.UTF8.GetBytes(messageText);
            if (textBytes.Length > ResponseChatMessagePacket.MessageCapacity)
            {
                Array.Resize(ref textBytes, ResponseChatMessagePacket.MessageCapacity);
            }

            var packet = new ResponseChatMessagePacket
            {
                SenderPlayerId = senderPlayerId,
                TimestampEpochMs = timestampEpochMs,
                MessageLength = (ushort)textBytes.Length,
                ChannelType = channelType
            };

            byte* target = packet.MessageText;
            for (int i = 0; i < ResponseChatMessagePacket.MessageCapacity; i++)
            {
                target[i] = i < textBytes.Length ? textBytes[i] : (byte)0;
            }

            return packet;
        }

        // Modul: validates content and publishes to Redis - never touches a
        // WebSocket. Rate limiting is NOT checked here - the caller
        // (NetworkBroadcastSystem's receive loop) must call the synchronous
        // TryConsumeChatToken against its own session.ChatTokenBucket first
        // (async methods cannot take a ref parameter, so the two checks
        // cannot live in one call). A rejected message (rate limited or
        // invalid content) simply returns false; the caller silently drops
        // it without closing the connection, matching this task's explicit
        // requirement that spam is dropped, not disconnect-worthy.
        public async Task<bool> PublishMessageAsync(long playerId, string messageText)
        {
            string trimmed = messageText.Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }

            byte[] textBytes = Encoding.UTF8.GetBytes(trimmed);
            if (textBytes.Length > RequestChatMessagePacket.MessageCapacity)
            {
                return false;
            }

            long timestampEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var redis = _serviceProvider.GetService<IConnectionMultiplexer>();
            if (redis == null || !redis.IsConnected)
            {
                return DispatchLocally(playerId, timestampEpochMs, trimmed, GlobalChannelType, DispatchModeGlobal, guildId: 0, targetPlayerId: 0);
            }

            string payload = $"{playerId}:{timestampEpochMs}:{trimmed}";

            try
            {
                var subscriber = redis.GetSubscriber();
                await subscriber.PublishAsync(RedisChannel.Literal(GlobalChatChannel), payload);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chat publish failed for player {playerId}: {ex.Message}");
                return DispatchLocally(playerId, timestampEpochMs, trimmed, GlobalChannelType, DispatchModeGlobal, guildId: 0, targetPlayerId: 0);
            }
        }

        // Modul: guild-channel counterpart to PublishMessageAsync above -
        // same validation and never touches a WebSocket, but publishes to
        // GuildChatChannel with guildId embedded in the payload so every
        // pod's HandleGuildRedisMessageAsync can hand it to
        // NetworkBroadcastSystem.BroadcastGuildChatMessage for server-side
        // membership filtering. The caller (NetworkBroadcastSystem's
        // receive loop) is responsible for resolving guildId from the
        // sender's own cached session state and for never calling this
        // with guildId <= 0.
        public async Task<bool> PublishGuildMessageAsync(long playerId, long guildId, string messageText)
        {
            string trimmed = messageText.Trim();
            if (trimmed.Length == 0 || guildId <= 0)
            {
                return false;
            }

            byte[] textBytes = Encoding.UTF8.GetBytes(trimmed);
            if (textBytes.Length > RequestChatMessagePacket.MessageCapacity)
            {
                return false;
            }

            long timestampEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var redis = _serviceProvider.GetService<IConnectionMultiplexer>();
            if (redis == null || !redis.IsConnected)
            {
                return DispatchLocally(playerId, timestampEpochMs, trimmed, GuildChannelType, DispatchModeGuild, guildId, targetPlayerId: 0);
            }

            string payload = $"{playerId}:{guildId}:{timestampEpochMs}:{trimmed}";

            try
            {
                var subscriber = redis.GetSubscriber();
                await subscriber.PublishAsync(RedisChannel.Literal(GuildChatChannel), payload);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Guild chat publish failed for player {playerId}: {ex.Message}");
                return DispatchLocally(playerId, timestampEpochMs, trimmed, GuildChannelType, DispatchModeGuild, guildId, targetPlayerId: 0);
            }
        }

        // Modul: Full-Stack Social Layer, Part 3. Whisper counterpart to
        // PublishGuildMessageAsync above - same validation, publishes to
        // WhisperChatChannel with the recipient embedded in the payload so
        // whichever pod the recipient happens to be connected to can
        // deliver it. Block-status is enforced at dispatch time (see
        // NetworkBroadcastSystem's dispatch handler), not here - by the
        // time a message reaches Redis it is already validated content
        // from a real sender, and the one authoritative block check
        // should live in exactly one place.
        /// <summary>
        /// Stores one private message as the durable half of a conversation.
        /// </summary>
        /// <remarks>
        /// Failure here must NOT stop delivery. A message that arrives but is
        /// not recorded is a worse outcome than one that is recorded but not
        /// recorded twice - and the alternative, refusing to deliver because a
        /// write failed, would turn a database hiccup into chat being down.
        /// Logged and swallowed, matching how the rest of this engine treats a
        /// broken transport.
        /// </remarks>
        private async Task PersistWhisperAsync(long senderPlayerId, long targetPlayerId, string messageText, long timestampEpochMs)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var (low, high) = ConversationMessage.PairKey(senderPlayerId, targetPlayerId);

                db.ConversationMessages.Add(new ConversationMessage
                {
                    LowPlayerId = low,
                    HighPlayerId = high,
                    SenderPlayerId = senderPlayerId,
                    RecipientPlayerId = targetPlayerId,
                    MessageText = messageText,
                    SentAtEpochMs = timestampEpochMs,
                    ReadAtEpochMs = null
                });

                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Whisper persistence failed for player {senderPlayerId}: {ex.Message}");
            }
        }

        public async Task<bool> PublishWhisperMessageAsync(long playerId, long targetPlayerId, string messageText)
        {
            string trimmed = messageText.Trim();
            if (trimmed.Length == 0 || targetPlayerId <= 0 || targetPlayerId == playerId)
            {
                return false;
            }

            byte[] textBytes = Encoding.UTF8.GetBytes(trimmed);
            if (textBytes.Length > RequestChatMessagePacket.MessageCapacity)
            {
                return false;
            }

            long timestampEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Modul: WRITE IT DOWN BEFORE TRYING TO DELIVER IT, 2026-09-01.
            //
            // Delivery is best-effort by design - Redis fan-out to whoever is
            // connected - and it used to be the ONLY thing that happened. A
            // whisper to a player who was offline reached the dispatch, found
            // no session in the connected-client map and returned, so the
            // sender saw it sent and the recipient never learned it existed.
            // Persisting first makes the record the source of truth and the
            // packet a live notification: an offline recipient now reads it in
            // their conversation the next time they sign in.
            //
            // Ordered before the Redis branch on purpose, so it happens whether
            // Redis is up, down, or absent - the loopback path below is exactly
            // when a message would otherwise be most likely to vanish.
            //
            // The text reaching here has already been through
            // ChatProfanityFilter at DispatchInboundChatRequest, which is the
            // single choke point for inbound chat. Anything that ever writes to
            // this table from somewhere else must filter first - the filter is
            // not applied at this layer or at the database.
            await PersistWhisperAsync(playerId, targetPlayerId, trimmed, timestampEpochMs);

            var redis = _serviceProvider.GetService<IConnectionMultiplexer>();
            if (redis == null || !redis.IsConnected)
            {
                return DispatchLocally(playerId, timestampEpochMs, trimmed, WhisperChannelType, DispatchModeWhisper, guildId: 0, targetPlayerId);
            }

            string payload = $"{playerId}:{targetPlayerId}:{timestampEpochMs}:{trimmed}";

            try
            {
                var subscriber = redis.GetSubscriber();
                await subscriber.PublishAsync(RedisChannel.Literal(WhisperChatChannel), payload);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Whisper chat publish failed for player {playerId}: {ex.Message}");
                return DispatchLocally(playerId, timestampEpochMs, trimmed, WhisperChannelType, DispatchModeWhisper, guildId: 0, targetPlayerId);
            }
        }

        // Modul: single-pod loopback. Every chat channel previously reached
        // its own recipients ONLY by round-tripping through Redis Pub/Sub -
        // so with Redis unreachable (this project's documented "Redis is
        // optional, degrade gracefully" stance, already applied to the
        // leaderboard endpoints and RedisPlayerSessionLock.RenewAsync)
        // Publish*Async returned false immediately and chat was silently,
        // completely dead: no global, no guild, no whisper, not even
        // between two players connected to this very pod.
        //
        // The Redis hop exists for CROSS-pod fan-out, not for delivery as
        // such - HandleRedisMessageAsync and friends do nothing but rebuild
        // the packet and enqueue it onto OutboundDispatchQueue, which is
        // exactly what this does directly. So without Redis, delivery
        // degrades to "everyone connected to this pod" instead of "nobody",
        // and the multi-pod path is untouched when Redis is present.
        private bool DispatchLocally(long senderPlayerId, long timestampEpochMs, string messageText, byte channelType, byte dispatchMode, long guildId, long targetPlayerId)
        {
            ResponseChatMessagePacket packet = BuildResponsePacket(senderPlayerId, timestampEpochMs, messageText, channelType);
            OutboundDispatchQueue.Enqueue(new ChatDispatchItem(packet, dispatchMode, guildId, targetPlayerId));
            return true;
        }
    }
}
