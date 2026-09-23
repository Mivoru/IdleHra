using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// The tick thread's Skill Tree hand-off. See LegacyStoreTickCoordinator
    /// for the coordinator shape this repeats.
    /// </summary>
    internal static class SkillTreeTickCoordinator
    {
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.SkillTreeSyncQueue.TryDequeue(out var treeNotif))
            {
                ref var treePayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, treeNotif.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref treePayload))
                {
                    Apply(ref treePayload, in treeNotif);
                }
            }
        }

        internal static void Apply(ref TickStatePayload payload, in SkillTreeSyncNotification treeNotif)
        {
            SimulationEngine.SetSkillTreeLevel(ref payload, treeNotif.BranchId, treeNotif.NewLevel);
            // The points were spent inside the same transaction the
            // level was written in, so the payload must take the
            // balance the engine reports rather than decrementing
            // its own copy - two subtractions of one purchase is
            // exactly how a counter drifts.
            payload.AvailableSkillPoints = treeNotif.RemainingSkillPoints;

            // Modul: and the respec counters, which a respec also
            // moves. Without this the levels cleared and the points
            // came back, but the button went on offering a free
            // respec the player had already spent - it only
            // corrected itself at the next full hydration. The same
            // "the output side was never wired" shape this codebase
            // keeps finding; the write happened, nothing carried it.
            payload.FreeRespecUsed = treeNotif.FreeRespecUsed;
            payload.PaidRespecGrants = treeNotif.PaidRespecGrants;
            payload.IsDirty = true;
        }
    }
}
