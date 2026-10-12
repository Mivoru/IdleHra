using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// What the world boss remembers about a player across weeks.
    /// </summary>
    /// <remarks>
    /// Modul: THE SANDBAGGING DETECTOR NEEDS A PAST (spec 2026-10-13, section 4).
    /// A player who throws every spear away on Monday and lands seams from
    /// Wednesday is only suspicious if they could have landed them on Monday
    /// too - a new player learning the parry looks exactly the same. So the
    /// detector only flags an account that has ALREADY shown the skill in an
    /// earlier week, and that fact has to outlive the weekly attempt rows,
    /// which are deleted every Monday.
    /// </remarks>
    [Table("world_boss_profiles")]
    public class WorldBossProfile
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public long PlayerId { get; set; }

        /// <summary>When the player first finished a week with a high skill score; null if never.</summary>
        public DateTime? ProvenSkillAtUtc { get; set; }
    }
}
