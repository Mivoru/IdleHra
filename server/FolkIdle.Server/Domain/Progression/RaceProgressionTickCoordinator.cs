using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// The tick thread's race-mastery and race-unlock hand-offs. Grouped in
    /// one coordinator because they are one domain (race progression), not to
    /// save a task - see LegacyStoreTickCoordinator for the coordinator shape
    /// this repeats.
    /// </summary>
    internal static class RaceProgressionTickCoordinator
    {
        internal static void DrainMasteryUpdates(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.MasteryUpdateQueue.TryDequeue(out var masteryUpdate))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, masteryUpdate.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    ApplyMasteryUpdate(ref currentPayload, in masteryUpdate);
                }
            }
        }

        internal static void ApplyMasteryUpdate(ref TickStatePayload payload, in MasteryUpdateNotification masteryUpdate)
        {
            // Modul 13 fix: was gated on raw literals (1, 3, 4) that predate
            // RaceIds and never matched it - Vila updates (RaceId=2) were
            // silently dropped entirely, and RaceId 3/4 mislabeled Draugr's
            // and Kobold's levels as Vila's/Draugr's respectively.
            if (masteryUpdate.RaceId == RaceIds.Human) payload.HumanMasteryLevel = masteryUpdate.MasteryLevel;
            else if (masteryUpdate.RaceId == RaceIds.Vila) payload.VilaMasteryLevel = masteryUpdate.MasteryLevel;
            else if (masteryUpdate.RaceId == RaceIds.Draugr) payload.DraugrMasteryLevel = masteryUpdate.MasteryLevel;
            else if (masteryUpdate.RaceId == RaceIds.Kobold) payload.KoboldMasteryLevel = masteryUpdate.MasteryLevel;
            else if (masteryUpdate.RaceId == RaceIds.Vodnik) payload.VodnikMasteryLevel = masteryUpdate.MasteryLevel;
            else if (masteryUpdate.RaceId == RaceIds.Moosleute) payload.MoosleuteMasteryLevel = masteryUpdate.MasteryLevel;
            payload.IsDirty = true;
        }

        // Modul: race unlock feedback. ORs the newly granted race into
        // the live mask so the next outbound packet carries it and the
        // client can announce it. An offline player needs nothing here:
        // the row is already committed and login hydrates the mask from
        // it.
        internal static void DrainRaceUnlocks(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.RaceUnlockQueue.TryDequeue(out var raceUnlock))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, raceUnlock.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    ApplyRaceUnlock(ref currentPayload, in raceUnlock);
                }
            }
        }

        internal static void ApplyRaceUnlock(ref TickStatePayload payload, in RaceUnlockNotification raceUnlock)
        {
            if (raceUnlock.RaceId >= 1 && raceUnlock.RaceId <= 8)
            {
                payload.UnlockedRaceBitmask |= (byte)(1 << (raceUnlock.RaceId - 1));
                payload.IsDirty = true;
            }
        }
    }
}
