using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Engine
{
    public class GuildContributionEngine
    {
        private readonly IServiceProvider _serviceProvider;

        public GuildContributionEngine(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task ContributeEquipmentAsync(long playerId, long guildId, long equipmentInstanceId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var equipQuery = "SELECT * FROM \"MarketEquipmentInstances\" WHERE \"Id\" = {0} FOR UPDATE";
                var equip = await db.MarketEquipmentInstances.FromSqlRaw(equipQuery, equipmentInstanceId).SingleOrDefaultAsync();

                if (equip == null || equip.PlayerId != playerId || equip.IsLockedInEscrow)
                {
                    Console.WriteLine("Contribution failed: Equipment unavailable.");
                    return;
                }

                long expValue = (equip.QualityTier + 1) * 100;

                // Delete item to create deflationary sink
                db.MarketEquipmentInstances.Remove(equip);

                await ApplyGuildExperienceAsync(db, guildId, expValue);

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Guild contribution failed: {ex.Message}");
            }
        }

        public async Task ContributeGoldAsync(long playerId, long guildId, long goldAmount)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var goldQuery = "SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE";
                var goldRecord = await db.CommodityRecords.FromSqlRaw(goldQuery, playerId).SingleOrDefaultAsync();

                if (goldRecord == null || goldRecord.Quantity < goldAmount)
                {
                    Console.WriteLine("Contribution failed: Insufficient gold.");
                    return;
                }

                // Delete gold to create deflationary sink
                goldRecord.Quantity -= goldAmount;

                var ledgerQuery = "SELECT * FROM \"GuildMaterialSinkLedgers\" WHERE \"GuildId\" = {0} AND \"CommodityId\" = 'gold' FOR UPDATE";
                var ledger = await db.GuildMaterialSinkLedgers.FromSqlRaw(ledgerQuery, guildId).SingleOrDefaultAsync();
                
                if (ledger == null)
                {
                    ledger = new GuildMaterialSinkLedger { GuildId = guildId, CommodityId = "gold", TotalAmountContributed = 0 };
                    db.GuildMaterialSinkLedgers.Add(ledger);
                }
                
                ledger.TotalAmountContributed += goldAmount;

                long expValue = goldAmount / 10; // e.g. 10g = 1 exp
                await ApplyGuildExperienceAsync(db, guildId, expValue);

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Guild gold contribution failed: {ex.Message}");
            }
        }

        private async Task ApplyGuildExperienceAsync(FolkIdleDbContext db, long guildId, long expAmount)
        {
            var guildQuery = "SELECT * FROM \"GuildRecords\" WHERE \"Id\" = {0} FOR UPDATE";
            var guild = await db.GuildRecords.FromSqlRaw(guildQuery, guildId).SingleOrDefaultAsync();

            if (guild != null)
            {
                guild.TotalGoldContributed += expAmount; // Reusing this column as 'Experience' proxy
                long requiredExp = (guild.CurrentTier + 1) * 1000;
                
                if (guild.TotalGoldContributed >= requiredExp)
                {
                    guild.CurrentTier++;
                    guild.TotalGoldContributed -= requiredExp;
                    Console.WriteLine($"Guild Level Up! New Tier: {guild.CurrentTier}");
                    
                    // Update the global static cache for the 10Hz tick simulation
                    GuildBonusesCache.UpdateGuildTier(guildId, guild.CurrentTier);
                }
            }
        }
    }
}
