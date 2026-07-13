using System;
using System.Collections.Generic;
using System.Data;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Engine
{
    public class AffixRerollEngine
    {
        private readonly IServiceProvider _serviceProvider;

        public AffixRerollEngine(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task ExecuteRerollAsync(long playerId, long targetItemGuid, int affixIndex)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var query = $"SELECT * FROM \"EquipmentInstances\" WHERE \"Id\" = {targetItemGuid} FOR UPDATE";
                var targetItem = await db.EquipmentInstances.FromSqlRaw(query).SingleOrDefaultAsync();

                if (targetItem == null || targetItem.PlayerId != playerId)
                {
                    Console.WriteLine("Reroll failed: Item not found or ownership mismatch.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(targetItem.AffixPayload))
                {
                    Console.WriteLine("Reroll failed: Item has no affixes.");
                    return;
                }

                if (targetItem.IsAffixLocked || targetItem.AffixPayload.Contains("\"is_affix_locked\":true", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Reroll failed: Item affixes are locked.");
                    return;
                }

                JsonObject affixPayload = JsonNode.Parse(targetItem.AffixPayload) as JsonObject ?? new JsonObject();
                var rerollableKeys = new List<string>(affixPayload.Count);
                foreach (var affix in affixPayload)
                {
                    if (affix.Key != "is_affix_locked" && affix.Value != null)
                    {
                        rerollableKeys.Add(affix.Key);
                    }
                }

                if (rerollableKeys.Count <= affixIndex || affixIndex < 0)
                {
                    Console.WriteLine("Reroll failed: Affix index out of bounds.");
                    return;
                }

                string affixKeyToReroll = rerollableKeys[affixIndex];

                long cost = (long)Math.Floor(5 * Math.Pow(1.35, targetItem.QualityTier - 1));

                var premiumCurrencyQuery = $"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = 'premium_diamond' FOR UPDATE";
                var premiumRecord = await db.CommodityRecords.FromSqlRaw(premiumCurrencyQuery).SingleOrDefaultAsync();

                if (premiumRecord == null || premiumRecord.Quantity < cost)
                {
                    Console.WriteLine("Reroll failed: Insufficient premium currency (premium_diamond).");
                    return;
                }

                premiumRecord.Quantity -= cost;

                affixPayload.Remove(affixKeyToReroll);
                string newAffixType = AffixEngine.GetRandomAffixKey();
                
                int regionTier = 1;
                if (int.TryParse(targetItem.BaseItemId, out int baseId) && baseId > 0)
                {
                    if (ContentRegistry.ItemDefinitions.Length > baseId - 1)
                    {
                        regionTier = ContentRegistry.ItemDefinitions[baseId - 1].RegionTier;
                    }
                }
                
                int targetValue = 0;
                if (newAffixType == "flat_hp") targetValue = AffixEngine.CalculateFlatHp(regionTier, targetItem.QualityTier);
                else if (newAffixType == "flat_armor") targetValue = AffixEngine.CalculateFlatArmor(regionTier, targetItem.QualityTier);
                else targetValue = AffixEngine.CalculatePercentagePool(5, 2, targetItem.QualityTier);

                string newAffixKey = $"{newAffixType}_{Guid.NewGuid().ToString().Substring(0, 4)}";
                
                affixPayload[newAffixKey] = targetValue;
                targetItem.AffixPayload = affixPayload.ToJsonString();

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                
                Console.WriteLine($"Reroll Success: {affixKeyToReroll} -> {newAffixKey}");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Reroll transaction aborted: {ex.Message}");
            }
        }
    }
}
