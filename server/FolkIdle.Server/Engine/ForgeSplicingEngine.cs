using System;
using System.Data;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Engine
{
    public enum ForgeSplicingResult
    {
        Success = 0,
        FailedSacrificesDestroyed = 1,
        FailedAffixLocked = 2,
        CriticalFailure = 3,
        InvalidRequest = 4,
        InsufficientGold = 5
    }

    public class ForgeSplicingEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry? _playerRegistry;
        private const long BaseGoldCost = 1000;

        public ForgeSplicingEngine(IServiceProvider serviceProvider, PlayerSessionRegistry? playerRegistry = null)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public async Task<ForgeSplicingResult> ExecuteFusionAsync(long playerId, long targetItemGuid, long sacrificialItem1Guid, long sacrificialItem2Guid)
        {
            if (targetItemGuid == sacrificialItem1Guid || targetItemGuid == sacrificialItem2Guid || sacrificialItem1Guid == sacrificialItem2Guid)
            {
                Console.WriteLine("Fusion failed: Identical items selected.");
                return ForgeSplicingResult.InvalidRequest;
            }

            long id0 = targetItemGuid, id1 = sacrificialItem1Guid, id2 = sacrificialItem2Guid;
            if (id0 > id1) { long tmp = id0; id0 = id1; id1 = tmp; }
            if (id1 > id2) { long tmp = id1; id1 = id2; id2 = tmp; }
            if (id0 > id1) { long tmp = id0; id0 = id1; id1 = tmp; }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            // Open transaction with Strict Serializable isolation
            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                // Explicit FOR UPDATE row-level pessimistic lock
                var query = $"SELECT * FROM \"MarketEquipmentInstances\" WHERE \"Id\" IN ({id0}, {id1}, {id2}) FOR UPDATE";
                var lockedItems = await db.MarketEquipmentInstances
                    .FromSqlRaw(query)
                    .ToListAsync();

                int forgeLevel = await db.VillageInfrastructures
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId && v.BuildingId == VillageManagementEngine.ForgeBuildingId)
                    .Select(v => (int?)v.CurrentLevel)
                    .SingleOrDefaultAsync() ?? 0;

                var validationPayload = new TickStatePayload
                {
                    PlayerId = playerId,
                    ForgeLevel = ClampByte(forgeLevel)
                };
                if (!ClientCommandValidator.ValidateForgeSplicingRequest(ref validationPayload, targetItemGuid, sacrificialItem1Guid, sacrificialItem2Guid, lockedItems))
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine("Fusion failed: Integrity gate rejected request.");
                    return ForgeSplicingResult.InvalidRequest;
                }

                MarketEquipmentInstance? targetItem = null;
                MarketEquipmentInstance? sac1 = null;
                MarketEquipmentInstance? sac2 = null;
                for (int i = 0; i < lockedItems.Count; i++)
                {
                    if (lockedItems[i].Id == targetItemGuid) targetItem = lockedItems[i];
                    else if (lockedItems[i].Id == sacrificialItem1Guid) sac1 = lockedItems[i];
                    else if (lockedItems[i].Id == sacrificialItem2Guid) sac2 = lockedItems[i];
                }

                if (targetItem == null || sac1 == null || sac2 == null)
                {
                    await transaction.RollbackAsync();
                    return ForgeSplicingResult.InvalidRequest;
                }

                if (targetItem.BaseItemId != sac1.BaseItemId || targetItem.BaseItemId != sac2.BaseItemId)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine("Fusion failed: Items must have identical Base Item IDs.");
                    return ForgeSplicingResult.InvalidRequest;
                }

                int currentTier = targetItem.QualityTier;
                
                long baseGoldCost = BaseGoldCost * (currentTier + 1);
                long cost = (long)(baseGoldCost * (1.0 + ((4 - sac1.QualityTier) * 0.50) + ((4 - sac2.QualityTier) * 0.50)));

                // Lock and fetch gold record
                var goldRecord = await db.CommodityRecords
                    .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                    .SingleOrDefaultAsync();

                if (goldRecord == null || goldRecord.Quantity < cost)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine("Fusion failed: Insufficient gold.");
                    return ForgeSplicingResult.InsufficientGold;
                }

                // Deduct cost
                goldRecord.Quantity -= cost;

                double baseProbability = Math.Max(0.05, 0.85 * Math.Pow(1.6, -(currentTier - 1)));
                if (SimulationEngine.ActiveGlobalEventId == 4) // DiamondStar
                {
                    baseProbability += 0.05;
                }
                double roll = Random.Shared.NextDouble();

                if (roll <= baseProbability)
                {
                    // SUCCESS
                    db.MarketEquipmentInstances.Remove(sac1);
                    db.MarketEquipmentInstances.Remove(sac2);

                    targetItem.QualityTier = currentTier + 1;

                    // Append/roll new affix modifier
                    JsonObject affixPayload = ParseAffixPayload(targetItem.AffixPayload);
                    string newAffixType = AffixEngine.GetRandomAffixKey();
                    int targetValue = 0;
                    int regionTier = 1;
                    if (int.TryParse(targetItem.BaseItemId, out int baseId) && baseId > 0)
                    {
                        if (ContentRegistry.ItemDefinitions.Length > baseId - 1)
                        {
                            regionTier = ContentRegistry.ItemDefinitions[baseId - 1].RegionTier;
                        }
                    }

                    if (newAffixType == "flat_hp") targetValue = AffixEngine.CalculateFlatHp(regionTier, currentTier + 1);
                    else if (newAffixType == "flat_armor") targetValue = AffixEngine.CalculateFlatArmor(regionTier, currentTier + 1);
                    else targetValue = AffixEngine.CalculatePercentagePool(5, 2, currentTier + 1);

                    string newAffixKey = $"{newAffixType}_{Guid.NewGuid().ToString().Substring(0, 4)}";
                    affixPayload[newAffixKey] = targetValue;
                    
                    targetItem.AffixPayload = affixPayload.ToJsonString();

                    Console.WriteLine($"Fusion Success! Target item {targetItem.Id} upgraded to Tier {targetItem.QualityTier}.");
                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();

                    _playerRegistry?.ForgeUpgradeQueue.Enqueue(new ForgeUpgradeNotification
                    {
                        PlayerId = playerId,
                        ResultingQualityTier = targetItem.QualityTier
                    });

                    return ForgeSplicingResult.Success;
                }
                else
                {
                    // FAILURE
                    if (currentTier == 2)
                    {
                        // Tier 2: Lock random affix slot
                        db.MarketEquipmentInstances.Remove(sac1);
                        db.MarketEquipmentInstances.Remove(sac2);

                        JsonObject affixPayload = ParseAffixPayload(targetItem.AffixPayload);
                        affixPayload["is_affix_locked"] = true;
                        targetItem.AffixPayload = affixPayload.ToJsonString();
                        targetItem.IsAffixLocked = true;

                        Console.WriteLine($"Fusion Failed! Target item {targetItem.Id} received affix lockout. Sacrifices lost.");
                        await db.SaveChangesAsync();
                        await transaction.CommitAsync();
                        return ForgeSplicingResult.FailedAffixLocked;
                    }
                    else if (currentTier >= 3)
                    {
                        // Tier 3+: Full vaporization
                        db.MarketEquipmentInstances.Remove(targetItem);
                        db.MarketEquipmentInstances.Remove(sac1);
                        db.MarketEquipmentInstances.Remove(sac2);
                        Console.WriteLine($"Fusion Critical Failure! All items destroyed.");
                        await db.SaveChangesAsync();
                        await transaction.CommitAsync();
                        return ForgeSplicingResult.CriticalFailure;
                    }
                    else
                    {
                        // Tier 1 failure: just lose sacrifices
                        db.MarketEquipmentInstances.Remove(sac1);
                        db.MarketEquipmentInstances.Remove(sac2);
                        Console.WriteLine($"Fusion Failed! Sacrifices lost.");
                        await db.SaveChangesAsync();
                        await transaction.CommitAsync();
                        return ForgeSplicingResult.FailedSacrificesDestroyed;
                    }
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Fusion transaction aborted: {ex.Message}");
                return ForgeSplicingResult.InvalidRequest;
            }
        }

        private static JsonObject ParseAffixPayload(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return new JsonObject();
            }

            try
            {
                return JsonNode.Parse(payload) as JsonObject ?? new JsonObject();
            }
            catch
            {
                return new JsonObject();
            }
        }

        private static byte ClampByte(int value)
        {
            if (value <= 0) return 0;
            if (value >= byte.MaxValue) return byte.MaxValue;
            return (byte)value;
        }
    }
}
