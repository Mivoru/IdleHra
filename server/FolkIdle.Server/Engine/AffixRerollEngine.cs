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
    public enum RerollOperation
    {
        // Same stat, same rarity, new magnitude inside the rarity's band.
        Value = 0,
        // New stat, rarity preserved. Costs 2.5x - it can convert a dead
        // affix into the one a build actually wants.
        StatType = 1,
        // One rarity step up. The only operation priced in Diamonds.
        UpgradeRarity = 2
    }

    public class AffixRerollEngine
    {
        // Result of the most recent successful reroll, so the pass-2
        // announcement layer can broadcast an Epic or Legendary outcome
        // without re-reading the row.
        public AffixRarity LastRerollResultRarity { get; private set; }
        public string LastRerollResultAffixId { get; private set; } = string.Empty;

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

        // Modul: reroll rework, 2026-08-01. Three distinct operations, two
        // currencies. See AffixRegistry for the cost curves and why value/stat
        // rerolls are gold while a rarity upgrade is Diamonds.
        //
        // consecutiveAttempts drives the escalating gold price. The caller owns
        // that counter because "consecutive" means "on this item without an
        // accepted result", which only the session knows - the database row has
        // no memory of a player rerolling, walking away, and coming back.
        // Modul: auto-reroll, 2026-08-01.
        //
        // Loops ExecuteRerollAsync until the stop condition is met, the attempt
        // limit is hit, or the player runs out of currency. Every guard that
        // can be decided without touching the database lives in
        // AutoRerollPlanner so it is unit-testable without a Postgres fixture.
        //
        // Reachability is checked BEFORE the first attempt rather than being
        // discovered by burning gold: asking a sword to roll block_chance_pct
        // (shield-only) or asking a Value reroll to raise rarity are both
        // conditions that can never be met, and the naive loop would happily
        // spend the entire budget finding that out.
        public async Task<AutoRerollStopReason> ExecuteAutoRerollAsync(
            long playerId,
            long targetItemGuid,
            int affixIndex,
            RerollOperation operation,
            AutoRerollStopCondition stopCondition,
            int maxAttempts)
        {
            if (stopCondition.IsTriviallySatisfied)
            {
                // Would accept the very first roll, so the player would pay for
                // a reroll whose outcome was guaranteed acceptable anyway.
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.GenericValidationFailure);
                return AutoRerollStopReason.RejectedTrivialCondition;
            }

            maxAttempts = AutoRerollPlanner.ClampAttempts(maxAttempts);

            (string currentAffixId, AffixRarity currentRarity, string baseItemId, bool found) = await ReadAffixStateAsync(playerId, targetItemGuid, affixIndex);
            if (!found)
            {
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.TargetNotFound);
                return AutoRerollStopReason.RejectedUnreachableCondition;
            }

            if (!AutoRerollPlanner.IsConditionReachable(stopCondition, baseItemId, operation, currentAffixId)
                || !AutoRerollPlanner.IsRarityTargetReachable(stopCondition, operation, currentRarity))
            {
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.GenericValidationFailure);
                return AutoRerollStopReason.RejectedUnreachableCondition;
            }

            // Already good enough before spending anything.
            if (AutoRerollPlanner.IsSatisfied(stopCondition, currentRarity, currentAffixId))
            {
                return AutoRerollStopReason.ConditionMet;
            }

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                LastRerollResultAffixId = string.Empty;

                await ExecuteRerollAsync(playerId, targetItemGuid, affixIndex, operation, attempt);

                if (string.IsNullOrEmpty(LastRerollResultAffixId))
                {
                    // The attempt did not commit - insufficient currency, a
                    // locked item, or a validation rejection. Stop rather than
                    // hammering the same failing transaction to the limit.
                    return AutoRerollStopReason.BudgetExhausted;
                }

                if (AutoRerollPlanner.IsSatisfied(stopCondition, LastRerollResultRarity, LastRerollResultAffixId))
                {
                    return AutoRerollStopReason.ConditionMet;
                }
            }

            return AutoRerollStopReason.AttemptLimitReached;
        }

        // Reads the current stat and rarity of one affix without mutating it,
        // so the reachability guards above can run before any spend.
        private async Task<(string AffixId, AffixRarity Rarity, string BaseItemId, bool Found)> ReadAffixStateAsync(long playerId, long targetItemGuid, int affixIndex)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            var item = await db.EquipmentInstances
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == targetItemGuid && e.PlayerId == playerId);

            if (item == null || string.IsNullOrWhiteSpace(item.AffixPayload))
            {
                return (string.Empty, AffixRarity.Common, string.Empty, false);
            }

            if (JsonNode.Parse(item.AffixPayload) is not JsonObject payload)
            {
                return (string.Empty, AffixRarity.Common, string.Empty, false);
            }

            int index = 0;
            foreach (var entry in payload)
            {
                if (entry.Key == "is_affix_locked" || entry.Value == null) continue;

                if (index == affixIndex)
                {
                    return (AffixRegistry.StripStackSuffix(entry.Key), AffixRegistry.ParseRarity(entry.Key), item.BaseItemId, true);
                }
                index++;
            }

            return (string.Empty, AffixRarity.Common, string.Empty, false);
        }

        public async Task ExecuteRerollAsync(long playerId, long targetItemGuid, int affixIndex, RerollOperation operation = RerollOperation.Value, int consecutiveAttempts = 0)
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

                string affixIdToReroll = AffixRegistry.StripStackSuffix(affixKeyToReroll);
                AffixRarity currentRarity = AffixRegistry.ParseRarity(affixKeyToReroll);

                if (!AffixRegistry.TryGetDefinition(affixIdToReroll, out var currentDefinition))
                {
                    Console.WriteLine("Reroll failed: affix id is not in the registry.");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.GenericValidationFailure);
                    await transaction.RollbackAsync();
                    return;
                }

                // A Legendary affix has nowhere to go. Rejected rather than
                // charged for a no-op - the engine this replaced had a bug of
                // exactly that shape, where the player paid to make an item worse.
                if (operation == RerollOperation.UpgradeRarity && currentRarity >= AffixRarity.Legendary)
                {
                    Console.WriteLine("Reroll failed: affix is already Legendary.");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.GenericValidationFailure);
                    await transaction.RollbackAsync();
                    return;
                }

                // An item whose BaseItemId carries no recognisable slot suffix
                // has no legal affix pool. Refuse before charging anything.
                //
                // Restored after the three-operation rework briefly lost it:
                // only the StatType branch calls TryRollReplacement, so a Value
                // or UpgradeRarity reroll on a malformed slug would have skipped
                // the check entirely and happily charged for it. The guard
                // belongs ahead of the payment, not inside one branch.
                // Heap array rather than stackalloc: this method is async, and
                // C# does not permit stackalloc there. A cold path that runs
                // once per reroll request, so the allocation is irrelevant -
                // unlike the 10 Hz tick, which is where the stackalloc
                // convention in AffixRegistry actually matters.
                int[] legalProbe = new int[16];
                if (AffixRegistry.GetLegalAffixIndices(AffixRegistry.ResolveSlot(targetItem.BaseItemId), legalProbe) == 0)
                {
                    Console.WriteLine("Reroll failed: item slot is unrecognisable, so it has no legal affix pool.");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.TargetNotFound);
                    await transaction.RollbackAsync();
                    return;
                }

                int regionTier = ResolveRegionTier(targetItem.BaseItemId);

                bool payWithDiamonds = operation == RerollOperation.UpgradeRarity;
                long cost = payWithDiamonds
                    ? AffixRegistry.CalculateRarityUpgradeDiamondCost(currentRarity)
                    : AffixRegistry.CalculateRerollGoldCost(
                        targetItem.QualityTier,
                        consecutiveAttempts,
                        operation == RerollOperation.StatType);

                string currencyItemId = payWithDiamonds ? "premium_diamond" : "gold";

                var currencyQuery = $"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = '{currencyItemId}' FOR UPDATE";
                var currencyRecord = await db.CommodityRecords.FromSqlRaw(currencyQuery).SingleOrDefaultAsync();

                if (currencyRecord == null || currencyRecord.Quantity < cost)
                {
                    Console.WriteLine($"Reroll failed: insufficient {currencyItemId} (need {cost}).");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.InsufficientMaterials);
                    await transaction.RollbackAsync();
                    return;
                }

                currencyRecord.Quantity -= cost;

                AffixDefinition resultDefinition = currentDefinition;
                AffixRarity resultRarity = currentRarity;

                switch (operation)
                {
                    case RerollOperation.UpgradeRarity:
                        AffixRegistry.TryGetNextRarity(currentRarity, out resultRarity);
                        break;

                    case RerollOperation.StatType:
                        // Stat type changes; RARITY IS PRESERVED. Rerolling the
                        // type is already the more expensive operation, and
                        // dropping it back to Common would make it a downgrade
                        // the player paid extra for.
                        if (!AffixRegistry.TryRollReplacement(targetItem.BaseItemId, affixIdToReroll, out resultDefinition))
                        {
                            Console.WriteLine("Reroll failed: no affix is legal for this item's slot.");
                            _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.TargetNotFound);
                            await transaction.RollbackAsync();
                            return;
                        }
                        break;

                    case RerollOperation.Value:
                    default:
                        // Same stat, same rarity - only the magnitude moves,
                        // inside AffixRegistry's +/-20% band.
                        break;
                }

                int resultMagnitude = AffixRegistry.RollMagnitude(resultDefinition, regionTier, resultRarity);

                affixPayload.Remove(affixKeyToReroll);

                // Preserve the stack shape: if the item already carries this
                // affix, the result becomes a further stacked instance rather
                // than overwriting the existing one.
                int stackIndex = 1;
                string newAffixKey = AffixRegistry.BuildPayloadKey(resultDefinition.Id, stackIndex, resultRarity);
                while (affixPayload.ContainsKey(newAffixKey))
                {
                    stackIndex++;
                    newAffixKey = AffixRegistry.BuildPayloadKey(resultDefinition.Id, stackIndex, resultRarity);
                }

                affixPayload[newAffixKey] = resultMagnitude;
                targetItem.AffixPayload = affixPayload.ToJsonString();

                LastRerollResultRarity = resultRarity;
                LastRerollResultAffixId = resultDefinition.Id;

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                
                Console.WriteLine($"Reroll success: {affixKeyToReroll} -> {newAffixKey} ({resultRarity})");
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
