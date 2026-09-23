using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// The tick thread's region-completion hand-off. See
    /// LegacyStoreTickCoordinator for the coordinator shape this repeats.
    /// </summary>
    internal static class RegionProgressionTickCoordinator
    {
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.RegionCompletionUpdateQueue.TryDequeue(out var regionCompletionUpdate))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, regionCompletionUpdate.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    Apply(ref currentPayload, in regionCompletionUpdate);
                }
            }
        }

        internal static void Apply(ref TickStatePayload payload, in RegionCompletionNotification regionCompletionUpdate)
        {
            payload.CompletedAreaFlags |= regionCompletionUpdate.CompletedRegionFlags;
            payload.IsDirty = true;
        }
    }
}
