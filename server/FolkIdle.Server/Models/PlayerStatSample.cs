using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// One ten-minute sample of an online account's lifetime counters (task 56).
    ///
    /// CUMULATIVE COUNTERS, NOT DELTAS. A rate over any window is the difference
    /// between two rows divided by the time between them, so a missed sample
    /// (the player was offline, the server restarted) costs resolution and
    /// never correctness - and gains made offline land in the next sample as
    /// what they are, earnings in that window.
    ///
    /// The four activity columns count the account's PLAYED characters (slots
    /// 0-2) by what each was doing at the sample, which is what "your style"
    /// is built from. Gold is the BALANCE; see StatSampler for how earned and
    /// spent are told apart.
    ///
    /// Written by StatSampler only; pruned past <see cref="FolkIdle.Server.Engine.StatSampler.Retention"/>.
    /// Composite key (PlayerId, AtUtc) in FolkIdleDbContext.
    /// </summary>
    [Table("player_stat_samples")]
    public class PlayerStatSample
    {
        public long PlayerId { get; set; }
        public DateTime AtUtc { get; set; }

        public long Kills { get; set; }
        public long Xp { get; set; }
        public long Gold { get; set; }
        public long Harvests { get; set; }
        public long Crafted { get; set; }

        public short Fighting { get; set; }
        public short Gathering { get; set; }
        public short Crafting { get; set; }
        public short Idle { get; set; }
    }
}
