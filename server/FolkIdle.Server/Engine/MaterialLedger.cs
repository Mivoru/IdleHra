using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>Which way a material moved. Stored as a short - append only.</summary>
    public enum MaterialFlowDirection : short
    {
        /// <summary>Gathered by a character, live or away, or produced by a village building.</summary>
        Gathered = 1,
        /// <summary>Consumed by crafting, a building upgrade, the larder or a guild donation.</summary>
        Spent = 2,
        /// <summary>Produced while away and discarded because the Warehouse was full.</summary>
        LostToWarehouseCap = 3,
        /// <summary>Sold from the Village Chest for gold.</summary>
        Sold = 4,
        /// <summary>Binned from the Village Chest for nothing.</summary>
        Discarded = 5,
    }

    /// <summary>
    /// Task 79, phase 2: the one way to record a material moving in or out of
    /// a player's stock. Call it inside the transaction that moves the stack.
    /// </summary>
    /// <remarks>
    /// Modul: THE CAP ONLY DISCARDS WHILE AWAY. The live tick PAUSES village
    /// production when a stack reaches CalculateWarehouseMaxStorage (see
    /// SimulationEngine's accumulator guard), so nothing produced is thrown
    /// away there. The offline grant is the one place the Warehouse clamps an
    /// amount that was already produced - GrantVillagePassiveProductionAsync -
    /// and that is where LostToWarehouseCap is written. The screen says so.
    /// </remarks>
    public static class MaterialLedger
    {
        public static Task RecordAsync(FolkIdleDbContext db, long playerId, MaterialFlowDirection direction, string itemId, long amount)
            => RecordManyAsync(db, playerId, direction, new[] { new KeyValuePair<string, long>(itemId, amount) });

        /// <summary>
        /// One statement for a batch (the gathering writer carries a dozen
        /// materials at once). Non-positive amounts, empty ids and gold are
        /// skipped - gold has its own ledger.
        /// </summary>
        public static async Task RecordManyAsync(FolkIdleDbContext db, long playerId, MaterialFlowDirection direction, IEnumerable<KeyValuePair<string, long>> amounts)
        {
            // Sorted and grouped for the same two reasons CommodityLedger.AddManyAsync
            // gives: a stable lock order, and ON CONFLICT refuses one row twice.
            var merged = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var a in amounts)
            {
                if (a.Value <= 0 || string.IsNullOrEmpty(a.Key) || a.Key == "gold") continue;
                merged.TryGetValue(a.Key, out long sum);
                merged[a.Key] = sum + a.Value;
            }
            if (merged.Count == 0) return;

            var day = DateOnly.FromDateTime(DateTime.UtcNow);
            short dir = (short)direction;
            string[] ids = merged.Keys.ToArray();
            long[] quantities = merged.Values.ToArray();

            await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO material_flow_daily (""PlayerId"", ""Day"", ""ItemId"", ""Direction"", ""Amount"")
SELECT {playerId}, {day}, u.item, {dir}, u.qty FROM unnest({ids}::text[], {quantities}::bigint[]) AS u(item, qty)
ON CONFLICT (""PlayerId"", ""Day"", ""ItemId"", ""Direction"")
DO UPDATE SET ""Amount"" = material_flow_daily.""Amount"" + EXCLUDED.""Amount""");
        }
    }
}
