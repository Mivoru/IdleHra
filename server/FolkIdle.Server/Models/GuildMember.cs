using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    // Raid participation reward ledger. Guild membership itself is tracked via
    // PlayerRecord.GuildId; this table only accumulates raid contribution points.
    public class GuildMember
    {
        [Key]
        public long PlayerId { get; set; }

        public long GuildId { get; set; }
        public long ContributionPoints { get; set; }
    }
}
