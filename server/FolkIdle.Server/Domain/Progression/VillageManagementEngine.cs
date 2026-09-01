using System;
using System.Data;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Progression
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
        public const float MineIronRatePerLevel = 0.05f;
        public const long WarehouseCapacityPerLevel = 1000L;

        // Modul: THE ORES ARE THE ONES PLAYERS ACTUALLY EARN, 2026-09-01.
        //
        // These read copper_ore / iron_ore / silver_ore before now - the legacy
        // six-slug gathering namespace (GetMaterialString) - while drops and
        // gathering pay out the CATALOGUED region ores in items.json. The two
        // are different CommodityRecords rows, so the village was priced in a
        // currency nothing in the game produces at any scale.
        //
        // Measured on the live account that reported this: 152,968 birch_log
        // and 629 malachite_ore against TWENTY-FIVE copper_ore, with a level
        // 0->1 upgrade costing 100. Not one building could be upgraded, ever,
        // and no amount of play would have changed that.
        //
        // Copper is the worst case of the trap: it exists as copper_ore (this
        // table, before now), copper_ore_crafting_material (recipes) and
        // malachite_ore (the region-1 ore drops give). Three names, one idea.
        // The catalogued ore is the real content - it has a RegionTier, a gold
        // value and a drop table - so that is what the village charges.
        //
        // Logs were already correct: birch/willow/acacia/frostpine/ebon are all
        // catalogued and all obtainable. Only the ore column moves.
        // Modul: ONE PAIR PER REGION, COMMON AND RARE, 2026-09-01.
        //
        // This is the same pairing the gathering loot tables and the guild's
        // BuffTierMaterials already use, so the village is no longer the odd
        // one out:
        //
        //   region 1  copper_ore    / malachite_ore
        //   region 2  iron_ore      / hematite_ore
        //   region 3  sulfur_ore    / obsidian_ore
        //   region 4  silver_ore    / cobalt_ore
        //   region 5  darksteel_ore / astralite_ore
        //
        // An earlier pass this same day moved the ore column to the RARE ores
        // (malachite at tier 1) because the common ones looked unobtainable.
        // They looked that way for a different reason: the mining loot table
        // pointed copper/iron/obsidian/silver at the *_crafting_material
        // variants, since those four had no items.json entry to point at. With
        // that corrected the commons are what mining actually pays out, which
        // is what a village should be built from - and the rares stay rare.
        private static readonly (string Log, string Ore, string RareLog, string RareOre)[] TierMaterials = new[]
        {
            ("birch_log",     "copper_ore",    "golden_birch_log",     "malachite_ore"),
            ("willow_log",    "iron_ore",      "golden_willow_log",    "hematite_ore"),
            ("acacia_log",    "sulfur_ore",    "golden_acacia_log",    "obsidian_ore"),
            ("frostpine_log", "silver_ore",    "golden_frostpine_log", "cobalt_ore"),
            ("ebon_log",      "darksteel_ore", "golden_ebon_log",      "astralite_ore"),
        };

        public static (string Log, string Ore, string RareLog, string RareOre) GetTierMaterials(int currentLevel)
        {
            int tier = Math.Clamp(currentLevel / 5, 0, 4);
            return TierMaterials[tier];
        }

        /// <summary>
        /// How often a production building pays its tier's RARE material
        /// instead of the common one, as a percentage.
        /// </summary>
        /// <remarks>
        /// Modul: 10, matching the 90/10 weights the gathering loot tables use
        /// for the same pairs. A Mine automates mining, so it should pay out
        /// what mining pays out - a building whose yield table disagreed with
        /// the activity it represents would be two descriptions of one idea,
        /// and this codebase has lost enough to that already.
        /// </remarks>
        public const int RareYieldPercent = 10;

        public static long CalculateWarehouseMaxStorage(int warehouseLevel)
        {
            return warehouseLevel <= 0 ? 0L : (long)warehouseLevel * WarehouseCapacityPerLevel;
        }

        // Modul: 500 and a 1.4 curve, from 1,000 and 1.5 - see
        // CalculateUpgradeCost. A level-10 service building was 57,665 gold on
        // the old curve, which is over two hours of region-2 income for one
        // level of one building, on top of the logs and ore it now also costs.
        private const long BaseUpgradeCost = 500L;

        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;

        public VillageManagementEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        /// <summary>
        /// Tells the player WHY an upgrade did not happen.
        /// </summary>
        /// <remarks>
        /// Modul: every refusal in ExecuteUpgradeBuildingAsync used to roll the
        /// transaction back and return, saying nothing to anybody. The client's
        /// button is only disabled while another upgrade is in flight, so a
        /// player pressing an unaffordable or ceiling-blocked upgrade got no
        /// toast, no error and no change - the press simply did nothing, which
        /// is indistinguishable from the feature being broken and is exactly
        /// how it was reported.
        /// </remarks>
        private void Reject(long playerId, FolkIdle.Server.Network.CommandResultCode code)
        {
            _playerRegistry?.EnqueueCommandResult(playerId, (byte)code);
        }

        /// <summary>
        /// Pays for a villager now. See VillageArrivalEngine.RecruitAsync for
        /// the rule; this is the scope, the lock and the transaction around it.
        ///
        /// SERIALIZABLE with the player row locked FOR UPDATE, because the
        /// escalating price lives on that row: two concurrent recruits reading
        /// the same counter would both charge the cheaper price and the
        /// escalation - the only thing stopping this being a slot machine -
        /// would be worth nothing.
        /// </summary>
        public async Task ExecuteRecruitVillagerAsync(long playerId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var player = await db.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                    .FirstOrDefaultAsync();

                if (player == null)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                int innLevel = await db.VillageInfrastructures
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId && v.BuildingId == InnBuildingId)
                    .Select(v => v.CurrentLevel)
                    .FirstOrDefaultAsync();

                string? refusal = await Engine.VillageArrivalEngine.RecruitAsync(
                    db, player, innLevel, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                if (refusal != null)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 70, Value2 = 1, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Recruit villager failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Turns somebody away, freeing a slot for a better roll.
        ///
        /// The other half of the population cap being a DECISION rather than a
        /// timer: a full village stops the arrival clock entirely, so somebody
        /// who turned up at 4/3/9/2 is occupying the slot the twenty would have
        /// walked into. An elder is refused - they married into the line and
        /// are a record of it, not a resident.
        /// </summary>
        public async Task ExecuteDismissNewcomerAsync(long playerId, long newcomerId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            try
            {
                bool dismissed = await Engine.VillageArrivalEngine.DismissAsync(db, playerId, newcomerId);
                if (!dismissed)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 71, Value2 = 1, Timestamp = Environment.TickCount64 });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Dismiss newcomer failed: {ex.Message}");
            }
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

                // Modul: THE FIRST UPGRADE BUILDS IT, 2026-09-01.
                //
                // This used to roll back and return when a player had no row
                // for the building, and the ONLY place a VillageInfrastructure
                // row was ever created was DevFixtureSeeder. Real accounts were
                // therefore born with no rows at all, and every building they
                // had never somehow acquired a row was permanently
                // unupgradable - silently, because the rollback said nothing.
                //
                // That included the TOWN HALL, which gates the level ceiling of
                // every other building, so the whole village was frozen behind
                // a building that could not be started. Measured on the live
                // account that reported it: rows for Forge and Inn only, both
                // at the level-0 Town Hall ceiling of 2, nothing else buildable
                // and no way to raise the cap.
                //
                // A missing row now means "not built yet" rather than "does not
                // exist": it is created at level 0 inside this same transaction
                // and the upgrade proceeds to level 1. The row still has to be
                // paid for and still has to clear the ceiling.
                if (infrastructure == null)
                {
                    infrastructure = new VillageInfrastructure
                    {
                        PlayerId = playerId,
                        BuildingId = (int)targetBuildingId,
                        CurrentLevel = 0
                    };
                    db.VillageInfrastructures.Add(infrastructure);
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
                    Reject(playerId, FolkIdle.Server.Network.CommandResultCode.MaxTierReached);
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
                        Reject(playerId, FolkIdle.Server.Network.CommandResultCode.TownHallCeilingReached);
                        return;
                    }
                }

                // Modul 16: the four passive-production buildings (Lumberjack/
                // Quarry/Mine/Warehouse) are raw-material sinks - upgrading them
                // costs Wood and Stone rather than the Gold the original four
                // service buildings (Forge/Inn/Breeding/Academy) use.
                bool isProductionBuilding = targetBuildingId == LumberjackBuildingId || targetBuildingId == MineBuildingId || targetBuildingId == WarehouseBuildingId;
                long cost = CalculateProductionUpgradeCost(infrastructure.CurrentLevel);
                
                var tierMats = GetTierMaterials(infrastructure.CurrentLevel);

                // ALL buildings consume tiered logs and ores now
                if (!await InventoryAndStashSystem.TryConsumeUnifiedAsync(db, playerId, tierMats.Log, cost) ||
                    !await InventoryAndStashSystem.TryConsumeUnifiedAsync(db, playerId, tierMats.Ore, cost))
                {
                    await transaction.RollbackAsync();
                    Reject(playerId, FolkIdle.Server.Network.CommandResultCode.InsufficientMaterials);
                    return;
                }

                if (isStructuralBuilding && targetBuildingId == CraftingWorkshopBuildingId)
                {
                    long rareLogCost = Math.Max(1L, cost / 10L);
                    if (!await InventoryAndStashSystem.TryConsumeUnifiedAsync(db, playerId, tierMats.RareLog, rareLogCost))
                    {
                        await transaction.RollbackAsync();
                        Reject(playerId, FolkIdle.Server.Network.CommandResultCode.InsufficientMaterials);
                        return;
                    }
                }
                // Modul: GOLD IS PART OF EVERY UPGRADE NOW, 2026-09-01.
                //
                // It used to fund only the four service buildings, which left
                // the production half priced in ore alone - and ore is the
                // scarce half of the economy while gold is the abundant one.
                // The account that reported the village as unupgradable was
                // sitting on ELEVEN MILLION gold with no sink for it and 25 ore.
                //
                // Charging both means an upgrade is paid for out of the thing a
                // player has too much of AND the thing they have to go and get,
                // rather than gating everything on the scarcer one alone.
                // Structural buildings keep their rare-log component above
                // instead; they are the ones the whole village is gated behind
                // and doubling their price would deepen the very wall this
                // change exists to remove.
                if (!isStructuralBuilding)
                {
                    long goldCost = CalculateUpgradeCost(infrastructure.CurrentLevel);
                    var goldRecord = await db.CommodityRecords
                        .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();

                    if (goldRecord == null || goldRecord.Quantity < goldCost)
                    {
                        await transaction.RollbackAsync();
                        Reject(playerId, FolkIdle.Server.Network.CommandResultCode.InsufficientGold);
                        return;
                    }
                    goldRecord.Quantity -= goldCost;
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
            // Modul: the Mentorship Academy is NOT in this range any more. The
            // feature it served was removed, and a building whose only purpose
            // was to raise a contract cap is a gold sink with nothing on the
            // end of it.
            return (buildingId >= ForgeBuildingId && buildingId <= BreedingGroundsBuildingId)
                || buildingId == LumberjackBuildingId || buildingId == MineBuildingId || buildingId == WarehouseBuildingId
                || buildingId == TownHallBuildingId
                || buildingId == CraftingWorkshopBuildingId;
        }

        public static long CalculateUpgradeCost(int currentLevel)
        {
            if (currentLevel < 0) currentLevel = 0;
            double scaled = BaseUpgradeCost * Math.Pow(1.4, currentLevel);
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
            // Cost resets per tier
            int tierLevel = currentLevel % 5;
            double scaled = BaseProductionUpgradeCost * Math.Pow(1.5, tierLevel);
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
            int mineLevel = 0;
            int warehouseLevel = 0;
            int townHallLevel = 0;
            int craftingWorkshopLevel = 0;
            byte pendingUpgradeBuildingId = 0;
            long pendingUpgradeCompletesAtEpoch = 0;

            for (int i = 0; i < levels.Count; i++)
            {
                if (levels[i].BuildingId == ForgeBuildingId) forgeLevel = levels[i].CurrentLevel;
                else if (levels[i].BuildingId == InnBuildingId) innLevel = levels[i].CurrentLevel;
                else if (levels[i].BuildingId == BreedingGroundsBuildingId) breedingLevel = levels[i].CurrentLevel;
                else if (levels[i].BuildingId == MentorshipAcademyBuildingId) academyLevel = levels[i].CurrentLevel;
                else if (levels[i].BuildingId == LumberjackBuildingId) lumberjackLevel = levels[i].CurrentLevel;
                else if (levels[i].BuildingId == MineBuildingId) mineLevel = levels[i].CurrentLevel;
                else if (levels[i].BuildingId == WarehouseBuildingId) warehouseLevel = levels[i].CurrentLevel;
                else if (levels[i].BuildingId == TownHallBuildingId) townHallLevel = levels[i].CurrentLevel;
                else if (levels[i].BuildingId == CraftingWorkshopBuildingId) craftingWorkshopLevel = levels[i].CurrentLevel;

                if (levels[i].UpgradeTargetLevel > 0)
                {
                    pendingUpgradeBuildingId = (byte)levels[i].BuildingId;
                    pendingUpgradeCompletesAtEpoch = levels[i].UpgradeCompletesAtEpoch;
                }
            }

            // Modul: THE VILLAGE COUNTED A TABLE NOTHING EVER WROTE TO.
            //
            // VillageResidents has no INSERT anywhere in the codebase - not in
            // registration, not in breeding, not in the village engine. So
            // every player's population read 0/10 forever while the Character
            // screen listed the two humans they actually own, and every
            // achievement and score keyed on it was dead.
            //
            // The people who live in your village ARE your characters. That is
            // the table breeding writes, the roster reads and the tick
            // simulates, so it is the one that answers "who lives here".
            int population = await db.CharacterRecords
                .AsNoTracking()
                .CountAsync(c => c.PlayerId == playerId && !c.IsLockedInEscrow);

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
                MineLevel = ClampByte(mineLevel),
                WarehouseLevel = ClampByte(warehouseLevel),
                TownHallLevel = ClampByte(townHallLevel),
                CraftingWorkshopLevel = ClampByte(craftingWorkshopLevel),
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

            int activeResidents = await db.CharacterRecords
                .AsNoTracking()
                .CountAsync(c => c.PlayerId == playerId && !c.IsLockedInEscrow);

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
