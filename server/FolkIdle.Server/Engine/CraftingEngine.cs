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

                // Check and deduct mats
                if (recipe.Mat1Id > 0 && recipe.Mat1Count > 0)
                {
                    // Use FOR UPDATE for row-level locking
                    string mat1ItemId = recipe.Mat1Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var mat1Rows = await context.CommodityRecords.FromSqlInterpolated($"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = {mat1ItemId} FOR UPDATE").ToListAsync();
                    var mat1 = mat1Rows.Count > 0 ? mat1Rows[0] : null;

                    if (mat1 == null || mat1.Quantity < recipe.Mat1Count)
                    {
                        await transaction.RollbackAsync();
                        return (false, 0);
                    }

                    mat1.Quantity -= recipe.Mat1Count;
                    context.CommodityRecords.Update(mat1);
                }

                if (recipe.Mat2Id > 0 && recipe.Mat2Count > 0)
                {
                    string mat2ItemId = recipe.Mat2Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var mat2Rows = await context.CommodityRecords.FromSqlInterpolated($"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = {mat2ItemId} FOR UPDATE").ToListAsync();
                    var mat2 = mat2Rows.Count > 0 ? mat2Rows[0] : null;

                    if (mat2 == null || mat2.Quantity < recipe.Mat2Count)
                    {
                        await transaction.RollbackAsync();
                        return (false, 0);
                    }

                    mat2.Quantity -= recipe.Mat2Count;
                    context.CommodityRecords.Update(mat2);
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

                // Lock commodity
                string materialItemId = ContentRegistry.GetMaterialString(recipe.MaterialId);
                var commodityRows = await context.CommodityRecords.FromSqlInterpolated($"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = {materialItemId} FOR UPDATE").ToListAsync();
                var mat = commodityRows.Count > 0 ? commodityRows[0] : null;

                if (mat == null || mat.Quantity < recipe.MaterialCost)
                {
                    await transaction.RollbackAsync();
                    return (false, 0L, 0L, 0);
                }

                // Lock crafting slot
                var slotRows = await context.PlayerCraftingSlots.FromSqlInterpolated($"SELECT * FROM \"PlayerCraftingSlots\" WHERE \"PlayerId\" = {playerId} AND \"SlotIndex\" = {(int)slotIndex} FOR UPDATE").ToListAsync();

                // Deduct materials
                mat.Quantity -= recipe.MaterialCost;
                context.CommodityRecords.Update(mat);

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
