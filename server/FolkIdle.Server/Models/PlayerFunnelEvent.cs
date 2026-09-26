using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// The first time one player reached one step of the new-player funnel.
    /// One row per (PlayerId, Step), ever; written only by FunnelRecorder, as
    /// <c>INSERT ... ON CONFLICT DO NOTHING</c>, so recording a step twice is
    /// free and the first occurrence is the one that stays.
    ///
    /// Modul: THIS IS ALSO THE ONLY RECORD OF WHEN AN ACCOUNT WAS CREATED.
    /// PlayerRecord has no creation timestamp at all, so step 1 (registered)
    /// is what returned_d1 / returned_d7 are measured against. Accounts that
    /// existed before this table have no step 1 and are outside the cohort -
    /// nothing can reconstruct their registration time (plan item 4, task 39).
    ///
    /// Deliberately NOT AccountAnalyticsLogs: that table is hash-typed, not
    /// unique per step, and shared with anti-cheat events - the wrong shape for
    /// a first-occurrence funnel. No foreign key to PlayerRecords either: a
    /// batch insert must not fail as a whole because one account was purged
    /// between the enqueue and the drain.
    ///
    /// Snake_case table (see CURRENT_IMPLEMENTATION_STATE.md §3): raw SQL
    /// names it unquoted, and its COLUMNS are PascalCase and quoted. Composite
    /// key (PlayerId, Step) is configured in FolkIdleDbContext. See
    /// docs/ops/funnel.sql for the read side.
    /// </summary>
    [Table("player_funnel_events")]
    public class PlayerFunnelEvent
    {
        public long PlayerId { get; set; }

        /// <summary>A <see cref="FolkIdle.Server.Engine.FunnelStep"/> value.</summary>
        public short Step { get; set; }

        /// <summary>UTC, when the producer saw the step - not when the worker wrote it.</summary>
        public DateTime At { get; set; }
    }
}
