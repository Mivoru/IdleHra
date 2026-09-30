using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// Task 87: the highest Boss Ascension step a player has CLEARED against one
    /// region's boss. Keyed (PlayerId, Region) and only ever raised, inside the
    /// transaction that grants that step's titles and frames - so this row, the
    /// rewards and their idempotency are one fact. The tick keeps a packed copy
    /// of it on the payload for start-step validation and reloads it from here
    /// at login: this table is the authority, the payload a cache.
    /// Snake_case table - see CURRENT_IMPLEMENTATION_STATE.md §3.
    /// </summary>
    [Table("boss_ascension_progress")]
    public class BossAscensionProgress
    {
        public long PlayerId { get; set; }

        public byte Region { get; set; }

        public byte HighestStep { get; set; }

        public DateTime UpdatedAtUtc { get; set; }
    }
}
