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
    public sealed class SeasonalRotationEngine
    {
        private const long EraDurationSeconds = 90L * 24L * 60L * 60L;
        private const int PlayerBatchSize = 100;
        private const double LegacyShardFloorEpsilon = 0.000000001;

        private readonly IServiceProvider _serviceProvider;
        private CancellationTokenSource _cts = new();

        public SeasonalRotationEngine(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ExecuteAsync(_cts.Token));
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
                    Console.WriteLine($"Seasonal rotation failed: {ex.Message}");
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

            if (closedEraId <= 0)
            {
                return;
            }

            GlobalEngineState.IsEraTransitionActive = true;
            
            var networkSystem = _serviceProvider.GetService<FolkIdle.Server.Network.NetworkBroadcastSystem>();
            if (networkSystem != null)
            {
                await networkSystem.DisconnectAllClientsGracefullyAsync();
            }
            try
            {
                await ExecutePlayerRolloversAsync(closedEraId, stoppingToken);
            }
            finally
            {
                GlobalEngineState.IsEraTransitionActive = false;
            }
        }

        private async Task ExecutePlayerRolloversAsync(int closedEraId, CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, stoppingToken);

            try
            {
                var playerIds = await db.PlayerRecords
                    .AsNoTracking()
                    .OrderBy(p => p.Id)
                    .Select(p => p.Id)
                    .ToListAsync(stoppingToken);

                var newLedgers = new System.Collections.Concurrent.ConcurrentBag<PlayerLegacyLedger>();

                var chunks = playerIds.Chunk(PlayerBatchSize).ToArray();
                foreach (var chunk in chunks)
                {
                    var chunkIds = chunk.ToList();
                    var goldDict = await db.CommodityRecords
                        .AsNoTracking()
                        .Where(c => chunkIds.Contains(c.PlayerId) && c.ItemId == "gold")
                        .ToDictionaryAsync(c => c.PlayerId, c => c.Quantity, stoppingToken);
                        
                    var charsByPlayer = await db.CharacterRecords
                        .AsNoTracking()
                        .Where(c => chunkIds.Contains(c.PlayerId))
                        .GroupBy(c => c.PlayerId)
                        .ToDictionaryAsync(g => g.Key, g => g.ToList(), stoppingToken);
                        
                    var eqByPlayer = await db.EquipmentInstances
                        .AsNoTracking()
                        .Where(c => chunkIds.Contains(c.PlayerId))
                        .GroupBy(c => c.PlayerId)
                        .ToDictionaryAsync(g => g.Key, g => g.ToList(), stoppingToken);
                        
                    var bankByPlayer = await db.BankEquipmentInstances
                        .AsNoTracking()
                        .Where(c => chunkIds.Contains(c.PlayerId))
                        .GroupBy(c => c.PlayerId)
                        .ToDictionaryAsync(g => g.Key, g => g.ToList(), stoppingToken);
                        
                    var marketByPlayer = await db.MarketEquipmentInstances
                        .AsNoTracking()
                        .Where(c => chunkIds.Contains(c.PlayerId) && !c.IsLockedInEscrow)
                        .GroupBy(c => c.PlayerId)
                        .ToDictionaryAsync(g => g.Key, g => g.ToList(), stoppingToken);

                    var ledgers = await db.PlayerLegacyLedgers
                        .Where(l => l.EraId == closedEraId && chunkIds.Contains(l.PlayerId))
                        .ToDictionaryAsync(l => l.PlayerId, stoppingToken);

                    foreach (var playerId in chunk)
                    {
                        long levelSquareSum = 0L;
                        if (charsByPlayer.TryGetValue(playerId, out var characters))
                        {
                            foreach (var ch in characters)
                            {
                                long level = Math.Max(1, ch.Level);
                                levelSquareSum += level * level;
                            }
                        }

                        var eq = eqByPlayer.GetValueOrDefault(playerId, new List<EquipmentInstance>());
                        var bEq = bankByPlayer.GetValueOrDefault(playerId, new List<BankEquipmentInstance>());
                        var mEq = marketByPlayer.GetValueOrDefault(playerId, new List<MarketEquipmentInstance>());
                        long inventoryScore = CalculateInventoryScore(eq, bEq, mEq);

                        long totalGold = Math.Max(0L, goldDict.GetValueOrDefault(playerId, 0L));
                        int shardsEarned = CalculateLegacyShards(totalGold, levelSquareSum, inventoryScore);

                        int inheritedSlots = await LoadUnlockedSlotMaskAsync(db, playerId, stoppingToken);
                        
                        if (ledgers.TryGetValue(playerId, out var ledger))
                        {
                            ledger.LegacyShardBalance = SafeAdd(ledger.LegacyShardBalance, shardsEarned);
                        }
                        else
                        {
                            var newLedger = new PlayerLegacyLedger
                            {
                                PlayerId = playerId,
                                EraId = closedEraId,
                                LegacyShardBalance = shardsEarned,
                                CitizenMultiSlotsUnlocked = inheritedSlots
                            };
                            db.PlayerLegacyLedgers.Add(newLedger);
                        }
                    }

                    await db.SaveChangesAsync(stoppingToken); // save ledgers per chunk to avoid memory bloat
                }

                // Bulk Updates & Truncations within the same transaction
                await db.Database.ExecuteSqlRawAsync("UPDATE \"PlayerRecords\" SET \"CurrentLevel\" = 1, \"CurrentXp\" = 0, \"AccumulatedTimeBankSeconds\" = 0, \"ActiveOffensivePotionId\" = 0, \"OffensivePotionDurationMs\" = 0, \"ActiveDefensivePotionId\" = 0, \"DefensivePotionDurationMs\" = 0, \"BankedChronoSeconds\" = 0, \"IsChronoAccelerating\" = FALSE", stoppingToken);
                await db.Database.ExecuteSqlRawAsync("UPDATE \"CommodityRecords\" SET \"Quantity\" = 0 WHERE \"ItemId\" = 'gold'", stoppingToken);
                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"CommodityRecords\" WHERE \"ItemId\" <> 'gold'", stoppingToken);

                // Modul 41: unconditional full-table wipes use TRUNCATE ...
                // RESTART IDENTITY CASCADE rather than DELETE FROM. Unlike
                // DELETE, TRUNCATE deallocates pages directly and produces a
                // small, fixed WAL footprint regardless of row count, avoiding
                // WAL bloat and the long-held-lock/gateway-timeout risk of
                // per-row tombstones on large tables at season-reset scale.
                // CASCADE is a no-op safety net here (this schema does not
                // enforce real FK constraints on these tables) but protects
                // against a future FK addition silently breaking this reset.
                await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"EquipmentInstances\", \"BankEquipmentInstances\" RESTART IDENTITY CASCADE", stoppingToken);

                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MarketOrderRecords\" o USING \"MarketEquipmentInstances\" e WHERE o.\"EquipmentInstanceId\" = e.\"Id\" AND e.\"IsLockedInEscrow\" = FALSE AND o.\"Status\" = 0 AND o.\"OrderType\" = 'SELL'", stoppingToken);
                await db.Database.ExecuteSqlRawAsync("UPDATE \"MarketOrderRecords\" o SET \"EquipmentInstanceId\" = NULL FROM \"MarketEquipmentInstances\" e WHERE o.\"EquipmentInstanceId\" = e.\"Id\" AND e.\"IsLockedInEscrow\" = FALSE", stoppingToken);
                // Modul 41: this TRUNCATE must run after the two statements
                // above, which still need to query MarketEquipmentInstances -
                // truncating it earlier would leave those queries with nothing
                // to match against.
                await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"MarketEquipmentInstances\" RESTART IDENTITY CASCADE", stoppingToken);
                await db.Database.ExecuteSqlRawAsync("UPDATE characters SET \"Level\" = 1, \"AgeTicks\" = 0, \"AgePhase\" = 1", stoppingToken);
                await db.Database.ExecuteSqlRawAsync("UPDATE player_race_masteries SET \"MasteryLevel\" = 1, \"CumulativeXp\" = 0", stoppingToken);
                await db.Database.ExecuteSqlRawAsync("UPDATE \"VillageInfrastructures\" SET \"CurrentLevel\" = 1", stoppingToken);
                await db.Database.ExecuteSqlRawAsync("UPDATE \"PlayerChroniclePasses\" SET \"PassLevel\" = 0, \"AccumulatedXp\" = 0, \"ClaimedMilestonesBitmask\" = 0", stoppingToken);

                await transaction.CommitAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(stoppingToken);
                Console.WriteLine($"SEASONAL RESET FAILURE - EraId {closedEraId}: {ex}");
                throw;
            }
        }

        public static int CalculateLegacyShards(long totalGold, long characterLevelSquareSum, long inventoryScore)
        {
            double goldTerm = 12.5 * Math.Log10(Math.Max(0.0, (double)totalGold) + 1.0);
            double levelTerm = 0.05 * Math.Max(0.0, (double)characterLevelSquareSum);
            double inventoryTerm = 1.50 * Math.Max(0.0, (double)inventoryScore);
            double raw = Math.Floor(goldTerm + levelTerm + inventoryTerm + LegacyShardFloorEpsilon);
            if (raw <= 0.0) return 0;
            if (raw >= int.MaxValue) return int.MaxValue;
            return (int)raw;
        }

        private static long CalculateInventoryScore(List<EquipmentInstance> equipment, List<BankEquipmentInstance> bankEquipment, List<MarketEquipmentInstance> marketEquipment)
        {
            long score = 0L;
            for (int i = 0; i < equipment.Count; i++) score += Math.Max(1, equipment[i].QualityTier);
            for (int i = 0; i < bankEquipment.Count; i++) score += Math.Max(1, bankEquipment[i].QualityTier);
            for (int i = 0; i < marketEquipment.Count; i++) score += Math.Max(1, marketEquipment[i].QualityTier);
            return score;
        }

        private static int SafeAdd(int left, int right)
        {
            long value = (long)left + right;
            if (value <= 0L) return 0;
            if (value >= int.MaxValue) return int.MaxValue;
            return (int)value;
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
