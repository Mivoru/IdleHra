using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Economy
{
    /// <summary>Where a loot chest came from (CHESTS_DUNGEONS_DIAMONDS.md section 1.3).</summary>
    public enum LootChestSource : byte
    {
        Dev = 0,
        DeepFloor = 1,
        BossFirstClear = 2,
    }

    /// <summary>
    /// Granting and opening loot chests. The rules are LootChestRules; this
    /// writes them, on the REST path like the Delve.
    /// </summary>
    /// <remarks>
    /// Modul: ONE TRANSACTION PER OPENING, AND THE ROW IS THE TICKET. The
    /// chest row is locked and deleted in the transaction that writes its
    /// contents, so a double tap, a retried request or two devices find no
    /// row the second time and grant nothing (section 1.4).
    ///
    /// Modul: GOLD GOES TO THE "gold" COMMODITY ROW, as the Delve's does - the
    /// row is the wallet's authority and the tick only adds its pending
    /// deltas to it - and to the income ledger as LootChest, so a chest's
    /// gold shows up in the gold report beside every other tap.
    ///
    /// Modul: CHEST EQUIPMENT IS NEVER AUTO-SALVAGED. A player who opens a
    /// chest sees what was in it (section 1.4); the piece goes straight into
    /// the chest, whatever the salvage rule says.
    /// </remarks>
    public static class LootChestEngine
    {
        /// <summary>The chests' own weekly diamond ceiling (build plan, section 1.4a).</summary>
        public const int MaxDiamondsPerWeek = 15;

        /// <summary>
        /// Gold per minute below which a chest's gold is never priced: a new
        /// account has almost no income history, and a chest of nothing teaches
        /// the wrong thing. A first cut by region, to re-measure.
        /// </summary>
        private static readonly double[] GoldPerMinuteFloorByRegion = { 100, 300, 900, 2700, 8000 };

        public sealed record Opened(long ChestId, LootChestTier Tier, int Region, LootChestRoll Roll, OpenedPiece? Piece);

        public sealed record OpenedPiece(long InstanceId, string BaseItemId, int QualityTier, string RarityName);

        public static async Task<long> GrantAsync(FolkIdleDbContext db, long playerId, LootChestTier tier, int region, LootChestSource source, DateTime utcNow)
        {
            var row = new LootChest
            {
                PlayerId = playerId,
                Tier = (byte)Math.Clamp((int)tier, 1, 4),
                Region = (byte)Math.Clamp(region, 1, 5),
                Source = (byte)source,
                GrantedAtUtc = utcNow,
            };
            db.LootChests.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        }

        /// <summary>Unopened chests by tier, for the shelf.</summary>
        public static async Task<Dictionary<LootChestTier, int>> CountsAsync(FolkIdleDbContext db, long playerId)
        {
            var rows = await db.LootChests.AsNoTracking()
                .Where(c => c.PlayerId == playerId)
                .GroupBy(c => c.Tier)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync();
            return rows.ToDictionary(r => (LootChestTier)r.Key, r => r.Count);
        }

        /// <summary>
        /// Opens up to <paramref name="count"/> chests of one tier, oldest first.
        /// Answers what came out of each; an empty list means there was none.
        /// </summary>
        public static async Task<List<Opened>> OpenAsync(
            FolkIdleDbContext db, long playerId, LootChestTier tier, int count, DateTime utcNow, Random rng)
        {
            var opened = new List<Opened>();
            var pieces = new List<(LootChest Chest, LootChestRoll Roll, EquipmentInstance? Piece)>();
            count = Math.Clamp(count, 1, 10);

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var chests = await db.LootChests
                .FromSqlRaw(
                    "SELECT * FROM \"LootChests\" WHERE \"PlayerId\" = {0} AND \"Tier\" = {1} ORDER BY \"Id\" LIMIT {2} FOR UPDATE SKIP LOCKED",
                    playerId, (short)tier, count)
                .ToListAsync();
            if (chests.Count == 0)
            {
                await tx.RollbackAsync();
                return opened;
            }

            int highest = await HighestRegionAsync(db, playerId);
            double perMinute = await GoldPerMinuteAsync(db, playerId, highest, utcNow);
            var week = await LockWeekAsync(db, playerId, utcNow);

            long goldTotal = 0;
            foreach (var chest in chests)
            {
                int room = Math.Max(0, MaxDiamondsPerWeek - week.DiamondsThisWeek);
                var roll = LootChestRules.Roll((LootChestTier)chest.Tier, chest.Region, highest, perMinute, room, rng);

                foreach (var (item, quantity) in roll.Materials)
                {
                    await CommodityLedger.AddAsync(db, playerId, item, quantity);
                }

                goldTotal += roll.Gold + roll.GoldInsteadOfDiamonds;
                if (roll.Diamonds > 0)
                {
                    week.DiamondsThisWeek += roll.Diamonds;
                    await db.PlayerRecords.Where(p => p.Id == playerId)
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.PremiumDiamonds, p => p.PremiumDiamonds + roll.Diamonds));
                }

                EquipmentInstance? piece = roll.Equipment ? AddPiece(db, playerId, chest.Region, roll.EquipmentRarityFloor, rng) : null;

                db.LootChests.Remove(chest);
                pieces.Add((chest, roll, piece));
            }

            if (goldTotal > 0)
            {
                await CommodityLedger.AddAsync(db, playerId, "gold", goldTotal);
                await GoldLedger.RecordIncomeAsync(db, playerId, GoldIncomeSource.LootChest, goldTotal);
            }

            await db.SaveChangesAsync();
            await tx.CommitAsync();

            // Built after the save, because a piece's id exists only then.
            foreach (var (chest, roll, piece) in pieces)
            {
                opened.Add(new Opened(chest.Id, (LootChestTier)chest.Tier, chest.Region, roll,
                    piece == null ? null : new OpenedPiece(piece.Id, piece.BaseItemId, piece.QualityTier, RarityTier.GetName(piece.QualityTier))));
            }
            return opened;
        }

        private static EquipmentInstance? AddPiece(FolkIdleDbContext db, long playerId, int region, int rarityFloor, Random rng)
        {
            var pool = WorkshopCommissionRules.CommissionableItemIds()
                .Where(id => ContentRegistry.ItemDefinitions[id - 1].RegionTier == region)
                .ToList();
            if (pool.Count == 0) return null;

            string baseItemId = ContentRegistry.GetItemBaseId(pool[rng.Next(pool.Count)]);
            int tier = WorkshopCommissionRules.ResolveTier(rarityFloor, rng);
            var affixes = new Dictionary<string, int>();
            AffixRegistry.RollAffixes(baseItemId, region, tier, RarityTier.GetAffixCount(tier), affixes);

            var instance = new EquipmentInstance
            {
                BaseItemId = baseItemId,
                PlayerId = playerId,
                QualityTier = tier,
                AffixPayload = JsonSerializer.Serialize(affixes),
                IsAffixLocked = false,
            };
            db.EquipmentInstances.Add(instance);
            return instance;
        }

        private static async Task<LootChestWeek> LockWeekAsync(FolkIdleDbContext db, long playerId, DateTime utcNow)
        {
            int weekKey = DelveEngine.CurrentWeekKey(utcNow);
            var week = await db.LootChestWeeks
                .FromSqlRaw("SELECT * FROM \"LootChestWeeks\" WHERE \"PlayerId\" = {0} FOR UPDATE", playerId)
                .SingleOrDefaultAsync();
            if (week == null)
            {
                week = new LootChestWeek { PlayerId = playerId, WeekKey = weekKey };
                db.LootChestWeeks.Add(week);
            }
            else if (week.WeekKey != weekKey)
            {
                week.WeekKey = weekKey;
                week.DiamondsThisWeek = 0;
            }
            return week;
        }

        private static async Task<int> HighestRegionAsync(FolkIdleDbContext db, long playerId)
        {
            var defeated = await RegionUnlockGate.LoadDefeatedBossesAsync(db, playerId);
            return Math.Clamp(RegionUnlockGate.HighestUnlockedRegion(defeated), 1, 5);
        }

        /// <summary>
        /// The player's own gold per minute over the last seven days of the
        /// income ledger, never below the region's floor.
        /// </summary>
        private static async Task<double> GoldPerMinuteAsync(FolkIdleDbContext db, long playerId, int highestRegion, DateTime utcNow)
        {
            var since = DateOnly.FromDateTime(utcNow.AddDays(-7));
            short chestSource = (short)GoldIncomeSource.LootChest;
            long earned = await db.GoldIncomeDaily.AsNoTracking()
                .Where(g => g.PlayerId == playerId && g.Day >= since && g.Source != chestSource)
                .SumAsync(g => (long?)g.Amount) ?? 0L;
            double measured = earned / (7.0 * 24 * 60);
            return Math.Max(measured, GoldPerMinuteFloorByRegion[highestRegion - 1]);
        }
    }
}
