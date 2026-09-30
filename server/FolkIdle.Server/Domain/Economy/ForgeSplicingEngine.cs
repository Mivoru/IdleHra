using System;
using System.Collections.Generic;
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
        // Modul: 200, down from 1,000, and the curve below softened with it.
        //
        // THE THREE ITEMS ARE THE COST. Fusion consumes three identical pieces
        // at the same rarity - assembling those is the work, and the gold was
        // supposed to be a fee on top rather than a second gate. At 1,000 and
        // 1.5^tier it was 25,628 to raise a tier-8 piece, about an hour of
        // region-2 income for one step, which is more than the items themselves
        // are worth to most players.
        private const long BaseGoldCost = 200;

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

        // Modul: 14, THE TOP OF THE RARITY LADDER - was 13.
        //
        // The old figure came from reading the GDD's fourteen tiers as
        // "tiers 0-13 inclusive". Drops do not agree: RarityTier.Normal is 1
        // and Transcendent is 14, so every item in the game is 1-based, and a
        // ceiling of 13 quietly made Transcendent the one rarity that exists
        // and cannot be reached. An off-by-one in a constant nobody re-derived
        // against the thing it caps.
        public const int MaxQualityTier = 14;

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
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.GenericValidationFailure);
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

                    // Modul: SAY WHICH GATE, because this one covers the two
                    // refusals a player can actually act on - a Forge too low
                    // for the rarity being reached for, and an item whose
                    // affixes are locked. It reported neither: it wrote to the
                    // server's console and returned, so the screen showed a
                    // failure with no reason and the player concluded fusion was
                    // broken. It is the likeliest cause of that report.
                    int wouldBeTier = 0;
                    bool anyLocked = false;
                    for (int i = 0; i < lockedItems.Count; i++)
                    {
                        if (lockedItems[i].Id == targetItemGuid) wouldBeTier = lockedItems[i].QualityTier + 1;
                        if (lockedItems[i].IsAffixLocked) anyLocked = true;
                    }

                    // Modul: THE CEILING IS CHECKED BEFORE THE FORGE LEVEL.
                    //
                    // An item already at the top wants a tier above the maximum,
                    // which is also above any possible Forge level - so the
                    // level branch fired first and told the player to upgrade
                    // their Forge, which would not have helped and cannot be
                    // done. "This is as high as it goes" is the true reason.
                    if (wouldBeTier > MaxQualityTier)
                    {
                        Console.WriteLine("Fusion failed: target item is already at the maximum rarity.");
                        _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.MaxTierReached);
                        return ForgeSplicingResult.MaxTierReached;
                    }

                    var gateReason = wouldBeTier > 0 && forgeLevel < wouldBeTier
                        ? FolkIdle.Server.Network.CommandResultCode.ForgeLevelTooLow
                        : anyLocked
                            ? FolkIdle.Server.Network.CommandResultCode.ItemEquipped
                            : FolkIdle.Server.Network.CommandResultCode.GenericValidationFailure;

                    Console.WriteLine($"Fusion failed: integrity gate rejected request (forge {forgeLevel}, wanted tier {wouldBeTier}, locked {anyLocked}).");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)gateReason);
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
                    // One of the three is no longer yours - sold, fused into
                    // something else in another tab, or listed on the market.
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.TargetNotFound);
                    return ForgeSplicingResult.InvalidRequest;
                }

                if (targetItem.BaseItemId != sac1.BaseItemId || targetItem.BaseItemId != sac2.BaseItemId)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine("Fusion failed: Items must have identical Base Item IDs.");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.ItemsNotIdentical);
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
                // Modul: ONE CEILING, the global one. The per-gear-band cap
                // that used to narrow this is gone - see
                // CraftingEngine.GetMaxForgeTierForRegion for why.
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
                    // RegionTier is still resolved - the affix roll further
                    // down needs it - but it no longer narrows the ceiling.
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

                double feeDiscount = FeeDiscountFor(forgePlayer?.BaseLuck);
                long cost = FusionFee(currentTier, feeDiscount);

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
                await GoldLedger.RecordSpendAsync(db, playerId, GoldSpendCategory.Fusion, cost);

                {
                    db.EquipmentInstances.Remove(sac1);
                    db.EquipmentInstances.Remove(sac2);

                    targetItem.QualityTier = currentTier + 1;

                    // Modul: the new affix the higher tier is owed. The forge used
                    // to write it as "<id>_<4 hex>" with its own magnitude
                    // formula and no slot check - a key the reroll refused and
                    // the stat totals never matched, so it could not be rerolled
                    // and added nothing (and "range_dmg_pct" landed on leggings).
                    // It is rolled by AffixRegistry now, the same way a drop is.
                    // See AffixRegistry.TryStripLegacyFusionSuffix for the rows
                    // written before.
                    JsonObject affixPayload = ParseAffixPayload(targetItem.AffixPayload);
                    int regionTier = targetRegionTier;
                    var existingKeys = new List<string>(affixPayload.Count);
                    foreach (var pair in affixPayload) existingKeys.Add(pair.Key);
                    if (AffixRegistry.TryRollOneAdditional(targetItem.BaseItemId, regionTier, currentTier + 1,
                            existingKeys, out string newAffixKey, out int newAffixValue))
                    {
                        affixPayload[newAffixKey] = newAffixValue;
                    }

                    targetItem.AffixPayload = affixPayload.ToJsonString();

                    // Modul: the Book of Deeds, chapter II. A lifetime count
                    // of SUCCESSFUL fusions, incremented inside the same
                    // transaction that consumed the sacrifices - a counter
                    // written outside it would drift every time a fusion rolled
                    // back, and a deed whose number is wrong is worse than one
                    // that does not exist.
                    var fusingPlayer = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
                    if (fusingPlayer != null) fusingPlayer.ForgeFusionsCompleted++;

                    Console.WriteLine($"Fusion Success! Target item {targetItem.Id} upgraded to Tier {targetItem.QualityTier}.");
                    await db.SaveChangesAsync();

                    // Modul: the drop record (task 26). Every fusion gets a row,
                    // whatever the tier: "was that Godly dropped or forged?" had
                    // to be reconstructed from affix-key shapes once, and that
                    // is a question a WHERE clause should answer. Same
                    // transaction, beside ForgeFusionsCompleted. The row only,
                    // not a count: the piece was counted when it dropped.
                    await DropRecord.RecordOneAsync(db, playerId, DropSource.Forge, regionTier,
                        targetItem, targetItem.BaseItemId, rolledTier: currentTier, finalTier: currentTier + 1,
                        alwaysNotable: true, countPiece: false);
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

        /// <summary>
        /// The fee discount Luck and the Diamond Star event buy, capped at
        /// <see cref="MaxForgeFeeDiscount"/>. One definition for the single
        /// fusion, the stack fusion and its preview, so the price a preview
        /// quotes is the price the fusion charges.
        /// </summary>
        public static double FeeDiscountFor(int? baseLuck)
        {
            double feeDiscount = 0.0;
            if (baseLuck.HasValue)
            {
                float forgeSuccessPct = StatsCalculator.Calculate(0, 0, 0, baseLuck.Value).ForgeSuccessPct;
                feeDiscount = Math.Min(MaxForgeFeeDiscount, forgeSuccessPct / 100.0);
            }

            if (SimulationEngine.ActiveGlobalEventId == 4) // DiamondStar
            {
                // Was +5 percentage points of success. With no roll left
                // it is 5 percentage points off the fee, so the event
                // still means something at the anvil.
                feeDiscount = Math.Min(MaxForgeFeeDiscount, feeDiscount + 0.05);
            }

            return feeDiscount;
        }

        /// <summary>
        /// The gold one fusion of a piece at <paramref name="currentTier"/>
        /// costs: <c>200 * 1.35^tier</c>, less the discount.
        /// </summary>
        public static long FusionFee(int currentTier, double feeDiscount)
        {
            long cost = (long)Math.Ceiling(BaseGoldCost * Math.Pow(1.35, currentTier));
            return (long)Math.Ceiling(cost * (1.0 - feeDiscount));
        }

        // Modul: TASK 69 - A WHOLE STACK IN ONE ACTION.
        //
        // Fusion is deterministic 3:1, so a pile of identical pieces has exactly
        // one decision in it: how far up. The screen made the player take it
        // one fusion at a time through three selects - the dev fixture holds
        // 7,550 Normal Birch Axes, about 2,500 presses for no choice at all.
        //
        // The stack fusion applies the single fusion's rules, not a copy of
        // them: the same fee (FusionFee), the same Forge-level ceiling, the same
        // refusal of a locked or worn piece, the same affix roll per step, the
        // same lifetime counter. What it adds is a bound: at most
        // MaxStackFusions per call, so one request is one bounded transaction.
        public const int MaxStackFusions = 10_000;

        /// <summary>What a stack fusion would do, or did.</summary>
        public sealed class StackFusionPlan
        {
            public int FromTier { get; init; }
            /// <summary>The highest tier the plan may reach: the requested
            /// tier, clamped to the Forge level and to MaxQualityTier.</summary>
            public int CeilingTier { get; init; }
            public int ForgeLevel { get; init; }
            /// <summary>Fusions per tier, indexed by the tier fused FROM.</summary>
            public int[] FusionsByTier { get; init; } = new int[MaxQualityTier + 1];
            /// <summary>Pieces of this base item per tier after the plan.</summary>
            public int[] CountsAfter { get; init; } = new int[MaxQualityTier + 1];
            public int TotalFusions { get; init; }
            public long GoldCost { get; init; }
            public long GoldAvailable { get; init; }
            /// <summary>True when gold, not pieces or the ceiling, ended it.</summary>
            public bool StoppedByGold { get; init; }
            public bool StoppedByCap { get; init; }
        }

        /// <summary>
        /// The pure planner. <paramref name="counts"/> is eligible pieces per
        /// tier (index = tier). Each tier from <paramref name="fromTier"/> up to
        /// one below the ceiling fuses as many triples as it holds and gold
        /// pays for; the products join the next tier and can fuse again.
        /// </summary>
        public static StackFusionPlan PlanStack(
            int[] counts, int fromTier, int requestedTier, int forgeLevel,
            double feeDiscount, long goldAvailable, int maxFusions = MaxStackFusions)
        {
            var after = new int[MaxQualityTier + 1];
            Array.Copy(counts, after, Math.Min(counts.Length, after.Length));
            var fusions = new int[MaxQualityTier + 1];

            int ceiling = Math.Min(Math.Min(requestedTier, forgeLevel), MaxQualityTier);
            long gold = goldAvailable;
            long spent = 0;
            int total = 0;
            bool byGold = false, byCap = false;

            for (int tier = Math.Max(1, fromTier); tier < ceiling; tier++)
            {
                int possible = after[tier] / 3;
                if (possible == 0) continue;

                long fee = FusionFee(tier, feeDiscount);
                int affordable = fee <= 0 ? possible : (int)Math.Min(possible, gold / fee);
                int room = maxFusions - total;
                int done = Math.Min(affordable, room);
                if (done < possible)
                {
                    if (done == room) byCap = true; else byGold = true;
                }
                if (done <= 0) break;

                after[tier] -= done * 3;
                after[tier + 1] += done;
                fusions[tier] = done;
                gold -= done * fee;
                spent += done * fee;
                total += done;
                if (byCap || byGold) break;
            }

            return new StackFusionPlan
            {
                FromTier = fromTier,
                CeilingTier = ceiling,
                ForgeLevel = forgeLevel,
                FusionsByTier = fusions,
                CountsAfter = after,
                TotalFusions = total,
                GoldCost = spent,
                GoldAvailable = goldAvailable,
                StoppedByGold = byGold,
                StoppedByCap = byCap,
            };
        }

        /// <summary>
        /// The eligible pieces of one stack: same player, same BaseItemId, at
        /// or above the sample's tier and below the ceiling, not locked, not
        /// worn by anyone on the account. The single fusion's refusals, as a
        /// filter.
        /// </summary>
        private static async Task<List<EquipmentInstance>> LoadStackAsync(
            FolkIdleDbContext db, long playerId, string baseItemId, int fromTier, int ceiling, bool forUpdate)
        {
            var worn = await VillageChestEngine.LoadWornEquipmentIdsAsync(db, playerId);
            List<EquipmentInstance> rows = forUpdate
                ? await db.EquipmentInstances
                    .FromSqlRaw("SELECT * FROM \"EquipmentInstances\" WHERE \"PlayerId\" = {0} AND \"BaseItemId\" = {1} AND \"QualityTier\" >= {2} AND \"QualityTier\" < {3} FOR UPDATE",
                        playerId, baseItemId, fromTier, ceiling)
                    .ToListAsync()
                : await db.EquipmentInstances.AsNoTracking()
                    .Where(e => e.PlayerId == playerId && e.BaseItemId == baseItemId
                        && e.QualityTier >= fromTier && e.QualityTier < ceiling)
                    .ToListAsync();

            rows.RemoveAll(e => e.IsAffixLocked || worn.Contains(e.Id)
                || ClientCommandValidator.HasLockedAffixPayload(e.AffixPayload));
            rows.Sort((a, b) => a.Id.CompareTo(b.Id));
            return rows;
        }

        private static int[] CountByTier(List<EquipmentInstance> rows)
        {
            var counts = new int[MaxQualityTier + 1];
            foreach (var row in rows)
            {
                if (row.QualityTier >= 1 && row.QualityTier <= MaxQualityTier) counts[row.QualityTier]++;
            }
            return counts;
        }

        private static async Task<int> ForgeLevelOfAsync(FolkIdleDbContext db, long playerId) =>
            await db.VillageInfrastructures
                .AsNoTracking()
                .Where(v => v.PlayerId == playerId && v.BuildingId == VillageManagementEngine.ForgeBuildingId)
                .Select(v => (int?)v.CurrentLevel)
                .SingleOrDefaultAsync() ?? 0;

        /// <summary>
        /// The preview: what fusing the stack <paramref name="sampleItemId"/>
        /// belongs to up to <paramref name="requestedTier"/> would do now.
        /// Read-only. Null when the sample is not the player's.
        /// </summary>
        public async Task<(string BaseItemId, StackFusionPlan Plan)?> PreviewStackFusionAsync(
            long playerId, long sampleItemId, int requestedTier)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            var sample = await db.EquipmentInstances.AsNoTracking()
                .Where(e => e.Id == sampleItemId && e.PlayerId == playerId)
                .Select(e => new { e.BaseItemId, e.QualityTier })
                .SingleOrDefaultAsync();
            if (sample == null) return null;

            int forgeLevel = await ForgeLevelOfAsync(db, playerId);
            int ceiling = Math.Min(Math.Min(requestedTier, forgeLevel), MaxQualityTier);
            var rows = await LoadStackAsync(db, playerId, sample.BaseItemId, sample.QualityTier, ceiling, forUpdate: false);

            var player = await db.PlayerRecords.AsNoTracking()
                .Where(p => p.Id == playerId).Select(p => new { p.BaseLuck }).SingleOrDefaultAsync();
            long gold = await db.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == "gold")
                .Select(c => c.Quantity).SingleOrDefaultAsync();

            var plan = PlanStack(CountByTier(rows), sample.QualityTier, requestedTier, forgeLevel,
                FeeDiscountFor(player?.BaseLuck), gold);
            return (sample.BaseItemId, plan);
        }

        /// <summary>What one stack fusion did, inside the caller's transaction.</summary>
        internal readonly struct StackFusionResult
        {
            public StackFusionPlan Plan { get; init; }
            /// <summary>The highest tier a piece came out of the anvil at; 0 when nothing fused.</summary>
            public int BestTier { get; init; }
        }

        /// <summary>
        /// THE stack fusion: loads the eligible pieces of one stack (FOR UPDATE),
        /// plans with <see cref="PlanStack"/>, and - when the plan fuses anything -
        /// removes the sacrifices, raises the targets with one affix roll per
        /// step, takes the fee off the locked gold row, counts the lifetime
        /// fusions and writes the drop record. All inside the CALLER's
        /// transaction, which the caller commits or rolls back.
        /// </summary>
        /// <remarks>
        /// Modul: ONE FUSION PATH, TWO CALLERS (task 85). The Forge's "fuse
        /// stack" button (ExecuteStackFusionAsync) and the automation rule
        /// "fuse stacks up to tier N" (AutoFuseStacksAsync, run by the loot
        /// worker) both land here, so the rule cannot price, filter or roll a
        /// fusion differently from the button a player can press.
        /// </remarks>
        internal static async Task<StackFusionResult> FuseStackInTransactionAsync(
            FolkIdleDbContext db, long playerId, string baseItemId, int fromTier, int requestedTier, int forgeLevel)
        {
            int ceiling = Math.Min(Math.Min(requestedTier, forgeLevel), MaxQualityTier);
            var rows = await LoadStackAsync(db, playerId, baseItemId, fromTier, ceiling, forUpdate: true);

            var player = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
            var goldRecord = await db.CommodityRecords
                .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", playerId)
                .SingleOrDefaultAsync();
            long gold = goldRecord?.Quantity ?? 0L;

            var plan = PlanStack(CountByTier(rows), fromTier, requestedTier, forgeLevel,
                FeeDiscountFor(player?.BaseLuck), gold);

            if (plan.TotalFusions == 0)
            {
                return new StackFusionResult { Plan = plan };
            }

            int regionTier = ContentRegistry.TryGetItemDefinitionByBaseId(baseItemId, out var definition)
                ? definition.RegionTier
                : 1;

            // Pieces per tier, oldest first. A fusion takes the first three:
            // the first keeps its affixes and gains one, the other two go.
            var byTier = new Queue<EquipmentInstance>[MaxQualityTier + 1];
            for (int t = 0; t <= MaxQualityTier; t++) byTier[t] = new Queue<EquipmentInstance>();
            foreach (var row in rows) byTier[row.QualityTier].Enqueue(row);

            var originalTier = new Dictionary<long, int>();
            var consumed = new HashSet<long>();
            for (int tier = Math.Max(1, fromTier); tier < plan.CeilingTier; tier++)
            {
                for (int n = 0; n < plan.FusionsByTier[tier]; n++)
                {
                    var target = byTier[tier].Dequeue();
                    var sac1 = byTier[tier].Dequeue();
                    var sac2 = byTier[tier].Dequeue();
                    db.EquipmentInstances.Remove(sac1);
                    db.EquipmentInstances.Remove(sac2);
                    consumed.Add(sac1.Id);
                    consumed.Add(sac2.Id);

                    if (!originalTier.ContainsKey(target.Id)) originalTier[target.Id] = tier;
                    target.QualityTier = tier + 1;

                    JsonObject affixPayload = ParseAffixPayload(target.AffixPayload);
                    var existingKeys = new List<string>(affixPayload.Count);
                    foreach (var pair in affixPayload) existingKeys.Add(pair.Key);
                    if (AffixRegistry.TryRollOneAdditional(target.BaseItemId, regionTier, tier + 1,
                            existingKeys, out string newAffixKey, out int newAffixValue))
                    {
                        affixPayload[newAffixKey] = newAffixValue;
                    }
                    target.AffixPayload = affixPayload.ToJsonString();

                    byTier[tier + 1].Enqueue(target);
                }
            }

            if (goldRecord != null) goldRecord.Quantity -= plan.GoldCost;
            if (goldRecord != null) await GoldLedger.RecordSpendAsync(db, playerId, GoldSpendCategory.Fusion, plan.GoldCost);
            if (player != null) player.ForgeFusionsCompleted += plan.TotalFusions;
            await db.SaveChangesAsync();

            // The drop record: one notable row per piece that came out of
            // the anvil changed - what the single fusion writes per fusion,
            // minus the rows for pieces a later step consumed. One write.
            var tally = new DropTally();
            DateTime now = DateTime.UtcNow;
            int bestTier = 0;
            foreach (var row in rows)
            {
                if (consumed.Contains(row.Id) || !originalTier.TryGetValue(row.Id, out int from)) continue;
                tally.Notable(playerId, DropSource.Forge, row, row.BaseItemId, from, row.QualityTier, 0f, now);
                if (row.QualityTier > bestTier) bestTier = row.QualityTier;
            }
            await DropRecord.WriteAsync(db, playerId, tally, now);

            return new StackFusionResult { Plan = plan, BestTier = bestTier };
        }

        /// <summary>
        /// Fuses the stack <paramref name="sampleItemId"/> belongs to, from the
        /// sample's tier up to <paramref name="requestedTier"/>, in one
        /// transaction. Answers with a command result, never a disconnect.
        /// </summary>
        public async Task<StackFusionPlan?> ExecuteStackFusionAsync(long playerId, long sampleItemId, int requestedTier)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var sample = await db.EquipmentInstances.AsNoTracking()
                    .Where(e => e.Id == sampleItemId && e.PlayerId == playerId)
                    .Select(e => new { e.BaseItemId, e.QualityTier })
                    .SingleOrDefaultAsync();
                if (sample == null)
                {
                    await transaction.RollbackAsync();
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.TargetNotFound);
                    return null;
                }

                int forgeLevel = await ForgeLevelOfAsync(db, playerId);
                int ceiling = Math.Min(Math.Min(requestedTier, forgeLevel), MaxQualityTier);
                if (sample.QualityTier >= MaxQualityTier)
                {
                    await transaction.RollbackAsync();
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.MaxTierReached);
                    return null;
                }
                if (ceiling <= sample.QualityTier)
                {
                    await transaction.RollbackAsync();
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.ForgeLevelTooLow);
                    return null;
                }

                var result = await FuseStackInTransactionAsync(db, playerId, sample.BaseItemId, sample.QualityTier, requestedTier, forgeLevel);
                var plan = result.Plan;

                if (plan.TotalFusions == 0)
                {
                    await transaction.RollbackAsync();
                    var reason = plan.StoppedByGold
                        ? FolkIdle.Server.Network.CommandResultCode.InsufficientGold
                        : FolkIdle.Server.Network.CommandResultCode.GenericValidationFailure;
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)reason);
                    return plan;
                }

                await transaction.CommitAsync();

                Console.WriteLine($"Stack fusion: player {playerId} fused {plan.TotalFusions}x {sample.BaseItemId} for {plan.GoldCost} gold.");
                _playerRegistry?.ForgeUpgradeQueue.Enqueue(new ForgeUpgradeNotification
                {
                    PlayerId = playerId,
                    ResultingQualityTier = result.BestTier
                });
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);
                return plan;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Stack fusion aborted: {ex.Message}");
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.GenericValidationFailure);
                return null;
            }
        }

        /// <summary>What an automatic fusion pass did for one player.</summary>
        internal readonly struct AutoFuseOutcome
        {
            public int TotalFusions { get; init; }
            public long GoldCost { get; init; }
            public int BestTier { get; init; }
        }

        /// <summary>
        /// Task 85, rule 3: fuses each named stack from tier 1 up to
        /// <paramref name="toTier"/>, in ONE transaction, through
        /// <see cref="FuseStackInTransactionAsync"/> - the button's own path.
        /// A stack that cannot fuse (too few pieces, no gold, the Forge level)
        /// is simply left; nothing here answers a command, because no command
        /// was sent. Called by CombatLootEngine after a cycle's drops commit.
        /// </summary>
        internal static async Task<AutoFuseOutcome> AutoFuseStacksAsync(
            FolkIdleDbContext db, long playerId, IReadOnlyCollection<string> baseItemIds, int toTier)
        {
            if (baseItemIds.Count == 0 || toTier < 2) return default;

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            int forgeLevel = await ForgeLevelOfAsync(db, playerId);
            int fusions = 0;
            long cost = 0;
            int best = 0;
            var ordered = new List<string>(baseItemIds);
            ordered.Sort(StringComparer.Ordinal);
            foreach (string baseItemId in ordered)
            {
                var result = await FuseStackInTransactionAsync(db, playerId, baseItemId, 1, toTier, forgeLevel);
                fusions += result.Plan.TotalFusions;
                cost += result.Plan.GoldCost;
                if (result.BestTier > best) best = result.BestTier;
                // Gold is the one budget the stacks share; once it has stopped
                // one stack it will stop the rest.
                if (result.Plan.StoppedByGold) break;
            }

            if (fusions == 0)
            {
                await transaction.RollbackAsync();
                return default;
            }

            await transaction.CommitAsync();
            return new AutoFuseOutcome { TotalFusions = fusions, GoldCost = cost, BestTier = best };
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
