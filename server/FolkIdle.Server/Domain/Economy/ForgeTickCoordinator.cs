using System.Threading.Tasks;
using System;
using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Shared;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Economy
{
    /// <summary>
    /// The tick thread's forge-upgrade hand-off. See LegacyStoreTickCoordinator
    /// for the coordinator shape this repeats.
    /// </summary>
    internal static class ForgeTickCoordinator
    {
        internal static void DrainNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.ForgeUpgradeQueue.TryDequeue(out var forgeUpgrade))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, forgeUpgrade.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    Apply(ref currentPayload, in forgeUpgrade);
                }
            }
        }

        internal static void Apply(ref TickStatePayload payload, in ForgeUpgradeNotification forgeUpgrade)
        {
            payload.ForgeUpgradeCount++;
            if (forgeUpgrade.ResultingQualityTier > payload.HighestForgeSynthesisTier)
            {
                payload.HighestForgeSynthesisTier = forgeUpgrade.ResultingQualityTier;
            }
            payload.IsDirty = true;
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.ExecuteForgeFusion)
        internal static void HandleExecuteForgeFusion(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            // Modul: REPORTS, does not disconnect.
            //
            // Reported from play as "I press fuse, I get an error,
            // and nothing happens" - and nothing happening was the
            // session being torn down. Every input here is chosen
            // by the player from a list on screen: three item ids
            // they own, against a Forge level that gates the rarity
            // they are reaching for. Picking three items your Forge
            // is too small to combine is a mistake to be told
            // about, not evidence of tampering.
            //
            // This is the same defect the larder had, and the
            // comment there says so: force-disconnecting on a
            // mis-click is how eating food used to throw players off
            // the server.
            if (!ClientCommandValidator.ValidateFusionCommand(ref currentPayload, cmd.TargetId, cmd.SecondaryId, cmd.TertiaryId))
            {
                ctx.PlayerRegistry.EnqueueCommandResult(
                    currentPayload.PlayerId,
                    (byte)Network.CommandResultCode.GenericValidationFailure);
                return;
            }

            currentPayload.IsSuspended = true;
            ctx.CheckpointManager.FlushStateAndAdvance(ref currentPayload);
            
            long pId = currentPayload.PlayerId;
            long cTargetId = cmd.TargetId;
            long cSecId = cmd.SecondaryId;
            long cTerId = cmd.TertiaryId;

            var forgeEngine = ctx.ForgeEngine;
            var networkSystem = ctx.NetworkSystem;
            ctx.SafeDispatch("Forge.Fusion", pId, async () => {
                var result = await forgeEngine.ExecuteFusionAsync(pId, cTargetId, cSecId, cTerId);
                if (result == ForgeSplicingResult.InvalidRequest)
                {
                    networkSystem.ForceDisconnect(pId);
                    return;
                }
                networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
            });
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.RerollItemAffix)
        internal static void HandleRerollItemAffix(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateAffixReroll(ref currentPayload, cmd.TargetId, cmd.LimitPrice))
            {
                ctx.RemoveActivePlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.ForceDisconnect(ctx.RoutingPlayerId);
                return;
            }

            currentPayload.IsSuspended = true;
            ctx.CheckpointManager.FlushStateAndAdvance(ref currentPayload);
            
            long pId = currentPayload.PlayerId;
            long cTargetId = cmd.TargetId;
            int affixIndex = cmd.LimitPrice;

            // Modul: reroll operations, 2026-08-01. Everything below
            // is copied off the command struct BEFORE the lambda, so
            // the closure never captures `cmd` - it is a ref-local
            // over tick-owned memory that will have been reused by
            // the time the continuation runs.
            var rerollOperation = (Engine.RerollOperation)cmd.RerollOperationKind;
            uint autoMaxAttempts = cmd.RerollAutoMaxAttempts;
            byte stopMinRarity = cmd.RerollStopMinRarity;
            byte stopAffixIndex = cmd.RerollStopAffixIndex;

            var rerollEngine = ctx.RerollEngine;
            var networkSystem = ctx.NetworkSystem;
            ctx.SafeDispatch("Affix.Reroll", pId, async () => {
                if (autoMaxAttempts == 0U)
                {
                    await rerollEngine.ExecuteRerollAsync(pId, cTargetId, affixIndex, rerollOperation);
                }
                else
                {
                    // The affix id is carried as a 1-based index into
                    // AffixRegistry.Definitions rather than a string,
                    // because the packet is fixed-layout. 0 means
                    // "any stat".
                    string? requiredAffixId = null;
                    if (stopAffixIndex > 0 && stopAffixIndex <= Engine.AffixRegistry.Definitions.Length)
                    {
                        requiredAffixId = Engine.AffixRegistry.Definitions[stopAffixIndex - 1].Id;
                    }

                    var stopCondition = new Engine.AutoRerollStopCondition(
                        (Engine.AffixRarity)(stopMinRarity < 1 ? 1 : stopMinRarity),
                        requiredAffixId);

                    // The client's attempt count is a request, not a
                    // bound - AutoRerollPlanner clamps it, because an
                    // unbounded loop of Serializable transactions is a
                    // self-inflicted denial of service.
                    await rerollEngine.ExecuteAutoRerollAsync(
                        pId, cTargetId, affixIndex, rerollOperation, stopCondition, (int)autoMaxAttempts);
                }

                networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
            });
        }
    }
}
