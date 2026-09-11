using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Pays the weekly diamond reward for a place on the global board.
    ///
    /// Modul: RANK IS READ FROM THE SAME ZSET THE BOARD IS SERVED FROM, which
    /// is what makes the payout and the screen incapable of disagreeing.
    /// `LeaderboardCronEngine` rebuilds `leaderboard:mastery` every five
    /// minutes into a staging key and RENAMEs it into place, so reading it here
    /// is reading exactly what the player was shown.
    ///
    /// WHAT STOPS IT PAYING TWICE. Every player row carries
    /// `LeaderboardPayoutWeekKey`. A payout is written in the same transaction
    /// as the diamonds, so either both land or neither does, and a second pass
    /// in the same ISO week finds the key already equal and does nothing. The
    /// cron therefore runs often and cheaply rather than trying to fire exactly
    /// once at a particular instant - which is the only shape that survives a
    /// restart, a deploy, or a server that was simply off on Monday morning.
    ///
    /// WHAT STOPS IT BECOMING THE ECONOMY. Nothing here decides an amount.
    /// `LeaderboardTierRegistry.WeeklyDiamondsFor` does, and it refuses to pay
    /// at all until the ranked population is large enough for a rank to mean
    /// something - see the population floor there, and the measurement that
    /// forced it.
    /// </summary>
    public sealed class LeaderboardPayoutEngine
    {
        /// <summary>
        /// Modul: TEN MINUTES, and it is a POLL rather than a schedule. The
        /// week boundary is a fact about the calendar, not an appointment this
        /// process has to be awake for: whenever it next runs, it pays whoever
        /// has not been paid for the current week. A server that was down all
        /// weekend settles up when it comes back.
        /// </summary>
        private const int TickIntervalMs = 600_000;

        /// <summary>How deep into the board to pay. The widest tier is 1000.</summary>
        private const int PayoutDepth = 1000;

        private readonly IServiceProvider _serviceProvider;
        private readonly IConnectionMultiplexer _redis;
        private CancellationTokenSource? _cts;

        public LeaderboardPayoutEngine(IServiceProvider serviceProvider, IConnectionMultiplexer redis)
        {
            _serviceProvider = serviceProvider;
            _redis = redis;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ExecuteAsync(_cts.Token));
        }

        public void Stop() => _cts?.Cancel();

        /// <remarks>
        /// Modul: the catch wraps the CALL, not the inside of the cycle. Every
        /// StartCron loop here runs in a bare Task.Run, so one exception ends
        /// the task for the lifetime of the process - no log, no restart, no
        /// symptom except a feature that quietly stopped. CombatLootEngine lost
        /// its whole drain that way and equipment stopped dropping server-wide.
        /// The throw that does it is usually `CreateScope`/`CreateDbContext`
        /// refusing a pooled connection, which is why a try that starts after
        /// those is not a guard at all.
        /// </remarks>
        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PayCurrentWeekAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"LeaderboardPayoutEngine failed: {ex.Message}");
                }

                try
                {
                    await Task.Delay(TickIntervalMs, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Settles the given week. Internal rather than private so the test
        /// suite can run one cycle against a chosen instant instead of waiting
        /// ten minutes for a real one.
        /// </summary>
        /// <returns>How many players were paid.</returns>
        internal async Task<int> PayCurrentWeekAsync(DateTime utcNow, CancellationToken token = default)
        {
            int weekKey = DelveEngine.CurrentWeekKey(utcNow);

            var dbRedis = _redis.GetDatabase();

            // Modul: THE SAME LOCK PATTERN AS THE SYNC. One replica is the
            // documented deployment, but a payout is the one job where a second
            // process would be expensive rather than merely wasteful, and the
            // week key alone does not protect against two passes racing inside
            // the same instant.
            string lockKey = "lock:leaderboard:payout";
            string lockToken = Guid.NewGuid().ToString();
            bool acquired = await dbRedis.StringSetAsync(lockKey, lockToken, TimeSpan.FromMinutes(2), When.NotExists);
            if (!acquired) return 0;

            try
            {
                long rankedPopulation = await dbRedis.SortedSetLengthAsync("leaderboard:mastery");
                if (rankedPopulation <= 0) return 0;

                // Nothing on this board can earn anything yet - don't read a
                // thousand rows to discover it.
                if (LeaderboardTierRegistry.WeeklyDiamondsFor(1, (int)rankedPopulation) <= 0) return 0;

                var top = await dbRedis.SortedSetRangeByRankAsync(
                    "leaderboard:mastery", 0, PayoutDepth - 1, Order.Descending);

                if (top.Length == 0) return 0;

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                int paid = 0;

                for (int i = 0; i < top.Length; i++)
                {
                    if (token.IsCancellationRequested) break;

                    int rank = i + 1;
                    int diamonds = LeaderboardTierRegistry.WeeklyDiamondsFor(rank, (int)rankedPopulation);
                    if (diamonds <= 0) continue;

                    if (!long.TryParse(top[i].ToString(), out long playerId)) continue;

                    // Modul: ONE PLAYER PER TRANSACTION, and isolated.
                    //
                    // A single transaction over a thousand rows would make one
                    // bad row lose the whole week's payout, and would hold a
                    // write lock across a thousand round trips. Per-player also
                    // means the week key and the diamonds commit together for
                    // that player, which is the property that makes a retry
                    // safe - see the try/catch: a row that throws is skipped
                    // and picked up on the next pass, because its week key was
                    // never written.
                    try
                    {
                        await using var tx = await db.Database.BeginTransactionAsync(token);

                        var player = await db.PlayerRecords
                            .Where(p => p.Id == playerId && p.LeaderboardPayoutWeekKey != weekKey)
                            .FirstOrDefaultAsync(token);

                        if (player is null)
                        {
                            await tx.RollbackAsync(token);
                            continue;
                        }

                        player.PremiumDiamonds += diamonds;
                        player.LeaderboardPayoutWeekKey = weekKey;
                        player.LeaderboardPayoutRank = rank;

                        await db.SaveChangesAsync(token);
                        await tx.CommitAsync(token);
                        paid++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"LeaderboardPayoutEngine: player {playerId} rank {rank} failed: {ex.Message}");
                        db.ChangeTracker.Clear();
                    }
                }

                if (paid > 0)
                {
                    Console.WriteLine(
                        $"Leaderboard payout: week {weekKey}, {rankedPopulation} ranked, {paid} paid.");
                }

                return paid;
            }
            finally
            {
                // Best-effort release; the two-minute expiry is the real safety
                // net if this process dies holding it.
                try { await dbRedis.KeyDeleteAsync(lockKey); } catch { }
            }
        }
    }
}
