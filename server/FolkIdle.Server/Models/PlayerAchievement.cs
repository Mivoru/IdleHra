using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    [Table("player_achievements")]
    public class PlayerAchievement
    {
        [Key]
        public long PlayerId { get; set; }

        public int ClaimedAchievementFlags { get; set; }
    }
}
