using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Network;
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
            var redis = _serviceProvider.GetService<IConnectionMultiplexer>();
            if (redis == null || !redis.IsConnected)
            {
                return;
            }

            var subscriber = redis.GetSubscriber();
            ChannelMessageQueue queue = subscriber.Subscribe(RedisChannel.Literal(GlobalChatChannel));
            queue.OnMessage(HandleRedisMessageAsync);

            ChannelMessageQueue guildQueue = subscriber.Subscribe(RedisChannel.Literal(GuildChatChannel));
            guildQueue.OnMessage(HandleGuildRedisMessageAsync);

            ChannelMessageQueue whisperQueue = subscriber.Subscribe(RedisChannel.Literal(WhisperChatChannel));
            whisperQueue.OnMessage(HandleWhisperRedisMessageAsync);

            StartDispatchWorker();
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

            var redis = _serviceProvider.GetService<IConnectionMultiplexer>();
            if (redis == null || !redis.IsConnected)
            {
                return false;
            }

            long timestampEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
                return false;
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

            var redis = _serviceProvider.GetService<IConnectionMultiplexer>();
            if (redis == null || !redis.IsConnected)
            {
                return false;
            }

            long timestampEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
                return false;
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

            var redis = _serviceProvider.GetService<IConnectionMultiplexer>();
            if (redis == null || !redis.IsConnected)
            {
                return false;
            }

            long timestampEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
                return false;
            }
        }
    }
}
