using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    public class AchievementEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _registry;
        private CancellationTokenSource _cts = new();

        public AchievementEngine(IServiceProvider serviceProvider, PlayerSessionRegistry registry)
        {
            _serviceProvider = serviceProvider;
            _registry = registry;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ExecuteAsync(_cts.Token));
            Task.Run(() => ProcessClaimsQueueAsync(_cts.Token));
        }

        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(15000, stoppingToken);

                var retryingOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
                await using var dbContext = new FolkIdleDbContext(retryingOptions.Options);

                try
                {
                    var activePlayers = await dbContext.PlayerRecords
                        .Where(p => p.LastLogoutTimestamp == 0 || (Environment.TickCount64 - p.LastLogoutTimestamp) < 60000)
                        .ToListAsync(stoppingToken);

                    foreach (var player in activePlayers)
                    {
                        // Modul: each player's transaction is its own retry
                        // unit - a Serializable conflict on player N retries
                        // only player N's attempt, not the whole batch.
                        var strategy = dbContext.Database.CreateExecutionStrategy();
                        await strategy.ExecuteAsync(async () =>
                        {
                            // player was loaded outside this retry boundary
                            // and is mutated below - re-attach after
                            // clearing so PremiumDiamonds changes are not
                            // silently dropped from SaveChangesAsync.
                            dbContext.ChangeTracker.Clear();
                            dbContext.Attach(player);

                            using var transaction = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, stoppingToken);

                            var achievementRecord = await dbContext.PlayerAchievements.FindAsync(new object[] { player.Id }, stoppingToken);
                            if (achievementRecord == null)
                            {
                                achievementRecord = new PlayerAchievement { PlayerId = player.Id, ClaimedAchievementFlags = 0 };
                                dbContext.PlayerAchievements.Add(achievementRecord);
                            }

                            int currentFlags = achievementRecord.ClaimedAchievementFlags;
                            int newFlags = currentFlags;
                            int diamondsToAward = 0;

                            // Treasury: CurrentGold >= 100000
                            var goldRecord = await dbContext.CommodityRecords.FindAsync(new object[] { player.Id, 0 }, stoppingToken);
                            long currentGold = goldRecord?.Quantity ?? 0;
                            if ((currentFlags & (1 << 0)) == 0 && currentGold >= 100000)
                            {
                                newFlags |= (1 << 0);
                                diamondsToAward += 100;
                            }

                            // Engineering & Demographic
                            var infrastructureRows = await dbContext.VillageInfrastructures
                                .AsNoTracking()
                                .Where(v => v.PlayerId == player.Id)
                                .ToListAsync(stoppingToken);

                            if (infrastructureRows.Count > 0)
                            {
                                int engineeringScore = infrastructureRows.Sum(v => v.CurrentLevel);
                                if ((currentFlags & (1 << 1)) == 0 && engineeringScore >= 10)
                                {
                                    newFlags |= (1 << 1);
                                    diamondsToAward += 100;
                                }

                                int population = await dbContext.VillageResidents
                                    .AsNoTracking()
                                    .CountAsync(v => v.PlayerId == player.Id && v.IsActive, stoppingToken);
                                if ((currentFlags & (1 << 2)) == 0 && population >= 50)
                                {
                                    newFlags |= (1 << 2);
                                    diamondsToAward += 100;
                                }
                            }

                            // Logistics: Guild Depot
                            if (player.GuildId > 0 && (currentFlags & (1 << 3)) == 0)
                            {
                                long totalDonations = await dbContext.GuildDepotBalances
                                    .Where(g => g.GuildId == player.GuildId)
                                    .SumAsync(g => (long)g.Quantity, stoppingToken);

                                if (totalDonations >= 10000)
                                {
                                    newFlags |= (1 << 3);
                                    diamondsToAward += 100;
                                }
                            }

                            if (newFlags != currentFlags)
                            {
                                achievementRecord.ClaimedAchievementFlags = newFlags;
                                player.PremiumDiamonds += diamondsToAward;
                                await dbContext.SaveChangesAsync(stoppingToken);
                            }

                            await transaction.CommitAsync(stoppingToken);
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to process achievements: {ex.Message}");
                }
            }
        }

        private async Task ProcessClaimsQueueAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_registry.AchievementClaimQueue.TryDequeue(out var req))
                {
                    try
                    {
                        var retryingOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
                        await using var dbContext = new FolkIdleDbContext(retryingOptions.Options);

                        var strategy = dbContext.Database.CreateExecutionStrategy();
                        await strategy.ExecuteAsync(async () =>
                        {
                            dbContext.ChangeTracker.Clear();

                            // IsolationLevel.Serializable and FOR UPDATE (simulated via EF Core)
                            using var transaction = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, stoppingToken);

                            // Aggregating volatile Redis state (session) with DB state
                            var achievement = await dbContext.PlayerLifetimeAchievements
                                .FirstOrDefaultAsync(a => a.PlayerId == req.PlayerId && a.AchievementId == req.AchievementId, stoppingToken);

                            if (achievement == null)
                            {
                                achievement = new PlayerLifetimeAchievement
                                {
                                    PlayerId = req.PlayerId,
                                    AchievementId = (int)req.AchievementId,
                                    CurrentProgress = 0,
                                    IsClaimed = false
                                };
                                dbContext.PlayerLifetimeAchievements.Add(achievement);
                            }

                            if (!achievement.IsClaimed)
                            {
                                long volatileKillCount = req.LiveSession.GetCurrentMonsterKills();

                                // Depending on achievementId, check if requirements met. For example:
                                // Achievement 1: Kill 10,000 monsters
                                if (req.AchievementId == 1 && (achievement.CurrentProgress + volatileKillCount) >= 10000)
                                {
                                    achievement.IsClaimed = true;
                                    var playerRecord = await dbContext.PlayerRecords.FindAsync(new object[] { req.PlayerId }, stoppingToken);
                                    if (playerRecord != null)
                                    {
                                        playerRecord.PremiumDiamonds += 500;
                                    }
                                }
                                // Other achievements mapped here in future...

                                await dbContext.SaveChangesAsync(stoppingToken);
                            }

                            await transaction.CommitAsync(stoppingToken);
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AchievementEngine] Failed to process claim: {ex.Message}");
                    }
                }
                else
                {
                    await Task.Delay(10, stoppingToken);
                }
            }
        }
    }
}
