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
    /// The tick thread's two village hand-offs. Grouped in one coordinator
    /// because they are one domain, not to save a task - see
    /// LegacyStoreTickCoordinator for the coordinator shape this repeats.
    /// </summary>
    internal static class VillageTickCoordinator
    {
        internal static void DrainInfrastructureUpdates(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.InfrastructureUpdateQueue.TryDequeue(out var updateNotif))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, updateNotif.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    ApplyInfrastructureUpdate(ref currentPayload, in updateNotif);
                }
            }
        }

        internal static void ApplyInfrastructureUpdate(ref TickStatePayload payload, in InfrastructureUpdateNotification updateNotif)
        {
            payload.ForgeLevel = updateNotif.ForgeLevel;
            payload.InnLevel = updateNotif.InnLevel;
            payload.BreedingLevel = updateNotif.BreedingLevel;
            payload.AcademyLevel = updateNotif.AcademyLevel;
            payload.CurrentPopulationCount = updateNotif.CurrentPopulationCount;
            payload.VillagePopulation = updateNotif.CurrentPopulationCount;
            payload.CachedCurrentToolTier = updateNotif.CurrentToolTier;
            payload.CachedInnMaturationBonus = updateNotif.InnMaturationBonus;
            payload.CachedMaxPopulationCapacity = updateNotif.MaxPopulationCapacity;
            payload.LumberjackLevel = updateNotif.LumberjackLevel;
            payload.MineLevel = updateNotif.MineLevel;
            payload.WarehouseLevel = updateNotif.WarehouseLevel;
            payload.TownHallLevel = updateNotif.TownHallLevel;
            payload.CraftingWorkshopLevel = updateNotif.CraftingWorkshopLevel;
            payload.PendingUpgradeBuildingId = updateNotif.PendingUpgradeBuildingId;
            payload.PendingUpgradeCompletesAtEpoch = updateNotif.PendingUpgradeCompletesAtEpoch;
            // Modul: the row is already debited (VillageManagementEngine),
            // so the live balance follows it and the pending delta is left
            // alone - the same reasoning as BirthNotification.GoldSpent.
            payload.CurrentGold = System.Math.Max(0L, payload.CurrentGold - updateNotif.GoldSpent);
            payload.IsDirty = true;
        }

        internal static void DrainRecruitmentUpdates(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.VillagerRecruitmentUpdateQueue.TryDequeue(out var recruitmentNotif))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, recruitmentNotif.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    ApplyRecruitmentUpdate(ref currentPayload, in recruitmentNotif);
                }
            }
        }

        internal static void ApplyRecruitmentUpdate(ref TickStatePayload payload, in VillagerRecruitmentNotification recruitmentNotif)
        {
            // Modul: the row is already debited (VillageArrivalEngine.RecruitAsync),
            // so the live balance follows it and the pending delta is left
            // alone - the same reasoning as BirthNotification.GoldSpent.
            payload.CurrentGold = System.Math.Max(0L, payload.CurrentGold - recruitmentNotif.GoldSpent);
            payload.IsDirty = true;
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.UpgradeBuilding)
        internal static void HandleUpgradeBuilding(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateVillageManagementRequest(ref currentPayload, ref cmd))
            {
                ctx.RemoveActivePlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.PurgeTokensForPlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.ForceDisconnect(ctx.RoutingPlayerId);
                return;
            }

            long pId = currentPayload.PlayerId;
            uint buildingId = cmd.TargetBuildingId;
            
            var villageManagementEngine = ctx.VillageManagementEngine;
            ctx.SafeDispatch("Village.UpgradeBuilding", pId, async () => {
                await villageManagementEngine.ExecuteUpgradeBuildingAsync(pId, buildingId);
            });
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.EvictVillager)
        internal static void HandleEvictVillager(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateVillageManagementRequest(ref currentPayload, ref cmd))
            {
                ctx.RemoveActivePlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.PurgeTokensForPlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.ForceDisconnect(ctx.RoutingPlayerId);
                return;
            }

            long pId = currentPayload.PlayerId;
            uint villagerSlot = cmd.TargetVillagerSlot;

            var villageManagementEngine = ctx.VillageManagementEngine;
            ctx.SafeDispatch("Village.EvictVillager", pId, async () => {
                await villageManagementEngine.ExecuteEvictVillagerAsync(pId, villagerSlot);
            });
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.RecruitVillager || cmd.Command == CommandType.DismissNewcomer)
        internal static void HandleRecruitOrDismissVillager(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: the village as something the player DOES. The
            // recruitment price and the refusals were written and
            // tested, DismissAsync existed, and neither had a way in -
            // so a full village was a dead end and the gold sink the top
            // of the economy lacks was unreachable.
            if (!ClientCommandValidator.ValidateVillageRosterRequest(ref currentPayload, ref cmd))
            {
                ctx.RemoveActivePlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.PurgeTokensForPlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.ForceDisconnect(ctx.RoutingPlayerId);
                return;
            }

            long pId = currentPayload.PlayerId;
            bool isRecruit = cmd.Command == CommandType.RecruitVillager;
            long newcomerId = cmd.TargetId;

            var villageManagementEngine = ctx.VillageManagementEngine;
            ctx.SafeDispatch(isRecruit ? "Village.Recruit" : "Village.Dismiss", pId, async () => {
                if (isRecruit)
                {
                    await villageManagementEngine.ExecuteRecruitVillagerAsync(pId);
                }
                else
                {
                    await villageManagementEngine.ExecuteDismissNewcomerAsync(pId, newcomerId);
                }
            });
        }
    }
}
