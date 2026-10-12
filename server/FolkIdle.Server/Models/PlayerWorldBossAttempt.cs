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

        // Modul: CHERNOBOG'S WEEK (spec 2026-10-13). TotalInflictedDamage is the
        // player's whole week - milestones and the board read it, registered or
        // not. The rest is what the reveal, the kill reward and the sandbagging
        // detector need, and none of it can be rebuilt from the running total.

        /// <summary>Struck during registration (Monday/Tuesday) at or above the ranked level.</summary>
        public bool IsRegistered { get; set; }

        /// <summary>Damage dealt during registration, and on how many strikes (one a day).</summary>
        public long RegistrationDamage { get; set; }
        public int RegistrationStrikes { get; set; }

        /// <summary>
        /// The shield-wheel skill score s of every PLAYED strike, summed, split at
        /// the reveal. Auto-strikes are left out: they are not a choice to play badly.
        /// </summary>
        public double RegistrationSkillSum { get; set; }
        public int RegistrationPlayed { get; set; }
        public double LaterSkillSum { get; set; }
        public int LaterPlayed { get; set; }

        /// <summary>Bit i set: weekly milestone i has been paid this encounter.</summary>
        public int MilestonesPaid { get; set; }
    }
}
