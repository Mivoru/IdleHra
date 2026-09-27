using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The steps of the new-player funnel. The NUMBERS are stored in
    /// player_funnel_events.Step and read by docs/ops/funnel.sql - they are
    /// identity, never renumber one.
    /// </summary>
    public enum FunnelStep : short
    {
        Registered = 1,
        FirstKill = 2,
        FirstEquip = 3,
        FirstCraft = 4,
        OnboardingDone = 5,
        Region1Boss = 6,
        Level5 = 7,
        Level10 = 8,
        Level20 = 9,
        JoinedGuild = 10,
        ReturnedD1 = 11,
        ReturnedD7 = 12,
    }

    /// <summary>
    /// Records the first time each player reaches each funnel step (task 39,
    /// plan item 4).
    ///
    /// Modul: 61 ACCOUNTS, ONE AT LEVEL 10, AND NOTHING SAID WHERE THE OTHER 60
    /// STOPPED (2026-09-25). This is the instrument for that question, and it
    /// is built so that recording costs a hot path nothing:
    ///
    ///   - producers only ENQUEUE (<see cref="Record"/>), which is safe on the
    ///     10 Hz tick thread - nothing here opens a connection on the caller;
    ///   - a per-session guard makes the kill path enqueue ONCE per session,
    ///     not once per kill;
    ///   - the worker below drains in budgeted batches, one multi-row
    ///     <c>INSERT ... ON CONFLICT DO NOTHING</c> each, so a repeat is free
    ///     and the first occurrence is the row that stays.
    ///
    /// ONE WRITER PER STEP. Every <c>FunnelStep.X</c> below appears at exactly
    /// one call site outside this file (registration has two, one per account
    /// route). Grep before adding a second: two writers for one step is how a
    /// funnel starts counting something nobody meant.
    ///
    ///   1 registered       AuthenticationEngine, both `new PlayerRecord` sites
    ///   2 first_kill       ProgressionEngine.ProcessMonsterDeath (live) and
    ///                      OfflineSimulationEngine (away) - one line each
    ///   3 first_equip      EquipmentSlotEngine.EquipItemAsync, after commit
    ///   4 first_craft      CraftingEngine.ExecuteCraftingAsync, after commit
    ///   5 onboarding_done  RecordCheckpoint (see there for why not the seen-set)
    ///   6 region1_boss     SimulationEngine, the first-clear branch
    ///   7-9 level_5/10/20  RecordCheckpoint, from FlushState
    ///   10 joined_guild    GuildManagementEngine.PublishJoined
    ///   11-12 returned     BeginSession, from the Login dispatch
    /// </summary>
    public class FunnelRecorder
    {
        /// <summary>
        /// One queued step. <see cref="Step"/> 0 is a LOGIN PROBE, not a step:
        /// the worker turns it into returned_d1 / returned_d7 against the
        /// player's own registered row, because only the database knows when
        /// the account was created.
        /// </summary>
        public readonly struct FunnelEventRequest
        {
            public readonly long PlayerId;
            public readonly short Step;
            public readonly DateTime At;

            public FunnelEventRequest(long playerId, short step, DateTime at)
            {
                PlayerId = playerId;
                Step = step;
                At = at;
            }
        }

        private const short LoginProbe = 0;

        // Modul: STATIC, like CombatLootEngine.DropRequestQueue, because the
        // producers are static engines with no handle on this instance. The
        // same consequence follows: a test that starts this worker must stop it
        // in a finally, or it drains another test's events into its own
        // database (CLAUDE.md "static queues").
        public static readonly ConcurrentQueue<FunnelEventRequest> Queue = new();

        // Modul: THE PER-SESSION GUARD. A bitmask per player of the steps
        // already enqueued since that player's last login. ProcessMonsterDeath
        // calls Record on every kill; this is what makes all but the first a
        // dictionary read. Cleared by BeginSession, so each login re-offers
        // each step once - which is also what makes a batch lost to a database
        // fault heal itself on the player's next session.
        private static readonly ConcurrentDictionary<long, int> _enqueuedThisSession = new();

        /// <summary>The most events one drain cycle takes, whatever the depth.</summary>
        public const int MaxEventsPerCycle = 2000;

        public static readonly TimeSpan ReturnedD1From = TimeSpan.FromHours(24);
        public static readonly TimeSpan ReturnedD1To = TimeSpan.FromHours(48);
        public static readonly TimeSpan ReturnedD7From = TimeSpan.FromDays(7);
        public static readonly TimeSpan ReturnedD7To = TimeSpan.FromDays(8);

        private readonly IServiceProvider _serviceProvider;
        private CancellationTokenSource _cts = new();

        private static long _written;
        private static long _probed;
        private static long _failed;
        private long _lastReportMs;

        public FunnelRecorder(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// Enqueue <paramref name="step"/> for <paramref name="playerId"/>,
        /// once per session. Safe on the tick thread.
        /// </summary>
        public static void Record(long playerId, FunnelStep step)
        {
            if (playerId <= 0) return;

            // Modul: REGISTRATION BYPASSES THE GUARD. It happens once per
            // account by construction, before any session exists, and the
            // guard is keyed by player id in a static map - so a stale entry
            // for a reused id (a test database, a restored backup) could
            // otherwise swallow the one row that defines the cohort.
            if (step == FunnelStep.Registered)
            {
                Queue.Enqueue(new FunnelEventRequest(playerId, (short)step, DateTime.UtcNow));
                return;
            }

            int bit = 1 << (int)step;
            while (true)
            {
                int seen = _enqueuedThisSession.GetOrAdd(playerId, 0);
                if ((seen & bit) != 0) return;
                if (_enqueuedThisSession.TryUpdate(playerId, seen | bit, seen)) break;
            }

            Queue.Enqueue(new FunnelEventRequest(playerId, (short)step, DateTime.UtcNow));
        }

        /// <summary>
        /// A login: re-arms the per-session guard and asks the worker whether
        /// this login is a day-1 or day-7 return.
        /// </summary>
        public static void BeginSession(long playerId)
        {
            if (playerId <= 0) return;
            _enqueuedThisSession.TryRemove(playerId, out _);
            Queue.Enqueue(new FunnelEventRequest(playerId, LoginProbe, DateTime.UtcNow));
        }

        /// <summary>
        /// The steps a durable checkpoint can see. Called by FlushState after it
        /// commits.
        ///
        /// Modul: LEVELS ARE HOOKED HERE ON PURPOSE. Three paths grow a level -
        /// a kill, warp, and offline catch-up (CLAUDE.md) - and each has been
        /// forgotten by some rule before. The checkpoint is the one place all
        /// three pass through, so one hook covers them all. A level reached
        /// within five minutes of logout still lands, because Logout flushes
        /// too; `At` is the checkpoint's time, so it can trail the level by up
        /// to one checkpoint interval.
        ///
        /// ONBOARDING_DONE IS HERE TOO, and not in the onboarding seen-set the
        /// plan pointed at: that set holds tier-two and tier-three explanation
        /// ids, which have no "final" one and are baselined in bulk for a
        /// returning player. What the client calls onboarding COMPLETED
        /// (TutorialStep.Completed in client_web tutorialSteps.ts) is a
        /// predicate over the state packet - food in the larder, level 2, a
        /// weapon worn - so the server reads the same three facts off the
        /// state it is checkpointing. If that client rule changes, change this.
        /// </summary>
        public static void RecordCheckpoint(in TickStatePayload state)
        {
            if (state.PlayerId <= 0) return;

            if (state.CurrentLevel >= 5) Record(state.PlayerId, FunnelStep.Level5);
            if (state.CurrentLevel >= 10) Record(state.PlayerId, FunnelStep.Level10);
            if (state.CurrentLevel >= 20) Record(state.PlayerId, FunnelStep.Level20);

            long food = (long)state.Food1_Count + state.Food2_Count + state.Food3_Count;
            if (food > 0 && state.CurrentLevel >= 2 && state.EquippedWeaponId > 0)
            {
                Record(state.PlayerId, FunnelStep.OnboardingDone);
            }
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ExecuteAsync(_cts.Token));
            Console.WriteLine("Funnel recorder worker started.");
        }

        public void StopCron()
        {
            _cts.Cancel();
        }

        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Modul: the try opens BEFORE the delay and before
                // DrainOneCycleAsync's CreateScope - that is where a connection
                // is acquired and where a refused one throws. A guard that
                // started after it would guard nothing (CronWorkerGuardTests).
                try
                {
                    await Task.Delay(2000, stoppingToken);
                    await DrainOneCycleAsync();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failed);
                    Console.WriteLine($"Funnel recorder cycle failed: {ex.Message}");
                }

                ReportHeartbeat();
            }

            Console.WriteLine("Funnel recorder worker STOPPED.");
        }

        /// <summary>
        /// One budgeted drain: at most the depth seen at the top of the cycle,
        /// and at most <see cref="MaxEventsPerCycle"/>. Returns the number of
        /// events taken off the queue.
        /// </summary>
        internal async Task<int> DrainOneCycleAsync()
        {
            // Modul: A BUDGET, NOT `while (TryDequeue)`. The depth is read
            // ONCE, so a producer that never pauses cannot keep this loop from
            // ending (CLAUDE.md "unbounded drain").
            int budget = Math.Min(Queue.Count, MaxEventsPerCycle);
            if (budget == 0) return 0;

            var steps = new Dictionary<(long, short), DateTime>();
            var probes = new List<FunnelEventRequest>();
            for (int i = 0; i < budget && Queue.TryDequeue(out var request); i++)
            {
                if (request.Step == LoginProbe)
                {
                    probes.Add(request);
                    continue;
                }

                // Coalesce: the earliest sighting of a step is the one that counts.
                var key = (request.PlayerId, request.Step);
                if (!steps.TryGetValue(key, out var existing) || request.At < existing)
                {
                    steps[key] = request.At;
                }
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                if (steps.Count > 0)
                {
                    long[] ids = steps.Keys.Select(k => k.Item1).ToArray();
                    short[] stepIds = steps.Keys.Select(k => k.Item2).ToArray();
                    DateTime[] ats = steps.Values.Select(AsUtc).ToArray();

                    int inserted = await db.Database.ExecuteSqlRawAsync(
                        @"INSERT INTO player_funnel_events (""PlayerId"", ""Step"", ""At"")
                          SELECT * FROM unnest(@ids, @steps, @ats)
                          ON CONFLICT (""PlayerId"", ""Step"") DO NOTHING",
                        new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = ids },
                        new NpgsqlParameter("steps", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = stepIds },
                        new NpgsqlParameter("ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = ats });
                    Interlocked.Add(ref _written, inserted);
                }

                // Probes run AFTER the steps, so a registration and a login in
                // the same cycle see each other - and a same-cycle login is not
                // a return anyway.
                if (probes.Count > 0)
                {
                    long[] ids = probes.Select(p => p.PlayerId).ToArray();
                    DateTime[] ats = probes.Select(p => AsUtc(p.At)).ToArray();

                    int inserted = await db.Database.ExecuteSqlRawAsync(
                        @"INSERT INTO player_funnel_events (""PlayerId"", ""Step"", ""At"")
                          SELECT r.""PlayerId"", w.step, l.at
                          FROM unnest(@ids, @ats) AS l(pid, at)
                          JOIN player_funnel_events r ON r.""PlayerId"" = l.pid AND r.""Step"" = @registered
                          CROSS JOIN (VALUES (@d1::smallint, @d1From::interval, @d1To::interval),
                                             (@d7::smallint, @d7From::interval, @d7To::interval)) AS w(step, lo, hi)
                          WHERE l.at - r.""At"" >= w.lo AND l.at - r.""At"" < w.hi
                          ON CONFLICT (""PlayerId"", ""Step"") DO NOTHING",
                        new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = ids },
                        new NpgsqlParameter("ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = ats },
                        new NpgsqlParameter("registered", NpgsqlDbType.Smallint) { Value = (short)FunnelStep.Registered },
                        new NpgsqlParameter("d1", NpgsqlDbType.Smallint) { Value = (short)FunnelStep.ReturnedD1 },
                        new NpgsqlParameter("d1From", NpgsqlDbType.Interval) { Value = ReturnedD1From },
                        new NpgsqlParameter("d1To", NpgsqlDbType.Interval) { Value = ReturnedD1To },
                        new NpgsqlParameter("d7", NpgsqlDbType.Smallint) { Value = (short)FunnelStep.ReturnedD7 },
                        new NpgsqlParameter("d7From", NpgsqlDbType.Interval) { Value = ReturnedD7From },
                        new NpgsqlParameter("d7To", NpgsqlDbType.Interval) { Value = ReturnedD7To });
                    Interlocked.Add(ref _probed, inserted);
                }
            }
            catch (Exception ex)
            {
                // Modul: DROPPED, not re-queued. This is telemetry: a batch
                // that failed is counted and logged, and the per-session guard
                // re-offers every recurring step at the player's next login. Only
                // a registration lost here is lost for good - accepted, and
                // visible in the heartbeat's failed count.
                Interlocked.Increment(ref _failed);
                Console.WriteLine($"Funnel recorder dropped {steps.Count} steps and {probes.Count} login probes: {ex.Message}");
            }

            return budget;
        }

        private static DateTime AsUtc(DateTime at) =>
            at.Kind == DateTimeKind.Utc ? at : DateTime.SpecifyKind(at.ToUniversalTime(), DateTimeKind.Utc);

        private void ReportHeartbeat()
        {
            // Modul: UNCONDITIONAL, one line a minute, with the queue depth -
            // a quiet worker cannot be told apart from a dead one otherwise
            // (see CombatLootEngine's heartbeat for the day that cost).
            long nowMs = Environment.TickCount64;
            if (nowMs - _lastReportMs < 60_000) return;
            _lastReportMs = nowMs;

            Console.WriteLine(
                $"Funnel: {Interlocked.Exchange(ref _written, 0)} steps written, "
                + $"{Interlocked.Exchange(ref _probed, 0)} returns recorded, "
                + $"{Interlocked.Exchange(ref _failed, 0)} failed cycles, "
                + $"{Queue.Count} still queued");
        }
    }
}
