using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace FolkIdle.Server.Engine
{
    public sealed class LeaderboardCronEngine
    {
        private const int TickIntervalMs = 300000; // 5 minutes
        private readonly IServiceProvider _serviceProvider;
        private readonly IConnectionMultiplexer _redis;
        private bool _isRunning;

        public LeaderboardCronEngine(IServiceProvider serviceProvider, IConnectionMultiplexer redis)
        {
            _serviceProvider = serviceProvider;
            _redis = redis;
        }

        public void StartCron()
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;
            _ = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            while (_isRunning)
            {
                try
                {
                    await SyncLeaderboardsAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"LeaderboardCronEngine failed: {ex.Message}");
                }

                await Task.Delay(TickIntervalMs);
            }
        }

        private async Task SyncLeaderboardsAsync()
        {
            var dbRedis = _redis.GetDatabase();

            // 1. Acquire Distributed Lock
            string lockKey = "lock:leaderboard:sync";
            string lockToken = Guid.NewGuid().ToString();
            
            // SET NX PX 10000 (10 seconds)
            bool acquired = await dbRedis.StringSetAsync(lockKey, lockToken, TimeSpan.FromSeconds(10), When.NotExists);
            
            if (!acquired)
            {
                // Another pod is handling the sync
                return;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                // We read uncommitted / read-only since it's an aggregation
                await using var transaction = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await dbContext.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var topPlayers = await dbContext.PlayerRecords
                    .AsNoTracking()
                    .Where(p => !p.IsQuarantined && !p.Quarantine_Active)
                    .OrderByDescending(p => p.CurrentXp)
                    .ThenByDescending(p => p.CurrentLevel)
                    .Take(10000) // limit to top 10000 to prevent OOM
                    .Select(p => new { p.Id, p.CurrentXp })
                    .ToListAsync();

                await transaction.CommitAsync();

                // 2. Stream to Staging ZSET
                string stagingKey = "leaderboard:mastery:staging";
                string prodKey = "leaderboard:mastery";
                
                await dbRedis.KeyDeleteAsync(stagingKey);

                var entries = new SortedSetEntry[topPlayers.Count];
                for (int i = 0; i < topPlayers.Count; i++)
                {
                    entries[i] = new SortedSetEntry(topPlayers[i].Id, topPlayers[i].CurrentXp);
                }

                if (entries.Length > 0)
                {
                    // Batch ZADD
                    await dbRedis.SortedSetAddAsync(stagingKey, entries);
                }

                // 3. Atomic RENAME
                await dbRedis.KeyRenameAsync(stagingKey, prodKey);
            }
            finally
            {
                // Release the lock if we still hold it (we use a script for safety)
                var script = @"
                    if redis.call('get', KEYS[1]) == ARGV[1] then
                        return redis.call('del', KEYS[1])
                    else
                        return 0
                    end";
                await dbRedis.ScriptEvaluateAsync(script, new RedisKey[] { lockKey }, new RedisValue[] { lockToken });
            }
        }
    }
}
