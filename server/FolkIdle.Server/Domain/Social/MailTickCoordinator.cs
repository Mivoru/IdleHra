using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Social
{
    /// <summary>
    /// The tick thread's mail-claim hand-off. Uses the SafeDispatchAsync
    /// convention Task 1.18's MarketTickCoordinator established: the
    /// delegate is handed down rather than reimplemented.
    /// </summary>
    internal static class MailTickCoordinator
    {
        internal static void DrainClaimRequests(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers,
            Action<string, long, Func<Task>> safeDispatch,
            MailboxAndBankEngine mailboxEngine)
        {
            while (registry.MailClaimRequestQueue.TryDequeue(out var req))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, req.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    {
                        currentPayload.AddGold(req.GoldAttachment);
                        currentPayload.IsDirty = true;
                        safeDispatch("MailClaim.Accept", req.PlayerId, async () => { await mailboxEngine.CommitMailClaimAsync(req.PlayerId, req.MailId, true); });
                    }
                }
            }
        }
    }
}
