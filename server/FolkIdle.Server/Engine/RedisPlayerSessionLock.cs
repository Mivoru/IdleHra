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

        private readonly IConnectionMultiplexer _redis;

        public RedisPlayerSessionLock(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        public async Task<string?> TryAcquireAsync(long playerId)
        {
            if (!_redis.IsConnected)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 53, Value2 = 1, Timestamp = Environment.TickCount64 });
                return null;
            }

            string token = Guid.NewGuid().ToString("N");
            RedisKey key = LockKey(playerId);
            bool acquired = await _redis.GetDatabase().StringSetAsync(key, token, TimeSpan.FromMilliseconds(LeaseMilliseconds), When.NotExists);
            return acquired ? token : null;
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
