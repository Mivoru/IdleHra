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

        // Modul 13: tiered milestone progress (0 = none, 1-4 = Tier I-IV), used by
        // the auto-awarded Treasury/Forging/Logistics achievements. Distinct from
        // IsClaimed, which remains the completion flag for the pre-existing
        // player-claimed "kill 10000 monsters" achievement (AchievementId 1).
        public int CompletedTier { get; set; }
    }
}
