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
    /// The tick thread's Inheritance hand-off. See LegacyStoreTickCoordinator
    /// for the coordinator shape this repeats: DrainNotifications owns the
    /// while/TryDequeue/ref-resolution loop, Apply is the independently
    /// callable per-payload mutation.
    /// </summary>
    internal static class InheritanceTickCoordinator
    {
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.InheritanceSyncQueue.TryDequeue(out var inheritNotif))
            {
                ref var inheritPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, inheritNotif.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref inheritPayload))
                {
                    Apply(ref inheritPayload, in inheritNotif);
                }
            }
        }

        internal static void Apply(ref TickStatePayload payload, in InheritanceSyncNotification inheritNotif)
        {
            SimulationEngine.SetInheritanceLevel(ref payload, inheritNotif.StatId, inheritNotif.NewLevel);
            payload.IsDirty = true;
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.PurchaseInheritanceLevel)
        internal static void HandlePurchaseInheritanceLevel(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: inheritance stats. Dispatched off the tick like
            // every other DB-transactional command; the balance
            // check, the deduction and the level write all resolve
            // in one Serializable FOR UPDATE transaction.
            //
            // TargetId carries the stat id, the way the skill
            // commands carry a skill id on it. The engine validates
            // the range itself and refuses rather than
            // disconnecting - a stat id is a menu choice, not a
            // capability claim, so a stale client asking for stat 9
            // deserves a rejection and not a kick.
            long inheritPlayerId = currentPayload.PlayerId;
            int inheritStatId = (int)cmd.TargetId;
            var inheritanceEngine = ctx.InheritanceEngine;
            ctx.SafeDispatch("Inheritance.Purchase", inheritPlayerId, async () => {
                if (inheritanceEngine != null) await inheritanceEngine.PurchaseLevelAsync(inheritPlayerId, inheritStatId);
            });
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.PurchaseAncestorSlot || cmd.Command == CommandType.KeepAncestor || cmd.Command == CommandType.ReleaseAncestor || cmd.Command == CommandType.AssignCharacterSlot)
        internal static void HandleHallOfAncestors(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: the Hall of Ancestors. Four commands over one
            // validator, because they share a shape: a character or
            // nothing, and never a field belonging to something else.
            if (!ClientCommandValidator.ValidateHallOfAncestorsRequest(ref currentPayload, ref cmd))
            {
                ctx.RemoveActivePlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.PurgeTokensForPlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.ForceDisconnect(ctx.RoutingPlayerId);
                return;
            }

            long hallPlayerId = currentPayload.PlayerId;
            var hallCommand = cmd.Command;
            var hallCharacterId = cmd.TargetGuid;
            int hallSlotIndex = (int)cmd.RequestedSlotIndex;

            var hallOfAncestorsEngine = ctx.HallOfAncestorsEngine;
            var networkSystem = ctx.NetworkSystem;
            ctx.SafeDispatch("Hall." + hallCommand, hallPlayerId, async () => {
                if (hallOfAncestorsEngine == null) return;

                switch (hallCommand)
                {
                    case CommandType.PurchaseAncestorSlot:
                        await hallOfAncestorsEngine.PurchaseSlotAsync(hallPlayerId);
                        break;
                    case CommandType.KeepAncestor:
                        await hallOfAncestorsEngine.SetKeptAsync(hallPlayerId, hallCharacterId, true);
                        break;
                    case CommandType.ReleaseAncestor:
                        await hallOfAncestorsEngine.SetKeptAsync(hallPlayerId, hallCharacterId, false);
                        break;
                    case CommandType.AssignCharacterSlot:
                        await hallOfAncestorsEngine.AssignSlotAsync(hallPlayerId, hallCharacterId, hallSlotIndex);
                        // Which character is in which slot decides
                        // everything the payload caches about the
                        // active register - gear, activity, stats -
                        // so the tick has to re-read it rather than
                        // keep simulating the character that moved.
                        networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = hallPlayerId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                        break;
                }
            });
        }
    }
}
