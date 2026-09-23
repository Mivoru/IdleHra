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
    internal static class LarderTickCoordinator
    {
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.LarderSlotUpdateQueue.TryDequeue(out var larderUpdate))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, larderUpdate.PlayerId);
                if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    continue;
                }

                Apply(ref currentPayload, in larderUpdate);
            }
        }

        internal static void Apply(ref TickStatePayload payload, in LarderSlotUpdateNotification larderUpdate)
        {
            switch (larderUpdate.SlotIndex)
            {
                case 0:
                    payload.Food1_ItemId = larderUpdate.ItemId;
                    payload.Food1_Count = larderUpdate.Count;
                    break;
                case 1:
                    payload.Food2_ItemId = larderUpdate.ItemId;
                    payload.Food2_Count = larderUpdate.Count;
                    break;
                case 2:
                    payload.Food3_ItemId = larderUpdate.ItemId;
                    payload.Food3_Count = larderUpdate.Count;
                    break;
            }

            // Modul: halt reasons. Stocking food is the direct answer to
            // an OutOfFood halt, so clear the banner as soon as there is
            // something to eat. The activity itself still needs
            // redeploying - only the player can decide that - so this
            // does not restart it.
            if (payload.ActivityHaltReason == Network.ActivityHaltReason.OutOfFood && larderUpdate.Count > 0)
            {
                payload.ActivityHaltReason = Network.ActivityHaltReason.None;
            }

            payload.IsDirty = true;
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.StockFoodSlot)
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
            // was fixed. LarderEngine validates and reports through
            // the CommandResult ring buffer instead.
            long larderPlayerId = currentPayload.PlayerId;
            int larderSlot = (int)cmd.TargetSlotIndex;
            int larderFoodId = (int)cmd.ConsumableItemId;
            int larderQuantity = (int)Math.Min(cmd.DepositQuantity, (uint)Network.LarderLimits.SlotCapacity);

            if (ctx.LarderEngine != null)
            {
                var larderEngine = ctx.LarderEngine;
                ctx.SafeDispatch("Larder.StockFoodSlot", larderPlayerId, async () => {
                    await larderEngine.ExecuteStockFoodSlotAsync(larderPlayerId, larderSlot, larderFoodId, larderQuantity);
                });
            }
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
