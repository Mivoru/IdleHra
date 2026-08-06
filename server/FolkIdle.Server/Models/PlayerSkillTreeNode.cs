using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// One skill-tree branch a player has put levels into.
    ///
    /// One row per (player, branch) rather than a column per branch, for the
    /// same reason PlayerInheritanceStat is shaped that way: adding a sixth
    /// branch is then a registry entry rather than a migration, and a player
    /// who has spent nothing has no rows at all.
    ///
    /// Levels RESET WITH THE SEASON, unlike inheritance. Skill points come from
    /// account levels and the season takes those back, so a tree that survived
    /// would be paid for twice - and the season is the ladder. What is meant to
    /// carry across a rollover already has a home in player_inheritance_stats.
    /// </summary>
    [Table("player_skill_tree")]
    public class PlayerSkillTreeNode
    {
        [Key]
        public long Id { get; set; }

        public long PlayerId { get; set; }

        /// <summary>A <see cref="Engine.SkillTreeRegistry"/> branch id.</summary>
        public int BranchId { get; set; }

        /// <summary>
        /// Levels bought, capped at SkillTreeRegistry.MaxLevel. There is no
        /// refund path: a respec would need a rule for what happens to points
        /// already spent on a branch whose price has risen, and the season
        /// already gives everyone a clean slate every three months.
        /// </summary>
        public int Level { get; set; }
    }
}
