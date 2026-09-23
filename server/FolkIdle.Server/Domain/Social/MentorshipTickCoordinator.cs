using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Social
{
    /// <summary>
    /// The tick thread's two mentorship hand-offs. Grouped in one coordinator
    /// because they are one domain, not to save a task - see
    /// LegacyStoreTickCoordinator for the coordinator shape this repeats.
    /// </summary>
    internal static class MentorshipTickCoordinator
    {
        internal static void DrainMentorshipUpdates(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.MentorshipUpdateQueue.TryDequeue(out var mentorshipUpdate))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, mentorshipUpdate.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    ApplyMentorshipUpdate(ref currentPayload, in mentorshipUpdate);
                }
            }
        }

        internal static void ApplyMentorshipUpdate(ref TickStatePayload payload, in MentorshipUpdateNotification mentorshipUpdate)
        {
            payload.CachedMentorCount++;
        }

        // Nothing enqueues these any more; drained so a stale entry
        // from a pre-removal process cannot sit in the queue forever.
        internal static void DrainContractUpdates(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.MentorshipContractUpdateQueue.TryDequeue(out var mentorshipNotif))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, mentorshipNotif.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    ApplyContractUpdate(ref currentPayload, in mentorshipNotif);
                }
            }
        }

        internal static void ApplyContractUpdate(ref TickStatePayload payload, in MentorshipContractUpdateNotification mentorshipNotif)
        {
            payload.ActiveMentorPlayerId = mentorshipNotif.MentorPlayerId;
            payload.MentorshipExpBonusMultiplier = mentorshipNotif.ExpBonusMultiplier;
            payload.ActiveMentorshipContractCount = mentorshipNotif.ActiveContractCount;
            if (mentorshipNotif.XpPenaltyExpiresEpoch > 0)
            {
                payload.XpPenaltyExpiresEpoch = mentorshipNotif.XpPenaltyExpiresEpoch;
            }
            payload.IsDirty = true;
        }
    }
}
