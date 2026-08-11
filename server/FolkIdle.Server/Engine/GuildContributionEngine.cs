using System;
using System.Data;
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
    public class GuildContributionEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry? _playerRegistry;

        public GuildContributionEngine(IServiceProvider serviceProvider, PlayerSessionRegistry? playerRegistry = null)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
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
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.TargetNotFound);
                    return;
                }

                // Modul: sourced from GameData/GameBalanceConfig.json - see
                // GuildRaidEngine's identical rationale.
                long expValue = (equip.QualityTier + 1) * ContentRegistry.Balance.GuildContributionEquipmentExpPerTier;

                // Delete item to create deflationary sink
                db.MarketEquipmentInstances.Remove(equip);

                await ApplyGuildExperienceAsync(db, guildId, expValue);

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);
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
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.InsufficientGold);
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

                // Modul: Comprehensive Game System Audit, Part 3.1. Gold
                // contributions previously updated only the guild-level
                // sink ledger and guild experience - never the
                // contributing member's own ContributionPoints, so the
                // roster's contribution ranking (HandleGuildRoster orders
                // by ContributionPoints desc) reflected raid victories but
                // was blind to gold donations. Same raw-SQL increment
                // pattern GuildRaidEngine already uses, inside this
                // method's existing Serializable transaction. Points scale
                // with the same divisor as guild experience so both
                // rankings share one unit of account.
                long contributionPoints = goldAmount / ContentRegistry.Balance.GuildContributionGoldToExpDivisor;
                if (contributionPoints > 0)
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE \"GuildMembers\" SET \"ContributionPoints\" = \"ContributionPoints\" + {0} WHERE \"GuildId\" = {1} AND \"PlayerId\" = {2}",
                        contributionPoints, guildId, playerId);
                }

                // Modul: sourced from GameData/GameBalanceConfig.json.
                long expValue = goldAmount / ContentRegistry.Balance.GuildContributionGoldToExpDivisor; // e.g. 10g = 1 exp
                await ApplyGuildExperienceAsync(db, guildId, expValue);

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);
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
            public async Task<bool> ContributeDepotMaterialAsync(long playerId, long guildId, string itemId, int quantity)
        {
            if (quantity <= 0) return false;

            if (itemId == "gold")
            {
                using var scope_gold = _serviceProvider.CreateScope();
                var db_gold = scope_gold.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                using var transaction_gold = await db_gold.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                try
                {
                    var recordQuery = "SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1} FOR UPDATE";
                    var playerCommodity = await db_gold.CommodityRecords.FromSqlRaw(recordQuery, playerId, itemId).SingleOrDefaultAsync();
                    if (playerCommodity == null || playerCommodity.Quantity < quantity) return false;
                    
                    playerCommodity.Quantity -= quantity;
                    if (playerCommodity.Quantity <= 0)
                    {
                        db_gold.CommodityRecords.Remove(playerCommodity);
                    }

                    var guildQuery = "SELECT * FROM \"GuildRecords\" WHERE \"Id\" = {0} FOR UPDATE";
                    var guild = await db_gold.GuildRecords.FromSqlRaw(guildQuery, guildId).SingleOrDefaultAsync();
                    if (guild == null) return false;

                    // Add to treasury gold (separate from guild XP/tier system)
                    guild.GuildTreasuryGold += quantity;

                    // Gold donations do NOT count toward weekly material contribution leaderboard
                    // They are tracked separately via TotalGoldContributed for guild XP
                    long goldExp = quantity / ContentRegistry.Balance.GuildContributionGoldToExpDivisor;
                    if (goldExp > 0) await ApplyGuildExperienceAsync(db_gold, guildId, goldExp);

                    await db_gold.SaveChangesAsync();
                    await transaction_gold.CommitAsync();
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);
                    return true;
                }
                catch (System.Exception ex)
                {
                    await transaction_gold.RollbackAsync();
                    System.Console.WriteLine($"Depot gold contribution failed: {ex.Message}");
                    return false;
                }
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var recordQuery = "SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1} FOR UPDATE";
                var playerCommodity = await db.CommodityRecords.FromSqlRaw(recordQuery, playerId, itemId).SingleOrDefaultAsync();

                if (playerCommodity == null || playerCommodity.Quantity < quantity)
                {
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.TargetNotFound);
                    return false;
                }

                // Verify it's a valid material and get its tier/weight
                if (!ContentRegistry.TryGetItemDefinitionByBaseId(itemId, out var def)) return false;
                
                int itemTier = def.RegionTier;
                int dropWeight = ContentRegistry.GetMaterialDropWeight(itemId); // We need to create this helper

                // Dedup inventory
                playerCommodity.Quantity -= quantity;
                if (playerCommodity.Quantity <= 0)
                {
                    db.CommodityRecords.Remove(playerCommodity);
                }

                // Add to Guild Depot
                var depotQuery = "SELECT * FROM \"GuildDepotBalances\" WHERE \"GuildId\" = {0} AND \"ItemDefinitionId\" = {1} FOR UPDATE";
                var depotRecord = await db.GuildDepotBalances.FromSqlRaw(depotQuery, guildId, def.Id).SingleOrDefaultAsync();
                
                if (depotRecord == null)
                {
                    depotRecord = new GuildDepotBalance { GuildId = guildId, ItemDefinitionId = def.Id, Quantity = 0 };
                    db.GuildDepotBalances.Add(depotRecord);
                }
                depotRecord.Quantity += quantity;

                // Calculate Weekly Contribution Points
                // Base 1 point per item. If item drop weight is 10 (10%), it gives 90/10 = 9 points. (assuming 90 is common weight)
                // We'll normalize points: Point = quantity * (100 / dropWeight) * itemTier
                int points = quantity * (100 / Math.Max(1, dropWeight)) * Math.Max(1, itemTier);

                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"GuildMembers\" SET \"WeeklyContributionPoints\" = \"WeeklyContributionPoints\" + {0} WHERE \"GuildId\" = {1} AND \"PlayerId\" = {2}",
                    points, guildId, playerId);

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);
                return true;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Depot contribution failed: {ex.Message}");
                return false;
            }
        }

        // Buff tier material definitions - (commonWoodBaseId, rareWoodBaseId, commonOreBaseId, rareOreBaseId)
        private static readonly (string CommonWood, string RareWood, string CommonOre, string RareOre)[] BuffTierMaterials = new[]
        {
            ("birch_log",      "golden_birch_log",    "copper_ore",    "malachite_ore"),  // Tier 1 - Sunlit Plains
            ("willow_log",     "golden_willow_log",   "iron_ore",      "hematite_ore"),   // Tier 2 - Whispering Woods
            ("acacia_log",     "golden_acacia_log",   "sulfur_ore",    "obsidian_ore"),   // Tier 3 - Scorched Wasteland
            ("frostpine_log",  "golden_frostpine_log","silver_ore",    "cobalt_ore"),     // Tier 4 - Frozen Peaks
            ("ebon_log",       "golden_ebon_log",     "darksteel_ore", "astralite_ore"),  // Tier 5 - Shadow Citadel
        };

        private const int BuffMaterialCostPerType = 25_000; // 25k wood + 25k ore = 50k total

        public async Task<bool> ActivateGuildBuffAsync(long playerId, long guildId, string buffType, int tier, string path)
        {
            // path = "common" or "rare"
            // tier = 1-5
            if (tier < 1 || tier > 5) return false;
            if (path != "common" && path != "rare") return false;
            
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                // Verify officer/leader
                var memberQuery = "SELECT * FROM \"GuildMembers\" WHERE \"GuildId\" = {0} AND \"PlayerId\" = {1}";
                var member = await db.GuildMembers.FromSqlRaw(memberQuery, guildId, playerId).SingleOrDefaultAsync();
                if (member == null || member.Role == 0) return false; // Members cannot activate buffs

                var tierDef = BuffTierMaterials[tier - 1];
                string woodId = path == "rare" ? tierDef.RareWood : tierDef.CommonWood;
                string oreId  = path == "rare" ? tierDef.RareOre  : tierDef.CommonOre;

                // Check wood balance in depot
                if (!ContentRegistry.TryGetItemDefinitionByBaseId(woodId, out var woodDef)) return false;
                if (!ContentRegistry.TryGetItemDefinitionByBaseId(oreId,  out var oreDef))  return false;

                var woodDepotQ = "SELECT * FROM \"GuildDepotBalances\" WHERE \"GuildId\" = {0} AND \"ItemDefinitionId\" = {1} FOR UPDATE";
                var woodRecord = await db.GuildDepotBalances.FromSqlRaw(woodDepotQ, guildId, woodDef.Id).SingleOrDefaultAsync();
                var oreRecord  = await db.GuildDepotBalances.FromSqlRaw(woodDepotQ, guildId, oreDef.Id).SingleOrDefaultAsync();

                if (woodRecord == null || woodRecord.Quantity < BuffMaterialCostPerType) return false;
                if (oreRecord  == null || oreRecord.Quantity  < BuffMaterialCostPerType) return false;

                // Consume materials
                woodRecord.Quantity -= BuffMaterialCostPerType;
                oreRecord.Quantity  -= BuffMaterialCostPerType;

                // Duration: common = 1h, rare = 9h
                TimeSpan duration = path == "rare" ? TimeSpan.FromHours(9) : TimeSpan.FromHours(1);

                // Apply/extend/upgrade buff
                var buffQuery = "SELECT * FROM \"GuildActiveBuffs\" WHERE \"GuildId\" = {0} AND \"BuffType\" = {1} FOR UPDATE";
                var activeBuff = await db.GuildActiveBuffs.FromSqlRaw(buffQuery, guildId, buffType).SingleOrDefaultAsync();
                
                if (activeBuff == null)
                {
                    activeBuff = new GuildActiveBuff
                    {
                        GuildId  = guildId,
                        BuffType = buffType,
                        Tier     = tier,
                        ExpiresAt = DateTime.UtcNow.Add(duration)
                    };
                    db.GuildActiveBuffs.Add(activeBuff);
                }
                else
                {
                    if (tier > activeBuff.Tier)
                    {
                        activeBuff.Tier = tier;
                        activeBuff.ExpiresAt = DateTime.UtcNow.Add(duration);
                    }
                    else if (tier == activeBuff.Tier)
                    {
                        activeBuff.ExpiresAt = activeBuff.ExpiresAt < DateTime.UtcNow
                            ? DateTime.UtcNow.Add(duration)
                            : activeBuff.ExpiresAt.Add(duration);
                    }
                    else
                    {
                        return false; // Cannot downgrade buff tier
                    }
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                GuildBonusesCache.MarkGuildDirty(guildId);
                return true;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Buff activation failed: {ex.Message}");
                return false;
            }
        }


    }
}
