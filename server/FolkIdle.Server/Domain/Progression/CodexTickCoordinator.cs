using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// The tick thread's codex-multiplier hand-off. See
    /// LegacyStoreTickCoordinator for the coordinator shape this repeats.
    /// </summary>
    internal static class CodexTickCoordinator
    {
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.CodexMultiplierUpdateQueue.TryDequeue(out var codexMultiplierUpdate))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, codexMultiplierUpdate.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    Apply(ref currentPayload, in codexMultiplierUpdate);
                }
            }
        }

        internal static void Apply(ref TickStatePayload payload, in CodexMultiplierUpdateNotification codexMultiplierUpdate)
        {
            payload.CachedCodexYieldMultiplier = codexMultiplierUpdate.YieldMultiplier;
            payload.CachedCodexDamageMultiplier = codexMultiplierUpdate.DamageMultiplier;
        }
    }
}
