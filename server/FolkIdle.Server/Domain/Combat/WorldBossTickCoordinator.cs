using System.Collections.Generic;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// The tick thread's World Boss attempt hand-off. See
    /// LegacyStoreTickCoordinator for the coordinator shape this repeats.
    /// </summary>
    internal static class WorldBossTickCoordinator
    {
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.WorldBossAttemptUpdateQueue.TryDequeue(out var worldBossAttemptUpdate))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, worldBossAttemptUpdate.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    Apply(ref currentPayload, in worldBossAttemptUpdate);
                }
            }
        }

        internal static void Apply(ref TickStatePayload payload, in WorldBossAttemptUpdateNotification worldBossAttemptUpdate)
        {
            payload.WorldBossAttemptCount = worldBossAttemptUpdate.AttemptCount;
            payload.WorldBossSessionEndsEpoch = worldBossAttemptUpdate.SessionEndsEpoch;
            payload.IsDirty = true;
        }
    }
}
