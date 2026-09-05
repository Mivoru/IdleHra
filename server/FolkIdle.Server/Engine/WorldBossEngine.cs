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

namespace FolkIdle.Server.Engine
{
    public class WorldBossDamagePayload
    {
        public long PlayerId { get; set; }
        public long Damage { get; set; }
    }

    public class WorldBossEngine
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
        private const int MaxAttemptsPerEncounter = 3;

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

        public WorldBossEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
            _redis = serviceProvider.GetService<IConnectionMultiplexer>();
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
        /// </summary>
        public void QueueAttack(long playerId, uint bossId, uint serverComputedDamage, byte plateIndex, bool autoEatFoodDepleted = false)
        {
            _ = Task.Run(async () => await ExecuteAttackAsync(playerId, bossId, serverComputedDamage, plateIndex, autoEatFoodDepleted));
        }

        public async Task ScaleActiveBossAsync(long[] onlinePlayerIds)
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

                var contributions = await LoadDistributedContributionsAsync();

                var rankedParticipants = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<long, long>>();
                foreach (var entry in contributions)
                {
                    if (entry.Key > 0 && entry.Value > 0)
                    {
                        rankedParticipants.Add(entry);
                    }
                }
                rankedParticipants.Sort((a, b) => b.Value.CompareTo(a.Value));

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int participantCount = rankedParticipants.Count;
                if (participantCount > 0)
                {
                    for (int i = 0; i < participantCount; i++)
                    {
                        long participantId = rankedParticipants[i].Key;

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
                        double percentileRank = (double)(i + 1) / participantCount;
                        int tokenQuantity;
                        long goldAttachment;
                        if (percentileRank <= 0.01)
                        {
                            tokenQuantity = 10;
                            goldAttachment = 250000L;
                        }
                        else if (percentileRank <= 0.10)
                        {
                            tokenQuantity = 6;
                            goldAttachment = 100000L;
                        }
                        else if (percentileRank <= 0.50)
                        {
                            tokenQuantity = 3;
                            goldAttachment = 50000L;
                        }
                        else
                        {
                            tokenQuantity = 1;
                            goldAttachment = 10000L;
                        }

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
                    }
                }

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
                        AttemptCount = 0,
                        SessionEndsEpoch = 0
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

        public async Task ActivateEventWindowAsync(long eventEndEpoch)
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
                snapshot.WeakPlateIndex = (byte)Random.Shared.Next(PlateCount);

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
                        AttemptCount = 0,
                        SessionEndsEpoch = 0
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

        public async Task FinalizeEventAsFailedAsync()
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

                snapshot.EventState = 2; // Concluded: failed, window expired without defeat.
                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                RefreshLocalSnapshot(snapshot);
                Console.WriteLine($"World boss event window closed without defeat. TotalDamageContributed={snapshot.TotalDamageContributed}, RemainingHp={snapshot.CurrentHp}.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"World boss event finalization failed: {ex.Message}");
            }
        }

        // Modul 06/15: session cutoff duration, matching the brief's absolute
        // 300-second per-player battle entry cap.
        // Modul: how long a player has, from their FIRST strike, to spend the
        // rest of their attempts. Public because the client has to be able to
        // say it - see WorldBossAttemptUpdateNotification.SessionEndsEpoch for
        // what it cost to leave that unsaid.
        public const long BattleSessionCapSeconds = 300L;

        internal async Task ExecuteAttackAsync(long playerId, uint bossId, uint serverComputedDamage, byte plateIndex = 0, bool autoEatFoodDepleted = false)
        {
            if (playerId <= 0 || bossId != ActiveBossInstanceId || serverComputedDamage == 0)
            {
                return;
            }

            if (plateIndex >= PlateCount)
            {
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                var snapshot = await db.WorldBossSnapshots
                    .FromSqlRaw("SELECT * FROM \"WorldBossSnapshots\" WHERE \"BossInstanceId\" = {0} FOR UPDATE", (long)bossId)
                    .SingleOrDefaultAsync();

                if (snapshot == null || snapshot.CurrentHp <= 0 || snapshot.EventState != 1)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                var attempt = await db.PlayerWorldBossAttempts
                    .FromSqlRaw("SELECT * FROM \"player_world_boss_attempts\" WHERE \"PlayerId\" = {0} AND \"BossInstanceId\" = {1} FOR UPDATE", playerId, (long)bossId)
                    .SingleOrDefaultAsync();

                long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                if (attempt == null)
                {
                    attempt = new PlayerWorldBossAttempt
                    {
                        PlayerId = playerId,
                        BossInstanceId = bossId,
                        AttemptCount = 0,
                        TotalInflictedDamage = 0,
                        SessionStartEpoch = nowEpoch
                    };
                    db.PlayerWorldBossAttempts.Add(attempt);
                }

                if (attempt.AttemptCount >= MaxAttemptsPerEncounter)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                // Modul 06/15: close this player's battle session instantly -
                // no new damage is applied, but the damage delta already
                // registered (attempt.TotalInflictedDamage / snapshot.CurrentHp)
                // stands untouched.
                if (attempt.SessionStartEpoch > 0 && nowEpoch - attempt.SessionStartEpoch >= BattleSessionCapSeconds)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                if (autoEatFoodDepleted)
                {
                    await transaction.RollbackAsync();
                    return;
                }

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
                long attemptSessionStart = attempt.SessionStartEpoch;

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                _playerDamageMap.AddOrUpdate(playerId, appliedDamage, (_, existing) => existing + appliedDamage);
                if (_redis?.IsConnected == true)
                {
                    await _redis.GetDatabase().HashIncrementAsync(ContributionKey(bossId), playerId, appliedDamage);
                }
                _playerRegistry.WorldBossAttemptUpdateQueue.Enqueue(new WorldBossAttemptUpdateNotification
                {
                    PlayerId = playerId,
                    AttemptCount = updatedAttemptCount,
                    SessionEndsEpoch = attemptSessionStart + BattleSessionCapSeconds
                });
                RefreshLocalSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"World boss attack failed for player {playerId}: {ex.Message}");
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
            Volatile.Write(ref _weakPlate,
                snapshot.WeakPlateRevealed == 1 ? snapshot.WeakPlateIndex : WeakPlateHidden);
        }

        private async Task<System.Collections.Generic.Dictionary<long, long>> LoadDistributedContributionsAsync()
        {
            var result = new System.Collections.Generic.Dictionary<long, long>();
            bool loadedRedisContributions = false;

            if (_redis?.IsConnected == true)
            {
                HashEntry[] entries = await _redis.GetDatabase().HashGetAllAsync(ContributionKey(ActiveBossInstanceId));
                loadedRedisContributions = entries.Length > 0;
                for (int i = 0; i < entries.Length; i++)
                {
                    long damage = (long)entries[i].Value;
                    if (long.TryParse(entries[i].Name.ToString(), out long playerId) && damage > 0)
                    {
                        result[playerId] = damage;
                    }
                }
            }

            if (!loadedRedisContributions)
            {
                foreach (var entry in _playerDamageMap)
                {
                    if (entry.Value > 0)
                    {
                        result[entry.Key] = result.TryGetValue(entry.Key, out long existing) ? existing + entry.Value : entry.Value;
                    }
                }
            }

            return result;
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
