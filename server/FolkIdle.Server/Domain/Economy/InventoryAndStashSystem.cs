using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Models;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Economy
{
    // Modul: Full-Stack Expansion, Parts 1/3. Unified read-write interface
    // over the two material storage tiers: the active Backpack
    // (CommodityRecords - the pool gathering, village production, and
    // every existing consumer already share) and the Village Stash
    // (VillageStashInstances - overflow/long-term storage, unlimited
    // stacks, each stack capped at VillageStashInstance.MaxStackQuantity).
    //
    // Consumption contract: availability is Backpack + Stash; deduction
    // drains the Backpack first, then seamlessly takes the remainder from
    // the Stash - hitting a workbench or vendor never requires a manual
    // transfer. Every method operates inside the CALLER's already-open
    // Serializable transaction (all callers - CraftingEngine et al. -
    // follow the codebase-standard FOR UPDATE row-locking pattern), so
    // this class opens no transactions of its own; it locks the rows it
    // touches with the same escaped-identifier raw SQL convention.
    public static class InventoryAndStashSystem
    {
        public readonly struct UnifiedBalance
        {
            public readonly long BackpackQuantity;
            public readonly long StashQuantity;
            public long Total => BackpackQuantity + StashQuantity;

            public UnifiedBalance(long backpackQuantity, long stashQuantity)
            {
                BackpackQuantity = backpackQuantity;
                StashQuantity = stashQuantity;
            }
        }

        // Locks (FOR UPDATE) and reads both tiers for one material. Caller
        // must hold an open transaction.
        public static async Task<UnifiedBalance> LockAndReadBalanceAsync(FolkIdleDbContext db, long playerId, string itemId)
        {
            var backpackRows = await db.CommodityRecords
                .FromSqlInterpolated($"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = {itemId} FOR UPDATE")
                .ToListAsync();
            long backpack = backpackRows.Count > 0 ? backpackRows[0].Quantity : 0L;

            var stashRows = await db.VillageStashInstances
                .FromSqlInterpolated($"SELECT * FROM \"VillageStashInstances\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = {itemId} FOR UPDATE")
                .ToListAsync();
            long stash = stashRows.Count > 0 ? stashRows[0].Quantity : 0L;

            return new UnifiedBalance(backpack, stash);
        }

        // Consumes requiredCost across both tiers, Backpack first, inside
        // the caller's transaction. Returns false (mutating nothing) if
        // the combined balance is insufficient. Rows the caller may have
        // already locked are re-fetched from the change tracker by EF, so
        // double-locking the same row in one transaction is safe.
        public static async Task<bool> TryConsumeUnifiedAsync(FolkIdleDbContext db, long playerId, string itemId, long requiredCost)
        {
            if (requiredCost <= 0)
            {
                return true;
            }

            var backpackRows = await db.CommodityRecords
                .FromSqlInterpolated($"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = {itemId} FOR UPDATE")
                .ToListAsync();
            var backpack = backpackRows.Count > 0 ? backpackRows[0] : null;
            long backpackQuantity = backpack?.Quantity ?? 0L;

            var stashRows = await db.VillageStashInstances
                .FromSqlInterpolated($"SELECT * FROM \"VillageStashInstances\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = {itemId} FOR UPDATE")
                .ToListAsync();
            var stash = stashRows.Count > 0 ? stashRows[0] : null;
            long stashQuantity = stash?.Quantity ?? 0L;

            if (backpackQuantity + stashQuantity < requiredCost)
            {
                return false;
            }

            long fromBackpack = Math.Min(backpackQuantity, requiredCost);
            long fromStash = requiredCost - fromBackpack;

            if (fromBackpack > 0 && backpack != null)
            {
                backpack.Quantity -= fromBackpack;
                db.CommodityRecords.Update(backpack);
            }

            if (fromStash > 0 && stash != null)
            {
                stash.Quantity -= fromStash;
                if (stash.Quantity <= 0)
                {
                    db.VillageStashInstances.Remove(stash);
                }
                else
                {
                    db.VillageStashInstances.Update(stash);
                }
            }

            return true;
        }

        // Deposits into the Village Chest. Upsert semantics rely on the unique
        // (PlayerId, ItemId) index.
        //
        // Modul: unlimited village chest. This used to clamp each stack to
        // VillageStashInstance.MaxStackQuantity (9999) and return the overflow
        // for the caller to handle. No caller ever handled it meaningfully -
        // the only place to put a rejected remainder is the backpack the player
        // was depositing FROM, so in practice a full stack silently ate
        // everything above the cap at the exact moment a player had succeeded
        // at the game.
        //
        // The chest is now genuinely unbounded, so this always accepts the
        // whole quantity. The return value is kept (and is always 0) rather
        // than changed to void, so the handful of call sites that check it stay
        // correct without edits and a future bounded tier could reintroduce a
        // real remainder without another signature change.
        public static async Task<long> DepositToStashAsync(FolkIdleDbContext db, long playerId, string itemId, long quantity)
        {
            if (quantity <= 0)
            {
                return 0L;
            }

            var stashRows = await db.VillageStashInstances
                .FromSqlInterpolated($"SELECT * FROM \"VillageStashInstances\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = {itemId} FOR UPDATE")
                .ToListAsync();
            var stash = stashRows.Count > 0 ? stashRows[0] : null;

            if (stash == null)
            {
                db.VillageStashInstances.Add(new VillageStashInstance { PlayerId = playerId, ItemId = itemId, Quantity = quantity });
            }
            else
            {
                stash.Quantity += quantity;
                db.VillageStashInstances.Update(stash);
            }

            return 0L;
        }
    }
}
