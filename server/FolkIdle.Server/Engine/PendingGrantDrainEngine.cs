using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    // Modul: THE RETRY HALF of the outbox CombatLootEngine and
    // OfflineSimulationEngine now write to on a failed grant. Guarded per
    // CronWorkerGuardTests' convention - the catch wraps the connection
    // acquisition, not just the loop around it, for the same reason
    // CombatLootEngine's does: that IS where Supabase's pooler throws.
    public class PendingGrantDrainEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerSessionRegistry;
        private CancellationTokenSource _cts = new();

        // Modul: A BUDGET, NOT "EVERY ELIGIBLE ROW" - see CombatLootEngine's
        // own recorded gathering-starvation trap. One cycle reads at most
        // this many rows; a bigger backlog drains across several cycles
        // instead of holding this worker (and the connection it is using)
        // for however long an unbounded backlog takes.
        public const int MaxGrantsPerDrainCycle = 100;

        public const int MaxAttempts = 10;
        private const long BaseBackoffMs = 15_000L;
        private const long MaxBackoffMs = 1_800_000L; // 30 minutes

        private static long _applied;
        private static long _failed;
        private static long _deadLettered;
        private long _lastReportMs;

        public PendingGrantDrainEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerSessionRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerSessionRegistry = playerSessionRegistry;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ExecuteAsync(_cts.Token));
            Console.WriteLine("Pending grant drain worker started.");
        }

        internal async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, stoppingToken);
                    await DrainOneCycleAsync();
                    ReportThroughput();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Same shape as CombatLootEngine's outer guard: nothing
                    // above should reach here, but the one thing this worker
                    // must never do is stop.
                    Console.WriteLine($"Pending grant drain cycle failed: {ex.Message}");
                }
            }

            Console.WriteLine("Pending grant drain worker STOPPED. Queued grants will not be retried until restart.");
        }

        internal async Task DrainOneCycleAsync()
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            for (int i = 0; i < MaxGrantsPerDrainCycle; i++)
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                // FOR UPDATE SKIP LOCKED: two overlapping containers during a
                // rolling deploy can poll at the same time without ever
                // claiming the same row - see the plan's Global Constraints.
                var row = await db.PendingGrants
                    .FromSqlInterpolated($@"
                        SELECT * FROM pending_grants
                        WHERE ""DeadLetteredAtEpochMs"" IS NULL AND ""NextAttemptAtEpochMs"" <= {nowMs}
                        ORDER BY ""Id"" LIMIT 1 FOR UPDATE SKIP LOCKED")
                    .SingleOrDefaultAsync();

                if (row == null) return; // nothing eligible - stop early, don't spin the budget for nothing.

                using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                try
                {
                    bool applied = await PendingGrantOutbox.TryApplyOneAsync(db, row);
                    if (!applied)
                    {
                        // Unrecognized PayloadKind - a version-skew bug, not
                        // transient. Dead-letter immediately rather than
                        // retrying something that can never succeed.
                        row.DeadLetteredAtEpochMs = nowMs;
                        row.LastError = $"unrecognized PayloadKind '{row.PayloadKind}'";
                        await db.SaveChangesAsync();
                        await transaction.CommitAsync();
                        Interlocked.Increment(ref _deadLettered);
                        continue;
                    }

                    // Modul: LIVE-SESSION NOTIFY (2026-09-19 revalidation,
                    // Task 4 Step 1b). Without this, a player who is online
                    // when a delayed grant lands sees nothing until their
                    // next relogin - the write above goes straight to the
                    // database with no signal to a live TickStatePayload.
                    // Reuses two mechanisms that already exist rather than
                    // adding a new notification type or wire field:
                    //
                    //   Gold: ChestSaleGoldQueue/ChestSaleGoldNotification -
                    //   "the database row is already correct, the live
                    //   session's DISPLAYED total is what's behind." This
                    //   outbox already wrote CommodityRecords["gold"]
                    //   directly (TryApplyOneAsync, above), so only the
                    //   display needs to catch up - never RedisPendingGoldDelta,
                    //   or the checkpoint banks the same gold twice.
                    //
                    //   Materials and equipment: EnqueueCommandResult is what
                    //   PR #10 (task 23) wired every screen's REST cache
                    //   invalidation through - an ordinary Success result
                    //   causes processCommandResults to invalidate globally,
                    //   so the client refetches its owned-items/materials
                    //   view on its own. No new queue, no new wire field.
                    if (_playerSessionRegistry.IsPlayerOnline(row.PlayerId))
                    {
                        if (row.PayloadKind == PendingGrantPayloadKind.CommodityDeltas)
                        {
                            var deltas = JsonSerializer.Deserialize<Dictionary<string, long>>(row.PayloadJson)!;
                            if (deltas.TryGetValue("gold", out long goldGained) && goldGained > 0L)
                            {
                                _playerSessionRegistry.ChestSaleGoldQueue.Enqueue(
                                    new ChestSaleGoldNotification { PlayerId = row.PlayerId, GoldGained = goldGained });
                            }
                        }

                        _playerSessionRegistry.EnqueueCommandResult(
                            row.PlayerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);
                    }

                    db.PendingGrants.Remove(row);
                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();
                    Interlocked.Increment(ref _applied);
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    Interlocked.Increment(ref _failed);

                    // A fresh scope: the one above may be in a bad state
                    // after the exception above (especially if it came from
                    // the connection itself). Recording the failed attempt
                    // must not depend on the connection that just failed.
                    using var retryScope = _serviceProvider.CreateScope();
                    var retryDb = retryScope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                    int attempt = row.AttemptCount + 1;
                    long backoffMs = Math.Min(MaxBackoffMs, BaseBackoffMs * (long)Math.Pow(3, attempt));
                    long nextAttemptAt = nowMs + backoffMs;
                    bool giveUp = attempt >= MaxAttempts;

                    await retryDb.Database.ExecuteSqlInterpolatedAsync($@"
                        UPDATE pending_grants SET
                            ""AttemptCount"" = {attempt},
                            ""LastError"" = {Truncate(ex.Message, 512)},
                            ""NextAttemptAtEpochMs"" = {nextAttemptAt},
                            ""DeadLetteredAtEpochMs"" = {(giveUp ? (long?)nowMs : null)}
                        WHERE ""Id"" = {row.Id}");

                    if (giveUp) Interlocked.Increment(ref _deadLettered);

                    Console.WriteLine(
                        $"Pending grant {row.Id} (player {row.PlayerId}, {row.PayloadKind}) attempt {attempt} failed: {ex.Message}"
                        + (giveUp ? " - DEAD-LETTERED." : $" - retrying in {backoffMs / 1000}s."));
                }
            }
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

        private void ReportThroughput()
        {
            long nowMs = Environment.TickCount64;
            if (nowMs - _lastReportMs < 60_000) return;
            _lastReportMs = nowMs;

            long applied = Interlocked.Exchange(ref _applied, 0);
            long failed = Interlocked.Exchange(ref _failed, 0);
            long deadLettered = Interlocked.Exchange(ref _deadLettered, 0);
            if (applied == 0 && failed == 0 && deadLettered == 0) return;

            Console.WriteLine($"Pending grants: {applied} applied, {failed} retried, {deadLettered} dead-lettered this cycle.");
        }
    }
}
