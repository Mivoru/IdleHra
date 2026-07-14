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

        public CraftingEngine(IDbContextFactory<FolkIdleDbContext> contextFactory, PlayerSessionRegistry playerRegistry)
        {
            _contextFactory = contextFactory;
            _playerRegistry = playerRegistry;
        }

        public async Task ExecuteCraftingAsync(long playerId, int recipeResultItemId)
        {
            if (!ContentRegistry.TryGetRecipe(recipeResultItemId, out var recipe))
            {
                return;
            }

            using var context = await _contextFactory.CreateDbContextAsync();
            using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                var player = await context.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
                if (player == null) return;

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
                        return;
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
                        return;
                    }

                    mat2.Quantity -= recipe.Mat2Count;
                    context.CommodityRecords.Update(mat2);
                }

                await context.SaveChangesAsync();
                await transaction.CommitAsync();

                // Enqueue completion
                _playerRegistry.CraftingCompletionQueue.Enqueue(new CraftingCompletionNotification
                {
                    PlayerId = playerId,
                    CraftedItemId = recipe.ResultItemId,
                    Quantity = quantityProduced
                });
            }
            catch
            {
                await transaction.RollbackAsync();
            }
        }

        public async Task ExecuteEquipmentCraftingAsync(long playerId, uint recipeId, uint slotIndex, uint tickToken)
        {
            if (!CraftingReceptuary.TryGetRecipe((int)recipeId, out var recipe))
            {
                return;
            }

            using var context = await _contextFactory.CreateDbContextAsync();
            using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                // Lock commodity
                string materialItemId = ContentRegistry.GetMaterialString(recipe.MaterialId);
                var commodityRows = await context.CommodityRecords.FromSqlInterpolated($"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = {materialItemId} FOR UPDATE").ToListAsync();
                var mat = commodityRows.Count > 0 ? commodityRows[0] : null;

                if (mat == null || mat.Quantity < recipe.MaterialCost)
                {
                    await transaction.RollbackAsync();
                    return;
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

                await context.SaveChangesAsync();
                await transaction.CommitAsync();

                // Notification queue pattern to avoid state mutation race
                _playerRegistry.CraftingCompletionQueue.Enqueue(new CraftingCompletionNotification
                {
                    PlayerId = playerId,
                    CraftedItemId = recipe.RecipeId,
                    Quantity = 1
                });
            }
            catch
            {
                await transaction.RollbackAsync();
            }
        }
    }
}
