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
    public readonly struct DistributedLockLease
    {
        public readonly bool Acquired;
        public readonly RedisKey Key;
        public readonly RedisValue Token;
        public readonly long FencingToken;

        public DistributedLockLease(bool acquired, RedisKey key, RedisValue token, long fencingToken)
        {
            Acquired = acquired;
            Key = key;
            Token = token;
            FencingToken = fencingToken;
        }
    }

    public sealed class DistributedLockManager
    {
        private const string ReleaseScript = "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) else return 0 end";
        private readonly IConnectionMultiplexer _redis;

        public DistributedLockManager(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        public async Task<DistributedLockLease> AcquireGuildMatchLockAsync(Guid matchUuid, int leaseMilliseconds = 5000)
        {
            RedisKey key = $"redlock:guild_match:{matchUuid:N}";
            RedisValue token = Guid.NewGuid().ToString("N");
            bool acquired = await _redis.GetDatabase().StringSetAsync(key, token, TimeSpan.FromMilliseconds(leaseMilliseconds), When.NotExists);
            long fencingToken = 0L;
            if (acquired)
            {
                fencingToken = await _redis.GetDatabase().StringIncrementAsync($"redlock:guild_match:fence:{matchUuid:N}");
            }

            return new DistributedLockLease(acquired, key, token, fencingToken);
        }

        public async Task ReleaseAsync(DistributedLockLease lease)
        {
            if (!lease.Acquired)
            {
                return;
            }

            await _redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, new[] { lease.Key }, new[] { lease.Token });
        }
    }
}
