using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// Task 84: where one player stands on one Great Work (monument). Keyed
    /// (PlayerId, Region), one monument per region. <see cref="Stage"/> is how
    /// many of its five stages are BUILT; <see cref="Progress"/> is what has been
    /// deposited into the NEXT stage. Both move only inside the transaction that
    /// consumes the materials, so the materials, the progress and the stage are
    /// one fact.
    ///
    /// Modul: THIS TABLE IS DELIBERATELY NOT ON REBIRTH'S RESET LIST. A Great
    /// Work is the permanent half of the material sink (the bonus it pays
    /// survives a rebirth), and rebirth deletes only the tables it names. The
    /// tick keeps a packed copy of the stages on the payload
    /// (GreatWorksStagesPacked) and reloads it from here at login: this table is
    /// the authority, the payload a cache.
    /// Snake_case table - see CURRENT_IMPLEMENTATION_STATE.md section 3.
    /// </summary>
    [Table("great_works_progress")]
    public class GreatWorkProgress
    {
        public long PlayerId { get; set; }

        public byte Region { get; set; }

        public byte Stage { get; set; }

        public long Progress { get; set; }

        public DateTime UpdatedAtUtc { get; set; }
    }
}
