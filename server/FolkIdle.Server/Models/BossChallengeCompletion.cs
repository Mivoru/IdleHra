using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// Task 55: a boss challenge a player has met, once and for good. Keyed
    /// (PlayerId, Region, Challenge) and inserted ON CONFLICT DO NOTHING, so a
    /// challenge met again on a later kill pays nothing a second time.
    /// Snake_case table - see CURRENT_IMPLEMENTATION_STATE.md §3.
    /// </summary>
    [Table("boss_challenge_completions")]
    public class BossChallengeCompletion
    {
        public long PlayerId { get; set; }

        public byte Region { get; set; }

        public byte Challenge { get; set; }

        public DateTime CompletedAtUtc { get; set; }
    }
}
