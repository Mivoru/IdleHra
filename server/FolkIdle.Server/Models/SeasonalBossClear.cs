using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// A seasonal boss tier's first clear, once per player per event per tier.
    /// The composite key IS the once: a second save of the same clear inserts
    /// nothing and pays nothing.
    /// </summary>
    [Table("seasonal_boss_clears")]
    public class SeasonalBossClear
    {
        public long PlayerId { get; set; }
        public int EventId { get; set; }
        public int Tier { get; set; }
        public DateTime ClearedAtUtc { get; set; }
    }
}
