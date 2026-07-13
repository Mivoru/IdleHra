using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    [Table("player_world_boss_attempts")]
    public class PlayerWorldBossAttempt
    {
        public long PlayerId { get; set; }
        public long BossInstanceId { get; set; }
        public int AttemptCount { get; set; }
        public long TotalInflictedDamage { get; set; }
    }
}
