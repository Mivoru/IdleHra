using System;
using System.Collections.Concurrent;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Shared
{
    // Modul: larder. Where auto-eat kicks in for a player who has never
    // touched the slider. 30 percent leaves enough headroom for one more
    // monster hit to land before the heal resolves at 10Hz, without burning
    // food on scratches.
    public static class AutoEatDefaults
    {
        public const int ThresholdPct = 30;
    }

    // Modul: multi-slot simulation. The two starting values every character
    // slot hydrates with, named once so slot 1's long-standing literals and
    // slots 2-3's new ones cannot drift apart.
    public static class CharacterSlotDefaults
    {
        // Milli-HP, matching the engine's thousandths convention everywhere
        // else (the outbound packet divides by 1000).
        public const int MilliHp = 100000;
        public const int RequiredProgressTicks = 50;
    }

    public class StateCheckpointManager
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ConcurrentDictionary<long, TickStatePayload> _dirtyStates = new();
        private readonly RedisSessionCache? _redisSessionCache;

        private Action<long>? _forceDisconnectCallback;

        public void RegisterDisconnectCallback(Action<long> callback)
        {
            _forceDisconnectCallback = callback;
        }

        /// <summary>
        /// Gold mailed once per split-brain incident (owner decision D1,
        /// 2026-09-27), independent of how far apart the two epochs are.
        /// </summary>
        public const long SplitBrainCompensationGold = 1000;

        // Test-only observability (via InternalsVisibleTo): the most recent
        // fire-and-forget compensation, so a test can await it instead of
        // polling the mailbox.
        internal Task LastSplitBrainCompensation { get; private set; } = Task.CompletedTask;

        // Modul: task 42. Records the incident and mails only when THIS call
        // recorded it. The INSERT ... ON CONFLICT DO NOTHING and the mail share
        // one transaction, so a failed mail rolls the incident back and a later
        // refusal at the same DbEpoch can still pay it; a second refusal at the
        // same DbEpoch (the same stale session flushed again, or a retry) finds
        // the row and pays nothing. Never throws - it runs detached.
        internal async Task CompensateSplitBrainAsync(long playerId, long dbEpoch, long sessionEpoch)
        {
            string lockHolder = "unknown";
            try
            {
                var sessionLock = _serviceProvider.GetService<RedisPlayerSessionLock>();
                if (sessionLock != null)
                {
                    lockHolder = await sessionLock.PeekHolderAsync(playerId) ?? "none";
                }
            }
            catch
            {
                // Diagnostics only.
            }

            try
            {
                using var bgScope = _serviceProvider.CreateScope();
                var bgDb = bgScope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await using var bgTx = await bgDb.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

                int inserted = await bgDb.Database.ExecuteSqlRawAsync(
                    "INSERT INTO split_brain_incidents (\"PlayerId\", \"DbEpoch\", \"At\") VALUES ({0}, {1}, {2}) ON CONFLICT DO NOTHING",
                    playerId, dbEpoch, DateTimeOffset.UtcNow);

                bool mailed = inserted == 1;
                if (mailed)
                {
                    bgDb.MailboxInstances.Add(new MailboxInstance
                    {
                        PlayerId = playerId,
                        BaseItemId = "GOLD_COMPENSATION",
                        QualityTier = 0,
                        Quantity = 0,
                        GoldAttachment = SplitBrainCompensationGold,
                        IsClaimed = false,
                        IsPending = false,
                        ReceivedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
                    await bgDb.SaveChangesAsync();
                }

                await bgTx.CommitAsync();

                Console.WriteLine($"Split-brain: player {playerId} db epoch {dbEpoch} > session epoch {sessionEpoch}, lock holder {lockHolder}, " +
                    (mailed ? $"mailed {SplitBrainCompensationGold} gold" : "already compensated for this db epoch"));
            }
            catch (Exception bgEx)
            {
                Console.WriteLine($"Split-brain compensation failed for player {playerId} (db epoch {dbEpoch}, session epoch {sessionEpoch}, lock holder {lockHolder}): {bgEx.Message}");
            }
        }

        public StateCheckpointManager(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _redisSessionCache = serviceProvider.GetService<RedisSessionCache>();
        }

        // Modul: checkpoints off the tick thread (task 43). Where acks go. A
        // manager on its own (a test) keeps them here; SimulationEngine binds
        // PlayerSessionRegistry.FlushAckQueue at construction, before any flush
        // can be requested.
        private ConcurrentQueue<FlushAck> _ackQueue = new();

        // Set by FlushState's split-brain branch, consumed by the writer, so a
        // refused flush is not retried or rescued as if it were a transient one.
        private readonly ConcurrentDictionary<long, byte> _splitBrainRefused = new();

        private CheckpointWriter? _writer;

        // Test-only (InternalsVisibleTo): stands in for FlushState on the
        // writer, so a test can fail or delay one flush of a sequence.
        internal Func<TickStatePayload, Task<bool>>? FlushOverrideForTests;
        private readonly object _writerGate = new();

        public void BindAckQueue(ConcurrentQueue<FlushAck> queue)
        {
            _ackQueue = queue;
        }

        internal ConcurrentQueue<FlushAck> AckQueue => _ackQueue;

        /// <summary>
        /// Started on first use, so a manager that only ever loads state (the
        /// login tests, the cold-recovery path) never owns four idle tasks.
        /// </summary>
        public CheckpointWriter Writer
        {
            get
            {
                var writer = Volatile.Read(ref _writer);
                if (writer != null)
                {
                    return writer;
                }

                lock (_writerGate)
                {
                    _writer ??= new CheckpointWriter(
                        snapshot => FlushOverrideForTests?.Invoke(snapshot) ?? FlushState(snapshot),
                        BankGoldDeltaAsync,
                        playerId => _splitBrainRefused.TryRemove(playerId, out _),
                        () => _ackQueue,
                        playerId => _forceDisconnectCallback?.Invoke(playerId));
                    return _writer;
                }
            }
        }

        /// <summary>
        /// Queues a checkpoint of <paramref name="state"/> on CheckpointWriter
        /// and returns at once. Tick thread only: it mutates the live payload.
        /// </summary>
        /// <remarks>
        /// Modul: THE EPOCH ARITHMETIC. FlushState refuses a snapshot whose
        /// epoch is behind the database's and, on commit, writes db = snapshot
        /// + 1. The live epoch only rises when an ack is applied, so a snapshot
        /// taken while k flushes are still in flight is stamped live + k: each
        /// queued flush is then exactly one ahead of the one before it, and a
        /// FIFO partition can never refuse this player's own next flush. A
        /// failed flush leaves a gap in the sequence, which is harmless -
        /// epochs only have to increase, and the command gate has a drift
        /// tolerance.
        ///
        /// Modul: THE GOLD MOVES WITH THE JOB. TryStoreFrame first (unchanged:
        /// with Redis up it moves RedisPendingGoldDelta into Redis and zeroes
        /// it), then whatever delta is left rides on the snapshot, and the live
        /// payload is zeroed at once. Gold earned while the flush is in flight
        /// accumulates from zero and goes with the NEXT flush; a failed flush's
        /// delta comes back through the ack. Each coin is carried by exactly one
        /// of them - which is the "two gold paths" rule held across threads.
        ///
        /// <paramref name="then"/> runs on the writer after the commit, never
        /// after a failure (a reload excepted, see FlushJob.RunThenOnFailure).
        /// It is how a command keeps "flush, then do the engine work that reads
        /// the flushed rows" in that order without waiting on the tick.
        /// </remarks>
        public void RequestFlush(ref TickStatePayload state, FlushReason reason, Func<Task>? then = null)
        {
            _redisSessionCache?.TryStoreFrame(ref state);

            var job = new FlushJob
            {
                Kind = FlushJobKind.Flush,
                PlayerId = state.PlayerId,
                Reason = reason,
                Snapshot = state,
                GoldDelta = state.RedisPendingGoldDelta,
                Then = then,
                RunThenOnFailure = reason == FlushReason.Reload
            };
            job.Snapshot.LogicEpochCounter = state.LogicEpochCounter + state.FlushesInFlight;

            state.RedisPendingGoldDelta = 0L;
            // Task 79: the income tally goes with the snapshot the same way
            // (FlushState writes job.Snapshot.PendingGoldIncome; a failed ack
            // hands it back). Earnings from here on start from zero.
            state.PendingGoldIncome = default;
            // Village production rides on the snapshot the same way (FlushState
            // banks job.Snapshot.Pending*Delta; a failed ack hands them back).
            // TryStoreFrame above already zeroed them if Redis took them, so a
            // non-zero value here is one that only the checkpoint will bank.
            state.PendingWoodDelta = 0L;
            state.PendingStoneDelta = 0L;
            state.PendingIronDelta = 0L;
            state.FlushesInFlight++;
            state.IsDirty = false;
            state.TicksSinceLastFlush = 0;
            _dirtyStates.TryRemove(state.PlayerId, out _);

            Writer.Enqueue(job);
        }

        /// <summary>
        /// Banks a gold delta that lost its checkpoint - a logout that failed
        /// every retry, or a failed flush whose player has gone by the time the
        /// ack arrived. The same FOR UPDATE increment FlushState applies.
        /// </summary>
        internal async Task<bool> BankGoldDeltaAsync(long playerId, long delta)
        {
            var retryingOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
            await using var dbContext = new FolkIdleDbContext(retryingOptions.Options);
            var strategy = dbContext.Database.CreateExecutionStrategy();
            try
            {
                await strategy.ExecuteAsync(async () =>
                {
                    dbContext.ChangeTracker.Clear();
                    await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
                    await ApplyPendingGoldDeltaAsync(dbContext, new TickStatePayload { PlayerId = playerId, RedisPendingGoldDelta = delta });
                    await dbContext.SaveChangesAsync();
                    await transaction.CommitAsync();
                });
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Gold rescue failed for player {playerId} (delta {delta}): {ex.Message}");
                return false;
            }
        }

        /// <summary>Queues a gold-only rescue on the player's partition.</summary>
        internal void RequestGoldRescue(long playerId, long delta)
        {
            if (delta == 0L)
            {
                return;
            }
            Writer.Enqueue(new FlushJob { Kind = FlushJobKind.GoldRescue, PlayerId = playerId, GoldDelta = delta });
        }

        /// <summary>
        /// Completes once every checkpoint already queued for this player has
        /// finished. A login awaits it before reading the database.
        /// </summary>
        public Task WaitForPendingFlushesAsync(long playerId)
        {
            var writer = Volatile.Read(ref _writer);
            return writer == null ? Task.CompletedTask : writer.WaitForPlayerAsync(playerId);
        }

        /// <summary>
        /// Shutdown: stops taking jobs and lets the queued ones finish. The
        /// caller then drains FlushAckQueue into the live payloads, so the
        /// batch flush that follows carries current epochs.
        /// </summary>
        public bool DrainWriter(TimeSpan timeout)
        {
            var writer = Volatile.Read(ref _writer);
            return writer == null || writer.CompleteAndDrain(timeout);
        }

        // Test-only (InternalsVisibleTo): wait for the writer, then apply this
        // player's acks to a payload the test holds, as the tick would.
        internal async Task WhenWriterIdleAsync()
        {
            var writer = Volatile.Read(ref _writer);
            if (writer != null)
            {
                await writer.WhenIdleAsync();
            }
        }

        internal int ApplyPendingAcks(ref TickStatePayload state)
        {
            int applied = 0;
            int pending = _ackQueue.Count;
            for (int i = 0; i < pending && _ackQueue.TryDequeue(out var ack); i++)
            {
                if (ack.PlayerId == state.PlayerId)
                {
                    CheckpointAckTickCoordinator.Apply(ref state, in ack);
                    applied++;
                }
                else
                {
                    _ackQueue.Enqueue(ack);
                }
            }
            return applied;
        }

        /// <summary>
        /// How far behind the database a live payload is allowed to fall.
        /// 3000 ticks at 10 Hz is five minutes.
        ///
        /// Public because a command that commits something the player
        /// DELIBERATELY placed - see CommandType.SpendAttributePoint - sets the
        /// counter to this so the next tick checkpoints, instead of leaving the
        /// choice sitting in memory for five minutes.
        /// </summary>
        public const int CheckpointBoundaryTicks = 3000;

        /// <summary>
        /// Where a session's checkpoint clock starts: a fixed phase per player
        /// in [0, CheckpointBoundaryTicks), so a crowd that logs in on one tick
        /// reaches the boundary spread over the whole window (task 43, 2c).
        /// </summary>
        public static int StaggeredStartTicks(long playerId)
            => (int)(((playerId % CheckpointBoundaryTicks) + CheckpointBoundaryTicks) % CheckpointBoundaryTicks);

        public void TrackState(ref TickStatePayload state)
        {
            // Modul: `|| state.InventorySpaceRemaining <= 0` was deleted here
            // (task 43). The backpack is gone and the counter gates nothing
            // (InventoryCensusTickCoordinator pins it at capacity), but the
            // loot path still decrements it between censuses - so once it
            // touched zero, every tick was a "boundary" and every tick queued
            // a checkpoint.
            bool reachedCheckpointBoundary = state.TicksSinceLastFlush >= CheckpointBoundaryTicks;
            if (_redisSessionCache != null && (state.IsDirty || state.RequiresRedisFlush || reachedCheckpointBoundary))
            {
                // Modul: A REDIS FRAME IS NOT A CHECKPOINT, AND TREATING IT AS
                // ONE THREW AWAY EVERY FIELD THE FRAME DOES NOT CARRY.
                //
                // This used to return here whenever Redis took the frame -
                // INCLUDING at the checkpoint boundary - so with Redis up (it is
                // up in dev and in production) the periodic path never reached
                // FlushState at all. The frame is twelve fields: level, xp,
                // lineage, the logout stamp, the time bank, the epoch, the
                // quarantine flag, gold and three counters. FlushState writes
                // far more than that - BaseStrength/Dexterity/Constitution/Luck,
                // UnspentAttributePoints, diamonds, skill points, the larder,
                // potions, daily quests, the chronicle pass, lifetime statistics
                // - and none of it had any periodic route to Postgres.
                //
                // Reported as "I distribute my attribute points, press F5, and
                // they are all back in the pool". Exactly right, and it was
                // never only the attributes.
                //
                // The frame is still written first: it is the fast session cache
                // and it is the durable path for gold (see the note below on why
                // gold travels as a delta). It just no longer stands in for the
                // checkpoint at the one moment the checkpoint is due.
                if (_redisSessionCache.TryStoreFrame(ref state) && !reachedCheckpointBoundary)
                {
                    state.IsDirty = false;
                    _dirtyStates[state.PlayerId] = state;
                    return;
                }
            }

            // Modul: GOLD HAD NO DURABLE PATH THAT DID NOT GO THROUGH REDIS.
            //
            // Every other earned thing is written by FlushState - level, XP,
            // diamonds, attributes, chrono. Gold is not: the checkpoint never
            // touched it. Its only route to the database was
            // TickStatePayload.RedisPendingGoldDelta -> TryStoreFrame ->
            // a Redis buffer -> RedisWriteBehindEngine's five-minute flush, and
            // BOTH of those return early when Redis is not connected.
            //
            // So with Redis down a session's gold accumulated in memory, was
            // shown correctly in the client header, was never persisted, and
            // was discarded at logout - while the Progress screen's Statistics
            // panel, which reads CommodityRecords directly, went on displaying
            // the balance from login. That is the reported "two different gold
            // figures on one screen", and the smaller one was the one that
            // would survive.
            //
            // Forcing the checkpoint whenever Redis did not take the frame
            // gives gold the same durability as everything else. It matters
            // that this is the ref-owning path: FlushState applies the delta as
            // an INCREMENT inside its transaction, so a market sale landing
            // between two checkpoints is added to, never overwritten. Since
            // task 43 the delta rides on the queued job and the live payload
            // is zeroed at request time; a failed flush's ack hands it back,
            // so it still retries instead of losing the coins.
            bool redisUnavailable = _redisSessionCache == null || !_redisSessionCache.IsConnected;
            bool goldOwedWithoutRedis = redisUnavailable && state.RedisPendingGoldDelta != 0L;

            // Modul: CHECKPOINTS OFF THE TICK THREAD (task 43). This used to
            // be FlushStateAndAdvance - a Serializable FOR UPDATE transaction
            // run synchronously here, on the thread that simulates everybody.
            // It is queued now, and the commit comes back as an ack
            // (CheckpointAckTickCoordinator). A failed flush hands its gold
            // back and re-arms the boundary there, so the retry is still "the
            // next tick", as it was.
            //
            // The TICK boundary requests unconditionally: RequestFlush resets
            // the counter, so it cannot fire twice, and a boundary a command
            // forced (SpendAttributePoint sets the counter to the boundary) has
            // to be honoured even behind another flush - it holds a choice the
            // queued snapshot predates. SustainedLoadTests saw the counter pass
            // 3000 when this waited.
            //
            // The Redis-down GOLD trigger is gated to one flush in flight:
            // every tick that earns gold reaches it, and without the gate each
            // of them would queue another flush behind the first. Gold earned
            // meanwhile waits on the payload for the next one - see
            // RequestFlush on why that banks it exactly once.
            if (reachedCheckpointBoundary || (goldOwedWithoutRedis && state.FlushesInFlight == 0))
            {
                RequestFlush(ref state, FlushReason.Periodic);
            }
            else if (state.IsDirty)
            {
                _dirtyStates[state.PlayerId] = state;
            }
        }

        public bool FlushStateAndAdvance(ref TickStatePayload state)
        {
            // TryStoreFrame zeroes RedisPendingGoldDelta when it succeeds, so
            // the copy handed to FlushState carries a non-zero delta only when
            // Redis did NOT take it. That is what keeps the two durable paths
            // from both applying the same coins.
            _redisSessionCache?.TryStoreFrame(ref state);
            long pendingGold = state.RedisPendingGoldDelta;

            // Modul: A SYNCHRONOUS FLUSH MUST QUEUE BEHIND THE WRITER'S (task
            // 43). A flush still in flight on CheckpointWriter was stamped
            // with this payload's epoch; committing this one first at the same
            // epoch - or at a higher one ahead of it - makes the queued one
            // look split-brained, which mails compensation gold and
            // disconnects an honest player. SustainedLoadTests caught exactly
            // that. So: wait for the player's queued flushes, then stamp past
            // them, exactly as RequestFlush does. The login path, the one
            // caller left, has nothing in flight and never waits.
            if (state.FlushesInFlight > 0)
            {
                WaitForPendingFlushesAsync(state.PlayerId).GetAwaiter().GetResult();
            }
            var snapshot = state;
            snapshot.LogicEpochCounter = state.LogicEpochCounter + state.FlushesInFlight;

            bool committed = FlushState(snapshot).GetAwaiter().GetResult();
            // The writer's split-brain mark is for the writer; this path has
            // its answer already and must not leave one behind for it.
            _splitBrainRefused.TryRemove(state.PlayerId, out _);
            if (committed)
            {
                state.LogicEpochCounter = snapshot.LogicEpochCounter + 1;
                state.IsDirty = false;

                // Only now. If the flush failed the coins are still owed, and
                // the next TrackState re-attempts with the delta intact.
                if (pendingGold != 0L && state.RedisPendingGoldDelta == pendingGold)
                {
                    state.RedisPendingGoldDelta = 0L;
                }

                // Task 79: the snapshot's tally is in gold_income_daily now.
                state.PendingGoldIncome.Subtract(snapshot.PendingGoldIncome);

                // The snapshot's village production is in CommodityRecords now.
                state.PendingWoodDelta -= snapshot.PendingWoodDelta;
                state.PendingStoneDelta -= snapshot.PendingStoneDelta;
                state.PendingIronDelta -= snapshot.PendingIronDelta;
            }
            return committed;
        }

        // Modul: THE FIELDED CHARACTER'S ACTIVITY IS DURABLE NOW, 2026-09-23.
        //
        // The single-character ChangeActivity path (TargetGuid empty) and the
        // death reset change ActiveActivityId on the live payload only, and
        // LoadPlayerState reads it from characters[0]'s row. Nothing wrote that
        // row, so a relogin brought the character back on whatever the row
        // last said - usually idle - and offline catch-up, which simulates the
        // loaded activity, simulated nothing. StateReloadMerge covers a reload
        // inside a session (task 24); this covers everything after it.
        //
        // GUARDED, not a write by id. The Hall of Ancestors rewrites slot
        // membership out-of-band and benches the displaced character idle
        // before the reload that follows - and that reload flushes FIRST, with
        // the stale payload still naming the benched character in slot 1. A
        // plain write by id would put the benched character's fight straight
        // back. So the row is written only while it is still the character
        // LoadPlayerState would field: the first non-escrowed row ordered by
        // SlotIndex, then Id. That is LoadPlayerState's order; change both together.
        //
        // Slots 2 and 3 are not written: their only activity writer is
        // ChangeCharacterActivityAsync, which commits the row itself.
        private static async Task PersistFieldedActivityAsync(FolkIdleDbContext dbContext, long playerId, System.Guid fieldedCharacterId, long activityId)
        {
            if (fieldedCharacterId == System.Guid.Empty)
            {
                return;
            }

            await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE ""characters"" SET ""ActiveActivityId"" = {activityId}
                WHERE ""Id"" = {fieldedCharacterId}
                  AND ""ActiveActivityId"" <> {activityId}
                  AND ""Id"" = (
                      SELECT c.""Id"" FROM ""characters"" c
                      WHERE c.""PlayerId"" = {playerId} AND NOT c.""IsLockedInEscrow""
                      ORDER BY c.""SlotIndex"", c.""Id""
                      LIMIT 1)");
        }

        public async Task<bool> FlushState(TickStatePayload state)
        {
            var retryingOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
            await using var dbContext = new FolkIdleDbContext(retryingOptions.Options);

            // Modul: the retried delegate below is only ever allowed to
            // return false via the explicit split-brain branch (a detected,
            // expected condition, not a failure) - it deliberately does NOT
            // catch-and-return-false on a thrown exception. A thrown
            // Serializable-conflict (40001) or deadlock (40P01) must
            // propagate out of the delegate for CreateExecutionStrategy to
            // retry it; catching it here would silently defeat that retry
            // and reproduce the exact silent-data-loss bug this method was
            // refactored to close. Only the outer catch, reached once
            // retries are exhausted or the exception is not retryable,
            // reports failure to the caller.
            var strategy = dbContext.Database.CreateExecutionStrategy();
            try
            {
                bool committed = await strategy.ExecuteAsync(async () =>
                {
                    dbContext.ChangeTracker.Clear();

                    using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                    // Pessimistic row-level epoch lock. FOR UPDATE prevents concurrent epoch modification.
                    var player = await dbContext.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", state.PlayerId)
                        .FirstOrDefaultAsync();

                    if (player != null)
                    {
                        // Split-brain vector timestamp sieve: if db epoch is strictly ahead, a concurrent node already wrote.
                        if (player.LogicEpochCounter > state.LogicEpochCounter)
                        {
                            await transaction.RollbackAsync();

                            // Modul: task 42. A flat SplitBrainCompensationGold, paid
                            // once per (PlayerId, DbEpoch) - see SplitBrainIncident.
                            // This used to mail epochDelta * 500 on every refused
                            // flush: uncapped, and paid again for every race.
                            TelemetryStreamer.TryWrite(new TelemetryEvent
                            {
                                PlayerId = state.PlayerId,
                                EventType = 5,
                                Value1 = (int)(player.LogicEpochCounter & 0x7FFFFFFF),
                                Value2 = (int)(state.LogicEpochCounter & 0x7FFFFFFF),
                                Timestamp = Environment.TickCount64
                            });

                            long capturedPlayerId = state.PlayerId;
                            long capturedDbEpoch = player.LogicEpochCounter;
                            long capturedSessionEpoch = state.LogicEpochCounter;
                            LastSplitBrainCompensation = Task.Run(() =>
                                CompensateSplitBrainAsync(capturedPlayerId, capturedDbEpoch, capturedSessionEpoch));

                            _forceDisconnectCallback?.Invoke(state.PlayerId);
                            _dirtyStates.TryRemove(state.PlayerId, out _);
                            _splitBrainRefused[state.PlayerId] = 0;
                            return false;
                        }

                        player.CurrentLevel = state.CurrentLevel;
                        player.CurrentXp = state.CurrentXp;
                        // Modul: lifetime statistics. Absolute assignment, not
                        // +=, so re-flushing the same snapshot is a no-op -
                        // see TickStatePayload.LifetimeDeaths.
                        ApplyLifetimeStatistics(player, state);
                        player.SelectedLineageId = state.SelectedLineageId;
                        player.LastLogoutTimestamp = state.LastLogoutTimestamp;
                        player.ActiveOffensivePotionId = state.ActiveOffensivePotionId;
                        player.OffensivePotionDurationMs = state.OffensivePotionDurationMs;
                        player.ActiveDefensivePotionId = state.ActiveDefensivePotionId;
                        player.DefensivePotionDurationMs = state.DefensivePotionDurationMs;

                        // Modul: larder. The auto-eat step consumes from these
                        // slots every time it fires, so the payload - not the
                        // PlayerRecords row LarderEngine wrote - is the current
                        // truth about what is left. Without this, food eaten
                        // during a session was restored in full at the next
                        // login: infinite sustain from one stocking.
                        player.LarderSlot1ItemId = state.Food1_Count > 0 ? state.Food1_ItemId : 0;
                        player.LarderSlot1Count = state.Food1_Count;
                        player.LarderSlot2ItemId = state.Food2_Count > 0 ? state.Food2_ItemId : 0;
                        player.LarderSlot2Count = state.Food2_Count;
                        player.LarderSlot3ItemId = state.Food3_Count > 0 ? state.Food3_ItemId : 0;
                        player.LarderSlot3Count = state.Food3_Count;
                        player.AutoEatThresholdPct = state.AutoEatThreshold;
                        player.LogicEpochCounter = state.LogicEpochCounter + 1;
                        // Modul: THE CHECKPOINT NO LONGER WRITES THE
                        // QUARANTINE FLAGS.
                        //
                        // AntiCheatTelemetryEngine.RequestShadowBan writes them
                        // to the database itself, in its own transaction, and
                        // then mirrors them onto the payload for the tick. This
                        // wrote them BACK from the payload every flush - so a
                        // live session's stale copy could resurrect a
                        // quarantine that had already been lifted, and an
                        // operator could never lift one for an online player at
                        // all. Clearing the columns appeared to work and the
                        // next flush undid it.
                        //
                        // Same shape as the gold bug: changing the database
                        // out-of-band while a session holds the old value. Gold
                        // survives because it is persisted as a delta; these
                        // are absolutes, so the only safe answer is one writer.
                        // Hydration reads them - see the payload build below.
                        player.BaseStrength = state.STR;
                        player.BaseDexterity = state.DEX;
                        player.BaseConstitution = state.CON;
                        player.BaseLuck = state.LCK;
                        // Modul: per-character equipment. The flush used to
                        // mirror the payload's equipped ids back onto
                        // PlayerRecords. Equipment now lives on CharacterRecord
                        // and EquipmentSlotEngine is its only writer, committing
                        // inside its own Serializable transaction - so the
                        // payload's copy is a read-through cache and writing it
                        // back here would let a stale register overwrite a fresh
                        // equip that landed between two checkpoints.
                        long consumableFlushEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        player.ActiveOffensivePotionId = state.ActiveOffensivePotionId;
                        player.ActiveOffensivePotionExpiresEpoch = state.OffensivePotionDurationMs > 0 ? consumableFlushEpoch + state.OffensivePotionDurationMs / 1000L : 0L;
                        player.ActiveDefensivePotionId = state.ActiveDefensivePotionId;
                        player.ActiveDefensivePotionExpiresEpoch = state.DefensivePotionDurationMs > 0 ? consumableFlushEpoch + state.DefensivePotionDurationMs / 1000L : 0L;
                        player.ActiveFoodId = state.ActiveFoodBuffId;
                        player.ActiveFoodExpiresEpoch = state.FoodBuffDurationMs > 0 ? consumableFlushEpoch + state.FoodBuffDurationMs / 1000L : 0L;
                        player.XpPenaltyExpiresEpoch = state.XpPenaltyExpiresEpoch;
                        player.PremiumDiamonds = state.PremiumCurrency;
                        player.AvailableSkillPoints = state.AvailableSkillPoints;
                        player.UnspentAttributePoints = state.UnspentAttributePoints;
                        // Task 51: records only improve - max / fastest, never a copy.
                        Domain.Progression.PersonalRecords.MergeInto(player, in state);

                        // Modul: larder. The auto-eat step consumes from these
                        // slots every time it fires, so the payload - not the
                        // PlayerRecords row LarderEngine wrote - is the current
                        // truth about what is left. Without this, food eaten
                        // during a session was restored in full at the next
                        // login: infinite sustain from one stocking.
                        player.LarderSlot1ItemId = state.Food1_Count > 0 ? state.Food1_ItemId : 0;
                        player.LarderSlot1Count = state.Food1_Count;
                        player.LarderSlot2ItemId = state.Food2_Count > 0 ? state.Food2_ItemId : 0;
                        player.LarderSlot2Count = state.Food2_Count;
                        player.LarderSlot3ItemId = state.Food3_Count > 0 ? state.Food3_ItemId : 0;
                        player.LarderSlot3Count = state.Food3_Count;
                        player.AutoEatThresholdPct = state.AutoEatThreshold;
                        await ApplyPendingGoldDeltaAsync(dbContext, state);
                        await ApplyPendingVillageProductionAsync(dbContext, state);
                        // Task 79: where the tick's gold came from, in THIS
                        // transaction - a refused or rolled-back flush counts
                        // nothing, and its ack hands the tally back.
                        await GoldLedger.RecordIncomeTallyAsync(dbContext, state.PlayerId, state.PendingGoldIncome);
                        // Modul: THE DEEP'S 7-DAY HIGH-WATER MARK (task 37), in
                        // THIS transaction so a flush that rolls back records
                        // nothing. CurrentGold is the live balance, which
                        // already includes gold still riding on
                        // RedisPendingGoldDelta. Deliberately not in TrackState's
                        // Redis frame: a frame is a cache, not a checkpoint.
                        await FolkIdle.Server.Domain.Economy.GoldHighWater.RecordAsync(
                            dbContext, state.PlayerId, state.CurrentGold,
                            FolkIdle.Server.Domain.Economy.GoldHighWater.Today(DateTime.UtcNow));
                        await UpsertChroniclePassAsync(dbContext, state);
                        await UpsertLifetimeAchievementsAsync(dbContext, player, state);
                        await QuestEngine.UpsertDailyQuestProgressAsync(dbContext, state);
                        await PersistFieldedActivityAsync(dbContext, state.PlayerId, state.Slot1_CharacterId, state.ActiveActivityId);
                    }
                    else
                    {
                        dbContext.PlayerRecords.Add(new PlayerRecord
                        {
                            Id = state.PlayerId,
                            CurrentLevel = state.CurrentLevel,
                            CurrentXp = state.CurrentXp,
                            SelectedLineageId = state.SelectedLineageId,
                            LastLogoutTimestamp = state.LastLogoutTimestamp,
                            LogicEpochCounter = state.LogicEpochCounter + 1,
                            // A row being created for the first time cannot
                            // already be quarantined, and the flags have one
                            // writer now - see above.
                            Quarantine_Active = false,
                            IsQuarantined = false
                        });
                        await UpsertChroniclePassAsync(dbContext, state);
                    }

                    await dbContext.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return true;
                });

                // Modul: funnel steps 5 and 7-9 (onboarding done, level
                // 5/10/20), AFTER the commit. The checkpoint is the one place
                // all three level paths - kill, warp, offline - pass through,
                // so hooking levels here covers all of them at once. A
                // split-brain refusal returned false above and records nothing.
                if (committed) FunnelRecorder.RecordCheckpoint(in state);
                return committed;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to flush state for player {state.PlayerId}: {ex.Message}");
                return false;
            }
        }

        public async Task<TickStatePayload> LoadPlayerState(long playerId)
        {
            // Modul: login-time state hydration - retry-configured so every
            // read below transparently survives a transient failure
            // or Serializable conflict during a concurrent login burst
            // (cold-boot recovery, many logins at once) instead of failing
            // the session outright.
            var retryingOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
            await using var dbContext = new FolkIdleDbContext(retryingOptions.Options);

            var player = await dbContext.PlayerRecords.FindAsync(playerId);

            // Modul: seed the name cache on the way past.
            //
            // The drop and reroll announcements are built INSIDE the simulation
            // tick, which cannot wait on a query - so they read a cache, and a
            // cache nobody fills answers "Player #123" forever. Hydration
            // already has the row in hand, and every player who can trigger an
            // announcement has been hydrated to get there.
            Engine.PlayerNameResolver.Remember(playerId, player?.Username);

            if (player == null)
            {
                var defaultPayload = new TickStatePayload
                {
                    PlayerId = playerId,
                    // Modul: both "where may I be" fields start at region 1
                    // EXPLICITLY. Left at the struct default they are 0, and
                    // every gate here is a `requested > held` comparison - so a
                    // zero does not mean "no progress", it means region 1 is
                    // shut and the player has nowhere to go at all. That was
                    // already true of HighestLocationReached before
                    // HighestUnlockedRegion joined it; setting only the new one
                    // would have left the older trap armed next to a comment
                    // claiming location 1 is always open.
                    HighestLocationReached = 1,
                    HighestUnlockedRegion = RegionUnlockGate.StartingRegion,
                    ActiveActivityId = 1,
                    CurrentProgressTicks = 0,
                    RequiredProgressTicks = 50,
                    InventorySpaceRemaining = 20,
                    PlayerHp = 100000,
                    CurrentGold = 10000,
                    PremiumCurrency = 0,
                    LogicEpochCounter = 0,
                    LegacyShardBalance = 0,
                    CitizenMultiSlotsUnlocked = 0,
                    GuildLogisticsCurrentStock = 0L,
                    GuildLogisticsTargetRequirement = 0L,
                    CombatSimulationMatchId = 0L,
                    CombatSimulationTurnCounter = 0,
                    CombatSimulationDamageDelta = 0,
                    AccountId = ResolveAccountId(playerId, Guid.Empty),
                    ActiveMentorPlayerId = 0L,
                    MentorshipExpBonusMultiplier = 1.0,
                    ForgeLevel = 0,
                    InnLevel = 0,
                    BreedingLevel = 0,
                    AcademyLevel = 0,
                    CurrentPopulationCount = 0,
                    ActiveMentorshipContractCount = 0,
                    CachedMaxPopulationCapacity = VillageManagementEngine.CalculatePopulationCapacity(0),
                    CachedInnMaturationBonus = 0,
                    CachedCurrentToolTier = 0,
                    IsQuarantined = false,
                    ActiveLanguageState = 1,
                    ActiveChroniclePassLevel = 0,
                    AccumulatedSeasonalXp = 0,
                    AvailableSkillPoints = 0
                };
                defaultPayload.InitializeObfuscation(GenerateSessionXorKey(playerId, 0));
                return defaultPayload;
            }

            int miningMonolith = 0;
            int woodMonolith = 0;
            long guildLogisticsStock = 0L;
            long guildLogisticsTarget = 0L;
            long combatMatchId = 0L;
            int combatTurnCounter = 0;
            Guid activeCrossShardMatchId = Guid.Empty;
            int activeMatchMmr = 0;
            long globalNodeRemainingHp = 0L;
            long activeGuildWarId = 0L;
            if (player.GuildId > 0)
            {
                var guild = await dbContext.GuildRecords.FindAsync(player.GuildId);
                if (guild != null)
                {
                    miningMonolith = guild.MiningMonolithLevel;
                    woodMonolith = guild.WoodcuttingMonolithLevel;
                }

                guildLogisticsStock = await dbContext.GuildLogisticsDepots
                    .AsNoTracking()
                    .Where(d => d.GuildId == player.GuildId)
                    .SumAsync(d => (long?)d.CurrentStock) ?? 0L;
                guildLogisticsTarget = await dbContext.GuildLogisticsDepots
                    .AsNoTracking()
                    .Where(d => d.GuildId == player.GuildId)
                    .SumAsync(d => (long?)d.TargetRequirement) ?? 0L;

                var activeCombatMatch = await dbContext.GuildWarActiveMatches
                    .AsNoTracking()
                    .Where(m => m.AttackingGuildId == player.GuildId || m.DefendingGuildId == player.GuildId)
                    .OrderBy(m => m.MatchId)
                    .FirstOrDefaultAsync();
                if (activeCombatMatch != null)
                {
                    combatMatchId = activeCombatMatch.MatchId;
                    combatTurnCounter = (int)GuildCombatSimulationEngine.ExtractTurnCounter(activeCombatMatch.CurrentStateBitmask);
                }

                var crossShardMatch = await dbContext.GuildMatchmakingSnapshots
                    .AsNoTracking()
                    .Where(m => !m.IsComplete && (m.AttackerGuildId == player.GuildId || m.DefenderGuildId == player.GuildId))
                    .OrderBy(m => m.TournamentGroupIndex)
                    .FirstOrDefaultAsync();
                if (crossShardMatch != null)
                {
                    activeCrossShardMatchId = crossShardMatch.MatchUuid;
                    activeMatchMmr = crossShardMatch.ActiveMatchMmr;
                    globalNodeRemainingHp = crossShardMatch.GlobalNodeRemainingHp;
                }

                // Modul: Play Mode audit fix. TickStatePayload.ActiveGuildWarId
                // gates every live contribution to the weekly guild-war
                // scoreboard (combat kills, tier-5 crafts, and
                // ContributeToWarSupply all check "> 0" in SimulationEngine
                // before enqueueing any points) and drives the client's
                // entire UiGuildWarPanel active/inactive state - but nothing
                // anywhere ever assigned it, so every session hydrated with
                // it permanently 0 even during a real active war. This is
                // the same GuildWarMatches row BuildGuildWarGroup's own
                // scoreboard reads from once populated live, distinct from
                // GuildWarActiveMatches (the turn-based combat sim match
                // above) and GuildMatchmakingSnapshots (cross-shard).
                var activeGuildWar = await dbContext.GuildWarMatches
                    .AsNoTracking()
                    .Where(m => m.IsActive && (m.GuildA_Id == player.GuildId || m.GuildB_Id == player.GuildId))
                    .FirstOrDefaultAsync();
                if (activeGuildWar != null)
                {
                    activeGuildWarId = activeGuildWar.MatchId;
                }
            }

            // Modul: roster slot ordering. This query had no OrderBy, so
            // Postgres returned the rows in whatever order it liked and .Take(3)
            // picked an arbitrary three. Everything downstream indexes this list
            // by POSITION - characters[0] is treated as the main character whose
            // gear hydrates the active register, characters[1] and [2] become
            // the Slot2/Slot3 activity states - while the Town Hall unlock gate
            // and the occupancy mutex both key off CharacterRecord.SlotIndex.
            //
            // The two disagreed. Caught in a live Play Mode session: a character
            // stored at SlotIndex 1 was simulated and broadcast as slot 3, so
            // the roster showed it in the wrong row, and the main character's
            // equipment could be read off whichever character the database
            // happened to return first. Ordering by SlotIndex makes position and
            // SlotIndex the same thing, which is what every consumer already
            // assumed, and makes .Take(3) mean "the first three slots" rather
            // than "any three".
            // Modul: heal dangling equip pointers before anything reads them.
            // See EquipmentSlotEngine.ClearDanglingEquipReferencesAsync.
            await Domain.Combat.EquipmentSlotEngine.ClearDanglingEquipReferencesAsync(dbContext, playerId);

            var characters = await dbContext.CharacterRecords
                .Include(c => c.Lineage)
                // Modul: the Academy exclusion is gone with the Academy. A
                // character lent out as a mentor used to be filtered out here,
                // which is half of why a player could own characters and have
                // none eligible to deploy.
                .Where(c => c.PlayerId == playerId && !c.IsLockedInEscrow)
                .OrderBy(c => c.SlotIndex)
                .ThenBy(c => c.Id)
                .Take(3)
                .ToListAsync();

            var achievements = await dbContext.PlayerAchievements.FindAsync(playerId);
            int achievementFlags = achievements?.ClaimedAchievementFlags ?? 0;

            var codexEntries = await dbContext.MonsterCodexEntries.Where(c => c.PlayerId == playerId).ToListAsync();
            // Modul: how far the player has reached. One kill anywhere in a
            // location is enough - the point is "have you been here", not
            // "have you finished here", which is what completedAreas below
            // answers. Location 1 is always open, so a player with no kills
            // at all still has somewhere to gather.
            int highestLocationReached = 1;
            var defeatedBosses = new HashSet<int>();
            for (int i = 0; i < codexEntries.Count; i++)
            {
                if (codexEntries[i].KillCount <= 0) continue;
                int location = ContentRegistry.GetCanonicalLocation(codexEntries[i].MonsterId);
                if (location > highestLocationReached) highestLocationReached = location;

                // Modul: region progression. Collected in the same pass rather
                // than by a second query - the codex is already in hand, and
                // the gate wants five booleans out of it.
                if (RaceUnlockRegistry.GetRegionForBossMonsterId(codexEntries[i].MonsterId) > 0)
                {
                    defeatedBosses.Add(codexEntries[i].MonsterId);
                }
            }

            // The durable answer. SimulationEngine raises this live when a boss
            // falls so the door opens immediately; here it is rebuilt from the
            // codex, which is what makes the live raise safe to be optimistic -
            // anything it got wrong is corrected on the next hydration rather
            // than persisting as a permanently wrong permission.
            int highestUnlockedRegion = RegionUnlockGate.HighestUnlockedRegion(defeatedBosses);

            // The same codex rows, kept as a bitmask so the tick loop can ask
            // "has this player beaten THIS boss" without a query - which
            // HighestUnlockedRegion cannot answer for region 5.
            byte defeatedRegionBossMask = BossFirstClearRules.MaskFrom(defeatedBosses);

            // Modul: HOW MANY WORLD BOSS ATTEMPTS THIS PLAYER HAS ALREADY SPENT,
            // which nothing loaded until 2026-09-05.
            //
            // WorldBossAttemptCount was written in exactly one place - the
            // notification the engine raises after an attack resolves - and read
            // straight onto the wire. So a player who spent their attempts,
            // logged out and came back saw three pips, all unspent. Clicking
            // Attack then hit the cap inside ExecuteAttackAsync, which ROLLS
            // BACK IN SILENCE: no damage, no message, no telemetry they would
            // ever see. The screen only told the truth after they had wasted a
            // click on it.
            //
            // Found by the exercise script's own numbers rather than by
            // reasoning: it reported an attempt going "0 -> 2 spent" on a single
            // strike, which is not a thing one strike can do.
            //
            // The row is deleted whenever a new event window opens, so its
            // absence is the honest zero.
            byte worldBossAttemptCount = 0;
            var bossAttemptRow = await dbContext.PlayerWorldBossAttempts
                .AsNoTracking()
                .SingleOrDefaultAsync(a => a.PlayerId == playerId
                    && a.BossInstanceId == FolkIdle.Server.Engine.WorldBossEngine.ActiveBossInstanceId);
            // Modul: ONE STRIKE A DAY (2026-09-25): the row's count belongs to
            // the day it was made on, so a row from an earlier day loads as 0 -
            // otherwise yesterday's strike would grey the button today. The
            // 300-second session is gone, and its wire field with it (task 36).
            if (bossAttemptRow != null
                && bossAttemptRow.AttemptDateKey == FolkIdle.Server.Engine.WorldBossCalendar.DayKey(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            {
                worldBossAttemptCount = (byte)Math.Clamp(bossAttemptRow.AttemptCount, 0, byte.MaxValue);
            }

            // Modul: the tools the ACTIVE CHARACTER IS WEARING.
            //
            // This used to scan both halves of the chest for the best tool
            // BaseId the player owned anywhere - which was the best that could
            // be done while tools were stackable materials, and meant a tool
            // worked from inside a crate and every axe of a given wood was
            // worth exactly the same as every other one. Tools are equipment
            // now, so the question is what is in the three tool slots, and the
            // answer carries the item's rolled affixes with it.
            ToolLoadout toolLoadout = ToolLoadout.Empty;
            {
                var mainCharacter = characters.Count > 0 ? characters[0] : null;
                if (mainCharacter != null)
                {
                    var toolInstanceIds = new List<long>(3);
                    if (mainCharacter.EquippedAxeId.HasValue) toolInstanceIds.Add(mainCharacter.EquippedAxeId.Value);
                    if (mainCharacter.EquippedPickaxeId.HasValue) toolInstanceIds.Add(mainCharacter.EquippedPickaxeId.Value);
                    if (mainCharacter.EquippedRodId.HasValue) toolInstanceIds.Add(mainCharacter.EquippedRodId.Value);

                    if (toolInstanceIds.Count > 0)
                    {
                        var toolRows = await dbContext.EquipmentInstances
                            .AsNoTracking()
                            .Where(e => toolInstanceIds.Contains(e.Id))
                            .ToDictionaryAsync(e => e.Id);
                        toolLoadout = ToolLoadoutResolver.Resolve(mainCharacter, toolRows);
                    }
                }
            }

            // Modul: the five canonical regions, by RegionCompletionRules - the
            // legacy monsters this used to include made completion impossible.
            var killsByMonster = new System.Collections.Generic.Dictionary<int, int>(codexEntries.Count);
            foreach (var c in codexEntries) killsByMonster[c.MonsterId] = c.KillCount;
            int completedAreas = RegionCompletionRules.CompletedFlags(
                id => killsByMonster.TryGetValue(id, out int k) ? k : 0);

            // Modul 13 fix: RaceId filters here previously used raw literals (1, 3, 4)
            // that predate RaceIds and never matched it - see the same fix in
            // SimulationEngine's MasteryUpdateQueue dispatcher for details.
            var masteries = await dbContext.PlayerRaceMasteries.Where(m => m.PlayerId == playerId).ToListAsync();
            int humanMastery = masteries.FirstOrDefault(m => m.RaceId == RaceIds.Human)?.MasteryLevel ?? 0;
            int vilaMastery = masteries.FirstOrDefault(m => m.RaceId == RaceIds.Vila)?.MasteryLevel ?? 0;
            int draugrMastery = masteries.FirstOrDefault(m => m.RaceId == RaceIds.Draugr)?.MasteryLevel ?? 0;
            int koboldMastery = masteries.FirstOrDefault(m => m.RaceId == RaceIds.Kobold)?.MasteryLevel ?? 0;
            int vodnikMastery = masteries.FirstOrDefault(m => m.RaceId == RaceIds.Vodnik)?.MasteryLevel ?? 0;
            int moosleuteMastery = masteries.FirstOrDefault(m => m.RaceId == RaceIds.Moosleute)?.MasteryLevel ?? 0;

            // Modul: inventory census. Hydration used to write "20 + bonus"
            // directly into InventorySpaceRemaining without ever looking at the
            // backpack, which meant a relogin was the only way to get inventory
            // space back - and gave a player with a genuinely full pack twenty
            // phantom slots. Counted here from the real rows, once per login, by
            // the same helper the per-kill census uses so the two can never
            // disagree about what a slot is.
            int backpackCapacity = SimulationEngine.DefaultBackpackCapacity + RaceMasteryResolver.GetHumanVaultBonusSlots(humanMastery);
            int occupiedBackpackSlots = await CombatLootEngine.CountOccupiedBackpackSlotsAsync(dbContext, playerId);

            // Modul 16/21: EquippedWeaponId/ArmorId are persisted, but the
            // derived stat totals StatsCalculator reads every tick are not - they
            // must be recomputed once at login rather than starting zeroed until
            // the player's next equip action.
            // Modul: per-character equipment. Totals are per character now, so
            // this resolves the main character's gear rather than the account's.
            // Slots 2 and 3 get the same treatment further down, where their
            // parked activity state is filled in - each character has to fight
            // in its own armour.
            CharacterRecord? mainCharacterRecord = characters.Count > 0 ? characters[0] : null;

            EquippedAffixTotals equippedAffixTotals = default;
            EquippedSetIds equippedSetIds = default;
            if (mainCharacterRecord != null)
            {
                (equippedAffixTotals, equippedSetIds) =
                    await EquipmentSlotEngine.ComputeEquippedTotalsAsync(dbContext, mainCharacterRecord);
            }

            // Modul: mentorship removed - two queries per hydration deleted
            // with it. The payload fields they filled stay at their neutral
            // values below.

            // Modul 16: resolve any upgrade that matured while this player was
            // offline before hydrating the login payload, so a returning
            // player never sees a stale CurrentLevel or a queue slot that is
            // actually already free.
            await VillageManagementEngine.ResolveMaturedUpgradesAsync(dbContext, playerId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            var villageRows = await dbContext.VillageInfrastructures
                .AsNoTracking()
                .Where(v => v.PlayerId == playerId)
                .ToListAsync();
            int forgeLevel = 0;
            int innLevel = 0;
            int breedingLevel = 0;
            int academyLevel = 0;
            int lumberjackLevel = 0;
            int mineLevel = 0;
            int warehouseLevel = 0;
            int townHallLevel = 0;
            int craftingWorkshopLevel = 0;
            byte pendingUpgradeBuildingId = 0;
            long pendingUpgradeCompletesAtEpoch = 0;
            for (int i = 0; i < villageRows.Count; i++)
            {
                if (villageRows[i].BuildingId == VillageManagementEngine.ForgeBuildingId) forgeLevel = villageRows[i].CurrentLevel;
                else if (villageRows[i].BuildingId == VillageManagementEngine.InnBuildingId) innLevel = villageRows[i].CurrentLevel;
                else if (villageRows[i].BuildingId == VillageManagementEngine.BreedingGroundsBuildingId) breedingLevel = villageRows[i].CurrentLevel;
                else if (villageRows[i].BuildingId == VillageManagementEngine.MentorshipAcademyBuildingId) academyLevel = villageRows[i].CurrentLevel;
                else if (villageRows[i].BuildingId == VillageManagementEngine.LumberjackBuildingId) lumberjackLevel = villageRows[i].CurrentLevel;

                else if (villageRows[i].BuildingId == VillageManagementEngine.MineBuildingId) mineLevel = villageRows[i].CurrentLevel;
                else if (villageRows[i].BuildingId == VillageManagementEngine.WarehouseBuildingId) warehouseLevel = villageRows[i].CurrentLevel;
                else if (villageRows[i].BuildingId == VillageManagementEngine.TownHallBuildingId) townHallLevel = villageRows[i].CurrentLevel;
                else if (villageRows[i].BuildingId == VillageManagementEngine.CraftingWorkshopBuildingId) craftingWorkshopLevel = villageRows[i].CurrentLevel;

                if (villageRows[i].UpgradeTargetLevel > 0)
                {
                    pendingUpgradeBuildingId = (byte)villageRows[i].BuildingId;
                    pendingUpgradeCompletesAtEpoch = villageRows[i].UpgradeCompletesAtEpoch;
                }
            }

            var villageCommodityRows = await dbContext.CommodityRecords
                .AsNoTracking()
                .Where(c => c.PlayerId == playerId && (
                    c.ItemId == VillageManagementEngine.WoodCommodityId ||
                    c.ItemId == VillageManagementEngine.StoneCommodityId ||
                    c.ItemId == VillageManagementEngine.IronOreCommodityId))
                .ToListAsync();
            long woodStock = villageCommodityRows.FirstOrDefault(c => c.ItemId == VillageManagementEngine.WoodCommodityId)?.Quantity ?? 0L;
            long stoneStock = villageCommodityRows.FirstOrDefault(c => c.ItemId == VillageManagementEngine.StoneCommodityId)?.Quantity ?? 0L;
            long ironOreStock = villageCommodityRows.FirstOrDefault(c => c.ItemId == VillageManagementEngine.IronOreCommodityId)?.Quantity ?? 0L;

            // Modul: the unlocked-skills bitmask is gone with the four active
            // skills. PlayerSkillUnlocks is left in the schema rather than
            // dropped in the same pass - see the handoff; a table nothing reads
            // is a smaller problem than a migration written in a hurry.

            // Modul: THE VILLAGE COUNTED A TABLE NOTHING EVER WROTE TO.
            //
            // VillageResidents has no INSERT anywhere in the codebase - not in
            // registration, not in breeding, not in the village engine. So
            // every player's population read 0/10 forever while the Character
            // screen listed the two humans they actually own, and every
            // achievement and score keyed on it was dead.
            //
            // The people who live in your village ARE your characters. That is
            // the table breeding writes, the roster reads and the tick
            // simulates, so it is the one that answers "who lives here".
            int activeResidentCount = await dbContext.CharacterRecords
                .AsNoTracking()
                .CountAsync(c => c.PlayerId == playerId && !c.IsLockedInEscrow);

            var legacyRows = await dbContext.PlayerLegacyLedgers
                .AsNoTracking()
                .Where(l => l.PlayerId == playerId)
                .ToListAsync();
            long shardTotal = 0L;
            int unlockedSlots = 0;
            for (int i = 0; i < legacyRows.Count; i++)
            {
                shardTotal += legacyRows[i].LegacyShardBalance;
                unlockedSlots |= legacyRows[i].CitizenMultiSlotsUnlocked;
            }
            if (shardTotal > int.MaxValue) shardTotal = int.MaxValue;

            int totalAchievements = await dbContext.PlayerLifetimeAchievements
                .AsNoTracking()
                .CountAsync(a => a.PlayerId == playerId && a.IsClaimed);

            var chroniclePass = await dbContext.PlayerChroniclePasses
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PlayerId == playerId);

            long currentUnixTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            (float codexYieldMultiplier, float codexDamageMultiplier) = await CodexEngine.CalculateActiveMultipliersAsync(playerId, dbContext);

            long questLoadEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var dailyQuests = await QuestEngine.EnsureAndLoadDailyQuestsAsync(dbContext, playerId, questLoadEpochSeconds);
            await dbContext.SaveChangesAsync();

            // Modul: Play Mode audit fix. CurrentGold was previously hardcoded
            // to 10000 on every hydration regardless of the real balance -
            // AuthenticationEngine seeds CommodityRecords ItemId="gold" at
            // registration (1000, not 10000) and every gold-earning/-spending
            // path (RedisWriteBehindEngine's delta flush, MarketEscrowEngine,
            // MarketOrderBookEngine) reads/writes that same row, so it is the
            // one authoritative balance - a live Play Mode session confirmed
            // this by comparing a player's real CommodityRecords balance
            // against what every login/reconnect actually loaded.
            byte[] inheritanceLevels = await InheritanceEngine.LoadLevelsAsync(dbContext, playerId);

            // Modul: skill tree. Hydrated the same way and refreshed the same
            // way - SkillTreeSyncQueue carries a purchase back to the tick.
            byte[] skillTreeLevels = await SkillTreeEngine.LoadLevelsAsync(dbContext, playerId);

            // Task 87: the highest Boss Ascension step per boss, packed - the
            // tick's cache of boss_ascension_progress, for start-step validation.
            int bossAscensionPacked = await BossAscensionEngine.LoadPackedAsync(dbContext, playerId);

            // Task 84: the built stages of each Great Work, packed - the cache
            // the gathering yield and the offline cap read (they survive rebirth).
            int greatWorksPacked = await Domain.Progression.GreatWorksEngine.LoadPackedAsync(dbContext, playerId);

            // Modul: SETTLE THE VILLAGE ARRIVAL CLOCK on the way in.
            //
            // Arrivals are hours apart, so checking them on a 10 Hz loop would
            // run three hundred thousand times per villager. What matters is
            // that the roster is right the moment a player looks at it, and a
            // login is the only moment that can be made true of anything they
            // were not watching.
            //
            // Wrapped rather than awaited bare: a village that fails to settle
            // must never block a login. The worst case is that nobody arrived
            // this session, and the next login settles the same elapsed time.
            try
            {
                await VillageArrivalEngine.SettleAsync(dbContext, player, innLevel, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VillageArrival: settle failed for player {playerId}: {ex.Message}");
            }

            long loadedGold = await dbContext.CommodityRecords
                .AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == "gold")
                .Select(c => c.Quantity)
                .FirstOrDefaultAsync();

            // Modul: the Deep's high-water mark, sampled at login from the row
            // just read - wealth that arrived while the player was away (a mail,
            // an offline catch-up banked by the last session's flush) would
            // otherwise not be seen until the first checkpoint. Guarded: a mark
            // that fails to write must never block a login.
            try
            {
                await FolkIdle.Server.Domain.Economy.GoldHighWater.RecordAsync(
                    dbContext, playerId, loadedGold,
                    FolkIdle.Server.Domain.Economy.GoldHighWater.Today(DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GoldHighWater: login sample failed for player {playerId}: {ex.Message}");
            }

            // Modul: Deferred Part 5 Implementation, Part 2 - consumable
            // expiry hydration reference clock (see the ActiveOffensive/
            // Defensive/Food assignments in the payload build below).
            long nowEpochSeconds = questLoadEpochSeconds;

            // Modul: race unlock feedback. Rebuilt from the durable rows rather
            // than persisted as its own column: PlayerRaceUnlocks is already the
            // authority on which races an account owns, and a second copy could
            // disagree with it. Human (race 1) is owned by every account without
            // an unlock row, so its bit is set unconditionally - otherwise the
            // client would announce "Human unlocked" on a brand new account.
            byte unlockedRaceBitmask = 1 << (RaceIds.Human - 1);
            var unlockedRaceIds = await dbContext.PlayerRaceUnlocks
                .AsNoTracking()
                .Where(u => u.PlayerId == playerId)
                .Select(u => u.RaceId)
                .ToListAsync();
            for (int i = 0; i < unlockedRaceIds.Count; i++)
            {
                int raceId = unlockedRaceIds[i];
                if (raceId >= 1 && raceId <= 8)
                {
                    unlockedRaceBitmask |= (byte)(1 << (raceId - 1));
                }
            }

            // Task 79: the wire's Treasury term reads this, not the payload.
            GoldLedger.NoteLifetimeSpent(player.Id, player.LifetimeGoldSpent);

            var payload = new TickStatePayload
            {
                CachedCodexYieldMultiplier = codexYieldMultiplier,
                CachedCodexDamageMultiplier = codexDamageMultiplier,
                PlayerId = player.Id,
                AccountId = ResolveAccountId(player.Id, player.PlayerGuid),
                CurrentLevel = player.CurrentLevel,
                CurrentXp = player.CurrentXp,
                SelectedLineageId = player.SelectedLineageId,
                LastLogoutTimestamp = player.LastLogoutTimestamp,
                // Modul: Deploy activation fix. Was hardcoded to 1. The block
                // further down overwrites this with characters[0]'s real
                // persisted activity, but ONLY when that query returned a
                // character - and it excludes any character currently lent
                // out as a mentor (MentorshipAcademyAssignments). A player
                // whose only character is mentoring therefore silently
                // resumed as though deployed on activity 1 forever, with no
                // character to actually run it. Idle (0) is the honest
                // default for "no eligible character".
                ActiveActivityId = 0,
                CurrentProgressTicks = 0,
                RequiredProgressTicks = 50,
                // Modul: inventory census. Was "20 + bonus" unconditionally,
                // regardless of what the backpack actually held - so a player
                // who logged out with a full pack logged back in with twenty
                // free slots, and the number then only ever fell. Both fields
                // are now derived from a real count of occupied slots taken just
                // above; capacity is carried separately so the difference can be
                // recomputed rather than only decremented.
                InventoryCapacity = backpackCapacity,
                // Modul: vestigial since the backpack was removed - materials go to
                // the unbounded chest and equipment to the bank or to scrap. Full
                // capacity keeps every remaining defensive decrement a no-op.
                InventorySpaceRemaining = backpackCapacity,

                // Modul: larder. Restores the three auto-eat slots and the
                // player's chosen threshold. All four were previously
                // session-only fields with no storage behind them, so the larder
                // was empty at every login and the threshold reverted to the
                // default. A persisted 0 threshold means "never configured" -
                // taking it literally would mean auto-eat only fires at exactly
                // 0 HP, i.e. never.
                Food1_ItemId = player.LarderSlot1ItemId,
                Food1_Count = player.LarderSlot1Count,
                Food2_ItemId = player.LarderSlot2ItemId,
                Food2_Count = player.LarderSlot2Count,
                Food3_ItemId = player.LarderSlot3ItemId,
                Food3_Count = player.LarderSlot3Count,
                AutoEatThreshold = player.AutoEatThresholdPct > 0 ? player.AutoEatThresholdPct : AutoEatDefaults.ThresholdPct,

                // Modul: the auto-salvage floor, so the loot engine can read it
                // off the payload instead of querying per kill. Clamped on the
                // way in as well as on the way out of the settings route: a row
                // written before the cap existed, or by hand, must not be able
                // to salvage a player's Legendaries.
                AutoSalvageBelowTier = System.Math.Clamp(
                    player.AutoSalvageBelowTier, 0, Engine.VillageChestEngine.MaxSweepableQualityTier),
                AutoSalvageRegionTiers = Engine.ChestSalvageRules.Sanitise(player.AutoSalvageRegionTiers),

                PlayerHp = 100000,
                CurrentGold = loadedGold,
                PremiumCurrency = player.PremiumDiamonds,
                GuildId = player.GuildId,
                ActiveGuildWarId = activeGuildWarId,
                ActiveCrossShardMatchId = activeCrossShardMatchId,
                ActiveMatchMmr = activeMatchMmr,
                GlobalNodeRemainingHp = globalNodeRemainingHp,
                CachedMiningMonolithLevel = miningMonolith,
                CachedWoodcuttingMonolithLevel = woodMonolith,
                CachedMentorCount = 0,
                ClaimedAchievementFlags = achievementFlags,
                TotalAchievementsClaimedCount = (uint)totalAchievements,
                CompletedAreaFlags = completedAreas,
                AxeToolTier = toolLoadout.AxeTier,
                PickaxeToolTier = toolLoadout.PickaxeTier,
                RodToolTier = toolLoadout.RodTier,
                ToolGatherSpeedPct = toolLoadout.GatherSpeedPct,
                ToolGatherYieldPct = toolLoadout.GatherYieldPct,
                ToolRareFindPct = toolLoadout.RareFindPct,
                HighestLocationReached = highestLocationReached,
                HighestUnlockedRegion = highestUnlockedRegion,
                DefeatedRegionBossMask = defeatedRegionBossMask,
                BossAscensionPacked = bossAscensionPacked,
                GreatWorksStagesPacked = greatWorksPacked,
                WorldBossAttemptCount = worldBossAttemptCount,
                HumanMasteryLevel = humanMastery,
                VilaMasteryLevel = vilaMastery,
                DraugrMasteryLevel = draugrMastery,
                KoboldMasteryLevel = koboldMastery,
                VodnikMasteryLevel = vodnikMastery,
                MoosleuteMasteryLevel = moosleuteMastery,
                STR = player.BaseStrength,
                DEX = player.BaseDexterity,
                CON = player.BaseConstitution,
                LCK = player.BaseLuck,
                EquippedWeaponId = mainCharacterRecord?.EquippedWeaponId ?? 0L,
                // Modul: which weapon family, for the hit effect - see
                // EquipmentSlotEngine.ResolveWeaponKind. Hydrated here as well
                // as pushed on every equip, so a player who signs in mid-fight
                // still swings the right animation.
                EquippedWeaponKind = await Domain.Combat.EquipmentSlotEngine.ResolveEquippedWeaponKindAsync(dbContext, mainCharacterRecord),
                EquippedHelmetId = mainCharacterRecord?.EquippedHelmetId ?? 0L,
                EquippedArmorId = mainCharacterRecord?.EquippedChestId ?? 0L,
                EquippedGlovesId = mainCharacterRecord?.EquippedGlovesId ?? 0L,
                EquippedLeggingsId = mainCharacterRecord?.EquippedLeggingsId ?? 0L,
                EquippedBootsId = mainCharacterRecord?.EquippedBootsId ?? 0L,
                EquippedAmuletId = mainCharacterRecord?.EquippedAmuletId ?? 0L,
                EquippedRingId = mainCharacterRecord?.EquippedRingId ?? 0L,

                // Modul: lifetime statistics. Hydrated so the tick thread can
                // keep an absolute running total - see TickStatePayload.
                LifetimeDeaths = player.TotalDeaths,
                LifetimeItemsCrafted = player.TotalItemsCrafted,
                WoodcuttingMasteryXp = player.WoodcuttingMasteryXp,
                WoodcuttingMasteryLevel = player.WoodcuttingMasteryLevel,
                MiningMasteryXp = player.MiningMasteryXp,
                MiningMasteryLevel = player.MiningMasteryLevel,
                FishingMasteryXp = player.FishingMasteryXp,
                FishingMasteryLevel = player.FishingMasteryLevel,
                HerbalismMasteryXp = player.HerbalismMasteryXp,
                HerbalismMasteryLevel = player.HerbalismMasteryLevel,
                // Modul: race unlock feedback. Rebuilt from the durable
                // PlayerRaceUnlocks rows, so the mask is correct after a
                // reconnect and for accounts that unlocked races before this
                // field existed.
                UnlockedRaceBitmask = unlockedRaceBitmask,
                PlayTimeSecondsAtLogin = player.TotalPlayTimeSeconds,
                SessionStartEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                XpPenaltyExpiresEpoch = player.XpPenaltyExpiresEpoch,

                // Modul: Deferred Part 5 Implementation, Part 2. Durable
                // consumable hydration - the persisted absolute expiry
                // epochs convert back to live millisecond countdowns
                // against the server clock; an already-expired buff loads
                // as inactive (id 0, countdown 0).
                ActiveOffensivePotionId = player.ActiveOffensivePotionExpiresEpoch > nowEpochSeconds ? player.ActiveOffensivePotionId : 0,
                OffensivePotionDurationMs = player.ActiveOffensivePotionExpiresEpoch > nowEpochSeconds ? (int)Math.Min(int.MaxValue, (player.ActiveOffensivePotionExpiresEpoch - nowEpochSeconds) * 1000L) : 0,
                ActiveDefensivePotionId = player.ActiveDefensivePotionExpiresEpoch > nowEpochSeconds ? player.ActiveDefensivePotionId : 0,
                DefensivePotionDurationMs = player.ActiveDefensivePotionExpiresEpoch > nowEpochSeconds ? (int)Math.Min(int.MaxValue, (player.ActiveDefensivePotionExpiresEpoch - nowEpochSeconds) * 1000L) : 0,
                ActiveFoodBuffId = player.ActiveFoodExpiresEpoch > nowEpochSeconds ? player.ActiveFoodId : 0,
                FoodBuffDurationMs = player.ActiveFoodExpiresEpoch > nowEpochSeconds ? (int)Math.Min(int.MaxValue, (player.ActiveFoodExpiresEpoch - nowEpochSeconds) * 1000L) : 0,
                CachedAffixTotals = equippedAffixTotals,
                CachedSetIds = equippedSetIds,
                LogicEpochCounter = player.LogicEpochCounter,
                LegacyShardBalance = (int)shardTotal,
                CitizenMultiSlotsUnlocked = unlockedSlots,
                GuildLogisticsCurrentStock = guildLogisticsStock,
                GuildLogisticsTargetRequirement = guildLogisticsTarget,
                CombatSimulationMatchId = combatMatchId,
                CombatSimulationTurnCounter = combatTurnCounter,
                CombatSimulationDamageDelta = 0,
                ActiveMentorPlayerId = 0L,
                MentorshipExpBonusMultiplier = 1.0,
                ForgeLevel = ClampByte(forgeLevel),
                InnLevel = ClampByte(innLevel),
                BreedingLevel = ClampByte(breedingLevel),
                AcademyLevel = ClampByte(academyLevel),
                CurrentPopulationCount = ClampByte(activeResidentCount),
                ActiveMentorshipContractCount = 0,
                LumberjackLevel = ClampByte(lumberjackLevel),
                MineLevel = ClampByte(mineLevel),
                WarehouseLevel = ClampByte(warehouseLevel),
                TownHallLevel = townHallLevel,
                CraftingWorkshopLevel = ClampByte(craftingWorkshopLevel),
                PendingUpgradeBuildingId = pendingUpgradeBuildingId,
                PendingUpgradeCompletesAtEpoch = pendingUpgradeCompletesAtEpoch,
                CachedWoodStock = woodStock,
                CachedStoneStock = stoneStock,
                CachedIronOreStock = ironOreStock,
                CachedCurrentToolTier = forgeLevel,
                CachedLegacyPerks = player.LegacyPerks,

                // Modul: inheritance stats. Read once at hydration - they only
                // change on a purchase, which pushes the new level onto the
                // live payload through InheritanceSyncQueue.
                Inherit_Damage = inheritanceLevels[InheritanceRegistry.StatDamage],
                Inherit_MaxHp = inheritanceLevels[InheritanceRegistry.StatMaxHp],
                Inherit_XpGain = inheritanceLevels[InheritanceRegistry.StatXpGain],
                Inherit_GoldGain = inheritanceLevels[InheritanceRegistry.StatGoldGain],
                Inherit_GatheringYield = inheritanceLevels[InheritanceRegistry.StatGatheringYield],
                Inherit_LootLuck = inheritanceLevels[InheritanceRegistry.StatLootLuck],
                // Task 88: RebirthEngine's column, read-only here.
                RenownedRebirths = player.RenownedRebirths,
                Skill_LootRarity = skillTreeLevels[SkillTreeRegistry.BranchLootRarity],
                Skill_WorldBossDamage = skillTreeLevels[SkillTreeRegistry.BranchWorldBossDamage],
                Skill_CritChance = skillTreeLevels[SkillTreeRegistry.BranchCritChance],
                Skill_CritDamage = skillTreeLevels[SkillTreeRegistry.BranchCritDamage],
                Skill_XpGain = skillTreeLevels[SkillTreeRegistry.BranchXpGain],
                Skill_Plenty = skillTreeLevels[SkillTreeRegistry.BoughPlenty],
                Skill_Rarity = skillTreeLevels[SkillTreeRegistry.BoughRarity],
                Skill_FirstBlood = skillTreeLevels[SkillTreeRegistry.BoughFirstBlood],
                Skill_TrophyHunter = skillTreeLevels[SkillTreeRegistry.BoughTrophyHunter],
                Skill_Guile = skillTreeLevels[SkillTreeRegistry.BoughGuile],
                Skill_Relentless = skillTreeLevels[SkillTreeRegistry.BoughRelentless],
                Skill_Bloodthirst = skillTreeLevels[SkillTreeRegistry.BoughBloodthirst],
                Skill_Fortitude = skillTreeLevels[SkillTreeRegistry.BoughFortitude],
                Skill_Craft = skillTreeLevels[SkillTreeRegistry.BoughCraft],
                Skill_Harvest = skillTreeLevels[SkillTreeRegistry.BoughHarvest],
                Skill_GoldenFleece = skillTreeLevels[SkillTreeRegistry.CrownGoldenFleece],
                Skill_Thunderer = skillTreeLevels[SkillTreeRegistry.CrownThunderer],
                Skill_DoubleStrike = skillTreeLevels[SkillTreeRegistry.CrownDoubleStrike],
                Skill_LastStand = skillTreeLevels[SkillTreeRegistry.CrownLastStand],
                Skill_Scholar = skillTreeLevels[SkillTreeRegistry.CrownScholar],
                FreeRespecUsed = (byte)(player.FreeRespecUsed ? 1 : 0),
                PaidRespecGrants = (byte)Math.Clamp(player.PaidRespecGrants, 0, 255),
                CachedLogisticsGatheringSpeedBonusPct = player.LogisticsGatheringSpeedBonusPct,
                CachedMaxPopulationCapacity = VillageManagementEngine.CalculatePopulationCapacity(innLevel),
                CachedInnMaturationBonus = innLevel,
                Quarantine_Active = player.Quarantine_Active || player.IsQuarantined,
                IsQuarantined = player.IsQuarantined,
                ActiveLanguageState = 1,
                ActiveChroniclePassLevel = (uint)Math.Max(0, chroniclePass?.PassLevel ?? 0),
                AccumulatedSeasonalXp = (uint)Math.Max(0, chroniclePass?.AccumulatedXp ?? 0),
                CachedClaimedMilestonesBitmask = chroniclePass?.ClaimedMilestonesBitmask ?? 0UL,
                AvailableSkillPoints = player.AvailableSkillPoints,
                UnspentAttributePoints = player.UnspentAttributePoints,
                // Task 51: the records ride the wire, so they are loaded here or
                // a relogin reads zero (StateUpdatePacketFieldCoverageTests).
                BestHit = player.BestHit,
                BossBestKillTenthsR1 = player.BossBestKillTenthsR1,
                BossBestKillTenthsR2 = player.BossBestKillTenthsR2,
                BossBestKillTenthsR3 = player.BossBestKillTenthsR3,
                BossBestKillTenthsR4 = player.BossBestKillTenthsR4,
                BossBestKillTenthsR5 = player.BossBestKillTenthsR5
            };

            payload.InitializeObfuscation(GenerateSessionXorKey(playerId, player.LogicEpochCounter));

            QuestEngine.ApplyToPayload(ref payload, dailyQuests, QuestEngine.GetUtcDateKey(questLoadEpochSeconds));

            // Modul: halt reasons. The query above deliberately excludes
            // escrowed characters and any character lent out as an Academy
            // mentor, so a player can legitimately own characters and still
            // have none eligible to deploy. That produced a hub that offered
            // no explanation for why nothing could be started.
            if (characters.Count == 0)
            {
                payload.ActivityHaltReason = Network.ActivityHaltReason.NoEligibleCharacter;
            }

            if (characters.Count > 0)
            {
                payload.Slot1_CharacterId = characters[0].Id;
                payload.Slot1_AgeTicks = characters[0].AgeTicks;
                payload.Slot1_AgePhase = characters[0].AgePhase;
                payload.Slot1_GeneticVector = characters[0].Lineage?.GeneticVector ?? 0;

                // Modul: and the active character's aptitudes. Defaulted to
                // the starting value rather than zero when a character has no
                // lineage row at all - an unbred founder is ordinary, not
                // worthless, and zeroing it would quietly make every pre-
                // breeding character weaker than a newly created one.
                var aptLineage = characters[0].Lineage;
                payload.Aptitude_Strength = (byte)(aptLineage?.AptitudeStrength ?? Engine.BreedingAptitudes.StartingValue);
                payload.Aptitude_Skill = (byte)(aptLineage?.AptitudeSkill ?? Engine.BreedingAptitudes.StartingValue);
                payload.Aptitude_Endurance = (byte)(aptLineage?.AptitudeEndurance ?? Engine.BreedingAptitudes.StartingValue);
                payload.Aptitude_Fortune = (byte)(aptLineage?.AptitudeFortune ?? Engine.BreedingAptitudes.StartingValue);

                // Modul: Play Mode audit fix. ActiveActivityId was hardcoded
                // to 1 above regardless of what the character was actually
                // doing - SimulationEngine.ChangeCharacterActivityAsync
                // correctly persists a real activity onto characters[0] and
                // then immediately triggers a ReloadState to pick it back up
                // live, but LoadPlayerState never read it, so the reload
                // instantly reverted the character to idle. Confirmed live:
                // deploying a fresh character against a monster wrote
                // ActiveActivityId=55 onto its characters row correctly, but
                // every subsequent broadcast kept reporting activity 0/1 and
                // combat never resolved (CurrentMonsterHp stayed 0 forever).
                payload.ActiveActivityId = characters[0].ActiveActivityId;

                // Modul 13.4.3: lineage flags and traits for the active (Slot1)
                // character only - combat/growth are always evaluated against
                // whichever character occupies Slot1, matching activeRaceId's
                // existing derivation.
                var slot1Lineage = characters[0].Lineage;
                if (slot1Lineage != null)
                {
                    payload.IsEpicMutation = slot1Lineage.IsEpicMutation;
                    payload.TraitMask = slot1Lineage.TraitMask;
                }
            }
            if (characters.Count > 1)
            {
                payload.Slot2_CharacterId = characters[1].Id;
                payload.Slot2_AgeTicks = characters[1].AgeTicks;
                payload.Slot2_AgePhase = characters[1].AgePhase;
                payload.Slot2_GeneticVector = characters[1].Lineage?.GeneticVector ?? 0;

                // Modul: multi-slot simulation. These slots' persisted activity
                // assignments were loaded nowhere - only characters[0]'s was
                // read - so a second or third character came back from every
                // login idle regardless of what the player had assigned, and
                // nothing simulated them anyway. Both halves are fixed now.
                payload.Slot2Activity.ActiveActivityId = characters[1].ActiveActivityId;
                payload.Slot2Activity.PlayerHp = CharacterSlotDefaults.MilliHp;
                payload.Slot2Activity.RequiredProgressTicks = CharacterSlotDefaults.RequiredProgressTicks;
                payload.Slot2Activity = await HydrateSlotEquipmentAsync(dbContext, characters[1], payload.Slot2Activity);
            }
            if (characters.Count > 2)
            {
                payload.Slot3_CharacterId = characters[2].Id;
                payload.Slot3_AgeTicks = characters[2].AgeTicks;
                payload.Slot3_AgePhase = characters[2].AgePhase;
                payload.Slot3_GeneticVector = characters[2].Lineage?.GeneticVector ?? 0;

                payload.Slot3Activity.ActiveActivityId = characters[2].ActiveActivityId;
                payload.Slot3Activity.PlayerHp = CharacterSlotDefaults.MilliHp;
                payload.Slot3Activity.RequiredProgressTicks = CharacterSlotDefaults.RequiredProgressTicks;
                payload.Slot3Activity = await HydrateSlotEquipmentAsync(dbContext, characters[2], payload.Slot3Activity);
            }

            return payload;
        }

        // Modul: lifetime statistics. Folds the session's running totals back
        // onto the row, shared by the single-flush and batch-flush paths.
        //
        // A method rather than two copies of three lines specifically because
        // those two paths exist: this codebase has repeatedly shipped bugs
        // where one of a pair of mirrored branches was updated and the other
        // was not (the VisualSyncProxy first-packet vs steady-state fields, the
        // two level-curve mirrors). One call site each is one thing to keep
        // right instead of two.
        //
        // Playtime is recomputed from the session start rather than
        // accumulated, so a checkpoint that fires twice in one second cannot
        // inflate it, and a session that never checkpoints loses at most the
        // time since its last one.
        private static void ApplyLifetimeStatistics(PlayerRecord player, TickStatePayload state)
        {
            player.TotalDeaths = state.LifetimeDeaths;

            // Gathering mastery. Tick thread is the sole author, so an absolute
            // snapshot is correct here.
            player.WoodcuttingMasteryXp = state.WoodcuttingMasteryXp;
            player.WoodcuttingMasteryLevel = state.WoodcuttingMasteryLevel;
            player.MiningMasteryXp = state.MiningMasteryXp;
            player.MiningMasteryLevel = state.MiningMasteryLevel;
            player.FishingMasteryXp = state.FishingMasteryXp;
            player.FishingMasteryLevel = state.FishingMasteryLevel;
            player.HerbalismMasteryXp = state.HerbalismMasteryXp;
            player.HerbalismMasteryLevel = state.HerbalismMasteryLevel;

            long sessionSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - state.SessionStartEpochSeconds;
            if (sessionSeconds < 0L) sessionSeconds = 0L;
            player.TotalPlayTimeSeconds = state.PlayTimeSecondsAtLogin + sessionSeconds;
        }

        // Modul: per-character equipment. Loads one non-main character's gear
        // and the stat totals derived from it into its parked slot state.
        //
        // The equipped ids are persisted but the derived totals are not, so a
        // character whose gear was never recomputed at login would fight naked
        // until its owner happened to re-equip something - the exact bug the
        // main character's login-time recompute was added to prevent, repeated
        // once per extra slot.
        // Taken and returned by value rather than by ref: async methods cannot
        // have ref parameters, and a struct copy at login time costs nothing.
        private static async Task<CharacterActivityState> HydrateSlotEquipmentAsync(FolkIdleDbContext dbContext, CharacterRecord character, CharacterActivityState slot)
        {
            slot.EquippedWeaponId = character.EquippedWeaponId ?? 0L;
            slot.EquippedHelmetId = character.EquippedHelmetId ?? 0L;
            slot.EquippedChestId = character.EquippedChestId ?? 0L;
            slot.EquippedGlovesId = character.EquippedGlovesId ?? 0L;
            slot.EquippedLeggingsId = character.EquippedLeggingsId ?? 0L;
            slot.EquippedBootsId = character.EquippedBootsId ?? 0L;
            slot.EquippedAmuletId = character.EquippedAmuletId ?? 0L;
            slot.EquippedRingId = character.EquippedRingId ?? 0L;

            (EquippedAffixTotals totals, EquippedSetIds setIds) =
                await EquipmentSlotEngine.ComputeEquippedTotalsAsync(dbContext, character);

            slot.CachedAffixTotals = totals;
            slot.CachedSetIds = setIds;

            return slot;
        }

        private static byte ClampByte(int value)
        {
            if (value <= 0) return 0;
            if (value >= byte.MaxValue) return byte.MaxValue;
            return (byte)value;
        }

        private static long GenerateSessionXorKey(long playerId, long epoch)
        {
            ulong x = (ulong)playerId;
            x ^= (ulong)epoch + 0x9E3779B97F4A7C15UL + (x << 6) + (x >> 2);
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            long key = unchecked((long)x);
            return key == 0L ? 0x5F3759DF5F3759DFL : key;
        }

        private static Guid ResolveAccountId(long playerId, Guid playerGuid)
        {
            if (playerGuid != Guid.Empty)
            {
                return playerGuid;
            }

            byte[] bytes = new byte[16];
            BitConverter.GetBytes(playerId).CopyTo(bytes, 0);
            bytes[15] = 0x67;
            return new Guid(bytes);
        }

        /// <summary>
        /// Writes the session's unbanked gold to the one authoritative balance,
        /// CommodityRecords["gold"], when Redis was not there to buffer it.
        ///
        /// An INCREMENT under FOR UPDATE, never an assignment. The live payload
        /// is not the authority on this number - a market sale, a mailbox claim
        /// or an affix reroll can move the same row between two checkpoints, and
        /// writing state.CurrentGold over the top would silently undo whichever
        /// of them landed last. The delta is what this session earned; adding it
        /// is the only statement that stays true alongside the other writers.
        ///
        /// The caller zeroes the delta on the live payload only after this
        /// transaction commits - see FlushStateAndAdvance.
        /// </summary>
        private static async Task ApplyPendingGoldDeltaAsync(FolkIdleDbContext dbContext, TickStatePayload state)
        {
            if (state.RedisPendingGoldDelta == 0L)
            {
                return;
            }

            var gold = await dbContext.CommodityRecords
                .FromSqlInterpolated($"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {state.PlayerId} AND \"ItemId\" = 'gold' FOR UPDATE")
                .FirstOrDefaultAsync();

            // Modul: only the missing-row branch is an upsert (CommodityLedger).
            // The existing-row branch stays a locked += because it CLAMPS at
            // zero, which a generic add cannot know to do. The delta passed
            // here is clamped the same way the old insert was.
            if (gold == null)
            {
                // GoldLedger: banks the delta; its income was tallied where it was earned.
                await CommodityLedger.AddAsync(dbContext, state.PlayerId, "gold", Math.Max(0L, state.RedisPendingGoldDelta));
                return;
            }

            // GoldLedger: banks the delta; its income was tallied where it was earned.
            gold.Quantity += state.RedisPendingGoldDelta;
            if (gold.Quantity < 0L) gold.Quantity = 0L;
        }

        /// <summary>
        /// Banks the session's live village production (Lumberjack, Quarry,
        /// Mine) that Redis did not take, as an increment, and records it as
        /// Gathered in task 79's MaterialLedger - the same row
        /// RedisWriteBehindEngine.ApplyCommodityDeltaAsync writes when Redis
        /// does take it.
        /// </summary>
        /// <remarks>
        /// Modul: LIVE VILLAGE PRODUCTION HAD NO DURABLE PATH WITHOUT REDIS
        /// (2026-09-30). Pending*Delta's only writer to the database was
        /// TryStoreFrame -> a Redis buffer -> write-behind, and both return
        /// early with Redis down - so the stock grew on screen (CachedWoodStock)
        /// and was discarded at logout. Gold's old defect, given gold's answer.
        ///
        /// NEVER BANKED TWICE: every checkpoint entry point runs TryStoreFrame
        /// before it snapshots (RequestFlush, FlushStateAndAdvance), and a
        /// frame that succeeds moves these deltas into the Redis buffers and
        /// zeroes them - so what reaches here is exactly what Redis never had.
        /// FlushBatch does not frame; what it sees on the payload likewise
        /// never reached Redis. VillageProductionCheckpointTests.
        /// </remarks>
        private static async Task ApplyPendingVillageProductionAsync(FolkIdleDbContext dbContext, TickStatePayload state)
        {
            await BankVillageDeltaAsync(dbContext, state.PlayerId, VillageManagementEngine.WoodCommodityId, state.PendingWoodDelta);
            await BankVillageDeltaAsync(dbContext, state.PlayerId, VillageManagementEngine.StoneCommodityId, state.PendingStoneDelta);
            await BankVillageDeltaAsync(dbContext, state.PlayerId, VillageManagementEngine.IronOreCommodityId, state.PendingIronDelta);
        }

        private static async Task BankVillageDeltaAsync(FolkIdleDbContext dbContext, long playerId, string itemId, long delta)
        {
            if (delta <= 0L)
            {
                return;
            }

            await CommodityLedger.AddAsync(dbContext, playerId, itemId, delta);
            await MaterialLedger.RecordAsync(dbContext, playerId, MaterialFlowDirection.Gathered, itemId, delta);
        }

        private static async Task UpsertChroniclePassAsync(FolkIdleDbContext dbContext, TickStatePayload state)
        {
            var pass = await dbContext.PlayerChroniclePasses
                .FromSqlRaw("SELECT * FROM \"PlayerChroniclePasses\" WHERE \"PlayerId\" = {0} FOR UPDATE", state.PlayerId)
                .FirstOrDefaultAsync();

            int passLevel = (int)Math.Min(50U, state.ActiveChroniclePassLevel);
            int seasonalXp = (int)Math.Min(int.MaxValue, state.AccumulatedSeasonalXp);

            if (pass == null)
            {
                dbContext.PlayerChroniclePasses.Add(new PlayerChroniclePass
                {
                    PlayerId = state.PlayerId,
                    PassLevel = passLevel,
                    AccumulatedXp = seasonalXp,
                    ClaimedMilestonesBitmask = 0UL
                });
                return;
            }

            if (pass.PassLevel < passLevel)
            {
                pass.PassLevel = passLevel;
            }

            if (pass.AccumulatedXp < seasonalXp)
            {
                pass.AccumulatedXp = seasonalXp;
            }
        }

        // Modul 13: auto-awarded tiered (I-IV) achievements, evaluated against
        // the live counters accumulated in TickStatePayload since the last
        // checkpoint. Distinct from the pre-existing player-claimed "kill 10000
        // monsters" achievement (AchievementId 1, handled by
        // AchievementEngine.ProcessClaimsQueueAsync) - these auto-award, no
        // client claim action required.
        private static async Task UpsertLifetimeAchievementsAsync(FolkIdleDbContext dbContext, PlayerRecord player, TickStatePayload state)
        {
            // Task 79: the Treasury pays on gold SPENT, read from the row the
            // ledger increments (never from the payload). Tiers already paid for
            // holding gold stay paid - the award only ever moves CompletedTier up.
            GoldLedger.NoteLifetimeSpent(state.PlayerId, player.LifetimeGoldSpent);
            await EvaluateAndAwardTierAsync(dbContext, player, state.PlayerId, AchievementMilestones.TreasuryAchievementId,
                AchievementMilestones.EvaluateTreasuryTier(player.LifetimeGoldSpent), player.LifetimeGoldSpent);

            await EvaluateAndAwardTierAsync(dbContext, player, state.PlayerId, AchievementMilestones.ForgingAchievementId,
                AchievementMilestones.EvaluateForgingTier(state.ForgeUpgradeCount, state.HighestForgeSynthesisTier), state.ForgeUpgradeCount);

            await EvaluateAndAwardTierAsync(dbContext, player, state.PlayerId, AchievementMilestones.LogisticsAchievementId,
                AchievementMilestones.EvaluateLogisticsTier(state.HarvestLoopCount), state.HarvestLoopCount);
        }

        private static async Task EvaluateAndAwardTierAsync(FolkIdleDbContext dbContext, PlayerRecord player, long playerId, int achievementId, int newTier, long currentProgress)
        {
            var record = await dbContext.PlayerLifetimeAchievements
                .FromSqlInterpolated($"SELECT * FROM \"player_lifetime_achievements\" WHERE \"PlayerId\" = {playerId} AND \"AchievementId\" = {achievementId} FOR UPDATE")
                .FirstOrDefaultAsync();

            if (record == null)
            {
                record = new PlayerLifetimeAchievement
                {
                    PlayerId = playerId,
                    AchievementId = achievementId,
                    CurrentProgress = 0,
                    CompletedTier = 0,
                    IsClaimed = false
                };
                dbContext.PlayerLifetimeAchievements.Add(record);
            }

            if (newTier > record.CompletedTier)
            {
                int diamondsAwarded = AchievementMilestones.GetDiamondsForTiersCrossed(achievementId, record.CompletedTier, newTier);
                int statBonusAwarded = AchievementMilestones.GetStatBonusForTiersCrossed(achievementId, record.CompletedTier, newTier);
                record.CompletedTier = newTier;
                player.PremiumDiamonds += diamondsAwarded;

                if (achievementId == AchievementMilestones.LogisticsAchievementId && statBonusAwarded > 0)
                {
                    player.LogisticsGatheringSpeedBonusPct += statBonusAwarded;
                }
            }

            record.CurrentProgress = currentProgress;
        }

        public void FlushAllGracefully()
        {
            var states = _dirtyStates.Values.ToList();
            bool committed = FlushBatch(states).GetAwaiter().GetResult();
            if (committed)
            {
                _dirtyStates.Clear();
            }
            else
            {
                // Shutdown-time flush failed even after retries - there is
                // no "next cycle" left to requeue onto since the process is
                // exiting, so this is a genuine, unavoidable loss for this
                // batch. Left in _dirtyStates (not cleared) and logged
                // loudly rather than silently discarded, so this is visible
                // in shutdown logs instead of vanishing the same way the
                // per-tick path used to.
                Console.WriteLine($"FlushAllGracefully: failed to persist {states.Count} dirty player state(s) during shutdown flush.");
            }
        }

        public async Task<bool> FlushBatch(System.Collections.Generic.IEnumerable<TickStatePayload> states)
        {
            var stateList = new System.Collections.Generic.List<TickStatePayload>(states);
            if (stateList.Count == 0)
            {
                return true;
            }

            var retryingOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
            await using var dbContext = new FolkIdleDbContext(retryingOptions.Options);

            var strategy = dbContext.Database.CreateExecutionStrategy();
            try
            {
                await strategy.ExecuteAsync(async () =>
                {
                    dbContext.ChangeTracker.Clear();

                    using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                    foreach (var state in stateList)
                    {
                        var player = await dbContext.PlayerRecords
                            .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", state.PlayerId)
                            .FirstOrDefaultAsync();

                        if (player == null) continue;

                        // Split-brain sieve on batch: skip divergent records silently (they were handled in single-flush path).
                        if (player.LogicEpochCounter > state.LogicEpochCounter) continue;

                        player.CurrentLevel = state.CurrentLevel;
                        player.CurrentXp = state.CurrentXp;
                        // Modul: lifetime statistics. Absolute assignment, not
                        // +=, so re-flushing the same snapshot is a no-op -
                        // see TickStatePayload.LifetimeDeaths.
                        ApplyLifetimeStatistics(player, state);
                        player.SelectedLineageId = state.SelectedLineageId;
                        player.LastLogoutTimestamp = state.LastLogoutTimestamp;
                        Domain.Progression.PersonalRecords.MergeInto(player, in state);
                        player.ActiveOffensivePotionId = state.ActiveOffensivePotionId;
                        player.OffensivePotionDurationMs = state.OffensivePotionDurationMs;
                        player.ActiveDefensivePotionId = state.ActiveDefensivePotionId;
                        player.DefensivePotionDurationMs = state.DefensivePotionDurationMs;
                        player.LogicEpochCounter = state.LogicEpochCounter + 1;
                        // Not written here either - one writer, see the
                        // main flush path above.
                        player.IsQuarantined = state.IsQuarantined;
                        player.BaseStrength = state.STR;
                        player.BaseDexterity = state.DEX;
                        player.BaseConstitution = state.CON;
                        player.BaseLuck = state.LCK;
                        // Modul: per-character equipment. The flush used to
                        // mirror the payload's equipped ids back onto
                        // PlayerRecords. Equipment now lives on CharacterRecord
                        // and EquipmentSlotEngine is its only writer, committing
                        // inside its own Serializable transaction - so the
                        // payload's copy is a read-through cache and writing it
                        // back here would let a stale register overwrite a fresh
                        // equip that landed between two checkpoints.
                        long consumableFlushEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        player.ActiveOffensivePotionId = state.ActiveOffensivePotionId;
                        player.ActiveOffensivePotionExpiresEpoch = state.OffensivePotionDurationMs > 0 ? consumableFlushEpoch + state.OffensivePotionDurationMs / 1000L : 0L;
                        player.ActiveDefensivePotionId = state.ActiveDefensivePotionId;
                        player.ActiveDefensivePotionExpiresEpoch = state.DefensivePotionDurationMs > 0 ? consumableFlushEpoch + state.DefensivePotionDurationMs / 1000L : 0L;
                        player.ActiveFoodId = state.ActiveFoodBuffId;
                        player.ActiveFoodExpiresEpoch = state.FoodBuffDurationMs > 0 ? consumableFlushEpoch + state.FoodBuffDurationMs / 1000L : 0L;
                        player.XpPenaltyExpiresEpoch = state.XpPenaltyExpiresEpoch;
                        player.PremiumDiamonds = state.PremiumCurrency;
                        await UpsertChroniclePassAsync(dbContext, state);
                        // Task 79: the shutdown flush counts the tick's income
                        // too. Written only past the epoch sieve above, which
                        // is what keeps it once: SIGTERM runs ShutdownGracefully
                        // and then ProcessExit runs it again over the same
                        // payloads, and the second pass is skipped there
                        // because the first already advanced the epoch.
                        await GoldLedger.RecordIncomeTallyAsync(dbContext, state.PlayerId, state.PendingGoldIncome);
                        // Modul: THE SHUTDOWN FLUSH NEVER BANKED THE GOLD
                        // (2026-09-30). This recorded the tally above and
                        // dropped the coins it tallies: with Redis down,
                        // RedisPendingGoldDelta is the only record of gold
                        // earned since the last checkpoint - and of gold a
                        // failed flush's ack handed back during
                        // DrainCheckpointWriterForShutdown. Every deploy lost it
                        // while gold_income_daily said it had been earned. Same
                        // increment FlushState applies, and kept once by the
                        // same epoch sieve as the tally. With Redis up the
                        // delta is almost always zero here (TrackState moved it
                        // into the buffer, which StopAndFlushAsync banks after
                        // this); whatever is still on the payload never
                        // reached Redis, so banking it cannot pay twice.
                        // ShutdownGoldBankingTests.
                        await ApplyPendingGoldDeltaAsync(dbContext, state);
                        await ApplyPendingVillageProductionAsync(dbContext, state);

                        if (state.Slot1_CharacterId != System.Guid.Empty)
                        {
                            var c1 = await dbContext.CharacterRecords.FindAsync(state.Slot1_CharacterId);
                            if (c1 != null) { c1.AgeTicks = state.Slot1_AgeTicks; c1.AgePhase = state.Slot1_AgePhase; }
                        }
                        // Shutdown flushes everyone through here, not FlushState.
                        await PersistFieldedActivityAsync(dbContext, state.PlayerId, state.Slot1_CharacterId, state.ActiveActivityId);
                        if (state.Slot2_CharacterId != System.Guid.Empty)
                        {
                            var c2 = await dbContext.CharacterRecords.FindAsync(state.Slot2_CharacterId);
                            if (c2 != null) { c2.AgeTicks = state.Slot2_AgeTicks; c2.AgePhase = state.Slot2_AgePhase; }
                        }
                        if (state.Slot3_CharacterId != System.Guid.Empty)
                        {
                            var c3 = await dbContext.CharacterRecords.FindAsync(state.Slot3_CharacterId);
                            if (c3 != null) { c3.AgeTicks = state.Slot3_AgeTicks; c3.AgePhase = state.Slot3_AgePhase; }
                        }
                    }

                    await dbContext.SaveChangesAsync();
                    await transaction.CommitAsync();
                });
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to flush batch: {ex.Message}");
                return false;
            }
        }
    }
}
