using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The village chest: the one place everything a character produces ends
    /// up, and the only place anything is ever deliberately destroyed.
    ///
    /// There is no carrying capacity anywhere in this game any more. Materials
    /// stack without limit and equipment instances accumulate, so nothing is
    /// scrapped on the way in - a low-tier piece is fuel for a forge upgrade,
    /// not junk, and deciding otherwise is the player's business.
    ///
    /// STORED IN THREE TABLES, PRESENTED AS ONE. CommodityRecords and
    /// VillageStashInstances hold stackable materials (a quantity per item id);
    /// EquipmentInstances holds equipment, one row per piece, because each
    /// carries its own AffixPayload and that is what makes two identical-looking
    /// pieces different objects that cannot stack. That split is a storage shape, not a design: the
    /// player sees one chest.
    /// </summary>
    public sealed class VillageChestEngine
    {
        /// <summary>
        /// What a piece of equipment is worth when sold from the chest.
        ///
        /// Mirrors MarketOrderBookEngine's own valuation - `BaseValueGold *
        /// (1 + tier * 0.5)` - so selling to the chest and pricing on the
        /// market cannot drift apart and quietly become two different
        /// economies. The vendor cut is applied separately below so the
        /// relationship between the two stays visible.
        /// </summary>
        public const double VendorPayoutFraction = 0.40;

        public static long ValueEquipment(string baseItemId, int qualityTier)
        {
            if (!ContentRegistry.TryGetItemDefinitionByBaseId(baseItemId, out ItemDefinition definition))
            {
                return 0L;
            }

            double marketValue = definition.BaseValueGold * (1.0 + (qualityTier * 0.5));

            // Selling to the chest is the convenient option, not the best one.
            // A player who wants full value lists on the market; this is what
            // they give up for not waiting for a buyer.
            return (long)Math.Max(1.0, marketValue * VendorPayoutFraction);
        }

        public static long ValueMaterial(string itemId, long quantity)
        {
            if (quantity <= 0) return 0L;
            if (!ContentRegistry.TryGetItemDefinitionByBaseId(itemId, out ItemDefinition definition))
            {
                return 0L;
            }

            return (long)Math.Max(1.0, definition.BaseValueGold * VendorPayoutFraction) * quantity;
        }

        public enum ChestActionResult
        {
            Success = 0,
            NotFound = 1,
            /// <summary>Equipment a character is wearing is never sold or binned by accident.</summary>
            Equipped = 2,
            InvalidQuantity = 3,
        }

        /// <summary>
        /// Sells one equipment instance for gold, or bins it for nothing.
        ///
        /// One method for both because they differ by a single boolean and the
        /// dangerous part - refusing to destroy a piece a character is wearing,
        /// under a lock, inside the transaction that removes it - is identical.
        /// Splitting them would mean maintaining that guard twice.
        /// </summary>
        public static async Task<(ChestActionResult Result, long GoldGained)> RemoveEquipmentAsync(
            FolkIdleDbContext db, long playerId, long equipmentId, bool sell)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                var item = await db.EquipmentInstances
                    .FromSqlInterpolated($"SELECT * FROM \"EquipmentInstances\" WHERE \"Id\" = {equipmentId} AND \"PlayerId\" = {playerId} FOR UPDATE")
                    .FirstOrDefaultAsync();

                if (item == null)
                {
                    await transaction.RollbackAsync();
                    return (ChestActionResult.NotFound, 0L);
                }

                // A piece a character is WEARING is never sold or binned.
                // The client disables those buttons, but the client is not the
                // authority - and the failure here would be a player's equipped
                // weapon vanishing mid-fight, with the character silently
                // pointing at a row that no longer exists.
                bool worn = await db.CharacterRecords.AnyAsync(c =>
                    c.PlayerId == playerId &&
                    (c.EquippedWeaponId == equipmentId ||
                     c.EquippedHelmetId == equipmentId ||
                     c.EquippedChestId == equipmentId ||
                     c.EquippedGlovesId == equipmentId ||
                     c.EquippedLeggingsId == equipmentId ||
                     c.EquippedBootsId == equipmentId ||
                     c.EquippedAmuletId == equipmentId ||
                     c.EquippedRingId == equipmentId));

                if (worn)
                {
                    await transaction.RollbackAsync();
                    return (ChestActionResult.Equipped, 0L);
                }

                long gold = sell ? ValueEquipment(item.BaseItemId, item.QualityTier) : 0L;

                db.EquipmentInstances.Remove(item);

                if (gold > 0)
                {
                    await CreditGoldAsync(db, playerId, gold);
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                return (ChestActionResult.Success, gold);
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                return (ChestActionResult.NotFound, 0L);
            }
        }

        /// <summary>
        /// Sells or bins a quantity of a stackable material.
        ///
        /// Draws from CommodityRecords first and the village stash after,
        /// which is the same order crafting spends them in - so "sell 500 Iron
        /// Ore" takes from the same place a recipe would have, and the two
        /// cannot disagree about what is left.
        /// </summary>
        public static async Task<(ChestActionResult Result, long GoldGained)> RemoveMaterialAsync(
            FolkIdleDbContext db, long playerId, string itemId, long quantity, bool sell)
        {
            if (quantity <= 0 || string.IsNullOrWhiteSpace(itemId))
            {
                return (ChestActionResult.InvalidQuantity, 0L);
            }

            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                long remaining = quantity;

                var commodity = await db.CommodityRecords
                    .FromSqlInterpolated($"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = {itemId} FOR UPDATE")
                    .FirstOrDefaultAsync();

                if (commodity != null && commodity.Quantity > 0)
                {
                    long taken = Math.Min(remaining, commodity.Quantity);
                    commodity.Quantity -= taken;
                    remaining -= taken;
                }

                if (remaining > 0)
                {
                    var stash = await db.VillageStashInstances
                        .FromSqlInterpolated($"SELECT * FROM \"VillageStashInstances\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = {itemId} FOR UPDATE")
                        .FirstOrDefaultAsync();

                    if (stash != null && stash.Quantity > 0)
                    {
                        long taken = Math.Min(remaining, stash.Quantity);
                        stash.Quantity -= taken;
                        remaining -= taken;
                    }
                }

                // Asking for more than exists takes what there is rather than
                // failing: the client's count can legitimately be a moment out
                // of date while a character is still gathering, and refusing
                // the whole operation over that would be worse than selling
                // slightly less than the button said.
                long sold = quantity - remaining;
                if (sold <= 0)
                {
                    await transaction.RollbackAsync();
                    return (ChestActionResult.NotFound, 0L);
                }

                long gold = sell ? ValueMaterial(itemId, sold) : 0L;
                if (gold > 0)
                {
                    await CreditGoldAsync(db, playerId, gold);
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                return (ChestActionResult.Success, gold);
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                return (ChestActionResult.NotFound, 0L);
            }
        }

        // Gold lives in CommodityRecords under the "gold" id like any other
        // commodity, which is why this is an upsert rather than a field write.
        private static async Task CreditGoldAsync(FolkIdleDbContext db, long playerId, long amount)
        {
            var gold = await db.CommodityRecords
                .FromSqlInterpolated($"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = 'gold' FOR UPDATE")
                .FirstOrDefaultAsync();

            if (gold == null)
            {
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = amount });
                return;
            }

            gold.Quantity += amount;
        }
    }
}
