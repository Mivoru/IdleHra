using System;
using System.Threading.Tasks;
using StackExchange.Redis;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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

        // Modul: Play Mode audit fix. This previously returned false (=
        // "renewal failed, force-disconnect the session") whenever Redis
        // was unreachable, exactly the same condition ForceAcquireAndEvictAsync
        // above already treats as fail-open ("no cross-pod eviction
        // possible without Redis"). The caller (NetworkBroadcastSystem.
        // RunRedisLockRenewalAsync) cannot distinguish "Redis is down" from
        // "another pod legitimately stole this lock" - both looked
        // identical to it - so every live session was force-disconnected
        // on every ~10s renewal tick whenever Redis was unavailable,
        // regardless of whether anything had actually superseded it.
        // Confirmed live: this was the actual root cause of a WebSocket
        // command (Forge crafting, Village building upgrades) silently
        // never taking effect - the connection was being killed and
        // silently reconnected (post- WS-reconnect-fix) or just silently
        // killed (pre-fix) every 10 seconds, unrelated to anything about
        // the command itself. Only a successful script execution that
        // explicitly finds a different token now counts as "genuinely
        // superseded" - Redis being unreachable, or any other transient
        // error contacting it, fails open instead (treat the lock as
        // still ours), matching this server's stated design everywhere
        // else (see docker-compose.yml: Redis is an optional write-behind
        // cache/lock, not a required dependency).
        public async Task<bool> RenewAsync(long playerId, string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            if (!_redis.IsConnected)
            {
                return true;
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
                return true;
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
