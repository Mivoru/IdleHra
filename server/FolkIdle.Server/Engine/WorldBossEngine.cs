using System;
using System.Collections.Concurrent;
using System.Data;
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

        private readonly IServiceProvider _serviceProvider;
        private readonly IConnectionMultiplexer? _redis;
        private long _bossMaxHp = BaseHp;
        private long _bossCurrentHp = BaseHp;
        private int _bossIsAlive = 1;
        private int _rewardDispatchActive;

        private readonly ConcurrentDictionary<long, long> _playerDamageMap = new();

        public long BossMaxHp => Interlocked.Read(ref _bossMaxHp);
        public long BossCurrentHp => Interlocked.Read(ref _bossCurrentHp);
        public bool IsAlive => Volatile.Read(ref _bossIsAlive) == 1 && BossCurrentHp > 0;

        public WorldBossEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
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

        public void RegisterDamage(long playerId, long damage)
        {
            if (damage <= 0)
            {
                return;
            }

            uint predictedDamage = damage > MaxClientPredictedDamage ? MaxClientPredictedDamage : (uint)damage;
            QueueAttack(playerId, ActiveBossInstanceId, predictedDamage);
        }

        public void QueueAttack(long playerId, uint bossId, uint clientPredictedDamage)
        {
            _ = Task.Run(async () => await ExecuteAttackAsync(playerId, bossId, clientPredictedDamage));
        }

        public async Task ScaleActiveBossAsync(int activeCcu)
        {
            await EnsureSnapshotAsync();

            long newMaxHp = (long)Math.Floor(BaseHp * Math.Max(1.0, activeCcu * 0.75));
            if (newMaxHp < BaseHp)
            {
                newMaxHp = BaseHp;
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
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
                long totalDamage = snapshot.TotalDamageContributed;
                if (totalDamage <= 0)
                {
                    foreach (var entry in contributions)
                    {
                        if (entry.Value > 0)
                        {
                            totalDamage += entry.Value;
                        }
                    }
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (totalDamage > 0)
                {
                    foreach (var participant in contributions)
                    {
                        if (participant.Key <= 0 || participant.Value <= 0)
                        {
                            continue;
                        }

                        var existingMail = await db.MailboxInstances
                            .FromSqlRaw("SELECT * FROM \"MailboxInstances\" WHERE \"PlayerId\" = {0} FOR UPDATE", participant.Key)
                            .ToListAsync();

                        if (existingMail.Count >= 50)
                        {
                            continue;
                        }

                        double contributionRatio = Math.Clamp((double)participant.Value / totalDamage, 0.0, 1.0);
                        int tokenQuantity = Math.Max(1, (int)Math.Ceiling(contributionRatio * 10.0));
                        long goldAttachment = Math.Max(10000L, (long)(250000L * contributionRatio));

                        db.MailboxInstances.Add(new MailboxInstance
                        {
                            PlayerId = participant.Key,
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

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                _playerDamageMap.Clear();
                if (_redis?.IsConnected == true)
                {
                    await _redis.GetDatabase().KeyDeleteAsync(ContributionKey(ActiveBossInstanceId));
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

        private async Task ExecuteAttackAsync(long playerId, uint bossId, uint clientPredictedDamage)
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

                if (snapshot == null || snapshot.CurrentHp <= 0)
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

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                _playerDamageMap.AddOrUpdate(playerId, appliedDamage, (_, existing) => existing + appliedDamage);
                if (_redis?.IsConnected == true)
                {
                    await _redis.GetDatabase().HashIncrementAsync(ContributionKey(bossId), playerId, appliedDamage);
                }
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
