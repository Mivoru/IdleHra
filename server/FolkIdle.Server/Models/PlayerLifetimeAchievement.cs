using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    [Table("player_lifetime_achievements")]
    public class PlayerLifetimeAchievement
    {
        public long PlayerId { get; set; }
        public int AchievementId { get; set; }
        public long CurrentProgress { get; set; }
        public bool IsClaimed { get; set; }
    }
}
