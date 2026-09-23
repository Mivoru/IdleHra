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
                    currentPayload.AddGold(notification.GoldDelta);
                    currentPayload.IsDirty = true;
                }
                else if (notification.GoldDelta != 0L)
                {
                    // Modul: market settlement rescue, 2026-08-01.
                    //
                    // MarketEscrowEngine chooses between crediting the
                    // database directly and posting here, based on whether
                    // the seller was online AT THAT MOMENT. If they logged
                    // out between that check and this drain - a window of up
                    // to one tick plus the escrow transaction's tail - this
                    // used to dequeue the notification, find no payload, and
                    // silently drop it. The database was never credited on
                    // that path, so the seller permanently lost the proceeds
                    // of a completed sale with no error and no telemetry.
                    //
                    // Falling back to the offline path closes it. Crediting
                    // the row directly is safe precisely because the player
                    // is NOT active: nothing holds a live CurrentGold that
                    // this could race, and hydration reads this row at their
                    // next login.
                    long rescuePlayerId = notification.PlayerId;
                    long rescueGold = notification.GoldDelta;

                    safeDispatch("Market.SettlementRescue", 0L, async () =>
                    {
                        await using var rescueDb = await contextFactory.CreateDbContextAsync();

                        var goldRow = await rescueDb.CommodityRecords
                            .FirstOrDefaultAsync(c => c.PlayerId == rescuePlayerId && c.ItemId == "gold");

                        if (goldRow == null)
                        {
                            rescueDb.CommodityRecords.Add(new CommodityRecord
                            {
                                PlayerId = rescuePlayerId,
                                ItemId = "gold",
                                Quantity = rescueGold
                            });
                        }
                        else
                        {
                            goldRow.Quantity += rescueGold;
                        }

                        await rescueDb.SaveChangesAsync();
                    });
                }
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
            
            currentPayload.IsSuspended = true;
            ctx.CheckpointManager.FlushStateAndAdvance(ref currentPayload);

            long pId = currentPayload.PlayerId;
            long targetId = cmd.TargetId;
            long price = cmd.LimitPrice;
            bool isBuy = cmd.Command == CommandType.MarketBuyItem;
            // The chest is unlimited, so a buy always has room.
            bool hasSpace = true;

            var escrowEngine = ctx.EscrowEngine;
            var networkSystem = ctx.NetworkSystem;
            ctx.SafeDispatch("Market.EscrowOrder", pId, async () => {
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

            currentPayload.IsSuspended = true;
            ctx.CheckpointManager.FlushStateAndAdvance(ref currentPayload);

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
            ctx.SafeDispatch("Market.LimitOrder", pId, async () => {
                await marketEngine.PlaceLimitOrderAsync(pId, isBuy, instanceId, price, baseItemId, qualityTier);
                networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand { PlayerId = pId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
            });
        }
    }
}
