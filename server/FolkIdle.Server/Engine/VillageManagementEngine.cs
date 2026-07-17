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

        // Modul: Deferred Part 5 Implementation, Part 3. Two structural
        // progression buildings, both hard-capped at level 5. Town Hall
        // gates every other building's level ceiling (see
        // GetMaxBuildingLevelCeiling); the Crafting Workshop's level feeds
        // CraftingEngine.RollCraftedRarity's workshop multiplier (+0.05
        // probability weight per level - the exact parameter that method
        // already exposes). Their upgrades consume Logs and Ores through
        // the unified InventoryAndStashSystem path (Backpack first, then
        // Village Stash) instead of gold or the wood/stone commodity pair.
        public const int TownHallBuildingId = 9;
        public const int CraftingWorkshopBuildingId = 10;
        public const int MaxStructuralBuildingLevel = 5;

        // Town Hall's structural gate: other buildings may not upgrade
        // beyond 2 + (TownHallLevel * 2) - level 0 Town Hall permits
        // levels up to 2, a maxed (5) Town Hall permits 12, keeping the
        // Town Hall on the critical path of village progression.
        public static int GetMaxBuildingLevelCeiling(int townHallLevel)
        {
            return 2 + townHallLevel * 2;
        }

        // Modul: Economy Polish, Part 2. Town Hall passive gold generation
        // rate in whole gold per hour, by building level. Pure integer
        // switch - zero allocation, callable from the 10Hz tick and the
        // offline extrapolation path alike. Levels 0 and 1 share the base
        // 50/h rate (an unbuilt Town Hall still trickles the village
        // baseline); each level thereafter triples the throughput up to
        // the hard level-5 structural cap.
        public static long GetTownHallGoldRatePerHour(int townHallLevel)
        {
            return townHallLevel switch
            {
                <= 1 => 50L,
                2 => 150L,
                3 => 450L,
                4 => 1200L,
                _ => 3000L
            };
        }

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
                long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Modul 16: lazily apply any upgrade that already matured
                // before deciding whether the (single, village-wide) upgrade
                // slot is free - a request arriving right as the previous
                // upgrade's timer expires must not be spuriously rejected.
                await ResolveMaturedUpgradesAsync(db, playerId, nowEpoch);

                bool slotOccupied = await db.VillageInfrastructures
                    .AsNoTracking()
                    .AnyAsync(v => v.PlayerId == playerId && v.UpgradeTargetLevel > 0);

                if (slotOccupied)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 29, Value2 = 2, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

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

                // Modul: Deferred Part 5 Implementation, Part 3. Town Hall
                // structural gates: (a) the two structural buildings hard-cap
                // at level 5; (b) every OTHER building's next level must stay
                // within the Town Hall's ceiling (2 + TownHallLevel * 2), so
                // the Town Hall stays on the village's critical path.
                bool isStructuralBuilding = targetBuildingId == TownHallBuildingId || targetBuildingId == CraftingWorkshopBuildingId;
                if (isStructuralBuilding && infrastructure.CurrentLevel >= MaxStructuralBuildingLevel)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                if (!isStructuralBuilding && targetBuildingId != TownHallBuildingId)
                {
                    int townHallLevel = await db.VillageInfrastructures
                        .AsNoTracking()
                        .Where(v => v.PlayerId == playerId && v.BuildingId == TownHallBuildingId)
                        .Select(v => (int?)v.CurrentLevel)
                        .SingleOrDefaultAsync() ?? 0;

                    if (infrastructure.CurrentLevel + 1 > GetMaxBuildingLevelCeiling(townHallLevel))
                    {
                        await transaction.RollbackAsync();
                        Console.WriteLine($"Village upgrade rejected: building {targetBuildingId} at level {infrastructure.CurrentLevel} exceeds the Town Hall ceiling.");
                        return;
                    }
                }

                // Modul 16: the four passive-production buildings (Lumberjack/
                // Quarry/Mine/Warehouse) are raw-material sinks - upgrading them
                // costs Wood and Stone rather than the Gold the original four
                // service buildings (Forge/Inn/Breeding/Academy) use.
                bool isProductionBuilding = targetBuildingId >= LumberjackBuildingId && targetBuildingId <= WarehouseBuildingId;
                long cost;

                if (isStructuralBuilding)
                {
                    // Modul: Deferred Part 5 Implementation, Part 3.
                    // Structural upgrades are permanent resource sinks fed
                    // through the unified Backpack+Stash consumption path:
                    // Logs + Ores (raw_log + copper_ore), and the Crafting
                    // Workshop additionally consumes a rare log
                    // (golden_birch_log) per upgrade.
                    cost = CalculateProductionUpgradeCost(infrastructure.CurrentLevel);

                    if (!await InventoryAndStashSystem.TryConsumeUnifiedAsync(db, playerId, "raw_log", cost) ||
                        !await InventoryAndStashSystem.TryConsumeUnifiedAsync(db, playerId, "copper_ore", cost))
                    {
                        await transaction.RollbackAsync();
                        return;
                    }

                    if (targetBuildingId == CraftingWorkshopBuildingId)
                    {
                        long rareLogCost = Math.Max(1L, cost / 10L);
                        if (!await InventoryAndStashSystem.TryConsumeUnifiedAsync(db, playerId, "golden_birch_log", rareLogCost))
                        {
                            await transaction.RollbackAsync();
                            return;
                        }
                    }
                }
                else if (isProductionBuilding)
                {
                    cost = CalculateProductionUpgradeCost(infrastructure.CurrentLevel);

                    var woodRecord = await db.CommodityRecords
                        .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1} FOR UPDATE", playerId, WoodCommodityId)
                        .SingleOrDefaultAsync();

                    var stoneRecord = await db.CommodityRecords
                        .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1} FOR UPDATE", playerId, StoneCommodityId)
                        .SingleOrDefaultAsync();

                    if (woodRecord == null || stoneRecord == null || woodRecord.Quantity < cost || stoneRecord.Quantity < cost)
                    {
                        await transaction.RollbackAsync();
                        return;
                    }

                    woodRecord.Quantity -= cost;
                    stoneRecord.Quantity -= cost;
                }
                else
                {
                    var goldRecord = await db.CommodityRecords
                        .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();

                    cost = CalculateUpgradeCost(infrastructure.CurrentLevel);

                    if (goldRecord == null || goldRecord.Quantity < cost)
                    {
                        await transaction.RollbackAsync();
                        return;
                    }

                    goldRecord.Quantity -= cost;
                }

                infrastructure.UpgradeTargetLevel = infrastructure.CurrentLevel + 1;
                infrastructure.UpgradeCompletesAtEpoch = nowEpoch + CalculateUpgradeDurationSeconds(cost);

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

        // Modul 16: applies any upgrade whose timer has already matured
        // (CurrentLevel = UpgradeTargetLevel, queue cleared) so the next read
        // or upgrade decision for this player never acts on stale data.
        // Called both from ExecuteUpgradeBuildingAsync (before deciding
        // whether the upgrade slot is free) and BuildInfrastructureNotificationAsync
        // (so a plain village-state refresh self-heals too) - intentionally
        // does not open its own transaction, so it composes inside whichever
        // transaction the caller already has open.
        public static async Task ResolveMaturedUpgradesAsync(FolkIdleDbContext db, long playerId, long nowEpoch)
        {
            var maturedRows = await db.VillageInfrastructures
                .Where(v => v.PlayerId == playerId && v.UpgradeTargetLevel > 0 && v.UpgradeCompletesAtEpoch <= nowEpoch)
                .ToListAsync();

            if (maturedRows.Count == 0)
            {
                return;
            }

            for (int i = 0; i < maturedRows.Count; i++)
            {
                maturedRows[i].CurrentLevel = maturedRows[i].UpgradeTargetLevel;
                maturedRows[i].UpgradeTargetLevel = 0;
                maturedRows[i].UpgradeCompletesAtEpoch = 0;
            }

            await db.SaveChangesAsync();
        }

        private const long MinUpgradeDurationSeconds = 30L;

        // Modul 16: upgrade duration scales with the same cost curve the gold/
        // wood/stone price already uses (cost/10), floored so an early, cheap
        // upgrade is never effectively instant.
        public static long CalculateUpgradeDurationSeconds(long cost)
        {
            long duration = cost / 10L;
            return duration < MinUpgradeDurationSeconds ? MinUpgradeDurationSeconds : duration;
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
                || (buildingId >= LumberjackBuildingId && buildingId <= WarehouseBuildingId)
                || buildingId == TownHallBuildingId
                || buildingId == CraftingWorkshopBuildingId;
        }

        public static long CalculateUpgradeCost(int currentLevel)
        {
            if (currentLevel < 0) currentLevel = 0;
            double scaled = BaseUpgradeCost * Math.Pow(1.5, currentLevel);
            if (scaled > long.MaxValue) return long.MaxValue;
            return (long)Math.Ceiling(scaled);
        }

        private const long BaseProductionUpgradeCost = 100L;

        // Modul: GDD-mandated exponential curve, matching
        // CalculateUpgradeCost's own formula exactly (BaseCost *
        // 1.5^currentLevel) - previously a polynomial (currentLevel + 1)^1.8
        // that grew far slower than the true exponential gold-upgrade
        // formulas above, letting Lumberjack/Quarry/Mine/Warehouse scaling
        // drift out of balance with the rest of the endgame economy.
        // currentLevel (not currentLevel + 1) as the exponent base is
        // correct here and does not need a level-0-is-free special case:
        // 1.5^0 = 1, so the very first upgrade (level 0 -> 1) still costs
        // exactly BaseProductionUpgradeCost, never zero.
        public static long CalculateProductionUpgradeCost(int currentLevel)
        {
            if (currentLevel < 0) currentLevel = 0;
            double scaled = BaseProductionUpgradeCost * Math.Pow(1.5, currentLevel);
            if (scaled > long.MaxValue) return long.MaxValue;
            return (long)Math.Ceiling(scaled);
        }

        private static async Task<InfrastructureUpdateNotification> BuildInfrastructureNotificationAsync(FolkIdleDbContext db, long playerId)
        {
            await ResolveMaturedUpgradesAsync(db, playerId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

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
            byte pendingUpgradeBuildingId = 0;
            long pendingUpgradeCompletesAtEpoch = 0;

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

                if (levels[i].UpgradeTargetLevel > 0)
                {
                    pendingUpgradeBuildingId = (byte)levels[i].BuildingId;
                    pendingUpgradeCompletesAtEpoch = levels[i].UpgradeCompletesAtEpoch;
                }
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
                WarehouseLevel = ClampByte(warehouseLevel),
                PendingUpgradeBuildingId = pendingUpgradeBuildingId,
                PendingUpgradeCompletesAtEpoch = pendingUpgradeCompletesAtEpoch
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
