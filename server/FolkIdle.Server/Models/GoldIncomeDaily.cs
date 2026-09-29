using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    // Modul: WHERE THE GOLD CAME FROM (task 79, phase 2). The mirror of
    // gold_spend_daily. One row per player, UTC day and source.
    //
    // Two kinds of writer, and the difference is the whole design:
    //  - gold an engine credits to CommodityRecords["gold"] itself (a sale, a
    //    mail claim, the login reward) is recorded beside that credit, in the
    //    same transaction;
    //  - gold that rides TickStatePayload.RedisPendingGoldDelta (a kill, the
    //    Town Hall, auto-salvage, fighting while away) is tallied on the
    //    payload (GoldIncomeTally) and written by the CHECKPOINT, in the
    //    checkpoint's own transaction. That gold is banked by Redis
    //    write-behind OR by the checkpoint, depending on whether Redis is up,
    //    so the bank is not one place to count it - the checkpoint is.
    //
    // Written ONLY through Engine.GoldLedger, whose upsert names this table in
    // raw SQL, so keep the [Table] name and the SQL in step.
    [Table("gold_income_daily")]
    public class GoldIncomeDaily
    {
        public long PlayerId { get; set; }

        /// <summary>UTC day it was recorded. For a tallied source, the day its checkpoint committed.</summary>
        public DateOnly Day { get; set; }

        /// <summary>Engine.GoldIncomeSource.</summary>
        public short Source { get; set; }

        public long Amount { get; set; }
    }
}
