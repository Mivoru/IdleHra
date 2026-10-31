using System.Threading.Tasks;
using System;
using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Shared;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>
    /// The tick thread's larder hand-off. See LegacyStoreTickCoordinator for
    /// the coordinator shape this repeats.
    /// </summary>
    /// <remarks>
    /// Modul: THE PAYLOAD IS THE LARDER'S TRUTH (2026-10-31). Stocking used to
    /// hand the whole request to LarderEngine, which read the slot from
    /// PlayerRecords - a row as old as the last checkpoint - and sent the tick
    /// back an ABSOLUTE count. Food eaten since that checkpoint came back on
    /// every restock, and an unload returned it to the chest as well: a
    /// duplication any fighter could repeat. Now the tick reads the live slot:
    /// unloading and taking some back happen here, on the payload, and the
    /// chest is credited exactly what left it; stocking takes food from the
    /// chest off the tick and hands back "+N", which DrainNotifications adds.
    /// The checkpoint writes the slots, as it already did after every bite. The
    /// potion shelf (PotionShelfTickCoordinator) is built the same way.
    /// </remarks>
    internal static class LarderTickCoordinator
    {
        /// <summary>
        /// Lands committed stockings on the live slots. A delta that no longer
        /// fits - the slot was given another food in between, or the player
        /// logged out - goes back to the chest rather than vanish.
        /// </summary>
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers,
            Action<string, long, Func<Task>> safeDispatch,
            LarderEngine? larderEngine)
        {
            while (registry.LarderSlotUpdateQueue.TryDequeue(out var larderUpdate))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, larderUpdate.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload) && Apply(ref currentPayload, in larderUpdate)) continue;

                var back = larderUpdate;
                if (larderEngine != null)
                    safeDispatch("Larder.Return", back.PlayerId, () => larderEngine.ReturnFoodToChestAsync(back.PlayerId, back.ItemId, back.Added));
            }
        }

        /// <summary>Adds a stocking to its slot; false when the slot holds another food.</summary>
        internal static bool Apply(ref TickStatePayload payload, in LarderSlotUpdateNotification larderUpdate)
        {
            ref int item = ref SlotItem(ref payload, larderUpdate.SlotIndex);
            ref int count = ref SlotCount(ref payload, larderUpdate.SlotIndex);
            if (count > 0 && item != larderUpdate.ItemId) return false;
            // A negative delta is food eaten off the tick (a dungeon run's meals):
            // never below zero, and an emptied slot holds nothing.
            if (larderUpdate.Added < 0 && count == 0) return true;
            item = larderUpdate.ItemId;
            count = Math.Clamp(count + larderUpdate.Added, 0, Network.LarderLimits.SlotCapacity);
            if (count == 0) item = 0;

            // Modul: halt reasons. Stocking food is the direct answer to
            // an OutOfFood halt, so clear the banner as soon as there is
            // something to eat. The activity itself still needs
            // redeploying - only the player can decide that - so this
            // does not restart it.
            if (payload.ActivityHaltReason == Network.ActivityHaltReason.OutOfFood && count > 0)
            {
                payload.ActivityHaltReason = Network.ActivityHaltReason.None;
            }

            payload.IsDirty = true;
            return true;
        }

        internal static ref int SlotItem(ref TickStatePayload payload, int slot)
        {
            if (slot == 0) return ref payload.Food1_ItemId;
            if (slot == 1) return ref payload.Food2_ItemId;
            return ref payload.Food3_ItemId;
        }

        internal static ref int SlotCount(ref TickStatePayload payload, int slot)
        {
            if (slot == 0) return ref payload.Food1_Count;
            if (slot == 1) return ref payload.Food2_Count;
            return ref payload.Food3_Count;
        }

        /// <summary>
        /// Takes up to <paramref name="quantity"/> (0 or less = all) out of a
        /// live slot and returns what was taken - what the chest gets back.
        /// </summary>
        internal static (int ItemId, int Taken) TakeFromSlot(ref TickStatePayload payload, int slot, int quantity)
        {
            ref int item = ref SlotItem(ref payload, slot);
            ref int count = ref SlotCount(ref payload, slot);
            int taken = quantity <= 0 ? count : Math.Min(quantity, count);
            int itemId = item;
            if (taken <= 0) return (itemId, 0);
            count -= taken;
            if (count == 0) item = 0;
            payload.IsDirty = true;
            return (itemId, taken);
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.StockFoodSlot)
        // - and rebuilt on the live slot on 2026-10-31, see the class remarks.
        internal static void HandleStockFoodSlot(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: larder. Deliberately does NOT terminate the
            // session on a bad request. Every field here is
            // player-chosen from a UI list (which slot, which food,
            // how many), so a stale client sending a food id that no
            // longer exists is a mistake to report, not evidence of
            // tampering - and TerminateSessionForSecurity for a
            // mis-click is exactly the failure mode that made eating
            // food force-disconnect players before AlchemyCompendium
            // was fixed. Every answer goes through the CommandResult
            // ring buffer instead.
            long playerId = currentPayload.PlayerId;
            int slot = (int)cmd.TargetSlotIndex;
            int foodId = (int)cmd.ConsumableItemId;
            int quantity = (int)Math.Min(cmd.DepositQuantity, (uint)Network.LarderLimits.SlotCapacity);
            var registry = ctx.PlayerRegistry;
            var larderEngine = ctx.LarderEngine;
            if (larderEngine == null) return;
            if (slot < 0 || slot >= Network.LarderLimits.SlotCount)
            {
                registry.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.GenericValidationFailure);
                return;
            }

            bool isUnload = quantity <= 0;
            // Food id 0 with a positive quantity: take that many back out.
            bool isPartialWithdrawal = !isUnload && foodId == 0;
            if (!isUnload && !isPartialWithdrawal && !FoodRegistry.IsFood(foodId))
            {
                registry.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.GenericValidationFailure);
                return;
            }

            int liveItem = SlotItem(ref currentPayload, slot);
            int liveCount = SlotCount(ref currentPayload, slot);
            if (isPartialWithdrawal && liveCount <= 0)
            {
                registry.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.GenericValidationFailure);
                return;
            }

            // Unload, take some back, or clear a slot that holds another food
            // - the chest gets back exactly what is in the slot NOW.
            if (isUnload || isPartialWithdrawal || (liveCount > 0 && liveItem != foodId))
            {
                var (backId, back) = TakeFromSlot(ref currentPayload, slot, isPartialWithdrawal ? quantity : 0);
                if (back > 0)
                    ctx.SafeDispatch("Larder.Return", playerId, () => larderEngine.ReturnFoodToChestAsync(playerId, backId, back));
                if (isUnload || isPartialWithdrawal)
                {
                    registry.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.Success);
                    return;
                }
                liveCount = 0;
            }

            int headroom = Network.LarderLimits.SlotCapacity - liveCount;
            if (headroom <= 0)
            {
                registry.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.InventoryFull);
                return;
            }
            int toMove = Math.Min(quantity, headroom);
            ctx.SafeDispatch("Larder.StockFoodSlot", playerId, async () =>
            {
                bool taken = await larderEngine.TakeFoodFromChestAsync(playerId, foodId, toMove);
                if (taken)
                    registry.LarderSlotUpdateQueue.Enqueue(new LarderSlotUpdateNotification { PlayerId = playerId, SlotIndex = slot, ItemId = foodId, Added = toMove });
                registry.EnqueueCommandResult(playerId, (byte)(taken ? Network.CommandResultCode.Success : Network.CommandResultCode.InsufficientMaterials));
            });
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.UpdateAutoEatThreshold)
        internal static void HandleUpdateAutoEatThreshold(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            int thresholdValue = cmd.LimitPrice;
            if (!ClientCommandValidator.ValidateCombatConfiguration(ref currentPayload, thresholdValue))
            {
                ctx.RemoveActivePlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.ForceDisconnect(ctx.RoutingPlayerId);
                return;
            }
            currentPayload.AutoEatThreshold = thresholdValue;

            // Modul: larder. This used to write the live payload and
            // nothing else, so a player's chosen auto-eat threshold
            // was silently discarded at every logout and reverted to
            // the default on the next login.
            if (ctx.LarderEngine != null)
            {
                long thresholdPlayerId = currentPayload.PlayerId;
                int persistedThreshold = thresholdValue;
                var larderEngine = ctx.LarderEngine;
                ctx.SafeDispatch("Larder.PersistAutoEatThreshold", thresholdPlayerId, async () => {
                    await larderEngine.PersistAutoEatThresholdAsync(thresholdPlayerId, persistedThreshold);
                });
            }
            currentPayload.IsDirty = true;
        }
    }
}
