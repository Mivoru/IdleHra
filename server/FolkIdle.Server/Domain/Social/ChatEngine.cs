using System;
using System.Text;
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

        public const double ChatBucketCapacity = 5.0;
        public const double ChatBucketRefillRatePerSecond = 0.5;

        private readonly IServiceProvider _serviceProvider;

        // Modul: a Func<T, Task>, not a plain Action, and dispatched through
        // ChannelMessageQueue.OnMessage below (not the sync Subscribe
        // overload) - StackExchange.Redis can invoke a plain Action handler
        // for a burst of near-simultaneous published messages concurrently,
        // which previously raced multiple NetworkBroadcastSystem.
        // BroadcastChatMessage calls against the same _connectedClients
        // sockets ("There is already one outstanding SendAsync call for
        // this WebSocket instance") - a fire-and-forget failure that
        // silently dropped messages under any real burst (a rate-limited
        // client sending several messages back to back was enough to
        // reproduce it). ChannelMessageQueue.OnMessage guarantees this
        // channel's messages are delivered to the handler strictly one at a
        // time, awaiting each call before dispatching the next, which is
        // exactly the ordering guarantee needed to make sequential SendAsync
        // calls per connection safe again.
        public event Func<ResponseChatMessagePacket, Task>? OnMessageReceived;

        // Modul: guildId is a separate delegate parameter, not embedded in
        // ResponseChatMessagePacket - the outgoing wire packet only needs
        // ChannelType (so the client's chat UI can route it) since
        // NetworkBroadcastSystem.BroadcastGuildChatMessage already performs
        // guild-membership filtering server-side before any send occurs;
        // by the time a client receives this, it is implicitly for their
        // own guild, so a GuildId field on the packet would be pure dead
        // weight over the wire.
        public event Func<ResponseChatMessagePacket, long, Task>? OnGuildMessageReceived;

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
        }

        private async Task HandleRedisMessageAsync(ChannelMessage message)
        {
            string payload = message.Message.ToString();
            string[] parts = payload.Split(':', 3);
            if (parts.Length != 3)
            {
                return;
            }

            if (!long.TryParse(parts[0], out long senderPlayerId) || !long.TryParse(parts[1], out long timestampEpochMs))
            {
                return;
            }

            ResponseChatMessagePacket packet = BuildResponsePacket(senderPlayerId, timestampEpochMs, parts[2], GlobalChannelType);

            if (OnMessageReceived != null)
            {
                await OnMessageReceived.Invoke(packet);
            }
        }

        // Modul: payload format "playerId:guildId:timestamp:message" - one
        // more colon-delimited part than the global channel's format (see
        // GuildChatChannel's own comment).
        private async Task HandleGuildRedisMessageAsync(ChannelMessage message)
        {
            string payload = message.Message.ToString();
            string[] parts = payload.Split(':', 4);
            if (parts.Length != 4)
            {
                return;
            }

            if (!long.TryParse(parts[0], out long senderPlayerId) || !long.TryParse(parts[1], out long guildId) || !long.TryParse(parts[2], out long timestampEpochMs))
            {
                return;
            }

            ResponseChatMessagePacket packet = BuildResponsePacket(senderPlayerId, timestampEpochMs, parts[3], GuildChannelType);

            if (OnGuildMessageReceived != null)
            {
                await OnGuildMessageReceived.Invoke(packet, guildId);
            }
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
    }
}
