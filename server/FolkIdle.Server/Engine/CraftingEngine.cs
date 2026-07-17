using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Models;
using System.Data;

namespace FolkIdle.Server.Engine
{
    public class CraftingEngine
    {
        private readonly IDbContextFactory<FolkIdleDbContext> _contextFactory;
        private readonly PlayerSessionRegistry _playerRegistry;
        private readonly GuildWarEngine? _guildWarEngine;
        private readonly RetryingDbContextOptions _retryingDbOptions;

        public CraftingEngine(IDbContextFactory<FolkIdleDbContext> contextFactory, PlayerSessionRegistry playerRegistry, RetryingDbContextOptions retryingDbOptions, GuildWarEngine? guildWarEngine = null)
        {
            _contextFactory = contextFactory;
            _playerRegistry = playerRegistry;
            _retryingDbOptions = retryingDbOptions;
            _guildWarEngine = guildWarEngine;
        }

        // Modul: Full-Stack Expansion, Part 4. The 14 rarity tiers'
        // baseline weights (tier 0 Common 50.0 through tier 13
        // Transcendent 0.0001) - the same strictly-decreasing geometric
        // family CombatLootEngine's combat-drop table uses, so crafted and
        // dropped rarity distributions stay in one economy.
        public const int RarityTierCount = 14;

        // Modul: Full-Stack Expansion, Part 4. Rolls a crafted item's
        // bonus rarity tier (0-13) with exactly zero managed heap
        // allocations: the 14 cumulative weight bounds are built in a
        // stackalloc double[14] and evaluated entirely on the stack -
        // no arrays, no LINQ, no boxing. craftingSkill and workshopLevel
        // shift probability weight toward high-tier outcomes by
        // compounding a per-tier multiplier (each successive tier's
        // weight is scaled by the multiplier one more time than the tier
        // below it), so higher inputs flatten the baseline decay without
        // ever making a lower tier less likely than a higher one.
        // seedRandomValue is the caller-supplied uniform [0, 1) roll -
        // passing it in keeps this function pure and deterministic for
        // tests while the live call site feeds Random.Shared.NextDouble().
        public static int RollCraftedRarity(int craftingSkill, int workshopLevel, double seedRandomValue)
        {
            if (craftingSkill < 0) craftingSkill = 0;
            if (workshopLevel < 0) workshopLevel = 0;
            if (seedRandomValue < 0.0) seedRandomValue = 0.0;
            if (seedRandomValue >= 1.0) seedRandomValue = 0.9999999999;

            Span<double> cumulativeBounds = stackalloc double[RarityTierCount];

            double tierMultiplier = 1.0 + craftingSkill * 0.002 + workshopLevel * 0.05;

            double weight = 50.0;
            double compounded = 1.0;
            double runningTotal = 0.0;
            for (int tier = 0; tier < RarityTierCount; tier++)
            {
                runningTotal += weight * compounded;
                cumulativeBounds[tier] = runningTotal;

                // Baseline decay mirrors the established drop-table curve:
                // halving-to-fifthing steps from 50.0 down to 0.0001.
                weight *= tier switch
                {
                    0 => 0.5,     // 50 -> 25
                    1 => 0.5,     // 25 -> 12.5
                    2 => 0.4,     // 12.5 -> 5
                    3 => 0.5,     // 5 -> 2.5
                    4 => 0.4,     // 2.5 -> 1
                    5 => 0.5,     // 1 -> 0.5
                    6 => 0.5,     // 0.5 -> 0.25
                    7 => 0.4,     // 0.25 -> 0.1
                    8 => 0.5,     // 0.1 -> 0.05
                    9 => 0.2,     // 0.05 -> 0.01
                    10 => 0.5,    // 0.01 -> 0.005
                    11 => 0.2,    // 0.005 -> 0.001
                    _ => 0.1      // 0.001 -> 0.0001
                };
                compounded *= tierMultiplier;
            }

            double roll = seedRandomValue * runningTotal;
            for (int tier = 0; tier < RarityTierCount; tier++)
            {
                if (roll < cumulativeBounds[tier])
                {
                    return tier;
                }
            }

            return RarityTierCount - 1;
        }

        // Modul: Full-Stack Expansion, Part 4. Hard forge/affix-upgrade
        // tier caps by the item's structural gear band, derived from the
        // same RequiredLevel axis EquipmentLevelGate anchors to (region
        // tier bands of 20 levels): band 1 caps at tier 5, band 2 at 10,
        // and bands 3+ at the global MaxQualityTier ceiling (the task's
        // nominal caps of 15/20/25 exceed the 14-tier system's hard
        // maximum of 13 and clamp to it). ForgeSplicingEngine rejects any
        // fusion whose target already sits at its band cap.
        public static int GetMaxForgeTierForRegion(int regionTier)
        {
            int requiredLevelBase = EquipmentLevelGate.DeriveRequiredLevel(regionTier, 0);
            if (requiredLevelBase < 20) return 5;
            if (requiredLevelBase < 40) return 10;
            return ForgeSplicingEngine.MaxQualityTier;
        }

        public async Task ExecuteCraftingAsync(long playerId, int recipeResultItemId)
        {
            if (!ContentRegistry.TryGetRecipe(recipeResultItemId, out var recipe))
            {
                return;
            }

            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            // Modul: the delegate returns (success, quantity) instead of
            // throwing-and-catching for the expected "insufficient
            // materials" outcome - that is a normal business result, not a
            // failure, and must not be retried. A genuine Serializable
            // conflict or transient failure is left to propagate out of the
            // delegate so CreateExecutionStrategy retries it; this method no
            // longer swallows exceptions itself, matching every other
            // fire-and-forget dispatch site's SafeDispatchAsync wrapper.
            (bool success, int quantityProduced) = await strategy.ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                var player = await context.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
                if (player == null) return (false, 0);

                var charRecord = await context.CharacterRecords.FirstOrDefaultAsync(c => c.PlayerId == playerId && c.Id == player.PlayerGuid);
                long geneticVector = 0;
                if (charRecord != null)
                {
                    var lineage = await context.CharacterLineages.FirstOrDefaultAsync(l => l.CharacterId == charRecord.Id);
                    if (lineage != null)
                    {
                        geneticVector = lineage.GeneticVector;
                    }
                }

                var gv = new GeneticVector(geneticVector);
                byte race = gv.LocusRace.Dominant;

                int quantityProduced = 1;

                // Kobold passive: 10% chance to duplicate bar outcome in smelting (Prof 2)
                if (recipe.ProfessionType == 2 && race == RaceIds.Kobold)
                {
                    if (Random.Shared.Next(100) < 10)
                    {
                        quantityProduced++;
                    }
                }

                // Vodník passive has been moved to item metadata payload in ExecuteEquipmentCraftingAsync

                // Modul: Full-Stack Expansion, Part 3. Check and deduct
                // materials through the unified Backpack+Stash interface -
                // availability is the combined balance, the Backpack drains
                // first, and the remainder comes seamlessly out of the
                // Village Stash inside this same Serializable transaction.
                if (recipe.Mat1Id > 0 && recipe.Mat1Count > 0)
                {
                    string mat1ItemId = recipe.Mat1Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!await InventoryAndStashSystem.TryConsumeUnifiedAsync(context, playerId, mat1ItemId, recipe.Mat1Count))
                    {
                        await transaction.RollbackAsync();
                        return (false, 0);
                    }
                }

                if (recipe.Mat2Id > 0 && recipe.Mat2Count > 0)
                {
                    string mat2ItemId = recipe.Mat2Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!await InventoryAndStashSystem.TryConsumeUnifiedAsync(context, playerId, mat2ItemId, recipe.Mat2Count))
                    {
                        await transaction.RollbackAsync();
                        return (false, 0);
                    }
                }

                await context.SaveChangesAsync();
                await transaction.CommitAsync();
                return (true, quantityProduced);
            });

            if (success)
            {
                // Enqueue completion
                _playerRegistry.CraftingCompletionQueue.Enqueue(new CraftingCompletionNotification
                {
                    PlayerId = playerId,
                    CraftedItemId = recipe.ResultItemId,
                    Quantity = quantityProduced
                });
            }
        }

        public async Task ExecuteEquipmentCraftingAsync(long playerId, uint recipeId, uint slotIndex, uint tickToken)
        {
            if (!CraftingReceptuary.TryGetRecipe((int)recipeId, out var recipe))
            {
                return;
            }

            await using var context = new FolkIdleDbContext(_retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            // Modul: same result-tuple pattern as ExecuteCraftingAsync - the
            // delegate returns the outcome instead of throwing for the
            // expected "insufficient materials" case, and no longer
            // swallows exceptions itself, so a Serializable conflict
            // propagates out for CreateExecutionStrategy to retry.
            (bool success, long guildId, long guildWarMatchId, int guildWarPoints) = await strategy.ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                // Modul: Full-Stack Expansion, Part 3. Unified
                // Backpack+Stash availability check and Backpack-first
                // consumption - see InventoryAndStashSystem.
                string materialItemId = ContentRegistry.GetMaterialString(recipe.MaterialId);

                // Lock crafting slot
                var slotRows = await context.PlayerCraftingSlots.FromSqlInterpolated($"SELECT * FROM \"PlayerCraftingSlots\" WHERE \"PlayerId\" = {playerId} AND \"SlotIndex\" = {(int)slotIndex} FOR UPDATE").ToListAsync();

                if (!await InventoryAndStashSystem.TryConsumeUnifiedAsync(context, playerId, materialItemId, recipe.MaterialCost))
                {
                    await transaction.RollbackAsync();
                    return (false, 0L, 0L, 0);
                }

                var player = await context.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
                long geneticVector = 0;
                if (player != null)
                {
                    var charRecord = await context.CharacterRecords.FirstOrDefaultAsync(c => c.PlayerId == playerId && c.Id == player.PlayerGuid);
                    if (charRecord != null)
                    {
                        var lineage = await context.CharacterLineages.FirstOrDefaultAsync(l => l.CharacterId == charRecord.Id);
                        if (lineage != null) geneticVector = lineage.GeneticVector;
                    }
                }
                var gv = new GeneticVector(geneticVector);
                byte race = gv.LocusRace.Dominant;

                // Generate item with AsNoTracking/batch write
                var item = EquipmentGenerator.GenerateEquipment(playerId, tickToken, recipe, slotIndex);

                // Modul: Full-Stack Expansion, Part 4. Crafted rarity roll -
                // the recipe's TierIndex is the guaranteed floor, and the
                // zero-allocation stackalloc roll can upgrade the outcome
                // toward higher tiers, weighted by the crafter's workshop
                // (Forge) level. The result is clamped to the item's
                // structural gear-band forge cap, so a low-band recipe can
                // never roll past the same ceiling ForgeSplicingEngine
                // enforces on fusion.
                int workshopLevel = await context.VillageInfrastructures
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId && v.BuildingId == VillageManagementEngine.ForgeBuildingId)
                    .Select(v => (int?)v.CurrentLevel)
                    .SingleOrDefaultAsync() ?? 0;

                int rarityBonus = RollCraftedRarity(0, workshopLevel, Random.Shared.NextDouble());
                if (rarityBonus > 0)
                {
                    int bandCap = ForgeSplicingEngine.MaxQualityTier;
                    if (ContentRegistry.TryGetItemDefinitionByBaseId(recipe.ResultBaseItemId, out var craftedDefinition))
                    {
                        bandCap = GetMaxForgeTierForRegion(craftedDefinition.RegionTier);
                    }
                    item.QualityTier = Math.Min(Math.Min(bandCap, ForgeSplicingEngine.MaxQualityTier), item.QualityTier + rarityBonus);
                }

                var affixes = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, int>>(item.AffixPayload) ?? new System.Collections.Generic.Dictionary<string, int>();

                // Vodník cooking passive (Prof 4)
                if (recipe.ProfessionType == 4 && race == RaceIds.Vodnik)
                {
                    if (Random.Shared.Next(100) < 15)
                    {
                        affixes["HealingMultiplier"] = 150;
                    }
                }

                // Moosleute alchemy passive (Prof 5)
                if (recipe.ProfessionType == 5 && race == RaceIds.Moosleute)
                {
                    affixes["PotencyMultiplier"] = 110;
                }

                item.AffixPayload = System.Text.Json.JsonSerializer.Serialize(affixes);

                context.EquipmentInstances.Add(item);

                // Modul 06/26: Guild War Production Logistics front (WP = 50 *
                // ItemRarityTier) for items scaling above Tier 5 (Epic), crafted
                // by a member of a guild currently in an active war match.
                GuildWarMatch? activeGuildWarMatch = null;
                if (recipe.TierIndex > 5 && player != null && player.GuildId > 0 && _guildWarEngine != null)
                {
                    activeGuildWarMatch = await context.GuildWarMatches
                        .AsNoTracking()
                        .FirstOrDefaultAsync(m => m.IsActive && (m.GuildA_Id == player.GuildId || m.GuildB_Id == player.GuildId));
                }

                await context.SaveChangesAsync();
                await transaction.CommitAsync();

                if (activeGuildWarMatch != null && player != null)
                {
                    return (true, player.GuildId, activeGuildWarMatch.MatchId, 50 * recipe.TierIndex);
                }

                return (true, 0L, 0L, 0);
            });

            if (!success)
            {
                return;
            }

            if (guildWarMatchId != 0L && _guildWarEngine != null)
            {
                _guildWarEngine.GuildWarPointQueue.Enqueue(new GuildWarPointEvent
                {
                    MatchId = guildWarMatchId,
                    GuildId = guildId,
                    Front = 1,
                    Points = guildWarPoints
                });
            }

            // Notification queue pattern to avoid state mutation race
            _playerRegistry.CraftingCompletionQueue.Enqueue(new CraftingCompletionNotification
            {
                PlayerId = playerId,
                CraftedItemId = recipe.RecipeId,
                Quantity = 1
            });
        }
    }
}
