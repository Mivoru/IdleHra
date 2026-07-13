using System.ComponentModel.DataAnnotations;

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
