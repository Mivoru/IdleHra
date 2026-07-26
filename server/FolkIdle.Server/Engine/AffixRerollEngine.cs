using System;
using System.Collections.Generic;
using System.Data;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Models;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public class AffixRerollEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry? _playerRegistry;

        public AffixRerollEngine(IServiceProvider serviceProvider, PlayerSessionRegistry? playerRegistry = null)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        // Modul: Affix System Unification. BaseItemId is a slug, so an item's
        // region tier has to come from a reverse lookup over the catalogue -
        // the same GetItemBaseId identity space every other resolution in this
        // codebase uses. Linear over a bounded static table, and only on the
        // reroll command path, never on a tick.
        private static int ResolveRegionTier(string baseItemId)
        {
            if (string.IsNullOrEmpty(baseItemId)) return 1;

            ReadOnlySpan<ItemDefinition> items = ContentRegistry.ItemDefinitions;
            for (int i = 0; i < items.Length; i++)
            {
                if (string.Equals(ContentRegistry.GetItemBaseId(items[i].Id), baseItemId, StringComparison.Ordinal))
                {
                    return items[i].RegionTier > 0 ? items[i].RegionTier : 1;
                }
            }
            return 1;
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
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.TargetNotFound);
                    return;
                }

                if (string.IsNullOrWhiteSpace(targetItem.AffixPayload))
                {
                    Console.WriteLine("Reroll failed: Item has no affixes.");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.GenericValidationFailure);
                    return;
                }

                if (targetItem.IsAffixLocked || targetItem.AffixPayload.Contains("\"is_affix_locked\":true", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Reroll failed: Item affixes are locked.");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.GenericValidationFailure);
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
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.GenericValidationFailure);
                    return;
                }

                string affixKeyToReroll = rerollableKeys[affixIndex];

                // Modul: Affix System Unification. Same GDD Module 03 section
                // 5.3 formula as before, now sourced from AffixRegistry so client
                // and server quote one number - see that method's note on the
                // GDD's own illustrative prices disagreeing with its formula.
                long cost = AffixRegistry.CalculateRerollDiamondCost(targetItem.QualityTier);

                var premiumCurrencyQuery = $"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = 'premium_diamond' FOR UPDATE";
                var premiumRecord = await db.CommodityRecords.FromSqlRaw(premiumCurrencyQuery).SingleOrDefaultAsync();

                if (premiumRecord == null || premiumRecord.Quantity < cost)
                {
                    Console.WriteLine("Reroll failed: Insufficient premium currency (premium_diamond).");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.InsufficientMaterials);
                    return;
                }

                premiumRecord.Quantity -= cost;

                // Modul: Affix System Unification. Three separate bugs lived
                // in this block.
                //
                // 1. The replacement affix came from AffixEngine.GetRandomAffixKey,
                //    which picked uniformly from all twelve ids with no regard
                //    for slot legality - a shield-only block_chance_pct could
                //    land on a sword.
                // 2. regionTier was resolved by int.TryParse on BaseItemId,
                //    but BaseItemId is a slug like
                //    "eq_steel_claymore_melee_weapon_slot_base", so the parse
                //    ALWAYS failed and every rerolled affix was scaled as
                //    though it came from region 1.
                // 3. Every percentage affix was valued with one hardcoded
                //    base/growth pair (5, 2), ignoring the per-affix numbers
                //    the GDD actually specifies.
                //
                // On top of that the new key carried a random hex suffix
                // ("flat_hp_a3f2") that no reader recognised, so a reroll
                // silently deleted the old stat and added nothing readable in
                // its place - the player paid diamonds to make the item
                // strictly worse. Keys are now plain GDD affix ids.
                string replacedAffixId = AffixRegistry.StripStackSuffix(affixKeyToReroll);

                if (!AffixRegistry.TryRollReplacement(targetItem.BaseItemId, replacedAffixId, out var replacement))
                {
                    Console.WriteLine("Reroll failed: no affix is legal for this item's slot.");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.TargetNotFound);
                    await transaction.RollbackAsync();
                    return;
                }

                int regionTier = ResolveRegionTier(targetItem.BaseItemId);
                int targetValue = AffixRegistry.CalculateMagnitude(replacement, regionTier, targetItem.QualityTier);

                affixPayload.Remove(affixKeyToReroll);

                // Preserve the stack shape: if the item already carries this
                // affix, the replacement becomes a further stacked instance
                // rather than overwriting the existing one.
                string newAffixKey = replacement.Id;
                int stackIndex = 2;
                while (affixPayload.ContainsKey(newAffixKey))
                {
                    newAffixKey = replacement.Id + AffixRegistry.StackSeparator + stackIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    stackIndex++;
                }

                affixPayload[newAffixKey] = targetValue;
                targetItem.AffixPayload = affixPayload.ToJsonString();

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                
                Console.WriteLine($"Reroll Success: {affixKeyToReroll} -> {newAffixKey}");
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Reroll transaction aborted: {ex.Message}");
            }
        }
    }
}
