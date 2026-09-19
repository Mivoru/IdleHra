using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// The tick thread's Inheritance hand-off. See LegacyStoreTickCoordinator
    /// for the coordinator shape this repeats: DrainNotifications owns the
    /// while/TryDequeue/ref-resolution loop, Apply is the independently
    /// callable per-payload mutation.
    /// </summary>
    internal static class InheritanceTickCoordinator
    {
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.InheritanceSyncQueue.TryDequeue(out var inheritNotif))
            {
                ref var inheritPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, inheritNotif.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref inheritPayload))
                {
                    Apply(ref inheritPayload, in inheritNotif);
                }
            }
        }

        internal static void Apply(ref TickStatePayload payload, in InheritanceSyncNotification inheritNotif)
        {
            SimulationEngine.SetInheritanceLevel(ref payload, inheritNotif.StatId, inheritNotif.NewLevel);
            payload.IsDirty = true;
        }
    }
}
