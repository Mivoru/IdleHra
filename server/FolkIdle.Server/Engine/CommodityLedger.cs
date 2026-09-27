using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The one way to ADD to a backpack stack (<c>CommodityRecords</c>), gold
    /// included. Decrements stay where they are: a <c>FOR UPDATE</c> read, a
    /// balance check, then <c>Quantity -= cost</c> is already correct, because
    /// the row it checks is the row it writes.
    /// </summary>
    // Modul: WHY THIS EXISTS (task 44, 2026-09-27). About thirty sites used to
    // do "SELECT ... FOR UPDATE; if null, INSERT". FOR UPDATE locks a row that
    // EXISTS; when there is none it locks nothing, so two transactions under
    // Read Committed could both see "no gold row" and both insert one. The
    // index on (PlayerId, ItemId) was not unique, so nothing refused the
    // second row, and every reader that takes FirstOrDefault then saw half a
    // balance. The index is unique now (migration
    // MakeCommodityRecordsPlayerItemUnique) and this is the upsert that targets
    // it: the conflict is resolved inside Postgres, atomically, whoever wins.
    //
    // A guard test (CommodityLedgerTests) allows `new CommodityRecord` only
    // here and in the two seeders, so a new check-then-insert cannot come back.
    public static class CommodityLedger
    {
        /// <summary>
        /// Adds <paramref name="delta"/> to the player's stack of
        /// <paramref name="itemId"/>, creating the row when there is none, and
        /// returns the quantity after the add. Runs immediately - NOT at
        /// SaveChanges - inside whatever transaction <paramref name="db"/>
        /// currently holds, so a rollback undoes it too.
        /// </summary>
        public static async Task<long> AddAsync(FolkIdleDbContext db, long playerId, string itemId, long delta)
        {
            if (string.IsNullOrEmpty(itemId)) throw new ArgumentException("itemId is required", nameof(itemId));

            var result = await db.Database.SqlQuery<long>($@"
INSERT INTO ""CommodityRecords"" (""PlayerId"", ""ItemId"", ""Quantity"") VALUES ({playerId}, {itemId}, {delta})
ON CONFLICT (""PlayerId"", ""ItemId"")
DO UPDATE SET ""Quantity"" = ""CommodityRecords"".""Quantity"" + EXCLUDED.""Quantity""
RETURNING ""Quantity"" AS ""Value""").ToListAsync();
            long after = result[0];
            RebaseTracked(db, playerId, itemId, after);
            return after;
        }

        /// <summary>
        /// <see cref="AddAsync"/> for a batch, in ONE statement. Non-positive
        /// deltas and empty ids are skipped. Used by the loot and gathering
        /// writers, where one request carries a dozen materials and a round
        /// trip per material would undo the batching those paths exist for.
        /// </summary>
        public static async Task AddManyAsync(FolkIdleDbContext db, long playerId, IEnumerable<KeyValuePair<string, long>> deltas)
        {
            // Modul: SORTED, so two batches for one player lock their rows in
            // the same order and cannot deadlock against each other. Grouped,
            // because ON CONFLICT refuses to touch one row twice in a single
            // statement ("cannot affect row a second time").
            var merged = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var d in deltas)
            {
                if (d.Value <= 0 || string.IsNullOrEmpty(d.Key)) continue;
                merged.TryGetValue(d.Key, out long sum);
                merged[d.Key] = sum + d.Value;
            }
            if (merged.Count == 0) return;

            string[] ids = merged.Keys.ToArray();
            long[] quantities = merged.Values.ToArray();

            var rows = await db.Database.SqlQuery<LedgerRow>($@"
INSERT INTO ""CommodityRecords"" (""PlayerId"", ""ItemId"", ""Quantity"")
SELECT {playerId}, u.item, u.qty FROM unnest({ids}::text[], {quantities}::bigint[]) AS u(item, qty)
ON CONFLICT (""PlayerId"", ""ItemId"")
DO UPDATE SET ""Quantity"" = ""CommodityRecords"".""Quantity"" + EXCLUDED.""Quantity""
RETURNING ""ItemId"", ""Quantity""").ToListAsync();

            foreach (var row in rows) RebaseTracked(db, playerId, row.ItemId, row.Quantity);
        }

        internal sealed class LedgerRow
        {
            public string ItemId { get; set; } = string.Empty;
            public long Quantity { get; set; }
        }

        private static void RebaseTracked(FolkIdleDbContext db, long playerId, string itemId, long after)
        {
            // Modul: THE CHANGE TRACKER DOES NOT SEE RAW SQL. If this context
            // already tracks the row (a caller read it earlier in the same
            // unit of work), its Quantity is now stale, and EF writes an
            // UPDATE as an ABSOLUTE value - so a later `row.Quantity -= fee`
            // plus SaveChanges would silently erase this add. Rebase the
            // tracked copy onto the database's value, keeping any change the
            // caller has already made to it as a pending delta on top.
            var tracked = db.ChangeTracker.Entries<CommodityRecord>()
                .FirstOrDefault(e => e.Entity.PlayerId == playerId && e.Entity.ItemId == itemId
                                     && e.State != EntityState.Added && e.State != EntityState.Detached);
            if (tracked != null)
            {
                // A stack spent to nothing and staged for deletion in this unit
                // of work is not empty any more: keep the row, or SaveChanges
                // would delete the add along with it.
                if (tracked.State == EntityState.Deleted) tracked.State = EntityState.Modified;

                var prop = tracked.Property(c => c.Quantity);
                long pending = prop.CurrentValue - prop.OriginalValue;
                prop.OriginalValue = after;
                prop.CurrentValue = after + pending;
                prop.IsModified = pending != 0;
            }
        }
    }
}
