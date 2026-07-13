namespace FolkIdle.Server.Models
{
    public class GuildLogisticsDepot
    {
        public long GuildId { get; set; }
        public int MaterialId { get; set; }
        public long CurrentStock { get; set; }
        public long TargetRequirement { get; set; }

        // Depot level for this material: incremented whenever CurrentStock reaches
        // TargetRequirement, which then scales the next TargetRequirement by 1.25.
        public int Level { get; set; }
    }
}
