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
    }
}
