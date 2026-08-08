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
    /// <summary>
    /// THERE IS ONE REROLL NOW, AND IT COSTS GOLD.
    ///
    /// There used to be three - Value, StatType and UpgradeRarity - and they
    /// split one decision across three purchases with two currencies: two were
    /// gold, the rarity step was Diamonds, and the player had to understand
    /// which axis they wanted before they could ask for anything. Rerolling the
    /// stat also deliberately PRESERVED the rarity, and rerolling the value
    /// preserved both, so improving an affix in the way you actually wanted
    /// usually meant paying for two or three of them in sequence.
    ///
    /// One operation replaces all of it: pick an affix, pay gold, and its type,
    /// its rarity and its magnitude are all rolled fresh together. It is a
    /// gamble rather than a purchase of a specific improvement, which is what
    /// makes a single price honest - and the other affixes on the item are
    /// untouched, so the choice of WHICH affix to reroll is the decision the
    /// player is actually making.
    ///
    /// The enum survives with one member because the wire carries it: a client
    /// that still sends 1 or 2 gets the same reroll rather than an error.
    /// </summary>
    public enum RerollOperation
    {
        /// New stat, new rarity, new magnitude, all at once. Gold.
        Full = 0
    }

    public class AffixRerollEngine
    {
        // Result of the most recent successful reroll, so the pass-2
        // announcement layer can broadcast an Epic or Legendary outcome
        // without re-reading the row.
        public AffixRarity LastRerollResultRarity { get; private set; }
        public string LastRerollResultAffixId { get; private set; } = string.Empty;

        // Held between the mutation and the commit so a rolled-back reroll is
        // never announced. Not thread-shared: one engine instance handles one
        // request at a time.
        private string? _pendingAnnouncement;

        // Staged like _pendingAnnouncement and for the same reason: the payload
        // must not be told about a balance the transaction then rolls back.
        // -1 means "no diamond spend in this attempt".
        private int _pendingDiamondBalance = -1;

        // "Player 4711 rerolled Critical Damage to LEGENDARY (+18.5%)".
        // Deliberately carries no player NAME - PlayerRecord has no display
        // name column, and inventing one here would put a second, wrong answer
        // next to whatever the social layer eventually uses. The client
        // resolves the id against its own roster cache.
        // Modul: WORDS, not a pipe-delimited payload.
        //
        // This produced "123|4|flat_hp|56" on the assumption that the client
        // would parse and render it. The client does not - Chat.svelte prints
        // an announcement's text verbatim - so every high-rarity reroll in the
        // game has been announced to the world as a row of numbers and pipes.
        private static string FormatRarityAnnouncement(long playerId, AffixRarity rarity, string affixId, int magnitude)
        {
            string affixName = affixId.Replace('_', ' ');
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{PlayerNameResolver.GetCachedOrFallback(playerId)} rerolled a {rarity} {affixName} (+{magnitude}). Congratulations!");
        }

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

        public async Task ExecuteRerollAsync(long playerId, long targetItemGuid, int affixIndex, RerollOperation operation = RerollOperation.Full, int consecutiveAttempts = 0)
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

                // Modul: a Legendary affix used to be REFUSED here, because the
                // only operation that touched rarity could climb and a Legendary
                // one had nowhere to climb to. There is nothing to refuse now:
                // the reroll rolls a fresh rarity from the whole table, so a
                // Legendary affix can be rerolled like any other - it is simply
                // a bet the player is very likely to lose, which is their call
                // to make and is visible in the price before they make it.

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

                // Modul: GOLD ONLY. The rarity step used to be priced in
                // Diamonds, which made the one operation a player most wanted
                // the one they had to buy currency for - and it was broken
                // besides: it spent from a "premium_diamond" CommodityRecords
                // row that nothing in the server has ever created, so every
                // diamond-priced reroll was rejected as unaffordable however
                // many diamonds the player held. The integration tests seeded
                // that row explicitly, which made a store the game never
                // populates look real.
                //
                // Deleting the currency split deletes the bug with it. Diamonds
                // are not spent anywhere on this path now.
                //
                // No stat-type surcharge either: there is one operation, so
                // there is nothing for a multiplier to distinguish.
                long cost = AffixRegistry.CalculateRerollGoldCost(
                    targetItem.QualityTier,
                    consecutiveAttempts,
                    rerollStatType: false);

                var currencyQuery = $"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = 'gold' FOR UPDATE";
                var currencyRecord = await db.CommodityRecords.FromSqlRaw(currencyQuery).SingleOrDefaultAsync();

                if (currencyRecord == null || currencyRecord.Quantity < cost)
                {
                    Console.WriteLine($"Reroll failed: insufficient gold (need {cost}).");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.InsufficientMaterials);
                    await transaction.RollbackAsync();
                    return;
                }

                currencyRecord.Quantity -= cost;

                // Modul: the Book of Deeds, chapter II. Counted where the gold
                // is spent, inside the same transaction, so the number matches
                // what the player actually paid for.
                var rerollingPlayer = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
                if (rerollingPlayer != null) rerollingPlayer.AffixRerollsPerformed++;

                // Modul: ONE ROLL, ALL THREE AXES. Type, rarity and magnitude
                // together - see RerollOperation on why the three separate
                // operations went. `operation` is accepted and ignored so an
                // older client sending StatType or UpgradeRarity gets the same
                // reroll rather than an error.
                if (!AffixRegistry.TryRollReplacement(targetItem.BaseItemId, affixIdToReroll, out AffixDefinition resultDefinition))
                {
                    // No OTHER affix is legal for this slot, so the pool is a
                    // single entry and the type cannot move. Roll that one
                    // again rather than failing: the rarity and the magnitude
                    // are still live, so the reroll is still worth what it cost.
                    resultDefinition = currentDefinition;
                }

                // The same weighted table the drop path rolls, so a reroll and
                // a fresh drop agree about what a rarity is worth. It can come
                // out LOWER than it went in - that is the gamble, and it is why
                // one price is honest.
                AffixRarity resultRarity = AffixRegistry.RollAffixRarity();

                int resultMagnitude = AffixRegistry.RollMagnitude(resultDefinition, regionTier, resultRarity);

                // Preserve the stack shape: if the item already carries this
                // affix, the result becomes a further stacked instance rather
                // than overwriting the existing one.
                int stackIndex = 1;
                string newAffixKey = AffixRegistry.BuildPayloadKey(resultDefinition.Id, stackIndex, resultRarity);
                while (affixPayload.ContainsKey(newAffixKey) && newAffixKey != affixKeyToReroll)
                {
                    stackIndex++;
                    newAffixKey = AffixRegistry.BuildPayloadKey(resultDefinition.Id, stackIndex, resultRarity);
                }

                // Modul: THE REROLLED AFFIX KEEPS ITS PLACE IN THE LIST.
                //
                // This was Remove-then-assign, and assigning a new key to a
                // JsonObject APPENDS it. So the affix a player had selected
                // moved to the end of the item and every affix after it shifted
                // up one - and since the reroll command addresses an affix by
                // INDEX, the selection silently landed on a different one.
                //
                // Reported exactly: "I reroll and get Rare lifesteal, then it
                // jumps to Epic attack speed out of nowhere and I am rerolling
                // that instead." Nothing jumped; the list moved under a
                // positional cursor.
                //
                // Rebuilt in order with the new key substituted at the old
                // key's position. A dictionary with no order is not a list, and
                // this payload has been addressed as a list since the reroll
                // shipped.
                var rebuilt = new JsonObject();
                foreach (var existing in affixPayload)
                {
                    if (existing.Key == affixKeyToReroll)
                    {
                        rebuilt[newAffixKey] = resultMagnitude;
                    }
                    else
                    {
                        rebuilt[existing.Key] = existing.Value?.DeepClone();
                    }
                }
                affixPayload = rebuilt;

                targetItem.AffixPayload = affixPayload.ToJsonString();

                LastRerollResultRarity = resultRarity;
                LastRerollResultAffixId = resultDefinition.Id;

                // Modul: high-rarity announcements. Epic and above only - the
                // same threshold UiRarityPalette uses to decide what glows, so
                // "it glowed" and "it was announced" never disagree.
                //
                // Enqueued AFTER the mutation but BEFORE the commit is
                // deliberate: the queue is drained by a different thread, and
                // announcing a reroll that then rolled back would put a lie in
                // global chat that nothing could retract. The commit follows
                // immediately below, and a failure there throws past this point
                // without the dispatch worker having anything to send yet.
                if (resultRarity >= AffixRarity.Epic)
                {
                    _pendingAnnouncement = FormatRarityAnnouncement(playerId, resultRarity, resultDefinition.Id, resultMagnitude);
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                // Announced only once the transaction is durable. The queue is
                // drained by the chat dispatch worker on another thread, so
                // announcing before the commit could put a claim in global chat
                // that a rollback then silently contradicts - and nothing can
                // retract a chat line.
                if (!string.IsNullOrEmpty(_pendingAnnouncement))
                {
                    Domain.Social.ChatEngine.EnqueueSystemAnnouncement(_pendingAnnouncement);
                    _pendingAnnouncement = null;
                }

                if (_pendingDiamondBalance >= 0)
                {
                    _playerRegistry?.BillingSyncQueue.Enqueue(new BillingSyncNotification
                    {
                        PlayerId = playerId,
                        PremiumDiamondsBalance = _pendingDiamondBalance
                    });
                    _pendingDiamondBalance = -1;
                }

                Console.WriteLine($"Reroll success: {affixKeyToReroll} -> {newAffixKey} ({resultRarity})");
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();

                // Discard any announcement staged before the failure. The
                // engine instance is reused across an auto-reroll run, so a
                // leftover here would be broadcast by the NEXT attempt and
                // credit the player with a roll that never committed.
                _pendingAnnouncement = null;
                _pendingDiamondBalance = -1;

                Console.WriteLine($"Reroll transaction aborted: {ex.Message}");
            }
        }
    }
}
