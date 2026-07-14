using System;
using System.Data;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    public sealed class VillageManagementEngine
    {
        public const int ForgeBuildingId = 1;
        public const int InnBuildingId = 2;
        public const int BreedingGroundsBuildingId = 3;
        public const int MentorshipAcademyBuildingId = 4;

        // Modul 16: Village Infrastructure Passive Production & Warehouse Caps.
        // Distinct from the 1-4 range above (Forge/Inn/Breeding/Academy) since
        // VillageInfrastructures is keyed on (PlayerId, BuildingId) - reusing 1-4
        // here would silently collide with those existing building rows.
        public const int LumberjackBuildingId = 5;
        public const int QuarryBuildingId = 6;
        public const int MineBuildingId = 7;
        public const int WarehouseBuildingId = 8;

        public const string WoodCommodityId = "wood";
        public const string StoneCommodityId = "stone";
        public const string IronOreCommodityId = "iron_ore";

        public const float LumberjackWoodRatePerLevel = 0.1f;
        public const float QuarryStoneRatePerLevel = 0.08f;
        public const float MineIronRatePerLevel = 0.05f;
        public const long WarehouseCapacityPerLevel = 1000L;

        public static long CalculateWarehouseMaxStorage(int warehouseLevel)
        {
            return warehouseLevel <= 0 ? 0L : (long)warehouseLevel * WarehouseCapacityPerLevel;
        }

        private const long BaseUpgradeCost = 1000L;

        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;

        public VillageManagementEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public async Task ExecuteUpgradeBuildingAsync(long playerId, uint targetBuildingId)
        {
            if (!IsValidBuildingId(targetBuildingId))
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 29, Value2 = 1, Timestamp = Environment.TickCount64 });
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"VillageInfrastructures\" (\"PlayerId\", \"BuildingId\", \"CurrentLevel\") VALUES ({0}, {1}, 0) ON CONFLICT (\"PlayerId\", \"BuildingId\") DO NOTHING",
                    playerId,
                    (int)targetBuildingId);

                var infrastructure = await db.VillageInfrastructures
                    .FromSqlRaw("SELECT * FROM \"VillageInfrastructures\" WHERE \"PlayerId\" = {0} AND \"BuildingId\" = {1} FOR UPDATE", playerId, (int)targetBuildingId)
                    .SingleOrDefaultAsync();

                if (infrastructure == null)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                // Modul 16: the four passive-production buildings (Lumberjack/
                // Quarry/Mine/Warehouse) are raw-material sinks - upgrading them
                // costs Wood and Stone rather than the Gold the original four
                // service buildings (Forge/Inn/Breeding/Academy) use.
                bool isProductionBuilding = targetBuildingId >= LumberjackBuildingId && targetBuildingId <= WarehouseBuildingId;

                if (isProductionBuilding)
                {
                    long productionCost = CalculateProductionUpgradeCost(infrastructure.CurrentLevel);

                    var woodRecord = await db.CommodityRecords
                        .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1} FOR UPDATE", playerId, WoodCommodityId)
                        .SingleOrDefaultAsync();

                    var stoneRecord = await db.CommodityRecords
                        .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1} FOR UPDATE", playerId, StoneCommodityId)
                        .SingleOrDefaultAsync();

                    if (woodRecord == null || stoneRecord == null || woodRecord.Quantity < productionCost || stoneRecord.Quantity < productionCost)
                    {
                        await transaction.RollbackAsync();
                        return;
                    }

                    woodRecord.Quantity -= productionCost;
                    stoneRecord.Quantity -= productionCost;
                }
                else
                {
                    var goldRecord = await db.CommodityRecords
                        .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();

                    long cost = CalculateUpgradeCost(infrastructure.CurrentLevel);

                    if (goldRecord == null || goldRecord.Quantity < cost)
                    {
                        await transaction.RollbackAsync();
                        return;
                    }

                    goldRecord.Quantity -= cost;
                }

                infrastructure.CurrentLevel++;

                await db.SaveChangesAsync();
                var notification = await BuildInfrastructureNotificationAsync(db, playerId);
                await transaction.CommitAsync();

                _playerRegistry.InfrastructureUpdateQueue.Enqueue(notification);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Village upgrade failed: {ex.Message}");
            }
        }

        public async Task ExecuteEvictVillagerAsync(long playerId, uint targetVillagerSlot)
        {
            if (targetVillagerSlot > int.MaxValue)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 30, Value2 = 1, Timestamp = Environment.TickCount64 });
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var resident = await db.VillageResidents
                    .FromSqlRaw("SELECT * FROM \"VillageResidents\" WHERE \"PlayerId\" = {0} AND \"SlotIndex\" = {1} FOR UPDATE", playerId, (int)targetVillagerSlot)
                    .SingleOrDefaultAsync();

                if (resident == null)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                resident.IsActive = false;
                resident.EfficiencyModifier = 0.0;

                await db.SaveChangesAsync();
                _ = await CalculateAccountProgressionScoreAsync(db, playerId);
                var notification = await BuildInfrastructureNotificationAsync(db, playerId);
                await transaction.CommitAsync();

                _playerRegistry.InfrastructureUpdateQueue.Enqueue(notification);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Village eviction failed: {ex.Message}");
            }
        }

        public static bool IsValidBuildingId(uint buildingId)
        {
            return (buildingId >= ForgeBuildingId && buildingId <= MentorshipAcademyBuildingId)
                || (buildingId >= LumberjackBuildingId && buildingId <= WarehouseBuildingId);
        }

        public static long CalculateUpgradeCost(int currentLevel)
        {
            if (currentLevel < 0) currentLevel = 0;
            double scaled = BaseUpgradeCost * Math.Pow(1.5, currentLevel);
            if (scaled > long.MaxValue) return long.MaxValue;
            return (long)Math.Ceiling(scaled);
        }

        private const long BaseProductionUpgradeCost = 100L;

        // Modul 16: Wood/Stone cost for Lumberjack/Quarry/Mine/Warehouse upgrades.
        // Uses (currentLevel + 1) rather than currentLevel as the exponent base so
        // the very first upgrade (level 0 -> 1) is not free.
        public static long CalculateProductionUpgradeCost(int currentLevel)
        {
            if (currentLevel < 0) currentLevel = 0;
            double scaled = BaseProductionUpgradeCost * Math.Pow(currentLevel + 1, 1.8);
            if (scaled > long.MaxValue) return long.MaxValue;
            return (long)Math.Ceiling(scaled);
        }

        private static async Task<InfrastructureUpdateNotification> BuildInfrastructureNotificationAsync(FolkIdleDbContext db, long playerId)
        {
            var levels = await db.VillageInfrastructures
                .AsNoTracking()
                .Where(v => v.PlayerId == playerId)
                .ToListAsync();

            int forgeLevel = 0;
            int innLevel = 0;
            int breedingLevel = 0;
            int academyLevel = 0;
            int lumberjackLevel = 0;
            int quarryLevel = 0;
            int mineLevel = 0;
            int warehouseLevel = 0;

            for (int i = 0; i < levels.Count; i++)
            {
                if (levels[i].BuildingId == ForgeBuildingId) forgeLevel = levels[i].CurrentLevel;
                else if (levels[i].BuildingId == InnBuildingId) innLevel = levels[i].CurrentLevel;
                else if (levels[i].BuildingId == BreedingGroundsBuildingId) breedingLevel = levels[i].CurrentLevel;
                else if (levels[i].BuildingId == MentorshipAcademyBuildingId) academyLevel = levels[i].CurrentLevel;
                else if (levels[i].BuildingId == LumberjackBuildingId) lumberjackLevel = levels[i].CurrentLevel;
                else if (levels[i].BuildingId == QuarryBuildingId) quarryLevel = levels[i].CurrentLevel;
                else if (levels[i].BuildingId == MineBuildingId) mineLevel = levels[i].CurrentLevel;
                else if (levels[i].BuildingId == WarehouseBuildingId) warehouseLevel = levels[i].CurrentLevel;
            }

            int population = await db.VillageResidents
                .AsNoTracking()
                .CountAsync(v => v.PlayerId == playerId && v.IsActive);

            return new InfrastructureUpdateNotification
            {
                PlayerId = playerId,
                ForgeLevel = ClampByte(forgeLevel),
                InnLevel = ClampByte(innLevel),
                BreedingLevel = ClampByte(breedingLevel),
                AcademyLevel = ClampByte(academyLevel),
                CurrentPopulationCount = ClampByte(population),
                CurrentToolTier = forgeLevel,
                MaxPopulationCapacity = CalculatePopulationCapacity(innLevel),
                InnMaturationBonus = innLevel,
                LumberjackLevel = ClampByte(lumberjackLevel),
                QuarryLevel = ClampByte(quarryLevel),
                MineLevel = ClampByte(mineLevel),
                WarehouseLevel = ClampByte(warehouseLevel)
            };
        }

        private static async Task<int> CalculateAccountProgressionScoreAsync(FolkIdleDbContext db, long playerId)
        {
            int infrastructureScore = await db.VillageInfrastructures
                .AsNoTracking()
                .Where(v => v.PlayerId == playerId)
                .SumAsync(v => (int?)v.CurrentLevel) ?? 0;

            int activeResidents = await db.VillageResidents
                .AsNoTracking()
                .CountAsync(v => v.PlayerId == playerId && v.IsActive);

            return infrastructureScore * 100 + activeResidents * 10;
        }

        public static int CalculatePopulationCapacity(int innLevel)
        {
            if (innLevel < 0) innLevel = 0;
            return 10 + (innLevel * 5);
        }

        private static byte ClampByte(int value)
        {
            if (value <= 0) return 0;
            if (value >= byte.MaxValue) return byte.MaxValue;
            return (byte)value;
        }
    }
}
