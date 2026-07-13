namespace FolkIdle.Server.Models
{
    public class PlayerChroniclePass
    {
        public long PlayerId { get; set; }
        public int PassLevel { get; set; }
        public int AccumulatedXp { get; set; }
        public ulong ClaimedMilestonesBitmask { get; set; }
    }
}
