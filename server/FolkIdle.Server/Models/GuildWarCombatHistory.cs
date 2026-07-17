using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    public class GuildWarCombatHistory
    {
        [Key]
        public long LogId { get; set; }
        public long MatchId { get; set; }
        public long ExecutionTick { get; set; }
        public int DamageDelta { get; set; }
    }
}
