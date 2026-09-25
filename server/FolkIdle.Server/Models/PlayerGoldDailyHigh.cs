using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// The most gold a player held on one UTC day, as the durable checkpoint saw
    /// it. At most eight rows per player (today and the seven days before);
    /// older rows are pruned by the same statement that writes a new one.
    ///
    /// Modul: THE DEEP'S STAKE READS THIS, and it exists so the stake cannot be
    /// dodged. The stake is a share of what a player holds; without a memory,
    /// mailing the hoard to an alt five minutes before descending would price
    /// the descent at the region-fee floor. The owner decided on 2026-09-24
    /// that the stake is priced on the maximum held over the last seven days
    /// (task 37 spec §3.4).
    ///
    /// Daily rows rather than one column: a single "max ever" never decays, so
    /// a player who really did spend down would pay on old wealth for ever, and
    /// a single "max since N" cannot roll. Eight small rows is the cheapest
    /// honest rolling window.
    ///
    /// Snake_case table (see CURRENT_IMPLEMENTATION_STATE.md §3): raw SQL
    /// names it unquoted, and its COLUMNS are PascalCase and quoted.
    /// Composite key (PlayerId, DayUtc) is configured in FolkIdleDbContext.
    /// </summary>
    [Table("player_gold_daily_high")]
    public class PlayerGoldDailyHigh
    {
        public long PlayerId { get; set; }

        public DateOnly DayUtc { get; set; }

        public long MaxGold { get; set; }
    }
}
