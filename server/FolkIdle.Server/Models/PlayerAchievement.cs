using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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
