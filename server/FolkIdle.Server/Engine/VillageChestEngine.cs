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

            /// <summary>
            /// The item is affix-locked, which means "do not change or destroy
            /// this one".
            ///
            /// Modul: THE LOCK MEANT LESS THAN ITS NAME. IsAffixLocked was
            /// honoured by the reroll and by forge fusion, and ignored by both
            /// removal paths - so a locked item could not be CHANGED but could
            /// still be sold, binned, or swallowed by the bulk sweep.
            ///
            /// That is not a distinction any player would draw from the word
            /// "locked", and the sweep is the operation that makes it matter:
            /// it deletes every unworn piece at or below a tier in one
            /// statement, and its rarity ceiling of 6 is the only thing
            /// standing between a player and their favourite Epic sword.
            ///
            /// Nothing can set the lock today (see docs/audit_2026_09_05.md
            /// finding A), so this is inert until it is built - which is
            /// exactly when it wants to already be true.
            /// </summary>
            Locked = 4,
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
                //
                // Modul: ELEVEN SLOTS. This list stopped at EquippedRingId, the
                // last of the eight combat slots, so the three TOOL slots were
                // not consulted at all - a worn axe, pickaxe or rod could be
                // sold or binned out from under the character wearing it, which
                // is exactly the dangling-pointer state
                // EquipmentSlotEngine.ClearDanglingEquipReferencesAsync exists
                // to heal. Same truncation the project has hit repeatedly; see
                // CLAUDE.md, and grep EquippedRingId for the rest.
                bool worn = await IsWornByAnyCharacterAsync(db, playerId, equipmentId);

                if (worn)
                {
                    await transaction.RollbackAsync();
                    return (ChestActionResult.Equipped, 0L);
                }

                // A locked piece is never sold or binned either - see
                // ChestActionResult.Locked for why the lock had to mean this
                // as well as "cannot be rerolled or fused".
                if (item.IsAffixLocked)
                {
                    await transaction.RollbackAsync();
                    return (ChestActionResult.Locked, 0L);
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

        /// <summary>
        /// Every equipment instance any of this player's characters is wearing,
        /// across ALL ELEVEN SLOTS - the eight combat slots and the three tools.
        ///
        /// One query, and one place the slot list is written down, because the
        /// slot list is the thing this codebase keeps truncating: every list
        /// that stopped at EquippedRingId (the last of the eight) has been a
        /// bug. Callers that need "is this one item worn" and callers that need
        /// "which of these thousands are worn" both come here, so the two can
        /// never answer differently.
        ///
        /// Benched characters count. A character past the three playable slots
        /// keeps its equipped ids, and salvaging what an ancestor is holding
        /// would break that row the moment they are fielded again - the same
        /// reasoning /api/v1/player/inventory's worn-item pass records.
        /// </summary>
        public static async Task<System.Collections.Generic.HashSet<long>> LoadWornEquipmentIdsAsync(
            FolkIdleDbContext db, long playerId)
        {
            var rows = await db.CharacterRecords
                .AsNoTracking()
                .Where(c => c.PlayerId == playerId)
                .Select(c => new
                {
                    c.EquippedWeaponId,
                    c.EquippedHelmetId,
                    c.EquippedChestId,
                    c.EquippedGlovesId,
                    c.EquippedLeggingsId,
                    c.EquippedBootsId,
                    c.EquippedAmuletId,
                    c.EquippedRingId,
                    c.EquippedAxeId,
                    c.EquippedPickaxeId,
                    c.EquippedRodId
                })
                .ToListAsync();

            var worn = new System.Collections.Generic.HashSet<long>();
            foreach (var row in rows)
            {
                void Note(long? id) { if (id.HasValue && id.Value > 0L) worn.Add(id.Value); }

                Note(row.EquippedWeaponId);
                Note(row.EquippedHelmetId);
                Note(row.EquippedChestId);
                Note(row.EquippedGlovesId);
                Note(row.EquippedLeggingsId);
                Note(row.EquippedBootsId);
                Note(row.EquippedAmuletId);
                Note(row.EquippedRingId);
                Note(row.EquippedAxeId);
                Note(row.EquippedPickaxeId);
                Note(row.EquippedRodId);
            }

            return worn;
        }

        private static async Task<bool> IsWornByAnyCharacterAsync(
            FolkIdleDbContext db, long playerId, long equipmentId)
        {
            var worn = await LoadWornEquipmentIdsAsync(db, playerId);
            return worn.Contains(equipmentId);
        }

        /// <summary>
        /// The highest rarity a player may point the bulk tools or auto-salvage
        /// at. Legendary (7) and above is never sweepable in bulk and never
        /// auto-salvaged: those are the drops the whole loop is for, and a
        /// mis-set threshold that ate them would be unrecoverable.
        ///
        /// Six still drains almost everything - Normal through Epic is over
        /// 99% of all drops by weight (see the client's RARITY_DROP_SHARE,
        /// which publishes the same table CombatLootEngine rolls against).
        /// </summary>
        public const int MaxSweepableQualityTier = 6;

        public readonly struct BulkRemovalOutcome
        {
            public int RemovedCount { get; init; }
            public long GoldGained { get; init; }
            public int SkippedWornCount { get; init; }
        }

        /// <summary>
        /// Sells or bins EVERY carried piece at or below a quality tier, in one
        /// transaction.
        ///
        /// Modul: this is the drain the chest never had. Loot arrives on 15% of
        /// kills and the only removal in the game was a per-item button, so the
        /// table grew without bound - one live account reached 17,836 rows and
        /// the inventory screen that would have let them clean it up was the
        /// screen the volume had made too slow to use. A per-item API cannot
        /// fix that: seventeen thousand round trips is not a remedy.
        ///
        /// Deliberately NOT expressed through RemoveEquipmentAsync in a loop.
        /// That method opens a Serializable transaction and re-reads the worn
        /// set per call; seventeen thousand of those is hours of work and
        /// seventeen thousand chances to half-finish. This reads the worn set
        /// once, values the batch in memory against the same ValueEquipment
        /// every single sale uses, and deletes with one statement - so the
        /// operation either happens or does not.
        ///
        /// The DELETE repeats the SELECT's predicate rather than listing the
        /// ids it just read: inside one Serializable transaction the two see
        /// the same rows, and an id list of seventeen thousand parameters is a
        /// statement no driver should be asked to plan.
        /// </summary>
        public static async Task<BulkRemovalOutcome> RemoveEquipmentUpToTierAsync(
            FolkIdleDbContext db, long playerId, int maxQualityTier, bool sell)
        {
            if (maxQualityTier < 1 || maxQualityTier > MaxSweepableQualityTier)
            {
                return new BulkRemovalOutcome();
            }

            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                var worn = await LoadWornEquipmentIdsAsync(db, playerId);

                // Long[] rather than the HashSet: Npgsql maps an array
                // parameter to `<> ALL(...)`, which is one predicate the
                // planner can use, instead of expanding to a chain of
                // inequalities that grows with the roster.
                long[] wornIds = worn.Count == 0 ? new long[] { 0L } : System.Linq.Enumerable.ToArray(worn);

                // Counted with the SAME tier predicate as the sweep, not as
                // `worn.Count`: a player wearing a Legendary sword has an item
                // this sweep was never going to touch, and reporting it as
                // "kept back because it is worn" would explain a decision the
                // threshold had already made.
                int skippedWorn = await db.EquipmentInstances
                    .AsNoTracking()
                    .CountAsync(e => e.PlayerId == playerId
                        && e.QualityTier <= maxQualityTier
                        && wornIds.Contains(e.Id));

                // Modul: AND A LOCKED PIECE SURVIVES THE SWEEP.
                //
                // This is the operation the lock exists for. It deletes every
                // unworn piece at or below a tier in a single statement, and
                // until now its rarity ceiling of 6 was the ONLY thing between
                // a player and a favourite Epic sword. "Locked" has to mean
                // "not this one" here or it does not mean anything a player
                // would recognise.
                //
                // Inert today - nothing can set the flag (see
                // docs/audit_2026_09_05.md finding A) - which is exactly when a
                // safety predicate wants to already be in place, rather than
                // being remembered on the day the lock is built.
                var doomed = await db.EquipmentInstances
                    .AsNoTracking()
                    .Where(e => e.PlayerId == playerId
                        && e.QualityTier <= maxQualityTier
                        && !e.IsAffixLocked
                        && !wornIds.Contains(e.Id))
                    .Select(e => new { e.BaseItemId, e.QualityTier })
                    .ToListAsync();

                if (doomed.Count == 0)
                {
                    await transaction.RollbackAsync();
                    return new BulkRemovalOutcome { SkippedWornCount = skippedWorn };
                }

                long gold = 0L;
                if (sell)
                {
                    for (int i = 0; i < doomed.Count; i++)
                    {
                        gold += ValueEquipment(doomed[i].BaseItemId, doomed[i].QualityTier);
                    }
                }

                // THE DELETE MUST REPEAT THE SELECT EXACTLY, including the
                // lock. The two run inside one Serializable transaction and are
                // meant to see the same rows; a predicate that appears in one
                // and not the other would sell a set of items and delete a
                // different one - here, deleting locked pieces the gold total
                // did not pay for.
                int removed = await db.EquipmentInstances
                    .Where(e => e.PlayerId == playerId
                        && e.QualityTier <= maxQualityTier
                        && !e.IsAffixLocked
                        && !wornIds.Contains(e.Id))
                    .ExecuteDeleteAsync();

                if (gold > 0)
                {
                    await CreditGoldAsync(db, playerId, gold);
                    await db.SaveChangesAsync();
                }

                await transaction.CommitAsync();

                return new BulkRemovalOutcome
                {
                    RemovedCount = removed,
                    GoldGained = gold,
                    SkippedWornCount = skippedWorn
                };
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                return new BulkRemovalOutcome();
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
