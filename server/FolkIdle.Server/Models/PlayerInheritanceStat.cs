using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// One inheritance stat a player has bought levels in.
    ///
    /// Modul: WHAT A SEASON LEAVES BEHIND, and what diamonds are finally for.
    ///
    /// Two problems met here. Diamonds had nine producers and exactly one sink
    /// worth the name - a currency that accumulates with nothing to spend it on
    /// stops reading as a reward. And a three-month wipe that takes everything
    /// is a hard thing to come back to: the village and race mastery already
    /// survive it, but nothing a player CHOSE survived.
    ///
    /// Inheritance stats are both answers at once. Levels are bought with
    /// diamonds, they are permanent multipliers, and the seasonal reset does
    /// not touch this table - so the season that just ended is the reason the
    /// next one starts faster, and the diamonds spent are the part of it the
    /// player picked.
    ///
    /// One row per (player, stat) rather than a column per stat: adding a stat
    /// is then a registry entry rather than a migration, and a player who has
    /// bought nothing has no rows at all.
    /// </summary>
    [Table("player_inheritance_stats")]
    public class PlayerInheritanceStat
    {
        [Key]
        public long Id { get; set; }

        public long PlayerId { get; set; }

        /// <summary>An <see cref="Engine.InheritanceRegistry"/> stat id.</summary>
        public int StatId { get; set; }

        /// <summary>
        /// How many levels have been bought. Never decreases - there is no
        /// refund path, deliberately: a permanent bonus a player could sell
        /// back would make the diamond price a loan rather than a decision.
        /// </summary>
        public int Level { get; set; }
    }
}
