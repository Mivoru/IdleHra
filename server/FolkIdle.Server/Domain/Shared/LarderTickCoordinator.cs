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
    }
}
