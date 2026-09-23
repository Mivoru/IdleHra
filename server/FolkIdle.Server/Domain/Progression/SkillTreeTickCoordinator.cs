using System.Threading.Tasks;
using System;
using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Shared;
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

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.PurchaseSkillTreeLevel)
        internal static void HandlePurchaseSkillTreeLevel(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: skill tree. Same shape as the inheritance
            // purchase above and for the same reasons - dispatched
            // off the tick, the point balance and the level written
            // in one Serializable FOR UPDATE transaction, and a
            // branch id out of range REFUSED rather than treated as
            // a protocol violation. A branch id is a menu choice.
            long treePlayerId = currentPayload.PlayerId;
            int treeBranchId = (int)cmd.TargetId;
            var skillTreeEngine = ctx.SkillTreeEngine;
            ctx.SafeDispatch("SkillTree.Purchase", treePlayerId, async () => {
                if (skillTreeEngine != null) await skillTreeEngine.PurchaseLevelAsync(treePlayerId, treeBranchId);
            });
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.RespecSkillTree)
        internal static void HandleRespecSkillTree(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: respec. Ring 2 forks and taking one side locks
            // the other for a ninety-day season, so there has to be
            // a way back - and it cannot be free and unlimited, or
            // the exclusivity that IS the choice would be gone.
            // One free a season, then a purchased grant.
            //
            // Dispatched off the tick like every other write: the
            // cleared levels come back through SkillTreeSyncQueue,
            // because the tick thread owns the payload.
            long respecPlayerId = currentPayload.PlayerId;
            var skillTreeEngine = ctx.SkillTreeEngine;
            ctx.SafeDispatch("SkillTree.Respec", respecPlayerId, async () => {
                if (skillTreeEngine != null) await skillTreeEngine.RespecAsync(respecPlayerId);
            });
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.RequestUnlockSkill || cmd.Command == CommandType.RequestCastSkill)
        internal static void HandleRetiredActiveSkill(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: RequestUnlockSkill and RequestCastSkill are RETIRED,
            // with the four active skills they drove. Measured, that
            // rotation was +90% damage - +136% with the status synergy -
            // available only to a player clicking every three seconds,
            // in a game whose whole premise is not clicking. See
            // SkillTreeRegistry for what the points buy now.
            //
            // Ignored rather than rejected, like CommandType.CraftItem:
            // a client still sending them is a stale bundle, not an
            // attack, and disconnecting a tab that has not reloaded
            // teaches nobody anything.
            // Deliberately empty.
        }
    }
}
