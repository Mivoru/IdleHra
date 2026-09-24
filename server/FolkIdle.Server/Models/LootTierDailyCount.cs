using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    // Modul: THE DROP RECORD, half one - COUNTS FOR EVERYTHING (task 26).
    //
    // "No Ancient+ in five days" could not be answered from the database: an
    // EquipmentInstances row has no timestamp and no origin, and the chest is
    // drops MINUS the sweep, MINUS auto-salvage, PLUS the forge - a biased
    // denominator. So every piece that is created is counted here, by UTC day,
    // source, region and FINAL tier, including the ones auto-salvage sold on
    // the way in. Rows, not per-drop events: a heavy player drops ~200 pieces
    // a day, which is the growth that took EquipmentInstances to 17,836 rows.
    // This table grows by (days x sources x regions x tiers actually hit).
    //
    // Composite key (PlayerId, Day, Source, RegionTier, QualityTier), set in
    // FolkIdleDbContext. Written ONLY through Engine.DropRecord, whose upsert
    // names this table - raw SQL, so keep the [Table] name and the SQL in step.
    [Table("loot_tier_daily_counts")]
    public class LootTierDailyCount
    {
        public long PlayerId { get; set; }

        /// <summary>UTC day the piece was created.</summary>
        public DateOnly Day { get; set; }

        /// <summary>Engine.DropSource.</summary>
        public short Source { get; set; }

        /// <summary>The region tier of the monster/content it came from; 0 when there is none (craft, forge).</summary>
        public short RegionTier { get; set; }

        /// <summary>The FINAL tier, after Golden Fleece and elevation.</summary>
        public short QualityTier { get; set; }

        /// <summary>Pieces that landed at this tier, salvaged ones included.</summary>
        public int Count { get; set; }

        /// <summary>Of <see cref="Count"/>, how many auto-salvage sold on the way in.</summary>
        public int SalvagedCount { get; set; }
    }
}
