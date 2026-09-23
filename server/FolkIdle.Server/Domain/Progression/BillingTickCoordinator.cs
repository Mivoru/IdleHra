using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// The tick thread's billing-sync hand-off. See LegacyStoreTickCoordinator
    /// for the coordinator shape this repeats.
    /// </summary>
    internal static class BillingTickCoordinator
    {
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.BillingSyncQueue.TryDequeue(out var billingSyncNotif))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, billingSyncNotif.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    Apply(ref currentPayload, in billingSyncNotif);
                }
            }
        }

        internal static void Apply(ref TickStatePayload payload, in BillingSyncNotification billingSyncNotif)
        {
            payload.SetPremiumCurrency(billingSyncNotif.PremiumDiamondsBalance);
            payload.IsDirty = true;
        }
    }
}
