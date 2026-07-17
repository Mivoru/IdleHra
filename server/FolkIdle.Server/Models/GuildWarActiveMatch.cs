using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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
