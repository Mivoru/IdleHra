using System;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Social
{
    /// <summary>
    /// The tick thread's guild contribution commands: materials, treasury gold/equipment and the logistics depot.
    ///
    /// Modul: a COORDINATOR, not an engine - a static class with no fields,
    /// called synchronously on the 10Hz tick thread by SimulationEngine's
    /// command dispatch table after CommandGate has said Proceed. It owns no
    /// thread, timer or state; anything asynchronous goes through the
    /// SafeDispatch delegate it is handed, never a task it starts itself.
    /// </summary>
    internal static class GuildTickCoordinator
    {
        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.ContributeToGuild)
        internal static void HandleContributeToGuild(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateGuildContributions(ref currentPayload, cmd.LimitPrice))
            {
                ctx.RemoveActivePlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.ForceDisconnect(ctx.RoutingPlayerId);
                return;
            }

            long guildId = currentPayload.GuildId;
            long quantity = cmd.LimitPrice;
            int itemDefinitionId = (int)cmd.TargetId;
            long pId = currentPayload.PlayerId;

            if (guildId > 0 && quantity > 0)
            {
                var guildLogisticsEngine = ctx.GuildLogisticsEngine;
                ctx.SafeDispatch("Guild.Contribution", pId, async () => {
                    await guildLogisticsEngine.ExecuteGuildContributionAsync(pId, guildId, quantity, itemDefinitionId);
                });
            }
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.ContributeGuildTreasury)
        internal static void HandleContributeGuildTreasury(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateGuildTreasuryContribution(ref currentPayload, ref cmd))
            {
                ctx.TerminateSessionForSecurity(ctx.RoutingPlayerId);
                return;
            }

            currentPayload.IsSuspended = true;
            ctx.CheckpointManager.FlushStateAndAdvance(ref currentPayload);

            long pId = currentPayload.PlayerId;
            // Modul: Play Mode audit fix. Previously trusted
            // cmd.SecondaryId as the target guild id directly -
            // a player could donate their own gold/equipment
            // toward ANY guild's tier, not just their own.
            // Derives from the player's own live GuildId instead,
            // matching how the materials/Monolith contribution
            // branch already resolves guild membership.
            long guildId = currentPayload.GuildId;
            bool isGold = cmd.TargetId == 0;
            long instanceId = cmd.TargetId;
            long goldAmount = cmd.LimitPrice;

            var guildEngine = ctx.GuildEngine;
            var networkSystem = ctx.NetworkSystem;
            ctx.SafeDispatch("Guild.ContributeGoldOrEquipment", pId, async () => {
                if (isGold)
                {
                    await guildEngine.ContributeGoldAsync(pId, guildId, goldAmount);
                }
                else
                {
                    await guildEngine.ContributeEquipmentAsync(pId, guildId, instanceId);
                }
                networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
            });
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.DepositGuildMaterial)
        internal static void HandleDepositGuildMaterial(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateGuildDepositRequest(ref currentPayload, ref cmd))
            {
                ctx.RemoveActivePlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.PurgeTokensForPlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.ForceDisconnect(ctx.RoutingPlayerId);
                return;
            }

            long pId = currentPayload.PlayerId;
            long guildId = currentPayload.GuildId;
            uint materialId = cmd.MaterialId;
            uint quantity = cmd.DepositQuantity;

            var guildLogisticsDepotEngine = ctx.GuildLogisticsDepotEngine;
            ctx.SafeDispatch("Guild.DepositMaterial", pId, async () => {
                await guildLogisticsDepotEngine.DepositMaterialAsync(pId, guildId, materialId, quantity);
            });
        }
    }
}
