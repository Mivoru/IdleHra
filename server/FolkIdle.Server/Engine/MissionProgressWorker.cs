using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Drains <see cref="MissionEvents"/> into PlayerMissionCounters: one
    /// coalesced upsert per cycle, plus an hourly prune of old days.
    /// </summary>
    public class MissionProgressWorker
    {
        public const int MaxEventsPerCycle = 5000;

        /// <summary>Counter rows older than this many days are deleted. The pass reads PlayerMissionDays, never these.</summary>
        public const int CounterRetentionDays = 3;

        /// <summary>A counter never goes past this, so a runaway writer cannot overflow the int.</summary>
        public const int CountCeiling = 1_000_000;

        private readonly IServiceProvider _serviceProvider;
        private CancellationTokenSource _cts = new();
        private long _written;
        private long _failed;
        private long _lastReportMs;
        private long _lastPruneMs;

        public MissionProgressWorker(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ExecuteAsync(_cts.Token));
            Console.WriteLine("Mission progress worker started.");
        }

        public void StopCron() => _cts.Cancel();

        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Modul: the try opens BEFORE the delay and before
                // DrainOneCycleAsync's CreateScope, where a connection is
                // acquired and a refused one throws (CronWorkerGuardTests).
                try
                {
                    await Task.Delay(1000, stoppingToken);
                    await DrainOneCycleAsync();
                    await PruneIfDueAsync();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failed);
                    Console.WriteLine($"Mission progress cycle failed: {ex.Message}");
                }

                ReportHeartbeat();
            }

            Console.WriteLine("Mission progress worker STOPPED.");
        }

        /// <summary>
        /// One budgeted drain. Returns the number of events taken off the queue.
        /// Tests call it directly instead of waiting for the loop.
        /// </summary>
        internal async Task<int> DrainOneCycleAsync()
        {
            // Modul: A BUDGET, NOT `while (TryDequeue)`. The depth is read once,
            // so a producer that never pauses cannot keep the cycle from ending
            // (server CLAUDE.md, "unbounded drain").
            int budget = Math.Min(MissionEvents.Queue.Count, MaxEventsPerCycle);
            if (budget == 0) return 0;

            var sums = new Dictionary<(long PlayerId, int DayKey, short Action, short Detail), long>();
            for (int i = 0; i < budget && MissionEvents.Queue.TryDequeue(out var e); i++)
            {
                var key = (e.PlayerId, e.DayKey, (short)e.Action, e.Detail);
                sums[key] = sums.TryGetValue(key, out long existing) ? existing + e.Amount : e.Amount;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await UpsertAsync(db, sums);
                Interlocked.Add(ref _written, sums.Count);
            }
            catch (Exception ex)
            {
                // Modul: DROPPED, not re-queued. A re-queued batch that fails on
                // a bad row would fail every cycle and starve the rest. Losing a
                // few seconds of mission progress in a database outage is the
                // smaller harm, and the heartbeat counts it.
                Interlocked.Increment(ref _failed);
                Console.WriteLine($"Mission progress dropped {sums.Count} counters: {ex.Message}");
            }

            return budget;
        }

        internal static async Task UpsertAsync(FolkIdleDbContext db, IReadOnlyDictionary<(long PlayerId, int DayKey, short Action, short Detail), long> sums)
        {
            if (sums.Count == 0) return;
            var keys = sums.Keys.ToArray();
            await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""PlayerMissionCounters"" (""PlayerId"", ""DayKey"", ""Action"", ""Detail"", ""Count"")
                  SELECT * FROM unnest(@ids, @days, @actions, @details, @counts)
                  ON CONFLICT (""PlayerId"", ""DayKey"", ""Action"", ""Detail"")
                  DO UPDATE SET ""Count"" = LEAST(""PlayerMissionCounters"".""Count"" + EXCLUDED.""Count"", @ceiling)",
                new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = keys.Select(k => k.PlayerId).ToArray() },
                new NpgsqlParameter("days", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = keys.Select(k => k.DayKey).ToArray() },
                new NpgsqlParameter("actions", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = keys.Select(k => k.Action).ToArray() },
                new NpgsqlParameter("details", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = keys.Select(k => k.Detail).ToArray() },
                new NpgsqlParameter("counts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = keys.Select(k => (int)Math.Min(CountCeiling, sums[k])).ToArray() },
                new NpgsqlParameter("ceiling", NpgsqlDbType.Integer) { Value = CountCeiling });
        }

        private async Task PruneIfDueAsync()
        {
            long nowMs = Environment.TickCount64;
            if (_lastPruneMs != 0 && nowMs - _lastPruneMs < 3_600_000) return;
            _lastPruneMs = nowMs;

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            int cutoff = (int)DailyLoginRewardEngine.ToDateKey(GameClock.UtcNow) - CounterRetentionDays;
            await db.PlayerMissionCounters.Where(c => c.DayKey < cutoff).ExecuteDeleteAsync();
        }

        private void ReportHeartbeat()
        {
            // UNCONDITIONAL, once a minute with the depth: a quiet worker cannot
            // be told apart from a dead one otherwise.
            long nowMs = Environment.TickCount64;
            if (nowMs - _lastReportMs < 60_000) return;
            _lastReportMs = nowMs;
            Console.WriteLine(
                $"Missions: {Interlocked.Exchange(ref _written, 0)} counters written, "
                + $"{Interlocked.Exchange(ref _failed, 0)} failed cycles, "
                + $"{MissionEvents.Queue.Count} still queued");
        }
    }
}
