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

        private readonly ConcurrentDictionary<long, long> _playerDamageMap = new();

        public long BossMaxHp => Interlocked.Read(ref _bossMaxHp);
        public long BossCurrentHp => Interlocked.Read(ref _bossCurrentHp);
        public bool IsAlive => Volatile.Read(ref _bossIsAlive) == 1 && BossCurrentHp > 0;
        public bool IsEventActive => Volatile.Read(ref _eventState) == 1;
        public byte EventState => (byte)Volatile.Read(ref _eventState);
        public long EventEndEpoch => Interlocked.Read(ref _eventEndEpoch);

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

        public void RegisterDamage(long playerId, long damage, bool autoEatFoodDepleted = false)
        {
            if (damage <= 0)
            {
                return;
            }

            uint predictedDamage = damage > MaxClientPredictedDamage ? MaxClientPredictedDamage : (uint)damage;
            QueueAttack(playerId, ActiveBossInstanceId, predictedDamage, autoEatFoodDepleted);
        }

        public void QueueAttack(long playerId, uint bossId, uint clientPredictedDamage, bool autoEatFoodDepleted = false)
        {
            _ = Task.Run(async () => await ExecuteAttackAsync(playerId, bossId, clientPredictedDamage, autoEatFoodDepleted));
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
        private const long BattleSessionCapSeconds = 300L;

        internal async Task ExecuteAttackAsync(long playerId, uint bossId, uint clientPredictedDamage, bool autoEatFoodDepleted = false)
        {
            if (playerId <= 0 || bossId != ActiveBossInstanceId || clientPredictedDamage == 0)
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

                long appliedDamage = ComputeAppliedDamage(snapshot.CurrentHp, clientPredictedDamage);
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

                _playerDamageMap.AddOrUpdate(playerId, appliedDamage, (_, existing) => existing + appliedDamage);
                if (_redis?.IsConnected == true)
                {
                    await _redis.GetDatabase().HashIncrementAsync(ContributionKey(bossId), playerId, appliedDamage);
                }
                _playerRegistry.WorldBossAttemptUpdateQueue.Enqueue(new WorldBossAttemptUpdateNotification
                {
                    PlayerId = playerId,
                    AttemptCount = updatedAttemptCount
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
