using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    public static class OfflineSimulationEngine
    {
        // Hard cap on analytically-projected offline time: Modul 11 formula
        // T_elapsed = Math.Min(43200, T_current - T_last_checkpoint).
        private const long MaxOfflineSeconds = 43200L;

        private readonly struct LootProjection
        {
            public readonly bool IsValid;
            public readonly int LootTableId;
            public readonly int LootRolls;

            public LootProjection(bool isValid, int lootTableId, int lootRolls)
            {
                IsValid = isValid;
                LootTableId = lootTableId;
                LootRolls = lootRolls;
            }
        }

        public static async Task<TickStatePayload> ExtrapolateOfflineProgressAsync(FolkIdleDbContext db, TickStatePayload payload, long currentUnixTimestamp)
        {
            if (payload.LastLogoutTimestamp == 0)
            {
                payload.LastLogoutTimestamp = currentUnixTimestamp;
                return payload;
            }

            long rawDeltaSeconds = currentUnixTimestamp - payload.LastLogoutTimestamp;
            if (rawDeltaSeconds <= 0)
            {
                return payload;
            }

            // Modul 13: Vodnik Mastery extends the universal offline cap.
            long effectiveMaxOfflineSeconds = RaceMasteryResolver.GetVodnikExtendedOfflineSeconds(payload.VodnikMasteryLevel, MaxOfflineSeconds);
            long elapsedSeconds = Math.Min(effectiveMaxOfflineSeconds, rawDeltaSeconds);

            await GrantVillagePassiveProductionAsync(db, payload.PlayerId, payload.LumberjackLevel, payload.QuarryLevel, payload.MineLevel, payload.WarehouseLevel, elapsedSeconds);

            if (ContentRegistry.TryGetGatheringNode(payload.ActiveActivityId, out GatheringNodeDefinition gatheringNode))
            {
                LootProjection projection = CalculateGatheringProjection(ref payload, gatheringNode, elapsedSeconds);
                int granted = await GrantProjectedLootAsync(db, payload.PlayerId, projection, payload.InventorySpaceRemaining);
                payload.InventorySpaceRemaining -= granted;
            }
            else if (payload.ActiveActivityId > 0)
            {
                LootProjection projection = CalculateCombatProjection(ref payload, elapsedSeconds);
                if (projection.IsValid)
                {
                    int granted = await GrantProjectedLootAsync(db, payload.PlayerId, projection, payload.InventorySpaceRemaining);
                    payload.InventorySpaceRemaining -= granted;
                }
                else
                {
                    BankOverflowSeconds(ref payload, elapsedSeconds);
                }
            }
            else
            {
                BankOverflowSeconds(ref payload, elapsedSeconds);
            }

            payload.LastLogoutTimestamp = currentUnixTimestamp;
            payload.IsDirty = true;
            return payload;
        }

        private static async Task<int> GrantProjectedLootAsync(FolkIdleDbContext db, long playerId, LootProjection projection, int availableInventorySpace)
        {
            // ReadOnlySpan<T> cannot be a parameter of an async method, so the span is
            // materialized into a plain array before the first await.
            LootTableEntry[] lootTable = ContentRegistry.GetLootTable(projection.LootTableId).ToArray();
            return await GrantAnalyticalLootAsync(db, playerId, lootTable, projection.LootRolls, availableInventorySpace);
        }

        // Modul 16: Village Infrastructure Passive Production & Warehouse Caps.
        // Grants offline wood/stone/iron_ore analytically, independent of
        // whatever gathering/combat activity was active while offline.
        private static async Task GrantVillagePassiveProductionAsync(FolkIdleDbContext db, long playerId, int lumberjackLevel, int quarryLevel, int mineLevel, int warehouseLevel, long elapsedSeconds)
        {
            if (elapsedSeconds <= 0)
            {
                return;
            }

            long maxStorage = VillageManagementEngine.CalculateWarehouseMaxStorage(warehouseLevel);
            if (maxStorage <= 0)
            {
                return;
            }

            float woodRate = lumberjackLevel * VillageManagementEngine.LumberjackWoodRatePerLevel;
            float stoneRate = quarryLevel * VillageManagementEngine.QuarryStoneRatePerLevel;
            float ironRate = mineLevel * VillageManagementEngine.MineIronRatePerLevel;

            if (woodRate <= 0f && stoneRate <= 0f && ironRate <= 0f)
            {
                return;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                await GrantSingleCommodityProductionAsync(db, playerId, VillageManagementEngine.WoodCommodityId, woodRate, elapsedSeconds, maxStorage);
                await GrantSingleCommodityProductionAsync(db, playerId, VillageManagementEngine.StoneCommodityId, stoneRate, elapsedSeconds, maxStorage);
                await GrantSingleCommodityProductionAsync(db, playerId, VillageManagementEngine.IronOreCommodityId, ironRate, elapsedSeconds, maxStorage);

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
            }
        }

        private static async Task GrantSingleCommodityProductionAsync(FolkIdleDbContext db, long playerId, string itemId, float productionRatePerSecond, long elapsedSeconds, long maxStorage)
        {
            if (productionRatePerSecond <= 0f)
            {
                return;
            }

            long potential = (long)(elapsedSeconds * productionRatePerSecond);
            if (potential <= 0)
            {
                return;
            }

            var commodity = await db.CommodityRecords
                .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1} FOR UPDATE", playerId, itemId)
                .SingleOrDefaultAsync();

            long currentStorage = commodity?.Quantity ?? 0L;
            long grantedAmount = Math.Min(potential, Math.Max(0L, maxStorage - currentStorage));
            if (grantedAmount <= 0)
            {
                return;
            }

            if (commodity == null)
            {
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = itemId, Quantity = grantedAmount });
            }
            else
            {
                commodity.Quantity += grantedAmount;
            }
        }

        private static LootProjection CalculateGatheringProjection(ref TickStatePayload payload, GatheringNodeDefinition node, long elapsedSeconds)
        {
            int masteryLevel = node.ProfessionType == 0 ? payload.WoodcuttingMasteryLevel : payload.MiningMasteryLevel;
            int requiredTicks = node.BaseTickThreshold - (masteryLevel * 2) - payload.CachedCurrentToolTier;
            if (requiredTicks < 2) requiredTicks = 2;

            double actionIntervalSeconds = requiredTicks / 10.0;
            double totalActionsDouble = elapsedSeconds / actionIntervalSeconds;

            long allowedActions = (long)Math.Min(totalActionsDouble, payload.InventorySpaceRemaining);
            double usedSeconds = allowedActions * actionIntervalSeconds;
            double overflowSeconds = elapsedSeconds - usedSeconds;
            BankOverflowSeconds(ref payload, (long)overflowSeconds);

            long masteryXpGained = allowedActions * node.BaseMasteryXpReward;
            ApplyGatheringMasteryXp(ref payload, node.ProfessionType, masteryXpGained);

            int lootRolls = (int)(allowedActions * payload.CachedCodexYieldMultiplier);
            return new LootProjection(true, node.ActivityId, lootRolls);
        }

        private static LootProjection CalculateCombatProjection(ref TickStatePayload payload, long elapsedSeconds)
        {
            int fallbackId = payload.ActiveActivityId > ContentRegistry.Monsters.Length ? 1 : (int)payload.ActiveActivityId;
            if (fallbackId <= 0 || fallbackId > ContentRegistry.Monsters.Length)
            {
                return new LootProjection(false, 0, 0);
            }

            MonsterDefinition activeMonster = ContentRegistry.Monsters[fallbackId - 1];

            int lineageId = payload.SelectedLineageId;
            if (lineageId < 0 || lineageId >= ProgressionEngine.Lineages.Length) lineageId = 0;
            LineageDefinition lineage = ProgressionEngine.Lineages[lineageId];

            int activeAgePhase = 1;
            int activeRaceId = 0;
            if (payload.Slot1_CharacterId != Guid.Empty)
            {
                activeAgePhase = payload.Slot1_AgePhase;
                activeRaceId = (int)(payload.Slot1_GeneticVector & 0xFF);
            }

            CombatStats combatStats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, activeAgePhase, payload.CompletedAreaFlags, activeRaceId, payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, payload.CachedEquippedFlatAttack, payload.CachedEquippedFlatDefense, payload.CachedEquippedCritBonus, payload.CachedEquippedLuckBonus, payload.IsEpicMutation, payload.LocusSpeed, payload.LocusCrit);

            int playerAttackSpeedMs = (int)(1500 * (1.0f - combatStats.AttackSpeedPct));
            if (playerAttackSpeedMs < 200) playerAttackSpeedMs = 200;

            // Analytical projection intentionally uses expected (average) damage per
            // hit rather than replaying per-swing hit/crit RNG.
            long baseMilliAttack = 15000L;
            long effectiveMilliAttack = baseMilliAttack + (baseMilliAttack * lineage.DamageScalePerLevelPct * payload.CurrentLevel / 100) + (combatStats.FlatMeleeDamage * 1000L);
            int netDamage = Math.Max(1000, (int)effectiveMilliAttack);
            netDamage = (int)(netDamage * payload.CachedCodexDamageMultiplier);

            double damagePerHit = netDamage / 1000.0;
            double attacksPerSecond = 1000.0 / playerAttackSpeedMs;
            double dps = damagePerHit * attacksPerSecond;

            if (dps <= 0.0 || activeMonster.MaxHp <= 0)
            {
                return new LootProjection(false, 0, 0);
            }

            double secondsPerKill = activeMonster.MaxHp / dps;
            double totalKillsDouble = elapsedSeconds / secondsPerKill;
            long totalKills = (long)totalKillsDouble;

            long xpGained = totalKills * activeMonster.BaseXpReward;
            ApplyCombatXp(ref payload, xpGained);

            int lootRolls = (int)(totalKillsDouble * payload.CachedCodexYieldMultiplier);
            return new LootProjection(true, activeMonster.LootTableId, lootRolls);
        }

        private static void ApplyCombatXp(ref TickStatePayload payload, long xpGained)
        {
            if (xpGained <= 0) return;

            payload.CurrentXp += xpGained;
            while (true)
            {
                long requiredXp = 100L * payload.CurrentLevel * payload.CurrentLevel;
                if (payload.CurrentXp >= requiredXp)
                {
                    payload.CurrentXp -= requiredXp;
                    payload.CurrentLevel++;
                }
                else
                {
                    break;
                }
            }
        }

        private static void ApplyGatheringMasteryXp(ref TickStatePayload payload, int professionType, long xpGained)
        {
            if (xpGained <= 0) return;

            int gainedXp = (int)Math.Min(xpGained, int.MaxValue);

            if (professionType == 0)
            {
                payload.WoodcuttingMasteryXp += gainedXp;
                while (true)
                {
                    int requiredMasteryXp = 50 * (payload.WoodcuttingMasteryLevel + 1) * (payload.WoodcuttingMasteryLevel + 1);
                    if (payload.WoodcuttingMasteryXp >= requiredMasteryXp)
                    {
                        payload.WoodcuttingMasteryXp -= requiredMasteryXp;
                        payload.WoodcuttingMasteryLevel++;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            else
            {
                payload.MiningMasteryXp += gainedXp;
                while (true)
                {
                    int requiredMasteryXp = 50 * (payload.MiningMasteryLevel + 1) * (payload.MiningMasteryLevel + 1);
                    if (payload.MiningMasteryXp >= requiredMasteryXp)
                    {
                        payload.MiningMasteryXp -= requiredMasteryXp;
                        payload.MiningMasteryLevel++;
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        private static void BankOverflowSeconds(ref TickStatePayload payload, long seconds)
        {
            if (seconds <= 0) return;

            double refund = Math.Min(seconds, 604800.0 - payload.BankedChronoSeconds);
            if (refund > 0)
            {
                payload.BankedChronoSeconds += refund;
            }
        }

        // Isolated so it can be tested directly against a hand-built loot table,
        // since ContentRegistry's real loot tables currently carry no entries.
        internal static async Task<int> GrantAnalyticalLootAsync(FolkIdleDbContext db, long playerId, LootTableEntry[] lootTable, int rollCount, int availableInventorySpace)
        {
            if (lootTable.Length == 0 || rollCount <= 0 || availableInventorySpace <= 0)
            {
                return 0;
            }

            int totalWeight = 0;
            for (int i = 0; i < lootTable.Length; i++)
            {
                totalWeight += lootTable[i].Weight;
            }

            if (totalWeight <= 0)
            {
                return 0;
            }

            int rollsToExecute = Math.Min(rollCount, availableInventorySpace);

            var grantedQuantities = new Dictionary<int, long>();
            for (int r = 0; r < rollsToExecute; r++)
            {
                int roll = Random.Shared.Next(totalWeight);
                int currentWeight = 0;
                for (int i = 0; i < lootTable.Length; i++)
                {
                    currentWeight += lootTable[i].Weight;
                    if (roll < currentWeight)
                    {
                        grantedQuantities.TryGetValue(lootTable[i].ItemId, out long existing);
                        grantedQuantities[lootTable[i].ItemId] = existing + 1;
                        break;
                    }
                }
            }

            foreach (KeyValuePair<int, long> kvp in grantedQuantities)
            {
                string materialName = ContentRegistry.GetMaterialString(kvp.Key);
                if (materialName == "unknown")
                {
                    continue;
                }

                var commodity = await db.CommodityRecords
                    .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1} FOR UPDATE", playerId, materialName)
                    .SingleOrDefaultAsync();

                if (commodity == null)
                {
                    db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = materialName, Quantity = kvp.Value });
                }
                else
                {
                    commodity.Quantity += kvp.Value;
                }
            }

            await db.SaveChangesAsync();

            return rollsToExecute;
        }
    }
}
