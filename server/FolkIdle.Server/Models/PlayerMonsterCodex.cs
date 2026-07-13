using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    [Table("player_monster_codex")]
    public class PlayerMonsterCodex
    {
        public long PlayerId { get; set; }
        public int MonsterId { get; set; }
        public long KillCount { get; set; }
        public byte MaxRarityFound { get; set; }
    }
}
