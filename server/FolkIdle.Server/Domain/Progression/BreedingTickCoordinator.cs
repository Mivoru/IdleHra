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
    /// The tick thread's birth hand-off. See LegacyStoreTickCoordinator for
    /// the coordinator shape this repeats.
    /// </summary>
    internal static class BreedingTickCoordinator
    {
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.BirthNotificationQueue.TryDequeue(out var birthNotification))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, birthNotification.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    Apply(ref currentPayload, in birthNotification);
                }
            }
        }

        internal static void Apply(ref TickStatePayload payload, in BirthNotification birthNotification)
        {
            // Modul: a birth used to bump VillagePopulation - the
            // village's WORK slots, which the infrastructure update
            // overwrites from the buildings - so a child read as a
            // worker until the next village change. What a birth
            // does move is the gold the engine spent on it: the row
            // is already debited, so the live balance follows it and
            // the pending delta is left alone.
            payload.CurrentGold = System.Math.Max(0L, payload.CurrentGold - birthNotification.GoldSpent);
            payload.IsDirty = true;
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.ExecuteBreeding)
        internal static void HandleExecuteBreeding(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateBreedingRequest(ref currentPayload, ref cmd))
            {
                ctx.RemoveActivePlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.PurgeTokensForPlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.ForceDisconnect(ctx.RoutingPlayerId);
                return;
            }

            long pId = currentPayload.PlayerId;
            var patId = cmd.TargetGuid;
            var matId = cmd.SecondaryGuid;
            int selectionMask = cmd.BreedingSelectionMask;

            var breedingEngine = ctx.BreedingEngine;
            ctx.SafeDispatch("Breeding.Execute", pId, async () => {
                await breedingEngine.ExecuteBreedingAsync(pId, patId, matId, selectionMask);
            });
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.ExecuteVillagerBreeding)
        internal static void HandleExecuteVillagerBreeding(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: hero x villager - THE standard pair. The gene pool
            // the village fills up every season was inert until this
            // branch existed; nothing could marry into it.
            if (!ClientCommandValidator.ValidateVillagerBreedingRequest(ref currentPayload, ref cmd))
            {
                ctx.RemoveActivePlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.PurgeTokensForPlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.ForceDisconnect(ctx.RoutingPlayerId);
                return;
            }

            long pId = currentPayload.PlayerId;
            var heroId = cmd.TargetGuid;
            long newcomerId = cmd.TargetId;
            int villagerSelectionMask = cmd.BreedingSelectionMask;

            var breedingEngine = ctx.BreedingEngine;
            ctx.SafeDispatch("Breeding.ExecuteVillager", pId, async () => {
                await breedingEngine.ExecuteHeroVillagerBreedingAsync(pId, heroId, newcomerId, villagerSelectionMask);
            });
        }
    }
}
