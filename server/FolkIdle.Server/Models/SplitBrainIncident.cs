using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// One split-brain refusal of a checkpoint: the database epoch was ahead of
    /// the session's, so the flush rolled back and the player was mailed
    /// compensation.
    ///
    /// Modul: THIS IS WHAT MAKES THE COMPENSATION PAY ONCE. The mail used to be
    /// `epochDelta * 500` gold on every refused flush, with no ceiling and no
    /// memory - minting currency on an error path, paid again for every race a
    /// player could provoke against the Redis session lock. The compensation
    /// task now INSERTs (PlayerId, DbEpoch) with ON CONFLICT DO NOTHING and
    /// mails only when that inserted a row, so one incident (one database
    /// epoch a stale session collided with) pays exactly once. Task 42.
    ///
    /// Snake_case table (see CURRENT_IMPLEMENTATION_STATE.md §3): raw SQL
    /// names it unquoted, and its COLUMNS are PascalCase and quoted.
    /// Composite key (PlayerId, DbEpoch) is configured in FolkIdleDbContext.
    /// </summary>
    [Table("split_brain_incidents")]
    public class SplitBrainIncident
    {
        public long PlayerId { get; set; }

        public long DbEpoch { get; set; }

        public DateTimeOffset At { get; set; }
    }
}
