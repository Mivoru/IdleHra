using Microsoft.EntityFrameworkCore;
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
    /// <summary>One batch of live village production, already split into common and rare.</summary>
    public struct VillageProductionGrant
    {
        public long PlayerId;
        public int LumberjackLevel;
        public int MineLevel;
        public int WarehouseLevel;
        public long Log;
        public long RareLog;
        public long Ore;
        public long RareOre;
    }

    internal static class VillageTickCoordinator
    {
        /// <summary>
        /// Writes the live tick's village production (SimulationEngine
        /// .VillageProductionQueue) off the tick, one transaction per batch,
        /// through VillageManagementEngine.GrantProductionAsync - the Warehouse
        /// cap and the material ledger the offline window writes through.
        /// </summary>
        /// <remarks>
        /// Modul: BOUNDED, per the worker-loop rule: the depth is read once and
        /// only that many are taken, so a producer cannot keep this loop on the
        /// tick. A batch is a minute of one player's village, so the depth is
        /// at most the online population.
        ///
        /// A failed write is kept rather than lost: the batch goes to the
        /// durable retry outbox (audit #18) under the source type the offline
        /// village grant already uses - it is the same material, from the same
        /// buildings.
        /// </remarks>
        internal static void DrainProductionGrants(
            Action<string, long, Func<Task>> safeDispatch,
            Microsoft.EntityFrameworkCore.IDbContextFactory<FolkIdle.Server.Models.FolkIdleDbContext> contextFactory)
        {
            int budget = SimulationEngine.VillageProductionQueue.Count;
            for (int i = 0; i < budget && SimulationEngine.VillageProductionQueue.TryDequeue(out var grant); i++)
            {
                var batch = grant;
                safeDispatch("Village.Production", 0L, () => WriteProductionGrantAsync(contextFactory, batch));
            }
        }

        internal static async Task WriteProductionGrantAsync(
            Microsoft.EntityFrameworkCore.IDbContextFactory<FolkIdle.Server.Models.FolkIdleDbContext> contextFactory,
            VillageProductionGrant grant)
        {
            await using var db = await contextFactory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            try
            {
                await VillageManagementEngine.GrantProductionAsync(
                    db, grant.PlayerId, grant.LumberjackLevel, grant.MineLevel, grant.WarehouseLevel,
                    grant.Log, grant.RareLog, grant.Ore, grant.RareOre);
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Village: live production for player {grant.PlayerId} failed and was rolled back: {ex.Message} - queued for retry.");

                var lumberjackMats = VillageManagementEngine.GetTierMaterials(grant.LumberjackLevel);
                var mineMats = VillageManagementEngine.GetTierMaterials(grant.MineLevel);
                var deltas = new Dictionary<string, long>();
                if (grant.Log > 0) deltas[lumberjackMats.Log] = grant.Log;
                if (grant.RareLog > 0) deltas[lumberjackMats.RareLog] = grant.RareLog;
                if (grant.Ore > 0) deltas[mineMats.Ore] = grant.Ore;
                if (grant.RareOre > 0) deltas[mineMats.RareOre] = grant.RareOre;
                db.ChangeTracker.Clear();
                await PendingGrantOutbox.EnqueueCommodityDeltasAsync(
                    db, grant.PlayerId, FolkIdle.Server.Models.PendingGrantSourceType.OfflineVillageProduction, deltas);
            }
        }

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
