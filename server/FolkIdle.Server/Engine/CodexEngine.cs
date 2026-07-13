using System;
using System.Collections.Concurrent;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    public struct KillEvent
    {
        public long PlayerId;
        public int MonsterId;
        public int RaceId;
        public long GainedXp;
    }

    public class CodexEngine
    {
        public static readonly ConcurrentQueue<KillEvent> KillEventQueue = new();
        
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;
        private CancellationTokenSource _cts = new();

        public CodexEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ExecuteAsync(_cts.Token));
        }

        // Level = KillCount / 10: linear per-monster codex mastery curve, uncapped.
        private static int CalculateLevelFromKillCount(int killCount)
        {
            return killCount / 10;
        }

        internal static async Task<(float YieldMultiplier, float DamageMultiplier)> CalculateActiveMultipliersAsync(long playerId, FolkIdleDbContext db)
        {
            int levelSum = await db.MonsterCodexEntries
                .Where(c => c.PlayerId == playerId)
                .SumAsync(c => c.Level);

            float yieldMultiplier = 1.0f + (levelSum * 0.005f);
            float damageMultiplier = 1.0f + (levelSum * 0.010f);
            return (yieldMultiplier, damageMultiplier);
        }

        public static async Task RecalculateAndSyncMultipliersAsync(long playerId, FolkIdleDbContext db, PlayerSessionRegistry registry)
        {
            (float yieldMultiplier, float damageMultiplier) = await CalculateActiveMultipliersAsync(playerId, db);

            registry.CodexMultiplierUpdateQueue.Enqueue(new CodexMultiplierUpdateNotification
            {
                PlayerId = playerId,
                YieldMultiplier = yieldMultiplier,
                DamageMultiplier = damageMultiplier
            });
        }

        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(5000, stoppingToken);

                var killsToProcess = new System.Collections.Generic.List<KillEvent>();
                while (KillEventQueue.TryDequeue(out var killEvent))
                {
                    killsToProcess.Add(killEvent);
                }

                if (killsToProcess.Count == 0) continue;

                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, stoppingToken);
                try
                {
                    var playerIds = killsToProcess.Select(k => k.PlayerId).Distinct().ToList();

                    var codexEntries = await dbContext.MonsterCodexEntries
                        .Where(c => playerIds.Contains(c.PlayerId))
                        .ToDictionaryAsync(c => new { c.PlayerId, c.MonsterId }, stoppingToken);

                    var masteries = await dbContext.PlayerRaceMasteries
                        .Where(m => playerIds.Contains(m.PlayerId))
                        .ToDictionaryAsync(m => new { m.PlayerId, m.RaceId }, stoppingToken);

                    var codexLeveledUpPlayerIds = new System.Collections.Generic.HashSet<long>();

                    foreach (var group in killsToProcess.GroupBy(k => new { k.PlayerId, k.MonsterId }))
                    {
                        var key = new { group.Key.PlayerId, group.Key.MonsterId };
                        int kills = group.Count();

                        if (codexEntries.TryGetValue(key, out var entry))
                        {
                            entry.KillCount += kills;
                        }
                        else
                        {
                            entry = new MonsterCodexEntry
                            {
                                PlayerId = key.PlayerId,
                                MonsterId = key.MonsterId,
                                KillCount = kills,
                                FirstDrawnRarity = 1
                            };
                            dbContext.MonsterCodexEntries.Add(entry);
                            codexEntries[key] = entry;
                        }

                        int newLevel = CalculateLevelFromKillCount(entry.KillCount);
                        if (newLevel > entry.Level)
                        {
                            entry.Level = newLevel;
                            codexLeveledUpPlayerIds.Add(key.PlayerId);
                        }
                    }

                    foreach (var group in killsToProcess.GroupBy(k => new { k.PlayerId, k.RaceId }))
                    {
                        if (group.Key.RaceId <= 0) continue;

                        var key = new { group.Key.PlayerId, group.Key.RaceId };
                        long totalXp = group.Sum(k => k.GainedXp);
                        
                        if (masteries.TryGetValue(key, out var mastery))
                        {
                            mastery.CumulativeXp += totalXp;
                        }
                        else
                        {
                            mastery = new PlayerRaceMastery
                            {
                                PlayerId = key.PlayerId,
                                RaceId = key.RaceId,
                                MasteryLevel = 1,
                                CumulativeXp = totalXp
                            };
                            dbContext.PlayerRaceMasteries.Add(mastery);
                            masteries[key] = mastery;
                        }

                        long requiredXp = (long)(500 * mastery.MasteryLevel * Math.Pow(1.32, mastery.MasteryLevel));
                        bool leveledUp = false;
                        while (mastery.CumulativeXp >= requiredXp)
                        {
                            mastery.CumulativeXp -= requiredXp;
                            mastery.MasteryLevel++;
                            leveledUp = true;
                            requiredXp = (long)(500 * mastery.MasteryLevel * Math.Pow(1.32, mastery.MasteryLevel));
                        }

                        if (leveledUp)
                        {
                            _playerRegistry.MasteryUpdateQueue.Enqueue(new MasteryUpdateNotification
                            {
                                PlayerId = group.Key.PlayerId,
                                RaceId = group.Key.RaceId,
                                MasteryLevel = mastery.MasteryLevel
                            });
                        }
                    }

                    await dbContext.SaveChangesAsync(stoppingToken);
                    await transaction.CommitAsync(stoppingToken);

                    foreach (long leveledUpPlayerId in codexLeveledUpPlayerIds)
                    {
                        await RecalculateAndSyncMultipliersAsync(leveledUpPlayerId, dbContext, _playerRegistry);
                    }
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(stoppingToken);
                    Console.WriteLine($"Failed to process Codex entries: {ex.Message}");
                }
            }
        }
    }
}
