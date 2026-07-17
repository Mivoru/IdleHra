using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    [Table("monster_codex_entries")]
    public class MonsterCodexEntry
    {
        [Key, Column(Order = 0)]
        public long PlayerId { get; set; }

        [Key, Column(Order = 1)]
        public int MonsterId { get; set; }

        public int KillCount { get; set; }
        public int FirstDrawnRarity { get; set; }

        // Passive codex mastery level for this monster, derived from KillCount
        // (Level = KillCount / 10). Feeds the Codex yield/damage multiplier sums.
        public int Level { get; set; }
    }
}
