using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Economy
{
    /// <summary>
    /// The tick thread's forge-upgrade hand-off. See LegacyStoreTickCoordinator
    /// for the coordinator shape this repeats.
    /// </summary>
    internal static class ForgeTickCoordinator
    {
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.ForgeUpgradeQueue.TryDequeue(out var forgeUpgrade))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, forgeUpgrade.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    Apply(ref currentPayload, in forgeUpgrade);
                }
            }
        }

        internal static void Apply(ref TickStatePayload payload, in ForgeUpgradeNotification forgeUpgrade)
        {
            payload.ForgeUpgradeCount++;
            if (forgeUpgrade.ResultingQualityTier > payload.HighestForgeSynthesisTier)
            {
                payload.HighestForgeSynthesisTier = forgeUpgrade.ResultingQualityTier;
            }
            payload.IsDirty = true;
        }
    }
}
