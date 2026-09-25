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

        // Modul: UNREAD since 2026-09-25. It stamped the start of a 300-second
        // battle session, which the owner dropped: with one strike a day there
        // is nothing to fence inside a session. The column stays because
        // dropping it would be a non-additive migration for nothing.
        public long SessionStartEpoch { get; set; }

        // Modul: ONE STRIKE A DAY (owner, 2026-09-25). AttemptCount now counts
        // the strikes made on THIS UTC day (WorldBossCalendar.DayKey), and the
        // engine resets it to 0 inside its transaction when the day has moved
        // on. TotalInflictedDamage still counts the whole encounter.
        public long AttemptDateKey { get; set; }
    }
}
