using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    public class GuildWarActiveMatch
    {
        [Key]
        public long MatchId { get; set; }
        public long AttackingGuildId { get; set; }
        public long DefendingGuildId { get; set; }
        public int InitialSeed { get; set; }
        public uint CurrentStateBitmask { get; set; }
    }
}
