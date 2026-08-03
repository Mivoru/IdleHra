using System;
using System.Data;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Models;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Economy
{
    public enum ForgeSplicingResult
    {
        Success = 0,
        FailedSacrificesDestroyed = 1,
        FailedAffixLocked = 2,
        CriticalFailure = 3,
        InvalidRequest = 4,
        InsufficientGold = 5,
        FailedItemEquipped = 6,
        MaxTierReached = 7
    }

    public class ForgeSplicingEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry? _playerRegistry;
        private const long BaseGoldCost = 1000;

        // Modul: fusion is no longer a gamble. Three IDENTICAL items of the
        // SAME rarity produce one of the next rarity, for a gold fee. The
        // random roll, the affix lockout on a tier 2 failure and the total
        // vaporization at tier 3+ are all gone.
        //
        // The reason is the chest. There is no scrapping any more - the way a
        // player disposes of duplicate gear IS the forge - so a mechanic that
        // eats three matched items and returns nothing is a dead end with no
        // alternative. Requiring all three to match in rarity as well as base
        // id is a much harder input to assemble than the old "any two
        // sacrifices", which is what pays for the certainty.
        //
        // Luck used to buy success probability. With no roll left it buys a
        // discount on the fee instead, so the stat keeps exactly one forge
        // meaning rather than silently having none - which is the state it was
        // in before "Luck made real" fixed it the first time.
        private const double MaxForgeFeeDiscount = 0.25;

        // Modul: GAME_DESIGN_SPEC.md/GDD's 14-tier geometric forge formula
        // requires exactly 3^13 = 1,594,323 Normal item bases to synthesize
        // one clean Transcendent-tier item, implying a hard ceiling at
        // tier 13 (tiers 0-13 inclusive, 14 total states) - previously
        // unenforced anywhere in this method, so QualityTier climbed
        // unbounded on every successful fusion past the intended endgame
        // cap.
        public const int MaxQualityTier = 13;

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
                // Modul: Forge fusion operates on EquipmentInstances (a
                // player's real owned gear, matching EquipmentSlotEngine) -
                // previously operated on MarketEquipmentInstances, a
                // fragmented, non-interoperating pool that a player's actual
                // inventory never populated.
                // Explicit FOR UPDATE row-level pessimistic lock
                var query = $"SELECT * FROM \"EquipmentInstances\" WHERE \"Id\" IN ({id0}, {id1}, {id2}) FOR UPDATE";
                var lockedItems = await db.EquipmentInstances
                    .FromSqlRaw(query)
                    .ToListAsync();

                // Modul: equipped-item guard. Reject the fusion outright if any
                // of the three locked rows is currently equipped, preventing a
                // dangling equip pointer or phantom duplication if the row is
                // later deleted/vaporized below.
                //
                // Modul: per-character equipment. This used to read three fields
                // off the player row. Gear now belongs to individual characters,
                // so the question is whether ANY character on the account is
                // wearing any of the three - a fusion that consumed the item a
                // second character was holding would leave that character
                // pointing at a deleted row.
                if (await EquipmentSlotEngine.IsAnyEquippedAnywhereAsync(db, playerId, targetItemGuid, sacrificialItem1Guid, sacrificialItem2Guid))
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine("Fusion failed: target or sacrifice item is currently equipped.");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.ItemEquipped);
                    return ForgeSplicingResult.FailedItemEquipped;
                }

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

                EquipmentInstance? targetItem = null;
                EquipmentInstance? sac1 = null;
                EquipmentInstance? sac2 = null;
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

                // Modul: all three must ALSO share a rarity. Fusion used to
                // take any two sacrifices regardless of tier, which let a
                // player feed two Normal duplicates into a Legendary and climb
                // for almost nothing - the cost formula even discounted low
                // tier fodder. Three matched rarities is the whole input rule
                // now, and it is what makes a guaranteed result affordable.
                if (targetItem.QualityTier != sac1.QualityTier || targetItem.QualityTier != sac2.QualityTier)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine("Fusion failed: all three items must share the same rarity.");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.RarityMismatch);
                    return ForgeSplicingResult.InvalidRequest;
                }

                int currentTier = targetItem.QualityTier;

                // Modul: hard tier cap - rejected before any resource
                // consumption (the gold check/deduction below) or database
                // write, matching every other early-rejection branch in
                // this method (equipped-item guard, integrity gate). An
                // item already at the Transcendent ceiling cannot be
                // fused further regardless of gold or fodder quality.
                //
                // Modul: Full-Stack Expansion, Part 4. The global ceiling
                // is additionally tightened per structural gear band -
                // low-band gear (by the item's RegionTier via
                // CraftingEngine.GetMaxForgeTierForRegion) caps below the
                // Transcendent maximum, blocking affix-upgrading past the
                // band limit server-side.
                int effectiveTierCap = MaxQualityTier;
                // Modul: forge region tier. Resolved ONCE here and reused by the
                // affix roll further down, which used to do its own broken
                // int.TryParse(BaseItemId) lookup. Two lookups of the same thing
                // in one method, one of which could never succeed, is how the
                // tier cap ended up correct while the affix scaling silently
                // was not.
                int targetRegionTier = 1;
                if (ContentRegistry.TryGetItemDefinitionByBaseId(targetItem.BaseItemId, out var targetDefinition))
                {
                    targetRegionTier = targetDefinition.RegionTier;
                    effectiveTierCap = Math.Min(MaxQualityTier, CraftingEngine.GetMaxForgeTierForRegion(targetRegionTier));
                }
                if (currentTier >= effectiveTierCap)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine("Fusion failed: target item has already reached MaxQualityTier.");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.MaxTierReached);
                    return ForgeSplicingResult.MaxTierReached;
                }

                // Modul: GDD-mandated exponential curve - Cost = BaseGoldCost
                // * 1.5^currentTier - replacing the previous linear
                // BaseGoldCost * (currentTier + 1), which grew far too
                // slowly to remain a meaningful gold sink at high quality
                // tiers relative to the rest of this game's exponential
                // economy (village production, legacy perks, level-up cost
                // all scale geometrically too).
                // The old multiplier discounted LOW tier fodder - it existed
                // because the two sacrifices could be any rarity. All three
                // now share a rarity by rule, so the modifier had exactly one
                // possible value and is gone; the curve itself is unchanged.
                long cost = (long)Math.Ceiling(BaseGoldCost * Math.Pow(1.5, currentTier));

                // Modul: Luck made real. StatsCalculator has always documented
                // Luck as granting "+0.05% Forge Success" and has always
                // computed CombatStats.ForgeSuccessPct - and nothing anywhere
                // read it, so every point a player put into Luck for forge
                // safety did exactly nothing. This is the only roll in the game
                // that stat was ever meant to touch.
                //
                // Routed through StatsCalculator rather than multiplying
                // BaseLuck here, so the coefficient stays defined in one place;
                // the other arguments keep their defaults because none of them
                // affect ForgeSuccessPct.
                var forgePlayer = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => new { p.BaseLuck })
                    .SingleOrDefaultAsync();

                double feeDiscount = 0.0;
                if (forgePlayer != null)
                {
                    float forgeSuccessPct = StatsCalculator.Calculate(0, 0, 0, forgePlayer.BaseLuck).ForgeSuccessPct;
                    feeDiscount = Math.Min(MaxForgeFeeDiscount, forgeSuccessPct / 100.0);
                }

                if (SimulationEngine.ActiveGlobalEventId == 4) // DiamondStar
                {
                    // Was +5 percentage points of success. With no roll left
                    // it is 5 percentage points off the fee, so the event
                    // still means something at the anvil.
                    feeDiscount = Math.Min(MaxForgeFeeDiscount, feeDiscount + 0.05);
                }

                cost = (long)Math.Ceiling(cost * (1.0 - feeDiscount));

                // Lock and fetch gold record
                var goldRecord = await db.CommodityRecords
                    .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                    .SingleOrDefaultAsync();

                if (goldRecord == null || goldRecord.Quantity < cost)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine("Fusion failed: Insufficient gold.");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.InsufficientGold);
                    return ForgeSplicingResult.InsufficientGold;
                }

                // Deduct cost
                goldRecord.Quantity -= cost;

                {
                    db.EquipmentInstances.Remove(sac1);
                    db.EquipmentInstances.Remove(sac2);

                    targetItem.QualityTier = currentTier + 1;

                    // Append/roll new affix modifier
                    JsonObject affixPayload = ParseAffixPayload(targetItem.AffixPayload);
                    string newAffixType = AffixEngine.GetRandomAffixKey();
                    int targetValue = 0;
                    // Modul: forge region tier. This used to be
                    // int.TryParse(targetItem.BaseItemId, ...), but BaseItemId
                    // is ALWAYS a descriptive slug ("gilded_sabatons_boots_
                    // armor_slot_base"), never a numeric string - every writer
                    // of it goes through ContentRegistry.GetItemBaseId. So the
                    // parse could never succeed, the lookup was dead code, and
                    // every forge fusion in the game rolled its affix at region
                    // tier 1 regardless of what was actually on the anvil.
                    //
                    // Seventh instance in this codebase of a numeric id being
                    // used directly as a game-object identity. The value is now
                    // the one already resolved for the tier cap above.
                    int regionTier = targetRegionTier;

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
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);

                    return ForgeSplicingResult.Success;
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
