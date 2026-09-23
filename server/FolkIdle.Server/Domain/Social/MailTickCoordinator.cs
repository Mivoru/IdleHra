using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Shared;
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

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.ClaimMailItem)
        internal static void HandleClaimMailItem(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateMailCommands(ref currentPayload, (byte)cmd.Command, cmd.TargetId))
            {
                ctx.TerminateSessionForSecurity(ctx.RoutingPlayerId);
                return;
            }

            long pId = currentPayload.PlayerId;
            long mailId = cmd.TargetId;
            var mailboxEngine = ctx.MailboxEngine;
            ctx.SafeDispatch("Mail.Claim", pId, async () => {
                await mailboxEngine.ClaimMailItemAsync(pId, mailId);
            });
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.ClaimAchievementReward)
        internal static void HandleClaimAchievementReward(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateAchievementClaimRequest(ref currentPayload, ref cmd))
            {
                ctx.TerminateSessionForSecurity(ctx.RoutingPlayerId);
                return;
            }

            long pId = currentPayload.PlayerId;
            uint achievementId = cmd.TargetAchievementId;

            if (ctx.LiveSessionContexts.TryGetValue(pId, out var sessionContext))
            {
                ctx.PlayerRegistry.AchievementClaimQueue.Enqueue(new AchievementClaimRequest
                {
                    PlayerId = pId,
                    AchievementId = achievementId,
                    LiveSession = sessionContext
                });
            }
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.DepositToBank || cmd.Command == CommandType.WithdrawFromBank)
        internal static void HandleRetiredBank(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: THE BANK IS RETIRED, and both commands are now
            // ignored rather than routed. See the RetireTheBank
            // migration: it was a 100-slot store that existed to
            // relieve a backpack cap the game no longer has, and an
            // item inside it could not be equipped, fused, rerolled or
            // sold - every one of those reads EquipmentInstances. Its
            // rows were moved there and the table dropped.
            //
            // Ignored rather than treated as a protocol violation: a
            // client still sending these is an old bundle, and
            // disconnecting a stale tab teaches nobody anything.
            // Deliberately empty.
        }
    }
}
