using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    // Modul: WHERE THE GOLD WENT (task 79). Every sink in the game debits the
    // same CommodityRecords "gold" row and nothing recorded which sink did it,
    // so "the economy under-spends" could be said but not measured, and a
    // player could not see what their gold had bought.
    //
    // One row per player, UTC day and category, incremented at the debit
    // inside the same transaction. A rollback takes the row with it. Composite
    // key (PlayerId, Day, Category), set in FolkIdleDbContext. Written ONLY
    // through Engine.GoldLedger, whose upsert names this table in raw SQL, so
    // keep the [Table] name and the SQL in step.
    [Table("gold_spend_daily")]
    public class GoldSpendDaily
    {
        public long PlayerId { get; set; }

        /// <summary>UTC day of the debit.</summary>
        public DateOnly Day { get; set; }

        /// <summary>Engine.GoldSpendCategory.</summary>
        public short Category { get; set; }

        public long Amount { get; set; }
    }
}
