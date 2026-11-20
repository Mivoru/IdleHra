using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    // Modul: WHERE THE DIAMONDS CAME FROM AND WENT (2026-11-20). Gold has had
    // gold_income_daily / gold_spend_daily since task 79; diamonds had nothing,
    // so "are diamonds too generous" could not be measured at all.
    //
    // Diamonds are written in ~20 places - engines adding to PlayerRecords,
    // raw SQL across a guild roster, and the checkpoint assigning the live
    // payload's balance back. Rather than trust every one of those (and every
    // future one) to remember a ledger call, a database TRIGGER on
    // PlayerRecords."PremiumDiamonds" writes this table on every change. A
    // writer only LABELS the change (Engine.DiamondLedger.LabelAsync); an
    // unlabelled change still lands, under "unlabelled", so nothing is lost
    // and a missing label is visible in the data.
    //
    // One row per player, UTC day and reason. Gained and Spent are both
    // positive; a reason may have both (a refund reverses a purchase).
    [Table("diamond_daily")]
    public class DiamondDaily
    {
        public long PlayerId { get; set; }

        /// <summary>UTC day of the change (the database's clock, set by the trigger).</summary>
        public DateOnly Day { get; set; }

        /// <summary>Engine.DiamondLedger reason, or "unlabelled".</summary>
        [MaxLength(32)]
        public string Reason { get; set; } = "";

        public long Gained { get; set; }

        public long Spent { get; set; }
    }
}
