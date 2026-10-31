using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>
    /// The potion shelves (herbs and potions, 2026-10-31): stock one from the
    /// chest, drink from it, auto-drink on or off.
    /// </summary>
    /// <remarks>
    /// Modul: THE PAYLOAD IS THE SHELF'S TRUTH, AND ONLY DELTAS CROSS THREADS.
    /// The larder's stocking reads the slot count from PlayerRecords and hands
    /// the tick an ABSOLUTE count - a row that can be older than the payload,
    /// where food was eaten since the last flush. Here stocking takes potions
    /// from the chest and hands the tick "+N of this potion"; unloading,
    /// drinking and the auto-drink switch happen on the tick itself, and the
    /// checkpoint writes the shelf from the payload. Nothing can be counted
    /// twice or restored after being drunk.
    ///
    /// One kind of potion per shelf: stocking a different one while the shelf
    /// holds some is refused (the screen unloads first), so a delta never has
    /// to decide between two potions.
    /// </remarks>
    internal static class PotionShelfTickCoordinator
    {
        internal readonly record struct ShelfDelta(long PlayerId, int Slot, int ItemId, int Added);

        internal static readonly ConcurrentQueue<ShelfDelta> Deltas = new();

        /// <summary>The most one shelf holds - the larder's slot capacity, for one rule.</summary>
        public const int ShelfCapacity = LarderLimits.SlotCapacity;

        // StockPotionShelf: TargetSlotIndex = slot, ConsumableItemId = potion,
        // DepositQuantity = how many (0 = take the whole shelf back to the chest).
        internal static void HandleStock(ref TickStatePayload payload, ref ClientCommandPacket cmd, in CommandCoordinatorContext ctx)
        {
            long playerId = payload.PlayerId;
            int slot = (int)cmd.TargetSlotIndex;
            int quantity = (int)Math.Min(cmd.DepositQuantity, (uint)ShelfCapacity);
            if (slot < 0 || slot >= PotionSlot.Count)
            {
                ctx.PlayerRegistry.EnqueueCommandResult(playerId, (byte)CommandResultCode.GenericValidationFailure);
                return;
            }

            ref int shelfItem = ref ConsumableEngine.ShelfItem(ref payload, slot);
            ref int shelfCount = ref ConsumableEngine.ShelfCount(ref payload, slot);
            var factory = ctx.ContextFactory;
            var registry = ctx.PlayerRegistry;

            if (quantity <= 0)
            {
                // Unload: off the shelf now, into the chest behind.
                int back = shelfCount;
                string baseId = back > 0 ? ContentRegistry.GetItemBaseId(shelfItem) : string.Empty;
                shelfCount = 0;
                shelfItem = 0;
                payload.IsDirty = true;
                if (back > 0 && !string.IsNullOrEmpty(baseId))
                {
                    ctx.SafeDispatch("Potions.Unload", playerId, () => CreditAsync(factory, playerId, baseId, back));
                }
                registry.EnqueueCommandResult(playerId, (byte)CommandResultCode.Success);
                return;
            }

            int itemId = (int)cmd.ConsumableItemId;
            if (!PotionRegistry.TryGet(itemId, out var potion) || potion.Slot != slot
                || (shelfCount > 0 && shelfItem != itemId))
            {
                registry.EnqueueCommandResult(playerId, (byte)CommandResultCode.GenericValidationFailure);
                return;
            }
            int room = ShelfCapacity - shelfCount;
            if (room <= 0)
            {
                registry.EnqueueCommandResult(playerId, (byte)CommandResultCode.InventoryFull);
                return;
            }
            int toMove = Math.Min(quantity, room);
            string potionBaseId = ContentRegistry.GetItemBaseId(itemId);
            ctx.SafeDispatch("Potions.Stock", playerId, async () =>
            {
                await using var db = await factory.CreateDbContextAsync();
                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                if (!await InventoryAndStashSystem.TryConsumeUnifiedAsync(db, playerId, potionBaseId, toMove))
                {
                    await transaction.RollbackAsync();
                    registry.EnqueueCommandResult(playerId, (byte)CommandResultCode.InsufficientMaterials);
                    return;
                }
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                Deltas.Enqueue(new ShelfDelta(playerId, slot, itemId, toMove));
                registry.EnqueueCommandResult(playerId, (byte)CommandResultCode.Success);
            });
        }

        // DrinkPotion: TargetSlotIndex = slot. Tick-only - the shelf is on the payload.
        internal static void HandleDrink(ref TickStatePayload payload, ref ClientCommandPacket cmd, in CommandCoordinatorContext ctx)
        {
            int slot = (int)cmd.TargetSlotIndex;
            bool drunk = slot >= 0 && slot < PotionSlot.Count && ConsumableEngine.TryDrinkFromShelf(ref payload, slot);
            ctx.PlayerRegistry.EnqueueCommandResult(payload.PlayerId, (byte)(drunk ? CommandResultCode.Success : CommandResultCode.InsufficientMaterials));
        }

        // SetPotionAutoDrink: TargetSlotIndex = slot, IsBuy = 1 on / 0 off.
        internal static void HandleAutoDrink(ref TickStatePayload payload, ref ClientCommandPacket cmd, in CommandCoordinatorContext ctx)
        {
            int slot = (int)cmd.TargetSlotIndex;
            if (slot < 0 || slot >= PotionSlot.Count)
            {
                ctx.PlayerRegistry.EnqueueCommandResult(payload.PlayerId, (byte)CommandResultCode.GenericValidationFailure);
                return;
            }
            byte bit = (byte)(1 << slot);
            payload.PotionAutoDrinkMask = cmd.IsBuy != 0 ? (byte)(payload.PotionAutoDrinkMask | bit) : (byte)(payload.PotionAutoDrinkMask & ~bit);
            // Switching it on with the slot empty drinks at once, as the live
            // tick would the moment a running potion ended.
            if (cmd.IsBuy != 0 && ConsumableEngine.Duration(ref payload, slot) <= 0) ConsumableEngine.TryDrinkFromShelf(ref payload, slot);
            payload.IsDirty = true;
        }

        /// <summary>
        /// Lands committed stockings on the live shelves. A delta that no longer
        /// fits (the player stocked a different potion in between, or logged
        /// out) goes back to the chest rather than vanish.
        /// </summary>
        internal static void DrainDeltas(
            Dictionary<long, TickStatePayload> activePlayers,
            Action<string, long, Func<Task>> safeDispatch,
            IDbContextFactory<FolkIdleDbContext> factory)
        {
            while (Deltas.TryDequeue(out var delta))
            {
                ref var payload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, delta.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref payload) && Apply(ref payload, in delta)) continue;

                string baseId = ContentRegistry.GetItemBaseId(delta.ItemId);
                var d = delta;
                if (!string.IsNullOrEmpty(baseId))
                    safeDispatch("Potions.Return", d.PlayerId, () => CreditAsync(factory, d.PlayerId, baseId, d.Added));
            }
        }

        /// <summary>Adds a delta to its shelf; false when the shelf holds another potion.</summary>
        internal static bool Apply(ref TickStatePayload payload, in ShelfDelta delta)
        {
            ref int shelfItem = ref ConsumableEngine.ShelfItem(ref payload, delta.Slot);
            ref int shelfCount = ref ConsumableEngine.ShelfCount(ref payload, delta.Slot);
            if (shelfCount > 0 && shelfItem != delta.ItemId) return false;
            shelfItem = delta.ItemId;
            shelfCount += delta.Added;
            payload.IsDirty = true;
            return true;
        }

        private static async Task CreditAsync(IDbContextFactory<FolkIdleDbContext> factory, long playerId, string baseId, int quantity)
        {
            await using var db = await factory.CreateDbContextAsync();
            await CommodityLedger.AddAsync(db, playerId, baseId, quantity);
            await db.SaveChangesAsync();
        }
    }
}
