using System;
using System.Collections.Concurrent;
using System.Data;
using System.Linq;
using System.Threading;
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
using FolkIdle.Server.Domain.Combat.WorldBossStrike;

namespace FolkIdle.Server.Engine
{
    public class WorldBossDamagePayload
    {
        public long PlayerId { get; set; }
        public long Damage { get; set; }
    }

    /// <summary>
    /// How one strike ended. Every value but Landed is something the player
    /// is told - see WorldBossEngine.ResultCodeFor.
    /// </summary>
    public enum WorldBossAttackOutcome : byte
    {
        Landed = 0,
        InvalidRequest = 1,
        NotActive = 2,
        AlreadyDefeated = 3,
        NoAttemptsLeft = 4,
        SessionClosed = 5,
        Failed = 6,
    }

    public class WorldBossEngine : IWorldBossStrikeBoard
    {
        public const uint ActiveBossInstanceId = 1;
        public const uint MaxClientPredictedDamage = 100000000;

        // Modul: THE BOSS WEARS ARMOUR NOW, and the whole point is that the
        // client stops sending a damage figure.
        //
        // Until 2026-09-05 an attack posted `clientPredictedDamage` - a number
        // the client computed about itself - and the only thing between it and
        // a shared health pool was the clamp above. It sends a PLATE INDEX now,
        // which has nothing to bound because it is not a quantity, and the
        // server takes the damage from the player's own cached attack power.
        //
        // Five plates against three attempts. Three of each was the first
        // design and it was wrong: a blind player cannot fail to find the weak
        // point in three tries out of three, so knowing where it is would be
        // worth 1.2x and nobody would ever read the board. At five it is worth
        // 1.67x. See docs/world_boss_design.md for the table.
        public const int PlateCount = 5;

        // A strike on the weak point. A strike anywhere else does full normal
        // damage - deliberately NOT reduced, so a player who guesses wrong
        // loses an upside rather than paying a fine, and has no reason to wait
        // for someone else to strip the armour.
        public const double WeakPlateDamageMultiplier = 3.0;

        /// <summary>On the wire while nobody has landed on the weak point yet.</summary>
        public const byte WeakPlateHidden = 255;
        private const long BaseHp = 50000000L;

        /// <summary>The least one strike's BASE hit is worth, before any plate or skill multiplier.</summary>
        public const long MinStrikeDamage = 1000L;
        /// <summary>Strikes per player per UTC day - see WorldBossCalendar. Was 3 per encounter until 2026-09-25.</summary>
        internal const int MaxAttemptsPerDay = WorldBossCalendar.StrikesPerDay;

        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;
        private readonly IConnectionMultiplexer? _redis;
        private long _bossMaxHp = BaseHp;
        private long _bossCurrentHp = BaseHp;
        private int _bossIsAlive = 1;
        private int _rewardDispatchActive;
        private int _eventState;
        private long _eventEndEpoch;

        // Modul: the armour, mirrored in memory for the same reason the health
        // is - the broadcast reads this on the tick thread once per player per
        // snapshot and must not touch the database to do it.
        //
        // _weakPlate is 255 while the weak point is still a secret. The
        // snapshot holds the real index either way; this mirror only ever
        // carries it once WeakPlateRevealed is set, so a bug that leaks this
        // field cannot leak the answer.
        private int _brokenPlateMask;
        private int _weakPlate = WeakPlateHidden;

        private readonly ConcurrentDictionary<long, long> _playerDamageMap = new();

        // Modul: ONE WRITER AT A TIME ON THE BOSS ROW (2026-09-25). Every strike,
        // the minute-by-minute rescale, the reward payout and opening or closing
        // an encounter take the one WorldBossSnapshots row FOR UPDATE inside a
        // Serializable transaction. When two met, the loser did not wait its
        // turn: Postgres failed it with a serialization error, and the player saw
        // "the strike could not be recorded". That covers two players striking
        // together, or a strike landing on LiveOps' rescale. The second tap of a
        // double-tap showed it (code 42 where "strike already spent" belonged)
        // once one-a-day made the second strike a same-row race. Strikes are
        // now one a player a day, so queueing them costs nothing and turns a
        // spurious failure into the right answer.
        private readonly SemaphoreSlim _snapshotGate = new(1, 1);

        public long BossMaxHp => Interlocked.Read(ref _bossMaxHp);
        public long BossCurrentHp => Interlocked.Read(ref _bossCurrentHp);
        public bool IsAlive => Volatile.Read(ref _bossIsAlive) == 1 && BossCurrentHp > 0;
        public bool IsEventActive => Volatile.Read(ref _eventState) == 1;
        public byte EventState => (byte)Volatile.Read(ref _eventState);
        public long EventEndEpoch => Interlocked.Read(ref _eventEndEpoch);

        /// <summary>Bit i is plate i, broken. What every player can see.</summary>
        public byte BrokenPlateMask => (byte)Volatile.Read(ref _brokenPlateMask);

        /// <summary>The weak point once somebody has found it, or 255 while it is still a secret.</summary>
        public byte WeakPlate => (byte)Volatile.Read(ref _weakPlate);

        /// <summary>
        /// FOLKIDLE_BOSS_MINIGAME. In Wheel mode the weak plate is drawn per
        /// attempt and the armour regrows every UTC midnight (spec 3.3.1); in
        /// Off and Practice, opcode 32 keeps task 10's one weak plate per
        /// encounter, revealed by the first hit.
        /// </summary>
        public BossMinigameMode MinigameMode { get; }

        private bool WheelMode => MinigameMode == BossMinigameMode.Wheel;

        public WorldBossEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry, BossMinigameMode? minigameMode = null)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
            _redis = serviceProvider.GetService<IConnectionMultiplexer>();
            MinigameMode = minigameMode ?? serviceProvider.GetService<BossMinigameSettings>()?.Mode ?? BossMinigameMode.Off;
        }

        public static RedisKey ContributionKey(uint bossId) => $"boss:{bossId}:contributions";

        public async Task EnsureSnapshotAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var snapshot = await db.WorldBossSnapshots.FindAsync((long)ActiveBossInstanceId);
            if (snapshot == null)
            {
                snapshot = new WorldBossSnapshot
                {
                    BossInstanceId = ActiveBossInstanceId,
                    MaxHp = BaseHp,
                    CurrentHp = BaseHp,
                    TotalDamageContributed = 0,
                    LastActiveTimestamp = now
                };
                db.WorldBossSnapshots.Add(snapshot);
                await db.SaveChangesAsync();
            }

            RefreshLocalSnapshot(snapshot);
        }

        public bool IsValidAttackTarget(uint bossId)
        {
            return bossId == ActiveBossInstanceId;
        }

        public bool IsBossDead()
        {
            return Volatile.Read(ref _bossIsAlive) == 0 || BossCurrentHp <= 0;
        }

        /// <summary>
        /// Queues one strike. `serverComputedDamage` is exactly that - taken
        /// from the player's own cached attack power inside the tick, never
        /// from anything the client said about itself.
        ///
        /// Modul: A GUARDED DISPATCH, and it answers (task 25). This was a bare
        /// `_ = Task.Run(...)`, so anything ExecuteAttackAsync threw became an
        /// unobserved task exception - no row, no log, no message - and every
        /// refusal inside it was a silent rollback. Whatever happens now, a
        /// strike that did not land puts a reason on the player's result ring.
        /// </summary>
        public void QueueAttack(long playerId, uint bossId, uint serverComputedDamage, byte plateIndex)
        {
            _ = Task.Run(async () =>
            {
                WorldBossAttackOutcome outcome;
                try
                {
                    outcome = await ExecuteAttackAsync(playerId, bossId, serverComputedDamage, plateIndex);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"World boss attack dispatch failed for player {playerId}: {ex.Message}");
                    outcome = WorldBossAttackOutcome.Failed;
                }

                var code = ResultCodeFor(outcome);
                if (code.HasValue)
                {
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)code.Value);
                }
            });
        }

        /// <summary>The sentence a refused strike maps to; null for a strike that landed.</summary>
        public static FolkIdle.Server.Network.CommandResultCode? ResultCodeFor(WorldBossAttackOutcome outcome) => outcome switch
        {
            WorldBossAttackOutcome.Landed => null,
            WorldBossAttackOutcome.NotActive => FolkIdle.Server.Network.CommandResultCode.WorldBossNotActive,
            WorldBossAttackOutcome.AlreadyDefeated => FolkIdle.Server.Network.CommandResultCode.WorldBossAlreadyDefeated,
            WorldBossAttackOutcome.NoAttemptsLeft => FolkIdle.Server.Network.CommandResultCode.WorldBossNoAttemptsLeft,
            WorldBossAttackOutcome.SessionClosed => FolkIdle.Server.Network.CommandResultCode.WorldBossSessionClosed,
            _ => FolkIdle.Server.Network.CommandResultCode.WorldBossStrikeFailed,
        };

        public async Task ScaleActiveBossAsync(long[] onlinePlayerIds)
        {
            await _snapshotGate.WaitAsync();
            try { await ScaleActiveBossCoreAsync(onlinePlayerIds); }
            finally { _snapshotGate.Release(); }
        }

        private async Task ScaleActiveBossCoreAsync(long[] onlinePlayerIds)
        {
            await EnsureSnapshotAsync();

            int activeAccounts = onlinePlayerIds.Length;

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            long totalMasterySum = activeAccounts > 0
                ? await db.PlayerRaceMasteries
                    .Where(m => onlinePlayerIds.Contains(m.PlayerId))
                    .SumAsync(m => (long)m.MasteryLevel)
                : 0L;

            // GlobalMaxHp = BaseHp * (ActiveAccountsCount * 1.50) + (AccountMasteryScoresSum * 250.0)
            long newMaxHp = (long)(BaseHp * (activeAccounts * 1.50) + (totalMasterySum * 250.0));
            if (newMaxHp < BaseHp)
            {
                newMaxHp = BaseHp;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            try
            {
                var snapshot = await db.WorldBossSnapshots
                    .FromSqlRaw("SELECT * FROM \"WorldBossSnapshots\" WHERE \"BossInstanceId\" = {0} FOR UPDATE", (long)ActiveBossInstanceId)
                    .SingleOrDefaultAsync();

                if (snapshot == null)
                {
                    await transaction.RollbackAsync();
                    await EnsureSnapshotAsync();
                    return;
                }

                if (snapshot.CurrentHp <= 0)
                {
                    RefreshLocalSnapshot(snapshot);
                    await transaction.CommitAsync();
                    return;
                }

                // The board everyone sees regrows within a minute of midnight;
                // a strike regrows it exactly (see RegrowArmourIfNewDay).
                if (RegrowArmourIfNewDay(snapshot, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                {
                    await db.SaveChangesAsync();
                }

                if (snapshot.MaxHp != newMaxHp)
                {
                    long oldMax = snapshot.MaxHp <= 0 ? BaseHp : snapshot.MaxHp;
                    long capacityDelta = newMaxHp - oldMax;
                    long newCurrentHp = snapshot.CurrentHp + capacityDelta;
                    if (newCurrentHp > newMaxHp)
                    {
                        newCurrentHp = newMaxHp;
                    }
                    if (newCurrentHp <= 0)
                    {
                        newCurrentHp = 1;
                    }

                    snapshot.MaxHp = newMaxHp;
                    snapshot.CurrentHp = newCurrentHp;
                    snapshot.LastActiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    await db.SaveChangesAsync();
                }

                await transaction.CommitAsync();
                RefreshLocalSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"World boss scale update failed: {ex.Message}");
            }
        }

        public async Task ProcessDefeatedBossAsync()
        {
            await _snapshotGate.WaitAsync();
            try { await ProcessDefeatedBossCoreAsync(); }
            finally { _snapshotGate.Release(); }
        }

        private async Task ProcessDefeatedBossCoreAsync()
        {
            if (Interlocked.CompareExchange(ref _rewardDispatchActive, 1, 0) != 0)
            {
                return;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                var snapshot = await db.WorldBossSnapshots
                    .FromSqlRaw("SELECT * FROM \"WorldBossSnapshots\" WHERE \"BossInstanceId\" = {0} FOR UPDATE", (long)ActiveBossInstanceId)
                    .SingleOrDefaultAsync();

                if (snapshot == null || snapshot.CurrentHp > 0)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await PayEncounterRewardsAsync(db, now);

                snapshot.MaxHp = BaseHp;
                snapshot.CurrentHp = BaseHp;
                snapshot.TotalDamageContributed = 0;
                snapshot.LastActiveTimestamp = now;
                snapshot.EventState = 2; // Concluded: defeated. Dormant until the next scheduled window.

                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"player_world_boss_attempts\" WHERE \"BossInstanceId\" = {0}", (long)ActiveBossInstanceId);
                await transaction.CommitAsync();

                _playerDamageMap.Clear();
                if (_redis?.IsConnected == true)
                {
                    await _redis.GetDatabase().KeyDeleteAsync(ContributionKey(ActiveBossInstanceId));
                }

                long[] onlinePlayerIds = _playerRegistry.GetOnlinePlayerIds();
                for (int i = 0; i < onlinePlayerIds.Length; i++)
                {
                    _playerRegistry.WorldBossAttemptUpdateQueue.Enqueue(new WorldBossAttemptUpdateNotification
                    {
                        PlayerId = onlinePlayerIds[i],
                        AttemptCount = 0
                    });
                }

                RefreshLocalSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"World boss reward distribution failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _rewardDispatchActive, 0);
            }
        }

        // Modul: THE BOSS DOES NOT HAVE TO FALL (owner, 2026-09-26). Rewards
        // used to be mailed only from ProcessDefeatedBossAsync, so a week the
        // boss survived - every week, at today's population against 50M+ HP -
        // paid nobody anything for seven days of strikes. Now the encounter pays
        // when it ends either way, by each player's rank in damage dealt, from
        // the same rows the damage board shows (WorldBossBoard). Called inside
        // the caller's transaction, before the attempt rows are deleted.
        internal static async Task<int> PayEncounterRewardsAsync(FolkIdleDbContext db, long now)
        {
            var ranked = await WorldBossBoard.RankedAsync(db);
            int participantCount = ranked.Count;
            int paid = 0;
            for (int i = 0; i < participantCount; i++)
            {
                long participantId = ranked[i].PlayerId;

                var existingMail = await db.MailboxInstances
                    .FromSqlRaw("SELECT * FROM \"MailboxInstances\" WHERE \"PlayerId\" = {0} FOR UPDATE", participantId)
                    .ToListAsync();

                if (existingMail.Count >= 50)
                {
                    // Modul: world boss reward visibility, 2026-08-01.
                    //
                    // A full mailbox silently destroyed the player's
                    // ENTIRE world boss reward - tokens and gold - after
                    // they had fought for it, with no log, no telemetry
                    // and nothing the player could see. They simply
                    // never received anything.
                    //
                    // Still skipped rather than force-inserted, because
                    // the 50 cap is a real invariant and overflowing it
                    // here would be a design decision this fix should
                    // not smuggle in. But it is no longer invisible: see
                    // NEXT_STEPS_BACKLOG for the open question of
                    // whether earned, non-repeatable rewards should
                    // bypass the cap or be held for later delivery.
                    Console.WriteLine(
                        $"World boss reward SKIPPED for player {participantId}: mailbox full ({existingMail.Count} items). Reward lost.");

                    TelemetryStreamer.TryWrite(new TelemetryEvent
                    {
                        PlayerId = participantId,
                        EventType = 3,
                        Value1 = 19,
                        Value2 = existingMail.Count,
                        Timestamp = Environment.TickCount64
                    });
                    continue;
                }

                // Percentile bracket by rank among damage-dealing participants: Top 1% / Top 10% / Top 50% / Participation.
                var (_, tokenQuantity, goldAttachment) = WorldBossBoard.BracketFor(i + 1, participantCount);

                // Modul: the drop record (task 26) counts the reward
                // HERE, where it is created as mail - the claim that
                // later turns mail into a row is a move, not a creation.
                await DropRecord.RecordOneAsync(db, participantId, DropSource.WorldBoss, 0,
                    null, "perun_avatar_reward_token", 5, 5);
                db.MailboxInstances.Add(new MailboxInstance
                {
                    PlayerId = participantId,
                    BaseItemId = "perun_avatar_reward_token",
                    QualityTier = 5,
                    Quantity = tokenQuantity,
                    IsClaimed = false,
                    IsPending = false,
                    GoldAttachment = goldAttachment,
                    ReceivedTimestamp = now
                });
                paid++;
            }
            return paid;
        }

        // Modul: A WINDOW A DEVELOPER CAN OPEN ON ANY DAY (task 25).
        //
        // The calendar (1st-7th, 15th-22nd) was the only way in, so on 13-16
        // days a month nothing could press Strike - not exercise.mjs, not a
        // developer reproducing "no attack has ever landed". A SQL poke to
        // EventState = 1 does not help: LiveOps sees "active outside the
        // calendar" on its next 60-second tick and finalises it as failed. So
        // the override lives HERE, and LiveOps asks it before closing anything.
        //
        // Only reachable through POST /api/v1/dev/worldboss/window, which
        // answers 404 unless FOLKIDLE_DEV_TOOLS=1 - opening a window deletes
        // every attempt row server-wide, which on production would refund
        // everybody's strikes mid-encounter.
        private long _manualWindowEndEpoch;

        public const long MinManualWindowSeconds = 60;
        public const long MaxManualWindowSeconds = 86_400;

        public static long ClampManualWindowSeconds(long seconds)
            => Math.Clamp(seconds, MinManualWindowSeconds, MaxManualWindowSeconds);

        public bool IsManualWindowOpen(long nowEpoch)
        {
            long end = Interlocked.Read(ref _manualWindowEndEpoch);
            return end > 0 && nowEpoch < end;
        }

        public long ManualWindowEndEpoch => Interlocked.Read(ref _manualWindowEndEpoch);

        /// <summary>
        /// Opens a fresh encounter now (full health, new weak point, every
        /// attempt row deleted) that LiveOps will not close until it runs out.
        /// </summary>
        public async Task OpenManualWindowAsync(long durationSeconds, long? nowEpoch = null)
        {
            long now = nowEpoch ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long end = now + ClampManualWindowSeconds(durationSeconds);
            Interlocked.Exchange(ref _manualWindowEndEpoch, end);
            await EnsureSnapshotAsync();
            await ActivateEventWindowAsync(end);
        }

        /// <summary>
        /// Ends the override and concludes the encounter, as the calendar would
        /// have - but pays nothing: a developer's window is a test, and
        /// exercise.mjs opens and closes several a run, which would otherwise
        /// fill the dev fixture's 50-item mailbox with boss rewards.
        /// </summary>
        public async Task CloseManualWindowAsync()
        {
            Interlocked.Exchange(ref _manualWindowEndEpoch, 0);
            await FinalizeEventAsFailedAsync(payRewards: false);
        }

        public async Task ActivateEventWindowAsync(long eventEndEpoch)
        {
            await _snapshotGate.WaitAsync();
            try { await ActivateEventWindowCoreAsync(eventEndEpoch); }
            finally { _snapshotGate.Release(); }
        }

        private async Task ActivateEventWindowCoreAsync(long eventEndEpoch)
        {
            await EnsureSnapshotAsync();

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                var snapshot = await db.WorldBossSnapshots
                    .FromSqlRaw("SELECT * FROM \"WorldBossSnapshots\" WHERE \"BossInstanceId\" = {0} FOR UPDATE", (long)ActiveBossInstanceId)
                    .SingleOrDefaultAsync();

                if (snapshot == null)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                snapshot.MaxHp = BaseHp;
                snapshot.CurrentHp = BaseHp;
                snapshot.TotalDamageContributed = 0;
                snapshot.EventState = 1; // Active
                snapshot.EventEndEpoch = eventEndEpoch;
                snapshot.LastActiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Modul: a fresh set of armour, and a fresh secret.
                //
                // RE-SEEDED PER ENCOUNTER, which is the difference between a
                // decision and a wiki lookup. If the weak point were a property
                // of the boss rather than of the encounter, this mechanic would
                // have a shelf life of about a day.
                snapshot.BrokenPlateMask = 0;
                snapshot.WeakPlateRevealed = 0;
                snapshot.ArmourDayKey = WorldBossCalendar.DayKey(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                // Modul: A CRYPTOGRAPHIC DRAW (task 36 spec section 4). A
                // predictable seed is a precomputable secret. In wheel mode this
                // index is unused - every attempt draws its own.
                snapshot.WeakPlateIndex = (byte)System.Security.Cryptography.RandomNumberGenerator.GetInt32(PlateCount);

                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"player_world_boss_attempts\" WHERE \"BossInstanceId\" = {0}", (long)ActiveBossInstanceId);
                await transaction.CommitAsync();

                _playerDamageMap.Clear();
                if (_redis?.IsConnected == true)
                {
                    await _redis.GetDatabase().KeyDeleteAsync(ContributionKey(ActiveBossInstanceId));
                }

                long[] onlinePlayerIds = _playerRegistry.GetOnlinePlayerIds();
                for (int i = 0; i < onlinePlayerIds.Length; i++)
                {
                    _playerRegistry.WorldBossAttemptUpdateQueue.Enqueue(new WorldBossAttemptUpdateNotification
                    {
                        PlayerId = onlinePlayerIds[i],
                        AttemptCount = 0
                    });
                }

                RefreshLocalSnapshot(snapshot);
                Console.WriteLine($"World boss event window activated. Ends at epoch {eventEndEpoch}.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"World boss event activation failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Closes an encounter the boss survived, and pays it out by damage
        /// dealt (owner, 2026-09-26: the boss does not have to fall).
        /// </summary>
        public async Task FinalizeEventAsFailedAsync(bool payRewards = true)
        {
            await _snapshotGate.WaitAsync();
            try { await FinalizeEventAsFailedCoreAsync(payRewards); }
            finally { _snapshotGate.Release(); }
        }

        private async Task FinalizeEventAsFailedCoreAsync(bool payRewards)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                var snapshot = await db.WorldBossSnapshots
                    .FromSqlRaw("SELECT * FROM \"WorldBossSnapshots\" WHERE \"BossInstanceId\" = {0} FOR UPDATE", (long)ActiveBossInstanceId)
                    .SingleOrDefaultAsync();

                if (snapshot == null || snapshot.EventState != 1)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                // Paid in the same transaction that concludes the encounter, so
                // it pays exactly once: a second close finds EventState 2 above
                // and returns. The attempt rows are left for the board to show
                // until the next encounter opens and deletes them.
                int paid = payRewards
                    ? await PayEncounterRewardsAsync(db, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    : 0;

                snapshot.EventState = 2; // Concluded: failed, window expired without defeat.
                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                RefreshLocalSnapshot(snapshot);
                Console.WriteLine($"World boss event window closed without defeat. TotalDamageContributed={snapshot.TotalDamageContributed}, RemainingHp={snapshot.CurrentHp}, rewards mailed={paid}.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"World boss event finalization failed: {ex.Message}");
            }
        }

        internal async Task<WorldBossAttackOutcome> ExecuteAttackAsync(long playerId, uint bossId, uint serverComputedDamage, byte plateIndex = 0)
        {
            await _snapshotGate.WaitAsync();
            try { return await ExecuteAttackCoreAsync(playerId, bossId, serverComputedDamage, plateIndex); }
            finally { _snapshotGate.Release(); }
        }

        private async Task<WorldBossAttackOutcome> ExecuteAttackCoreAsync(long playerId, uint bossId, uint serverComputedDamage, byte plateIndex)
        {
            if (playerId <= 0 || bossId != ActiveBossInstanceId || serverComputedDamage == 0)
            {
                return WorldBossAttackOutcome.InvalidRequest;
            }

            if (plateIndex >= PlateCount)
            {
                return WorldBossAttackOutcome.InvalidRequest;
            }

            // Modul: the scope, the connection and BeginTransaction are INSIDE
            // the try now. CLAUDE.md: "a guard that starts AFTER
            // CreateScope/BeginTransactionAsync is not a guard - that is where
            // the connection is acquired and where the throw comes from." They
            // sat outside it, in a fire-and-forget task, so a pooler refusing
            // the connection lost the strike without a trace.
            IServiceScope? scope = null;
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
            try
            {
                scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                var snapshot = await db.WorldBossSnapshots
                    .FromSqlRaw("SELECT * FROM \"WorldBossSnapshots\" WHERE \"BossInstanceId\" = {0} FOR UPDATE", (long)bossId)
                    .SingleOrDefaultAsync();

                if (snapshot == null || snapshot.EventState != 1)
                {
                    await transaction.RollbackAsync();
                    return WorldBossAttackOutcome.NotActive;
                }

                if (snapshot.CurrentHp <= 0)
                {
                    await transaction.RollbackAsync();
                    return WorldBossAttackOutcome.AlreadyDefeated;
                }

                var attempt = await db.PlayerWorldBossAttempts
                    .FromSqlRaw("SELECT * FROM \"player_world_boss_attempts\" WHERE \"PlayerId\" = {0} AND \"BossInstanceId\" = {1} FOR UPDATE", playerId, (long)bossId)
                    .SingleOrDefaultAsync();

                long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long today = WorldBossCalendar.DayKey(nowEpoch);

                if (attempt == null)
                {
                    attempt = new PlayerWorldBossAttempt
                    {
                        PlayerId = playerId,
                        BossInstanceId = bossId,
                        AttemptCount = 0,
                        TotalInflictedDamage = 0,
                        AttemptDateKey = today
                    };
                    db.PlayerWorldBossAttempts.Add(attempt);
                }

                // Modul: ONE STRIKE A DAY (owner, 2026-09-25). The count is for
                // today only; a row last used on an earlier day starts again
                // at 0. Done HERE, under the row lock, so the rollover cannot
                // race a strike - the in-memory check on the tick is only a
                // cheap early answer.
                if (attempt.AttemptDateKey != today)
                {
                    attempt.AttemptDateKey = today;
                    attempt.AttemptCount = 0;
                }

                if (attempt.AttemptCount >= MaxAttemptsPerDay)
                {
                    await transaction.RollbackAsync();
                    return WorldBossAttackOutcome.NoAttemptsLeft;
                }

                // Modul: THE 300-SECOND BATTLE SESSION IS GONE (owner decision,
                // 2026-09-24, task 36 spec section 4). With one strike a day
                // there is nothing left for it to fence.
                // WorldBossAttackOutcome.SessionClosed and result code 41 stay
                // declared and unproduced - codes are never reused.

                // Modul: THE LARDER RULE IS GONE (owner decision 2026-09-24).
                // "Auto-eat food depleted closes the battle session" meant a
                // brand-new account - whose larder is empty until it cooks or
                // buys food - could not strike the world boss at all, and the
                // strike itself eats nothing. Dropped end to end.

                // Modul: which plate was struck decides what the blow is worth,
                // and what it teaches everyone else.
                //
                // Hitting the weak point pays triple and REVEALS it, from then
                // on, to every player - the finder included. Hitting anything
                // else pays in full and breaks that plate, permanently, which
                // is how the boss ends up telling the next arrival where not to
                // look.
                bool struckWeakPoint = plateIndex == snapshot.WeakPlateIndex;
                double plateMultiplier = struckWeakPoint ? WeakPlateDamageMultiplier : 1.0;

                if (struckWeakPoint)
                {
                    snapshot.WeakPlateRevealed = 1;
                }
                else
                {
                    snapshot.BrokenPlateMask |= (byte)(1 << plateIndex);
                }

                long scaledDamage = (long)Math.Min(uint.MaxValue, serverComputedDamage * plateMultiplier);
                long appliedDamage = ComputeAppliedDamage(snapshot.CurrentHp, (uint)scaledDamage);
                snapshot.CurrentHp -= appliedDamage;
                if (snapshot.CurrentHp < 0)
                {
                    snapshot.CurrentHp = 0;
                }
                snapshot.TotalDamageContributed += appliedDamage;
                snapshot.LastActiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                attempt.AttemptCount++;
                attempt.TotalInflictedDamage += appliedDamage;

                byte updatedAttemptCount = (byte)attempt.AttemptCount;

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                // Modul: PAST THE COMMIT THE STRIKE HAS LANDED, whatever
                // happens next. These steps used to share the catch below, so
                // a Redis timeout here answered "nothing was spent - try again"
                // for an attempt already in the database, and skipped the
                // notification that spends the pip. The notification goes
                // FIRST and nothing after the commit may turn Landed into Failed.
                AfterStrikeCommitted(playerId, appliedDamage, updatedAttemptCount, snapshot);
                await MirrorContributionAsync(playerId, appliedDamage);
                return WorldBossAttackOutcome.Landed;
            }
            catch (Exception ex)
            {
                if (transaction != null)
                {
                    try { await transaction.RollbackAsync(); } catch { /* the connection may already be gone */ }
                }
                Console.WriteLine($"World boss attack failed for player {playerId}: {ex.Message}");
                return WorldBossAttackOutcome.Failed;
            }
            finally
            {
                if (transaction != null) await transaction.DisposeAsync();
                scope?.Dispose();
            }
        }

        private void RefreshLocalSnapshot(WorldBossSnapshot snapshot)
        {
            Interlocked.Exchange(ref _bossMaxHp, snapshot.MaxHp);
            Interlocked.Exchange(ref _bossCurrentHp, snapshot.CurrentHp);
            Volatile.Write(ref _bossIsAlive, snapshot.CurrentHp > 0 ? 1 : 0);
            Volatile.Write(ref _eventState, snapshot.EventState);
            Interlocked.Exchange(ref _eventEndEpoch, snapshot.EventEndEpoch);
            Volatile.Write(ref _brokenPlateMask, snapshot.BrokenPlateMask);

            // The secret stays a secret. Only a revealed weak point reaches the
            // mirror the broadcast reads, so nothing downstream can leak it by
            // accident.
            //
            // In wheel mode there is no encounter-wide weak plate to reveal at
            // all: each attempt draws its own, so the mirror is always hidden -
            // including a WeakPlateRevealed left over from before the flip.
            Volatile.Write(ref _weakPlate,
                !WheelMode && snapshot.WeakPlateRevealed == 1 ? snapshot.WeakPlateIndex : WeakPlateHidden);
        }

        // Modul: THE ARMOUR REGROWS EVERY UTC MIDNIGHT, IN WHEEL MODE ONLY
        // (owner, 2026-09-26, spec 3.3.1). Each attempt's weak plate is drawn
        // from the unbroken plates, so without regrowth the crowd would strip
        // four plates by the first morning and every later strike of the week
        // would know the answer. Keyed on a PERSISTED day rather than on
        // LiveOps' midnight edge, which never fires for a midnight the server
        // was down across (its first tick after a start only records "today").
        // Called by every writer that already holds the row lock.
        internal bool RegrowArmourIfNewDay(WorldBossSnapshot snapshot, long nowEpoch)
        {
            if (!WheelMode) return false;
            long today = WorldBossCalendar.DayKey(nowEpoch);
            if (snapshot.ArmourDayKey == today) return false;
            snapshot.ArmourDayKey = today;
            snapshot.BrokenPlateMask = 0;
            return true;
        }

        // --- the shield wheel's strike (task 36 Phase 2) ------------------------

        public async Task<int> StrikesUsedTodayAsync(long playerId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            long today = WorldBossCalendar.DayKey(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var row = await db.PlayerWorldBossAttempts.AsNoTracking()
                .Where(a => a.PlayerId == playerId && a.BossInstanceId == ActiveBossInstanceId)
                .Select(a => new { a.AttemptCount, a.AttemptDateKey })
                .SingleOrDefaultAsync();
            return row == null || row.AttemptDateKey != today ? 0 : row.AttemptCount;
        }

        public void Submit(WorldBossStrikeOrder order) => _playerRegistry.WorldBossStrikeQueue.Enqueue(order);

        public bool HasGameSession(long playerId) => _playerRegistry.IsPlayerOnline(playerId);

        /// <summary>
        /// Applies a priced order off the tick thread and ALWAYS completes it:
        /// the REST handler is waiting on the answer, and a strike with no
        /// answer is the silent rollback CLAUDE.md warns about.
        /// </summary>
        public void QueueStrike(WorldBossStrikeOrder order, long attackDamage)
        {
            _ = Task.Run(async () =>
            {
                WorldBossStrikeOutcome outcome;
                try
                {
                    outcome = await ExecuteStrikeAsync(order, attackDamage);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"World boss strike dispatch failed for player {order.PlayerId}: {ex.Message}");
                    outcome = WorldBossStrikeOutcome.Refusal(WorldBossStrikeResult.Failed);
                }
                order.Complete(outcome);
            });
        }

        internal async Task<WorldBossStrikeOutcome> ExecuteStrikeAsync(WorldBossStrikeOrder order, long attackDamage)
        {
            await _snapshotGate.WaitAsync();
            try { return await ExecuteStrikeCoreAsync(order, attackDamage); }
            finally { _snapshotGate.Release(); }
        }

        private async Task<WorldBossStrikeOutcome> ExecuteStrikeCoreAsync(WorldBossStrikeOrder order, long attackDamage)
        {
            long playerId = order.PlayerId;
            // attackDamage 0 is legal: a striker with no live payload, priced at
            // the MinStrikeDamage floor (see WorldBossTickCoordinator.DrainStrikeOrders).
            if (playerId <= 0 || attackDamage < 0 || !WheelMode)
            {
                return WorldBossStrikeOutcome.Refusal(WorldBossStrikeResult.Failed);
            }
            if (order.WeakPlate is int held && (held < 0 || held >= PlateCount))
            {
                return WorldBossStrikeOutcome.Refusal(WorldBossStrikeResult.Failed);
            }

            // The same guarded shape as ExecuteAttackCoreAsync: the scope, the
            // connection and the transaction are all inside the try.
            IServiceScope? scope = null;
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
            try
            {
                scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                var snapshot = await db.WorldBossSnapshots
                    .FromSqlRaw("SELECT * FROM \"WorldBossSnapshots\" WHERE \"BossInstanceId\" = {0} FOR UPDATE", (long)ActiveBossInstanceId)
                    .SingleOrDefaultAsync();

                // A challenge from last week's encounter answers NotActive and
                // spends nothing (spec 5.6): the boss it was aimed at is gone.
                if (snapshot == null || snapshot.EventState != 1
                    || (order.EncounterEndEpoch != 0 && order.EncounterEndEpoch != snapshot.EventEndEpoch))
                {
                    await transaction.RollbackAsync();
                    return WorldBossStrikeOutcome.Refusal(WorldBossStrikeResult.NotActive);
                }

                if (snapshot.CurrentHp <= 0)
                {
                    await transaction.RollbackAsync();
                    return WorldBossStrikeOutcome.Refusal(WorldBossStrikeResult.AlreadyDefeated);
                }

                var attempt = await db.PlayerWorldBossAttempts
                    .FromSqlRaw("SELECT * FROM \"player_world_boss_attempts\" WHERE \"PlayerId\" = {0} AND \"BossInstanceId\" = {1} FOR UPDATE", playerId, (long)ActiveBossInstanceId)
                    .SingleOrDefaultAsync();

                long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long today = WorldBossCalendar.DayKey(nowEpoch);
                if (attempt == null)
                {
                    attempt = new PlayerWorldBossAttempt
                    {
                        PlayerId = playerId,
                        BossInstanceId = ActiveBossInstanceId,
                        AttemptCount = 0,
                        TotalInflictedDamage = 0,
                        AttemptDateKey = today
                    };
                    db.PlayerWorldBossAttempts.Add(attempt);
                }
                if (attempt.AttemptDateKey != today)
                {
                    attempt.AttemptDateKey = today;
                    attempt.AttemptCount = 0;
                }
                if (attempt.AttemptCount >= MaxAttemptsPerDay)
                {
                    await transaction.RollbackAsync();
                    return WorldBossStrikeOutcome.Refusal(WorldBossStrikeResult.NoAttemptsLeft);
                }

                RegrowArmourIfNewDay(snapshot, nowEpoch);

                // The weak plate: the challenge's, drawn at issue - or, for an
                // auto-strike, drawn now from the plates this locked row leaves
                // standing. Either way it lives for this one attempt.
                int weak = order.WeakPlate ?? WeakPlateDraw.From(snapshot.BrokenPlateMask);
                var price = WorldBossStrikeRules.Price(order.Landings, weak, order.Multiplier, snapshot.BrokenPlateMask);
                if (price.BreakPlate is int broken)
                {
                    snapshot.BrokenPlateMask |= (byte)(1 << broken);
                }

                // Modul: WeakPlateRevealed is NOT set in wheel mode. The secret
                // lives for one attempt; "four broken, so the last is weak" is
                // what the board already shows (spec 3.3.1, rule 3).
                // Modul: THE FLOOR IS ON THE BASE HIT, NOT ON THE RESULT (found by
                // exercise.mjs, 2026-09-26). ComputeAppliedDamage floors at 1,000
                // AFTER any multiplier, and a typical character's A x G is 100-200
                // hit points, so a blind run and a capped one with three weak
                // seams (6x) both dealt exactly 1,000: the whole skill lever and
                // the weak plate were invisible in damage. Flooring the base hit
                // first keeps "an account that has never fought still
                // contributes" and lets every multiplier count on top of it.
                double raw = Math.Max(MinStrikeDamage, attackDamage) * price.Played;
                long appliedDamage = ComputeAppliedDamage(snapshot.CurrentHp, (uint)Math.Min(uint.MaxValue, raw));
                snapshot.CurrentHp -= appliedDamage;
                if (snapshot.CurrentHp < 0) snapshot.CurrentHp = 0;
                snapshot.TotalDamageContributed += appliedDamage;
                snapshot.LastActiveTimestamp = nowEpoch;

                attempt.AttemptCount++;
                attempt.TotalInflictedDamage += appliedDamage;
                byte updatedAttemptCount = (byte)attempt.AttemptCount;

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                AfterStrikeCommitted(playerId, appliedDamage, updatedAttemptCount, snapshot);
                await MirrorContributionAsync(playerId, appliedDamage);

                return new WorldBossStrikeOutcome(
                    order.LandedAs,
                    appliedDamage,
                    price.Multiplier,
                    price.PlateMultiplier,
                    price.Played,
                    price.BreakPlate ?? -1,
                    order.Landings.Select(l => new LandingDto
                    {
                        Seq = l.Seq, Plate = l.Plate, Class = l.Class, IsCounter = l.IsCounter,
                        WeakHit = l.Class >= SpearClass.Plate && l.Plate == weak,
                    }).ToList());
            }
            catch (Exception ex)
            {
                if (transaction != null)
                {
                    try { await transaction.RollbackAsync(); } catch { /* the connection may already be gone */ }
                }
                Console.WriteLine($"World boss strike failed for player {playerId}: {ex.Message}");
                return WorldBossStrikeOutcome.Refusal(WorldBossStrikeResult.Failed);
            }
            finally
            {
                if (transaction != null) await transaction.DisposeAsync();
                scope?.Dispose();
            }
        }

        /// <summary>
        /// Past the commit the strike has landed, whatever happens next: the
        /// notification that spends the pip goes first, and nothing here may
        /// turn Landed into Failed. Shared by opcode 32 and the wheel.
        /// </summary>
        private void AfterStrikeCommitted(long playerId, long appliedDamage, byte updatedAttemptCount, WorldBossSnapshot snapshot)
        {
            _playerRegistry.WorldBossAttemptUpdateQueue.Enqueue(new WorldBossAttemptUpdateNotification
            {
                PlayerId = playerId,
                AttemptCount = updatedAttemptCount
            });
            _playerDamageMap.AddOrUpdate(playerId, appliedDamage, (_, existing) => existing + appliedDamage);
            RefreshLocalSnapshot(snapshot);
        }

        private async Task MirrorContributionAsync(long playerId, long appliedDamage)
        {
            try
            {
                if (_redis?.IsConnected == true)
                {
                    await _redis.GetDatabase().HashIncrementAsync(ContributionKey(ActiveBossInstanceId), playerId, appliedDamage);
                }
            }
            catch (Exception redisEx)
            {
                Console.WriteLine($"World boss contribution mirror failed for player {playerId} (strike landed): {redisEx.Message}");
            }
        }

        private static long ComputeAppliedDamage(long currentHp, uint clientPredictedDamage)
        {
            Span<long> damageScratch = stackalloc long[4];
            damageScratch[0] = clientPredictedDamage;
            damageScratch[1] = damageScratch[0] > MaxClientPredictedDamage ? MaxClientPredictedDamage : damageScratch[0];
            damageScratch[2] = damageScratch[1] < 1000L ? 1000L : damageScratch[1];
            damageScratch[3] = damageScratch[2] > currentHp ? currentHp : damageScratch[2];
            return damageScratch[3];
        }
    }
}
