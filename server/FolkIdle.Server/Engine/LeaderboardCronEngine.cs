using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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
                    await SyncGuildWeeklyLeaderboardsAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"LeaderboardCronEngine failed: {ex.Message}");
                }

                await Task.Delay(TickIntervalMs);
            }
        }

        /// <summary>One player's ranking inputs, straight off the query.</summary>
        public sealed class LeaderboardRow
        {
            public long PlayerId { get; set; }
            public int Level { get; set; }
            public int HardestMonsterId { get; set; }
            public int KillsOfHardest { get; set; }
        }

        /// <summary>
        /// Three ordered keys packed into the one double a Redis sorted set
        /// gives us: level, then hardest monster, then kills of it.
        ///
        /// A double carries 53 bits of exact integer precision - about 9e15 -
        /// and every field has to fit inside that TOGETHER, not merely look
        /// small on its own.
        ///
        /// Modul: the first version of this multiplied level by 1e12 and
        /// clamped it at 9,999, which is 1e16 - past the exact range, where two
        /// genuinely different players collapse onto one number and the order
        /// between them becomes whatever Redis feels like. The comment above it
        /// even said "so level is clamped", which was true and did not help.
        /// LeaderboardRankingTests pins the widest possible score.
        ///
        /// Four digits for the monster id is generous - the canonical ladder
        /// ends at 115 - and six for kills, which nobody will exceed on one
        /// monster. The widest score is about 1e14.
        /// </summary>
        internal static double CompositeScore(LeaderboardRow row)
        {
            long level = Math.Clamp(row.Level, 0, 9_999);
            long hardest = Math.Clamp(row.HardestMonsterId, 0, 9_999);
            long kills = Math.Clamp(row.KillsOfHardest, 0, 999_999);
            return (level * 10_000_000_000L) + (hardest * 1_000_000L) + kills;
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

                // Modul: RANKED BY LEVEL, THEN BY HOW FAR THEY HAVE GOT.
                //
                // This ordered by raw XP, which is nearly the same thing as
                // level and is worse at saying it - two players on level 60
                // were separated by whichever happened to be further into the
                // bar, which is minutes of play and reads as noise.
                //
                // Level first, because that is the number everyone already
                // compares. Then the HARDEST MONSTER they have ever put down,
                // which is the actual measure of progress in a game gated by
                // gear rather than by level - the monster ladder is one
                // continuous curve, so a higher id is strictly a harder fight.
                // Then, between two players stuck on the same wall, how many
                // times they have beaten it.
                //
                // Raw SQL and a LATERAL join: "the highest monster id this
                // player has a kill for, and its kill count" is one index seek
                // per player, and the LINQ shape for it is a group-by followed
                // by a self-join that reads worse and plans worse.
                var topPlayers = await dbContext.Database
                    .SqlQueryRaw<LeaderboardRow>(@"
                        SELECT p.""Id"" AS ""PlayerId"",
                               p.""CurrentLevel"" AS ""Level"",
                               COALESCE(m.""MonsterId"", 0) AS ""HardestMonsterId"",
                               COALESCE(m.""KillCount"", 0) AS ""KillsOfHardest""
                        FROM ""PlayerRecords"" p
                        LEFT JOIN LATERAL (
                            SELECT c.""MonsterId"", c.""KillCount""
                            FROM ""monster_codex_entries"" c
                            WHERE c.""PlayerId"" = p.""Id"" AND c.""KillCount"" >= 1
                            ORDER BY c.""MonsterId"" DESC
                            LIMIT 1
                        ) m ON TRUE
                        WHERE NOT p.""IsQuarantined"" AND NOT p.""Quarantine_Active""
                        ORDER BY p.""CurrentLevel"" DESC,
                                 COALESCE(m.""MonsterId"", 0) DESC,
                                 COALESCE(m.""KillCount"", 0) DESC
                        LIMIT 10000")
                    .ToListAsync();

                await transaction.CommitAsync();

                // 2. Stream to Staging ZSET
                string stagingKey = "leaderboard:mastery:staging";
                string prodKey = "leaderboard:mastery";
                
                await dbRedis.KeyDeleteAsync(stagingKey);

                var entries = new SortedSetEntry[topPlayers.Count];
                for (int i = 0; i < topPlayers.Count; i++)
                {
                    entries[i] = new SortedSetEntry(topPlayers[i].PlayerId, CompositeScore(topPlayers[i]));
                }

                if (entries.Length > 0)
                {
                    // Batch ZADD
                    await dbRedis.SortedSetAddAsync(stagingKey, entries);

                    // 3. Atomic RENAME
                    await dbRedis.KeyRenameAsync(stagingKey, prodKey);
                }
                else
                {
                    // Modul: leaderboard empty-set fix, 2026-08-01.
                    //
                    // RENAME on a key that does not exist throws "ERR no such
                    // key", and with zero qualifying players the staging key was
                    // never created - it had just been deleted a few lines up.
                    // So an empty result set did not produce an empty
                    // leaderboard, it threw and aborted the whole sync pass,
                    // taking the guild leaderboard below down with it. Seen on
                    // every boot of a fresh database.
                    //
                    // Deleting the production key is the correct empty state:
                    // no players ranked means no ranking, not last cycle's
                    // ranking kept alive forever.
                    await dbRedis.KeyDeleteAsync(prodKey);
                }

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
                await dbRedis.KeyRenameAsync(stagingKey, prodKey);
            }
            else
            {
                // Same empty-set fault as the player leaderboard above - see
                // that comment. A server with no guilds yet hit this on every
                // sync pass.
                await dbRedis.KeyDeleteAsync(prodKey);
            }
        }
    
        private async Task SyncGuildWeeklyLeaderboardsAsync()
        {
            var dbRedis = _redis.GetDatabase();

            string lastSyncKey = "leaderboard:guild:weekly_last_sync";
            var lastSyncVal = await dbRedis.StringGetAsync(lastSyncKey);
            
            DateTime now = DateTime.UtcNow;
            
            // Sync happens on Monday (as 0). Wait, DayOfWeek.Monday is 1. Let's do Sunday midnight or Monday midnight.
            // A simple approach: we store the week number. "2026-W32"
            var cal = System.Globalization.DateTimeFormatInfo.CurrentInfo.Calendar;
            int weekOfYear = cal.GetWeekOfYear(now, System.Globalization.CalendarWeekRule.FirstDay, DayOfWeek.Monday);
            string currentWeekStr = $"{now.Year}-W{weekOfYear}";
            
            if (lastSyncVal.HasValue && lastSyncVal.ToString() == currentWeekStr)
            {
                return; // Already processed this week
            }
            
            string lockKey = "lock:leaderboard:guild_weekly_sync";
            string lockToken = Guid.NewGuid().ToString();
            
            bool acquired = await dbRedis.StringSetAsync(lockKey, lockToken, TimeSpan.FromSeconds(30), StackExchange.Redis.When.NotExists);
            if (!acquired) return;
            
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<FolkIdle.Server.Models.FolkIdleDbContext>();
                
                await using var transaction = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                
                var guilds = await dbContext.GuildRecords.ToListAsync();
                foreach (var guild in guilds)
                {
                    var members = await dbContext.GuildMembers
                        .Where(m => m.GuildId == guild.Id && m.WeeklyContributionPoints > 0)
                        .OrderByDescending(m => m.WeeklyContributionPoints)
                        .Take(3)
                        .ToListAsync();
                        
                    if (members.Count() > 0 && guild.TotalGoldContributed > 0)
                    {
                        long pool = guild.TotalGoldContributed / 2;
                        guild.TotalGoldContributed -= pool;
                        
                        long[] cuts = { (long)(pool * 0.25), (long)(pool * 0.15), (long)(pool * 0.10) };
                        
                        for (int i = 0; i < members.Count() && i < 3; i++)
                        {
                            long payout = cuts[i];
                            if (payout > 0)
                            {
                                var goldRow = await dbContext.CommodityRecords.FirstOrDefaultAsync(c => c.PlayerId == members[i].PlayerId && c.ItemId == "gold");
                                if (goldRow != null)
                                {
                                    goldRow.Quantity += payout;
                                }
                                else
                                {
                                    dbContext.CommodityRecords.Add(new FolkIdle.Server.Models.CommodityRecord { PlayerId = members[i].PlayerId, ItemId = "gold", Quantity = payout });
                                }
                            }
                        }
                    }
                }
                
                // Reset everyone's points
                await dbContext.Database.ExecuteSqlRawAsync("UPDATE \"GuildMembers\" SET \"WeeklyContributionPoints\" = 0");
                
                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                
                await dbRedis.StringSetAsync(lastSyncKey, currentWeekStr);
            }
            finally
            {
                var currentLock = await dbRedis.StringGetAsync(lockKey);
                if (currentLock == lockToken)
                {
                    await dbRedis.KeyDeleteAsync(lockKey);
                }
            }
        }
}
}
