using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    public class SeasonEraEngine
    {
        private const long EraDurationSeconds = 90L * 24L * 60L * 60L;
        private const int PurgeChunkSize = 100;
        private readonly IServiceProvider _serviceProvider;
        private CancellationTokenSource _cts = new();

        public SeasonEraEngine(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ExecuteAsync(_cts.Token));
        }

        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ExecuteEraCheckAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Season era check failed: {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        private async Task ExecuteEraCheckAsync(CancellationToken stoppingToken)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int closedEraId = 0;

            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, stoppingToken);

                var activeEra = await db.SeasonalEraRecords
                    .FromSqlRaw("SELECT * FROM \"SeasonalEraRecords\" WHERE \"IsActive\" = TRUE ORDER BY \"EndTimestamp\" LIMIT 1 FOR UPDATE")
                    .FirstOrDefaultAsync(stoppingToken);

                if (activeEra == null)
                {
                    db.SeasonalEraRecords.Add(new SeasonalEraRecord
                    {
                        EndTimestamp = now + EraDurationSeconds,
                        IsActive = true
                    });
                    await db.SaveChangesAsync(stoppingToken);
                    await transaction.CommitAsync(stoppingToken);
                    return;
                }

                if (activeEra.EndTimestamp > now)
                {
                    await transaction.CommitAsync(stoppingToken);
                    return;
                }

                activeEra.IsActive = false;
                closedEraId = activeEra.EraId;
                db.SeasonalEraRecords.Add(new SeasonalEraRecord
                {
                    EndTimestamp = now + EraDurationSeconds,
                    IsActive = true
                });

                await db.SaveChangesAsync(stoppingToken);
                await transaction.CommitAsync(stoppingToken);
            }

            if (closedEraId > 0)
            {
                await ExecuteCascadePurgeAsync(closedEraId, stoppingToken);
            }
        }

        private async Task ExecuteCascadePurgeAsync(int closedEraId, CancellationToken stoppingToken)
        {
            List<long> playerIds;
            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                playerIds = await db.PlayerRecords
                    .AsNoTracking()
                    .OrderBy(p => p.Id)
                    .Select(p => p.Id)
                    .ToListAsync(stoppingToken);
            }

            for (int index = 0; index < playerIds.Count; index += PurgeChunkSize)
            {
                var chunk = playerIds.Skip(index).Take(PurgeChunkSize).ToArray();
                await ExecuteCascadeChunkAsync(closedEraId, chunk, stoppingToken);
            }
        }

        private async Task ExecuteCascadeChunkAsync(int closedEraId, long[] playerIds, CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, stoppingToken);

            try
            {
                for (int i = 0; i < playerIds.Length; i++)
                {
                    long playerId = playerIds[i];
                    var player = await db.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                        .FirstOrDefaultAsync(stoppingToken);
                    if (player == null)
                    {
                        continue;
                    }

                    var gold = await db.CommodityRecords
                        .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                        .FirstOrDefaultAsync(stoppingToken);

                    var characters = await db.CharacterRecords
                        .FromSqlRaw("SELECT * FROM characters WHERE \"PlayerId\" = {0} FOR UPDATE", playerId)
                        .ToListAsync(stoppingToken);

                    long levelSquareSum = 0L;
                    for (int c = 0; c < characters.Count; c++)
                    {
                        long level = characters[c].Level;
                        if (level < 0L) level = 0L;
                        levelSquareSum += level * level;
                    }

                    double totalGoldAccumulated = Math.Max(0.0, (double)(gold?.Quantity ?? 0L));
                    double lifetimeContributionScore = Math.Max(0.0, (double)player.CurrentXp);
                    int shardsEarned = CalculateLegacyShards(totalGoldAccumulated, (double)levelSquareSum, lifetimeContributionScore);

                    int inheritedSlots = await LoadUnlockedSlotMaskAsync(db, playerId, stoppingToken);
                    var ledger = await db.PlayerLegacyLedgers
                        .FromSqlRaw("SELECT * FROM \"PlayerLegacyLedgers\" WHERE \"PlayerId\" = {0} AND \"EraId\" = {1} FOR UPDATE", playerId, closedEraId)
                        .FirstOrDefaultAsync(stoppingToken);

                    if (ledger == null)
                    {
                        ledger = new PlayerLegacyLedger
                        {
                            PlayerId = playerId,
                            EraId = closedEraId,
                            LegacyShardBalance = 0,
                            CitizenMultiSlotsUnlocked = inheritedSlots
                        };
                        db.PlayerLegacyLedgers.Add(ledger);
                    }

                    if (shardsEarned > 0)
                    {
                        ledger.LegacyShardBalance += shardsEarned;
                    }

                    if (gold != null)
                    {
                        gold.Quantity = 0L;
                    }

                    await db.Database.ExecuteSqlRawAsync("DELETE FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" <> 'gold'", playerId);
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM \"EquipmentInstances\" WHERE \"PlayerId\" = {0}", playerId);
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM \"BankEquipmentInstances\" WHERE \"PlayerId\" = {0}", playerId);
                    await db.Database.ExecuteSqlRawAsync("UPDATE \"MarketEquipmentInstances\" SET \"QualityTier\" = 1 WHERE \"PlayerId\" = {0} AND \"IsLockedInEscrow\" = FALSE", playerId);
                    await db.Database.ExecuteSqlRawAsync(
                        "INSERT INTO \"VillageInfrastructures\" (\"PlayerId\", \"BuildingId\", \"CurrentLevel\") VALUES ({0}, {1}, 1) ON CONFLICT (\"PlayerId\", \"BuildingId\") DO UPDATE SET \"CurrentLevel\" = 1",
                        playerId,
                        VillageManagementEngine.ForgeBuildingId);
                }

                await db.SaveChangesAsync(stoppingToken);
                await transaction.CommitAsync(stoppingToken);
            }
            catch
            {
                await transaction.RollbackAsync(stoppingToken);
                throw;
            }
        }

        private static int CalculateLegacyShards(double totalGoldAccumulated, double characterLevelSquareSum, double lifetimeContributionScore)
        {
            double safeGold = Math.Max(0.0, totalGoldAccumulated);
            double safeLevelSquareSum = Math.Max(0.0, characterLevelSquareSum);
            double safeContribution = Math.Max(0.0, lifetimeContributionScore);

            double raw = Math.Floor(10.0 * Math.Log10(safeGold + 1.0) + 0.05 * safeLevelSquareSum + 0.1 * safeContribution);
            if (raw < 0.0) return 0;
            if (raw > int.MaxValue) return int.MaxValue;
            return (int)raw;
        }

        private static async Task<int> LoadUnlockedSlotMaskAsync(FolkIdleDbContext db, long playerId, CancellationToken stoppingToken)
        {
            var ledgers = await db.PlayerLegacyLedgers
                .AsNoTracking()
                .Where(l => l.PlayerId == playerId)
                .Select(l => l.CitizenMultiSlotsUnlocked)
                .ToListAsync(stoppingToken);

            int mask = 0;
            for (int i = 0; i < ledgers.Count; i++)
            {
                mask |= ledgers[i];
            }
            return mask;
        }
    }
}
