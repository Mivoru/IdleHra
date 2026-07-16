using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Engine
{
    public class GuildLogisticsEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;

        public GuildLogisticsEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public async Task ExecuteGuildContributionAsync(long playerId, long guildId, long quantity, int itemDefinitionId)
        {
            if (quantity <= 0)
            {
                Console.WriteLine("Invalid contribution quantity.");
                return;
            }

            string materialName = ContentRegistry.GetMaterialString(itemDefinitionId);
            if (materialName == "unknown")
            {
                Console.WriteLine("Invalid material definition for contribution.");
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                // Verify guild membership securely - read-only verification is fine, but player record has guildId
                var playerQuery = "SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0}";
                var player = await db.PlayerRecords.FromSqlRaw(playerQuery, playerId).SingleOrDefaultAsync();

                if (player == null || player.GuildId != guildId)
                {
                    Console.WriteLine("Contribution failed: Invalid guild membership.");
                    return;
                }

                // Lock only the player's stock row
                var commodityQuery = "SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1} FOR UPDATE";
                var commodity = await db.CommodityRecords.FromSqlRaw(commodityQuery, playerId, materialName).SingleOrDefaultAsync();

                if (commodity == null || commodity.Quantity < quantity)
                {
                    Console.WriteLine("Contribution failed: Insufficient resources.");
                    return;
                }

                // Deduct material
                commodity.Quantity -= quantity;

                // Atomic addition for GuildDepotBalances (Upsert pattern since it might not exist)
                var upsertDepotQuery = @"
                    INSERT INTO ""GuildDepotBalances"" (""GuildId"", ""ItemDefinitionId"", ""Quantity"")
                    VALUES ({0}, {1}, {2})
                    ON CONFLICT (""GuildId"", ""ItemDefinitionId"")
                    DO UPDATE SET ""Quantity"" = ""GuildDepotBalances"".""Quantity"" + {2};
                ";
                await db.Database.ExecuteSqlRawAsync(upsertDepotQuery, guildId, itemDefinitionId, quantity);

                // Handle Monolith logic
                await ApplyMonolithProgressionAsync(db, guildId, itemDefinitionId, quantity);

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Guild contribution failed: {ex.Message}");
            }
        }

        private async Task ApplyMonolithProgressionAsync(FolkIdleDbContext db, long guildId, int itemDefinitionId, long quantity)
        {
            // Modul: metadata-driven classification via
            // ContentRegistry.GetMaterialProfessionType - replaces the
            // previous itemDefinitionId % 2 != 0 parity heuristic, which
            // broke silently if this material id space were ever
            // renumbered. See that method's own comment for the explicit
            // per-material mapping.
            bool isMining = ContentRegistry.GetMaterialProfessionType(itemDefinitionId) == GatheringProfessionType.Mining;

            string progressColumn = isMining ? "MiningMonolithProgress" : "WoodcuttingMonolithProgress";
            string levelColumn = isMining ? "MiningMonolithLevel" : "WoodcuttingMonolithLevel";

            // Atomic progress addition
            var addProgressQuery = $@"
                UPDATE ""GuildRecords""
                SET ""{progressColumn}"" = ""{progressColumn}"" + {{1}}
                WHERE ""Id"" = {{0}};
            ";
            await db.Database.ExecuteSqlRawAsync(addProgressQuery, guildId, quantity);

            // Fetch to check level up
            var guildCheckQuery = "SELECT * FROM \"GuildRecords\" WHERE \"Id\" = {0}";
            var guild = await db.GuildRecords.FromSqlRaw(guildCheckQuery, guildId).SingleOrDefaultAsync();

            if (guild != null)
            {
                int currentLevel = isMining ? guild.MiningMonolithLevel : guild.WoodcuttingMonolithLevel;
                long currentProgress = isMining ? guild.MiningMonolithProgress : guild.WoodcuttingMonolithProgress;

                long requiredProgress = (long)Math.Floor(1000 * currentLevel * 1.45);
                if (currentLevel == 0) requiredProgress = 1000; // Base requirement for level 1

                if (currentProgress >= requiredProgress && currentLevel < 50)
                {
                    var levelUpQuery = $@"
                        UPDATE ""GuildRecords""
                        SET ""{levelColumn}"" = ""{levelColumn}"" + 1,
                            ""{progressColumn}"" = ""{progressColumn}"" - {{1}}
                        WHERE ""Id"" = {{0}};
                    ";
                    await db.Database.ExecuteSqlRawAsync(levelUpQuery, guildId, requiredProgress);
                    
                    currentLevel++;
                    Console.WriteLine($"Guild {guildId} Monolith Level Up! New Level: {currentLevel}");

                    // Enqueue notification for 10Hz loop sync
                    _playerRegistry.EnqueueGuildUpdate(new GuildUpdateNotification 
                    {
                        GuildId = guildId,
                        IsMining = isMining,
                        NewLevel = currentLevel
                    });
                }
            }
        }
    }
}
