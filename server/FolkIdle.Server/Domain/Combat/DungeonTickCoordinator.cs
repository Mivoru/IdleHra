using System.Collections.Generic;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>The tick thread's side of a dungeon run: its XP lands on the live payload here.</summary>
    /// <remarks>
    /// Modul: THE MAIN CHARACTER IS IN THE REGISTER HERE. The drains run
    /// between sub-ticks, when slot 1 sits in the active register (the per-slot
    /// loop swaps 2 and 3 in and back inside a finally), so the XP goes to the
    /// run's champion with no swap. ApplyBulkExperience is the warp path's
    /// level-up loop: skill points, race growth and the level cosmetic come
    /// with it. A grant for a player who logged out in between is dropped -
    /// lost, never doubled, the GoldLedger rule.
    /// </remarks>
    internal static class DungeonTickCoordinator
    {
        internal static void DrainXpGrants(PlayerSessionRegistry registry, Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.DungeonXpGrantQueue.TryDequeue(out var grant))
            {
                ref var payload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, grant.PlayerId);
                if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref payload)) continue;
                Apply(ref payload, grant.Xp);
            }
        }

        internal static void Apply(ref TickStatePayload payload, long xp)
        {
            if (xp <= 0) return;
            SimulationEngine.ApplyBulkExperience(ref payload, xp, SimulationEngine.ActiveRaceId(in payload));
            payload.IsDirty = true;
        }
    }
}
