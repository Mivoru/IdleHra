using System.ComponentModel.DataAnnotations.Schema;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    [Table("player_world_boss_attempts")]
    public class PlayerWorldBossAttempt
    {
        public long PlayerId { get; set; }
        public long BossInstanceId { get; set; }
        public int AttemptCount { get; set; }
        public long TotalInflictedDamage { get; set; }

        // Modul 06/15: unix-epoch-seconds when this player's current World Boss
        // battle session began. A session is capped at 300 seconds - once
        // exceeded (or the player's Auto-Eat food stock is depleted), further
        // attacks are rejected and the damage delta already registered stands.
        public long SessionStartEpoch { get; set; }
    }
}
