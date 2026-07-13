using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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
    }
}
