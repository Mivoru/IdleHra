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

                // Modul: Comprehensive Game System Audit, Part 3.2. Global
                // guild leaderboard - previously nothing anywhere ranked
                // guilds. Same staging-ZSET-plus-atomic-rename pipeline as
                // the player leaderboard above, scored by a combined
                // weight of guild progression tier and active war
                // placement: CurrentTier dominates (x10000) so a
                // higher-tier guild always outranks a lower-tier one, and
                // GuildMMR (the war matchmaking rating, baseline 1000)
                // breaks ties within a tier - the "combined weight of
                // Guild Level and active Guild War placement" the audit
                // requires, from columns that already exist.
                await SyncGuildLeaderboardAsync(dbRedis);
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

        // Modul: Comprehensive Game System Audit, Part 3.2. See the call
        // site's comment for the ranking-weight rationale. Runs under the
        // same distributed sync lock the player leaderboard holds.
        private async Task SyncGuildLeaderboardAsync(IDatabase dbRedis)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
            await dbContext.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

            var topGuilds = await dbContext.GuildRecords
                .AsNoTracking()
                .OrderByDescending(g => g.CurrentTier)
                .ThenByDescending(g => g.GuildMMR)
                .Take(1000)
                .Select(g => new { g.Id, g.CurrentTier, g.GuildMMR })
                .ToListAsync();

            await transaction.CommitAsync();

            string stagingKey = "leaderboard:guilds:staging";
            string prodKey = "leaderboard:guilds";

            await dbRedis.KeyDeleteAsync(stagingKey);

            var entries = new SortedSetEntry[topGuilds.Count];
            for (int i = 0; i < topGuilds.Count; i++)
            {
                double combinedScore = (double)topGuilds[i].CurrentTier * 10000.0 + topGuilds[i].GuildMMR;
                entries[i] = new SortedSetEntry(topGuilds[i].Id, combinedScore);
            }

            if (entries.Length > 0)
            {
                await dbRedis.SortedSetAddAsync(stagingKey, entries);
            }

            await dbRedis.KeyRenameAsync(stagingKey, prodKey);
        }
    }
}
