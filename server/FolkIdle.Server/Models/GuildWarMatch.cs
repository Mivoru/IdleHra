using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    public class GuildWarMatch
    {
        [Key]
        public long MatchId { get; set; }
        public long GuildA_Id { get; set; }
        public long GuildB_Id { get; set; }
        public int MatchEpoch { get; set; }
        public int CombatVanguardWP_A { get; set; }
        public int ProductionLogisticsWP_A { get; set; }
        public int GatheringSupplyChainWP_A { get; set; }
        public int CombatVanguardWP_B { get; set; }
        public int ProductionLogisticsWP_B { get; set; }
        public int GatheringSupplyChainWP_B { get; set; }
        public bool IsActive { get; set; }
    }
}
