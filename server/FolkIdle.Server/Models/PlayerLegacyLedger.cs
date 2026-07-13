namespace FolkIdle.Server.Models
{
    public class PlayerLegacyLedger
    {
        public long PlayerId { get; set; }
        public int EraId { get; set; }
        public int LegacyShardBalance { get; set; }
        public int CitizenMultiSlotsUnlocked { get; set; }
        public SeasonalEraRecord? Era { get; set; }
    }
}
