using System.Threading.Tasks;
using System;
using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Shared;
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

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.AssignMentor || cmd.Command == CommandType.EstablishMentorship || cmd.Command == CommandType.TerminateMentorship)
        internal static void HandleRetiredMentorship(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: MENTORSHIP IS GONE - ignored, not rejected.
            //
            // The Academy, the mentor slots and the contracts were
            // removed as a feature: three screens and an XP penalty
            // that existed to make one number slightly larger, in a
            // game whose social half is guilds.
            //
            // Ignoring rather than disconnecting is deliberate and
            // is the same rule the removed active skills follow. A
            // client built before the removal still has the buttons,
            // and a player pressing one deserves nothing happening -
            // not to be thrown off the server for sending a command
            // that was valid when their tab was opened.
        }
    }
}
