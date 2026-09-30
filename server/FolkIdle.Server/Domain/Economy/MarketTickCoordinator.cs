using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Shared;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Domain.Economy
{
    /// <summary>
    /// The tick thread's market-match hand-off, and the convention every
    /// later coordinator that needs to dispatch async work off the tick
    /// thread copies: SafeDispatchAsync is handed down as a cached delegate
    /// (SimulationEngine._safeDispatch) rather than reimplemented, per
    /// CLAUDE.md's cron-worker-silent-death trap.
    /// </summary>
    internal static class MarketTickCoordinator
    {
        internal static void DrainMatchNotifications(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers,
            Action<string, long, Func<Task>> safeDispatch,
            IDbContextFactory<FolkIdleDbContext> contextFactory)
        {
            while (registry.MarketMatchQueue.TryDequeue(out var notification))
            {
                ref var currentPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, notification.PlayerId);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref currentPayload))
                {
                    // GoldLedger: display only - the sale was recorded as income by its engine.
                    currentPayload.AddGold(notification.GoldDelta);
                    currentPayload.IsDirty = true;
                }
                // Modul: NO RESCUE CREDIT ANY MORE (2026-09-30). Every market
                // engine now credits the row inside the sale's own transaction
                // and posts here only to move a live display. A player who
                // logged out between the post and this drain has nothing to
                // display and nothing owed - the row already holds it, and
                // hydration reads it at their next login. The old rescue
                // credited the row a second time.
            }
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.MarketListItem || cmd.Command == CommandType.MarketBuyItem)
        internal static void HandleMarketListOrBuy(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidateMarketCommands(ref currentPayload, (byte)cmd.Command, cmd.TargetId, cmd.LimitPrice))
            {
                ctx.RemoveActivePlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.ForceDisconnect(ctx.RoutingPlayerId);
                return;
            }
            
            long pId = currentPayload.PlayerId;
            long targetId = cmd.TargetId;
            long price = cmd.LimitPrice;
            bool isBuy = cmd.Command == CommandType.MarketBuyItem;
            // The chest is unlimited, so a buy always has room.
            bool hasSpace = true;

            var escrowEngine = ctx.EscrowEngine;
            var networkSystem = ctx.NetworkSystem;
            var safeDispatch = ctx.SafeDispatch;

            // Modul: checkpoints off the tick thread (task 43). Suspend, flush
            // on CheckpointWriter, and only once that commits dispatch the
            // escrow work that reads the flushed gold. A failed flush never
            // trades: the ack un-suspends and answers CheckpointFailed.
            currentPayload.IsSuspended = true;
            ctx.CheckpointManager.RequestFlush(ref currentPayload, FlushReason.Command, then: () =>
            {
                safeDispatch("Market.EscrowOrder", pId, async () => {
                    if (isBuy)
                    {
                        await escrowEngine.BuyItemAsync(pId, targetId, hasSpace);
                    }
                    else
                    {
                        await escrowEngine.ListItemAsync(pId, targetId, price);
                    }
                    networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                });
                return Task.CompletedTask;
            });
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.PlaceLimitOrder)
        internal static void HandlePlaceLimitOrder(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            if (!ClientCommandValidator.ValidatePlaceLimitOrderRequest(ref currentPayload, ref cmd))
            {
                ctx.RemoveActivePlayer(ctx.RoutingPlayerId);
                ctx.NetworkSystem.ForceDisconnect(ctx.RoutingPlayerId);
                return;
            }

            long pId = currentPayload.PlayerId;
            bool isBuy = cmd.IsBuy == 1;
            long instanceId = cmd.TargetId;
            long price = cmd.LimitPrice;
            int qualityTier = cmd.QualityTier;
            // Modul: Play Mode audit fix. This used to synthesize a
            // bogus "ItemType_{TargetId}" string that never matched
            // any real MarketEquipmentInstance.BaseItemId - every BUY
            // limit order placed through the real wire protocol was
            // permanently unmatchable (only the direct-call unit test
            // passed a real baseItemId, bypassing this dispatcher
            // entirely). TargetId is the same numeric ContentRegistry
            // item id used by ConsumableEngine/CombatLootEngine -
            // resolving it here is the same GetItemBaseId lookup they
            // already use, not a new convention.
            string baseItemId = isBuy ? ContentRegistry.GetItemBaseId((int)cmd.TargetId) : "";

            var marketEngine = ctx.MarketEngine;
            var networkSystem = ctx.NetworkSystem;
            var safeDispatch = ctx.SafeDispatch;

            // Modul: checkpoints off the tick thread (task 43) - as above.
            currentPayload.IsSuspended = true;
            ctx.CheckpointManager.RequestFlush(ref currentPayload, FlushReason.Command, then: () =>
            {
                safeDispatch("Market.LimitOrder", pId, async () => {
                    await marketEngine.PlaceLimitOrderAsync(pId, isBuy, instanceId, price, baseItemId, qualityTier);
                    networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                });
                return Task.CompletedTask;
            });
        }
    }
}
