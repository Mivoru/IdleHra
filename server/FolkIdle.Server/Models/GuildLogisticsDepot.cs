namespace FolkIdle.Server.Models
{
    public class GuildLogisticsDepot
    {
        public long GuildId { get; set; }
        public int MaterialId { get; set; }
        public long CurrentStock { get; set; }
        public long TargetRequirement { get; set; }
    }
}
