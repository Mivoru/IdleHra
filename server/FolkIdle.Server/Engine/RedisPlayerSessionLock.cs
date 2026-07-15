using System;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace FolkIdle.Server.Engine
{
    public sealed class RedisPlayerSessionLock
    {
        private const string ReleaseScript = "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) else return 0 end";
        private const string RenewScript = "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('PEXPIRE', KEYS[1], ARGV[2]) else return 0 end";
        private const int LeaseMilliseconds = 30000;

        // Modul: eviction notification channel for multi-boxing prevention.
        // Every pod subscribes once (see NetworkBroadcastSystem.Start) and,
        // on receiving a message for a player it currently holds a
        // _connectedClients entry for with a DIFFERENT lock token than the
        // one just announced, force-disconnects its own stale connection -
        // this is what makes eviction work even when the older connection
        // lives on a different pod than the new login.
        public const string EvictionChannel = "session-evict";

        private readonly IConnectionMultiplexer _redis;

        public RedisPlayerSessionLock(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        // Modul: unconditionally takes ownership of the lock (unlike a
        // NotExists-guarded acquire, which would reject a new connection
        // outright whenever an old lock is still held) and publishes an
        // eviction notice so whichever pod is still holding the superseded
        // connection - possibly this one, possibly another - disconnects it.
        // Used exclusively by the JWT-validated WebSocket handshake: a
        // successful login is a deliberate, authenticated act of claiming
        // this account's single live session, so it always wins against
        // whatever connection existed before it (preventing multi-boxing),
        // rather than being blocked by a lock a dropped connection simply
        // never got to release.
        public async Task<string> ForceAcquireAndEvictAsync(long playerId)
        {
            string token = Guid.NewGuid().ToString("N");

            if (!_redis.IsConnected)
            {
                // No cross-pod eviction possible without Redis - the caller's
                // own same-pod _connectedClients replacement still applies.
                return token;
            }

            try
            {
                RedisKey key = LockKey(playerId);
                await _redis.GetDatabase().StringSetAsync(key, token, TimeSpan.FromMilliseconds(LeaseMilliseconds));

                var subscriber = _redis.GetSubscriber();
                await subscriber.PublishAsync(RedisChannel.Literal(EvictionChannel), $"{playerId}:{token}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redis force-acquire/evict failed for player {playerId}: {ex.Message}");
            }

            return token;
        }

        public async Task<bool> RenewAsync(long playerId, string token)
        {
            if (!_redis.IsConnected || string.IsNullOrEmpty(token))
            {
                return false;
            }

            try
            {
                RedisResult result = await _redis.GetDatabase().ScriptEvaluateAsync(
                    RenewScript,
                    new RedisKey[] { LockKey(playerId) },
                    new RedisValue[] { token, LeaseMilliseconds });

                return (int)result == 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redis player lock renewal failed for player {playerId}: {ex.Message}");
                return false;
            }
        }

        public async Task ReleaseAsync(long playerId, string token)
        {
            if (!_redis.IsConnected || string.IsNullOrEmpty(token))
            {
                return;
            }

            try
            {
                await _redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, new RedisKey[] { LockKey(playerId) }, new RedisValue[] { token });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Redis player lock release failed for player {playerId}: {ex.Message}");
            }
        }

        private static RedisKey LockKey(long playerId) => $"lock:player:{playerId}";
    }
}
