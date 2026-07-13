namespace FolkIdle.Server.Models
{
    public class GuildContributionLedger
    {
        public long PlayerId { get; set; }
        public long GuildId { get; set; }
        public int MaterialId { get; set; }
        public long LifetimeContributed { get; set; }
    }
}
